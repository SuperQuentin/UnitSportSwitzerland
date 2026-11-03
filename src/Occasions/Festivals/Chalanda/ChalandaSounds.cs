using Godot;
using UnitSport.Audio;
using UnitSport.Occasions.Festivals.Desalpe;

namespace UnitSport.Occasions.Festivals.Chalanda;

/// <summary>
/// The Chalandamarz's synthesised sounds (#1356), baked once as static banks (so the sound player lists them): the march drum that keeps the beat (a soft
/// skin thump), the muffled clank of a Treichel rung off the beat or standing still (a hand over the bell), and the bright chime of a ring in step with
/// the party. The Treichel itself is the Désalpe's (<see cref="DesalpeSounds.Treichel"/>): a ring on the beat plays the middle one, a good ring the small one.
/// </summary>
public static class ChalandaSounds
{
    private static SfxBank? _drum, _clunk, _chime;

    /// <summary>The march drum: a 95 Hz skin thump falling to 60 Hz with a short click of the stick, 0.3 s.</summary>
    public static SfxBank MarchDrum => _drum ??= SfxBank.Build("march drum", 3, 0.3f, 22011, (rng, n) =>
    {
        var click = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(1500f), Dsp.Coef(5000f));
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            phase += Mathf.Tau * (60f + 40f * Mathf.Exp(-t * 30f)) / Dsp.Rate;
            s[i] = (float)Math.Sin(phase) * Mathf.Exp(-t * 14f) * 0.8f + click[i] * Mathf.Exp(-t * 120f) * 0.35f;
        }
        return Clip(s);
    });

    /// <summary>A Treichel with a hand on it: a dull clank, no ring, 0.25 s.</summary>
    public static SfxBank MuffledClank => _clunk ??= SfxBank.Build("muffled treichel", 3, 0.25f, 22101, (rng, n) =>
    {
        float f = 230f + (float)rng.NextDouble() * 40f;
        var knock = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(1200f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = (Mathf.Sin(Mathf.Tau * f * t) + 0.5f * Mathf.Sin(Mathf.Tau * f * 2.7f * t)) * Mathf.Exp(-t * 38f) * 0.5f + knock[i] * Mathf.Exp(-t * 60f) * 0.6f;
        }
        return Clip(s);
    });

    /// <summary>The in-step chime: two bright notes a third apart (E6 then G#6), 0.5 s.</summary>
    public static SfxBank InStepChime => _chime ??= SfxBank.Build("in step chime", 2, 0.5f, 22201, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float a = Mathf.Sin(Mathf.Tau * 1318.5f * t) * Mathf.Exp(-t * 10f);
            float tt = Mathf.Max(0f, t - 0.09f);
            float b = t < 0.09f ? 0f : Mathf.Sin(Mathf.Tau * 1661.2f * tt) * Mathf.Exp(-tt * 9f);
            s[i] = (a + b) * 0.4f;
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
