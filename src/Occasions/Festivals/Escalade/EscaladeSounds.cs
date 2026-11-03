using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.Escalade;

/// <summary>
/// The Escalade's synthesised sounds (#1351), baked once as static banks (so the sound player lists them): the Marmite breaking (a dull crack of the
/// chocolate shell, then crumbs and a few iron clanks), the ladle against the soup pot, the Course's starting horn (two brass notes) and a gate's bell.
/// </summary>
public static class EscaladeSounds
{
    private static SfxBank? _smash, _ladle, _horn, _gate;

    /// <summary>The Marmite smashed: a low thump and crack in the first 80 ms, a hundred crumbs rattling for a second, three iron clanks as the lid hits the cauldron.</summary>
    public static SfxBank MarmiteSmash => _smash ??= SfxBank.Build("marmite smash", 3, 1.5f, 21011, (rng, n) =>
    {
        var s = new float[n];
        var thud = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(500f));
        var crack = Dsp.Noise(rng, n);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = thud[i] * Mathf.Exp(-t * 28f) * 3f + crack[i] * Mathf.Exp(-t * 90f) * 0.8f + Mathf.Sin(Mathf.Tau * 85f * t) * Mathf.Exp(-t * 18f) * 0.6f;
        }
        for (int k = 0; k < 110; k++)
        {
            int start = (int)((0.04 + rng.NextDouble() * 0.9) * Dsp.Rate);
            float f = 1800f + (float)rng.NextDouble() * 2600f, amp = 0.05f + (float)rng.NextDouble() * 0.12f;
            for (int i = start; i < Math.Min(n, start + 600); i++)
            {
                float t = (i - start) / (float)Dsp.Rate;
                s[i] += amp * Mathf.Sin(Mathf.Tau * f * t) * Mathf.Exp(-t * 400f);
            }
        }
        for (int k = 0; k < 3; k++)
        {
            int start = (int)((0.12 + k * 0.17 + rng.NextDouble() * 0.05) * Dsp.Rate);
            float f = 620f + (float)rng.NextDouble() * 240f;
            for (int i = start; i < Math.Min(n, start + (int)(0.5 * Dsp.Rate)); i++)
            {
                float t = (i - start) / (float)Dsp.Rate;
                s[i] += 0.3f * Mathf.Exp(-t * 11f) * (Mathf.Sin(Mathf.Tau * f * t) + 0.5f * Mathf.Sin(Mathf.Tau * f * 2.32f * t));
            }
        }
        return Clip(s);
    });

    /// <summary>A ladle knocked on the rim of a copper pot, then a short slosh: a ringing 700 Hz clank with an inharmonic partner, noise through a low-pass.</summary>
    public static SfxBank PotLadle => _ladle ??= SfxBank.Build("soup ladle", 3, 0.8f, 21101, (rng, n) =>
    {
        float f = 680f + (float)rng.NextDouble() * 120f;
        var slosh = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(700f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float ring = (Mathf.Sin(Mathf.Tau * f * t) + 0.6f * Mathf.Sin(Mathf.Tau * f * 2.76f * t)) * Mathf.Exp(-t * 14f);
            float wash = t > 0.12f ? slosh[i] * Mathf.Sin(Mathf.Pi * Mathf.Clamp((t - 0.12f) / 0.5f, 0f, 1f)) * 0.9f : 0f;
            s[i] = ring * 0.5f + wash;
        }
        return Clip(s);
    });

    /// <summary>The starter's horn: two brass notes (a fourth apart, G3 then C4), a sawtooth through a low-pass with a vibrato-free swell.</summary>
    public static SfxBank CourseHorn => _horn ??= SfxBank.Build("course horn", 2, 1.3f, 21201, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float f = t < 0.4f ? 196f : 261.6f;
            float tt = t < 0.4f ? t : t - 0.4f, len = t < 0.4f ? 0.4f : 0.9f;
            float env = Mathf.Clamp(tt / 0.04f, 0f, 1f) * Mathf.Clamp((len - tt) / 0.1f, 0f, 1f);
            float v = 0;
            for (int h = 1; h <= 6; h++) v += Mathf.Sin(Mathf.Tau * f * h * t) / h;
            s[i] = v * env * 0.45f;
        }
        return Clip(s);
    });

    /// <summary>A gate's bell: a clear strike with a fifth above, 0.5 s.</summary>
    public static SfxBank GateBell => _gate ??= SfxBank.Build("gate bell", 3, 0.6f, 21301, (rng, n) =>
    {
        float f = 1046f * (1f + ((float)rng.NextDouble() - 0.5f) * 0.02f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = (Mathf.Sin(Mathf.Tau * f * t) + 0.5f * Mathf.Sin(Mathf.Tau * f * 1.5f * t) + 0.25f * Mathf.Sin(Mathf.Tau * f * 2.76f * t)) * Mathf.Exp(-t * 9f) * 0.45f;
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
