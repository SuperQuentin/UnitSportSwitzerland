using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.Sechselaeuten;

/// <summary>
/// The Sechseläuten's synthesised sounds (#1358), baked once as static banks (so the sound player lists them): the head bursting (a deep boom, a
/// crack and a string of firecrackers going off), the pyre catching (a whoosh with a bell on top), the fire's pops and a coin dropped on the stake.
/// </summary>
public static class SechselaeutenSounds
{
    private static SfxBank? _blast, _light, _crackle, _coin;

    /// <summary>The head bursts: a 55 Hz boom with a 12 Hz thump under it and a crack in the first 30 ms, then some sixty firecrackers over a second and a half, thinning out.</summary>
    public static SfxBank HeadBlast => _blast ??= SfxBank.Build("boogg head bang", 3, 2.4f, 22011, (rng, n) =>
    {
        var s = new float[n];
        var rumble = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(260f));
        var crack = Dsp.Noise(rng, n);
        float f = 52f + (float)rng.NextDouble() * 10f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * f * t * (1f - 0.25f * t)) * Mathf.Exp(-t * 3.4f) * 0.9f
                 + rumble[i] * Mathf.Exp(-t * 4.5f) * 2.4f
                 + crack[i] * Mathf.Exp(-t * 70f) * 1.1f;
        }
        for (int k = 0; k < 60; k++)
        {
            double at = 0.05 + Math.Pow(rng.NextDouble(), 1.7) * 1.6;
            int start = (int)(at * Dsp.Rate);
            float amp = 0.18f + (float)rng.NextDouble() * 0.3f;
            for (int i = start; i < Math.Min(n, start + 900); i++)
            {
                float t = (i - start) / (float)Dsp.Rate;
                s[i] += amp * crack[i] * Mathf.Exp(-t * 260f);
            }
        }
        return Clip(s);
    });

    /// <summary>The pyre catching: a rising whoosh of noise through a sweeping low-pass, and a bell struck at the start (the guilds' bells ring the festival in).</summary>
    public static SfxBank PyreWhoosh => _light ??= SfxBank.Build("boogg pyre lit", 2, 2.2f, 22101, (rng, n) =>
    {
        var s = new float[n];
        var noise = Dsp.Noise(rng, n);
        float lp = 0f;
        float bell = 520f + (float)rng.NextDouble() * 60f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float cutoff = Mathf.Clamp(0.015f + t * 0.06f, 0.015f, 0.2f);
            lp += (noise[i] - lp) * cutoff;
            float swell = Mathf.Clamp(t / 0.9f, 0f, 1f) * Mathf.Clamp((2.2f - t) / 0.6f, 0f, 1f);
            s[i] = lp * swell * 4.5f + (Mathf.Sin(Mathf.Tau * bell * t) + 0.5f * Mathf.Sin(Mathf.Tau * bell * 2.4f * t)) * Mathf.Exp(-t * 2.2f) * 0.4f;
        }
        return Clip(s);
    });

    /// <summary>A few pops and a hiss of burning wood, 0.7 s: played now and then while the pyre burns.</summary>
    public static SfxBank FireCrackle => _crackle ??= SfxBank.Build("boogg fire crackle", 4, 0.7f, 22201, (rng, n) =>
    {
        var s = new float[n];
        var hiss = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(1800f));
        for (int i = 0; i < n; i++) s[i] = hiss[i] * 0.25f * Mathf.Clamp(1f - i / (float)n, 0f, 1f);
        int pops = 3 + rng.Next(4);
        for (int k = 0; k < pops; k++)
        {
            int start = (int)(rng.NextDouble() * 0.55 * Dsp.Rate);
            float f = 700f + (float)rng.NextDouble() * 1500f, amp = 0.4f + (float)rng.NextDouble() * 0.5f;
            for (int i = start; i < Math.Min(n, start + 1500); i++)
            {
                float t = (i - start) / (float)Dsp.Rate;
                s[i] += amp * Mathf.Sin(Mathf.Tau * f * t) * Mathf.Exp(-t * 180f);
            }
        }
        return Clip(s);
    });

    /// <summary>A coin dropped on the stake: a short bright ring with an inharmonic partner and a tiny rattle.</summary>
    public static SfxBank CoinStake => _coin ??= SfxBank.Build("boogg bet coin", 3, 0.5f, 22301, (rng, n) =>
    {
        float f = 2600f + (float)rng.NextDouble() * 500f;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = (Mathf.Sin(Mathf.Tau * f * t) + 0.6f * Mathf.Sin(Mathf.Tau * f * 1.51f * t)) * Mathf.Exp(-t * 14f) * 0.4f;
            if (t > 0.07f) s[i] += (Mathf.Sin(Mathf.Tau * f * 0.93f * (t - 0.07f)) + 0.5f * Mathf.Sin(Mathf.Tau * f * 1.45f * (t - 0.07f))) * Mathf.Exp(-(t - 0.07f) * 22f) * 0.22f;
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
