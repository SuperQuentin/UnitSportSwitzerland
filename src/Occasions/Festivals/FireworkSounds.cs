using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// The fireworks' synthesised sounds (#1269 part 2), each baked once: the mortar's thump and the shell's hissing climb, the burst's
/// bang with its rolling echo off the hills, the crackle of crossettes and willows. They are static banks, so the sound player lists
/// them by itself. <see cref="Fireworks"/> plays them delayed by distance / 343 m/s.
/// </summary>
public static class FireworkSounds
{
    private static SfxBank? _bang, _launch, _crackle;

    /// <summary>A shell bursting: a flat crack, a body of low thump, and a tail of echo rolling back off the valley.</summary>
    public static SfxBank Bang => _bang ??= SfxBank.Build("firework bang", 4, 2.8f, 17011, (rng, n) =>
    {
        float f0 = 70f + (float)rng.NextDouble() * 30f, tail = 1.4f + (float)rng.NextDouble() * 0.6f;
        var crack = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(900f));
        var rumble = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(240f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float thump = Mathf.Sin(Mathf.Tau * (f0 * (0.45f + 0.55f * Mathf.Exp(-t * 6f))) * t) * Mathf.Exp(-t * 7f);
            float snap = crack[i] * Mathf.Exp(-t * 38f) * Mathf.Min(1f, t * 4000f);
            // the echo: the rumble swells again a little after the bang, as a valley wall sends it back
            float echo = rumble[i] * (Mathf.Exp(-t * 3.2f / tail) * 0.8f + Mathf.Exp(-Mathf.Pow((t - 0.55f) * 4f, 2f)) * 0.5f);
            s[i] = thump * 1.1f + snap * 1.2f + echo * 2.2f;
        }
        return Clip(s);
    });

    /// <summary>A mortar firing and the shell's hiss as it climbs: a dull thump, then a rising band of noise.</summary>
    public static SfxBank Launch => _launch ??= SfxBank.Build("firework launch", 3, 1.9f, 17101, (rng, n) =>
    {
        var hiss = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(1400f), Dsp.Coef(5200f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float pop = Mathf.Sin(Mathf.Tau * 95f * t) * Mathf.Exp(-t * 22f) * 1.3f;
            // the climb: loudest at the start, thinning as the shell goes away
            float env = Mathf.Min(1f, t * 30f) * Mathf.Exp(-t * 1.6f) * (t < 1.8f ? 1f : 0f);
            s[i] = pop + hiss[i] * env * 0.9f;
        }
        return Clip(s);
    });

    /// <summary>The crackle of a crossette or a willow's falling stars: a hundred small clicks that thin out.</summary>
    public static SfxBank Crackle => _crackle ??= SfxBank.Build("firework crackle", 3, 2.2f, 17201, (rng, n) =>
    {
        var s = new float[n];
        int clicks = 90 + rng.Next(40);
        for (int c = 0; c < clicks; c++)
        {
            // dense at first, then fewer: the start time is squared so the early part gets the most
            float u = (float)rng.NextDouble();
            int at = (int)(u * u * (n - Dsp.Rate / 20));
            float amp = 0.35f + (float)rng.NextDouble() * 0.65f;
            int len = Dsp.Rate / 160;
            for (int i = 0; i < len && at + i < n; i++)
                s[at + i] += ((float)rng.NextDouble() * 2f - 1f) * amp * Mathf.Exp(-i * 9f / len);
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
