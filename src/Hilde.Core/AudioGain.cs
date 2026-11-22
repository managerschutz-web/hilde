namespace Hilde.Core;

/// <summary>
/// Hebt leise Sprache vor der Erkennung an (Whisper erkennt leise Aufnahmen deutlich schlechter).
/// Nur wenn sich Sprache klar vom Hintergrund abhebt – reines Rauschen wird nicht verstärkt.
/// Die gespeicherte Originalaufnahme bleibt unverändert.
/// </summary>
public static class AudioGain
{
    private const int Frame = WavIO.SampleRate / 50; // 20 ms
    public const float TargetRms = 0.08f;           // ≈ −22 dBFS in den lauten Abschnitten
    public const float MaxGain = 6f;

    public static float ComputeGain(float[] samples)
    {
        if (samples.Length < Frame * 10) return 1f;
        var rms = new List<float>(samples.Length / Frame);
        float peak = 0;
        for (int i = 0; i + Frame <= samples.Length; i += Frame)
        {
            double sum = 0;
            for (int j = i; j < i + Frame; j++)
            {
                float v = samples[j];
                sum += v * v;
                float a = Math.Abs(v);
                if (a > peak) peak = a;
            }
            rms.Add((float)Math.Sqrt(sum / Frame));
        }
        rms.Sort();
        float loud = Avg(rms, 0.7, 1.0);    // lauteste 30 % = Sprache
        float quiet = Avg(rms, 0.0, 0.3);   // leiseste 30 % = Hintergrund
        if (loud < 1e-4f || loud < quiet * 3f) return 1f; // kein klarer Sprachanteil → nichts verstärken
        float gain = Math.Clamp(TargetRms / loud, 1f, MaxGain);
        if (peak > 0) gain = Math.Min(gain, 0.97f / peak);   // nicht übersteuern
        return gain < 1.15f ? 1f : gain;
    }

    public static float[] Apply(float[] samples, float gain)
    {
        if (gain == 1f) return samples;
        var o = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++) o[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        return o;
    }

    public static float[] Normalize(float[] samples) => Apply(samples, ComputeGain(samples));

    private static float Avg(List<float> sorted, double from, double to)
    {
        int a = (int)(sorted.Count * from), b = Math.Max(a + 1, (int)(sorted.Count * to));
        double s = 0;
        for (int i = a; i < b && i < sorted.Count; i++) s += sorted[i];
        return (float)(s / Math.Max(1, Math.Min(b, sorted.Count) - a));
    }
}
