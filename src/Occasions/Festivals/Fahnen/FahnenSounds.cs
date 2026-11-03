using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.Fahnen;

/// <summary>
/// The flag swingers' synthesised sounds (#1269 part 5), baked once as static banks (so the sound player lists them): the march drum
/// that keeps the beat, the whoosh of a figure, the snap of the cloth on a good finish, and the thud of a caught flag.
/// </summary>
public static class FahnenSounds
{
    private static SfxBank? _drum, _whoosh, _snap, _catch;

    /// <summary>A small military drum on the beat: a tuned skin tap with a short snare rattle.</summary>
    public static SfxBank Drum => _drum ??= SfxBank.Build("fahne march drum", 3, 0.28f, 18011, (rng, n) =>
    {
        float f0 = 150f + (float)rng.NextDouble() * 20f;
        var rattle = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(2500f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * f0 * (0.8f + 0.2f * Mathf.Exp(-t * 30f)) * t) * Mathf.Exp(-t * 22f) + rattle[i] * Mathf.Exp(-t * 28f) * 0.5f;
        }
        return Clip(s);
    });

    /// <summary>Cloth cutting the air: a band of noise that swells and falls, over about half a second.</summary>
    public static SfxBank Whoosh => _whoosh ??= SfxBank.Build("fahne whoosh", 4, 0.6f, 18101, (rng, n) =>
    {
        var air = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(500f + (float)rng.NextDouble() * 300f), Dsp.Coef(2600f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)n;
            s[i] = air[i] * Mathf.Sin(Mathf.Pi * u) * Mathf.Sin(Mathf.Pi * u) * 1.6f;
        }
        return Clip(s);
    });

    /// <summary>The crack of a flag pulled taut on the beat: a bright burst with a quick flutter after.</summary>
    public static SfxBank Snap => _snap ??= SfxBank.Build("fahne snap", 3, 0.35f, 18201, (rng, n) =>
    {
        var crack = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(1800f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float flutter = 0.55f + 0.45f * Mathf.Sin(Mathf.Tau * 38f * t);
            s[i] = crack[i] * (Mathf.Exp(-t * 55f) + 0.25f * flutter * Mathf.Exp(-t * 9f));
        }
        return Clip(s);
    });

    /// <summary>The flag caught: the staff slapping into a palm, a dull knock with a little cloth behind it.</summary>
    public static SfxBank Catch => _catch ??= SfxBank.Build("fahne catch", 3, 0.4f, 18301, (rng, n) =>
    {
        var cloth = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(1500f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * 110f * t) * Mathf.Exp(-t * 30f) * 1.2f + cloth[i] * Mathf.Exp(-t * 12f) * 0.6f;
        }
        return Clip(s);
    });

    private static float[] Clip(float[] s)
    {
        float peak = 0;
        foreach (float v in s) peak = Mathf.Max(peak, Mathf.Abs(v));
        if (peak > 1f)
            for (int i = 0; i < s.Length; i++) s[i] /= peak;
        return s;
    }
}
