using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors.Games.AirHockey;

/// <summary>
/// An air hockey table's synthesised sounds (#1326), each built once (static: the sound player lists them by itself):
/// the puck's clack off a mallet or a rail, and a goal: the puck down the slot and the table's buzzer.
/// </summary>
public static class AirHockeySounds
{
    private static AudioStreamWav? _clack, _goal;

    /// <summary>A hard plastic puck struck: a bright, short clack.</summary>
    public static AudioStreamWav Clack => _clack ??= OneShot(Dsp.OneShot(0.12f, 14271, (rng, n) =>
    {
        var s = new float[n];
        var noise = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(1800f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = (Mathf.Sin(Mathf.Tau * 2200f * t) * 0.6f + noise[i] * 0.7f) * Mathf.Exp(-t * 120f);
        }
        return s;
    }));

    /// <summary>A goal: the puck's clunk into the slot, then a two-tone buzzer.</summary>
    public static AudioStreamWav Goal => _goal ??= OneShot(Dsp.OneShot(1.1f, 14272, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * 180f * t) * Mathf.Exp(-t * 20f) * 0.8f;
            if (t > 0.15f && t < 1.0f)
            {
                float f = t < 0.55f ? 660f : 880f;
                // a square-ish buzzer
                s[i] += Mathf.Sign(Mathf.Sin(Mathf.Tau * f * t)) * 0.18f * Mathf.Min(1, (1.0f - t) * 10);
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
