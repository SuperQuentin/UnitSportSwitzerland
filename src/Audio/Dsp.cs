using Godot;

namespace UnitSport.Audio;

/// <summary>
/// The shared building blocks of every synthesised sound: noise, one-pole filters, loop and
/// one-shot baking, and 16-bit encoding. Baked sounds (<see cref="SfxSynth"/>, <see cref="SfxBank"/>)
/// and live ones (<see cref="EngineSynth"/>, ambience) all run at <see cref="Rate"/>.
/// </summary>
public static class Dsp
{
    public const int Rate = 22050;

    public static float[] Noise(Random rng, int n)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = (float)(rng.NextDouble() * 2 - 1);
        return s;
    }

    /// <summary>One-pole low-pass; <paramref name="a"/> is the per-sample coefficient (0..1).</summary>
    public static float[] LowPass(float[] x, float a)
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

    public static float[] HighPass(float[] x, float a)
    {
        var low = LowPass(x, a);
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i] - low[i];
        return y;
    }

    public static float[] BandPass(float[] x, float lowCut, float highCut)
    {
        var upper = LowPass(x, highCut);
        var lower = LowPass(x, lowCut);
        for (int i = 0; i < x.Length; i++) upper[i] -= lower[i];
        return upper;
    }

    /// <summary>One-pole coefficient for a cutoff in Hz, for the per-sample filters above.</summary>
    public static float Coef(float hz) => 1f - Mathf.Exp(-Mathf.Tau * hz / Rate);

    /// <summary>
    /// A seamless loop: the synthesised tail is crossfaded into the head, so the join has no
    /// click — random noise does not otherwise end where it began.
    /// </summary>
    public static AudioStreamWav Loop(float seconds, int seed, Func<Random, int, float[]> make)
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
        return LoopWav(Pcm16(Normalise(s, 0.8f)), stereo: false, Rate);
    }

    /// <summary>
    /// A forward loop over 16-bit PCM (<paramref name="pcm"/>, whole frames, mono or interleaved
    /// stereo). Godot mixes a loop up to <c>LoopEnd</c> inclusive (4.7 <c>audio_stream_wav.cpp</c>:
    /// <c>end_limit = loop_end</c>), so the buffer carries one frame more, a copy of the first, and
    /// <c>LoopEnd</c> is the frame count: that last read stays in the buffer and is the head the loop
    /// wraps to. Ended at the frame count, every pass read a frame past the buffer, which on Android
    /// is the guard page after a large allocation: the wind killed the game (#1010).
    /// </summary>
    public static AudioStreamWav LoopWav(byte[] pcm, bool stereo, int mixRate)
    {
        int frameBytes = stereo ? 4 : 2, frames = pcm.Length / frameBytes;
        var data = new byte[frames * frameBytes + frameBytes];
        Array.Copy(pcm, data, frames * frameBytes);
        Array.Copy(pcm, 0, data, frames * frameBytes, frameBytes);
        return new AudioStreamWav
        {
            Data = data, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = mixRate, Stereo = stereo,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = frames,
        };
    }

    public static AudioStreamWav OneShot(float seconds, int seed, Func<Random, int, float[]> make)
        => Encode(RenderOneShot(seconds, seed, make));

    /// <summary><see cref="OneShot"/> without the encoding, for offline checks.</summary>
    public static float[] RenderOneShot(float seconds, int seed, Func<Random, int, float[]> make)
    {
        int n = (int)(seconds * Rate);
        var s = make(new Random(seed), n);
        // a short fade-out, or a cut-off tail clicks
        int fade = Math.Min(n / 4, Rate / 100);
        for (int i = 0; i < fade; i++) s[n - 1 - i] *= (float)i / fade;
        return Normalise(s, 0.9f);
    }

    public static float[] Normalise(float[] s, float peak)
    {
        float max = 1e-6f;
        foreach (float v in s) max = Math.Max(max, Math.Abs(v));
        for (int i = 0; i < s.Length; i++) s[i] *= peak / max;
        return s;
    }

    /// <summary>Mono float samples as 16-bit little-endian PCM.</summary>
    public static byte[] Pcm16(float[] s)
    {
        var bytes = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            short v = (short)Math.Clamp((int)(s[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    public static AudioStreamWav Encode(float[] s)
    {
        return new AudioStreamWav
        {
            Data = Pcm16(s),
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Rate,
            Stereo = false,
        };
    }

    /// <summary>Writes mono float samples as a 16-bit WAV file (for <c>--soundcheck</c>).</summary>
    public static void WriteWav(string path, float[] s)
    {
        using var f = System.IO.File.Create(path);
        using var w = new System.IO.BinaryWriter(f);
        w.Write("RIFF"u8); w.Write(36 + s.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(s.Length * 2);
        foreach (float v in s) w.Write((short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue));
    }
}
