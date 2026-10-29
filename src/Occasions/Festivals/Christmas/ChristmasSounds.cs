using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.Christmas;

/// <summary>
/// The Christmas market's synthesised sounds (#1348), baked once as static banks (so the sound player lists them): the Samichlaus's sleigh bells
/// (a dozen tiny bells shaken at random, bright and short), the clink of a mug against a counter (a glazed-ceramic knock with a ring) and the stall's
/// "ting" (two rising bell notes, the stamp of a taste).
/// </summary>
public static class ChristmasSounds
{
    private static SfxBank? _bells, _clink, _ting;

    /// <summary>The Samichlaus's bells: about twenty tiny strikes over 1.1 s, each a bright inharmonic ring (2.6-5.4 kHz) that dies in a tenth of a second.</summary>
    public static SfxBank SleighBells => _bells ??= SfxBank.Build("sleigh bells", 3, 1.4f, 20011, (rng, n) =>
    {
        var s = new float[n];
        for (int k = 0; k < 22; k++)
        {
            int start = (int)(rng.NextDouble() * 1.1 * Dsp.Rate * (0.4 + 0.6 * rng.NextDouble()));
            float f = 2600f + (float)rng.NextDouble() * 2800f, amp = 0.25f + (float)rng.NextDouble() * 0.35f, decay = 22f + (float)rng.NextDouble() * 20f;
            for (int i = start; i < n; i++)
            {
                float t = (i - start) / (float)Dsp.Rate;
                if (t > 0.35f) break;
                s[i] += amp * Mathf.Exp(-decay * t) * (Mathf.Sin(Mathf.Tau * f * t) + 0.5f * Mathf.Sin(Mathf.Tau * f * 2.76f * t));
            }
        }
        return Clip(s);
    });

    /// <summary>A mug set down on a wooden counter: a dull knock, then a short glazed ring.</summary>
    public static SfxBank MugClink => _clink ??= SfxBank.Build("mug clink", 3, 0.5f, 20101, (rng, n) =>
    {
        var knock = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(900f));
        float f0 = 2300f + (float)rng.NextDouble() * 500f;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float ring = Mathf.Sin(Mathf.Tau * f0 * t) * Mathf.Exp(-t * 38f) + 0.5f * Mathf.Sin(Mathf.Tau * f0 * 1.62f * t) * Mathf.Exp(-t * 55f);
            s[i] = knock[i] * Mathf.Exp(-t * 120f) * 2.2f + ring * 0.55f;
        }
        return Clip(s);
    });

    /// <summary>A stall's "ting": two soft bell notes, a fifth apart, the second a little later.</summary>
    public static SfxBank StallTing => _ting ??= SfxBank.Build("stall ting", 3, 0.9f, 20201, (rng, n) =>
    {
        float f = 880f * (1f + ((float)rng.NextDouble() - 0.5f) * 0.03f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float a = Mathf.Sin(Mathf.Tau * f * t) * Mathf.Exp(-t * 7f);
            float t2 = t - 0.11f;
            float b = t2 > 0 ? Mathf.Sin(Mathf.Tau * f * 1.5f * t2) * Mathf.Exp(-t2 * 6f) : 0f;
            s[i] = (a + b + 0.2f * Mathf.Sin(Mathf.Tau * f * 2.01f * t) * Mathf.Exp(-t * 14f)) * 0.5f;
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
