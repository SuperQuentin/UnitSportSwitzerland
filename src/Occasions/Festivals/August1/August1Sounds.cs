using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.August1;

/// <summary>
/// The 1 August's synthesised sounds (#1355), baked once as static banks (so the sound player lists them): the rustle of a paper lantern taken from the
/// stand, the soft chime of a stop on the lampion walk, the short brass fanfare of a finished walk and the hiss of a fuse before a box of fireworks goes off.
/// </summary>
public static class August1Sounds
{
    private static SfxBank? _rustle, _chime, _fanfare, _fuse;

    /// <summary>Paper lantern: a dry band-passed noise swell and a few crinkles in 0.5 s.</summary>
    public static SfxBank LampionRustle => _rustle ??= SfxBank.Build("lampion rustle", 3, 0.5f, 31011, (rng, n) =>
    {
        var s = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(5200f));
        var low = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(1400f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = (s[i] - low[i]) * Mathf.Sin(Mathf.Pi * Mathf.Clamp(t / 0.45f, 0f, 1f)) * 0.9f;
        }
        for (int k = 0; k < 14; k++)
        {
            int start = (int)(rng.NextDouble() * 0.4 * Dsp.Rate);
            float amp = 0.1f + (float)rng.NextDouble() * 0.2f;
            for (int i = start; i < Math.Min(n, start + 220); i++) s[i] += amp * (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-(i - start) / 50f);
        }
        return Clip(s);
    });

    /// <summary>A stop's chime: a glockenspiel bar, a fundamental and a bright partner, 0.7 s.</summary>
    public static SfxBank StopChime => _chime ??= SfxBank.Build("lampion stop chime", 3, 0.7f, 31101, (rng, n) =>
    {
        float f = 880f * (1f + ((float)rng.NextDouble() - 0.5f) * 0.03f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = (Mathf.Sin(Mathf.Tau * f * t) + 0.4f * Mathf.Sin(Mathf.Tau * f * 3.9f * t) * Mathf.Exp(-t * 12f)) * Mathf.Exp(-t * 6f) * 0.5f;
        }
        return Clip(s);
    });

    /// <summary>Walk done: three brass notes (C, E, G, a major triad up) ending on a held fifth, 1.4 s.</summary>
    public static SfxBank WalkFanfare => _fanfare ??= SfxBank.Build("lampion walk fanfare", 2, 1.4f, 31201, (rng, n) =>
    {
        var s = new float[n];
        float[] notes = [261.6f, 329.6f, 392f];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            int k = Math.Min(2, (int)(t / 0.28f));
            float f = notes[k], tt = k < 2 ? t - k * 0.28f : t - 0.56f;
            float len = k < 2 ? 0.28f : 0.84f;
            float env = Mathf.Clamp(tt / 0.03f, 0f, 1f) * Mathf.Clamp((len - tt) / 0.12f, 0f, 1f);
            float v = 0;
            for (int h = 1; h <= 6; h++) v += Mathf.Sin(Mathf.Tau * f * h * t) / h;
            s[i] = v * env * 0.4f;
        }
        return Clip(s);
    });

    /// <summary>A fuse: a hissing noise that sputters and climbs for a second.</summary>
    public static SfxBank FuseHiss => _fuse ??= SfxBank.Build("firework fuse", 3, 1.1f, 31301, (rng, n) =>
    {
        var hiss = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(7000f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float sputter = 0.6f + 0.4f * Mathf.Sin(t * 90f + Mathf.Sin(t * 23f) * 3f);
            s[i] = hiss[i] * sputter * Mathf.Clamp(t / 0.1f, 0f, 1f) * (1f - 0.6f * t / 1.1f) * 0.8f;
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
