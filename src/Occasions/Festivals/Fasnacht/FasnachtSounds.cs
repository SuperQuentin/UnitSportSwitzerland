using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.Fasnacht;

/// <summary>
/// The Fasnacht's synthesised sounds (#1357), baked once as static banks (so the sound player lists them): the Guggenmusik's snare, bass drum, trumpet and
/// sousaphone (one note each, the game pitches it by the part's semitones), the toss of a handful of Räppli and the Morgestraich's drum roll. Loud on purpose:
/// a Gugge is brass and drums played as hard as they go, a little flat and a little out.
/// </summary>
public static class FasnachtSounds
{
    private static SfxBank? _snare, _bass, _trumpet, _tuba, _toss, _roll;

    /// <summary>The semitone step of the trumpet and the sousaphone: <c>PitchScale = 2^(semitones / 12)</c>.</summary>
    public static float Pitch(int semitones) => Mathf.Pow(2f, semitones / 12f);

    /// <summary>A snare: a noise crack through a high-pass with a short ring of the head at 190 Hz, 0.25 s.</summary>
    public static SfxBank Snare => _snare ??= SfxBank.Build("guggen snare", 3, 0.3f, 23011, (rng, n) =>
    {
        var noise = Dsp.Noise(rng, n);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = noise[i] * Mathf.Exp(-t * 26f) * 0.9f + Mathf.Sin(Mathf.Tau * 190f * t) * Mathf.Exp(-t * 30f) * 0.5f;
        }
        return Clip(s);
    });

    /// <summary>A bass drum: a sine falling from 130 to 52 Hz over a click, 0.4 s.</summary>
    public static SfxBank BassDrum => _bass ??= SfxBank.Build("guggen bass drum", 2, 0.45f, 23101, (rng, n) =>
    {
        var s = new float[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float f = 52f + 78f * Mathf.Exp(-t * 22f);
            phase += Mathf.Tau * f / Dsp.Rate;
            s[i] = Mathf.Sin(phase) * Mathf.Exp(-t * 9f) * 0.95f + ((float)rng.NextDouble() - 0.5f) * Mathf.Exp(-t * 200f) * 0.4f;
        }
        return Clip(s);
    });

    /// <summary>A trumpet stab at 440 Hz (the game pitches it): a bright sawtooth through a rising low-pass, a quick attack, a 0.35 s decay, a little detune for the Gugge's sourness.</summary>
    public static SfxBank Trumpet => _trumpet ??= SfxBank.Build("guggen trumpet", 3, 0.5f, 23201, (rng, n) =>
    {
        float detune = 1f + ((float)rng.NextDouble() - 0.5f) * 0.03f;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate, f = 440f * detune;
            float env = Mathf.Clamp(t / 0.015f, 0f, 1f) * Mathf.Exp(-t * 7f);
            float bright = 0.4f + 0.6f * Mathf.Exp(-t * 12f);
            float v = 0;
            for (int h = 1; h <= 9; h++) v += Mathf.Sin(Mathf.Tau * f * h * t) / h * Mathf.Pow(bright, h * 0.35f);
            s[i] = v * env * 0.5f;
        }
        return Clip(s);
    });

    /// <summary>A sousaphone's oom at 110 Hz (the game pitches it): a round, low sawtooth with only the first harmonics, a puff of breath, 0.45 s.</summary>
    public static SfxBank Sousaphone => _tuba ??= SfxBank.Build("guggen sousaphone", 2, 0.55f, 23301, (rng, n) =>
    {
        var breath = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(600f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Clamp(t / 0.03f, 0f, 1f) * Mathf.Exp(-t * 5.5f);
            float v = 0;
            for (int h = 1; h <= 5; h++) v += Mathf.Sin(Mathf.Tau * 110f * h * t) / (h * h * 0.5f + 0.5f);
            s[i] = v * env * 0.55f + breath[i] * Mathf.Exp(-t * 30f) * 0.5f;
        }
        return Clip(s);
    });

    /// <summary>A handful of Räppli thrown: a short airy whoosh with a hundred tiny paper ticks falling after it, 0.9 s.</summary>
    public static SfxBank RaeppliToss => _toss ??= SfxBank.Build("raeppli toss", 3, 0.9f, 23401, (rng, n) =>
    {
        var air = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(3500f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = air[i] * Mathf.Sin(Mathf.Pi * Mathf.Clamp(t / 0.35f, 0f, 1f)) * 0.5f;
        }
        for (int k = 0; k < 90; k++)
        {
            int start = (int)((0.1 + rng.NextDouble() * 0.75) * Dsp.Rate);
            float f = 3000f + (float)rng.NextDouble() * 4000f, amp = 0.03f + (float)rng.NextDouble() * 0.05f;
            for (int i = start; i < Math.Min(n, start + 160); i++)
            {
                float t = (i - start) / (float)Dsp.Rate;
                s[i] += amp * Mathf.Sin(Mathf.Tau * f * t) * Mathf.Exp(-t * 900f);
            }
        }
        return Clip(s);
    });

    /// <summary>The Morgestraich's drum roll: the Basel drums' march rolling in, loud, then one clipped accent as the lanterns light, 3 s.</summary>
    public static SfxBank MorgestraichRoll => _roll ??= SfxBank.Build("morgestraich roll", 1, 3.2f, 23501, (rng, n) =>
    {
        var s = new float[n];
        // a roll: a stick every 55 ms growing louder, then the accent at 2.6 s
        for (float at = 0.05f; at < 2.55f; at += 0.055f)
        {
            int start = (int)(at * Dsp.Rate);
            float amp = 0.25f + 0.55f * at / 2.55f;
            for (int i = start; i < Math.Min(n, start + (int)(0.09 * Dsp.Rate)); i++)
            {
                float t = (i - start) / (float)Dsp.Rate;
                s[i] += amp * (((float)rng.NextDouble() - 0.5f) * 1.6f + Mathf.Sin(Mathf.Tau * 200f * t) * 0.4f) * Mathf.Exp(-t * 38f);
            }
        }
        int accent = (int)(2.6f * Dsp.Rate);
        for (int i = accent; i < n; i++)
        {
            float t = (i - accent) / (float)Dsp.Rate;
            s[i] += (((float)rng.NextDouble() - 0.5f) * 2f + Mathf.Sin(Mathf.Tau * 90f * t) * 1.2f) * Mathf.Exp(-t * 11f) * 0.9f;
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
