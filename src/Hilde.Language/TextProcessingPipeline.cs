using System.Text;
using System.Text.RegularExpressions;
using Hilde.Core;
using Hilde.Core.Engines;
using Serilog;

namespace Hilde.Language;

/// <summary>
/// ITextProcessingEngine für HILDE: Regelbereinigung immer, KI-Bereinigung optional im isolierten
/// llama.cpp-Prozess, jede KI-Ausgabe wird geprüft. Wirft nie – im schlimmsten Fall Regeltext.
/// </summary>
public sealed partial class TextProcessingPipeline : ITextProcessingEngine
{
    private readonly EngineProcessClient _client;
    private readonly Func<PromptFormat> _format;
    private readonly ILogger _log;

    public TextProcessingPipeline(EngineProcessClient client, Func<PromptFormat> format, ILogger log)
    {
        _client = client;
        _format = format;
        _log = log.ForContext<TextProcessingPipeline>();
    }

    public EngineProcessClient Client => _client;
    public bool IsAvailable => _client.HasModel;

    public async Task<TextResult> ProcessAsync(TextRequest request, CancellationToken ct)
    {
        var baseline = DeterministicCleaner.Clean(request.Text, request.Language);
        if (request.Mode == TextMode.Original) return new TextResult(request.Text, false, null);
        if (!IsAvailable) return new TextResult(baseline, false, "Kein KI-Modell eingerichtet");

        var chunks = SplitIntoChunks(request.Text, 1200);
        var output = new StringBuilder();
        string? fallback = null;
        bool usedModel = false;
        foreach (var chunk in chunks)
        {
            var (text, ok, reason) = await ProcessChunkAsync(chunk, request, ct).ConfigureAwait(false);
            if (output.Length > 0) output.Append(' ');
            output.Append(text);
            usedModel |= ok;
            if (!ok) fallback ??= reason;
        }
        return new TextResult(output.ToString().Trim(), usedModel, fallback);
    }

    private async Task<(string Text, bool Ok, string? Reason)> ProcessChunkAsync(string chunk, TextRequest req, CancellationToken ct)
    {
        var baseline = DeterministicCleaner.Clean(chunk, req.Language);
        if (chunk.Trim().Length < 3) return (baseline, false, null);
        var prompt = PromptBuilder.Build(_format(), req.Mode, chunk, req.ProtectedTerms);
        var timeout = TimeSpan.FromSeconds(20 + prompt.MaxTokens / 4.0);
        try
        {
            using var gate = await _client.Gate.EnterAsync(highPriority: true, ct).ConfigureAwait(false);
            var result = await _client.CallAsync<GenerateResult>(EngineProtocol.Methods.Generate,
                new GenerateParams(prompt.Prompt, prompt.MaxTokens, prompt.Stop, 0.1f), timeout, ct).ConfigureAwait(false);
            var normalized = OutputValidator.Normalize(result.Text, chunk);
            var verdict = OutputValidator.Validate(chunk, normalized, req.Mode, req.ProtectedTerms);
            if (verdict.Ok)
            {
                _log.Debug("KI-Bereinigung ok ({Tokens} Tokens, {Sec:F2}s)", result.Tokens, result.Seconds);
                return (normalized, true, null);
            }
            _log.Information("{ErrorId}: KI-Ausgabe verworfen – {Reason}", HildeErrors.LlmRejected.Id, verdict.Reason);
            return (baseline, false, "KI-Ausgabe verworfen: " + verdict.Reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HildeException ex)
        {
            return (baseline, false, ex.Error.Message);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "{ErrorId}: KI-Bereinigung ausgefallen", HildeErrors.LlmFailed.Id);
            return (baseline, false, "KI-Bereinigung ausgefallen");
        }
    }

    [GeneratedRegex(@"(?<=[.!?…])\s+")] private static partial Regex SentenceBoundary();

    internal static List<string> SplitIntoChunks(string text, int maxChars)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in SentenceBoundary().Split(text))
        {
            if (current.Length > 0 && current.Length + sentence.Length + 1 > maxChars)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(sentence);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    public Task<EngineHealth> HealthAsync(CancellationToken ct) => _client.HealthAsync(ct);
    public Task UnloadAsync() => _client.StopAsync();
}
