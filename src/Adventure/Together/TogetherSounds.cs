using Godot;
using UnitSport.Audio;

namespace UnitSport.Adventure.Together;

/// <summary>
/// Playing together's synthesised sounds (#1266), each built once: the carabiner clicking shut when a rope ties in,
/// the rope's zip on a caught fall, a hauler's "Zieh!", and the pot's: the split's hiss, the fork's scrape, the plop
/// of lost bread, the "Prost" glasses at the end, the forfeit's yodel (static: the sound player lists them by
/// itself). The bubbling cheese is built per rate (a <see cref="SoundShowcaseAttribute"/> set).
/// </summary>
public static class TogetherSounds
{
    private static AudioStreamWav? _clink, _zip, _haul, _hiss, _scrape, _plop, _prost, _yodel, _crack, _thud;
    private static readonly AudioStreamWav?[] Bubbles = new AudioStreamWav?[4];

    /// <summary>A screwgate carabiner clicking shut on the harness: two bright metal ticks.</summary>
    public static AudioStreamWav Clink => _clink ??= Dsp.OneShot(0.45f, 12661, (rng, n) =>
    {
        var s = new float[n];
        Ping(s, 0.0f, 3150f, 0.5f);
        Ping(s, 0.09f, 4420f, 0.35f);
        for (int i = 0; i < Math.Min(n, Dsp.Rate / 50); i++) s[i] += (float)(rng.NextDouble() * 2 - 1) * 0.2f * (1 - i / (float)(Dsp.Rate / 50));
        return Clip(s);
    });

    /// <summary>The rope running through a hand and catching: a falling zip of noise, then a taut thud.</summary>
    public static AudioStreamWav Zip => _zip ??= Dsp.OneShot(0.7f, 12662, (rng, n) =>
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(900f), Dsp.Coef(4200f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = t < 0.45f ? Mathf.Min(1, t * 30) * (1 - t / 0.45f * 0.5f) : 0f;
            float thud = t > 0.42f ? Mathf.Sin(Mathf.Tau * 85f * t) * Mathf.Exp(-(t - 0.42f) * 18f) * 0.8f : 0f;
            s[i] = s[i] * env * 1.4f + thud;
        }
        return Clip(s);
    });

    /// <summary>"Zieh!" (pull!): a short voiced syllable, its vowel's formants gliding from "ts" to "ie".</summary>
    public static AudioStreamWav Haul => _haul ??= Dsp.OneShot(0.55f, 12663, (rng, n) =>
    {
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            // the "ts": a burst of hiss
            float ts = t < 0.08f ? (float)(rng.NextDouble() * 2 - 1) * 0.35f * (1 - t / 0.08f) : 0f;
            // the vowel: a glottal buzz through two formants of "ie" (280 Hz, 2250 Hz), falling in pitch
            float f0 = 190f - 50f * t;
            phase += f0 / Dsp.Rate;
            float buzz = (float)(phase % 1.0) * 2f - 1f;
            float env = t > 0.06f ? Mathf.Min(1, (t - 0.06f) * 25f) * Mathf.Exp(-Mathf.Max(0, t - 0.25f) * 9f) : 0f;
            float v = buzz * (0.6f * Mathf.Sin(Mathf.Tau * 280f * t) + 0.4f * Mathf.Sin(Mathf.Tau * 2250f * t)) * env;
            s[i] = ts + v * 0.7f;
        }
        return Clip(s);
    });

    /// <summary>The cheese splitting (tranchée): a hiss of fat on the hot caquelon.</summary>
    public static AudioStreamWav Hiss => _hiss ??= Dsp.OneShot(1.4f, 12664, (rng, n) =>
    {
        var s = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(2500f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float crackle = rng.NextDouble() < 0.002 ? (float)(rng.NextDouble() * 2 - 1) * 0.6f : 0f;
            s[i] = s[i] * Mathf.Min(1, t * 12) * Mathf.Exp(-t * 1.6f) * 0.8f + crackle;
        }
        return Clip(s);
    });

    /// <summary>A fork scraping the pot's bottom (la religieuse): a rasp of filtered noise in strokes.</summary>
    public static AudioStreamWav Scrape => _scrape ??= Dsp.OneShot(0.6f, 12665, (rng, n) =>
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(1400f), Dsp.Coef(5200f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float strokes = 0.5f + 0.5f * Mathf.Sin(Mathf.Tau * 7f * t);
            s[i] *= strokes * Mathf.Min(1, t * 20) * Mathf.Min(1, (0.6f - t) * 10) * 0.9f;
        }
        return Clip(s);
    });

    /// <summary>Bread lost in the cheese: a soft, thick plop.</summary>
    public static AudioStreamWav Plop => _plop ??= Dsp.OneShot(0.4f, 12666, (_, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float f = 420f - 600f * t;
            s[i] = Mathf.Sin(Mathf.Tau * Mathf.Max(80f, f) * t) * Mathf.Exp(-t * 14f) * Mathf.Min(1, t * 400) * 0.8f;
        }
        return Clip(s);
    });

    /// <summary>"Prost!": glasses clinking round the table, four bright rings a little apart.</summary>
    public static AudioStreamWav Prost => _prost ??= Dsp.OneShot(1.6f, 12667, (_, n) =>
    {
        var s = new float[n];
        float[] at = { 0f, 0.07f, 0.16f, 0.21f };
        float[] f = { 2630f, 2890f, 2410f, 3120f };
        for (int k = 0; k < at.Length; k++) Ping(s, at[k], f[k], 0.32f, decay: 3.2f);
        return Clip(s);
    });

    /// <summary>The forfeit's yodel: "Holla-di-ri-o", chest voice to head voice and back, the break in between.</summary>
    public static AudioStreamWav Yodel => _yodel ??= Dsp.OneShot(2.4f, 12668, (_, n) =>
    {
        var s = new float[n];
        // (start s, pitch Hz): low, high, low, high, low, a held last note
        (float At, float Hz)[] notes = { (0f, 330f), (0.3f, 523f), (0.55f, 392f), (0.8f, 659f), (1.1f, 494f), (1.4f, 392f) };
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            int k = 0;
            while (k + 1 < notes.Length && t >= notes[k + 1].At) k++;
            float hz = notes[k].Hz * (1 + 0.012f * Mathf.Sin(Mathf.Tau * 5.5f * t));
            phase += hz / Dsp.Rate;
            float p = (float)(phase % 1.0);
            // the head voice (the high notes) is purer than the chest voice
            bool head = hz > 450f;
            float wave = head ? Mathf.Sin(Mathf.Tau * p) : Mathf.Sin(Mathf.Tau * p) * 0.7f + Mathf.Sin(Mathf.Tau * 2 * p) * 0.25f + Mathf.Sin(Mathf.Tau * 3 * p) * 0.1f;
            float into = t - notes[k].At;
            float env = Mathf.Min(1, into * 30) * (t > 2.1f ? Mathf.Max(0, (2.4f - t) / 0.3f) : 1f);
            s[i] = wave * env * 0.45f;
        }
        return Clip(s);
    });

    /// <summary>A snow bridge giving way (#1372): a dry crack, then the slab's low rumble as it drops into the ice.</summary>
    public static AudioStreamWav Crack => _crack ??= Dsp.OneShot(1.1f, 12690, (rng, n) =>
    {
        var low = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(60f), Dsp.Coef(420f));
        var hiss = Dsp.HighPass(Dsp.Noise(rng, n), Dsp.Coef(1800f));
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float snap = t < 0.05f ? hiss[i] * (1 - t / 0.05f) * 1.1f : 0f;
            float rumble = low[i] * Mathf.Min(1, t * 20) * Mathf.Exp(-t * 3.2f) * 2.2f;
            s[i] = snap + rumble;
        }
        return Clip(s);
    });

    /// <summary>A body landing on the crevasse's ice floor (#1372): a dull thud with a short ring of the ice.</summary>
    public static AudioStreamWav Thud => _thud ??= Dsp.OneShot(0.8f, 12691, (_, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float f = Mathf.Max(40f, 120f - 160f * t);
            float body = Mathf.Sin(Mathf.Tau * f * t) * Mathf.Exp(-t * 12f) * Mathf.Min(1, t * 500) * 0.9f;
            float ring = Mathf.Sin(Mathf.Tau * 1870f * t) * Mathf.Exp(-t * 9f) * 0.12f;
            s[i] = body + ring;
        }
        return Clip(s);
    });

    /// <summary>The cheese bubbling, faster as it gets hotter or nears splitting (<paramref name="rate"/> 0..3).</summary>
    public static AudioStreamWav Bubble(int rate) => Bubbles[Math.Clamp(rate, 0, 3)] ??= Dsp.Encode(BubbleSamples(Math.Clamp(rate, 0, 3)));

    private static float[] BubbleSamples(int rate) => Dsp.RenderOneShot(1.5f, 12670 + rate, (rng, n) =>
    {
        var s = new float[n];
        int bubbles = 4 + rate * 4;
        for (int b = 0; b < bubbles; b++)
        {
            int from = (int)(rng.NextDouble() * (n - Dsp.Rate / 10));
            float f0 = 260f + (float)rng.NextDouble() * 320f;
            for (int i = 0; i < Dsp.Rate / 10 && from + i < n; i++)
            {
                float t = i / (float)Dsp.Rate;
                s[from + i] += Mathf.Sin(Mathf.Tau * (f0 + 900f * t) * t) * Mathf.Exp(-t * 45f) * 0.35f;
            }
        }
        return Clip(s);
    });

    /// <summary>The cheese's bubbling at each rate, in the sound player.</summary>
    [SoundShowcase("Adventure")]
    private static IEnumerable<(string, string, Func<float[]>)> ShowcaseBubbles() =>
        Enumerable.Range(0, 4).Select(r => ("Adventure", $"Fondue bubbling {r}", (Func<float[]>)(() => BubbleSamples(r))));

    private static void Ping(float[] s, float at, float f, float amp, float decay = 22f)
    {
        int from = (int)(at * Dsp.Rate);
        for (int i = from; i < s.Length; i++)
        {
            float t = (i - from) / (float)Dsp.Rate;
            float env = Mathf.Exp(-t * decay) * Mathf.Min(1, t * 2000);
            if (t > 0.01f && env < 1e-4f) break;
            s[i] += (Mathf.Sin(Mathf.Tau * f * t) + 0.4f * Mathf.Sin(Mathf.Tau * f * 2.7f * t)) * env * amp;
        }
    }

    private static float[] Clip(float[] s)
    {
        float peak = 0;
        foreach (float v in s) peak = Math.Max(peak, Math.Abs(v));
        return peak > 0.95f ? Dsp.Normalise(s, 0.95f) : s;
    }
}
