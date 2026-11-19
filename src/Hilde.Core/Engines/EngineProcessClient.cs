using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Hilde.Core.Engines;

public sealed class EngineCrashedException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class EngineCallException(string? errorId, string message) : Exception(message)
{
    public string? ErrorId { get; } = errorId;
}

/// <summary>
/// Startet, überwacht und spricht mit einem isolierten Engine-Prozess (Hilde.Engine.exe).
/// Pflichten: Start, Stop, Healthcheck, Abbruch, Timeout, Neustart, Entladen bei Inaktivität,
/// CPU-Fallback nach GPU-Absturz. Ein Absturz der Engine beendet niemals HILDE selbst.
/// </summary>
public sealed class EngineProcessClient : IAsyncDisposable
{
    private readonly EngineKind _kind;
    private readonly string _hostExe;
    private readonly Func<EngineLaunchOptions?> _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private readonly Timer _idleTimer;
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _nextId;
    private DateTime _lastUse = DateTime.UtcNow;
    private EngineLaunchOptions? _runningWith;
    private bool _forceCpu;
    private int _consecutiveCrashes;

    public TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(5);
    public PriorityGate Gate { get; } = new();
    public bool IsRunning => _process is { HasExited: false };
    public bool GpuDisabledAfterCrash => _forceCpu;
    public string Kind => _kind.ToString().ToLowerInvariant();

    public EngineProcessClient(EngineKind kind, string hostExe, Func<EngineLaunchOptions?> options, ILogger log)
    {
        _kind = kind;
        _hostExe = hostExe;
        _options = options;
        _log = log.ForContext("Engine", kind.ToString());
        _idleTimer = new Timer(_ => _ = CheckIdleAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public bool HasModel => _options() is { } o && File.Exists(o.ModelPath);

    public async Task<T> CallAsync<T>(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastUse = DateTime.UtcNow;
            await EnsureStartedAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await SendAsync(method, parameters, timeout, ct).ConfigureAwait(false);
                _consecutiveCrashes = 0;
                return result.HasValue
                    ? result.Value.Deserialize<T>(EngineProtocol.Json)!
                    : default!;
            }
            catch (Exception ex) when (ex is EngineCrashedException or IOException or TimeoutException or OperationCanceledException)
            {
                bool wasGpu = _runningWith?.UseGpu == true;
                await KillAsync(ex is OperationCanceledException ? "Abbruch" : ex.GetType().Name).ConfigureAwait(false);
                if (ex is EngineCrashedException or IOException)
                {
                    _consecutiveCrashes++;
                    if (wasGpu && !_forceCpu)
                    {
                        _forceCpu = true;
                        _log.Warning("Engine nach GPU-Absturz – künftig CPU-Fallback");
                    }
                }
                throw;
            }
            finally { _lastUse = DateTime.UtcNow; }
        }
        finally { _callLock.Release(); }
    }

    public async Task<EngineHealth> HealthAsync(CancellationToken ct)
    {
        if (!IsRunning) return new EngineHealth(false, false, null, HasModel ? "bereit (nicht geladen)" : "Modell fehlt");
        try
        {
            return await CallAsync<EngineHealth>(EngineProtocol.Methods.Health, null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new EngineHealth(false, false, null, ex.Message);
        }
    }

    /// <summary>Lädt das Modell vorab (z. B. beim Drücken der Taste), damit das Diktat schneller ist.</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        if (!HasModel) return;
        try
        {
            await CallAsync<object>(EngineProtocol.Methods.Load, null, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Debug(ex, "WarmUp"); }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        var opts = _options() ?? throw new HildeException(
            _kind == EngineKind.Speech ? HildeErrors.WhisperModelMissing : HildeErrors.LlmModelMissing);
        if (!File.Exists(opts.ModelPath))
            throw new HildeException(_kind == EngineKind.Speech ? HildeErrors.WhisperModelMissing : HildeErrors.LlmModelMissing);
        if (_forceCpu) opts = opts with { UseGpu = false };

        if (IsRunning && _runningWith == opts && _pipe is { IsConnected: true }) return;
        if (IsRunning) await KillAsync("Konfiguration geändert").ConfigureAwait(false);
        if (_consecutiveCrashes >= 3)
        {
            // Nicht endlos neu starten; nach 3 Abstürzen kurze Pause erzwingen.
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        if (!File.Exists(_hostExe)) throw new HildeException(HildeErrors.EngineStartFailed, detail: "Hilde.Engine.exe fehlt");

        var pipeName = $"hilde-{Kind}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var psi = new ProcessStartInfo(_hostExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = false,
            WorkingDirectory = Path.GetDirectoryName(_hostExe)!,
        };
        psi.ArgumentList.Add("--kind"); psi.ArgumentList.Add(Kind);
        psi.ArgumentList.Add("--pipe"); psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--parent"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(opts.ModelPath);
        psi.ArgumentList.Add("--gpu"); psi.ArgumentList.Add(opts.UseGpu ? "true" : "false");
        psi.ArgumentList.Add("--threads"); psi.ArgumentList.Add(opts.Threads.ToString());

        _log.Information("Starte Engine {Kind} (GPU {Gpu})", Kind, opts.UseGpu);
        var p = Process.Start(psi) ?? throw new HildeException(HildeErrors.EngineStartFailed);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* optional */ }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { p.Kill(true); } catch { }
                pipe.Dispose();
                throw new HildeException(HildeErrors.EngineStartFailed, ex);
            }
        }
        _process = p;
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _runningWith = opts;

        var hello = await SendAsync(EngineProtocol.Methods.Hello, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var h = hello!.Value.Deserialize<HelloResult>(EngineProtocol.Json)!;
        if (h.ProtocolVersion != EngineProtocol.Version)
        {
            await KillAsync("Protokollversion").ConfigureAwait(false);
            throw new HildeException(HildeErrors.EngineStartFailed, detail: $"Protokoll {h.ProtocolVersion} ≠ {EngineProtocol.Version}");
        }
    }

    private async Task<JsonElement?> SendAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        JsonElement? p = parameters == null ? null : JsonSerializer.SerializeToElement(parameters, EngineProtocol.Json);
        var line = JsonSerializer.Serialize(new EngineRequest(id, method, p), EngineProtocol.Json);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _writer!.WriteLineAsync(line.AsMemory(), cts.Token).ConfigureAwait(false);
            while (true)
            {
                var respLine = await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (respLine == null)
                    throw new EngineCrashedException($"Engine {Kind} beendet (Exit {SafeExitCode()})");
                var resp = JsonSerializer.Deserialize<EngineResponse>(respLine, EngineProtocol.Json)!;
                if (resp.Id != id) continue; // verspätete Antwort eines abgebrochenen Aufrufs
                if (!resp.Ok) throw new EngineCallException(resp.ErrorId, resp.Error ?? "Engine-Fehler");
                return resp.Result;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Engine {Kind}: Zeitüberschreitung bei {method}");
        }
    }

    private string SafeExitCode()
    {
        try { return _process is { HasExited: true } ? _process.ExitCode.ToString() : "?"; } catch { return "?"; }
    }

    private async Task CheckIdleAsync()
    {
        if (!IsRunning || DateTime.UtcNow - _lastUse < IdleUnload) return;
        if (!await _callLock.WaitAsync(0).ConfigureAwait(false)) return; // läuft gerade etwas
        try
        {
            if (DateTime.UtcNow - _lastUse < IdleUnload) return;
            _log.Information("Engine {Kind} nach {Min} min Inaktivität entladen", Kind, IdleUnload.TotalMinutes);
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally { _callLock.Release(); }
    }

    /// <summary>Sauber beenden (Modell freigeben).</summary>
    public async Task StopAsync()
    {
        await _callLock.WaitAsync().ConfigureAwait(false);
        try { await StopInternalAsync().ConfigureAwait(false); }
        finally { _callLock.Release(); }
    }

    private async Task StopInternalAsync()
    {
        if (!IsRunning) { await KillAsync(null).ConfigureAwait(false); return; }
        try
        {
            await SendAsync(EngineProtocol.Methods.Shutdown, null, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            if (_process != null && !_process.WaitForExit(3000)) _process.Kill(true);
        }
        catch { /* Kill folgt */ }
        await KillAsync(null).ConfigureAwait(false);
    }

    private Task KillAsync(string? reason)
    {
        if (reason != null) _log.Warning("Engine {Kind} wird beendet: {Reason}", Kind, reason);
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _process?.Dispose();
        _process = null; _pipe = null; _reader = null; _writer = null; _runningWith = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _idleTimer.DisposeAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        _callLock.Dispose();
    }
}
