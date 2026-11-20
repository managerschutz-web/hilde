using Hilde.Core;
using Xunit;

namespace Hilde.Tests.Unit;

public class FormatterTests
{
    private static string Cmd(string s) => TextFormatter.DecodeCommands(TextFormatter.EncodeCommands(s));

    [Fact]
    public void Dictation_commands()
    {
        Assert.Equal("Sehr geehrte Damen und Herren,\n\nWir bestätigen den Termin: Montag.",
            Cmd("Sehr geehrte Damen und Herren, neuer Absatz. Wir bestätigen den Termin Doppelpunkt Montag."));
        Assert.Equal("Erste Zeile.\nZweite Zeile.", Cmd("Erste Zeile. Neue Zeile. zweite Zeile."));
    }

    [Theory]
    [InlineData("Die Prämie beträgt 1250 Euro.", "Die Prämie beträgt 1.250 €.")]
    [InlineData("Rest 99,5 Euro", "Rest 99,50 €")]
    [InlineData("insgesamt 1.250 EUR", "insgesamt 1.250 €")]
    [InlineData("5 Prozent Rabatt", "5 % Rabatt")]
    [InlineData("am 3. Mai 2026", "am 03.05.2026")]
    [InlineData("bis 15. Dezember", "bis 15.12.")]
    [InlineData("um 10 Uhr 30", "um 10:30 Uhr")]
    [InlineData("um 10.30 Uhr", "um 10:30 Uhr")]
    [InlineData("IBAN DE89370400440532013000", "IBAN DE89 3704 0044 0532 0130 00")]
    [InlineData("Kennzeichen IGB AB 123", "Kennzeichen IGB-AB 123")]
    [InlineData("Vertrag 12345 läuft", "Vertrag 12345 läuft")]
    public void Formats_numbers(string input, string expected) =>
        Assert.Equal(expected, TextFormatter.FormatNumbers(input, new FormatOptions()));

    [Fact]
    public void Snippets_roundtrip()
    {
        var snippets = new[] { new Snippet(1, "Gruß", "Mit freundlichen Grüßen"), new Snippet(2, "Gruß Kunde", "Viele Grüße\nIhr M&P-Team") };
        var (enc, map) = TextFormatter.EncodeSnippets("Danke. Baustein Gruß Kunde. Und baustein gruß", snippets);
        Assert.DoesNotContain("aust", enc);
        Assert.Equal(2, map.Count);
        Assert.Equal("Danke. Viele Grüße\nIhr M&P-Team Und Mit freundlichen Grüßen", TextFormatter.DecodeSnippets(enc, map));
        Assert.Equal(2, TextFormatter.Placeholders(enc).Count());
    }
}

public class CorrectionLearnerTests
{
    [Fact]
    public void Suggests_replaced_phrase()
    {
        var s = CorrectionLearner.Suggest("Termin mit Marta und Prahl am Montag", "Termin mit MARTENS & PRAHL am Montag", []);
        Assert.Single(s);
        Assert.Equal("Marta und Prahl", s[0].From);
        Assert.Equal("MARTENS & PRAHL", s[0].To);
    }

    [Fact]
    public void Ignores_numbers_and_known_rules()
    {
        Assert.Empty(CorrectionLearner.Suggest("Betrag 250 Euro", "Betrag 350 Euro", []));
        var rules = new[] { new ReplacementRule { Pattern = "EVB", Replacement = "eVB" } };
        Assert.Empty(CorrectionLearner.Suggest("die EVB kommt", "die eVB kommt", rules));
        Assert.Single(CorrectionLearner.Suggest("die EVB kommt", "die eVB kommt", []));
    }
}
