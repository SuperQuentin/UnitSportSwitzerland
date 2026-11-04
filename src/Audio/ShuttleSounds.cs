using Godot;

namespace UnitSport.Audio;

/// <summary>
/// The space shuttle's sounds (#1492): three loops, their gain and pitch set by the flight each frame (the
/// rockets' thrust, the drive's speed, re-entry's heat). Static streams, so the sound player lists them by
/// themselves (<c>audio/sound-player</c>).
/// </summary>
public static class ShuttleSounds
{
    /// <summary>
    /// The main engines: a deep broadband roar (brown noise under a low band) with the crackle of a rocket's
    /// shock diamonds, a few dozen sharp pops a second. Pitched up a little with the thrust.
    /// </summary>
    public static AudioStreamWav Rockets => _rockets ??= Dsp.Loop(3f, 14921, (rng, n) =>
    {
        var noise = Dsp.Noise(rng, n);
        var rumble = Dsp.LowPass(noise, Dsp.Coef(90f));
        var body = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(150f), Dsp.Coef(900f));
        var s = new float[n];
        float pop = 0f;
        for (int i = 0; i < n; i++)
        {
            // crackle: random sharp impulses, each a fast-decaying click
            if (rng.NextDouble() < 40.0 / Dsp.Rate) pop = (float)(rng.NextDouble() * 0.8 + 0.4) * (rng.Next(2) * 2 - 1);
            pop *= 0.93f;
            s[i] = rumble[i] * 4.5f + body[i] * 1.2f + pop * 0.35f;
        }
        return s;
    });
    private static AudioStreamWav? _rockets;

    /// <summary>
    /// The cruise drive: a low hum of three detuned partials that beat slowly against each other, a soft
    /// shimmer over them. Pitched up with the drive's speed.
    /// </summary>
    public static AudioStreamWav Drive => _drive ??= Dsp.Loop(4f, 14922, (rng, n) =>
    {
        var shimmer = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(2500f), Dsp.Coef(5000f));
        var s = new float[n];
        // whole cycles over the 4 s loop (1/4 Hz steps), so the partials join without a phase jump
        const float f0 = 55f, f1 = 55.25f, f2 = 110.5f, f3 = 165f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Dsp.Rate;
            float hum = Mathf.Sin(Mathf.Tau * f0 * t) + 0.8f * Mathf.Sin(Mathf.Tau * f1 * t)
                + 0.45f * Mathf.Sin(Mathf.Tau * f2 * t) + 0.2f * Mathf.Sin(Mathf.Tau * f3 * t);
            float swell = 0.75f + 0.25f * Mathf.Sin(Mathf.Tau * 0.5f * t);
            s[i] = hum * swell + shimmer[i] * 0.6f;
        }
        return s;
    });
    private static AudioStreamWav? _drive;

    /// <summary>
    /// Re-entry's plasma: a hollow, rushing roar (band-passed noise with a slow buffeting), heard through the
    /// airframe. Louder and brighter with the heat.
    /// </summary>
    public static AudioStreamWav Plasma => _plasma ??= Dsp.Loop(3f, 14923, (rng, n) =>
    {
        var roar = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(250f), Dsp.Coef(2200f));
        var low = Dsp.LowPass(Dsp.Noise(rng, n), Dsp.Coef(60f));
        var s = new float[n];
        float buffet = 0f;
        for (int i = 0; i < n; i++)
        {
            // the buffeting: a slowly wandering gain on the roar
            buffet += ((float)rng.NextDouble() - 0.5f) * 0.002f;
            buffet *= 0.9995f;
            s[i] = roar[i] * (1f + buffet * 8f) * 2f + low[i] * 3f;
        }
        return s;
    });
    private static AudioStreamWav? _plasma;
}
