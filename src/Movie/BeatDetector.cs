namespace UnitSport.Movie;

/// <summary>A piece of music's beat (#656): its tempo and every beat's time from the start, in seconds.</summary>
public sealed record BeatGrid(double Bpm, double[] Beats)
{
    public static readonly BeatGrid None = new(0, Array.Empty<double>());
}

/// <summary>
/// Finds the beat of a piece of music (#656), offline, in plain C#: an onset envelope (spectral
/// flux of a 1024-point FFT every 128 samples at ~11 kHz), the tempo as the strongest
/// autocorrelation between 60 and 200 BPM (weighted towards 120, which keeps it off half and
/// double tempo), the phase that lines most onsets up, then each beat pulled onto the strongest
/// onset near where the last one says it should be, so a band that drifts is followed.
/// </summary>
public static class BeatDetector
{
    private const int N = 1024, Hop = 128;
    private const double MinBpm = 60, MaxBpm = 200;

    /// <param name="mono">Samples in -1..1.</param>
    /// <param name="rate">Their rate; ~11 kHz is plenty (<see cref="Pcm.Decimate"/>).</param>
    public static BeatGrid Detect(float[] mono, int rate)
    {
        var env = Onsets(mono, rate);
        if (env.Length < 16) return BeatGrid.None;
        double frame = (double)Hop / rate;   // seconds per envelope value

        // tempo: the lag whose autocorrelation is strongest, weighted by a log-normal round 120 BPM
        int minLag = (int)Math.Floor(60 / MaxBpm / frame), maxLag = (int)Math.Ceiling(60 / MinBpm / frame);
        maxLag = Math.Min(maxLag, env.Length / 2);
        if (maxLag <= minLag + 2) return BeatGrid.None;
        var ac = new double[maxLag + 2];
        for (int lag = minLag; lag <= maxLag + 1 && lag < env.Length; lag++)
        {
            double s = 0;
            for (int i = lag; i < env.Length; i++) s += env[i] * env[i - lag];
            ac[lag] = s / (env.Length - lag);
        }
        int best = minLag;
        double bestScore = double.NegativeInfinity;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double bpm = 60 / (lag * frame);
            double w = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 0.9, 2));
            double score = ac[lag] * w;
            if (score > bestScore) { bestScore = score; best = lag; }
        }
        if (bestScore <= 0) return BeatGrid.None;
        // parabolic interpolation between the neighbours: a period finer than one envelope frame
        double l = best;
        if (best > minLag && best < maxLag)
        {
            double a = ac[best - 1], b = ac[best], c = ac[best + 1], d = a - 2 * b + c;
            if (Math.Abs(d) > 1e-12) l += Math.Clamp(0.5 * (a - c) / d, -0.5, 0.5);
        }
        double period = l * frame;

        // phase: the offset in the first period whose grid collects the most onset
        int lagFrames = (int)Math.Round(l);
        int phase = 0;
        double phaseScore = double.NegativeInfinity;
        for (int o = 0; o < lagFrames; o++)
        {
            double s = 0;
            for (double i = o; i < env.Length; i += l) s += env[(int)i];
            if (s > phaseScore) { phaseScore = s; phase = o; }
        }

        // beats: each one pulled onto the strongest onset within a tenth of a period of its prediction
        var beats = new List<double>();
        double duration = (double)mono.Length / rate;
        int window = Math.Max(1, (int)Math.Round(0.1 * l));
        double t = phase * frame + OnsetLag(rate);
        while (t < duration)
        {
            int at = (int)Math.Round((t - OnsetLag(rate)) / frame), pick = at;
            float peak = -1;
            for (int i = Math.Max(0, at - window); i <= Math.Min(env.Length - 1, at + window); i++)
                if (env[i] > peak) { peak = env[i]; pick = i; }
            double snapped = pick * frame + OnsetLag(rate);
            // a quiet stretch keeps the grid instead of wandering after noise
            double beat = peak > 0 ? snapped : t;
            if (beat >= 0 && (beats.Count == 0 || beat - beats[^1] > period * 0.5)) beats.Add(beat);
            t = beat + period;
        }
        return new BeatGrid(60 / period, beats.ToArray());
    }

    // A frame's flux peaks when the onset sits where its Hann window falls fastest, three quarters of
    // the way through it (the next frame's window rises over it): that, not the frame's centre, is
    // where the beat is. Centred, every beat came out 27 ms early on a click track.
    private static double OnsetLag(int rate) => N * 0.75 / rate;

    /// <summary>The onset strength every <see cref="Hop"/> samples: positive log-spectral flux, less its local mean.</summary>
    public static float[] Onsets(float[] mono, int rate)
    {
        int frames = mono.Length < N ? 0 : 1 + (mono.Length - N) / Hop;
        var flux = new float[frames];
        var re = new double[N];
        var im = new double[N];
        var prev = new double[N / 2];
        var window = new double[N];
        for (int i = 0; i < N; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
        for (int f = 0; f < frames; f++)
        {
            int start = f * Hop;
            for (int i = 0; i < N; i++) { re[i] = mono[start + i] * window[i]; im[i] = 0; }
            Fft(re, im);
            double sum = 0;
            for (int k = 1; k < N / 2; k++)
            {
                double mag = Math.Log(1 + 100 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                double d = mag - prev[k];
                if (d > 0 && f > 0) sum += d;
                prev[k] = mag;
            }
            flux[f] = (float)sum;
        }
        // less a moving average over ~0.4 s, so a loud passage is not all onset
        int half = Math.Max(1, (int)(0.2 * rate / Hop));
        var env = new float[frames];
        double run = 0;
        int lo = 0, hi = -1;
        for (int f = 0; f < frames; f++)
        {
            while (hi < Math.Min(frames - 1, f + half)) run += flux[++hi];
            while (lo < f - half) run -= flux[lo++];
            env[f] = (float)Math.Max(0, flux[f] - run / (hi - lo + 1));
        }
        return env;
    }

    /// <summary>In-place radix-2 FFT; the length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }
}
