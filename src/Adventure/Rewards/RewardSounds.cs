using Godot;
using UnitSport.Audio;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The rewards' synthesised sounds (#1227), each built once: a jingle per chest tier, the lid, an edelweiss picked,
/// the Höhenfeuer's whoosh, the detector's beep, the gust, the marmot's whistle (static: the sound player lists them
/// by itself), and the cowbells, built per pitch (a <see cref="SoundShowcaseAttribute"/> set).
/// </summary>
public static class RewardSounds
{
    private static readonly AudioStreamWav?[] Jingles = new AudioStreamWav?[4];
    private static AudioStreamWav? _lid, _pick, _whoosh, _beep, _gust, _whistle;

    /// <summary>The pentatonic notes of the jingles (Hz): Common two, Fine three, Precious four, Luxurious a run of six and a chord.</summary>
    private static readonly float[][] Notes =
    {
        new[] { 523.25f, 659.25f },
        new[] { 523.25f, 659.25f, 783.99f },
        new[] { 587.33f, 739.99f, 880f, 1174.66f },
        new[] { 523.25f, 587.33f, 659.25f, 783.99f, 880f, 1046.5f },
    };

    public static AudioStreamWav CommonJingle => Jingle(ChestTier.Common);
    public static AudioStreamWav FineJingle => Jingle(ChestTier.Fine);
    public static AudioStreamWav PreciousJingle => Jingle(ChestTier.Precious);
    public static AudioStreamWav LuxuriousJingle => Jingle(ChestTier.Luxurious);

    /// <summary>A tier's jingle: plucked bell tones rising, the Luxurious one ending on a held chord.</summary>
    public static AudioStreamWav Jingle(ChestTier t) => Jingles[(int)t] ??= Dsp.OneShot(t == ChestTier.Luxurious ? 2.2f : 1.3f, 1227 + (int)t, (_, n) =>
    {
        var s = new float[n];
        var notes = Notes[(int)t];
        float step = t == ChestTier.Luxurious ? 0.11f : 0.13f;
        for (int k = 0; k < notes.Length; k++) Pluck(s, notes[k], k * step, 0.9f, 0.32f);
        if (t == ChestTier.Luxurious)
            foreach (float f in new[] { 523.25f, 659.25f, 783.99f }) Pluck(s, f, notes.Length * step + 0.05f, 1.6f, 0.2f);
        return Clip(s);
    });

    /// <summary>The lid: a wooden creak and its knock.</summary>
    public static AudioStreamWav Lid => _lid ??= Dsp.OneShot(0.7f, 12271, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float creak = t < 0.45f ? Mathf.Sin(Mathf.Tau * (180 + 90 * Mathf.Sin(t * 37)) * t) * 0.18f * (float)(0.6 + 0.4 * rng.NextDouble()) : 0;
            float knock = t > 0.48f ? Mathf.Sin(Mathf.Tau * 120 * t) * Mathf.Exp(-(t - 0.48f) * 30) * 0.7f : 0;
            s[i] = creak * Mathf.Min(1, t * 40) + knock;
        }
        return s;
    });

    /// <summary>An edelweiss picked: a soft rustle and a high chime.</summary>
    public static AudioStreamWav Pick => _pick ??= Dsp.OneShot(0.6f, 12272, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 25) * 0.25f;
        }
        Pluck(s, 1567.98f, 0.05f, 0.5f, 0.3f);
        return Clip(s);
    });

    /// <summary>A wood stack catching: a whoosh of noise rising, then crackle.</summary>
    public static AudioStreamWav Whoosh => _whoosh ??= Dsp.OneShot(1.4f, 12273, (rng, n) =>
    {
        var raw = Dsp.Noise(rng, n);
        var s = Dsp.LowPass(raw, Dsp.Coef(900));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Min(1, t * 5) * Mathf.Exp(-Mathf.Max(0, t - 0.3f) * 2.5f);
            float crackle = rng.NextDouble() < 0.0015 ? (float)(rng.NextDouble() * 2 - 1) * 0.8f : 0;
            s[i] = s[i] * env * 0.9f + crackle * Mathf.Exp(-t * 1.5f);
        }
        return Clip(s);
    });

    /// <summary>The detector's beep: a short square-ish blip.</summary>
    public static AudioStreamWav Beep => _beep ??= Dsp.OneShot(0.08f, 12274, (_, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sign(Mathf.Sin(Mathf.Tau * 1760 * t)) * 0.22f * Mathf.Min(1, (0.08f - t) * 200) * Mathf.Min(1, t * 2000);
        }
        return s;
    });

    /// <summary>A bottled gust let out: a rising roar of wind.</summary>
    public static AudioStreamWav Gust => _gust ??= Dsp.OneShot(2.5f, 12275, (rng, n) =>
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), 200, 1600);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] *= Mathf.Min(1, t * 1.5f) * Mathf.Exp(-Mathf.Max(0, t - 1.2f) * 2f) * 1.6f;
        }
        return Clip(s);
    });

    /// <summary>The marmot's alarm whistle: a sharp falling whistle, twice.</summary>
    public static AudioStreamWav Whistle => _whistle ??= Dsp.OneShot(0.8f, 12276, (_, n) =>
    {
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float local = t < 0.3f ? t : t - 0.4f;
            if (local < 0 || local > 0.25f) continue;
            float f = 3000 - 900 * (local / 0.25f);
            phase += Mathf.Tau * f / Dsp.Rate;
            s[i] = (float)Math.Sin(phase) * 0.45f * Mathf.Min(1, local * 300) * Mathf.Min(1, (0.25f - local) * 60);
        }
        return s;
    });

    /// <summary>The cowbells' pitches (Hz): the rail's four and the fifth that plays the tune.</summary>
    public static readonly float[] BellPitches = { 392f, 440f, 523.25f, 587.33f, 659.25f };

    private static readonly AudioStreamWav?[] Bells = new AudioStreamWav?[5];

    /// <summary>A cowbell: a clank and its hollow, inharmonic ring.</summary>
    public static AudioStreamWav Bell(int i) => Bells[i] ??= Dsp.Encode(BellSamples(BellPitches[i]));

    private static float[] BellSamples(float f) => Dsp.RenderOneShot(1.2f, (int)f, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float ring = Mathf.Sin(Mathf.Tau * f * t) * 0.5f + Mathf.Sin(Mathf.Tau * f * 2.76f * t) * 0.22f + Mathf.Sin(Mathf.Tau * f * 5.4f * t) * 0.1f;
            float clank = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 90) * 0.5f;
            s[i] = ring * Mathf.Exp(-t * 3.2f) * Mathf.Min(1, t * 900) + clank;
        }
        return Clip(s);
    });

    /// <summary>The cowbells, one per pitch, in the sound player.</summary>
    [SoundShowcase("Adventure")]
    private static IEnumerable<(string, string, Func<float[]>)> ShowcaseBells() =>
        BellPitches.Select((f, i) => ("Adventure", i < 4 ? $"Cowbell {i + 1}" : "Cowbell (the tune's)", (Func<float[]>)(() => BellSamples(f))));

    private static void Pluck(float[] s, float f, float at, float len, float amp)
    {
        int from = (int)(at * Dsp.Rate), to = Math.Min(s.Length, from + (int)(len * Dsp.Rate));
        for (int i = from; i < to; i++)
        {
            float t = (i - from) / (float)Dsp.Rate;
            float env = Mathf.Exp(-t * 4.5f) * Mathf.Min(1, t * 600);
            s[i] += (Mathf.Sin(Mathf.Tau * f * t) + 0.3f * Mathf.Sin(Mathf.Tau * f * 2 * t) + 0.12f * Mathf.Sin(Mathf.Tau * f * 3.01f * t)) * env * amp;
        }
    }

    private static float[] Clip(float[] s)
    {
        float peak = 0;
        foreach (float v in s) peak = Math.Max(peak, Math.Abs(v));
        return peak > 0.95f ? Dsp.Normalise(s, 0.95f) : s;
    }
}
