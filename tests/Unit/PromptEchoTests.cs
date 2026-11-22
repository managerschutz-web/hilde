using Hilde.Core;
using Xunit;

namespace Hilde.Tests.Unit;

public class PromptEchoTests
{
    private const string Prompt = "Fachbegriffe: eVB, HSN/TSN, MARTENS & PRAHL.";

    [Theory]
    [InlineData("EV, HSN/TSN, MARTENS & PRAHL. Bitte rufen Sie mich morgen zurück.", "Bitte rufen Sie mich morgen zurück.")]
    [InlineData("Bitte rufen Sie mich morgen zurück. eVB, HSN/TSN, MARTENS & PRAHL.", "Bitte rufen Sie mich morgen zurück.")]
    [InlineData("Fachbegriffe: eVB, HSN/TSN, MARTENS & PRAHL.", "")]
    [InlineData("HSN/TSN, MARTENS & PRAHL", "")]
    public void RemovesEchoedTermList(string input, string expected) =>
        Assert.Equal(expected, PromptEcho.Strip(input, Prompt));

    [Theory]
    [InlineData("Die eVB und HSN/TSN schicke ich Ihnen heute noch.")]
    [InlineData("Viele Grüße von MARTENS & PRAHL.")]
    [InlineData("Ich brauche die eVB für den Wagen.")]
    public void KeepsRealSpeech(string input) =>
        Assert.Equal(input, PromptEcho.Strip(input, Prompt));

    [Fact]
    public void TermsAreParsed() =>
        Assert.Equal(["eVB", "HSN/TSN", "MARTENS & PRAHL"], PromptEcho.TermsOf(Prompt));
}
