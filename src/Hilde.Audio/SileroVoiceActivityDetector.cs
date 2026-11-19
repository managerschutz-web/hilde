using Hilde.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;

namespace Hilde.Audio;

/// <summary>
/// Silero VAD v5 (MIT) über ONNX Runtime. Fällt auf eine Energie-Schwelle zurück,
/// wenn das Modell fehlt oder nicht geladen werden kann – Diktate gehen dadurch nie verloren.
/// </summary>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector, IDisposable
{
    private const int Window = 512;       // 32 ms bei 16 kHz
    private const int Context = 64;
    private readonly Func<string?> _modelPath;
    private readonly ILogger _log;
    private InferenceSession? _session;
    private string? _loadedPath;
    private readonly object _lock = new();

    public float Threshold { get; set; } = 0.5f;

    public SileroVoiceActivityDetector(Func<string?> modelPath, ILogger log)
    {
        _modelPath = modelPath;
        _log = log.ForContext<SileroVoiceActivityDetector>();
    }

    public VadResult Analyze(ReadOnlySpan<float> samples)
    {
        if (samples.Length < Window) return new VadResult(false, TimeSpan.Zero, TimeSpan.Zero, 0);
        try
        {
            var session = GetSession();
            if (session != null) return AnalyzeSilero(session, samples);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Silero VAD fehlgeschlagen – Energie-Fallback");
        }
        return EnergyVad.Analyze(samples);
    }

    private InferenceSession? GetSession()
    {
        var path = _modelPath();
        if (path == null || !File.Exists(path)) return null;
        lock (_lock)
        {
            if (_session != null && _loadedPath == path) return _session;
            _session?.Dispose();
            var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
            _session = new InferenceSession(path, opts);
            _loadedPath = path;
            return _session;
        }
    }

    private VadResult AnalyzeSilero(InferenceSession session, ReadOnlySpan<float> samples)
    {
        var state = new DenseTensor<float>(new[] { 2, 1, 128 });
        // „sr“ ist im Silero-v5-Modell ein Skalar (Rang 0)
        var sr = new DenseTensor<long>(new long[] { WavIO.SampleRate }, Array.Empty<int>());
        var context = new float[Context];
        int windows = samples.Length / Window;
        int speechWindows = 0, first = -1, last = -1;

        lock (_lock)
        {
            for (int w = 0; w < windows; w++)
            {
                var input = new DenseTensor<float>(new[] { 1, Context + Window });
                for (int i = 0; i < Context; i++) input[0, i] = context[i];
                var chunk = samples.Slice(w * Window, Window);
                for (int i = 0; i < Window; i++) input[0, Context + i] = chunk[i];
                chunk[(Window - Context)..].CopyTo(context);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", input),
                    NamedOnnxValue.CreateFromTensor("state", state),
                    NamedOnnxValue.CreateFromTensor("sr", sr),
                };
                using var results = session.Run(inputs);
                float prob = 0;
                foreach (var r in results)
                {
                    if (r.Name == "output") prob = r.AsTensor<float>().GetValue(0);
                    else if (r.Name == "stateN")
                    {
                        var t = r.AsTensor<float>();
                        state = new DenseTensor<float>(t.ToArray(), new[] { 2, 1, 128 });
                    }
                }
                if (prob >= Threshold)
                {
                    speechWindows++;
                    if (first < 0) first = w;
                    last = w;
                }
            }
        }

        // mindestens ~150 ms Sprache
        bool has = speechWindows >= 5;
        double ratio = windows == 0 ? 0 : speechWindows / (double)windows;
        return new VadResult(has,
            TimeSpan.FromSeconds(Math.Max(0, first) * Window / (double)WavIO.SampleRate),
            TimeSpan.FromSeconds((last + 1) * Window / (double)WavIO.SampleRate), ratio);
    }

    public void Dispose()
    {
        lock (_lock) { _session?.Dispose(); _session = null; }
    }
}

/// <summary>Einfache, robuste Energie-Schwelle als Rückfallebene.</summary>
public static class EnergyVad
{
    public static VadResult Analyze(ReadOnlySpan<float> samples)
    {
        const int win = 480; // 30 ms
        int windows = samples.Length / win;
        if (windows == 0) return new VadResult(false, TimeSpan.Zero, TimeSpan.Zero, 0);
        var energies = new double[windows];
        for (int w = 0; w < windows; w++)
        {
            double sum = 0;
            var s = samples.Slice(w * win, win);
            foreach (var v in s) sum += v * v;
            energies[w] = Math.Sqrt(sum / win);
        }
        var sorted = energies.OrderBy(e => e).ToArray();
        double noise = sorted[(int)(sorted.Length * 0.15)];
        double threshold = Math.Max(0.01, noise * 3);
        int speech = 0, first = -1, last = -1;
        for (int w = 0; w < windows; w++)
        {
            if (energies[w] > threshold) { speech++; if (first < 0) first = w; last = w; }
        }
        return new VadResult(speech >= 5, TimeSpan.FromSeconds(Math.Max(0, first) * 0.03),
            TimeSpan.FromSeconds((last + 1) * 0.03), speech / (double)windows);
    }
}
