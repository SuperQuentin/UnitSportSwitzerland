using Godot;
using UnitSport.Terrain.Construction;

namespace UnitSport.Audio;

// Plain C# with Godot maths only (no AudioStream): linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// The sounds of a building site (#617), synthesised like <see cref="SfxSynth"/>: mono floats at
/// <see cref="Rate"/>, every sample inside [-1, 1] (peak 0.9), deterministic per variant, so
/// <c>SiteSounds</c> can encode them into banks and a test can check the numbers. Nothing here
/// calls the engine; <see cref="Clips"/> bakes a kind once, on first use, from any thread.
///
/// <para>
/// Every generator takes a variant number and draws its own parameter jitter from it before any
/// noise, so the variants differ in shape (the pitch of a ring, the length of a cut), not only
/// in grain, the <see cref="SfxBank"/> rule.
/// </para>
/// </summary>
public static class SiteSfx
{
    /// <summary>The rate every synthesised sound runs at (<c>Dsp.Rate</c>).</summary>
    public const int Rate = 22050;

    public const int HammerVariants = 6, GrinderVariants = 3, VibratorVariants = 3, BeeperVariants = 2,
        RadioVariants = 2, CraneVariants = 2;

    /// <summary>How many variants a kind is baked in.</summary>
    public static int Variants(SiteSoundKind kind) => kind switch
    {
        SiteSoundKind.Hammer => HammerVariants,
        SiteSoundKind.Grinder => GrinderVariants,
        SiteSoundKind.Vibrator => VibratorVariants,
        SiteSoundKind.Beeper => BeeperVariants,
        SiteSoundKind.Radio => RadioVariants,
        _ => CraneVariants,
    };

    private static readonly float[][]?[] Baked = new float[6][][];
    private static readonly object Lock = new();

    /// <summary>A kind's variants, baked on first use and kept: ~1.2 M samples for all six kinds.</summary>
    public static float[][] Clips(SiteSoundKind kind)
    {
        lock (Lock)
        {
            if (Baked[(int)kind] is { } done) return done;
            var clips = new float[Variants(kind)][];
            for (int v = 0; v < clips.Length; v++)
                clips[v] = kind switch
                {
                    SiteSoundKind.Hammer => Hammer(v),
                    SiteSoundKind.Grinder => Grinder(v),
                    SiteSoundKind.Vibrator => Vibrator(v),
                    SiteSoundKind.Beeper => Beeper(3 + v, v),
                    SiteSoundKind.Radio => Radio(v),
                    _ => CraneMotor(v),
                };
            return Baked[(int)kind] = clips;
        }
    }

    // ----- hammer ---------------------------------------------------------------------------

    /// <summary>
    /// One hammer blow: even variants strike steel (a bright click over a ringing, inharmonic
    /// partial set that dies in a tenth of a second), odd ones timber (a dull knock and a thud).
    /// </summary>
    public static float[] Hammer(int variant)
    {
        var rng = new Random(1700 + variant * 131);
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.15f;
        bool steel = variant % 2 == 0;
        int n = (int)((steel ? 0.5f : 0.3f) * Rate);
        var s = new float[n];
        if (steel)
        {
            var click = HighPass(Noise(rng, n), Coef(1800f));
            float f0 = 950f * J() + 250f * (float)rng.NextDouble();
            float[] ratios = { 1f, 2.76f, 5.4f, 8.93f };
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float ring = 0f;
                for (int k = 0; k < ratios.Length; k++)
                    ring += Mathf.Sin(Mathf.Tau * f0 * ratios[k] * t) * Mathf.Exp(-t * (14f + 11f * k)) / (1f + k * 0.6f);
                s[i] = click[i] * 1.6f * Mathf.Exp(-t * 320f) + ring * 0.7f + Mathf.Sin(Mathf.Tau * 150f * J() * t) * 0.5f * Mathf.Exp(-t * 70f);
            }
        }
        else
        {
            var body = LowPass(Noise(rng, n), Coef(1700f));
            float knock = 330f * J() + 160f * (float)rng.NextDouble();
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                s[i] = body[i] * 3f * Mathf.Exp(-t * 170f)
                    + Mathf.Sin(Mathf.Tau * knock * t * (1f - t)) * 0.8f * Mathf.Exp(-t * 52f)
                    + Mathf.Sin(Mathf.Tau * 95f * t) * 0.4f * Mathf.Exp(-t * 40f);
            }
        }
        return Finish(s);
    }

    // ----- angle grinder --------------------------------------------------------------------

    /// <summary>
    /// A cut with an angle grinder, 2.2 to 3.8 s: the disc's bright noise rising as it bites,
    /// wobbling with the feed, a motor whine under it, a few sparks ticking, then it spools down.
    /// </summary>
    public static float[] Grinder(int variant)
    {
        var rng = new Random(2100 + variant * 211);
        float seconds = 2.2f + variant * 0.8f;
        int n = (int)(seconds * Rate);
        var hiss = BandPass(Noise(rng, n), Coef(1800f), Coef(7500f));
        float whine = 470f + 60f * (float)rng.NextDouble();
        float wobbleHz = 4.5f + variant * 1.3f;
        var s = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float env = Mathf.Min(1f, t / 0.18f) * Mathf.Min(1f, (seconds - t) / 0.35f);
            float wob = 0.72f + 0.28f * Mathf.Sin(Mathf.Tau * wobbleHz * t);
            float f = whine * (0.9f + 0.1f * env);
            phase += f / Rate;
            float tone = 0f;
            for (int k = 1; k <= 4; k++) tone += Mathf.Sin(Mathf.Tau * phase * k) / k;
            s[i] = (hiss[i] * 3f * wob + tone * 0.3f) * env;
        }
        // sparks: tiny ticks scattered through the cut
        for (int k = 0; k < (int)(seconds * 18); k++)
        {
            int at = rng.Next(n - 400);
            float amp = 0.3f + 0.5f * (float)rng.NextDouble();
            for (int i = 0; i < 300; i++)
                s[at + i] += (float)(rng.NextDouble() * 2 - 1) * amp * Mathf.Exp(-i / 40f);
        }
        return Finish(s);
    }

    // ----- concrete vibrator ----------------------------------------------------------------

    /// <summary>
    /// A poker vibrator in a pour, 3 to 6 s: a 150 Hz buzz with its harmonics, swelling about seven
    /// times a second and drifting a little in pitch, over a low rumble of wet concrete.
    /// </summary>
    public static float[] Vibrator(int variant)
    {
        var rng = new Random(2700 + variant * 307);
        float seconds = 3f + variant * 1.5f;
        int n = (int)(seconds * Rate);
        var rumble = LowPass(Noise(rng, n), Coef(320f));
        float f0 = 150f * (1f + ((float)rng.NextDouble() * 2 - 1) * 0.04f);
        float amHz = 6.5f + 2f * (float)rng.NextDouble();
        var s = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            phase += f0 * (1f + 0.02f * Mathf.Sin(Mathf.Tau * 0.7f * t)) / Rate;
            float tone = Mathf.Sin(Mathf.Tau * phase) + 0.5f * Mathf.Sin(Mathf.Tau * phase * 2f)
                + 0.3f * Mathf.Sin(Mathf.Tau * phase * 3f) + 0.15f * Mathf.Sin(Mathf.Tau * phase * 5f);
            float am = 0.7f + 0.3f * Mathf.Sin(Mathf.Tau * amHz * t);
            float env = Mathf.Min(1f, t / 0.4f) * Mathf.Min(1f, (seconds - t) / 0.4f);
            s[i] = (tone * am * 0.6f + rumble[i] * 4f) * env;
        }
        return Finish(s);
    }

    // ----- reversing beeper -----------------------------------------------------------------

    /// <summary>
    /// A reversing alarm: <paramref name="beeps"/> beeps of about 1 kHz, half a second on and half
    /// a second off, ending on the last beep. Public so the machines of #611-#614 can reuse it.
    /// </summary>
    public static float[] Beeper(int beeps, int variant)
    {
        beeps = Math.Max(1, beeps);
        float f = 1000f + variant * 45f;
        int on = Rate / 2, period = Rate;
        var s = new float[(beeps - 1) * period + on];
        int edge = Rate / 250;   // 4 ms: a beep without a click
        for (int b = 0; b < beeps; b++)
            for (int i = 0; i < on; i++)
            {
                float t = (float)i / Rate;
                float e = Math.Min(1f, Math.Min(i, on - 1 - i) / (float)edge);
                // a square-ish tone, as a piezo sounder is
                s[b * period + i] = (Mathf.Sin(Mathf.Tau * f * t) + 0.35f * Mathf.Sin(Mathf.Tau * f * 3f * t) + 0.12f * Mathf.Sin(Mathf.Tau * f * 5f * t)) * e;
            }
        return Normalise(s, 0.9f);
    }

    // ----- site radio -----------------------------------------------------------------------

    /// <summary>
    /// A radio in the site office, heard through its window, 8 s: a pentatonic tune in triangle waves
    /// over a bass and a kick-and-hat beat, a murmur of speech-like noise through it, all squeezed
    /// into 280 Hz to 3 kHz so it sounds small and tinny. It is music-like, not a recording.
    /// </summary>
    public static float[] Radio(int variant)
    {
        var rng = new Random(3300 + variant * 409);
        const float seconds = 8f;
        int n = (int)(seconds * Rate);
        float root = 196f * (variant == 0 ? 1f : 1.125f);
        float[] scale = { 1f, 9f / 8f, 5f / 4f, 3f / 2f, 5f / 3f, 2f, 9f / 4f };
        float step = 0.25f;
        int steps = (int)(seconds / step);
        var notes = new float[steps];
        for (int k = 0; k < steps; k++)
            notes[k] = k > 0 && rng.NextDouble() < 0.35 ? notes[k - 1] : root * scale[rng.Next(scale.Length)] * (rng.NextDouble() < 0.2 ? 2f : 1f);
        var hat = HighPass(Noise(rng, n), Coef(5000f));
        var talk = BandPass(Noise(rng, n), Coef(450f), Coef(1900f));
        var mod = LowPass(Noise(rng, n), Coef(6f));
        var s = new float[n];
        float lead = 0f, bass = 0f, kick = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            int k = Math.Min(steps - 1, (int)(t / step));
            float within = t - k * step;
            lead += notes[k] / Rate;
            float tri = 1f - 4f * Mathf.Abs(lead % 1f - 0.5f);
            float leadV = tri * 0.5f * Mathf.Exp(-within * 3.5f);
            bass += root * 0.5f / Rate;
            float bassV = (bass % 1f < 0.5f ? 1f : -1f) * 0.18f;
            float beat = t % 0.5f;
            kick += (100f * Mathf.Exp(-beat * 18f) + 45f) / Rate;
            float kickV = Mathf.Sin(Mathf.Tau * kick) * Mathf.Exp(-beat * 16f) * 0.6f;
            float hatV = hat[i] * 0.5f * Mathf.Exp(-(t % 0.25f) * 60f);
            float speech = talk[i] * Mathf.Min(1f, Mathf.Abs(mod[i]) * 45f) * 1.2f;
            s[i] = leadV + bassV + kickV + hatV + speech;
        }
        // two passes: one pole is only 6 dB an octave, and a kick and a bass would still boom through
        var band = BandPass(BandPass(s, Coef(2800f), Coef(280f)), Coef(3000f), Coef(300f));
        return Finish(band);
    }

    // ----- crane motor ----------------------------------------------------------------------

    /// <summary>
    /// A tower crane's slewing drive, 4 s: a whine that swells and rises as the jib starts to turn
    /// and falls as it stops, with a gearbox hum and two harmonics, a faint wobble in it.
    /// </summary>
    public static float[] CraneMotor(int variant)
    {
        var rng = new Random(3900 + variant * 503);
        const float seconds = 4f;
        int n = (int)(seconds * Rate);
        var gear = BandPass(Noise(rng, n), Coef(900f), Coef(180f));
        float baseHz = 360f * (1f + ((float)rng.NextDouble() * 2 - 1) * 0.12f);
        var s = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float swell = Mathf.Sin(Mathf.Pi * t / seconds);
            float f = baseHz * (0.75f + 0.5f * swell) * (1f + 0.012f * Mathf.Sin(Mathf.Tau * 5.5f * t));
            phase += f / Rate;
            float tone = Mathf.Sin(Mathf.Tau * phase) + 0.5f * Mathf.Sin(Mathf.Tau * phase * 2f) + 0.25f * Mathf.Sin(Mathf.Tau * phase * 3f);
            s[i] = (tone * 0.45f + gear[i] * 3f) * Mathf.Pow(swell, 0.7f);
        }
        return Finish(s);
    }

    // ----- a roller's vibrating drums ---------------------------------------------------------

    /// <summary>
    /// A tandem roller's drums vibrating (#614), <paramref name="n"/> samples for a loop: the
    /// eccentric weights' 55 Hz thump with its harmonics, two weights a hair apart beating slowly
    /// against each other, and the frame's steel rattling at every stroke. Unnormalised: the loop
    /// that bakes it normalises.
    /// </summary>
    public static float[] RollerDrum(Random rng, int n)
    {
        var rattle = BandPass(Noise(rng, n), Coef(1400f), Coef(250f));
        const float f0 = 55f;
        var s = new float[n];
        float pa = 0f, pb = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            pa += f0 / Rate;
            pb += f0 * 1.008f / Rate;
            float a = Mathf.Sin(Mathf.Tau * pa) + 0.6f * Mathf.Sin(Mathf.Tau * pa * 2f) + 0.35f * Mathf.Sin(Mathf.Tau * pa * 3f);
            float b = Mathf.Sin(Mathf.Tau * pb) + 0.6f * Mathf.Sin(Mathf.Tau * pb * 2f);
            // each stroke shakes the frame: the rattle comes in bursts at the drum's rate
            float stroke = Mathf.Pow(0.5f + 0.5f * Mathf.Sin(Mathf.Tau * pa), 4f);
            s[i] = (a + b) * 0.5f + rattle[i] * 5f * stroke + 0.05f * Mathf.Sin(Mathf.Tau * 0.9f * t) * a;
        }
        return s;
    }

    // ----- helpers --------------------------------------------------------------------------
    // Dsp's own are the same few lines, but Dsp names Godot's AudioStreamWav, which the unit
    // tests do not link; these keep this file Godot-engine-free.

    private static float[] Noise(Random rng, int n)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = (float)(rng.NextDouble() * 2 - 1);
        return s;
    }

    private static float Coef(float hz) => 1f - Mathf.Exp(-Mathf.Tau * hz / Rate);

    private static float[] LowPass(float[] x, float a)
    {
        var y = new float[x.Length];
        float state = 0;
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
        for (int i = 0; i < x.Length; i++) low[i] = x[i] - low[i];
        return low;
    }

    /// <summary>Between two coefficients: the one for the higher cutoff first or second, either order.</summary>
    private static float[] BandPass(float[] x, float a, float b)
    {
        var upper = LowPass(x, Math.Max(a, b));
        var lower = LowPass(x, Math.Min(a, b));
        for (int i = 0; i < upper.Length; i++) upper[i] -= lower[i];
        return upper;
    }

    /// <summary>A short fade-out (a cut-off tail clicks), then normalised to a 0.9 peak.</summary>
    private static float[] Finish(float[] s)
    {
        int n = s.Length, fade = Math.Min(n / 4, Rate / 100);
        for (int i = 0; i < fade; i++) s[n - 1 - i] *= (float)i / fade;
        return Normalise(s, 0.9f);
    }

    private static float[] Normalise(float[] s, float peak)
    {
        float max = 1e-6f;
        foreach (float v in s) max = Math.Max(max, Math.Abs(v));
        for (int i = 0; i < s.Length; i++) s[i] *= peak / max;
        return s;
    }
}
