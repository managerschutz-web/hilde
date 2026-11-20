using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Hilde.Core;
using Serilog;

namespace Hilde.Setup;

/// <summary>
/// Download (fortsetzbar, HTTP-Range), SHA-256-Prüfung, Offline-Import, keine stillen Modellwechsel:
/// Ein installiertes Modell wird nur durch ausdrückliche Nutzeraktion ersetzt.
/// </summary>
public sealed class ModelManager : IModelManager
{
    private readonly HildePaths _paths;
    private ModelManifest _manifest;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly object _stateLock = new();
    private InstalledModels _installed;

    public event EventHandler<ModelProgress>? Progress;
    public ModelManifest Manifest => _manifest;

    public ModelManager(HildePaths paths, ModelManifest manifest, ILogger log, HttpMessageHandler? handler = null)
    {
        _paths = paths;
        _manifest = manifest;
        _log = log.ForContext<ModelManager>();
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HILDE-Modelldownload/1.0");
        _installed = LoadState();
    }

    private InstalledModels LoadState()
    {
        try
        {
            if (File.Exists(_paths.ModelsStateFile))
                return JsonSerializer.Deserialize<InstalledModels>(File.ReadAllText(_paths.ModelsStateFile), ModelManifest.Json) ?? new();
        }
        catch (Exception ex) { _log.Warning(ex, "models.json defekt"); }
        return new InstalledModels();
    }

    private void SaveState()
    {
        lock (_stateLock)
        {
            Directory.CreateDirectory(_paths.Config);
            var tmp = _paths.ModelsStateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_installed, ModelManifest.Json));
            File.Move(tmp, _paths.ModelsStateFile, overwrite: true);
        }
    }

    public IReadOnlyList<ModelStatus> GetStatus(PerformanceTier tier) =>
        _manifest.ForTier(tier).Select(m =>
        {
            var inst = _installed.Models.GetValueOrDefault(m.Id);
            bool present = inst != null && File.Exists(inst.Path);
            return new ModelStatus(m.Id, m.Kind, m.DisplayName, m.SizeBytes, present,
                present && inst!.VerifiedAgainstManifest, m.License, !m.Optional);
        }).ToList();

    public string? GetInstalledPath(ModelKind kind, PerformanceTier tier)
    {
        // Bevorzugt das Modell der gewählten Stufe; sonst irgendein installiertes dieser Art (kein stiller Wechsel
        // auf ein nicht installiertes Modell, aber Weiterarbeit mit dem vorhandenen).
        var preferred = _manifest.Find(kind, tier);
        if (preferred != null && _installed.Models.TryGetValue(preferred.Id, out var p) && File.Exists(p.Path)) return p.Path;
        foreach (var m in _manifest.Models.Where(m => m.Kind == kind))
            if (_installed.Models.TryGetValue(m.Id, out var i) && File.Exists(i.Path)) return i.Path;
        return null;
    }

    public ModelEntry? GetInstalledEntry(ModelKind kind, PerformanceTier tier)
    {
        var path = GetInstalledPath(kind, tier);
        if (path == null) return null;
        var id = _installed.Models.Values.FirstOrDefault(i => i.Path == path)?.Id;
        return _manifest.Models.FirstOrDefault(m => m.Id == id);
    }

    public long RequiredBytes(PerformanceTier tier, bool includeLlm) =>
        _manifest.ForTier(tier).Where(m => includeLlm || m.Kind != ModelKind.Llm)
            .Where(m => !IsInstalled(m)).Sum(m => m.SizeBytes ?? 0);

    private bool IsInstalled(ModelEntry m) => _installed.Models.TryGetValue(m.Id, out var i) && File.Exists(i.Path);

    public async Task DownloadAsync(PerformanceTier tier, bool includeLlm, CancellationToken ct)
    {
        var todo = _manifest.ForTier(tier)
            .Where(m => (includeLlm || m.Kind != ModelKind.Llm) && (!m.Optional || m.Kind == ModelKind.Llm))
            .Where(m => !IsInstalled(m)).ToList();

        long needed = todo.Sum(m => m.SizeBytes ?? 0) + 200L * 1024 * 1024;
        var free = SystemCheck.FreeBytes(_paths.Root);
        if (free is { } f && f < needed)
            throw new HildeException(HildeErrors.DiskFull, detail: $"{f / 1_048_576} MB frei, {needed / 1_048_576} MB nötig");

        foreach (var m in todo)
        {
            ct.ThrowIfCancellationRequested();
            await DownloadOneAsync(m, ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadOneAsync(ModelEntry m, CancellationToken ct)
    {
        var dir = _paths.ModelDir(m.Kind);
        Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, m.FileName);
        var part = final + ".part";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, m.Url);
                if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // .part ist bereits vollständig (oder kaputt) – prüfen statt neu laden
                    if (await FinalizeAsync(m, part, final, ct).ConfigureAwait(false)) return;
                    File.Delete(part);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) existing = 0;
                long? total = resp.Content.Headers.ContentLength is { } len ? len + existing : m.SizeBytes;

                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buffer = new byte[1 << 16];
                    long done = existing;
                    var lastReport = DateTime.MinValue;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                        {
                            lastReport = DateTime.UtcNow;
                            Report(m, done, total, "download");
                        }
                    }
                }
                if (await FinalizeAsync(m, part, final, ct).ConfigureAwait(false)) return;
                throw new HildeException(HildeErrors.ModelChecksum);
            }
            catch (OperationCanceledException)
            {
                Report(m, 0, m.SizeBytes, "paused", message: "Download pausiert – wird beim nächsten Mal fortgesetzt.");
                throw;
            }
            catch (HildeException ex) when (ex.Error == HildeErrors.ModelChecksum)
            {
                Report(m, 0, m.SizeBytes, "error", ex.Error.Id, ex.Error.Message);
                throw;
            }
            catch (Exception ex) when (attempt < 4)
            {
                _log.Warning(ex, "Download {Model} Versuch {Attempt} fehlgeschlagen", m.Id, attempt);
                Report(m, 0, m.SizeBytes, "retry", HildeErrors.DownloadFailed.Id, $"Verbindung unterbrochen – neuer Versuch ({attempt}/3) …");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(m, 0, m.SizeBytes, "error", HildeErrors.DownloadFailed.Id, HildeErrors.DownloadFailed.Message);
                throw new HildeException(HildeErrors.DownloadFailed, ex);
            }
        }
    }

    private async Task<bool> FinalizeAsync(ModelEntry m, string part, string final, CancellationToken ct)
    {
        Report(m, 0, null, "verify", message: "Prüfe SHA-256 …");
        var hash = await HashFileAsync(part, ct).ConfigureAwait(false);
        bool verified = m.Sha256 != null;
        if (verified && !string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning("{ErrorId} Prüfsumme {Model} falsch", HildeErrors.ModelChecksum.Id, m.Id);
            File.Delete(part);
            return false;
        }
        if (!verified) _log.Warning("Modell {Model} ohne Manifest-Prüfsumme (nur interner Build) – Hash lokal festgehalten", m.Id);
        File.Move(part, final, overwrite: true);
        Register(m, final, hash, verified);
        Report(m, new FileInfo(final).Length, new FileInfo(final).Length, "done");
        return true;
    }

    private void Register(ModelEntry m, string path, string hash, bool verified)
    {
        lock (_stateLock)
        {
            _installed.Models[m.Id] = new InstalledModel
            {
                Id = m.Id, Path = path, Sha256 = hash, SizeBytes = new FileInfo(path).Length,
                InstalledUtc = DateTime.UtcNow, ManifestVersion = _manifest.ManifestVersion,
                VerifiedAgainstManifest = verified
            };
        }
        SaveState();
    }

    /// <summary>Offline-Paket: Ordner oder ZIP mit den Modelldateien (Dateinamen laut Manifest).</summary>
    public async Task<int> ImportOfflinePackageAsync(string folderOrZip, CancellationToken ct)
    {
        string folder = folderOrZip;
        string? tempDir = null;
        if (File.Exists(folderOrZip) && folderOrZip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            tempDir = Path.Combine(_paths.Temp, "import-" + Guid.NewGuid().ToString("N"));
            System.IO.Compression.ZipFile.ExtractToDirectory(folderOrZip, tempDir);
            folder = tempDir;
        }
        try
        {
            int count = 0;
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            foreach (var m in _manifest.Models)
            {
                if (!files.TryGetValue(m.FileName, out var src)) continue;
                Report(m, 0, m.SizeBytes, "verify", message: "Prüfe SHA-256 …");
                var hash = await HashFileAsync(src, ct).ConfigureAwait(false);
                if (m.Sha256 != null && !string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Report(m, 0, m.SizeBytes, "error", HildeErrors.ModelChecksum.Id, HildeErrors.ModelChecksum.Message);
                    continue;
                }
                var dir = _paths.ModelDir(m.Kind);
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, m.FileName);
                Report(m, 0, m.SizeBytes, "copy", message: "Kopiere …");
                await using (var s = File.OpenRead(src))
                await using (var d = File.Create(dst + ".part"))
                    await s.CopyToAsync(d, ct).ConfigureAwait(false);
                File.Move(dst + ".part", dst, overwrite: true);
                Register(m, dst, hash, m.Sha256 != null);
                Report(m, new FileInfo(dst).Length, new FileInfo(dst).Length, "done");
                count++;
            }
            return count;
        }
        finally
        {
            if (tempDir != null) try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public async Task<bool> VerifyAsync(string modelId, CancellationToken ct)
    {
        if (!_installed.Models.TryGetValue(modelId, out var inst) || !File.Exists(inst.Path)) return false;
        var hash = await HashFileAsync(inst.Path, ct).ConfigureAwait(false);
        var expected = _manifest.Models.FirstOrDefault(m => m.Id == modelId)?.Sha256 ?? inst.Sha256;
        return string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record ModelUpdate(string Id, string DisplayName, ModelKind Kind, long? SizeBytes, string License, string? Replaces);
    public sealed record UpdateCheck(bool Available, int CurrentVersion, int RemoteVersion, string RemoteDate, IReadOnlyList<ModelUpdate> NewModels);

    private ModelManifest? _pendingRemote;
    private string? _pendingRemoteJson;

    /// <summary>Lädt die Modellliste von <paramref name="url"/> (nur auf ausdrücklichen Wunsch) und vergleicht.</summary>
    public async Task<UpdateCheck> CheckForUpdatesAsync(string url, PerformanceTier tier, CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Nur https-Adressen sind erlaubt.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var json = await _http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        var remote = JsonSerializer.Deserialize<ModelManifest>(json, ModelManifest.Json) ?? throw new InvalidDataException("Modellliste leer.");
        if (remote.Models.Count == 0 || remote.Models.Any(m => !m.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(m.FileName)
                || m.FileName.Contains('/') || m.FileName.Contains('\\') || m.FileName.Contains("..")))
            throw new InvalidDataException("Modellliste ungültig.");
        var news = new List<ModelUpdate>();
        foreach (var m in remote.ForTier(tier))
        {
            bool known = _manifest.Models.Any(x => x.Id == m.Id && x.Sha256 == m.Sha256 && x.Url == m.Url);
            if (known) continue;
            var replaced = _manifest.Find(m.Kind, tier);
            news.Add(new ModelUpdate(m.Id, m.DisplayName, m.Kind, m.SizeBytes, m.License,
                replaced != null && replaced.Id != m.Id ? replaced.DisplayName : null));
        }
        bool available = remote.ManifestVersion > _manifest.ManifestVersion && news.Count > 0;
        if (available) { _pendingRemote = remote; _pendingRemoteJson = json; }
        return new UpdateCheck(available, _manifest.ManifestVersion, remote.ManifestVersion, remote.Updated, news);
    }

    /// <summary>Übernimmt die zuvor geprüfte neue Modellliste (gespeichert unter Config/models-manifest.json).</summary>
    public void ApplyPendingManifest()
    {
        if (_pendingRemote == null || _pendingRemoteJson == null) throw new InvalidOperationException("Keine neue Modellliste geprüft.");
        Directory.CreateDirectory(_paths.Config);
        File.WriteAllText(Path.Combine(_paths.Config, "models-manifest.json"), _pendingRemoteJson);
        _manifest = _pendingRemote;
        _pendingRemote = null;
        _pendingRemoteJson = null;
        _log.Information("Modellliste v{Version} übernommen", _manifest.ManifestVersion);
    }

    /// <summary>Entfernt installierte Modelle, die in der aktuellen Liste nicht mehr vorkommen und für die es Ersatz gibt.</summary>
    public int RemoveSuperseded()
    {
        int removed = 0;
        foreach (var inst in _installed.Models.Values.ToList())
        {
            if (_manifest.Models.Any(m => m.Id == inst.Id)) continue;
            Remove(inst.Id);
            removed++;
        }
        return removed;
    }

    /// <summary>Wählt die neuere der beiden Listen: mitgeliefert oder zuletzt übernommen.</summary>
    public static ModelManifest LoadEffective(string bundledPath, HildePaths paths)
    {
        var bundled = ModelManifest.Load(bundledPath);
        var local = Path.Combine(paths.Config, "models-manifest.json");
        try
        {
            if (File.Exists(local))
            {
                var l = ModelManifest.Load(local);
                if (l.ManifestVersion > bundled.ManifestVersion) return l;
            }
        }
        catch { /* defekt → mitgelieferte Liste */ }
        return bundled;
    }

    /// <summary>Verschiebt alle installierten Modelle in einen neuen Ordner (Engines müssen beendet sein).</summary>
    public async Task<int> MoveAllAsync(string newRoot, CancellationToken ct)
    {
        int moved = 0;
        foreach (var inst in _installed.Models.Values.ToList())
        {
            var entry = _manifest.Models.FirstOrDefault(m => m.Id == inst.Id);
            if (entry == null || !File.Exists(inst.Path)) continue;
            var dir = Path.Combine(newRoot, entry.Kind.ToString().ToLowerInvariant());
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, entry.FileName);
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(inst.Path), StringComparison.OrdinalIgnoreCase)) continue;
            Report(entry, 0, inst.SizeBytes, "copy", message: "Verschiebe …");
            // Kopieren + prüfen + alte Datei löschen (funktioniert auch über Laufwerksgrenzen)
            await using (var s = File.OpenRead(inst.Path))
            await using (var d = File.Create(target + ".part"))
                await s.CopyToAsync(d, ct).ConfigureAwait(false);
            File.Move(target + ".part", target, overwrite: true);
            if (!string.Equals(await HashFileAsync(target, ct).ConfigureAwait(false), inst.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new HildeException(HildeErrors.ModelChecksum);
            }
            var old = inst.Path;
            lock (_stateLock) inst.Path = target;
            SaveState();
            try { File.Delete(old); } catch (Exception ex) { _log.Warning(ex, "Alte Modelldatei nicht gelöscht"); }
            Report(entry, inst.SizeBytes, inst.SizeBytes, "done");
            moved++;
        }
        return moved;
    }

    /// <summary>Entfernt ein Modell (nur auf ausdrücklichen Wunsch).</summary>
    public void Remove(string modelId)
    {
        lock (_stateLock)
        {
            if (_installed.Models.Remove(modelId, out var inst))
            {
                try { File.Delete(inst.Path); } catch (Exception ex) { _log.Warning(ex, "Modell löschen"); }
            }
        }
        SaveState();
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void Report(ModelEntry m, long done, long? total, string phase, string? errorId = null, string? message = null) =>
        Progress?.Invoke(this, new ModelProgress(m.Id, m.DisplayName, done, total, phase, errorId, message));
}
