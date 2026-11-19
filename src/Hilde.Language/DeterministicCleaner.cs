using System.Text.RegularExpressions;

namespace Hilde.Language;

/// <summary>
/// Regelbasierte Bereinigung ohne KI – immer verfügbar und Rückfallebene, wenn die KI ausfällt
/// oder ihr Ergebnis verworfen wird. Ändert keine Inhalte, nur Füllwörter und Leerzeichen.
/// </summary>
public static partial class DeterministicCleaner
{
    private static readonly string[] GermanFillers = ["ähm", "äh", "öhm", "öh", "ähem", "hmm", "hm", "ehm"];
    private static readonly string[] EnglishFillers = ["uhm", "uh", "um", "erm", "hmm", "hm"];

    private static Regex BuildFillerRegex(IEnumerable<string> fillers)
    {
        var alt = string.Join("|", fillers.OrderByDescending(f => f.Length).Select(Regex.Escape));
        // Füllwort samt angrenzendem Komma/Punkten/Gedankenstrich entfernen
        return new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{alt})(?![\p{{L}}\p{{N}}])(?:\s*[,…]|\.\.\.)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static readonly Regex GermanFillerRegex = BuildFillerRegex(GermanFillers);
    private static readonly Regex EnglishFillerRegex = BuildFillerRegex(EnglishFillers);

    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"\s+([,.;:!?])")] private static partial Regex SpaceBeforePunct();
    [GeneratedRegex(@"([,;])\1+")] private static partial Regex DoublePunct();
    [GeneratedRegex(@"^[\s,;]+")] private static partial Regex LeadingJunk();
    [GeneratedRegex(@"([.!?])\s*,")] private static partial Regex PunctComma();
    [GeneratedRegex(@"(^|[.!?]\s+)(\p{Ll})")] private static partial Regex SentenceStart();

    public static string Clean(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text;
        t = GermanFillerRegex.Replace(t, "");
        if (!language.StartsWith("de", StringComparison.OrdinalIgnoreCase))
            t = EnglishFillerRegex.Replace(t, "");
        t = MultiSpace().Replace(t, " ");
        t = SpaceBeforePunct().Replace(t, "$1");
        t = DoublePunct().Replace(t, "$1");
        t = PunctComma().Replace(t, "$1");
        t = LeadingJunk().Replace(t, "");
        t = t.Trim();
        // Satzanfänge groß (nur nach Satzende, nie mitten im Satz)
        t = SentenceStart().Replace(t, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
        return t;
    }
}
