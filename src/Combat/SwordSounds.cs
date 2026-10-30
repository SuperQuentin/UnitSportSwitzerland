using Godot;
using UnitSport.Audio;

namespace UnitSport.Combat;

/// <summary>
/// The sword's synthesised sounds (#1371), each baked once and static, so the sound player (<c>--sounds</c>) lists them by itself:
/// the swish of a light swing and of a heavy one, the strike of a blow landing, the dodge's rush of air, and the charge winding up.
/// </summary>
public static class SwordSounds
{
    private static SfxBank? _swish, _heavy, _strike, _dodge, _charge, _knock;

    /// <summary>A light swing: a thin, fast rush of air that rises and falls over a third of a second.</summary>
    public static SfxBank Swish => _swish ??= SfxBank.Build("sword swish", 5, 0.30f, 19011, (rng, n) =>
    {
        float lo = 0.06f + (float)rng.NextDouble() * 0.03f, hi = 0.34f;
        var s = Dsp.BandPass(Dsp.Noise(rng, n), lo, hi);
        for (int i = 0; i < n; i++)
        {
            float x = (float)i / n;
            float env = Mathf.Sin(Mathf.Pi * Mathf.Pow(x, 0.55f));
            s[i] *= 3.2f * env * env;
        }
        return s;
    });

    /// <summary>A heavy blow (the finisher, the charged one, the plunge): the swish, lower, longer, with a swell of weight under it.</summary>
    public static SfxBank Heavy => _heavy ??= SfxBank.Build("sword heavy swing", 4, 0.55f, 19111, (rng, n) =>
    {
        var air = Dsp.BandPass(Dsp.Noise(rng, n), 0.02f, 0.14f);
        float f0 = 70f + (float)rng.NextDouble() * 15f;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate, x = (float)i / n;
            float env = Mathf.Sin(Mathf.Pi * Mathf.Pow(x, 0.7f));
            s[i] = air[i] * 4f * env * env + Mathf.Sin(Mathf.Tau * f0 * (1.2f - 0.5f * x) * t) * env * 0.5f;
        }
        return s;
    });

    /// <summary>A blow landing on a target: a dry knock, a bright ring of steel and a puff of straw.</summary>
    public static SfxBank Strike => _strike ??= SfxBank.Build("sword strike", 5, 0.45f, 19211, (rng, n) =>
    {
        float f0 = 140f + (float)rng.NextDouble() * 50f;
        float ring = 1500f + (float)rng.NextDouble() * 500f;
        var straw = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(1800f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float knock = Mathf.Sin(Mathf.Tau * f0 * (0.7f + 0.3f * Mathf.Exp(-t * 20f)) * t) * Mathf.Exp(-t * 18f);
            float steel = (Mathf.Sin(Mathf.Tau * ring * t) + 0.5f * Mathf.Sin(Mathf.Tau * ring * 2.4f * t)) * Mathf.Exp(-t * 14f) * 0.35f;
            s[i] = knock * 1.1f + steel + straw[i] * Mathf.Exp(-t * 45f) * 0.7f;
        }
        return s;
    });

    /// <summary>The dodge: a soft rush of air with a little cloth in it, rising.</summary>
    public static SfxBank Dodge => _dodge ??= SfxBank.Build("sword dodge", 4, 0.28f, 19311, (rng, n) =>
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), 0.03f, 0.2f);
        for (int i = 0; i < n; i++)
        {
            float x = (float)i / n;
            float env = Mathf.Sin(Mathf.Pi * Mathf.Pow(x, 0.45f));
            s[i] *= 2.2f * env;
        }
        return s;
    });

    /// <summary>The charge winding up: a rising shimmer of steel, 0.9 seconds.</summary>
    public static SfxBank Charge => _charge ??= SfxBank.Build("sword charge", 2, 0.9f, 19411, (rng, n) =>
    {
        float f0 = 380f + (float)rng.NextDouble() * 30f;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate, x = (float)i / n;
            float f = f0 * (1f + 1.4f * x * x);
            float env = Mathf.Min(1f, t * 12f) * (0.35f + 0.65f * x) * Mathf.Min(1f, (1f - x) * 12f);
            s[i] = (Mathf.Sin(Mathf.Tau * f * t) + 0.4f * Mathf.Sin(Mathf.Tau * f * 2.01f * t)) * env * 0.4f;
        }
        return s;
    });

    /// <summary>A trained-on dummy knocked flat: a wooden clatter that dies away.</summary>
    public static SfxBank Clatter => _knock ??= SfxBank.Build("dummy clatter", 3, 0.7f, 19511, (rng, n) =>
    {
        var s = new float[n];
        var hits = new[] { 0f, 0.07f + (float)rng.NextDouble() * 0.03f, 0.17f + (float)rng.NextDouble() * 0.05f };
        for (int h = 0; h < hits.Length; h++)
        {
            float f0 = 220f + h * 70f + (float)rng.NextDouble() * 40f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Dsp.Rate - hits[h];
                if (t < 0f) continue;
                s[i] += Mathf.Sin(Mathf.Tau * f0 * t) * Mathf.Exp(-t * 28f) * (1f - h * 0.25f);
            }
        }
        return s;
    });
}
