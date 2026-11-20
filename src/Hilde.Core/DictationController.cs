using System.Diagnostics;
using System.Threading.Channels;
using Serilog;

namespace Hilde.Core;

public sealed record StatusEventArgs(HildeState State, string? Message = null, string? ErrorId = null,
    RecordingMode? Mode = null);

public sealed record CopyFallbackEventArgs(string Text, string ErrorId, string Message);

/// <summary>
/// Diktierpipeline (Pflichtenheft 5.3): Hotkey → Ziel merken → Aufnahme → Whisper → Wörterbuch →
/// KI-Bereinigung → Einfügen → Archiv. Diktate werden strikt nacheinander verarbeitet.
/// </summary>
public sealed class DictationController : IAsyncDisposable
{
    private sealed class RecordingContext
    {
        public required string Id { get; init; }
        public required InsertionTarget Target { get; init; }
        public required string RawWavPath { get; init; }
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public RecordingMode Mode { get; set; }
    }

    private sealed record ProcessJob(RecordingContext Context, CapturedAudio Audio, bool Insert, string? Notice,
        LivePreviewSession? Preview = null);

    private readonly IAudioCaptureService _audio;
    private readonly IVoiceActivityDetector _vad;
    private readonly ISpeechRecognitionEngine _speech;
    private readonly ITextProcessingEngine _text;
    private readonly IDictionaryService _dictionary;
    private readonly ITextInsertionService _insertion;
    private readonly ITranscriptRepository _repo;
    private readonly ISettingsStore _settings;
    private readonly HildePaths _paths;
    private readonly ILogger _log;

    private readonly Channel<GestureAction> _actions = Channel.CreateUnbounded<GestureAction>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<ProcessJob> _jobs = Channel.CreateUnbounded<ProcessJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _actionLoop;
    private readonly Task _jobLoop;
    private RecordingContext? _current;
    private LivePreviewSession? _preview;
    private int _pendingJobs;
    private CancellationTokenSource? _doneResetCts;

    public GestureRecognizer Gestures { get; } = new();

    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<float>? LevelChanged;
    public event EventHandler<CopyFallbackEventArgs>? CopyFallbackRequested;
    public event EventHandler<TranscriptRecord>? Completed;
    /// <summary>Live-Text: (bereits fest erkannt, vorläufig). Leer = Vorschau ausblenden.</summary>
    public event Action<string, string>? PreviewChanged;

    public HildeState State { get; private set; } = HildeState.Ready;

    public DictationController(IAudioCaptureService audio, IVoiceActivityDetector vad,
        ISpeechRecognitionEngine speech, ITextProcessingEngine text, IDictionaryService dictionary,
        ITextInsertionService insertion, ITranscriptRepository repo, ISettingsStore settings,
        HildePaths paths, ILogger log)
    {
        _audio = audio; _vad = vad; _speech = speech; _text = text; _dictionary = dictionary;
        _insertion = insertion; _repo = repo; _settings = settings; _paths = paths;
        _log = log.ForContext<DictationController>();

        ApplyGestureSettings(settings.Current);
        settings.Changed += (_, s) => ApplyGestureSettings(s);
        _audio.LevelChanged += (_, l) => LevelChanged?.Invoke(this, l);
        _audio.CaptureFailed += (_, _) => Post(GestureAction.StopAndProcess, deviceLost: true);

        _actionLoop = Task.Run(ActionLoopAsync);
        _jobLoop = Task.Run(JobLoopAsync);
    }

    private void ApplyGestureSettings(HildeSettings s)
    {
        Gestures.MinHoldMs = s.Hotkey.MinHoldMs;
        Gestures.DoubleTapMs = s.Hotkey.DoubleTapMs;
    }

    private volatile bool _deviceLostFlag;

    /// <summary>Thread-sicher und schnell – darf direkt aus dem Tastatur-Hook aufgerufen werden.</summary>
    public void Post(GestureAction action, bool deviceLost = false)
    {
        if (action == GestureAction.None) return;
        if (deviceLost) _deviceLostFlag = true;
        _actions.Writer.TryWrite(action);
    }

    /// <summary>Pill/Tray: Freihandaufnahme starten oder beenden.</summary>
    public void ToggleHandsFree()
    {
        GestureAction a;
        lock (Gestures) a = Gestures.ExternalToggle();
        Post(a);
    }

    private async Task ActionLoopAsync()
    {
        await foreach (var action in _actions.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            try
            {
                switch (action)
                {
                    case GestureAction.StartProvisional: StartRecording(RecordingMode.PushToTalk, provisional: true); break;
                    case GestureAction.StartHandsFree: StartRecording(RecordingMode.HandsFree, provisional: false); break;
                    case GestureAction.DiscardProvisional: DiscardRecording(silent: true); break;
                    case GestureAction.Cancel: DiscardRecording(silent: false); break;
                    case GestureAction.StopAndProcess: await StopRecordingAsync().ConfigureAwait(false); break;
                }
            }
            catch (HildeException ex)
            {
                _log.Warning("Aktion {Action} fehlgeschlagen: {ErrorId}", action, ex.Error.Id);
                ResetGestures();
                _current = null;
                SetState(HildeState.Error, ex.Error.Message, ex.Error.Id);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Aktion {Action} fehlgeschlagen", action);
                ResetGestures();
                _current = null;
                SetState(HildeState.Error, HildeErrors.Unexpected.Message, HildeErrors.Unexpected.Id);
            }
        }
    }

    private void StartRecording(RecordingMode mode, bool provisional)
    {
        if (_current != null)
        {
            // Doppeltipp nach verworfenem Tipp: läuft schon? Dann nur Modus wechseln.
            _current.Mode = mode;
            if (!provisional) SetState(HildeState.Recording, "Freihand – zum Beenden Taste erneut drücken", mode: mode);
            return;
        }
        var target = _insertion.CaptureTarget();
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = _paths.RecordingDir(id);
        Directory.CreateDirectory(dir);
        var raw = Path.Combine(dir, "capture.wav");
        _audio.Start(_settings.Current.MicrophoneId, raw);
        _current = new RecordingContext { Id = id, Target = target, RawWavPath = raw, Mode = mode };
        if (_settings.Current.LivePreview)
        {
            var session = new LivePreviewSession(_audio, _speech, _settings.Current.Language, _dictionary.InitialPrompt,
                dir, _log, (fixedText, provisional) => PreviewChanged?.Invoke(fixedText, provisional));
            _preview = session;
            session.Start();
        }
        _deviceLostFlag = false;
        _doneResetCts?.Cancel();
        SetState(HildeState.Recording,
            mode == RecordingMode.HandsFree ? "Freihand – zum Beenden Taste erneut drücken" : "Aufnahme …", mode: mode);
    }

    private void DiscardRecording(bool silent)
    {
        var ctx = _current;
        if (ctx == null) return;
        _current = null;
        _preview?.Abandon();
        _preview = null;
        PreviewChanged?.Invoke("", "");
        try { _audio.Cancel(); } catch (Exception ex) { _log.Debug(ex, "Cancel"); }
        TryDeleteDir(_paths.RecordingDir(ctx.Id));
        if (silent) SetIdleOrProcessing();
        else SetState(HildeState.Done, "Aufnahme verworfen");
        if (!silent) ScheduleReady();
    }

    private async Task StopRecordingAsync()
    {
        var ctx = _current;
        if (ctx == null) { ResetGestures(); return; }
        _current = null;
        var preview = _preview;
        _preview = null;
        var notice = _deviceLostFlag ? HildeErrors.MicrophoneLost.Message : null;
        CapturedAudio audio;
        try
        {
            audio = await _audio.StopAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Aufnahme konnte nicht beendet werden");
            SetState(HildeState.Error, HildeErrors.MicrophoneLost.Message, HildeErrors.MicrophoneLost.Id);
            return;
        }
        ResetGestures();
        Interlocked.Increment(ref _pendingJobs);
        SetState(HildeState.Processing, "Verarbeite …");
        _jobs.Writer.TryWrite(new ProcessJob(ctx, audio, Insert: true, notice, preview));
    }

    private void ResetGestures() { lock (Gestures) Gestures.Reset(); }

    private async Task JobLoopAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(job).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Verarbeitung {Id} fehlgeschlagen", job.Context.Id);
                SetState(HildeState.Error, HildeErrors.Unexpected.Message, HildeErrors.Unexpected.Id);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingJobs);
            }
        }
    }

    private async Task ProcessAsync(ProcessJob job)
    {
        var ctx = job.Context;
        var s = _settings.Current;
        var sw = Stopwatch.StartNew();
        var record = new TranscriptRecord
        {
            Id = ctx.Id,
            Kind = TranscriptKind.Dictation,
            CreatedUtc = ctx.StartedUtc,
            Status = TranscriptStatus.Processing,
            Mode = s.TextMode,
            DurationMs = (long)job.Audio.Duration.TotalMilliseconds,
            AudioPath = job.Audio.WavPath,
            TargetApp = job.Insert ? ctx.Target.ProcessName : null,
            Language = s.Language,
            Source = "Mikrofon",
            Title = BuildTitle(ctx.StartedUtc, ctx.Target.ProcessName)
        };
        await SafeSaveAsync(record).ConfigureAwait(false);
        TryDelete(ctx.RawWavPath);

        if (job.Audio.Duration < TimeSpan.FromMilliseconds(300))
        {
            await DropAsync(record, HildeErrors.TooShort).ConfigureAwait(false);
            return;
        }

        float[] samples = WavIO.ReadMono16k(job.Audio.WavPath);
        var vad = _vad.Analyze(samples);
        if (!vad.HasSpeech)
        {
            await DropAsync(record, HildeErrors.NoSpeech).ConfigureAwait(false);
            return;
        }

        // 1) Spracherkennung (isolierter Prozess, mit Timeout) – Vorarbeit der Live-Vorschau übernehmen
        SpeechResult speech;
        string? prefix = null;
        string asrPath = job.Audio.WavPath;
        var asrDuration = job.Audio.Duration;
        if (job.Preview != null)
        {
            await job.Preview.StopAsync().ConfigureAwait(false);
            if (!job.Preview.Failed && job.Preview.CommittedSamples > 0 && job.Preview.CommittedSamples < samples.Length)
            {
                prefix = job.Preview.CommittedText;
                var rest = samples[job.Preview.CommittedSamples..];
                asrPath = Path.Combine(_paths.RecordingDir(ctx.Id), "rest.wav");
                WavIO.WriteMono16k(asrPath, rest);
                asrDuration = TimeSpan.FromSeconds(rest.Length / (double)WavIO.SampleRate);
                _log.Information("Vorarbeit genutzt: {Done:F1}s bereits erkannt, Rest {Rest:F1}s",
                    job.Preview.CommittedSamples / (double)WavIO.SampleRate, asrDuration.TotalSeconds);
            }
        }
        var timeout = TimeSpan.FromSeconds(60) + asrDuration * 3;
        using (var tcs = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
        {
            tcs.CancelAfter(timeout);
            try
            {
                if (prefix != null && !LivePreviewSession.HasEnergy(WavIO.ReadMono16k(asrPath)))
                {
                    speech = new SpeechResult("", s.Language is "de" or "en" ? s.Language : "de", []);
                }
                else
                {
                    speech = await _speech.TranscribeAsync(
                        new SpeechRequest(asrPath, s.Language,
                            prefix == null ? _dictionary.InitialPrompt : LivePreviewSession.BuildPrompt(_dictionary.InitialPrompt, prefix),
                            Fast: s.FastShortDictation), tcs.Token)
                        .ConfigureAwait(false);
                }
                if (prefix != null)
                    speech = speech with { Text = (prefix + " " + HallucinationFilter.Clean(speech.Text)).Trim() };
            }
            catch (Exception ex)
            {
                var err = ex is OperationCanceledException ? HildeErrors.SpeechTimeout
                    : ex is HildeException he ? he.Error : HildeErrors.SpeechEngineFailed;
                _log.Warning(ex, "Spracherkennung fehlgeschlagen ({ErrorId}) für {Id}", err.Id, ctx.Id);
                record.Status = TranscriptStatus.Failed;
                record.ErrorId = err.Id;
                await SafeSaveAsync(record).ConfigureAwait(false);
                PreviewChanged?.Invoke("", "");
                SetState(HildeState.Error, err.Message, err.Id);
                ScheduleReady(TimeSpan.FromSeconds(6));
                return;
            }
        }
        var tAsr = sw.Elapsed;

        var raw = HallucinationFilter.Clean(speech.Text, vad);
        if (string.IsNullOrWhiteSpace(raw))
        {
            await DropAsync(record, HildeErrors.NoSpeech).ConfigureAwait(false);
            return;
        }
        record.RawText = raw;
        record.Language = speech.DetectedLanguage;
        await SafeSaveAsync(record).ConfigureAwait(false);   // Rohtext ist ab hier sicher

        // 2) Wörterbuch + KI-/Regelbereinigung
        string clean;
        if (s.TextMode == TextMode.Original)
        {
            clean = raw;
        }
        else
        {
            var pre = _dictionary.Apply(raw);
            var tr = await _text.ProcessAsync(
                new TextRequest(pre, s.TextMode, speech.DetectedLanguage, _dictionary.ProtectedTerms), _cts.Token)
                .ConfigureAwait(false);
            clean = _dictionary.Apply(tr.Text);
            record.FallbackReason = tr.FallbackReason;
        }
        record.CleanText = clean;
        record.FinalText = clean;
        await SafeSaveAsync(record).ConfigureAwait(false);
        var tText = sw.Elapsed;

        // 3) Einfügen
        string doneMessage = "Eingefügt";
        if (job.Insert)
        {
            InsertionOutcome outcome;
            try
            {
                outcome = await _insertion.InsertAsync(ctx.Target, clean, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Einfügen fehlgeschlagen");
                outcome = InsertionOutcome.Fail(HildeErrors.InsertFailed.Id);
            }
            record.InsertionMethod = outcome.Method;
            if (!outcome.Success)
            {
                var err = HildeErrors.Get(outcome.ErrorId);
                record.ErrorId = err.Id;
                _log.Information("Einfügen nicht möglich: {ErrorId} ({Detail}) Ziel {Target}", err.Id, outcome.Detail ?? "-", ctx.Target.Description);
                CopyFallbackRequested?.Invoke(this, new CopyFallbackEventArgs(clean, err.Id, err.Message));
                doneMessage = "Bereit zum Kopieren";
            }
        }
        else doneMessage = "Wiederhergestellt";

        // 4) Archiv
        record.Status = job.Insert ? TranscriptStatus.Done : TranscriptStatus.Recovered;
        record.UpdatedUtc = DateTime.UtcNow;
        if (!s.Archive.KeepAudio)
        {
            TryDeleteDir(_paths.RecordingDir(ctx.Id));
            record.AudioPath = null;
        }
        if (s.Archive.Enabled || !job.Insert) await SafeSaveAsync(record).ConfigureAwait(false);
        else
        {
            try { await _repo.DeleteAsync(record.Id).ConfigureAwait(false); } catch (Exception ex) { _log.Warning(ex, "Löschen"); }
            TryDeleteDir(_paths.RecordingDir(ctx.Id));
        }

        // Nur Kennzahlen loggen – niemals Inhalte.
        _log.Information("Diktat {Id}: Audio {Audio:F1}s, ASR {Asr:F2}s, Text {Text:F2}s, gesamt {Total:F2}s, Modus {Mode}, Einfügen {Method}, Fallback {Fallback}",
            ctx.Id, job.Audio.Duration.TotalSeconds, tAsr.TotalSeconds, (tText - tAsr).TotalSeconds,
            sw.Elapsed.TotalSeconds, s.TextMode, record.InsertionMethod, record.FallbackReason ?? "-");

        PreviewChanged?.Invoke("", "");
        Completed?.Invoke(this, record);
        SetState(HildeState.Done, job.Notice ?? doneMessage);
        ScheduleReady();
    }

    /// <summary>Gesicherte, aber nicht verarbeitete Aufnahme (Fehler oder Absturz) erneut verarbeiten.</summary>
    public async Task<bool> ReprocessAsync(string id)
    {
        var rec = await _repo.GetAsync(id).ConfigureAwait(false);
        var dir = _paths.RecordingDir(id);
        var wav16 = rec?.AudioPath is { } p && File.Exists(p) ? p : Path.Combine(dir, "mic16k.wav");
        if (!File.Exists(wav16))
        {
            var raw = Path.Combine(dir, "capture.wav");
            if (!File.Exists(raw)) return false;
            wav16 = _audio.NormalizeFile(raw, Path.Combine(dir, "mic16k.wav")).WavPath;
        }
        var duration = TimeSpan.FromSeconds(new FileInfo(wav16).Length / 2.0 / WavIO.SampleRate);
        var ctx = new RecordingContext
        {
            Id = id, Target = NullTarget.Instance, RawWavPath = Path.Combine(dir, "capture.wav"),
            Mode = RecordingMode.PushToTalk
        };
        Interlocked.Increment(ref _pendingJobs);
        SetState(HildeState.Processing, "Verarbeite gesicherte Aufnahme …");
        _jobs.Writer.TryWrite(new ProcessJob(ctx, new CapturedAudio(wav16, duration, 0), Insert: false, null));
        return true;
    }

    /// <summary>Beim Start: unterbrochene Aufnahmen finden und im Hintergrund wiederherstellen.</summary>
    public async Task<int> RecoverAsync()
    {
        int count = 0;
        var known = (await _repo.FindUnfinishedAsync().ConfigureAwait(false)).ToDictionary(r => r.Id);
        if (!Directory.Exists(_paths.Recordings)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(_paths.Recordings))
        {
            var id = Path.GetFileName(dir);
            var existing = await _repo.GetAsync(id).ConfigureAwait(false);
            bool unfinished = existing == null || known.ContainsKey(id);
            bool hasAudio = File.Exists(Path.Combine(dir, "capture.wav")) || File.Exists(Path.Combine(dir, "mic16k.wav"));
            if (!unfinished || !hasAudio) continue;
            if (existing?.Status == TranscriptStatus.Failed) continue; // bewusst: Nutzer entscheidet
            try
            {
                if (existing == null)
                {
                    await SafeSaveAsync(new TranscriptRecord
                    {
                        Id = id, Status = TranscriptStatus.Processing, Title = "Wiederhergestellte Aufnahme",
                        CreatedUtc = Directory.GetCreationTimeUtc(dir)
                    }).ConfigureAwait(false);
                }
                if (await ReprocessAsync(id).ConfigureAwait(false)) count++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Wiederherstellung {Id} fehlgeschlagen", id);
            }
        }
        if (count > 0) _log.Information("{Count} unterbrochene Aufnahme(n) wiederhergestellt", count);
        return count;
    }

    private async Task DropAsync(TranscriptRecord record, HildeErrors.Entry reason)
    {
        PreviewChanged?.Invoke("", "");
        try { await _repo.DeleteAsync(record.Id).ConfigureAwait(false); } catch (Exception ex) { _log.Debug(ex, "Drop"); }
        TryDeleteDir(_paths.RecordingDir(record.Id));
        SetState(HildeState.Done, reason.Message, reason.Id);
        ScheduleReady();
    }

    private async Task SafeSaveAsync(TranscriptRecord record)
    {
        try
        {
            record.UpdatedUtc = DateTime.UtcNow;
            await _repo.SaveAsync(record).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Speicherfehler dürfen das Diktat nicht verhindern.
            _log.Error(ex, "{ErrorId} beim Speichern von {Id}", HildeErrors.StorageFailed.Id, record.Id);
        }
    }

    private static string BuildTitle(DateTime utc, string? app)
    {
        var local = utc.ToLocalTime();
        return app is { Length: > 0 } ? $"Diktat {local:dd.MM. HH:mm} · {app}" : $"Diktat {local:dd.MM. HH:mm}";
    }

    private void SetIdleOrProcessing()
    {
        if (Volatile.Read(ref _pendingJobs) > 0) SetState(HildeState.Processing, "Verarbeite …");
        else SetState(HildeState.Ready);
    }

    private void ScheduleReady(TimeSpan? delay = null)
    {
        _doneResetCts?.Cancel();
        var cts = _doneResetCts = new CancellationTokenSource();
        _ = Task.Delay(delay ?? TimeSpan.FromMilliseconds(1600), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || _current != null) return;
            SetIdleOrProcessing();
        }, TaskScheduler.Default);
    }

    private void SetState(HildeState state, string? message = null, string? errorId = null, RecordingMode? mode = null)
    {
        // Eine laufende Aufnahme hat in der Anzeige Vorrang.
        if (_current != null && state is HildeState.Processing or HildeState.Done) return;
        State = state;
        StatusChanged?.Invoke(this, new StatusEventArgs(state, message, errorId, mode));
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { _log.Debug(ex, "Löschen"); }
    }

    private void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { _log.Debug(ex, "Ordner löschen"); }
    }

    public async ValueTask DisposeAsync()
    {
        _actions.Writer.TryComplete();
        _jobs.Writer.TryComplete();
        try { if (_current != null) _audio.Cancel(); } catch { }
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_actionLoop, _jobLoop).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private sealed class NullTarget : InsertionTarget
    {
        public static readonly NullTarget Instance = new();
        public override string Description => "–";
        public override string? ProcessName => null;
        public override bool IsRemoteSession => false;
    }
}
