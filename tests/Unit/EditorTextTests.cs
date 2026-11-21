using Hilde.Core;
using Xunit;

namespace Hilde.Tests.Unit;

public class EditorTextTests
{
    [Fact]
    public void EmptyEditorTakesTextAsIs()
    {
        var (t, c) = EditorText.InsertAt("", 0, " guten Morgen. ");
        Assert.Equal("Guten Morgen.", t);
        Assert.Equal(t.Length, c);
    }

    [Fact]
    public void AppendsWithSpaceAfterSentence()
    {
        var (t, c) = EditorText.InsertAt("Hallo Herr Meier.", 17, "danke für Ihre Nachricht.");
        Assert.Equal("Hallo Herr Meier. Danke für Ihre Nachricht.", t);
        Assert.Equal(t.Length, c);
    }

    [Fact]
    public void InsertsMidSentenceWithoutChangingCase()
    {
        var text = "Wir prüfen den Schaden.";
        int caret = text.IndexOf("den", StringComparison.Ordinal) - 1; // nach „prüfen“
        var (t, _) = EditorText.InsertAt(text, caret, "Haus");
        Assert.Equal("Wir prüfen Haus den Schaden.", t);
    }

    [Fact]
    public void NoSpaceBeforePunctuation()
    {
        var (t, _) = EditorText.InsertAt("Das passt", 9, ", oder?");
        Assert.Equal("Das passt, oder?", t);
    }

    [Fact]
    public void NewLineStartsSentence()
    {
        var (t, _) = EditorText.InsertAt("Hallo,\n", 7, "anbei die Unterlagen.");
        Assert.Equal("Hallo,\nAnbei die Unterlagen.", t);
    }
}
