using Godot;
using UnitSport.Audio;

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>
/// The alphorn game's synthesised voices (#1269), each baked once per partial: the alphorn's natural tones (a held loop for a blow that lasts
/// as long as the finger, a swelling one-shot for a note heard from afar) and the yodel's chest and head voice with the "Juuz" between.
/// The sound player lists them through <see cref="Showcase"/>.
/// </summary>
public static class HornSounds
{
    private static readonly Dictionary<int, AudioStreamWav> Holds = new(), Notes = new(), Yodels = new();
    private static AudioStreamWav? _juuz;

    /// <summary>The partial's note, held: a steady loop, to start on the press and stop on the release.</summary>
    public static AudioStreamWav Hold(int partial) =>
        Holds.TryGetValue(partial, out var w) ? w : Holds[partial] = Dsp.Loop(1f, 4400 + partial, (rng, n) => HornSamples(partial, n, 0f, false));

    /// <summary>The partial's note for 3 s with its swell in and its fade out: what the others hear, and the echo.</summary>
    public static AudioStreamWav Note(int partial) =>
        Notes.TryGetValue(partial, out var w) ? w : Notes[partial] = Dsp.OneShot(3f, 4500 + partial, (rng, n) => HornSamples(partial, n, 3f, true));

    /// <summary>A yodel syllable on the partial: the chest voice for the lower ones, the head voice (thin, higher) from the sixth up.</summary>
    public static AudioStreamWav Yodel(int partial) =>
        Yodels.TryGetValue(partial, out var w) ? w : Yodels[partial] = Dsp.OneShot(0.7f, 4600 + partial, (rng, n) => YodelSamples(partial, n, partial >= 6, false));

    /// <summary>The break: the voice leaps from the chest into the head, the yodel's "Juuz".</summary>
    public static AudioStreamWav Juuz => _juuz ??= Dsp.OneShot(0.9f, 4699, (rng, n) => YodelSamples(3, n, false, true));

    /// <summary>
    /// F-sharp 1's overtones as a brass bell shapes them: the lower ones strongest, a dark roll-off, a little breath at the front. The
    /// samples are phase-continuous for a loop, and <paramref name="swell"/> adds the envelope for a one-shot.
    /// </summary>
    internal static float[] HornSamples(int partial, int n, float seconds, bool swell)
    {
        var s = new float[n];
        double hz = AlphornRules.Frequency(partial);
        var rng = new Random(partial);
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Dsp.Rate;
            float v = 0;
            for (int k = 1; k <= 7; k++)
            {
                double f = hz * k;
                if (f > 5000) break;
                // the bell passes the low harmonics and dulls the rest
                v += (float)(Math.Sin(Math.Tau * f * t + k * 0.7) / Math.Pow(k, 1.15));
            }
            lp += ((float)(rng.NextDouble() * 2 - 1) - lp) * 0.2f;
            float breath = swell && t < 0.18 ? lp * (float)(1 - t / 0.18) * 0.12f : 0f;
            float env = swell ? Mathf.Min(1f, (float)t / 0.3f) * Mathf.Clamp((seconds - (float)t) / 1.2f, 0f, 1f) : 1f;
            s[i] = (v * 0.45f + breath) * env;
        }
        return s;
    }

    /// <summary>
    /// A voice: a glottal buzz through the formants of "jo" (chest) or a thinner "ü" (head); the break leaps an octave and a half at
    /// 0.3 s.
    /// </summary>
    internal static float[] YodelSamples(int partial, int n, bool head, bool leap)
    {
        var s = new float[n];
        double phase = 0, f0 = AlphornRules.Frequency(partial);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Dsp.Rate;
            double f = f0;
            if (leap) f = f0 * (t < 0.28 ? 1 : t < 0.34 ? 1 + (t - 0.28) / 0.06 * 1.5 : 2.5);
            double vibrato = 1 + 0.012 * Math.Sin(Math.Tau * 5.5 * t) * Math.Min(1, t * 3);
            phase += f * vibrato / Dsp.Rate;
            bool airy = head || leap && t >= 0.31;
            // the buzz: a saw whose upper partials are thinned for the head voice
            float saw = (float)(phase % 1.0) * 2f - 1f;
            float f1 = airy ? 330f : 520f, f2 = airy ? 2300f : 1100f;
            float body = saw * (0.55f * Mathf.Sin(Mathf.Tau * f1 * (float)t) + 0.35f * Mathf.Sin(Mathf.Tau * f2 * (float)t) + 0.35f);
            float pure = Mathf.Sin((float)(Math.Tau * phase));
            float env = Mathf.Min(1f, (float)t / 0.04f) * Mathf.Exp(-(float)Math.Max(0, t - (leap ? 0.6 : 0.3)) * (leap ? 6f : 7f));
            s[i] = (airy ? body * 0.35f + pure * 0.45f : body * 0.7f + pure * 0.2f) * env;
        }
        return s;
    }

    /// <summary>Every partial of the horn, a held-length note of each, and the yodel's voices: for the sound player (<c>--sounds</c>).</summary>
    [SoundShowcase("Alphorn")]
    private static IEnumerable<(string, string, Func<float[]>)> Showcase()
    {
        for (int p = AlphornRules.LowestPartial; p <= AlphornRules.HighestPartial; p++)
        {
            int partial = p;
            yield return ("Alphorn", partial == 11 ? "Alphorn partial 11 (the Alphorn-Fa)" : $"Alphorn partial {partial}",
                () => Dsp.RenderOneShot(3f, 4500 + partial, (_, n) => HornSamples(partial, n, 3f, true)));
        }
        foreach (int p in new[] { 3, 4, 5, 6, 8, 10 })
        {
            int partial = p;
            yield return ("Alphorn", $"Yodel {(partial >= 6 ? "head" : "chest")} {partial}",
                () => Dsp.RenderOneShot(0.7f, 4600 + partial, (_, n) => YodelSamples(partial, n, partial >= 6, false)));
        }
        yield return ("Alphorn", "Yodel break (Juuz)", () => Dsp.RenderOneShot(0.9f, 4699, (_, n) => YodelSamples(3, n, false, true)));
    }
}
