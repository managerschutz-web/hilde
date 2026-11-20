using System.Diagnostics;
using System.Text.Json;
using Hilde.Core;
using Hilde.Core.Engines;
using SherpaOnnx;
using Serilog;

namespace Hilde.EngineHost.Tts;

/// <summary>
/// Sprachausgabe mit Piper-Stimmen (VITS) über sherpa-onnx – vollständig lokal.
/// Das Modell ist ein Ordner mit *.onnx, tokens.txt und espeak-ng-data.
/// </summary>
public sealed class SherpaTtsHandler : IEngineHandler
{
    private readonly string _dir;
    private readonly int _threads;
    private readonly ILogger _log;
    private OfflineTts? _tts;

    public SherpaTtsHandler(string voiceDir, int threads, ILogger log)
    {
        _dir = voiceDir;
        _threads = Math.Clamp(threads, 1, 4);
        _log = log;
    }

    public string EngineVersion => "sherpa-onnx/piper " + typeof(OfflineTts).Assembly.GetName().Version;

    public Task LoadAsync(CancellationToken ct)
    {
        if (_tts != null) return Task.CompletedTask;
        var onnx = Directory.EnumerateFiles(_dir, "*.onnx", SearchOption.AllDirectories).FirstOrDefault()
                   ?? throw new HildeException(HildeErrors.Unexpected, detail: "Stimmmodell (.onnx) fehlt");
        var root = Path.GetDirectoryName(onnx)!;
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = onnx;
        config.Model.Vits.Tokens = Path.Combine(root, "tokens.txt");
        config.Model.Vits.DataDir = Path.Combine(root, "espeak-ng-data");
        config.Model.NumThreads = _threads;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 2;
        _tts = new OfflineTts(config);
        _log.Information("Stimme geladen: {Voice}", Path.GetFileNameWithoutExtension(onnx));
        return Task.CompletedTask;
    }

    public EngineHealth Health() => new(true, _tts != null, "CPU", null);

    public async Task<object?> HandleAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        if (method != EngineProtocol.Methods.Speak) throw new EngineCallException(null, $"Unbekannte Methode {method}");
        var p = parameters!.Value.Deserialize<SpeakParams>(EngineProtocol.Json)!;
        await LoadAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        // Piper: kleineres „Speed“ = langsamer; Nutzerwert 0,5–2,0
        var audio = _tts!.Generate(p.Text, Math.Clamp(p.Speed, 0.5f, 2.0f), 0);
        Directory.CreateDirectory(Path.GetDirectoryName(p.OutPath)!);
        audio.SaveToWaveFile(p.OutPath);
        double seconds = audio.Samples.Length / (double)audio.SampleRate;
        _log.Information("TTS: {Sec:F2}s Audio in {Compute:F2}s", seconds, sw.Elapsed.TotalSeconds);
        return new SpeakResult(audio.SampleRate, seconds, sw.Elapsed.TotalSeconds);
    }

    public void Dispose()
    {
        _tts?.Dispose();
        _tts = null;
    }
}
