using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors.Games.Darts;

/// <summary>
/// A dartboard's synthesised sounds (#1324), each built once (static: the sound player lists them by itself): a dart
/// into the sisal, one into the black surround (a hollower knock), the visit's darts pulled out.
/// </summary>
public static class DartsSounds
{
    private static AudioStreamWav? _thunk, _miss, _pull;

    /// <summary>A steel tip into packed sisal: a short, dry thock.</summary>
    public static AudioStreamWav Thunk => _thunk ??= OneShot(Dsp.OneShot(0.18f, 14251, (rng, n) =>
    {
        var s = new float[n];
        var noise = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(500f), Dsp.Coef(2500f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = noise[i] * 1.6f * Mathf.Exp(-t * 90f) + Mathf.Sin(Mathf.Tau * 240f * t) * 0.5f * Mathf.Exp(-t * 60f);
        }
        return s;
    }));

    /// <summary>A dart into the cabinet's surround: a hollow wooden knock.</summary>
    public static AudioStreamWav Miss => _miss ??= OneShot(Dsp.OneShot(0.25f, 14252, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Exp(-t * 35f) * Mathf.Min(1, t * 4000);
            s[i] = (Mathf.Sin(Mathf.Tau * 380f * t) + 0.4f * Mathf.Sin(Mathf.Tau * 910f * t)) * env * 0.7f;
        }
        return s;
    }));

    /// <summary>Three darts pulled from the board: three quick scrapes.</summary>
    public static AudioStreamWav Pull => _pull ??= OneShot(Dsp.OneShot(0.6f, 14253, (rng, n) =>
    {
        var s = new float[n];
        var noise = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(1500f));
        for (int k = 0; k < 3; k++)
        {
            int from = (int)((0.04f + k * 0.16f) * Dsp.Rate);
            for (int i = from; i < Math.Min(n, from + Dsp.Rate / 14); i++)
            {
                float t = (i - from) / (float)Dsp.Rate;
                s[i] += noise[i] * 0.8f * Mathf.Sin(Mathf.Pi * t * 14f);
            }
        }
        return s;
    }));

    private static AudioStreamWav OneShot(AudioStreamWav w)
    {
        w.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
        return w;
    }
}
