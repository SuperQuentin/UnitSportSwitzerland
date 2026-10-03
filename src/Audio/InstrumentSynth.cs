using Godot;

namespace UnitSport.Audio;

/// <summary>What a house instrument is (#433): its sounds and how its keys map to them.</summary>
public enum InstrumentKind { Piano, Keyboard, Drums }

/// <summary>
/// The sounds of the house instruments and the running tap (#433), synthesised like everything else
/// (<see cref="SfxBank"/>, <see cref="Dsp"/>) and baked once on first use:
/// <list type="bullet">
/// <item><b>Piano</b>: six slightly stretched partials, the high ones dying first, a felt thump
/// under the attack. One sample per MIDI note, tuned, no jitter.</item>
/// <item><b>Keyboard</b>: an electric piano, a sine with a bell-like second partial that fades fast.</item>
/// <item><b>Drums</b>: kick, snare, closed and open hi-hat, two toms, crash and ride
/// (<see cref="DrumNames"/>), as sfxr-style recipes.</item>
/// <item><b>Water</b>: a seamless loop of band-passed noise with a slow gurgle.</item>
/// </list>
/// Notes: docs/notes/audio/instruments.md.
/// </summary>
public static class InstrumentSynth
{
    /// <summary>The drum pieces, in note order.</summary>
    public static readonly string[] DrumNames = { "kick", "snare", "hat", "open hat", "tom", "floor tom", "crash", "ride" };

    /// <summary>The lowest and highest note the keys can reach (C2..C7).</summary>
    public const int LowNote = 36, HighNote = 96;

    private static readonly Dictionary<int, AudioStreamWav> Cache = new();
    private static AudioStreamWav? _water;

    /// <summary>The sample for <paramref name="note"/> on <paramref name="kind"/>: a MIDI note, or a drum index.</summary>
    public static AudioStreamWav Note(InstrumentKind kind, int note)
    {
        note = kind == InstrumentKind.Drums ? Math.Clamp(note, 0, DrumNames.Length - 1) : Math.Clamp(note, LowNote, HighNote);
        int key = (int)kind * 1000 + note;
        if (!Cache.TryGetValue(key, out var wav)) Cache[key] = wav = Dsp.Encode(Render(kind, note));
        return wav;
    }

    /// <summary>The raw samples (for <c>--soundcheck</c> and <see cref="Note"/>).</summary>
    public static float[] Render(InstrumentKind kind, int note) => kind switch
    {
        InstrumentKind.Piano => Piano(note),
        InstrumentKind.Keyboard => ElectricPiano(note),
        _ => Drum(note),
    };

    /// <summary>The running tap: a two-second seamless loop.</summary>
    public static AudioStreamWav Water => _water ??= Dsp.Loop(2f, 433, WaterSamples);

    public static float[] WaterSamples(Random rng, int n)
    {
        var s = Dsp.BandPass(Dsp.Noise(rng, n), 350f, 3200f);
        // the stream breaking on the basin: a slow wobble in level and a little splash grain
        float g1 = (float)rng.NextDouble() * Mathf.Tau, g2 = (float)rng.NextDouble() * Mathf.Tau;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Dsp.Rate;
            s[i] *= 0.75f + 0.15f * Mathf.Sin(Mathf.Tau * 3.1f * t + g1) + 0.1f * Mathf.Sin(Mathf.Tau * 7.3f * t + g2);
        }
        return s;
    }

    public static float Hz(int midi) => 440f * Mathf.Pow(2f, (midi - 69) / 12f);

    private static float[] Piano(int midi)
    {
        float f0 = Hz(midi);
        // low notes ring longer, as real strings do
        float seconds = Mathf.Lerp(3.2f, 1.2f, Mathf.Clamp((midi - LowNote) / 60f, 0f, 1f));
        int n = (int)(seconds * Dsp.Rate);
        var s = new float[n];
        const float stretch = 0.0006f;
        for (int k = 1; k <= 6; k++)
        {
            float f = f0 * k * Mathf.Sqrt(1f + stretch * k * k);
            if (f > Dsp.Rate * 0.45f) break;
            float amp = 1f / Mathf.Pow(k, 1.3f);
            float decay = 0.9f + 0.6f * k + f0 / 600f;
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Dsp.Rate;
                phase += f / Dsp.Rate;
                s[i] += Mathf.Sin(Mathf.Tau * phase) * amp * Mathf.Exp(-decay * t);
            }
        }
        // the hammer: a short low thump of filtered noise
        var rng = new Random(midi);
        float lp = 0f, a = Dsp.Coef(Mathf.Min(f0 * 4f, 4000f));
        int hammer = Math.Min(n, Dsp.Rate / 40);
        for (int i = 0; i < hammer; i++)
        {
            lp += a * ((float)(rng.NextDouble() * 2 - 1) - lp);
            s[i] += lp * 0.5f * (1f - (float)i / hammer);
        }
        Attack(s, 0.003f);
        return Finish(s, 0.7f);
    }

    private static float[] ElectricPiano(int midi)
    {
        float f0 = Hz(midi);
        int n = (int)(1.8f * Dsp.Rate);
        var s = new float[n];
        float p1 = 0f, p2 = 0f, p3 = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Dsp.Rate;
            p1 += f0 / Dsp.Rate;
            p2 += f0 * 2f / Dsp.Rate;
            p3 += f0 * 7.1f / Dsp.Rate;
            // the tine: a sine with a soft phase wobble, the bell partials on top only at the strike
            float body = Mathf.Sin(Mathf.Tau * p1 + 0.6f * Mathf.Exp(-6f * t) * Mathf.Sin(Mathf.Tau * p2));
            float bell = 0.35f * Mathf.Sin(Mathf.Tau * p2) * Mathf.Exp(-9f * t)
                + (f0 * 7.1f < Dsp.Rate * 0.45f ? 0.12f * Mathf.Sin(Mathf.Tau * p3) * Mathf.Exp(-30f * t) : 0f);
            float trem = 1f + 0.08f * Mathf.Sin(Mathf.Tau * 4.5f * t);
            s[i] = (body * Mathf.Exp(-1.6f * t) + bell) * trem;
        }
        Attack(s, 0.002f);
        return Finish(s, 0.65f);
    }

    private static readonly SfxRecipe[] Drums =
    {
        // kick: a sine falling from 150 to 48 Hz, a click on top
        new() { Seconds = 0.5f, Drive = 0.6f, Layers = new[] {
            new SfxLayer { Wave = SfxWave.Sine, FreqStart = 150f, FreqEnd = 48f, SlideSeconds = 0.09f, Decay = 7f },
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 3000f, FreqEnd = 800f, Decay = 120f, Gain = 0.35f } } },
        // snare: the shell's tone and the wires' bright noise
        new() { Seconds = 0.35f, Layers = new[] {
            new SfxLayer { Wave = SfxWave.Triangle, FreqStart = 230f, FreqEnd = 180f, SlideSeconds = 0.04f, Decay = 22f, Gain = 0.7f },
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 9000f, FreqEnd = 6000f, HighPassHz = 1500f, Decay = 14f } } },
        // closed hat
        new() { Seconds = 0.12f, Layers = new[] {
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 10000f, FreqEnd = 10000f, HighPassHz = 6500f, Decay = 45f } } },
        // open hat
        new() { Seconds = 0.6f, Layers = new[] {
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 10000f, FreqEnd = 9000f, HighPassHz = 6000f, Decay = 6f },
            new SfxLayer { Wave = SfxWave.Square, FreqStart = 5400f, FreqEnd = 5400f, Duty = 0.3f, Decay = 9f, Gain = 0.08f } } },
        // tom
        new() { Seconds = 0.6f, Layers = new[] {
            new SfxLayer { Wave = SfxWave.Sine, FreqStart = 210f, FreqEnd = 150f, SlideSeconds = 0.12f, Decay = 7f },
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 2000f, FreqEnd = 600f, Decay = 60f, Gain = 0.25f } } },
        // floor tom
        new() { Seconds = 0.8f, Layers = new[] {
            new SfxLayer { Wave = SfxWave.Sine, FreqStart = 130f, FreqEnd = 92f, SlideSeconds = 0.15f, Decay = 5f },
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 1500f, FreqEnd = 500f, Decay = 50f, Gain = 0.25f } } },
        // crash: a long wash of bright noise
        new() { Seconds = 2.2f, Layers = new[] {
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 11000f, FreqEnd = 7000f, SlideSeconds = 1.5f, HighPassHz = 3500f, Decay = 1.6f },
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 4000f, FreqEnd = 2500f, HighPassHz = 900f, Decay = 6f, Gain = 0.4f } } },
        // ride: a ping and a quiet wash
        new() { Seconds = 1.4f, Layers = new[] {
            new SfxLayer { Wave = SfxWave.Square, FreqStart = 3150f, FreqEnd = 3150f, Duty = 0.45f, Decay = 5f, Gain = 0.18f },
            new SfxLayer { Wave = SfxWave.Sine, FreqStart = 4730f, FreqEnd = 4730f, Decay = 4f, Gain = 0.25f },
            new SfxLayer { Wave = SfxWave.Noise, FreqStart = 9000f, FreqEnd = 8000f, HighPassHz = 5000f, Decay = 3f, Gain = 0.5f } } },
    };

    private static float[] Drum(int i)
    {
        var s = Drums[i].Render(1000 + i);
        return Finish(s, i is 2 or 7 ? 0.45f : i is 6 ? 0.55f : 0.85f);
    }

    private static void Attack(float[] s, float seconds)
    {
        int a = Math.Min(s.Length, (int)(seconds * Dsp.Rate));
        for (int i = 0; i < a; i++) s[i] *= (float)i / a;
    }

    /// <summary>A short fade-out (no click on the cut) and the level set.</summary>
    private static float[] Finish(float[] s, float peak)
    {
        int fade = Math.Min(s.Length / 4, Dsp.Rate / 20);
        for (int i = 0; i < fade; i++) s[s.Length - 1 - i] *= (float)i / fade;
        return Dsp.Normalise(s, peak);
    }
}
