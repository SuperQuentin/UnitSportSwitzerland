using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.Zibelemaerit;

/// <summary>
/// The Zibelemärit's synthesised sounds (#1373), baked once as static banks (so the sound player lists them): the stall's bell (a bright bell struck twice,
/// the stamp of a taste), the paper bag's rustle (a ware handed over the counter) and the confetti egg's crack (a dry pop, then a shower of paper ticks).
/// </summary>
public static class ZibeleSounds
{
    private static SfxBank? _bell, _rustle, _crack;

    /// <summary>A hand bell struck twice, the second a little softer and a fourth higher.</summary>
    public static SfxBank MarketBell => _bell ??= SfxBank.Build("onion market bell", 3, 1.0f, 21011, (rng, n) =>
    {
        float f = 1320f * (1f + ((float)rng.NextDouble() - 0.5f) * 0.04f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate, t2 = t - 0.16f;
            float a = (Mathf.Sin(Mathf.Tau * f * t) + 0.4f * Mathf.Sin(Mathf.Tau * f * 2.4f * t)) * Mathf.Exp(-t * 9f);
            float b = t2 > 0 ? (Mathf.Sin(Mathf.Tau * f * 1.335f * t2) + 0.4f * Mathf.Sin(Mathf.Tau * f * 3.2f * t2)) * Mathf.Exp(-t2 * 10f) * 0.7f : 0f;
            s[i] = (a + b) * 0.4f;
        }
        return Clip(s);
    });

    /// <summary>A paper bag taken from a counter: band-limited noise in three crinkles.</summary>
    public static SfxBank PaperRustle => _rustle ??= SfxBank.Build("paper rustle", 3, 0.5f, 21101, (rng, n) =>
    {
        var noise = Dsp.Noise(rng, n);
        var low = Dsp.LowPass(noise, Dsp.Coef(1200f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = 0f;
            foreach (float at in new[] { 0f, 0.12f, 0.27f })
            {
                float d = t - at;
                if (d > 0) env += Mathf.Exp(-d * 24f) * (0.5f + 0.5f * Mathf.Sin(d * 420f));
            }
            s[i] = (noise[i] - low[i]) * env * 0.55f;
        }
        return Clip(s);
    });

    /// <summary>A confetti egg cracked on a head: a dry pop and a shower of tiny paper ticks.</summary>
    public static SfxBank EggCrack => _crack ??= SfxBank.Build("confetti egg crack", 3, 0.7f, 21201, (rng, n) =>
    {
        var noise = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(3500f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = noise[i] * Mathf.Exp(-t * 90f) * 1.6f + Mathf.Sin(Mathf.Tau * 190f * t) * Mathf.Exp(-t * 60f) * 0.4f;
        }
        for (int k = 0; k < 30; k++)
        {
            int start = (int)((0.03 + rng.NextDouble() * 0.5) * Dsp.Rate);
            float amp = 0.08f + (float)rng.NextDouble() * 0.12f;
            for (int j = 0; j < 90 && start + j < n; j++) s[start + j] += amp * (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-j / 20f);
        }
        return Clip(s);
    });

    private static float[] Clip(float[] s)
    {
        float peak = 0;
        foreach (float v in s) peak = Mathf.Max(peak, Mathf.Abs(v));
        if (peak > 0.95f)
            for (int i = 0; i < s.Length; i++) s[i] *= 0.95f / peak;
        return s;
    }
}
