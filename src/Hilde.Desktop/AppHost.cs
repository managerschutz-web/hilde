using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Hilde.Audio;
using Hilde.Core;
using Hilde.Core.Engines;
using Hilde.Insertion;
using Hilde.Language;
using Hilde.Setup;
using Hilde.Speech;
using Hilde.Storage;
using Serilog;

namespace Hilde.Desktop;

/// <summary>Setzt alle Bausteine zusammen (Composition Root) und verbindet sie mit Pill, Tray und Oberfläche.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly HildePaths _paths;
    private readonly Dispatcher _ui;
    private readonly ILogger _log = Log.ForContext<AppHost>();

    private JsonSettingsStore _settings = null!;
    private SqliteTranscriptRepository _repo = null!;
    private DictionaryService _dictionary = null!;
    private ModelManager _models = null!;
    private EngineProcessClient _speechClient = null!;
    private EngineProcessClient _llmClient = null!;
    private RemoteSpeechEngine _speech = null!;
    private TextProcessingPipeline _text = null!;
    private SileroVoiceActivityDetector _vad = null!;
    private WasapiCaptureService _audio = null!;
    private Win32TextInsertionService _insertion = null!;
    private DictationController _controller = null!;
    private LowLevelKeyboardHook? _hook;
    private readonly UiBridge _bridge = new();
    private PillWindow? _pill;
    private MainWindow? _main;
    private TrayIcon? _tray;
    private CancellationTokenSource? _downloadCts;
    private string? _testDir;
    private int _warmupToken;

    public AppHost(HildePaths paths, Dispatcher ui)
    {
        _paths = paths;
        _ui = ui;
    }

    private PerformanceTier Tier => _settings.Current.PerformanceTier;

    public async Task StartAsync(bool startInTray, Func<Task>? beforeShow = null)
    {
        _settings = new JsonSettingsStore(_paths, Log.Logger);
        _paths.ModelsOverride = _settings.Current.ModelsDirectory;
        // Autostart-Eintrag an den aktuellen Programmpfad anpassen (z. B. nach Update) bzw. Reste entfernen.
        try { Autostart.Apply(_settings.Current.Autostart); } catch (Exception ex) { _log.Debug(ex, "Autostart"); }
        _repo = new SqliteTranscriptRepository(_paths.DatabaseFile);
        await _repo.InitializeAsync();
        _dictionary = new DictionaryService(_repo);
        await _dictionary.ReloadAsync();

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "models", "manifest.json");
        _models = new ModelManager(_paths, ModelManager.LoadEffective(manifestPath, _paths), Log.Logger);
        _models.Progress += (_, p) => _bridge.Emit("modelProgress", p);

        var engineExe = Path.Combine(AppContext.BaseDirectory, "Hilde.Engine.exe");
        int threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
        _speechClient = new EngineProcessClient(EngineKind.Speech, engineExe, () =>
            _models.GetInstalledPath(ModelKind.Whisper, Tier) is { } p ? new EngineLaunchOptions(p, _settings.Current.UseGpu, threads) : null,
            Log.Logger);
        _llmClient = new EngineProcessClient(EngineKind.Language, engineExe, () =>
            _models.GetInstalledPath(ModelKind.Llm, Tier) is { } p ? new EngineLaunchOptions(p, _settings.Current.UseGpu, threads) : null,
            Log.Logger);
        ApplyIdleUnload(_settings.Current);

        _speech = new RemoteSpeechEngine(_speechClient);
        _text = new TextProcessingPipeline(_llmClient,
            () => PromptBuilder.ParseFormat(_models.GetInstalledEntry(ModelKind.Llm, Tier)?.PromptFormat), Log.Logger);
        _vad = new SileroVoiceActivityDetector(() => _models.GetInstalledPath(ModelKind.Vad, Tier), Log.Logger);
        _audio = new WasapiCaptureService(Log.Logger);
        _insertion = new Win32TextInsertionService(_settings, Log.Logger);

        _controller = new DictationController(_audio, _vad, _speech, _text, _dictionary, _insertion, _repo,
            _settings, _paths, Log.Logger);
        _controller.StatusChanged += OnStatus;
        _controller.LevelChanged += (_, l) =>
        {
            _ui.InvokeAsync(() => _pill?.SetLevel(l), DispatcherPriority.Render);
            _bridge.Emit("level", l);
        };
        _controller.CopyFallbackRequested += (_, e) => _ui.InvokeAsync(() =>
        {
            var w = new CopyWindow(e.Text, e.Message, e.ErrorId);
            w.Show();
            w.Activate();
        });
        _controller.Completed += (_, r) => _bridge.Emit("transcript", new { r.Id });
        _controller.PreviewChanged += (f, pv) => _ui.InvokeAsync(() => _pill?.SetPreview(f, pv));

        RegisterBridge();
        CreateUi();
        _settings.Changed += (_, s) => _ui.InvokeAsync(() => ApplySettings(s));

        try
        {
            _hook = new LowLevelKeyboardHook(_controller.Gestures, a => _controller.Post(a),
                () => _settings.Current.Hotkey.VirtualKey, () => _settings.Current.Hotkey.EscapeCancels, Log.Logger);
            _hook.Start();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hotkey nicht verfügbar");
            _tray?.Notify("HILDE", "Die Diktiertaste konnte nicht aktiviert werden. Diktieren ist über die Pill möglich.");
        }

        _paths.CleanTemp(TimeSpan.FromDays(1));
        _ = Task.Run(CleanupOldAudioAsync);
        _ = Task.Run(async () =>
        {
            try
            {
                var n = await _controller.RecoverAsync();
                if (n > 0) _ui.InvokeAsync(() => _tray?.Notify("HILDE", $"{n} unterbrochene Aufnahme(n) wurden wiederhergestellt und stehen im Archiv."));
            }
            catch (Exception ex) { _log.Warning(ex, "Wiederherstellung"); }
        });

        if (beforeShow != null) await beforeShow();
        if (!_settings.Current.SetupCompleted || !startInTray) ShowMainWindow();
    }

    private void CreateUi()
    {
        var s = _settings.Current;
        _pill = new PillWindow { Topmost = s.Pill.Topmost, Opacity = s.Pill.Opacity };
        _pill.ApplyColors(s.Pill.BackgroundColor, s.Pill.AccentColor);
        _pill.MicClicked += () => _controller.ToggleHandsFree();
        _pill.ExpandClicked += ShowMainWindow;
        _pill.QuitRequested += Quit;
        _pill.PauseClicked += () => _controller.TogglePause();
        _pill.HideRequested += () =>
        {
            var c = _settings.Current.Clone();
            c.Pill.Visible = false;
            _settings.Save(c);
            _tray?.Notify("HILDE", "Die Pill ist ausgeblendet. Einblenden über das HILDE-Symbol im Infobereich.");
        };
        _pill.PositionChanged += (l, t) =>
        {
            var c = _settings.Current.Clone();
            c.Pill.Left = l; c.Pill.Top = t;
            _settings.Save(c);
        };
        if (s.Pill.Left is { } left && s.Pill.Top is { } top)
        {
            _pill.Left = left; _pill.Top = top;
        }
        if (s.Pill.Visible)
        {
            _pill.Show();
            if (s.Pill.Left == null) _pill.PlaceDefault(); else _pill.EnsureOnScreen();
        }

        _tray = new TrayIcon();
        _tray.SetPillVisible(s.Pill.Visible);
        _tray.OpenRequested += () => _ui.InvokeAsync(ShowMainWindow);
        _tray.ToggleDictationRequested += () => _controller.ToggleHandsFree();
        _tray.PauseRequested += () => _controller.TogglePause();
        _tray.TogglePillRequested += () =>
        {
            var c = _settings.Current.Clone();
            c.Pill.Visible = !c.Pill.Visible;
            _settings.Save(c);
        };
        _tray.ExitRequested += Quit;
    }

    private void ApplySettings(HildeSettings s)
    {
        if (_pill != null)
        {
            _pill.Topmost = s.Pill.Topmost;
            _pill.Opacity = s.Pill.Opacity;
            _pill.ApplyColors(s.Pill.BackgroundColor, s.Pill.AccentColor);
            if (s.Pill.Visible && !_pill.IsVisible) { _pill.Show(); _pill.EnsureOnScreen(); }
            if (!s.Pill.Visible && _pill.IsVisible) _pill.Hide();
        }
        _tray?.SetPillVisible(s.Pill.Visible);
        ApplyIdleUnload(s);
        _bridge.Emit("settings", s);
    }

    private void ApplyIdleUnload(HildeSettings s)
    {
        var idle = TimeSpan.FromMinutes(Math.Clamp(s.ModelIdleUnloadMinutes, 1, 240));
        _speechClient.IdleUnload = idle;
        _llmClient.IdleUnload = idle;
    }

    private void OnStatus(object? sender, StatusEventArgs e)
    {
        _ui.InvokeAsync(() =>
        {
            _pill?.SetStatus(e);
            _tray?.SetStatus(e.Message ?? e.State.ToString(), e.State is HildeState.Recording or HildeState.Paused);
        });
        _bridge.Emit("status", e);
        if (e.State == HildeState.Recording) ScheduleWarmUp();
    }

    /// <summary>Modelle laden, während noch gesprochen wird – erst nach 400 ms, damit Strg-Kürzel nichts auslösen.</summary>
    private void ScheduleWarmUp()
    {
        int token = Interlocked.Increment(ref _warmupToken);
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            if (token != _warmupToken || _controller.State != HildeState.Recording) return;
            if (!_speechClient.IsRunning) _ = _speechClient.WarmUpAsync(CancellationToken.None);
            if (_settings.Current.TextMode != TextMode.Original && _llmClient.HasModel && !_llmClient.IsRunning)
                _ = _llmClient.WarmUpAsync(CancellationToken.None);
        });
    }

    /// <summary>HILDE vollständig beenden (Engines werden mit beendet).</summary>
    public void Quit() => _ui.InvokeAsync(() => Application.Current.Shutdown(0));

    public void ShowMainWindow()
    {
        if (_main == null)
        {
            _main = new MainWindow(_paths, _bridge);
        }
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    // ------------------------------------------------------------------ Brücke zur Oberfläche

    private void RegisterBridge()
    {
        var b = _bridge;
        b.Register("app.info", _ => new
        {
            version = typeof(AppHost).Assembly.GetName().Version?.ToString(3),
            setupCompleted = _settings.Current.SetupCompleted,
            dataPath = _paths.Root,
            hotkey = _settings.Current.Hotkey.DisplayName,
            state = _controller.State,
            llm = _text.IsAvailable,
        });
        b.Register("app.quit", _ => { Quit(); return null; });
        b.Register("app.openLogs", _ => { OpenFolder(_paths.Logs); return null; });
        b.Register("app.openData", _ => { OpenFolder(_paths.Root); return null; });
        b.Register("app.licenses", _ => ReadLicenses());

        // Einstellungen
        b.Register("settings.get", _ => _settings.Current);
        b.Register("settings.save", p =>
        {
            var s = p.Deserialize<HildeSettings>(UiBridge.Json) ?? throw new ArgumentException("Einstellungen fehlen");
            s.Hotkey.MinHoldMs = Math.Clamp(s.Hotkey.MinHoldMs, 100, 800);
            s.Hotkey.DoubleTapMs = Math.Clamp(s.Hotkey.DoubleTapMs, 200, 800);
            s.ModelIdleUnloadMinutes = Math.Clamp(s.ModelIdleUnloadMinutes, 1, 240);
            try { Autostart.Apply(s.Autostart); }
            catch (Exception ex) { _log.Warning(ex, "Autostart"); s.Autostart = Autostart.IsEnabled(); }
            _settings.Save(s);
            return _settings.Current;
        });
        b.RegisterAsync("hotkey.capture", async _ =>
        {
            if (_hook == null) return new { ok = false, message = "Hotkey nicht verfügbar." };
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(int vk) => tcs.TrySetResult(vk);
            _hook.KeyCaptured += Handler;
            _hook.CaptureMode = true;
            try
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(10000));
                if (done != tcs.Task) return new { ok = false, message = "Keine Taste gedrückt." };
                var vk = tcs.Task.Result;
                if (!HotkeyNames.IsAllowed(vk))
                    return new { ok = false, message = $"„{HotkeyNames.Name(vk)}“ wird zum Schreiben gebraucht. Bitte z. B. Strg rechts, Pause oder F13–F24 wählen." };
                return new { ok = true, vk, name = HotkeyNames.Name(vk) };
            }
            finally
            {
                _hook.CaptureMode = false;
                _hook.KeyCaptured -= Handler;
            }
        });

        // Mikrofon
        b.Register("devices.list", _ => _audio.GetInputDevices());
        b.Register("mic.monitorStart", p => { _audio.StartMonitor(p.Str("deviceId")); return null; });
        b.Register("mic.monitorStop", _ => { _audio.StopMonitor(); return null; });

        // Archiv
        b.RegisterAsync("transcripts.list", async p =>
        {
            long requested = p.Long("limit");
            int limit = requested > 0 ? (int)Math.Min(requested, 500) : 200;
            var list = await _repo.ListAsync(new TranscriptQuery(p.Str("search"), TranscriptKind.Dictation, limit, (int)p.Long("offset")));
            return list.Select(r => new
            {
                r.Id, r.Title, r.CreatedUtc, r.Status, r.Mode, r.DurationMs, r.TargetApp, r.ErrorId, r.Language,
                preview = Preview(string.IsNullOrEmpty(r.FinalText) ? r.RawText : r.FinalText),
                hasAudio = r.AudioPath != null && File.Exists(r.AudioPath)
            }).ToList();
        });
        b.RegisterAsync("transcripts.get", async p =>
        {
            var r = await _repo.GetAsync(p.Str("id") ?? "") ?? throw new ArgumentException("Nicht gefunden");
            bool hasAudio = r.AudioPath != null && File.Exists(r.AudioPath);
            return new
            {
                record = r, hasAudio,
                audioUrl = hasAudio ? $"https://{MainWindow.AudioHost}/{Uri.EscapeDataString(r.Id)}/{Path.GetFileName(r.AudioPath)}" : null,
                segments = r.SegmentsJson == null ? (JsonElement?)null : JsonDocument.Parse(r.SegmentsJson).RootElement.Clone(),
                keepAudio = _settings.Current.Archive.KeepAudio,
                error = r.ErrorId == null ? null : HildeErrors.Get(r.ErrorId).Message
            };
        });
        b.RegisterAsync("transcripts.update", async p =>
        {
            var r = await _repo.GetAsync(p.Str("id") ?? "") ?? throw new ArgumentException("Nicht gefunden");
            if (p.Str("title") is { } title) r.Title = title.Trim();
            if (p.Str("finalText") is { } text) r.FinalText = text; // Roh- und Bereinigt-Text bleiben unverändert
            await _repo.SaveAsync(r);
            return null;
        });
        b.RegisterAsync("transcripts.suggestRules", async p =>
        {
            var before = p.Str("before") ?? "";
            var after = p.Str("after") ?? "";
            if (before == after) return Array.Empty<RuleSuggestion>();
            return CorrectionLearner.Suggest(before, after, await _repo.GetReplacementsAsync());
        });
        b.RegisterAsync("transcripts.delete", async p =>
        {
            var id = p.Str("id") ?? "";
            await _repo.DeleteAsync(id);
            TryDeleteDir(_paths.RecordingDir(id));
            TryDeleteDir(Path.Combine(_paths.Transcripts, id));
            return null;
        });
        b.RegisterAsync("transcripts.rewrite", async p =>
        {
            var r = await _repo.GetAsync(p.Str("id") ?? "") ?? throw new ArgumentException("Nicht gefunden");
            var source = p.Str("source") switch { "raw" => r.RawText, "clean" => r.CleanText, _ => r.FinalText };
            if (p.Str("text") is { Length: > 0 } custom) source = custom; // aktuell im Editor angezeigter Text
            var kind = p.Str("kind") == "shorten" ? RewriteKind.Shorten : RewriteKind.Clarify;
            var res = await _text.RewriteAsync(source, kind, _dictionary.ProtectedTerms, CancellationToken.None);
            return new { text = _dictionary.Apply(res.Text), notes = res.Notes, seconds = res.Seconds, source };
        });
        b.RegisterAsync("transcripts.reprocess", async p => await _controller.ReprocessAsync(p.Str("id") ?? ""));
        b.RegisterAsync("transcripts.copy", async p =>
        {
            var r = await _repo.GetAsync(p.Str("id") ?? "") ?? throw new ArgumentException("Nicht gefunden");
            var text = p.Str("variant") switch { "raw" => r.RawText, "clean" => r.CleanText, _ => r.FinalText };
            _ui.Invoke(() => Clipboard.SetText(text));
            return null;
        });
        b.RegisterAsync("transcripts.export", async p =>
        {
            var r = await _repo.GetAsync(p.Str("id") ?? "") ?? throw new ArgumentException("Nicht gefunden");
            var format = p.Str("format") ?? "txt";
            return _ui.Invoke(() =>
            {
                Directory.CreateDirectory(HildePaths.ExportsFolder);
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    InitialDirectory = HildePaths.ExportsFolder,
                    FileName = SafeFileName(r.Title) + "." + format,
                    Filter = format switch
                    {
                        "md" => "Markdown (*.md)|*.md",
                        "json" => "JSON (*.json)|*.json",
                        _ => "Text (*.txt)|*.txt"
                    }
                };
                if (dlg.ShowDialog(_main) != true) return (object?)new { saved = false };
                File.WriteAllText(dlg.FileName, Exporter.Render(r, format), new System.Text.UTF8Encoding(true));
                return new { saved = true, path = dlg.FileName };
            });
        });

        // Wörterbuch
        b.RegisterAsync("dictionary.get", async _ => new
        {
            rules = await _repo.GetReplacementsAsync(),
            terms = await _repo.GetTermsAsync(),
            snippets = await _repo.GetSnippetsAsync()
        });
        b.RegisterAsync("dictionary.saveSnippet", async p =>
        {
            var name = (p.Str("name") ?? "").Trim();
            var text = p.Str("text") ?? "";
            if (name.Length == 0 || text.Trim().Length == 0) throw new ArgumentException("Name und Text angeben");
            var saved = await _repo.SaveSnippetAsync(new Snippet(p.Long("id"), name, text.Replace("\r\n", "\n")));
            await _dictionary.ReloadAsync();
            return saved;
        });
        b.RegisterAsync("dictionary.deleteSnippet", async p =>
        {
            await _repo.DeleteSnippetAsync(p.Long("id"));
            await _dictionary.ReloadAsync();
            return null;
        });
        b.RegisterAsync("dictionary.saveRule", async p =>
        {
            var rule = p.To<ReplacementRule>();
            if (string.IsNullOrWhiteSpace(rule.Pattern)) throw new ArgumentException("Suchtext fehlt");
            var saved = await _repo.SaveReplacementAsync(rule);
            await _dictionary.ReloadAsync();
            return saved;
        });
        b.RegisterAsync("dictionary.deleteRule", async p =>
        {
            await _repo.DeleteReplacementAsync(p.Long("id"));
            await _dictionary.ReloadAsync();
            return null;
        });
        b.Register("dictionary.preview", p =>
        {
            var rule = p.GetProperty("rule").To<ReplacementRule>();
            var (result, matches) = DictionaryService.Preview(rule, p.Str("sample") ?? "");
            return new { result, matches };
        });
        b.RegisterAsync("dictionary.saveTerm", async p =>
        {
            var term = (p.Str("term") ?? "").Trim();
            if (term.Length == 0) throw new ArgumentException("Begriff fehlt");
            var saved = await _repo.SaveTermAsync(new VocabularyTerm { Id = p.Long("id"), Term = term });
            await _dictionary.ReloadAsync();
            return saved;
        });
        b.RegisterAsync("dictionary.deleteTerm", async p =>
        {
            await _repo.DeleteTermAsync(p.Long("id"));
            await _dictionary.ReloadAsync();
            return null;
        });

        // Einrichtung & Modelle
        b.Register("setup.system", _ => SystemCheck.Run(_paths));
        b.Register("models.status", p =>
        {
            var tier = Enum.TryParse<PerformanceTier>(p.Str("tier"), true, out var t) ? t : Tier;
            return new
            {
                models = _models.GetStatus(tier),
                requiredBytes = _models.RequiredBytes(tier, includeLlm: true),
                freeBytes = SystemCheck.FreeBytes(_paths.Root),
                downloading = _downloadCts != null
            };
        });
        b.Register("models.download", p =>
        {
            if (_downloadCts != null) return new { started = false };
            var tier = Enum.TryParse<PerformanceTier>(p.Str("tier"), true, out var t) ? t : Tier;
            bool llm = p.Bool("includeLlm", true);
            var cts = _downloadCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _models.DownloadAsync(tier, llm, cts.Token);
                    _bridge.Emit("modelsDone", new { ok = true });
                }
                catch (OperationCanceledException) { _bridge.Emit("modelsDone", new { ok = false, cancelled = true, message = "Pausiert – Fortsetzen jederzeit möglich." }); }
                catch (HildeException ex) { _bridge.Emit("modelsDone", new { ok = false, errorId = ex.Error.Id, message = ex.Error.Message + (ex.Error.Hint is { } h ? " " + h : "") }); }
                catch (Exception ex)
                {
                    _log.Error(ex, "Modelldownload");
                    _bridge.Emit("modelsDone", new { ok = false, errorId = HildeErrors.DownloadFailed.Id, message = HildeErrors.DownloadFailed.Message });
                }
                finally { _downloadCts = null; }
            });
            return new { started = true };
        });
        b.Register("models.location", _ => new
        {
            path = _paths.Models,
            isDefault = string.IsNullOrWhiteSpace(_settings.Current.ModelsDirectory),
            oneDrive = HildePaths.IsInOneDrive(_paths.Models)
        });
        b.Register("models.chooseFolder", _ => _ui.Invoke(() =>
        {
            var fd = new Microsoft.Win32.OpenFolderDialog { Title = "Neuen Speicherort für die HILDE-Modelle wählen" };
            if (fd.ShowDialog(_main) != true) return (object?)new { cancelled = true };
            var target = Path.Combine(fd.FolderName, "HILDE-Modelle");
            return new { cancelled = false, path = target, oneDrive = HildePaths.IsInOneDrive(target),
                         freeBytes = SystemCheck.FreeBytes(fd.FolderName) };
        }));
        b.RegisterAsync("models.move", async p =>
        {
            var target = p.Bool("reset") ? _paths.DefaultModels : p.Str("path") ?? throw new ArgumentException("Ordner fehlt");
            if (_downloadCts != null) throw new HildeException(HildeErrors.Unexpected, detail: "Download läuft");
            // Modelle freigeben, bevor Dateien verschoben werden
            await _speechClient.StopAsync();
            await _llmClient.StopAsync();
            _vad.Dispose();
            Directory.CreateDirectory(target);
            int n = await Task.Run(() => _models.MoveAllAsync(target, CancellationToken.None));
            var c = _settings.Current.Clone();
            c.ModelsDirectory = p.Bool("reset") ? null : target;
            _paths.ModelsOverride = c.ModelsDirectory;
            _settings.Save(c);
            return new { moved = n, path = _paths.Models };
        });
        b.Register("app.uninstallInfo", _ => Uninstaller.Find() is { } u ? new { available = true, kind = u.Kind } : new { available = false, kind = "" });
        b.Register("app.uninstall", p =>
        {
            var entry = Uninstaller.Find() ?? throw new HildeException(HildeErrors.Unexpected, detail: "Kein Deinstallationseintrag gefunden");
            Uninstaller.Launch(entry, p.Bool("deleteData"), _paths.Root);
            Quit();
            return null;
        });
        b.RegisterAsync("models.checkUpdates", async p =>
        {
            var tier = Enum.TryParse<PerformanceTier>(p.Str("tier"), true, out var t) ? t : Tier;
            try
            {
                var r = await _models.CheckForUpdatesAsync(_settings.Current.ModelsManifestUrl, tier, CancellationToken.None);
                return new { ok = true, r.Available, r.CurrentVersion, r.RemoteVersion, r.RemoteDate, newModels = r.NewModels, message = (string?)null };
            }
            catch (Exception ex)
            {
                _log.Information("Modellliste nicht erreichbar: {Msg}", ex.Message);
                return new { ok = false, Available = false, CurrentVersion = 0, RemoteVersion = 0, RemoteDate = "", newModels = (IReadOnlyList<ModelManager.ModelUpdate>)Array.Empty<ModelManager.ModelUpdate>(),
                    message = (string?)"Die Modellliste ist gerade nicht erreichbar. HILDE arbeitet mit den vorhandenen Modellen weiter." };
            }
        });
        b.Register("models.applyUpdate", p =>
        {
            if (_downloadCts != null) return new { started = false };
            _models.ApplyPendingManifest();
            var tier = Enum.TryParse<PerformanceTier>(p.Str("tier"), true, out var t) ? t : Tier;
            bool llm = !_settings.Current.LlmSkipped || p.Bool("includeLlm");
            var cts = _downloadCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _models.DownloadAsync(tier, llm, cts.Token);
                    // Neue Modelle laufen erst nach Neustart der Engines; alte erst danach löschen
                    await _speechClient.StopAsync();
                    await _llmClient.StopAsync();
                    int removed = _models.RemoveSuperseded();
                    _bridge.Emit("modelsDone", new { ok = true, removed });
                }
                catch (OperationCanceledException) { _bridge.Emit("modelsDone", new { ok = false, cancelled = true, message = "Pausiert – Fortsetzen jederzeit möglich." }); }
                catch (HildeException ex) { _bridge.Emit("modelsDone", new { ok = false, errorId = ex.Error.Id, message = ex.Error.Message }); }
                catch (Exception ex)
                {
                    _log.Error(ex, "Modell-Aktualisierung");
                    _bridge.Emit("modelsDone", new { ok = false, errorId = HildeErrors.DownloadFailed.Id, message = HildeErrors.DownloadFailed.Message });
                }
                finally { _downloadCts = null; }
            });
            return new { started = true };
        });
        b.Register("models.cancel", _ => { _downloadCts?.Cancel(); return null; });
        b.RegisterAsync("models.import", async p =>
        {
            string? source = _ui.Invoke(() =>
            {
                if (p.Str("type") == "zip")
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Offline-Modellpaket (*.zip)|*.zip" };
                    return dlg.ShowDialog(_main) == true ? dlg.FileName : null;
                }
                var fd = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner mit HILDE-Modellen wählen" };
                return fd.ShowDialog(_main) == true ? fd.FolderName : null;
            });
            if (source == null) return new { imported = 0, cancelled = true };
            int n = await _models.ImportOfflinePackageAsync(source, CancellationToken.None);
            return new { imported = n, cancelled = false };
        });
        b.RegisterAsync("engines.health", async _ => new
        {
            speech = await _speech.HealthAsync(CancellationToken.None),
            language = await _text.HealthAsync(CancellationToken.None),
            gpuFallback = _speechClient.GpuDisabledAfterCrash || _llmClient.GpuDisabledAfterCrash
        });
        b.Register("setup.complete", p =>
        {
            var s = _settings.Current.Clone();
            s.SetupCompleted = true;
            s.LlmSkipped = p.Bool("llmSkipped");
            if (s.LlmSkipped && s.TextMode != TextMode.Original && !_llmClient.HasModel) s.TextMode = TextMode.Clean;
            _settings.Save(s);
            return null;
        });

        // Testdiktat (ohne Einfügen) – zugleich Messung der Verarbeitungszeit
        b.Register("test.start", _ =>
        {
            if (_audio.IsRecording) throw new HildeException(HildeErrors.Unexpected, detail: "Aufnahme läuft bereits");
            _testDir = Path.Combine(_paths.Temp, "test-" + Guid.NewGuid().ToString("N"));
            _audio.Start(_settings.Current.MicrophoneId, Path.Combine(_testDir, "capture.wav"));
            return null;
        });
        b.RegisterAsync("test.stop", async _ =>
        {
            var dir = _testDir;
            _testDir = null;
            if (dir == null || !_audio.IsRecording) return new { ok = false, message = "Keine Testaufnahme aktiv." };
            try
            {
                var audio = await _audio.StopAsync();
                var vad = _vad.Analyze(WavIO.ReadMono16k(audio.WavPath));
                if (!vad.HasSpeech) return new { ok = false, message = HildeErrors.NoSpeech.Message };
                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var sr = await _speech.TranscribeAsync(new SpeechRequest(audio.WavPath, _settings.Current.Language, _dictionary.InitialPrompt, Fast: _settings.Current.FastShortDictation), cts.Token);
                var asr = sw.Elapsed.TotalSeconds;
                var raw = HallucinationFilter.Clean(sr.Text, vad);
                var tr = await _text.ProcessAsync(new TextRequest(_dictionary.Apply(raw), _settings.Current.TextMode,
                    sr.DetectedLanguage, _dictionary.ProtectedTerms), cts.Token);
                return (object)new
                {
                    ok = true, raw, clean = _dictionary.Apply(tr.Text), usedModel = tr.UsedModel, fallback = tr.FallbackReason,
                    asrSeconds = asr, totalSeconds = sw.Elapsed.TotalSeconds, audioSeconds = audio.Duration.TotalSeconds
                };
            }
            catch (HildeException ex) { return new { ok = false, message = ex.Error.Message + (ex.Error.Hint is { } h ? " " + h : "") }; }
            catch (Exception ex)
            {
                _log.Warning(ex, "Testdiktat");
                return new { ok = false, message = HildeErrors.SpeechEngineFailed.Message };
            }
            finally { TryDeleteDir(dir); }
        });
        b.Register("test.cancel", _ =>
        {
            if (_testDir != null) { _audio.Cancel(); TryDeleteDir(_testDir); _testDir = null; }
            return null;
        });
    }

    /// <summary>Behaltenes Audio nach der eingestellten Anzahl Tage löschen.</summary>
    private async Task CleanupOldAudioAsync()
    {
        try
        {
            int days = _settings.Current.Archive.AudioRetentionDays;
            if (days <= 0) return;
            var cutoff = DateTime.UtcNow.AddDays(-days);
            int n = 0;
            foreach (var r in await _repo.ListAsync(new TranscriptQuery(null, null, 100000)))
            {
                if (r.AudioPath == null || r.CreatedUtc > cutoff) continue;
                TryDeleteDir(_paths.RecordingDir(r.Id));
                r.AudioPath = null;
                await _repo.SaveAsync(r);
                n++;
            }
            if (n > 0) _log.Information("{Count} Audiodateien nach {Days} Tagen gelöscht", n, days);
        }
        catch (Exception ex) { _log.Warning(ex, "Audio-Aufräumen"); }
    }

    private static string Preview(string text) => text.Length <= 180 ? text : text[..180].TrimEnd() + " …";

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 80 ? name[..80] : name;
    }

    private string ReadLicenses()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "licenses");
        if (!Directory.Exists(dir)) return "";
        return string.Join("\n\n", Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f).Select(File.ReadAllText));
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception ex) { _log.Debug(ex, "Löschen"); }
    }

    /// <summary>Muss auf dem UI-Thread laufen (vor <see cref="DisposeAsync"/>).</summary>
    public void DisposeUi()
    {
        _hook?.Dispose();
        _hook = null;
        _tray?.Dispose();
        _tray = null;
        _pill?.Close();
        _main?.ForceClose();
    }

    /// <summary>Nicht-UI-Teil; läuft ohne UI-Synchronisationskontext (kein Deadlock beim Beenden).</summary>
    public async ValueTask DisposeAsync()
    {
        _downloadCts?.Cancel();
        if (_controller != null) await _controller.DisposeAsync().ConfigureAwait(false);
        try { await _speechClient.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _llmClient.DisposeAsync().ConfigureAwait(false); } catch { }
        _audio?.Dispose();
        _vad?.Dispose();
    }
}
