namespace UnitSport.Movie;

/// <summary>
/// The movie's sound for an export (#786): every sound clip that plays between two timeline times, mixed
/// into one mono track at one rate. Pure: the engine decodes the files, this lays them out.
/// </summary>
public static class Soundtrack
{
    /// <summary>One sound clip to mix: its samples, their rate, and where the clip plays (as on the timeline).</summary>
    public readonly record struct Piece(float[] Samples, int Rate, double Start, double In, double Out);

    /// <summary>
    /// <paramref name="pieces"/> mixed over <paramref name="from"/>..<paramref name="to"/> (timeline seconds) at
    /// <paramref name="rate"/>: each resampled linearly, summed, then scaled down only if the sum would clip.
    /// </summary>
    public static short[] Mix(IReadOnlyList<Piece> pieces, double from, double to, int rate)
    {
        int n = Math.Max(0, (int)Math.Round((to - from) * rate));
        var sum = new float[n];
        foreach (var p in pieces)
        {
            if (p.Samples.Length == 0 || p.Rate <= 0) continue;
            // the stretch of the output this piece covers
            double start = Math.Max(from, p.Start), end = Math.Min(to, p.Start + (p.Out - p.In));
            int a = Math.Max(0, (int)Math.Floor((start - from) * rate)), b = Math.Min(n, (int)Math.Ceiling((end - from) * rate));
            for (int i = a; i < b; i++)
            {
                double t = from + (double)i / rate;               // timeline time
                double source = (p.In + (t - p.Start)) * p.Rate;  // where in the piece's samples
                int k = (int)source;
                if (k < 0 || k >= p.Samples.Length) continue;
                float frac = (float)(source - k);
                float v = k + 1 < p.Samples.Length ? p.Samples[k] + (p.Samples[k + 1] - p.Samples[k]) * frac : p.Samples[k];
                sum[i] += v;
            }
        }
        float peak = 0;
        foreach (float v in sum) peak = Math.Max(peak, Math.Abs(v));
        float gain = peak > 1 ? 1 / peak : 1;
        var pcm = new short[n];
        for (int i = 0; i < n; i++) pcm[i] = (short)Math.Clamp(sum[i] * gain * 32767f, -32768f, 32767f);
        return pcm;
    }
}
