using System.Text.RegularExpressions;

namespace Hilde.Core;

/// <summary>
/// Entfernt typische Whisper-Halluzinationen (Untertitel-Floskeln bei Stille).
/// Wird nur auf ganze Sätze angewendet, die exakt einer bekannten Floskel entsprechen.
/// </summary>
public static partial class HallucinationFilter
{
    private static readonly string[] Phrases =
    [
        "Untertitel im Auftrag des ZDF",
        "Untertitel im Auftrag des ZDF für funk",
        "Untertitel von Stephanie Geiges",
        "Untertitelung des ZDF",
        "Untertitel der Amara.org-Community",
        "Vielen Dank fürs Zuschauen",
        "Vielen Dank für's Zuschauen",
        "Danke fürs Zuschauen",
        "Bis zum nächsten Mal",
        "Thanks for watching",
        "Thank you for watching",
        "Subtitles by the Amara.org community",
        "Copyright WDR",
        "SWR",
        "[Musik]",
        "(Musik)",
        "[Music]",
        "*Musik*",
    ];

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    public static string Clean(string text, VadResult? vad = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sentences = SentenceSplit().Split(text.Trim());
        var kept = new List<string>(sentences.Length);
        foreach (var sentence in sentences)
        {
            var core = sentence.Trim().TrimEnd('.', '!', '?', ' ').Trim();
            bool isPhrase = Phrases.Any(p => string.Equals(core, p.TrimEnd('.', '!'), StringComparison.OrdinalIgnoreCase))
                            || core.StartsWith("Untertitel im Auftrag", StringComparison.OrdinalIgnoreCase)
                            || core.StartsWith("Untertitel von ", StringComparison.OrdinalIgnoreCase);
            if (!isPhrase) kept.Add(sentence.Trim());
        }
        var result = MultiSpace().Replace(string.Join(" ", kept), " ").Trim();

        // Bei sehr wenig Sprachanteil sind einzelne Floskelwörter fast immer Halluzination.
        if (vad is { SpeechRatio: < 0.05 } && result.Length < 25) return "";
        return result;
    }
}
