using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// The stone put's synthesised sounds (#1269 part 3), each baked once and listed by the sound player (static banks): the lifter's effort,
/// the festival drum that sets the run-up's beat, the stone falling in sawdust and the slip of a stone that was never lifted.
/// </summary>
public static class SteinSounds
{
    private static SfxBank? _grunt, _drum, _accent, _thud, _slip;

    /// <summary>The effort of lifting: a low voiced breath that swells and cuts off.</summary>
    public static SfxBank Grunt => _grunt ??= SfxBank.Build("stone lift effort", 3, 0.7f, 18011, (rng, n) =>
    {
        float f0 = 105f + (float)rng.NextDouble() * 25f;
        var breath = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(200f), Dsp.Coef(1400f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Min(1f, t * 14f) * Mathf.Exp(-Mathf.Pow((t - 0.28f) * 3.4f, 2f));
            float voice = Mathf.Sin(Mathf.Tau * f0 * (1f - 0.18f * t) * t) + 0.5f * Mathf.Sin(Mathf.Tau * f0 * 2f * (1f - 0.18f * t) * t);
            s[i] = (voice * 0.55f + breath[i] * 1.2f) * env;
        }
        return Clip(s);
    });

    /// <summary>A beat of the run-up's drum: a deep skin hit with a short slap.</summary>
    public static SfxBank Drum => _drum ??= SfxBank.Build("festival drum beat", 2, 0.45f, 18101, (rng, n) => Hit(rng, n, 78f, 1f));

    /// <summary>The fourth beat, the push: the same drum, higher and harder.</summary>
    public static SfxBank Accent => _accent ??= SfxBank.Build("festival drum accent", 2, 0.55f, 18201, (rng, n) => Hit(rng, n, 96f, 1.5f));

    private static float[] Hit(Random rng, int n, float f0, float slap)
    {
        var skin = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(500f), Dsp.Coef(3500f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float body = Mathf.Sin(Mathf.Tau * f0 * (0.6f + 0.4f * Mathf.Exp(-t * 18f)) * t) * Mathf.Exp(-t * 9f);
            s[i] = body * 1.3f + skin[i] * Mathf.Exp(-t * 60f) * 0.6f * slap;
        }
        return Clip(s);
    }

    /// <summary>The stone falling into sawdust: one dull thump and a soft hush of settling grains, no ring.</summary>
    public static SfxBank Thud => _thud ??= SfxBank.Build("stone in sawdust", 3, 0.9f, 18301, (rng, n) =>
    {
        float f0 = 52f + (float)rng.NextDouble() * 14f;
        var grains = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(2200f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float thump = Mathf.Sin(Mathf.Tau * f0 * (0.5f + 0.5f * Mathf.Exp(-t * 25f)) * t) * Mathf.Exp(-t * 14f);
            float hush = grains[i] * Mathf.Min(1f, t * 80f) * Mathf.Exp(-t * 6f);
            s[i] = thump * 1.6f + hush * 0.7f;
        }
        return Clip(s);
    });

    /// <summary>A foul: the stone scraping off the hands and dropping on the planks.</summary>
    public static SfxBank Slip => _slip ??= SfxBank.Build("stone slips", 2, 0.8f, 18401, (rng, n) =>
    {
        var scrape = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(300f), Dsp.Coef(2500f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float rub = scrape[i] * Mathf.Exp(-Mathf.Pow((t - 0.12f) * 9f, 2f)) * 0.9f;
            float drop = t > 0.3f ? Mathf.Sin(Mathf.Tau * 60f * (t - 0.3f)) * Mathf.Exp(-(t - 0.3f) * 16f) * 1.4f : 0f;
            s[i] = rub + drop;
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
