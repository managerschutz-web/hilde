using System.Text.RegularExpressions;
using Hilde.Core;

namespace Hilde.Language;

/// <summary>
/// Sicherheitsregeln für die KI-Ausgabe: Nichts darf erfunden, keine Zahl/kein Name/kein Kürzel
/// eigenmächtig verändert werden. Im Zweifel wird die KI-Ausgabe verworfen (Rückfall auf Regeln).
/// </summary>
public static partial class OutputValidator
{
    public sealed record Verdict(bool Ok, string? Reason)
    {
        public static readonly Verdict Pass = new(true, null);
        public static Verdict Fail(string reason) => new(false, reason);
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")] private static partial Regex NumberRx();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+|(?:https?://|www\.)\S+", RegexOptions.IgnoreCase)] private static partial Regex LinkRx();
    [GeneratedRegex(@"(?<![\p{L}])\p{L}*\p{Lu}\p{L}*\p{Lu}\p{L}*(?![\p{L}])")] private static partial Regex AcronymRx();
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex WordRx();
    [GeneratedRegex(@"\b(nein|ne|doch nicht|ich meine|ich mein|korrigiere|korrektur|besser gesagt|beziehungsweise|bzw|sorry|entschuldigung|quatsch|falsch|no|i mean|actually|wait|scratch that|rather)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CorrectionMarkerRx();

    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["null"] = "0", ["eins"] = "1", ["ein"] = "1", ["eine"] = "1", ["einen"] = "1", ["zwei"] = "2", ["drei"] = "3",
        ["vier"] = "4", ["fünf"] = "5", ["sechs"] = "6", ["sieben"] = "7", ["acht"] = "8", ["neun"] = "9",
        ["zehn"] = "10", ["elf"] = "11", ["zwölf"] = "12", ["zwanzig"] = "20", ["dreißig"] = "30", ["hundert"] = "100",
        ["tausend"] = "1000", ["halb"] = "30", ["viertel"] = "15",
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6",
        ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12",
        ["twenty"] = "20", ["thirty"] = "30", ["hundred"] = "100", ["thousand"] = "1000",
    };

    private static readonly string[] MetaPrefixes =
    [
        "hier ist", "hier der", "hier die", "bereinigter text", "bearbeiteter text", "korrigierter text",
        "here is", "here's", "cleaned text", "corrected text", "text:", "ergebnis:"
    ];

    /// <summary>Entfernt Verpackung, die Modelle gern hinzufügen.</summary>
    public static string Normalize(string output, string input)
    {
        var t = output.Replace("<diktat>", "").Replace("</diktat>", "").Trim();
        // „Hier ist der bereinigte Text:“-Zeile entfernen
        var firstBreak = t.IndexOf('\n');
        if (firstBreak > 0)
        {
            var first = t[..firstBreak].Trim().ToLowerInvariant();
            if (first.EndsWith(':') && MetaPrefixes.Any(first.StartsWith)) t = t[(firstBreak + 1)..].Trim();
        }
        // Umschließende Anführungszeichen, wenn die Eingabe keine hatte
        if (t.Length >= 2 && IsQuote(t[0]) && IsQuote(t[^1]) && !(input.Length > 0 && IsQuote(input.TrimStart()[0])))
            t = t[1..^1].Trim();
        return t;
    }

    private static bool IsQuote(char c) => c is '"' or '„' or '“' or '”' or '\'' or '»' or '«';

    public static Verdict Validate(string input, string output, TextMode mode, IReadOnlyList<string> protectedTerms)
    {
        if (string.IsNullOrWhiteSpace(output)) return Verdict.Fail("leer");

        bool hasCorrection = CorrectionMarkerRx().IsMatch(input);

        // 1) Länge plausibel
        double ratio = (double)output.Length / Math.Max(1, input.Length);
        double max = mode == TextMode.Professional ? 1.5 : 1.25;
        if (input.Length > 20 && ratio > max) return Verdict.Fail($"zu lang ({ratio:F2})");
        if (input.Length > 30 && ratio < (hasCorrection ? 0.25 : 0.4)) return Verdict.Fail($"zu kurz ({ratio:F2})");

        // 2) Zahlen: keine neuen; fehlende nur bei Selbstkorrektur
        var inNums = Numbers(input);
        var inWords = WordRx().Matches(input).Select(m => m.Value).ToList();
        var allowedNums = new HashSet<string>(inNums);
        foreach (var w in inWords) if (NumberWords.TryGetValue(w, out var n)) allowedNums.Add(n);
        var outNums = Numbers(output);
        foreach (var n in outNums)
        {
            if (!allowedNums.Contains(n) && !IsComposedOfAllowed(n, allowedNums))
                return Verdict.Fail($"neue Zahl");
        }
        if (!hasCorrection)
        {
            foreach (var n in inNums)
                if (!outNums.Contains(n)) return Verdict.Fail("Zahl entfernt");
        }

        // 3) Geschützte Begriffe, Links/E-Mails, Kürzel unverändert
        foreach (var term in protectedTerms)
        {
            if (term.Length > 0 && input.Contains(term, StringComparison.Ordinal) && !output.Contains(term, StringComparison.Ordinal))
                return Verdict.Fail("geschützter Begriff verändert");
        }
        foreach (Match m in LinkRx().Matches(input))
        {
            var link = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (!output.Contains(link, StringComparison.OrdinalIgnoreCase)) return Verdict.Fail("Link/E-Mail verändert");
        }
        if (!hasCorrection)
        {
            foreach (Match m in AcronymRx().Matches(input))
                if (!output.Contains(m.Value, StringComparison.Ordinal)) return Verdict.Fail("Kürzel verändert");
        }

        // 4) Keine erfundenen Inhalte: Anteil neuer Wörter begrenzen
        var inSet = new HashSet<string>(inWords.Select(w => w.ToLowerInvariant()));
        var outWords = WordRx().Matches(output).Select(m => m.Value).ToList();
        if (outWords.Count > 0)
        {
            int novel = outWords.Count(w => w.Length >= 4 && !inSet.Contains(w.ToLowerInvariant()) && !IsInflectionOfInput(w, inSet));
            double novelRatio = (double)novel / outWords.Count;
            double maxNovel = mode == TextMode.Professional ? 0.35 : 0.12;
            if (outWords.Count >= 4 && novelRatio > maxNovel) return Verdict.Fail($"neue Wörter ({novelRatio:P0})");

            // Neue großgeschriebene Wörter mitten im Satz (mögliche erfundene Namen) im Bereinigt-Modus
            if (mode == TextMode.Clean)
            {
                int newCaps = outWords.Count(w => w.Length >= 3 && char.IsUpper(w[0])
                    && !inSet.Contains(w.ToLowerInvariant()) && !IsInflectionOfInput(w, inSet));
                if (newCaps > 2) return Verdict.Fail("neue Namen/Begriffe");
            }
        }

        // 5) Keine Antwort/Meta-Text
        var lower = output.TrimStart().ToLowerInvariant();
        if (MetaPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)) &&
            !input.TrimStart().ToLowerInvariant().StartsWith(lower[..Math.Min(8, lower.Length)], StringComparison.Ordinal))
            return Verdict.Fail("Meta-Antwort");

        return Verdict.Pass;
    }

    private static HashSet<string> Numbers(string text) =>
        NumberRx().Matches(text).Select(m => m.Value.Replace(".", "").Replace(",", ".")).ToHashSet();

    /// <summary>„10:30“ wird zu 10 und 30 – zusammengesetzte Uhrzeiten etc. zulassen.</summary>
    private static bool IsComposedOfAllowed(string n, HashSet<string> allowed)
    {
        // z. B. „zehn Uhr dreißig“ → 10 und 30 erlaubt; „1030“ wäre unzulässig.
        return n.TrimStart('0') is { Length: > 0 } trimmed && allowed.Contains(trimmed);
    }

    /// <summary>Grobe Flexionstoleranz (Grammatikkorrektur): gleicher Stamm ≥ 5 Zeichen.</summary>
    private static bool IsInflectionOfInput(string word, HashSet<string> inSet)
    {
        var w = word.ToLowerInvariant();
        if (w.Length < 5) return false;
        var stem = w[..Math.Max(4, w.Length - 3)];
        return inSet.Any(i => i.Length >= 4 && (i.StartsWith(stem, StringComparison.Ordinal) || w.StartsWith(i[..Math.Max(4, i.Length - 3)], StringComparison.Ordinal)));
    }
}
