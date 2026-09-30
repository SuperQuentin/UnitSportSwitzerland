using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions;

/// <summary>
/// The occasions' synthesised sounds, as plain sample arrays (22.05 kHz mono). No audio files, in
/// keeping with <see cref="SfxSynth"/>: an owl is two formant tones, a wolf a gliding harmonic
/// stack, wind a swept band of noise. <c>--soundcheck</c> renders every one to WAV.
/// </summary>
public static class OccasionSounds
{
    private const int R = Dsp.Rate;

    /// <summary>Every sound, for <c>--soundcheck</c>.</summary>
    public static IEnumerable<(string Name, float[] Samples)> All()
    {
        yield return ("owl", Dsp.Normalise(Owl(new Random(1)), 0.9f));
        yield return ("howl", Dsp.Normalise(Howl(new Random(2)), 0.9f));
        yield return ("wind", Dsp.Normalise(Wind(new Random(3)), 0.9f));
        yield return ("toll", Dsp.Normalise(Toll(new Random(4)), 0.9f));
        yield return ("jingle_halloween", Dsp.Normalise(HalloweenJingle(), 0.9f));
        yield return ("sleigh", Dsp.Normalise(SleighBells(new Random(5)), 0.9f));
        yield return ("carol", Dsp.Normalise(Carol(new Random(6)), 0.9f));
        yield return ("jingle_christmas", Dsp.Normalise(ChristmasJingle(), 0.9f));
    }

    /// <summary>A tawny owl: "hoo … hu-hoooo", a soft tone near 450 Hz with a breathy edge.</summary>
    public static float[] Owl(Random rng)
    {
        float f = 420f + (float)rng.NextDouble() * 50f;
        (float Start, float Dur, float F0, float F1, float Trem)[] calls =
        [
            (0.00f, 0.45f, f * 1.03f, f * 0.98f, 0f),
            (1.35f, 0.16f, f * 0.94f, f * 0.97f, 0f),
            (1.58f, 0.95f, f * 1.00f, f * 0.92f, 13f),
        ];
        var s = new float[(int)(2.8f * R)];
        foreach (var c in calls)
        {
            int n = (int)(c.Dur * R), o = (int)(c.Start * R);
            float phase = 0;
            for (int i = 0; i < n && o + i < s.Length; i++)
            {
                float t = i / (float)n;
                float hz = Mathf.Lerp(c.F0, c.F1, t);
                phase += hz / R;
                float env = Mathf.Pow(Mathf.Sin(Mathf.Pi * t), 0.6f);
                if (c.Trem > 0) env *= 0.65f + 0.35f * Mathf.Sin(Mathf.Tau * c.Trem * i / R);
                s[o + i] += env * (Mathf.Sin(Mathf.Tau * phase) + 0.18f * Mathf.Sin(2 * Mathf.Tau * phase));
            }
        }
        var breath = Dsp.LowPass(Dsp.Noise(rng, s.Length), Dsp.Coef(900));
        for (int i = 0; i < s.Length; i++) s[i] += breath[i] * Math.Abs(s[i]) * 0.9f;
        return Dsp.LowPass(s, Dsp.Coef(2400));
    }

    /// <summary>A wolf far off: rises, holds with a slow vibrato, falls away. Dulled by distance.</summary>
    public static float[] Howl(Random rng)
    {
        float baseHz = 300f + (float)rng.NextDouble() * 60f;
        float dur = 3.4f + (float)rng.NextDouble() * 0.8f;
        var s = new float[(int)(dur * R)];
        float phase = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)s.Length;
            float contour = t < 0.25f ? Mathf.SmoothStep(0f, 1f, t / 0.25f)
                : t < 0.75f ? 1f
                : 1f - 0.35f * Mathf.SmoothStep(0f, 1f, (t - 0.75f) / 0.25f);
            float vib = 1f + 0.012f * Mathf.Sin(Mathf.Tau * 5.2f * i / R) * Mathf.SmoothStep(0.2f, 0.4f, t);
            float hz = baseHz * (1f + 0.75f * contour) * vib;
            phase += hz / R;
            float env = Mathf.SmoothStep(0f, 0.08f, t) * (1f - Mathf.SmoothStep(0.8f, 1f, t));
            s[i] = env * (Mathf.Sin(Mathf.Tau * phase) + 0.4f * Mathf.Sin(2 * Mathf.Tau * phase) + 0.15f * Mathf.Sin(3 * Mathf.Tau * phase));
        }
        return Dsp.LowPass(s, Dsp.Coef(1500));
    }

    /// <summary>A gust: noise through a low-pass whose corner rises and falls with the gust's strength.</summary>
    public static float[] Wind(Random rng)
    {
        float dur = 4.5f + (float)rng.NextDouble() * 2f;
        var noise = Dsp.Noise(rng, (int)(dur * R));
        var s = new float[noise.Length];
        float lp = 0, lp2 = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)s.Length;
            float gust = Mathf.Pow(Mathf.Sin(Mathf.Pi * t), 1.6f) * (0.8f + 0.2f * Mathf.Sin(Mathf.Tau * 0.9f * i / R));
            float a = Dsp.Coef(180f + 900f * gust);
            lp += a * (noise[i] - lp);
            lp2 += a * (lp - lp2);
            s[i] = (lp - lp2 * 0.6f) * gust;
        }
        return s;
    }

    /// <summary>A low, detuned church bell: two bells a few Hz apart, beating slowly. The midnight toll.</summary>
    public static float[] Toll(Random rng)
    {
        var a = AmbienceDsp.ChurchBell(rng, 131f);
        var b = AmbienceDsp.ChurchBell(rng, 133.5f);
        var s = new float[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < s.Length; i++)
            s[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] * 0.8f : 0);
        return s;
    }

    /// <summary>
    /// A sleigh passing: harness bells shaken about five times a second, each shake a handful of
    /// small bells (three inharmonic partials between 2 and 6 kHz, gone in a tenth of a second),
    /// swelling and fading as it goes by.
    /// </summary>
    public static float[] SleighBells(Random rng)
    {
        float dur = 3.4f;
        var s = new float[(int)(dur * R)];
        for (float shake = 0.05f; shake < dur - 0.2f; shake += 1f / (5.2f + (float)rng.NextDouble() * 0.8f))
        {
            float pass = Mathf.Pow(Mathf.Sin(Mathf.Pi * shake / dur), 1.5f);
            for (int b = 0; b < 5; b++)
            {
                int o = (int)((shake + (float)rng.NextDouble() * 0.03f) * R);
                float f0 = 2200f + (float)rng.NextDouble() * 1400f;
                float[] ratios = [1f, 1.47f, 2.09f];
                int n = (int)(0.12f * R);
                for (int i = 0; i < n && o + i < s.Length; i++)
                {
                    float t = i / (float)R, env = Mathf.Exp(-t / 0.035f) * pass * 0.25f;
                    float v = 0;
                    foreach (float r in ratios) v += Mathf.Sin(Mathf.Tau * f0 * r * t) / r;
                    s[o + i] += v * env;
                }
            }
        }
        return s;
    }

    /// <summary>
    /// The chorus of "Jingle Bells" (Pierpont, 1857 — public domain) rung on a church bell: one
    /// bell rendered once and resampled to each note's pitch, every note left to ring into the next.
    /// </summary>
    public static float[] Carol(Random rng)
    {
        var bell = AmbienceDsp.ChurchBell(rng, 329.6f);   // E4
        (int Midi, float Beats)[] tune =
        [
            (64, 1), (64, 1), (64, 2), (64, 1), (64, 1), (64, 2),
            (64, 1), (67, 1), (60, 1.5f), (62, 0.5f), (64, 4),
            (65, 1), (65, 1), (65, 1.5f), (65, 0.5f), (65, 1), (64, 1), (64, 1), (64, 0.5f), (64, 0.5f),
            (64, 1), (62, 1), (62, 1), (64, 1), (62, 2), (67, 2),
        ];
        const float beat = 0.5f;
        float total = tune.Sum(n => n.Beats) * beat + bell.Length / (float)R;
        var s = new float[(int)(total * R)];
        float at = 0;
        foreach (var (midi, beats) in tune)
        {
            float ratio = Mathf.Pow(2f, (midi - 64) / 12f);
            int o = (int)(at * R);
            for (int i = 0; o + i < s.Length; i++)
            {
                float src = i * ratio;
                int k = (int)src;
                if (k + 1 >= bell.Length) break;
                s[o + i] += Mathf.Lerp(bell[k], bell[k + 1], src - k) * 0.5f;
            }
            at += beats * beat;
        }
        return s;
    }

    /// <summary>"Deck the Halls" (Welsh traditional, public domain), first line, on the chip.</summary>
    public static float[] ChristmasJingle()
    {
        var N = ChipTune.N;
        ChipTune.Note[] lead =
        [
            N(79, 1.5f), N(77, 0.5f), N(76, 1), N(74, 1),
            N(72, 1), N(74, 1), N(76, 1), N(72, 1),
            N(74, 0.5f), N(76, 0.5f), N(77, 0.5f), N(74, 0.5f), N(76, 1.5f), N(74, 0.5f),
            N(72, 1), N(71, 1), N(72, 2),
        ];
        ChipTune.Note[] bass =
        [
            N(48, 2), N(43, 2),
            N(45, 2), N(48, 2),
            N(43, 2), N(48, 2),
            N(43, 2), N(36, 2),
        ];
        return ChipTune.Render(lead, bass, 150f);
    }

    /// <summary>An original minor-key phrase in D, with the augmented step that says "spooky".</summary>
    public static float[] HalloweenJingle()
    {
        var N = ChipTune.N;
        ChipTune.Note[] lead =
        [
            N(74, 1), N(77, 0.5f), N(81, 0.5f), N(80, 1), N(81, 1),
            N(86, 0.5f), N(84, 0.5f), N(81, 0.5f), N(77, 0.5f), N(80, 1), N(76, 1),
            N(74, 0.5f), N(77, 0.5f), N(76, 0.5f), N(73, 0.5f), N(74, 2),
        ];
        ChipTune.Note[] bass =
        [
            N(50, 2), N(50, 2),
            N(46, 2), N(45, 2),
            N(50, 2), N(45, 1), N(38, 1),
        ];
        return ChipTune.Render(lead, bass, 132f);
    }
}
