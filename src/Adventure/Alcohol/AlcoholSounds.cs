using Godot;
using UnitSport.Audio;

namespace UnitSport.Adventure.Alcohol;

/// <summary>
/// The breathalyzers' synthesised sounds (#1271), each built once (static: the sound player lists them by itself): the
/// electronic device's rising blow tone (5 s), its end beeps, and the crystal tube's hiss.
/// </summary>
public static class AlcoholSounds
{
    private static AudioStreamWav? _blow, _beep, _hiss;

    /// <summary>A tone that climbs from 600 to 1 400 Hz over the five seconds of the blow, ticking faster.</summary>
    public static AudioStreamWav Blow => _blow ??= Dsp.OneShot(AlcoholRules.BlowSeconds, 14861, (rng, n) =>
    {
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float k = t / AlcoholRules.BlowSeconds;
            phase += (600f + 800f * k) / Dsp.Rate;
            float gate = Mathf.Sin(Mathf.Tau * (2f + 6f * k) * t) > -0.2f ? 1f : 0.25f;
            float env = Mathf.Min(1f, t * 20f) * Mathf.Min(1f, (AlcoholRules.BlowSeconds - t) * 20f);
            s[i] = (float)Math.Sin(phase * Math.Tau) * 0.22f * env * gate;
        }
        return s;
    });

    /// <summary>The end of the blow: two short high beeps.</summary>
    public static AudioStreamWav Beep => _beep ??= Dsp.OneShot(0.5f, 14862, (rng, n) =>
    {
        var s = new float[n];
        foreach (float at in new[] { 0f, 0.2f })
            for (int i = (int)(at * Dsp.Rate); i < Math.Min(n, (int)((at + 0.14f) * Dsp.Rate)); i++)
            {
                float t = i / (float)Dsp.Rate - at;
                s[i] += Mathf.Sin(Mathf.Tau * 2400f * t) * Mathf.Min(1f, t * 300f) * Mathf.Min(1f, (0.14f - t) * 300f) * 0.4f;
            }
        return s;
    });

    /// <summary>The crystals in the tube changing colour: a soft hiss that thins out.</summary>
    public static AudioStreamWav Hiss => _hiss ??= Dsp.OneShot(1.2f, 14863, (rng, n) =>
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(2500f), Dsp.Coef(7500f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] *= Mathf.Min(1f, t * 15f) * Mathf.Exp(-t * 2.6f) * 0.9f;
        }
        return s;
    });
}
