using Hilde.Core;
using Hilde.Speech;
using Xunit;

namespace Hilde.Tests.Unit;

public class LivePreviewTests
{
    private static float[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(seconds * 16000)).Select(i => (float)Math.Sin(i * 0.12) * 0.3f).ToArray();

    private static float[] Silence(double seconds) => new float[(int)(seconds * 16000)];

    [Fact]
    public void Cuts_in_speech_pause()
    {
        var samples = Tone(4).Concat(Silence(0.5)).Concat(Tone(2)).ToArray();
        var cut = LivePreviewSession.FindPause(samples, minOffset: 3 * 16000, forceAfter: 22 * 16000);
        Assert.NotNull(cut);
        Assert.InRange(cut!.Value / 16000.0, 4.0, 4.5);
    }

    [Fact]
    public void Ignores_short_gaps_and_early_pauses()
    {
        Assert.Null(LivePreviewSession.FindPause(Tone(2).Concat(Silence(0.6)).Concat(Tone(2)).ToArray(), 3 * 16000, 22 * 16000));
        Assert.Null(LivePreviewSession.FindPause(Tone(4).Concat(Silence(0.15)).Concat(Tone(2)).ToArray(), 3 * 16000, 22 * 16000));
    }

    [Fact]
    public void Forces_cut_in_long_monologue()
    {
        var cut = LivePreviewSession.FindPause(Tone(24), 3 * 16000, 22 * 16000);
        Assert.NotNull(cut);
    }

    [Fact]
    public void Detects_energy()
    {
        Assert.False(LivePreviewSession.HasEnergy(Silence(2)));
        Assert.True(LivePreviewSession.HasEnergy(Tone(1)));
    }

    [Fact]
    public void Builds_prompt_with_tail()
    {
        Assert.Null(LivePreviewSession.BuildPrompt(null, ""));
        Assert.Equal("Fachbegriffe: eVB. Hallo Welt.", LivePreviewSession.BuildPrompt("Fachbegriffe: eVB.", "Hallo Welt."));
        Assert.Equal(200 + "Fachbegriffe: eVB. ".Length, LivePreviewSession.BuildPrompt("Fachbegriffe: eVB.", new string('a', 500))!.Length);
    }

    [Theory]
    [InlineData(5, true, 448)]
    [InlineData(20, true, 1152)]
    [InlineData(30, true, 1500)]
    [InlineData(5, false, 1500)]
    public void Audio_context_shrinks_for_short_audio(double seconds, bool fast, int expected) =>
        Assert.Equal(expected, WhisperEngineHandler.AudioContextFor(seconds, fast));
}
