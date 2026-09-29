using Godot;

namespace UnitSport.Audio;

/// <summary>
/// Several baked variants of one sound, played so that no two hits in a row are the same.
///
/// <para>
/// A sound heard twice identically reads as a machine, however good it is once. Every variant
/// here is rendered from its own seeded <see cref="Random"/>, and a generator is expected to draw
/// its own parameter jitter from it (a slightly lower thump, a shorter tail) before drawing any
/// noise — so a variant differs in shape, not only in the grain of its noise. On top of that each
/// <see cref="Pick"/> adds a small pitch and volume jitter, which costs nothing and hides the
/// round-robin completely.
/// </para>
/// </summary>
public sealed class SfxBank
{
    public string Name { get; }
    public AudioStreamWav[] Variants { get; }

    /// <summary>± fraction of pitch added per play (0.04 = ±4%).</summary>
    public float PitchJitter { get; init; } = 0.04f;
    /// <summary>± decibels added per play.</summary>
    public float VolumeJitterDb { get; init; } = 1.5f;

    private int _last = -1;

    public SfxBank(string name, AudioStreamWav[] variants)
    {
        if (variants.Length == 0) throw new ArgumentException("a bank needs at least one variant", nameof(variants));
        Name = name;
        Variants = variants;
    }

    /// <summary>
    /// Bakes <paramref name="count"/> variants of a one-shot. <paramref name="make"/> gets a Random
    /// seeded per variant (seed, seed+1, ...) and the sample count.
    /// </summary>
    public static SfxBank Build(string name, int count, float seconds, int seed, Func<Random, int, float[]> make)
        => new(name, Enumerable.Range(0, count).Select(v => Dsp.OneShot(seconds, seed + v * 7919, make)).ToArray());

    /// <summary>Bakes variants of a recipe, each one mutated by <paramref name="amount"/>.</summary>
    public static SfxBank Build(string name, int count, SfxRecipe recipe, float amount, int seed)
        => new(name, Enumerable.Range(0, count).Select(v =>
        {
            var rng = new Random(seed + v * 7919);
            var r = v == 0 ? recipe : recipe.Mutate(rng, amount);
            return Dsp.Encode(r.Render(seed + v));
        }).ToArray());

    /// <summary>The next variant (never the previous one) with its per-play jitter.</summary>
    public (AudioStreamWav Stream, float Pitch, float VolumeDb) Pick(Random rng)
    {
        int i = rng.Next(Variants.Length);
        if (Variants.Length > 1 && i == _last) i = (i + 1 + rng.Next(Variants.Length - 1)) % Variants.Length;
        _last = i;
        float pitch = 1f + ((float)rng.NextDouble() * 2 - 1) * PitchJitter;
        float db = ((float)rng.NextDouble() * 2 - 1) * VolumeJitterDb;
        return (Variants[i], pitch, db);
    }
}

public enum SfxWave { Sine, Square, Saw, Triangle, Noise }

/// <summary>
/// One voice of a <see cref="SfxRecipe"/>: an oscillator or a filtered noise, with a pitch slide
/// and an attack / exponential-decay envelope. In the spirit of sfxr, the classic procedural SFX
/// generator, but layered, because a footstep or a bell is several things at once.
/// </summary>
public sealed record SfxLayer
{
    public SfxWave Wave { get; init; } = SfxWave.Sine;
    public float Gain { get; init; } = 1f;
    /// <summary>Start frequency (Hz). For noise, the low-pass cutoff at the start.</summary>
    public float FreqStart { get; init; } = 440f;
    /// <summary>End frequency (Hz), reached after <see cref="SlideSeconds"/>.</summary>
    public float FreqEnd { get; init; } = 440f;
    public float SlideSeconds { get; init; } = 0.1f;
    /// <summary>Square duty (0..1).</summary>
    public float Duty { get; init; } = 0.5f;
    /// <summary>For noise: high-pass cutoff (Hz), 0 = none.</summary>
    public float HighPassHz { get; init; }
    public float Delay { get; init; }
    public float Attack { get; init; } = 0.002f;
    /// <summary>Exponential decay rate, 1/s.</summary>
    public float Decay { get; init; } = 10f;
    /// <summary>Vibrato depth (fraction of frequency) and rate (Hz).</summary>
    public float Vibrato { get; init; }
    public float VibratoHz { get; init; } = 6f;
}

/// <summary>A layered sound description that can render and mutate itself.</summary>
public sealed record SfxRecipe
{
    public float Seconds { get; init; } = 0.3f;
    public SfxLayer[] Layers { get; init; } = [];
    /// <summary>tanh saturation drive, 0 = clean.</summary>
    public float Drive { get; init; }
    /// <summary>Bit depth to crush to, 0 = none.</summary>
    public int Bits { get; init; }

    public float[] Render(int seed)
    {
        int n = Math.Max(1, (int)(Seconds * Dsp.Rate));
        var rng = new Random(seed);
        var mix = new float[n];
        foreach (var l in Layers)
        {
            float phase = 0, lp = 0, hpLow = 0;
            float hpA = l.HighPassHz > 0 ? Dsp.Coef(l.HighPassHz) : 0;
            int start = (int)(l.Delay * Dsp.Rate);
            for (int i = start; i < n; i++)
            {
                float t = (float)(i - start) / Dsp.Rate;
                float k = l.SlideSeconds > 0 ? Mathf.Min(1f, t / l.SlideSeconds) : 1f;
                // exponential slide: pitch moves evenly in octaves, as a falling thump does
                float f = l.FreqStart * Mathf.Pow(Mathf.Max(1e-3f, l.FreqEnd) / Mathf.Max(1e-3f, l.FreqStart), k);
                if (l.Vibrato > 0) f *= 1f + l.Vibrato * Mathf.Sin(Mathf.Tau * l.VibratoHz * t);
                float env = (l.Attack > 0 ? Mathf.Min(1f, t / l.Attack) : 1f) * Mathf.Exp(-l.Decay * t);
                float v;
                if (l.Wave == SfxWave.Noise)
                {
                    float raw = (float)(rng.NextDouble() * 2 - 1);
                    lp += Dsp.Coef(Mathf.Min(f, Dsp.Rate * 0.45f)) * (raw - lp);
                    v = lp;
                    if (hpA > 0) { hpLow += hpA * (v - hpLow); v -= hpLow; }
                    v *= 2f;
                }
                else
                {
                    phase = (phase + f / Dsp.Rate) % 1f;
                    v = l.Wave switch
                    {
                        SfxWave.Square => phase < l.Duty ? 1f : -1f,
                        SfxWave.Saw => phase * 2f - 1f,
                        SfxWave.Triangle => 1f - 4f * Mathf.Abs(phase - 0.5f),
                        _ => Mathf.Sin(Mathf.Tau * phase),
                    };
                }
                mix[i] += v * env * l.Gain;
            }
        }
        if (Drive > 0) for (int i = 0; i < n; i++) mix[i] = Mathf.Tanh(mix[i] * (1f + Drive));
        if (Bits > 0)
        {
            float q = (1 << (Bits - 1));
            for (int i = 0; i < n; i++) mix[i] = Mathf.Round(mix[i] * q) / q;
        }
        int fade = Math.Min(n / 4, Dsp.Rate / 100);
        for (int i = 0; i < fade; i++) mix[n - 1 - i] *= (float)i / fade;
        return Dsp.Normalise(mix, 0.9f);
    }

    /// <summary>A copy with every frequency, gain and decay nudged by up to ±<paramref name="amount"/>.</summary>
    public SfxRecipe Mutate(Random rng, float amount)
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * amount;
        return this with
        {
            Seconds = Seconds * (1f + ((float)rng.NextDouble() * 2 - 1) * amount * 0.3f),
            Layers = Layers.Select(l =>
            {
                float pitch = J();   // one pitch factor per layer keeps its slide's shape
                return l with
                {
                    FreqStart = l.FreqStart * pitch,
                    FreqEnd = l.FreqEnd * pitch,
                    Gain = l.Gain * J(),
                    Decay = l.Decay * J(),
                    Delay = l.Delay * J(),
                };
            }).ToArray(),
        };
    }
}
