using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hilde.Core;
using Hilde.Core.Engines;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Serilog;

namespace Hilde.Language;

/// <summary>Läuft ausschließlich im Engine-Prozess. llama.cpp über LLamaSharp (MIT).</summary>
public sealed class LlamaEngineHandler : IEngineHandler
{
    private readonly string _modelPath;
    private readonly bool _useGpu;
    private readonly int _threads;
    private readonly ILogger _log;
    private LLamaWeights? _weights;
    private ModelParams? _params;
    private StatelessExecutor? _executor;
    private static bool _nativeConfigured;

    public string EngineVersion => "llama.cpp/LLamaSharp " + typeof(LLamaWeights).Assembly.GetName().Version;

    public LlamaEngineHandler(string modelPath, bool useGpu, int threads, ILogger log)
    {
        _modelPath = modelPath;
        _useGpu = useGpu;
        _threads = threads;
        _log = log;
    }

    public Task LoadAsync(CancellationToken ct)
    {
        if (_executor != null) return Task.CompletedTask;
        if (!_nativeConfigured)
        {
            // Muss vor dem ersten nativen Aufruf passieren.
            NativeLibraryConfig.All.WithVulkan(_useGpu).WithAutoFallback(true)
                .WithLogCallback((level, message) =>
                {
                    // Nur Geräte-/Offload-Zeilen festhalten (Diagnose GPU ja/nein), keine Inhalte.
                    if (message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("offload", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("ggml_vulkan", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("CPU_REPACK", StringComparison.OrdinalIgnoreCase))
                        _log.Information("llama.cpp: {Msg}", message.Trim());
                });
            _nativeConfigured = true;
        }
        _params = new ModelParams(_modelPath)
        {
            ContextSize = 4096,
            GpuLayerCount = _useGpu ? 99 : 0,
            Threads = _threads,
        };
        _weights = LLamaWeights.LoadFromFile(_params);
        _executor = new StatelessExecutor(_weights, _params);
        _log.Information("LLM geladen (GPU {Gpu})", _useGpu);
        return Task.CompletedTask;
    }

    public EngineHealth Health() => new(true, _executor != null, _useGpu ? "Vulkan (Fallback CPU)" : "CPU", null);

    public async Task<object?> HandleAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        if (method != EngineProtocol.Methods.Generate)
            throw new EngineCallException(null, $"Unbekannte Methode {method}");
        var p = parameters!.Value.Deserialize<GenerateParams>(EngineProtocol.Json)!;
        await LoadAsync(ct).ConfigureAwait(false);

        var inference = new InferenceParams
        {
            MaxTokens = p.MaxTokens,
            AntiPrompts = p.Stop.ToList(),
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = p.Temperature, Seed = 42 }
        };
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        int tokens = 0;
        await foreach (var piece in _executor!.InferAsync(p.Prompt, inference, ct).ConfigureAwait(false))
        {
            sb.Append(piece);
            tokens++;
        }
        var text = sb.ToString();
        foreach (var stop in p.Stop)
        {
            var idx = text.IndexOf(stop, StringComparison.Ordinal);
            if (idx >= 0) text = text[..idx];
        }
        _log.Information("LLM: {Tokens} Tokens in {Sec:F2}s (Prompt {PromptChars} Zeichen)", tokens, sw.Elapsed.TotalSeconds, p.Prompt.Length);
        return new GenerateResult(text.Trim(), tokens, sw.Elapsed.TotalSeconds);
    }

    public void Dispose()
    {
        _weights?.Dispose();
        _weights = null;
        _executor = null;
    }
}
