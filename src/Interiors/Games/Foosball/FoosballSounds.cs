using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors.Games.Foosball;

/// <summary>
/// A table football table's synthesised sounds (#1324), each built once (static: the sound player lists them by
/// itself): a figure's hard kick on the cork ball, a softer knock, the ball dropping into a goal's box.
/// </summary>
public static class FoosballSounds
{
    private static AudioStreamWav? _kick, _knock, _goal;

    /// <summary>A plastic foot striking the ball hard: a sharp crack.</summary>
    public static AudioStreamWav Kick => _kick ??= OneShot(Dsp.OneShot(0.15f, 14261, (rng, n) => Hit(rng, n, 1400f, 1f, 90f)));

    /// <summary>The ball off a figure or a wall: a lighter knock.</summary>
    public static AudioStreamWav Knock => _knock ??= OneShot(Dsp.OneShot(0.12f, 14262, (rng, n) => Hit(rng, n, 900f, 0.6f, 110f)));

    /// <summary>A goal: the ball into the box, its rattle down the return.</summary>
    public static AudioStreamWav Goal => _goal ??= OneShot(Dsp.OneShot(1.0f, 14263, (rng, n) =>
    {
        var s = Hit(rng, n, 260f, 1f, 25f);
        var noise = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(300f), Dsp.Coef(1200f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            if (t > 0.12f) s[i] += noise[i] * Mathf.Exp(-(t - 0.12f) * 4f) * (0.6f + 0.4f * Mathf.Sin(Mathf.Tau * 19f * t));
        }
        return s;
    }));

    private static float[] Hit(Random rng, int n, float f, float amp, float decay)
    {
        var s = new float[n];
        var noise = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(1000f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Exp(-t * decay) * Mathf.Min(1, t * 4000);
            s[i] = (Mathf.Sin(Mathf.Tau * f * t) * 0.8f + noise[i] * 0.5f * Mathf.Exp(-t * 200f)) * env * amp;
        }
        return s;
    }

    private static AudioStreamWav OneShot(AudioStreamWav w)
    {
        w.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
        return w;
    }
}
