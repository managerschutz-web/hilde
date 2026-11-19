using System.Text.RegularExpressions;
using Hilde.Core;

namespace Hilde.Language;

/// <summary>Exakte Ersetzungsregeln (Wortgrenzen, Groß-/Kleinschreibung) + Fachbegriffe.</summary>
public sealed class DictionaryService(ITranscriptRepository repo) : IDictionaryService
{
    private volatile CompiledRule[] _rules = [];
    private volatile string[] _protected = [];
    private volatile string? _prompt;

    public IReadOnlyList<string> ProtectedTerms => _protected;
    public string? InitialPrompt => _prompt;

    public async Task ReloadAsync()
    {
        var rules = await repo.GetReplacementsAsync().ConfigureAwait(false);
        var terms = await repo.GetTermsAsync().ConfigureAwait(false);
        _rules = rules.Where(r => r.Enabled && r.Pattern.Trim().Length > 0)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .Select(Compile).ToArray();
        _protected = rules.Where(r => r.Enabled).Select(r => r.Replacement)
            .Concat(terms.Select(t => t.Term))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal).ToArray();
        _prompt = BuildPrompt(terms.Select(t => t.Term).Concat(rules.Where(r => r.Enabled).Select(r => r.Replacement)));
    }

    public string Apply(string text) => ApplyRules(text, _rules);

    public sealed record CompiledRule(Regex Regex, string Replacement);

    public static CompiledRule Compile(ReplacementRule rule)
    {
        var body = Regex.Escape(rule.Pattern.Trim()).Replace("\\ ", "\\s+");
        var pattern = rule.WholeWord ? $@"(?<![\p{{L}}\p{{N}}_]){body}(?![\p{{L}}\p{{N}}_])" : body;
        var options = RegexOptions.CultureInvariant | (rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new CompiledRule(new Regex(pattern, options, TimeSpan.FromMilliseconds(250)), rule.Replacement);
    }

    public static string ApplyRules(string text, IEnumerable<CompiledRule> rules)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var r in rules)
        {
            try { text = r.Regex.Replace(text, _ => r.Replacement); }
            catch (RegexMatchTimeoutException) { /* Regel überspringen */ }
        }
        return text;
    }

    /// <summary>Vorschau für die Oberfläche: Ergebnis und Anzahl Treffer.</summary>
    public static (string Result, int Matches) Preview(ReplacementRule rule, string sample)
    {
        var compiled = Compile(rule);
        int count = compiled.Regex.Matches(sample).Count;
        return (ApplyRules(sample, [compiled]), count);
    }

    internal static string? BuildPrompt(IEnumerable<string> terms)
    {
        var list = terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return null;
        var sb = new System.Text.StringBuilder("Fachbegriffe: ");
        foreach (var t in list)
        {
            if (sb.Length + t.Length + 2 > 600) break; // Whisper-Prompt bleibt kurz
            sb.Append(t).Append(", ");
        }
        return sb.ToString().TrimEnd(',', ' ') + ".";
    }
}
