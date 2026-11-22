using Hilde.Core;
using Xunit;

namespace Hilde.Tests.Unit;

public class AudioGainTests
{
    private static float[] Speechy(float amp, float noise)
    {
        var rnd = new Random(1);
        var s = new float[16000 * 3];
        for (int i = 0; i < s.Length; i++)
        {
            bool talking = (i / 8000) % 2 == 0; // 0,5 s Sprache, 0,5 s Pause
            s[i] = (float)((rnd.NextDouble() * 2 - 1) * noise + (talking ? amp * Math.Sin(i * 0.05) : 0));
        }
        return s;
    }

    [Fact]
    public void QuietSpeechIsAmplified()
    {
        var g = AudioGain.ComputeGain(Speechy(0.02f, 0.001f));
        Assert.InRange(g, 3f, AudioGain.MaxGain);
    }

    [Fact]
    public void NormalSpeechIsLeftAlone() => Assert.Equal(1f, AudioGain.ComputeGain(Speechy(0.3f, 0.002f)));

    [Fact]
    public void PureNoiseIsNotAmplified()
    {
        var rnd = new Random(2);
        var s = Enumerable.Range(0, 16000 * 2).Select(_ => (float)((rnd.NextDouble() * 2 - 1) * 0.003)).ToArray();
        Assert.Equal(1f, AudioGain.ComputeGain(s));
    }

    [Fact]
    public void NeverClips()
    {
        var n = AudioGain.Normalize(Speechy(0.02f, 0.001f));
        Assert.True(n.Max(Math.Abs) <= 1f);
    }
}
