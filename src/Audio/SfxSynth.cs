using Godot;

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
    public const int Rate = 22050;

    private static AudioStreamWav? _hiss, _tyre, _scrape, _landing, _whoosh, _tick, _impact;
    private static AudioStreamWav[]? _steps;

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
    public static AudioStreamWav[] Steps => _steps ??= Enumerable.Range(0, 4).Select(v => OneShot(0.14f, 20 + v, (rng, n) =>
    {
        var s = LowPass(Noise(rng, n), 0.18f + 0.05f * v);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            // a heel-strike click then the softer roll of the sole
            s[i] = s[i] * 3f * (Mathf.Exp(-45f * t) + 0.35f * Mathf.Exp(-14f * t));
            if (i < 30) s[i] += (float)(rng.NextDouble() * 2 - 1) * 0.5f * (1 - i / 30f);
        }
        return s;
    })).ToArray();

    /// <summary>Landing thump: a falling low sine under a burst of grit.</summary>
    public static AudioStreamWav Landing => _landing ??= OneShot(0.4f, 30, (rng, n) =>
    {
        var grit = LowPass(Noise(rng, n), 0.12f);
        var s = new float[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            phase += Mathf.Tau * Mathf.Lerp(85f, 38f, Mathf.Min(1f, t / 0.25f)) / Rate;
            s[i] = Mathf.Sin(phase) * Mathf.Exp(-11f * t) * 0.9f + grit[i] * 3f * Mathf.Exp(-22f * t);
        }
        return s;
    });

    /// <summary>A rush of air: jumps and wall kicks.</summary>
    public static AudioStreamWav Whoosh => _whoosh ??= OneShot(0.38f, 31, (rng, n) =>
    {
        var s = BandPass(Noise(rng, n), 0.04f, 0.3f);
        for (int i = 0; i < n; i++)
        {
            float x = (float)i / n;
            float env = Mathf.Sin(Mathf.Pi * Mathf.Pow(x, 0.6f));
            s[i] *= 3f * env * env;
        }
        return s;
    });

    /// <summary>One click of a freewheel pawl. Repeated at a rate set by wheel speed.</summary>
    public static AudioStreamWav Tick => _tick ??= OneShot(0.02f, 32, (rng, n) =>
    {
        var s = HighPass(Noise(rng, n), 0.4f);
        for (int i = 0; i < n; i++) s[i] *= 1.4f * Mathf.Exp(-350f * i / Rate);
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
    public static AudioStreamWav Boom => _boom ??= OneShot(2.6f, 42, (rng, n) =>
    {
        var blast = LowPass(Noise(rng, n), 0.25f);
        var rumble = LowPass(Noise(rng, n), 0.012f);
        var s = new float[n];
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            phase += Mathf.Tau * Mathf.Lerp(70f, 22f, Mathf.Min(1f, t / 0.8f)) / Rate;
            s[i] = blast[i] * 3.5f * Mathf.Exp(-9f * t)
                 + Mathf.Sin(phase) * 1.2f * Mathf.Exp(-3.5f * t)
                 + rumble[i] * 14f * Mathf.Exp(-1.6f * t);
        }
        return s;
    });
    private static AudioStreamWav? _boom;

    /// <summary>
    /// The reward sound for a clean landing or trick: two bright bell partials a fifth apart,
    /// with a quick attack. Pure tones are the one thing here that is not noise, which is why
    /// it cuts through everything else.
    /// </summary>
    public static AudioStreamWav Chime => _chime ??= OneShot(0.5f, 34, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float env = Mathf.Min(1f, t * 400f) * Mathf.Exp(-7f * t);
            // the upper note enters a beat later, so it reads as a rising "ding-ding"
            float second = t > 0.07f ? Mathf.Exp(-7f * (t - 0.07f)) : 0f;
            s[i] = env * Mathf.Sin(Mathf.Tau * 880f * t) * 0.6f
                 + second * Mathf.Min(1f, (t - 0.07f) * 400f) * Mathf.Sin(Mathf.Tau * 1320f * t) * 0.5f;
        }
        return s;
    });
    private static AudioStreamWav? _chime;

    /// <summary>A crash into something solid.</summary>
    public static AudioStreamWav Impact => _impact ??= OneShot(0.3f, 33, (rng, n) =>
    {
        var s = LowPass(Noise(rng, n), 0.3f);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            s[i] = s[i] * 3f * Mathf.Exp(-16f * t) + Mathf.Sin(Mathf.Tau * 60f * t) * Mathf.Exp(-14f * t) * 0.7f;
        }
        return s;
    });

    // ------------------------------------------------------------------------------------
    // building blocks
    // ------------------------------------------------------------------------------------

    private static float[] Noise(Random rng, int n)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = (float)(rng.NextDouble() * 2 - 1);
        return s;
    }

    /// <summary>One-pole low-pass; <paramref name="a"/> is the per-sample coefficient (0..1).</summary>
    private static float[] LowPass(float[] x, float a)
    {
        var y = new float[x.Length];
        float state = 0;
        // run twice so a loop starts from a settled filter rather than from silence
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < x.Length; i++)
            {
                state += a * (x[i] - state);
                y[i] = state;
            }
        return y;
    }

    private static float[] HighPass(float[] x, float a)
    {
        var low = LowPass(x, a);
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i] - low[i];
        return y;
    }

    private static float[] BandPass(float[] x, float lowCut, float highCut)
    {
        var upper = LowPass(x, highCut);
        var lower = LowPass(x, lowCut);
        for (int i = 0; i < x.Length; i++) upper[i] -= lower[i];
        return upper;
    }

    /// <summary>
    /// A seamless loop: the synthesised tail is crossfaded into the head, so the join has no
    /// click — random noise does not otherwise end where it began.
    /// </summary>
    private static AudioStreamWav Loop(float seconds, int seed, Func<Random, int, float[]> make)
    {
        int fade = Rate / 10;
        int n = (int)(seconds * Rate);
        var raw = make(new Random(seed), n + fade);
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = raw[i];
        for (int i = 0; i < fade; i++)
        {
            float t = (float)i / fade;
            s[i] = raw[n + i] * (1 - t) + raw[i] * t;
        }
        var wav = Encode(Normalise(s, 0.8f));
        wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        wav.LoopBegin = 0;
        wav.LoopEnd = n;
        return wav;
    }

    private static AudioStreamWav OneShot(float seconds, int seed, Func<Random, int, float[]> make)
    {
        int n = (int)(seconds * Rate);
        var s = make(new Random(seed), n);
        // a short fade-out, or a cut-off tail clicks
        int fade = Math.Min(n / 4, Rate / 100);
        for (int i = 0; i < fade; i++) s[n - 1 - i] *= (float)i / fade;
        return Encode(Normalise(s, 0.9f));
    }

    private static float[] Normalise(float[] s, float peak)
    {
        float max = 1e-6f;
        foreach (float v in s) max = Math.Max(max, Math.Abs(v));
        for (int i = 0; i < s.Length; i++) s[i] *= peak / max;
        return s;
    }

    private static AudioStreamWav Encode(float[] s)
    {
        var bytes = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            short v = (short)Math.Clamp((int)(s[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return new AudioStreamWav
        {
            Data = bytes,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Rate,
            Stereo = false,
        };
    }
}
