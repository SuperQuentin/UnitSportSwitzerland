namespace UnitSport.Audio.Cd;

/// <summary>
/// What the singer does, from the vocal <see cref="VocalSeparator"/> pulled out of a CD (#1153):
/// level, syllables, vowel, pitch, the mouth's aperture and a consonant's burst per 50 ms frame for
/// lip sync, and the melody's runs, long notes and entries for the light desk. Pure C#, on the burn
/// worker, ~10 ms frames boiled down to <see cref="CdAnalysis.EnvRate"/> Hz.
/// </summary>
public static class VocalAnalyzer
{
    /// <summary>Frames a second of the result: <see cref="CdAnalysis.EnvRate"/>, kept here so the analyser links into the tests without Godot.</summary>
    public const int FrameRate = 20;

    /// <summary>Bytes per frame (v4, #1153): level, syllable, vowel, pitch, the mouth's aperture and a consonant's burst.</summary>
    public const int FrameBytes = 6;

    public sealed record Result(byte[] Frames, float[] Rises, float[] Falls, float[] Holds, float[] VocalIns);

    private const int Win = 1024, Hop = 220;           // at 22.05 kHz: 46 ms window, ~10 ms hop
    private const float MinHz = 70f, MaxHz = 1000f;    // a sung voice, bass to soprano's top
    private const float YinThreshold = 0.15f;
    private const float Gate = 0.05f;                  // of the track's loud vocal: below it is bleed, not singing

    /// <summary>
    /// The first formant's band and its map to the aperture: a closed vowel's F1 sits near 300 Hz, an
    /// open "ah" near 800, so F1 is the jaw without needing a full formant tracker.
    /// </summary>
    private const float OpenLoHz = 300f, OpenHiHz = 800f, EnvelopeHz = 200f;

    /// <summary>Semitones a run must climb (or fall) to count, and its shortest / longest span, s.</summary>
    public const float RunSemitones = 4f, RunMin = 0.5f, RunMax = 8f;

    /// <summary>A long note: within this many semitones of its mean for at least <see cref="HoldMin"/> s.</summary>
    public const float HoldSpread = 1f, HoldMin = 1.2f;   // a held note with vibrato swings about a semitone

    /// <summary>Gap in the singing, s, bridged inside one phrase.</summary>
    private const float PhraseGap = 0.25f;

    public static Result Analyse(float[] vocal, int rate, float bpm)
    {
        int frames = Math.Max(0, (vocal.Length - Win) / Hop + 1);
        var rms = new float[frames];
        var flux = new float[frames];
        var vowel = new float[frames];
        var hz = new float[frames];
        var open = new float[frames];   // the first formant, Hz: how far the jaw is open
        var burst = new float[frames];  // high-band flux: a consonant's bite

        var re = new float[Win];
        var im = new float[Win];
        var prev = new float[Win / 2];
        var win = new float[Win];
        for (int i = 0; i < Win; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / Win);
        float binHz = rate / (float)Win;
        int f1a = (int)(300 / binHz), f1b = (int)(900 / binHz), f2a = (int)(1500 / binHz), f2b = (int)(3000 / binHz);
        int va = (int)(300 / binHz), vb = (int)(4000 / binHz);
        int ca = (int)(250 / binHz), cb = (int)(1100 / binHz);   // where the first formant lives
        int ha = (int)(3000 / binHz), hb = (int)(8000 / binHz);  // where a consonant's burst lives
        var yin = new YinScratch((int)(rate / MinHz) + 2);
        var amp = new float[Win / 2];
        var env = new float[cb - ca + 1];                        // the smoothed low band, reused per frame
        int half = Math.Max(1, (int)(EnvelopeHz / binHz));       // the envelope's half width

        for (int f = 0; f < frames; f++)
        {
            int s = f * Hop;
            double e = 0;
            for (int i = 0; i < Win; i++)
            {
                float x = vocal[s + i];
                e += x * x;
                re[i] = x * win[i];
                im[i] = 0f;
            }
            rms[f] = (float)Math.Sqrt(e / Win);
            Fft.Forward(re, im);
            float e1 = 0f, e2 = 0f, fl = 0f, hi = 0f;
            for (int k = 1; k < Win / 2; k++)
            {
                float a = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                amp[k] = a;
                float mag = MathF.Log(1f + 100f * a);
                if (k >= va && k <= vb) fl += Math.Max(0f, mag - prev[k]);
                if (k >= ha && k <= hb) hi += Math.Max(0f, mag - prev[k]);   // before prev moves on
                prev[k] = mag;
                if (k >= f1a && k <= f1b) e1 += mag;
                else if (k >= f2a && k <= f2b) e2 += mag;
            }
            flux[f] = fl;
            burst[f] = hi;
            open[f] = rms[f] > 2e-3f ? EnvPeak(amp, ca, cb, env, half, binHz) : 0f;   // F1, Hz
            vowel[f] = MathF.Log((e2 / (f2b - f2a + 1) + 1e-3f) / (e1 / (f1b - f1a + 1) + 1e-3f));
            hz[f] = rms[f] > 2e-3f ? Yin(vocal, s, rate, yin) : 0f;   // silence has no pitch
        }

        // per-track references: the loud vocal (99th percentile), the vowel's spread, a consonant's burst
        float loud = Percentile(rms, 0.99f);
        float fluxRef = Math.Max(1e-6f, Percentile(flux, 0.99f));
        float burstRef = Math.Max(1e-6f, Percentile(burst, 0.99f));
        var sung = new List<float>();
        for (int f = 0; f < frames; f++) if (loud > 0f && rms[f] >= Gate * loud) sung.Add(vowel[f]);
        float vLo = sung.Count > 0 ? Percentile(sung.ToArray(), 0.1f) : 0f, vHi = sung.Count > 0 ? Percentile(sung.ToArray(), 0.9f) : 1f;

        // local-mean-subtracted flux: a syllable is a jump above the last ~0.3 s
        var onset = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float mean = 0f;
            int from = Math.Max(0, f - 30);
            for (int j = from; j < f; j++) mean += flux[j];
            mean = f > from ? mean / (f - from) : 0f;
            onset[f] = Math.Max(0f, flux[f] - mean);
        }
        float onsetRef = Math.Max(1e-6f, Percentile(onset, 0.99f));

        // median of 3 over F1: one harmonic jumps it, a vowel does not
        var openS = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            if (open[f] <= 0f) continue;
            float o0 = f > 0 && open[f - 1] > 0f ? open[f - 1] : open[f];
            float o2 = f + 1 < frames && open[f + 1] > 0f ? open[f + 1] : open[f];
            openS[f] = Median3(o0, open[f], o2);
        }

        // down to EnvRate: max level, onset and burst, median pitch over voiced 10 ms frames,
        // mean vowel and aperture
        double perOut = rate / (double)FrameRate / Hop;
        int outN = (int)(vocal.Length / (double)rate * FrameRate);
        var bytes = new byte[outN * FrameBytes];
        var pitch = new float[outN];
        var level = new float[outN];
        var buf = new List<float>();
        for (int o = 0; o < outN; o++)
        {
            int a = (int)(o * perOut), b = Math.Min(frames, (int)((o + 1) * perOut));
            float lv = 0f, on = 0f, vw = 0f, ap = 0f, bu = 0f;
            int sungN = 0, openN = 0;
            buf.Clear();
            for (int f = a; f < b; f++)
            {
                lv = Math.Max(lv, rms[f]);
                on = Math.Max(on, onset[f]);
                bu = Math.Max(bu, burst[f]);
                if (loud > 0f && rms[f] >= Gate * loud)
                {
                    vw += vowel[f];
                    sungN++;
                    if (hz[f] > 0f) buf.Add(Midi(hz[f]));
                    if (openS[f] > 0f)
                    {
                        ap += Math.Clamp((openS[f] - OpenLoHz) / (OpenHiHz - OpenLoHz), 0f, 1f);
                        openN++;
                    }
                }
            }
            bool singing = loud > 0f && lv >= Gate * loud;
            level[o] = singing ? Math.Min(1f, lv / loud) : 0f;
            bytes[FrameBytes * o] = (byte)(level[o] * 255f);
            bytes[FrameBytes * o + 1] = singing ? (byte)(Math.Min(1f, on / onsetRef) * 255f) : (byte)0;
            float v = sungN > 0 && vHi > vLo ? Math.Clamp((vw / sungN - vLo) / (vHi - vLo), 0f, 1f) : 0.5f;
            bytes[FrameBytes * o + 2] = (byte)(v * 255f);
            bytes[FrameBytes * o + 4] = singing && openN > 0 ? (byte)(ap / openN * 255f) : (byte)0;
            bytes[FrameBytes * o + 5] = (byte)(Math.Min(1f, bu / burstRef) * 255f);
            if (buf.Count * 2 >= Math.Max(1, b - a)) { buf.Sort(); pitch[o] = buf[buf.Count / 2]; }
        }
        // median of 3 over the pitch: one octave error in a frame is not a run
        var smooth = new float[outN];
        for (int o = 0; o < outN; o++)
        {
            if (pitch[o] == 0f) continue;
            float p0 = o > 0 && pitch[o - 1] > 0f ? pitch[o - 1] : pitch[o], p2 = o + 1 < outN && pitch[o + 1] > 0f ? pitch[o + 1] : pitch[o];
            smooth[o] = Median3(p0, pitch[o], p2);
            bytes[FrameBytes * o + 3] = (byte)Math.Clamp(MathF.Round(smooth[o]), 1f, 127f);
        }

        Melody(smooth, out var rises, out var falls, out var holds);
        return new Result(bytes, rises, falls, holds, Entries(level, bpm));
    }

    /// <summary>
    /// The melody's runs and long notes, from the 20 Hz pitch (0 = unvoiced). A phrase is sung
    /// frames with gaps up to <see cref="PhraseGap"/>; inside it a rise runs from a low to the
    /// highest point before the tune drops a semitone below it (a fall, mirrored).
    /// </summary>
    public static void Melody(float[] pitch, out float[] rises, out float[] falls, out float[] holds)
    {
        var r = new List<float>();
        var fl = new List<float>();
        var h = new List<float>();
        float dt = 1f / FrameRate;
        int gap = (int)(PhraseGap * FrameRate);
        int n = pitch.Length, i = 0;
        while (i < n)
        {
            if (pitch[i] == 0f) { i++; continue; }
            int end = i, quiet = 0;
            for (int j = i; j < n; j++)
            {
                if (pitch[j] > 0f) { end = j; quiet = 0; }
                else if (++quiet > gap) break;
            }
            // the phrase's voiced frames, gaps held at the last note
            var p = new float[end - i + 1];
            float last = pitch[i];
            for (int j = i; j <= end; j++) p[j - i] = last = pitch[j] > 0f ? pitch[j] : last;
            Runs(p, i, +1f, r);
            Runs(p, i, -1f, fl);
            Holds(p, i, h);
            i = end + 1;
        }
        rises = r.ToArray();
        falls = fl.ToArray();
        holds = h.ToArray();

        void Runs(float[] p, int at, float sign, List<float> into)
        {
            int lo = 0, hi = 0;
            for (int j = 1; j <= p.Length; j++)
            {
                bool done = j == p.Length || sign * (p[j] - p[hi]) < -1f;
                if (!done)
                {
                    if (sign * (p[j] - p[hi]) > 0f) hi = j;
                    if (sign * (p[j] - p[lo]) < 0f && hi < j) { lo = j; hi = j; }
                    continue;
                }
                float span = (hi - lo) * dt;
                if (sign * (p[hi] - p[lo]) >= RunSemitones && span >= RunMin && span <= RunMax)
                    into.AddRange(new[] { (at + lo) * dt, (at + hi) * dt });
                lo = hi = j;
            }
        }

        void Holds(float[] p, int at, List<float> into)
        {
            int start = 0;
            float sum = 0f;
            for (int j = 0; j <= p.Length; j++)
            {
                int count = j - start;
                if (j < p.Length && (count == 0 || MathF.Abs(p[j] - sum / count) <= HoldSpread)) { sum += p[j]; continue; }
                if (count * dt >= HoldMin) into.AddRange(new[] { (at + start) * dt, (at + j) * dt });
                if (j < p.Length) { start = j; sum = p[j]; }
            }
        }
    }

    /// <summary>Where the voice comes back after two bars (at <paramref name="bpm"/>; 4 s when unknown) without it, or first starts.</summary>
    public static float[] Entries(float[] level, float bpm)
    {
        float bars2 = bpm > 0f ? 8f * 60f / bpm : 4f;
        float dt = 1f / FrameRate;
        var ins = new List<float>();
        float lastSung = float.NegativeInfinity;
        for (int o = 0; o < level.Length; o++)
        {
            if (level[o] <= 0f) continue;
            float t = o * dt;
            if (t - lastSung >= bars2) ins.Add(t);
            lastSung = t;
        }
        return ins.ToArray();
    }

    /// <summary>YIN's buffers: the difference, the window's running energy, one 2048-point transform.</summary>
    private sealed class YinScratch(int lags)
    {
        public const int M = 2 * Win;
        public readonly float[] D = new float[lags], Energy = new float[Win + 1], Re = new float[M], Im = new float[M];
    }

    /// <summary>
    /// YIN (de Cheveigné & Kawahara 2002) on one window: the fundamental in Hz, or 0 when not periodic
    /// enough. The difference sum (x_i - x_i+tau)^2 is taken as two energies minus twice the
    /// correlation, and the correlation through one FFT: the window's head (a) and the whole window
    /// (b) packed as a + ib, split by symmetry, conj(A) B transformed back.
    /// </summary>
    private static float Yin(float[] x, int s, int rate, YinScratch y)
    {
        float[] d = y.D, en = y.Energy, re = y.Re, im = y.Im;
        int tMin = (int)(rate / MaxHz), tMax = Math.Min(d.Length - 1, Win / 2);
        int w = Win - tMax, m = YinScratch.M;
        en[0] = 0f;
        for (int i = 0; i < Win; i++) en[i + 1] = en[i] + x[s + i] * x[s + i];
        Array.Clear(re);
        Array.Clear(im);
        for (int i = 0; i < Win; i++)
        {
            if (i < w) re[i] = x[s + i];
            im[i] = x[s + i];
        }
        Fft.Forward(re, im);
        // P = conj(A) B for k and m-k at once, stored conjugated for the inverse through Forward
        for (int k = 0; k <= m / 2; k++)
        {
            int j = (m - k) % m;
            float ar = 0.5f * (re[k] + re[j]), ai = 0.5f * (im[k] - im[j]);
            float br = 0.5f * (im[k] + im[j]), bi = -0.5f * (re[k] - re[j]);
            float pr = ar * br + ai * bi, pi = ar * bi - ai * br;
            re[k] = pr; im[k] = -pi;
            re[j] = pr; im[j] = pi;   // conj(P[k]) = P[m-k] for a real correlation, then conjugated
        }
        Fft.Forward(re, im);
        d[0] = 1f;
        float run = 0f;
        for (int tau = 1; tau <= tMax; tau++)
        {
            float sum = Math.Max(0f, en[w] + (en[tau + w] - en[tau]) - 2f * re[tau] / m);
            run += sum;
            d[tau] = run > 0f ? sum * tau / run : 1f;   // cumulative mean normalised difference
        }
        for (int tau = tMin; tau < tMax; tau++)
        {
            if (d[tau] >= YinThreshold) continue;
            while (tau + 1 < tMax && d[tau + 1] < d[tau]) tau++;
            // parabolic interpolation of the dip
            float a = d[tau - 1], b = d[tau], c = d[tau + 1];
            float den = a - 2f * b + c;
            float shift = MathF.Abs(den) > 1e-9f ? 0.5f * (a - c) / den : 0f;
            return rate / (tau + shift);
        }
        return 0f;
    }

    private static float Midi(float hz) => 69f + 12f * MathF.Log2(hz / 440f);

    private static float Median3(float a, float b, float c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    /// <summary>
    /// The first spectral-envelope peak in bins <paramref name="lo"/>..<paramref name="hi"/>: F1, which
    /// follows the jaw. The band is box-smoothed over ~200 Hz so harmonics do not pass for formants,
    /// and the lowest local maximum above 55 % of the band's maximum wins, so a strong F2 does not
    /// read as an open vowel; the plain maximum when no peak qualifies. Returns Hz, 0 in silence.
    /// </summary>
    private static float EnvPeak(float[] amp, int lo, int hi, float[] env, int half, float binHz)
    {
        int n = hi - lo + 1;
        float max = 0f;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(n - 1, i + half);
            float sum = 0f;
            for (int j = a; j <= b; j++) sum += amp[lo + j];
            env[i] = sum / (b - a + 1);
            if (env[i] > max) max = env[i];
        }
        if (max <= 1e-9f) return 0f;
        int pick = 0;
        for (int i = 1; i < n - 1; i++)
            if (env[i] >= env[i - 1] && env[i] > env[i + 1] && env[i] >= 0.55f * max) { pick = i; break; }
        if (pick == 0)
            for (int i = 1; i < n; i++) if (env[i] > env[pick]) pick = i;
        return (lo + pick) * binHz;
    }

    private static float Percentile(float[] v, float q)
    {
        if (v.Length == 0) return 0f;
        var c = (float[])v.Clone();
        Array.Sort(c);
        return c[Math.Clamp((int)(q * (c.Length - 1)), 0, c.Length - 1)];
    }
}
