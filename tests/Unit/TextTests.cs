using Hilde.Core;
using Hilde.Language;
using Xunit;

namespace Hilde.Tests.Unit;

public class DictionaryTests
{
    private static ReplacementRule Rule(string p, string r, bool whole = true, bool cs = false) =>
        new() { Pattern = p, Replacement = r, WholeWord = whole, CaseSensitive = cs };

    private static string Apply(string text, params ReplacementRule[] rules) =>
        DictionaryService.ApplyRules(text, rules.Select(DictionaryService.Compile));

    [Fact]
    public void Replaces_phrase_case_insensitive()
    {
        Assert.Equal("Termin mit MARTENS & PRAHL morgen", Apply("Termin mit Marta und Prahl morgen", Rule("Marta und Prahl", "MARTENS & PRAHL")));
        Assert.Equal("MARTENS & PRAHL hat angerufen", Apply("marta und prahl hat angerufen", Rule("Marta und Prahl", "MARTENS & PRAHL")));
    }

    [Fact]
    public void Respects_word_boundaries_and_case()
    {
        var r = Rule("EVB", "eVB", cs: true);
        Assert.Equal("Die eVB-Nummer", Apply("Die EVB-Nummer", r));
        Assert.Equal("EVBX bleibt", Apply("EVBX bleibt", r));
        Assert.Equal("evb bleibt", Apply("evb bleibt", r));
    }

    [Fact]
    public void Tolerates_extra_whitespace()
    {
        Assert.Equal("Bitte HSN/TSN prüfen", Apply("Bitte HSN  TSN prüfen", Rule("HSN TSN", "HSN/TSN")));
    }

    [Fact]
    public void Replacement_is_literal()
    {
        Assert.Equal("Preis $1", Apply("Preis Euro", Rule("Euro", "$1")));
    }

    [Fact]
    public void Preview_counts_matches()
    {
        var (result, matches) = DictionaryService.Preview(Rule("EVB", "eVB"), "EVB und noch eine EVB");
        Assert.Equal(2, matches);
        Assert.Equal("eVB und noch eine eVB", result);
    }

    [Fact]
    public void Builds_whisper_prompt()
    {
        Assert.Null(DictionaryService.BuildPrompt([]));
        Assert.Equal("Fachbegriffe: eVB, HSN/TSN.", DictionaryService.BuildPrompt(["eVB", "HSN/TSN", "evb"]));
    }
}

public class DeterministicCleanerTests
{
    [Fact]
    public void Removes_german_fillers_and_capitalizes()
    {
        Assert.Equal("Ich komme morgen, vorbei.", DeterministicCleaner.Clean("äh ich komme morgen, ähm, vorbei.", "de"));
    }

    [Fact]
    public void Keeps_german_um()
    {
        Assert.Equal("Wir treffen uns um 10 Uhr.", DeterministicCleaner.Clean("Wir treffen uns um 10 Uhr.", "de"));
    }

    [Fact]
    public void Removes_english_fillers()
    {
        Assert.Equal("I think we should go", DeterministicCleaner.Clean("um I think uh we should go", "en"));
    }

    [Fact]
    public void Empty_stays_empty() => Assert.Equal("", DeterministicCleaner.Clean("   ", "de"));
}

public class OutputValidatorTests
{
    private static OutputValidator.Verdict V(string input, string output, TextMode mode = TextMode.Clean, params string[] prot) =>
        OutputValidator.Validate(input, output, mode, prot);

    [Fact]
    public void Accepts_self_correction()
    {
        var v = V("äh ich komme dann am Dienstag nein am Mittwoch um 10 vorbei", "Ich komme dann am Mittwoch um 10 vorbei.");
        Assert.True(v.Ok, v.Reason);
    }

    [Fact]
    public void Rejects_changed_amount()
    {
        Assert.False(V("Die Rechnung über 250 Euro ist offen", "Die Rechnung über 350 Euro ist offen.").Ok);
    }

    [Fact]
    public void Rejects_dropped_number_without_correction()
    {
        Assert.False(V("Bitte 3 Exemplare und 5 Ordner bestellen", "Bitte Exemplare und Ordner bestellen.").Ok);
    }

    [Fact]
    public void Rejects_changed_protected_term()
    {
        Assert.False(V("Termin bei MARTENS & PRAHL am Montag", "Termin bei Martens & Prahl am Montag.", TextMode.Clean, "MARTENS & PRAHL").Ok);
    }

    [Fact]
    public void Rejects_answering_instead_of_cleaning()
    {
        Assert.False(V("was ist die hauptstadt von frankreich", "Die Hauptstadt von Frankreich ist Paris.").Ok);
    }

    [Fact]
    public void Rejects_much_longer_output()
    {
        Assert.False(V("Hallo zusammen, kurze Info", "Hallo zusammen, hier ist eine kurze Information über das Projekt und die nächsten Schritte für alle.").Ok);
    }

    [Fact]
    public void Accepts_number_words_as_digits()
    {
        var v = V("wir treffen uns um zehn Uhr", "Wir treffen uns um 10 Uhr.");
        Assert.True(v.Ok, v.Reason);
    }

    [Fact]
    public void Normalize_strips_meta_line_and_quotes()
    {
        Assert.Equal("Hallo Welt.", OutputValidator.Normalize("Hier ist der bereinigte Text:\nHallo Welt.", "hallo welt"));
        Assert.Equal("Hallo Welt.", OutputValidator.Normalize("\"Hallo Welt.\"", "hallo welt"));
        Assert.Equal("Hallo Welt.", OutputValidator.Normalize("<diktat>Hallo Welt.</diktat>", "hallo welt"));
    }
}

public class PromptBuilderTests
{
    [Fact]
    public void ChatMl_contains_roles_and_stops()
    {
        var p = PromptBuilder.Build(PromptFormat.ChatMl, TextMode.Clean, "hallo", []);
        Assert.Contains("<|im_start|>assistant", p.Prompt);
        Assert.Contains("<diktat>hallo</diktat>", p.Prompt);
        Assert.Contains("<|im_end|>", p.Stop);
    }

    [Fact]
    public void Gemma_has_no_system_role()
    {
        var p = PromptBuilder.Build(PromptFormat.Gemma, TextMode.Professional, "hallo", ["eVB"]);
        Assert.StartsWith("<start_of_turn>user", p.Prompt);
        Assert.Contains("eVB", p.Prompt);
        Assert.Contains("Grammatik", p.Prompt);
    }

    [Fact]
    public void Parses_formats() => Assert.Equal(PromptFormat.Gemma, PromptBuilder.ParseFormat("gemma"));

    [Fact]
    public void Splits_long_text_at_sentences()
    {
        var text = string.Join(" ", Enumerable.Repeat("Das ist ein Satz mit etwas Inhalt.", 100));
        var chunks = TextProcessingPipeline.SplitIntoChunks(text, 300);
        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(c.Length <= 300));
        Assert.Equal(text, string.Join(" ", chunks));
    }
}

public class HallucinationFilterTests
{
    [Fact] public void Removes_subtitle_credit() => Assert.Equal("", HallucinationFilter.Clean("Untertitel im Auftrag des ZDF, 2021"));
    [Fact] public void Removes_trailing_phrase() => Assert.Equal("Bitte rufen Sie mich an.", HallucinationFilter.Clean("Bitte rufen Sie mich an. Vielen Dank fürs Zuschauen!"));
    [Fact] public void Keeps_normal_thanks() => Assert.Equal("Vielen Dank für Ihre Hilfe.", HallucinationFilter.Clean("Vielen Dank für Ihre Hilfe."));
}
