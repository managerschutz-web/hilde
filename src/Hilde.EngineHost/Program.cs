using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Hilde.Core;
using Hilde.Core.Engines;
using Hilde.Language;
using Hilde.Speech;
using Serilog;

namespace Hilde.EngineHost;

/// <summary>
/// Isolierter Engine-Prozess (Pflichtenheft 5.2): ein Absturz hier beendet nie HILDE selbst.
/// Aufruf: Hilde.Engine.exe --kind speech|language --pipe NAME --parent PID --model PFAD --gpu true|false --threads N
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var a = ParseArgs(args);
        if (!a.TryGetValue("kind", out var kind) || !a.TryGetValue("pipe", out var pipeName) || !a.TryGetValue("model", out var model))
        {
            Console.Error.WriteLine("Hilde.Engine: Parameter fehlen.");
            return 2;
        }
        bool gpu = a.GetValueOrDefault("gpu") == "true";
        int threads = int.TryParse(a.GetValueOrDefault("threads"), out var t) && t > 0 ? t : Math.Max(2, Environment.ProcessorCount / 2);
        int parent = int.TryParse(a.GetValueOrDefault("parent"), out var pp) ? pp : 0;

        var paths = new HildePaths();
        Directory.CreateDirectory(paths.Logs);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Engine", kind)
            .WriteTo.File(Path.Combine(paths.Logs, $"engine-{kind}-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, shared: true)
            .CreateLogger();

        using var cts = new CancellationTokenSource();
        if (parent > 0) _ = WatchParentAsync(parent, cts);

        IEngineHandler handler = kind switch
        {
            "speech" => new WhisperEngineHandler(model, gpu, threads, Log.Logger),
            "language" => new LlamaEngineHandler(model, gpu, threads, Log.Logger),
            "tts" => new Hilde.EngineHost.Tts.SherpaTtsHandler(model, threads, Log.Logger),
            _ => throw new ArgumentException("Unbekannte Engine: " + kind)
        };

        try
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cts.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Log.Information("Engine {Kind} verbunden (PID {Pid}, GPU {Gpu}, Threads {Threads})", kind, Environment.ProcessId, gpu, threads);

            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line == null) break; // HILDE hat die Verbindung geschlossen
                EngineRequest? req;
                try { req = JsonSerializer.Deserialize<EngineRequest>(line, EngineProtocol.Json); }
                catch (JsonException) { continue; }
                if (req == null) continue;

                EngineResponse resp;
                try
                {
                    object? result = req.Method switch
                    {
                        EngineProtocol.Methods.Hello => new HelloResult(EngineProtocol.Version, kind, handler.EngineVersion, Environment.ProcessId),
                        EngineProtocol.Methods.Health => handler.Health(),
                        EngineProtocol.Methods.Load => await LoadAsync(handler, cts.Token),
                        EngineProtocol.Methods.Shutdown => null,
                        _ => await handler.HandleAsync(req.Method, req.Params, cts.Token)
                    };
                    resp = new EngineResponse(req.Id, true,
                        result == null ? null : JsonSerializer.SerializeToElement(result, result.GetType(), EngineProtocol.Json), null, null);
                }
                catch (EngineCallException ex)
                {
                    resp = new EngineResponse(req.Id, false, null, ex.ErrorId, ex.Message);
                }
                catch (HildeException ex)
                {
                    resp = new EngineResponse(req.Id, false, null, ex.Error.Id, ex.Error.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error(ex, "Engine-Fehler bei {Method}", req.Method);
                    resp = new EngineResponse(req.Id, false, null, HildeErrors.Unexpected.Id, ex.GetType().Name + ": " + ex.Message);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp, EngineProtocol.Json));
                if (req.Method == EngineProtocol.Methods.Shutdown) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { Log.Information("Pipe geschlossen: {Msg}", ex.Message); }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Engine {Kind} abgestürzt", kind);
            return 1;
        }
        finally
        {
            handler.Dispose();
            Log.Information("Engine {Kind} beendet", kind);
            await Log.CloseAndFlushAsync();
        }
        return 0;
    }

    private static async Task<object?> LoadAsync(IEngineHandler handler, CancellationToken ct)
    {
        await handler.LoadAsync(ct);
        return handler.Health();
    }

    /// <summary>Beendet sich, sobald HILDE nicht mehr läuft (keine verwaisten Prozesse).</summary>
    private static async Task WatchParentAsync(int pid, CancellationTokenSource cts)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            await parent.WaitForExitAsync(cts.Token);
        }
        catch (ArgumentException) { /* Prozess existiert schon nicht mehr */ }
        catch (OperationCanceledException) { return; }
        Log.Information("HILDE beendet – Engine fährt herunter");
        cts.Cancel();
        await Task.Delay(3000);
        Environment.Exit(0);
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) d[args[i][2..]] = args[++i];
        }
        return d;
    }
}
