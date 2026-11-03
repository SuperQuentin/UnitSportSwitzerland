using Godot;
using UnitSport.Audio;

namespace UnitSport.Combat;

/// <summary>
/// Schwingen's synthesised sounds (#1269), each baked once and static, so the sound player lists them by itself: the grunt of a throw
/// starting, the slap of a hand on a braced back, the thud of a body in the sawdust (with its dull scatter of sawdust), and the
/// bell of the Gang's end.
/// </summary>
public static class SchwingSounds
{
    private static SfxBank? _grunt, _slap, _thud, _bell;

    /// <summary>A throw starting: a breathy low voice and a rush of cloth.</summary>
    public static SfxBank Grunt => _grunt ??= SfxBank.Build("schwing grunt", 4, 0.45f, 18011, (rng, n) =>
    {
        float f0 = 95f + (float)rng.NextDouble() * 35f;
        var breath = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(300f), Dsp.Coef(1800f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Min(1f, t * 40f) * Mathf.Exp(-t * 7f);
            float voice = (Mathf.Sin(Mathf.Tau * f0 * t) + 0.5f * Mathf.Sin(Mathf.Tau * f0 * 2f * t) + 0.25f * Mathf.Sin(Mathf.Tau * f0 * 3f * t)) * 0.4f;
            s[i] = (voice + breath[i] * 0.8f) * env;
        }
        return s;
    });

    /// <summary>A hand slapping a braced back or a trouser leg: a short flat crack with a little body.</summary>
    public static SfxBank Slap => _slap ??= SfxBank.Build("schwing slap", 4, 0.3f, 18101, (rng, n) =>
    {
        var crack = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(1100f));
        float f0 = 150f + (float)rng.NextDouble() * 60f;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = crack[i] * Mathf.Exp(-t * 55f) * 1.1f + Mathf.Sin(Mathf.Tau * f0 * t) * Mathf.Exp(-t * 30f) * 0.6f;
        }
        return s;
    });

    /// <summary>A body landing on the sawdust: a deep thump, the air going out, and the sawdust hissing away.</summary>
    public static SfxBank Thud => _thud ??= SfxBank.Build("schwing thud", 4, 0.9f, 18201, (rng, n) =>
    {
        float f0 = 55f + (float)rng.NextDouble() * 25f;
        var dust = Dsp.LowPass(Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(500f)), Dsp.Coef(4000f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float thump = Mathf.Sin(Mathf.Tau * f0 * (0.6f + 0.4f * Mathf.Exp(-t * 14f)) * t) * Mathf.Exp(-t * 9f) * 1.5f;
            float scatter = dust[i] * Mathf.Min(1f, t * 60f) * Mathf.Exp(-t * 6f) * 0.5f;
            s[i] = thump + scatter;
        }
        return s;
    });

    /// <summary>The bell that ends a Gang: two strikes of a small bronze bell.</summary>
    public static SfxBank Bell => _bell ??= SfxBank.Build("schwing bell", 2, 1.6f, 18301, (rng, n) =>
    {
        float f0 = 620f + (float)rng.NextDouble() * 40f;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float strike = Mathf.Exp(-t * 3.2f) + (t > 0.35f ? Mathf.Exp(-(t - 0.35f) * 3.2f) * 0.8f : 0f);
            s[i] = (Mathf.Sin(Mathf.Tau * f0 * t) + 0.6f * Mathf.Sin(Mathf.Tau * f0 * 2.76f * t) + 0.3f * Mathf.Sin(Mathf.Tau * f0 * 5.4f * t)) * strike * 0.45f;
        }
        return s;
    });
}
