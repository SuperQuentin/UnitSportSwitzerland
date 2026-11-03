using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors.Games.Claw;

/// <summary>
/// A claw machine's synthesised sounds (#1326), each built once (static: the sound player lists them by itself): the
/// gantry's motor while the claw moves (a loop), and a win's jingle as a prize drops down the chute.
/// </summary>
public static class ClawSounds
{
    private static AudioStreamWav? _motor, _win;

    /// <summary>The small gear motor: a whine with its ripple, seamless as a loop.</summary>
    public static AudioStreamWav Motor => _motor ??= Dsp.Loop(1.0f, 14291, (rng, n) =>
    {
        var s = new float[n];
        var hiss = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(800f), Dsp.Coef(2500f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * 220f * t) * 0.25f * (0.8f + 0.2f * Mathf.Sin(Mathf.Tau * 30f * t)) + hiss[i] * 0.15f;
        }
        return s;
    });

    /// <summary>A prize down the chute: a soft thump, then a rising five-note jingle.</summary>
    public static AudioStreamWav Win => _win ??= OneShot(Dsp.OneShot(1.4f, 14292, (rng, n) =>
    {
        var s = new float[n];
        float[] notes = { 523f, 659f, 784f, 1047f, 1319f };
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * 90f * t) * Mathf.Exp(-t * 25f) * 0.7f;
            int k = (int)((t - 0.15f) / 0.13f);
            if (t > 0.15f && k < notes.Length)
            {
                float tn = t - 0.15f - k * 0.13f;
                s[i] += Mathf.Sign(Mathf.Sin(Mathf.Tau * notes[k] * t)) * 0.12f * Mathf.Exp(-tn * 8f);
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
