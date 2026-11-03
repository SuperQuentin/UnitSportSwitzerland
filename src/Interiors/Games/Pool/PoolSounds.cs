using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors.Games.Pool;

/// <summary>
/// A pool table's synthesised sounds (#1324), each built once (static: the sound player lists them by itself): the
/// cue's tip on the ball, two balls' clack, a ball into a cushion, a ball dropping into a pocket.
/// </summary>
public static class PoolSounds
{
    private static AudioStreamWav? _cue, _clack, _cushion, _pocket;

    /// <summary>The leather tip on the cue ball: a short dull tock.</summary>
    public static AudioStreamWav Cue => _cue ??= OneShot(Dsp.OneShot(0.15f, 14241, (rng, n) =>
    {
        var s = new float[n];
        Knock(s, 0, 900f, 0.7f, 70f);
        var noise = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(800f), Dsp.Coef(3000f));
        for (int i = 0; i < n; i++) s[i] += noise[i] * 0.5f * Mathf.Exp(-i / (float)Dsp.Rate * 120f);
        return s;
    }));

    /// <summary>Two phenolic balls: the bright, hard clack.</summary>
    public static AudioStreamWav Clack => _clack ??= OneShot(Dsp.OneShot(0.2f, 14242, (rng, n) =>
    {
        var s = new float[n];
        Knock(s, 0, 3100f, 0.8f, 55f);
        Knock(s, 0, 4700f, 0.4f, 80f);
        var noise = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(2500f));
        for (int i = 0; i < n; i++) s[i] += noise[i] * 0.6f * Mathf.Exp(-i / (float)Dsp.Rate * 200f);
        return s;
    }));

    /// <summary>A ball into the rubber cushion: a soft, low thud.</summary>
    public static AudioStreamWav Cushion => _cushion ??= OneShot(Dsp.OneShot(0.25f, 14243, (rng, n) =>
    {
        var s = new float[n];
        Knock(s, 0, 190f, 0.8f, 30f);
        var noise = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(600f));
        for (int i = 0; i < n; i++) s[i] += noise[i] * 1.2f * Mathf.Exp(-i / (float)Dsp.Rate * 40f);
        return s;
    }));

    /// <summary>A ball into a pocket: the knock on the jaw, the drop, its roll down the return.</summary>
    public static AudioStreamWav Pocket => _pocket ??= OneShot(Dsp.OneShot(0.9f, 14244, (rng, n) =>
    {
        var s = new float[n];
        Knock(s, 0, 420f, 0.6f, 35f);
        Knock(s, 0.09f, 160f, 0.8f, 18f);
        var roll = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(200f), Dsp.Coef(800f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            if (t > 0.15f) s[i] += roll[i] * 1.1f * Mathf.Exp(-(t - 0.15f) * 4f) * (0.7f + 0.3f * Mathf.Sin(Mathf.Tau * 23f * t));
        }
        return s;
    }));

    private static void Knock(float[] s, float at, float f, float amp, float decay)
    {
        int from = (int)(at * Dsp.Rate);
        for (int i = from; i < s.Length; i++)
        {
            float t = (i - from) / (float)Dsp.Rate;
            float env = Mathf.Exp(-t * decay) * Mathf.Min(1, t * 4000);
            if (t > 0.01f && env < 1e-4f) break;
            s[i] += (Mathf.Sin(Mathf.Tau * f * t) + 0.3f * Mathf.Sin(Mathf.Tau * f * 2.7f * t)) * env * amp;
        }
    }

    private static AudioStreamWav OneShot(AudioStreamWav w)
    {
        w.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
        return w;
    }
}
