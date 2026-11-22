using System.Text.RegularExpressions;

namespace Hilde.Core;

/// <summary>
/// Whisper schreibt bei kurzen oder leisen Aufnahmen gern den Hinweistext (Fachbegriffe aus dem Wörterbuch)
/// mit ab – z. B. „eVB, HSN/TSN, MARTENS &amp; PRAHL“, obwohl das niemand gesagt hat. Solche Listen werden entfernt.
/// </summary>
public static class PromptEcho
{
    private const string Prefix = "Fachbegriffe:";

    public static IReadOnlyList<string> TermsOf(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return [];
        var p = prompt.Trim();
        if (p.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) p = p[Prefix.Length..];
        return p.TrimEnd('.').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0).ToList();
    }

    public static string Strip(string text, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var terms = TermsOf(prompt);
        var result = Regex.Replace(text, @"Fachbegriffe\s*:", " ", RegexOptions.IgnoreCase);
        if (terms.Count > 0)
        {
            // Ein „Eintrag“ ist ein Begriff aus dem Hinweis oder ein kurzes Kürzel (Whisper verstümmelt gern: eVB → EV).
            var alt = string.Join("|", terms.OrderByDescending(t => t.Length).Select(Regex.Escape));
            var item = $@"(?:{alt}|(?-i:\b[A-ZÄÖÜ]{{2,4}}\b))";
            var run = new Regex($@"(?<![\p{{L}}\p{{N}}]){item}(?:[\s,;:.]+{item})+[\s,;:.]*", RegexOptions.IgnoreCase);
            var termRegex = new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{alt})(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
            var trimmed = result.Trim();
            result = run.Replace(trimmed, m =>
            {
                int realTerms = termRegex.Matches(m.Value).Count;
                bool atEdge = m.Index == 0 || m.Index + m.Length >= trimmed.Length;
                bool listy = m.Value.Contains(',');
                // Nur echte Begriffslisten: mind. 3 Begriffe, oder 2 als Liste am Anfang/Ende des Diktats
                return (realTerms >= 3 && listy) || (realTerms >= 2 && listy && atEdge) ? " " : m.Value;
            });
        }
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        result = Regex.Replace(result, @"^[\s,;:.]+", "");
        return result;
    }
}
