using System.Text;
using System.Text.Json;
using Hilde.Core;
using Hilde.Core.Engines;
using Serilog;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Hilde.Speech;

/// <summary>
/// Läuft ausschließlich im Engine-Prozess. whisper.cpp über Whisper.net (MIT).
/// GPU über Vulkan (herstellerunabhängig), sonst CPU.
/// </summary>
public sealed class WhisperEngineHandler : IEngineHandler
{
    private readonly string _modelPath;
    private readonly bool _useGpu;
    private readonly int _threads;
    private readonly ILogger _log;
    private WhisperFactory? _factory;
    private string? _backend;

    public string EngineVersion => "whisper.cpp/Whisper.net " + typeof(WhisperFactory).Assembly.GetName().Version;

    public WhisperEngineHandler(string modelPath, bool useGpu, int threads, ILogger log)
    {
        _modelPath = modelPath;
        _useGpu = useGpu;
        _threads = threads;
        _log = log;
    }

    public Task LoadAsync(CancellationToken ct)
    {
        if (_factory != null) return Task.CompletedTask;
        RuntimeOptions.RuntimeLibraryOrder = _useGpu
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];
        LogProvider.AddLogger((level, message) =>
        {
            if (message != null && (message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)
                || message.Contains("backend", StringComparison.OrdinalIgnoreCase)
                || message.Contains("GPU", StringComparison.Ordinal)))
                _log.Information("whisper.cpp: {Msg}", message.Trim());
        });
        _factory = WhisperFactory.FromPath(_modelPath);
        _backend = _useGpu ? "Vulkan (Fallback CPU)" : "CPU";
        _log.Information("Whisper geladen ({Backend})", _backend);
        return Task.CompletedTask;
    }

    public EngineHealth Health() => new(true, _factory != null, _backend, null);

    public async Task<object?> HandleAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        if (method != EngineProtocol.Methods.Transcribe)
            throw new EngineCallException(null, $"Unbekannte Methode {method}");
        var p = parameters!.Value.Deserialize<TranscribeParams>(EngineProtocol.Json)!;
        await LoadAsync(ct).ConfigureAwait(false);

        var builder = _factory!.CreateBuilder()
            .WithThreads(_threads)
            .WithNoContext()
            .WithTemperature(0f);
        builder = p.Language is "de" or "en" ? builder.WithLanguage(p.Language) : builder.WithLanguageDetection();
        if (!string.IsNullOrWhiteSpace(p.Prompt)) builder = builder.WithPrompt(p.Prompt);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var processor = builder.Build();
        await using var fs = File.OpenRead(p.WavPath);
        var segments = new List<TimedSegment>();
        var text = new StringBuilder();
        string detected = p.Language is "de" or "en" ? p.Language : "de";
        await foreach (var seg in processor.ProcessAsync(fs, ct).ConfigureAwait(false))
        {
            var t = seg.Text?.Trim() ?? "";
            if (t.Length == 0) continue;
            segments.Add(new TimedSegment(seg.Start, seg.End, t));
            if (text.Length > 0) text.Append(' ');
            text.Append(t);
            if (!string.IsNullOrEmpty(seg.Language)) detected = seg.Language;
        }
        _log.Information("Whisper: {Sec:F2}s für {Segments} Segmente", sw.Elapsed.TotalSeconds, segments.Count);
        return new SpeechResult(text.ToString().Trim(), detected, segments);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
