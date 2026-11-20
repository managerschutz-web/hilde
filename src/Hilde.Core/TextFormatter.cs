using System.Globalization;
using System.Text.RegularExpressions;

namespace Hilde.Core;

public sealed record FormatOptions(bool Amounts = true, bool Dates = true, bool Times = true, bool Codes = true, bool Percent = true);

public sealed record Snippet(long Id, string Name, string Text);

/// <summary>
/// Diktierbefehle, Textbausteine und Formatierung von Zahlen/Daten/Beträgen.
/// Befehle und Bausteine werden vor der KI durch Platzhalter ersetzt (⟦…⟧), damit sie unverändert bleiben.
/// </summary>
public static partial class TextFormatter
{
    public const string Paragraph = "⟦¶⟧";
    public const string LineBreak = "⟦↵⟧";

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    // ---------- Diktierbefehle ----------
    [GeneratedRegex(@"\s*\b(?:neuer|neuen|new)\s+(?:absatz|paragraph)\b[\s.,;:!]*", RegexOptions.IgnoreCase)] private static partial Regex ParagraphCmd();
    [GeneratedRegex(@"\s*\b(?:neue|new)\s+(?:zeile|line)\b[\s.,;:!]*", RegexOptions.IgnoreCase)] private static partial Regex LineCmd();
    [GeneratedRegex(@"[\s,]*\bdoppelpunkt\b[.,]?", RegexOptions.IgnoreCase)] private static partial Regex ColonCmd();
    [GeneratedRegex(@"[ \t]*⟦¶⟧[ \t]*")] private static partial Regex ParagraphMark();
    [GeneratedRegex(@"[ \t]*⟦↵⟧[ \t]*")] private static partial Regex LineMark();
    [GeneratedRegex(@"(\n)(\p{Ll})")] private static partial Regex LowerAfterBreak();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex TooManyBreaks();

    public static string EncodeCommands(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = ParagraphCmd().Replace(text, " " + Paragraph + " ");
        text = LineCmd().Replace(text, " " + LineBreak + " ");
        text = ColonCmd().Replace(text, ":");
        return text.Trim();
    }

    public static string DecodeCommands(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = ParagraphMark().Replace(text, "\n\n");
        text = LineMark().Replace(text, "\n");
        text = TooManyBreaks().Replace(text, "\n\n");
        text = LowerAfterBreak().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value.ToUpper(De));
        return text.TrimStart('\n');
    }

    public static IEnumerable<string> Placeholders(string text)
    {
        foreach (Match m in PlaceholderRx().Matches(text)) yield return m.Value;
    }

    [GeneratedRegex(@"⟦[^⟧]{1,6}⟧")] private static partial Regex PlaceholderRx();

    // ---------- Textbausteine ----------
    /// <summary>Ersetzt „Baustein &lt;Name&gt;“ durch Platzhalter ⟦B1⟧ … und liefert die Zuordnung.</summary>
    public static (string Text, Dictionary<string, string> Map) EncodeSnippets(string text, IReadOnlyList<Snippet> snippets)
    {
        var map = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(text) || snippets.Count == 0) return (text, map);
        int n = 0;
        foreach (var s in snippets.Where(s => s.Name.Trim().Length > 0).OrderByDescending(s => s.Name.Length))
        {
            var name = string.Join(@"[\s-]+", s.Name.Trim().Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            var rx = new Regex($@"\b(?:text)?baustein\s+{name}\b[.,;:!?]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            text = rx.Replace(text, _ =>
            {
                var key = $"⟦B{++n}⟧";
                map[key] = s.Text;
                return key;
            });
        }
        return (text, map);
    }

    public static string DecodeSnippets(string text, IReadOnlyDictionary<string, string> map)
    {
        foreach (var (key, value) in map) text = text.Replace(key, value);
        return text;
    }

    // ---------- Zahlen, Daten, Beträge ----------
    [GeneratedRegex(@"(?<![\d.,])(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d{1,2}))?\s*(?:Euro|EUR|€)(?![\p{L}])")] private static partial Regex AmountRx();
    [GeneratedRegex(@"(?<![\d,])(\d+(?:,\d+)?)\s*(?:Prozent|%)(?![\p{L}])")] private static partial Regex PercentRx();
    [GeneratedRegex(@"\b(\d{1,2})\.\s*(Januar|Jänner|Februar|März|April|Mai|Juni|Juli|August|September|Oktober|November|Dezember)(?:\s+(\d{4}))?\b")] private static partial Regex DateRx();
    [GeneratedRegex(@"\b(\d{1,2})\s*Uhr\s+(\d{1,2})\b(?!\s*(?:Uhr|Minuten))")] private static partial Regex TimeSpokenRx();
    [GeneratedRegex(@"\b(\d{1,2})\.(\d{2})\s*Uhr\b")] private static partial Regex TimeDotRx();
    [GeneratedRegex(@"\bDE\s?(\d{2})((?:\s?\d){18})\b")] private static partial Regex IbanRx();
    [GeneratedRegex(@"\b((?:Kfz-)?Kennzeichen:?\s+)([A-ZÄÖÜ]{1,3})[\s-]+([A-Z]{1,2})[\s-]*(\d{1,4})([EH]?)\b")] private static partial Regex PlateRx();

    private static readonly string[] Months = ["januar", "februar", "märz", "april", "mai", "juni", "juli", "august", "september", "oktober", "november", "dezember"];

    public static string FormatNumbers(string text, FormatOptions o)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (o.Amounts)
        {
            text = AmountRx().Replace(text, m =>
            {
                if (!long.TryParse(m.Groups[1].Value.Replace(".", ""), out var euros)) return m.Value;
                var s = euros.ToString("#,0", De);
                if (m.Groups[2].Success) s += "," + m.Groups[2].Value.PadRight(2, '0');
                return s + " €";
            });
        }
        if (o.Percent) text = PercentRx().Replace(text, "$1 %");
        if (o.Dates)
        {
            text = DateRx().Replace(text, m =>
            {
                var month = Months.Contains(m.Groups[2].Value.ToLowerInvariant())
                    ? Array.IndexOf(Months, m.Groups[2].Value.ToLowerInvariant()) + 1 : 1; // Jänner
                int day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (day is < 1 or > 31) return m.Value;
                return m.Groups[3].Success ? $"{day:00}.{month:00}.{m.Groups[3].Value}" : $"{day:00}.{month:00}.";
            });
        }
        if (o.Times)
        {
            text = TimeSpokenRx().Replace(text, m =>
            {
                int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                return h < 24 && min < 60 ? $"{h}:{min:00} Uhr" : m.Value;
            });
            text = TimeDotRx().Replace(text, m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) < 60 ? $"{m.Groups[1].Value}:{m.Groups[2].Value} Uhr" : m.Value);
        }
        if (o.Codes)
        {
            text = IbanRx().Replace(text, m =>
            {
                var digits = ("DE" + m.Groups[1].Value + m.Groups[2].Value).Replace(" ", "");
                return string.Join(" ", Enumerable.Range(0, (digits.Length + 3) / 4).Select(i => digits.Substring(i * 4, Math.Min(4, digits.Length - i * 4))));
            });
            text = PlateRx().Replace(text, "$1$2-$3 $4$5");
        }
        return text;
    }
}
