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
    private static SfxBank? _stepsBank, _landingBank, _whooshBank, _tickBank, _impactBank, _chimeBank, _boomBank, _gulpBank, _crunchBank;

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

    /// <summary>
    /// Looping tyre squeal: a few stick-slip partials with a slow wobble in pitch and level, over a
    /// thin band of rubbery noise. Every tone is a whole number of Hz in the two-second loop, so the
    /// crossfaded seam is in phase and does not thin out; pitch is then set by the player.
    /// </summary>
    public static AudioStreamWav Squeal => _squeal ??= Loop(2.0f, 15, (rng, n) =>
    {
        var grit = BandPass(Noise(rng, n), 0.12f, 0.3f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            // slip-stick: the note trembles around 1.75 kHz, never quite steady
            float wob = 0.012f * Mathf.Sin(Mathf.Tau * 6f * t) + 0.007f * Mathf.Sin(Mathf.Tau * 11f * t);
            float amp = 0.75f + 0.25f * Mathf.Sin(Mathf.Tau * 9f * t + 1f);
            float a = Mathf.Sin(Mathf.Tau * 1750f * t + 40f * wob);
            float b = 0.6f * Mathf.Sin(Mathf.Tau * 2310f * t + 55f * wob + 0.7f);
            float c = 0.3f * Mathf.Sin(Mathf.Tau * 3530f * t + 90f * wob + 1.9f);
            s[i] = (a + b + c) * amp * 0.5f + grit[i] * 1.2f;
        }
        return s;
    });
    private static AudioStreamWav? _squeal;

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

    private static SfxBank? _blast;

    /// <summary>A shotgun report (the bird hunt, relayed as an item event): a sharp crack, a noise body and a low thump, then a short tail.</summary>
    public static SfxBank Shotgun => _blast ??= SfxBank.Build("shotgun", 6, 1.2f, 71, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.1f;
        var crack = HighPass(Noise(rng, n), 0.3f);
        var body = LowPass(Noise(rng, n), 0.08f * J());
        float d1 = 70f * J(), d2 = 11f * J(), d3 = 2.5f * J(), f = 55f * J();
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            s[i] = crack[i] * 2.5f * Mathf.Exp(-d1 * t) + body[i] * 7f * Mathf.Exp(-d2 * t)
                 + Mathf.Sin(Mathf.Tau * f * t) * 0.9f * Mathf.Exp(-14f * t)
                 + body[i] * 2.5f * Mathf.Exp(-d3 * t) * Mathf.Min(1f, t * 20f);
        }
        return s;
    });

    private static SfxBank? _pump;

    /// <summary>A pump-action cycle: the slide racking back and the shell chambering, two metallic clacks 0.16 s apart.</summary>
    public static SfxBank Pump => _pump ??= SfxBank.Build("pump", 4, 0.34f, 72, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.1f;
        var hi = HighPass(Noise(rng, n), 0.25f);
        var mid = LowPass(HighPass(Noise(rng, n), 0.05f), 0.35f);
        float gap = 0.16f * J(), f = 190f * J();
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float t2 = t - gap;
            float a = Mathf.Exp(-90f * t) * 0.9f + Mathf.Sin(Mathf.Tau * f * t) * Mathf.Exp(-45f * t) * 0.5f;
            float b = t2 > 0f ? Mathf.Exp(-110f * t2) * 1.1f + Mathf.Sin(Mathf.Tau * f * 1.4f * t2) * Mathf.Exp(-55f * t2) * 0.45f : 0f;
            s[i] = (hi[i] * 0.6f + mid[i] * 1.4f) * (a + b);
        }
        return s;
    });

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

    /// <summary>Three swallows: short low blips that fall in pitch, over a wet band of noise. Drinking.</summary>
    public static SfxBank GulpBank => _gulpBank ??= SfxBank.Build("gulp", 4, 0.6f, 35, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.1f;
        var s = BandPass(Noise(rng, n), 0.02f, 0.12f);
        float f0 = 190f * J();
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float local = t % 0.19f;   // one swallow per 0.19 s
            bool on = t < 0.57f;
            float env = on ? Mathf.Min(1f, local * 250f) * Mathf.Exp(-local * 22f) : 0f;
            float f = f0 * (1f - local * 2.2f);
            s[i] = env * (Mathf.Sin(Mathf.Tau * f * local) * 0.8f + s[i] * 0.9f);
        }
        return s;
    });

    /// <summary>A bite of something dry: a few bursts of bright, fast-decaying noise.</summary>
    public static SfxBank CrunchBank => _crunchBank ??= SfxBank.Build("crunch", 4, 0.5f, 36, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.15f;
        var s = HighPass(Noise(rng, n), 0.25f * J());
        float[] at = { 0f, 0.09f * J(), 0.2f * J(), 0.31f * J() };
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate, env = 0f;
            foreach (float a in at) if (t >= a) env += Mathf.Exp(-(t - a) * 55f) * (0.5f + 0.5f * (float)rng.NextDouble());
            s[i] *= 1.6f * env;
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

    /// <summary>A door opening: the latch clacks, then the hinge creaks as it swings.</summary>
    public static SfxBank DoorOpenBank => _doorOpenBank ??= SfxBank.Build("door_open", 6, 0.9f, 51, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.15f;
        float f0 = 330f * J(), f1 = 520f * J(), start = 0.09f * J(), length = 0.55f * J();
        var click = BandPass(Noise(rng, n), 0.15f, 0.6f);
        var rasp = Noise(rng, n);
        var s = new float[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            // the latch: two quick metal ticks
            s[i] = click[i] * 2.5f * (Mathf.Exp(-120f * t) + 0.6f * (t > 0.035f ? Mathf.Exp(-140f * (t - 0.035f)) : 0f));
            // the hinge: a stick-slip tone wandering up in pitch, rough at the edges
            float u = (t - start) / length;
            if (u > 0 && u < 1)
            {
                float f = Mathf.Lerp(f0, f1, u) * (1f + 0.04f * Mathf.Sin(Mathf.Tau * 23f * t));
                phase += Mathf.Tau * f / Rate;
                float env = Mathf.Sin(Mathf.Pi * u) * 0.35f;
                float saw = 2f * (phase / Mathf.Tau - Mathf.Floor(phase / Mathf.Tau + 0.5f));
                s[i] += (saw * 0.6f + rasp[i] * 0.4f) * env * (0.7f + 0.3f * Mathf.Sin(Mathf.Tau * 41f * t));
            }
        }
        return LowPass(s, 0.45f);
    });

    /// <summary>A door shutting: a wooden thud and the latch catching.</summary>
    public static SfxBank DoorCloseBank => _doorCloseBank ??= SfxBank.Build("door_close", 6, 0.5f, 52, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.12f;
        float f = 72f * J(), d = 18f * J(), latch = 0.05f * J();
        var body = LowPass(Noise(rng, n), 0.08f);
        var click = BandPass(Noise(rng, n), 0.2f, 0.7f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            s[i] = Mathf.Sin(Mathf.Tau * f * t) * Mathf.Exp(-d * t) * 0.9f + body[i] * 4f * Mathf.Exp(-30f * t);
            if (t > latch) s[i] += click[i] * 1.6f * Mathf.Exp(-150f * (t - latch));
        }
        return s;
    });

    /// <summary>
    /// The street heard through a wall or a doorway: a low rumble of distant traffic and wind,
    /// looping. Filtered further by whoever plays it, according to how open the way out is.
    /// </summary>
    public static AudioStreamWav Street => _street ??= Loop(3.0f, 53, (rng, n) =>
    {
        var s = BandPass(Noise(rng, n), 0.004f, 0.08f);
        var swell = LowPass(Noise(rng, n), 0.0008f);
        for (int i = 0; i < n; i++) s[i] *= 3f * (0.6f + swell[i] * 30f);
        return s;
    });

    private static SfxBank? _doorOpenBank, _doorCloseBank;
    private static AudioStreamWav? _street;
}
