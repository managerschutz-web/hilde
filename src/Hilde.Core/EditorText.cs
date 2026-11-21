namespace Hilde.Core;

/// <summary>Fügt diktierten Text an der Cursorposition im Diktierfenster ein (Leerzeichen, Satzanfang).</summary>
public static class EditorText
{
    private const string NoSpaceBefore = ".,;:!?)]}…%";

    public static (string Text, int Caret) InsertAt(string text, int caret, string insert)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);
        insert = (insert ?? "").Trim();
        if (insert.Length == 0) return (text, caret);
        var before = text[..caret];
        var after = text[caret..];

        var trimmed = before.TrimEnd(' ', '\t');
        bool sentenceStart = trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?' or '\n';
        if (sentenceStart && char.IsLower(insert[0])) insert = char.ToUpper(insert[0]) + insert[1..];

        bool needSpaceBefore = before.Length > 0 && !char.IsWhiteSpace(before[^1]) && before[^1] is not '(' and not '[' and not '„' and not '"'
                               && !NoSpaceBefore.Contains(insert[0]);
        bool needSpaceAfter = after.Length > 0 && !char.IsWhiteSpace(after[0]) && !NoSpaceBefore.Contains(after[0]);

        var piece = (needSpaceBefore ? " " : "") + insert + (needSpaceAfter ? " " : "");
        var result = before + piece + after;
        int newCaret = before.Length + piece.Length - (needSpaceAfter ? 1 : 0);
        return (result, newCaret);
    }
}
