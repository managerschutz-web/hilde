using System.Text.RegularExpressions;

namespace Hilde.Core;

public sealed record RuleSuggestion(string From, string To);

/// <summary>
/// „Lernen aus Korrekturen“: vergleicht den Text vor und nach einer Bearbeitung im Archiv und schlägt
/// Ersetzungsregeln für geänderte Wörter/Wortgruppen vor (der Nutzer entscheidet).
/// </summary>
public static partial class CorrectionLearner
{
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[./'’-][\p{L}\p{N}]+)*|[&+]")] private static partial Regex TokenRx();
    [GeneratedRegex(@"^\d[\d.,]*$")] private static partial Regex NumberRx();

    public static IReadOnlyList<RuleSuggestion> Suggest(string before, string after, IEnumerable<ReplacementRule> existing, int max = 3)
    {
        var a = TokenRx().Matches(before).Select(m => m.Value).Take(1500).ToArray();
        var b = TokenRx().Matches(after).Select(m => m.Value).Take(1500).ToArray();
        var known = new HashSet<string>(existing.Select(r => r.Pattern.Trim()), StringComparer.OrdinalIgnoreCase);

        // LCS-Tabelle
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var result = new List<RuleSuggestion>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { x++; y++; continue; }
            int x0 = x, y0 = y;
            while (x < a.Length || y < b.Length)
            {
                if (x < a.Length && y < b.Length && a[x] == b[y]) break;
                if (y >= b.Length || (x < a.Length && dp[x + 1, y] >= dp[x, y + 1])) x++;
                else y++;
            }
            int del = x - x0, ins = y - y0;
            if (del is >= 1 and <= 4 && ins is >= 1 and <= 4)
            {
                var from = string.Join(" ", a[x0..x]);
                var to = string.Join(" ", b[y0..y]);
                bool numeric = a[x0..x].All(t => NumberRx().IsMatch(t)) || b[y0..y].All(t => NumberRx().IsMatch(t));
                if (!numeric && from != to && !known.Contains(from) && from.Length >= 2
                    && !result.Any(r => r.From.Equals(from, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new RuleSuggestion(from, to));
            }
            if (result.Count >= max) break;
        }
        return result;
    }
}
