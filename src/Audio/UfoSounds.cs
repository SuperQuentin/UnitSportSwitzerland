using Godot;
using static UnitSport.Audio.Dsp;

namespace UnitSport.Audio;

/// <summary>
/// The flying saucer's sounds (#861), synthesised like every game sound and found by the sound
/// player by themselves (static members): the hum (a pulsing low tone with a theremin wail on top,
/// pitched up with the speed by <c>UfoRig</c>), the beam's shimmering drone, the servo that runs
/// the legs and the ramp, and the thud of the legs taking its weight.
/// </summary>
public static class UfoSounds
{
    /// <summary>The engine: 70 Hz and its harmonics, a slow swell, a gentle vibrato, a high wail. Loops.</summary>
    public static AudioStreamWav Hum => _hum ??= Loop(4f, 861, (rng, n) =>
    {
        var s = new float[n];
        double p1 = 0, p2 = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            // 0.5 Hz and 6 Hz: whole cycles in the 4 s loop, so the join lands in step
            float vib = 1f + 0.025f * Mathf.Sin(Mathf.Tau * 6f * t);
            float swell = 0.8f + 0.2f * Mathf.Sin(Mathf.Tau * 0.5f * t);
            p1 += Mathf.Tau * 70f * vib / Rate;
            p2 += Mathf.Tau * (880f + 60f * Mathf.Sin(Mathf.Tau * 0.25f * t)) * vib / Rate;
            float body = Mathf.Sin((float)p1) + 0.45f * Mathf.Sin((float)(2 * p1)) + 0.2f * Mathf.Sin((float)(3 * p1));
            s[i] = body * swell * 0.7f + Mathf.Sin((float)p2) * 0.08f;
        }
        return s;
    });
    private static AudioStreamWav? _hum;

    /// <summary>The beam: a bright chord trembling at 8 Hz over a soft rush of air. Loops.</summary>
    public static AudioStreamWav BeamDrone => _beam ??= Loop(3f, 862, (rng, n) =>
    {
        var air = BandPass(Noise(rng, n), 0.02f, 0.15f);
        var s = new float[n];
        double a = 0, b = 0, c = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float trem = 0.65f + 0.35f * Mathf.Sin(Mathf.Tau * 8f * t);
            a += Mathf.Tau * 330f / Rate; b += Mathf.Tau * 495f / Rate; c += Mathf.Tau * 742.5f / Rate;
            s[i] = (Mathf.Sin((float)a) + 0.6f * Mathf.Sin((float)b) + 0.3f * Mathf.Sin((float)c)) * trem * 0.5f + air[i] * 1.5f;
        }
        return s;
    });
    private static AudioStreamWav? _beam;

    /// <summary>The servo of the legs and the ramp: an electric whine with a mechanical buzz. Loops while they move.</summary>
    public static AudioStreamWav Servo => _servo ??= Loop(1f, 863, (rng, n) =>
    {
        var grit = HighPass(Noise(rng, n), 0.3f);
        var s = new float[n];
        double p = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            p += Mathf.Tau * (420f + 12f * Mathf.Sin(Mathf.Tau * 3f * t)) / Rate;
            float saw = (float)(p / Mathf.Tau % 1.0) * 2f - 1f;
            float buzz = Mathf.Sin(Mathf.Tau * 40f * t) > 0.6f ? 0.25f : 0f;
            s[i] = saw * 0.35f + Mathf.Sin((float)p * 2f) * 0.2f + grit[i] * (0.15f + buzz);
        }
        return s;
    });
    private static AudioStreamWav? _servo;

    /// <summary>Touchdown: the legs taking the saucer's weight, a deep thump and a metal clank.</summary>
    public static SfxBank LegsBank => _legs ??= SfxBank.Build("ufo legs", 4, 0.7f, 864, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.1f;
        float f0 = 70f * J(), f1 = 32f * J(), clank = 610f * J();
        var grit = LowPass(Noise(rng, n), 0.08f);
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            phase += Mathf.Tau * Mathf.Lerp(f0, f1, Mathf.Min(1f, t / 0.3f)) / Rate;
            s[i] = Mathf.Sin((float)phase) * Mathf.Exp(-7f * t)
                + Mathf.Sin(Mathf.Tau * clank * t) * 0.25f * Mathf.Exp(-14f * t)
                + Mathf.Sin(Mathf.Tau * clank * 1.48f * t) * 0.15f * Mathf.Exp(-18f * t)
                + grit[i] * 2f * Mathf.Exp(-20f * t);
        }
        return s;
    });
    private static SfxBank? _legs;
}
