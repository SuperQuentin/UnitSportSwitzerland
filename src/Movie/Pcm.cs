using System.Text;

namespace UnitSport.Movie;

/// <summary>Plain sample helpers for the studio's audio (#656): no engine, unit tested.</summary>
public static class Pcm
{
    /// <summary>
    /// <paramref name="mono"/> brought down to about <paramref name="target"/> Hz by averaging whole
    /// groups of samples (a crude low-pass, plenty for finding beats). The rate it ends at comes out.
    /// </summary>
    public static float[] Decimate(float[] mono, int rate, int target, out int newRate)
    {
        int factor = Math.Max(1, (int)Math.Round((double)rate / target));
        newRate = rate / factor;
        if (factor == 1) return mono;
        var o = new float[mono.Length / factor];
        for (int i = 0; i < o.Length; i++)
        {
            float s = 0;
            for (int k = 0; k < factor; k++) s += mono[i * factor + k];
            o[i] = s / factor;
        }
        return o;
    }

    /// <summary>The loudest sample in each 1/<paramref name="perSecond"/> s, 0..255: what the timeline draws as a waveform.</summary>
    public static byte[] Peaks(float[] mono, int rate, int perSecond)
    {
        int step = Math.Max(1, rate / perSecond);
        var o = new byte[(mono.Length + step - 1) / step];
        for (int i = 0; i < o.Length; i++)
        {
            float m = 0;
            for (int k = i * step; k < Math.Min(mono.Length, (i + 1) * step); k++) m = Math.Max(m, Math.Abs(mono[k]));
            o[i] = (byte)Math.Clamp(m * 255, 0, 255);
        }
        return o;
    }

    /// <summary>16-bit mono samples as a .wav file.</summary>
    public static void WriteWav(Stream to, ReadOnlySpan<short> samples, int rate)
    {
        using var w = new BinaryWriter(to, Encoding.ASCII, leaveOpen: true);
        int bytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        foreach (short s in samples) w.Write(s);
    }
}
