using Godot;
using static UnitSport.Audio.Dsp;

namespace UnitSport.Audio;

/// <summary>
/// Every sound effect in the game, synthesised at startup from noise, filters and envelopes.
///
/// <para>
/// There are no audio files in the project, and none are needed for this palette: tyre
/// roar, the hiss of a ski edge, a scrape, a footstep and a thump are all shaped noise, which is
/// exactly what they are physically too. Generating them keeps the repository asset-free, lets
/// the character of a sound be tuned in code next to the physics that drives it, and suits a
/// PS1-styled world, where a slightly synthetic sound reads as intended. A recorded sample can
/// replace any one of these later by swapping what the property returns.
/// </para>
///
/// <para>
/// 22.05 kHz mono 16-bit: plenty for noise-based sounds, and the whole bank is a few hundred KB.
/// Built once, lazily, and shared.
/// </para>
/// </summary>
public static class SfxSynth
{
    public const int Rate = Dsp.Rate;

    private static AudioStreamWav? _hiss, _tyre, _scrape;
    private static SfxBank? _stepsBank, _landingBank, _whooshBank, _tickBank, _impactBank, _chimeBank, _boomBank;

    /// <summary>Looping edge hiss for skis: bright, high-passed noise.</summary>
    public static AudioStreamWav Hiss => _hiss ??= Loop(2.0f, 12, (rng, n) =>
    {
        var s = HighPass(Noise(rng, n), 0.25f);
        var wobble = LowPass(Noise(rng, n), 0.004f);
        for (int i = 0; i < n; i++) s[i] *= 0.7f + wobble[i] * 12f;
        return s;
    });

    /// <summary>Looping tyre roar: road noise with a faint tread whine in it.</summary>
    public static AudioStreamWav Tyre => _tyre ??= Loop(2.0f, 13, (rng, n) =>
    {
        var s = BandPass(Noise(rng, n), 0.03f, 0.2f);
        for (int i = 0; i < n; i++)
            s[i] = s[i] * 2.2f + 0.08f * Mathf.Sin(Mathf.Tau * 170f * i / Rate);   // 170 Hz fits 2 s exactly
        return s;
    });

    /// <summary>Looping ground scrape, for a slide.</summary>
    public static AudioStreamWav Scrape => _scrape ??= Loop(1.5f, 14, (rng, n) =>
    {
        var s = BandPass(Noise(rng, n), 0.06f, 0.35f);
        var grit = Noise(rng, n);
        for (int i = 0; i < n; i++) s[i] = s[i] * 2f + (grit[i] > 0.97f ? 0.5f : 0f);
        return s;
    });

    /// <summary>Four footstep variants, so a run does not sound like a metronome.</summary>
    public static AudioStreamWav[] Steps => StepsBank.Variants;

    /// <summary>Footstep variants with per-variant jitter.</summary>
    public static SfxBank StepsBank => _stepsBank ??= SfxBank.Build("steps", 6, 0.14f, 20, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.12f;
        float cut = (0.18f + 0.05f * (float)rng.NextDouble() * 3f) * J();
        float d1 = 45f * J(), d2 = 14f * J();
        var s = LowPass(Noise(rng, n), cut);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            // a heel-strike click then the softer roll of the sole
            s[i] = s[i] * 3f * (Mathf.Exp(-d1 * t) + 0.35f * Mathf.Exp(-d2 * t));
            if (i < 30) s[i] += (float)(rng.NextDouble() * 2 - 1) * 0.5f * (1 - i / 30f);
        }
        return s;
    });

    /// <summary>Landing thump: a falling low sine under a burst of grit.</summary>
    public static AudioStreamWav Landing => LandingBank.Variants[0];

    public static SfxBank LandingBank => _landingBank ??= SfxBank.Build("landing", 6, 0.4f, 30, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.12f;
        float f0 = 85f * J(), f1 = 38f * J(), d1 = 11f * J(), d2 = 22f * J();
        var grit = LowPass(Noise(rng, n), 0.12f);
        var s = new float[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            phase += Mathf.Tau * Mathf.Lerp(f0, f1, Mathf.Min(1f, t / 0.25f)) / Rate;
            s[i] = Mathf.Sin(phase) * Mathf.Exp(-d1 * t) * 0.9f + grit[i] * 3f * Mathf.Exp(-d2 * t);
        }
        return s;
    });

    /// <summary>A rush of air: jumps and wall kicks.</summary>
    public static AudioStreamWav Whoosh => WhooshBank.Variants[0];

    public static SfxBank WhooshBank => _whooshBank ??= SfxBank.Build("whoosh", 6, 0.38f, 31, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.12f;
        float lo = 0.04f * J(), hi = 0.3f * J(), sk = 0.6f * J();
        var s = BandPass(Noise(rng, n), lo, hi);
        for (int i = 0; i < n; i++)
        {
            float x = (float)i / n;
            float env = Mathf.Sin(Mathf.Pi * Mathf.Pow(x, sk));
            s[i] *= 3f * env * env;
        }
        return s;
    });

    /// <summary>One click of a freewheel pawl. Repeated at a rate set by wheel speed.</summary>
    public static AudioStreamWav Tick => TickBank.Variants[0];

    public static SfxBank TickBank => _tickBank ??= SfxBank.Build("tick", 6, 0.02f, 32, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.12f;
        float hp = 0.4f * J(), dk = 350f * J();
        var s = HighPass(Noise(rng, n), hp);
        for (int i = 0; i < n; i++) s[i] *= 1.4f * Mathf.Exp(-dk * i / Rate);
        return s;
    });

    /// <summary>
    /// Helicopter: the blade "whop" (pressure pulses at the blade-pass rate, 4.5 Hz here, nine to
    /// the two-second loop so it repeats seamlessly) over a thin turbine whine. Pitch-scaled by
    /// rotor spool, which drags both up together as it would.
    /// </summary>
    public static AudioStreamWav Rotor => _rotor ??= Loop(2.0f, 40, (rng, n) =>
    {
        var body = LowPass(Noise(rng, n), 0.05f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float phase = t * 4.5f % 1f;
            float whop = Mathf.Exp(-phase * 9f);                     // a sharp pulse per blade pass
            s[i] = body[i] * 8f * (0.25f + whop)
                 + Mathf.Sin(Mathf.Tau * 42f * t) * whop * 0.5f      // the thump's low body
                 + Mathf.Sin(Mathf.Tau * 1100f * t) * 0.025f;         // turbine
        }
        return s;
    });
    private static AudioStreamWav? _rotor;

    /// <summary>
    /// Piston engine and propeller: a buzzy 80 Hz fundamental (whole cycles in the one-second
    /// loop) soft-clipped for its harmonics, with exhaust roughness on top. Pitch-scaled by the
    /// throttle, which is what an engine note does.
    /// </summary>
    public static AudioStreamWav Engine => _engine ??= Loop(1.0f, 41, (rng, n) =>
    {
        var rough = LowPass(Noise(rng, n), 0.2f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float tone = Mathf.Sin(Mathf.Tau * 80f * t) + 0.5f * Mathf.Sin(Mathf.Tau * 160f * t)
                       + 0.3f * Mathf.Sin(Mathf.Tau * 40f * t);
            s[i] = Mathf.Tanh(tone * 1.6f) + rough[i] * 2.5f;
        }
        return s;
    });
    private static AudioStreamWav? _engine;

    /// <summary>
    /// An explosion: a noise blast with a hard attack, a falling sub-bass thump under it, and a
    /// long rumbling tail. Played as a 3D sound, so distance does the rest.
    /// </summary>
    public static AudioStreamWav Boom => BoomBank.Variants[0];

    public static SfxBank BoomBank => _boomBank ??= SfxBank.Build("boom", 6, 2.6f, 42, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.12f;
        float f0 = 70f * J(), f1 = 22f * J(), d1 = 9f * J(), d2 = 3.5f * J(), d3 = 1.6f * J();
        var blast = LowPass(Noise(rng, n), 0.25f);
        var rumble = LowPass(Noise(rng, n), 0.012f);
        var s = new float[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            phase += Mathf.Tau * Mathf.Lerp(f0, f1, Mathf.Min(1f, t / 0.8f)) / Rate;
            s[i] = blast[i] * 3.5f * Mathf.Exp(-d1 * t)
                 + Mathf.Sin(phase) * 1.2f * Mathf.Exp(-d2 * t)
                 + rumble[i] * 14f * Mathf.Exp(-d3 * t);
        }
        return s;
    });

    /// <summary>
    /// One round from an aircraft gun: a hard, bright crack of noise over a short low thump.
    /// Short enough (0.14 s) that 14 a second overlap into a rattle rather than a smear.
    /// </summary>
    public static SfxBank GunBank => _gunBank ??= SfxBank.Build("gun", 6, 0.14f, 43, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.1f;
        float crack = 38f * J(), thump = 22f * J(), f = 95f * J();
        var s = HighPass(Noise(rng, n), 0.08f);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            s[i] = s[i] * 2.2f * Mathf.Exp(-crack * t)
                 + Mathf.Sin(Mathf.Tau * f * t) * 0.9f * Mathf.Exp(-thump * t);
        }
        return s;
    });

    private static SfxBank? _gunBank;

    /// <summary>
    /// The reward sound for a clean landing or trick: two bright bell partials a fifth apart,
    /// with a quick attack. Pure tones are the one thing here that is not noise, which is why
    /// it cuts through everything else.
    /// </summary>
    public static AudioStreamWav Chime => ChimeBank.Variants[0];

    public static SfxBank ChimeBank => _chimeBank ??= SfxBank.Build("chime", 6, 0.5f, 34, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.03f;
        float fa = 880f * J(), fb = 1320f * J(), dk = 7f * J();
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float env = Mathf.Min(1f, t * 400f) * Mathf.Exp(-dk * t);
            // the upper note enters a beat later, so it reads as a rising "ding-ding"
            float second = t > 0.07f ? Mathf.Exp(-dk * (t - 0.07f)) : 0f;
            s[i] = env * Mathf.Sin(Mathf.Tau * fa * t) * 0.6f
                 + second * Mathf.Min(1f, (t - 0.07f) * 400f) * Mathf.Sin(Mathf.Tau * fb * t) * 0.5f;
        }
        return s;
    });

    /// <summary>A crash into something solid.</summary>
    public static AudioStreamWav Impact => ImpactBank.Variants[0];

    public static SfxBank ImpactBank => _impactBank ??= SfxBank.Build("impact", 6, 0.3f, 33, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.12f;
        float cut = 0.3f * J(), d1 = 16f * J(), d2 = 14f * J(), f = 60f * J();
        var s = LowPass(Noise(rng, n), cut);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            s[i] = s[i] * 3f * Mathf.Exp(-d1 * t) + Mathf.Sin(Mathf.Tau * f * t) * Mathf.Exp(-d2 * t) * 0.7f;
        }
        return s;
    });
}
