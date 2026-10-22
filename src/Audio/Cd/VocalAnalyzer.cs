namespace UnitSport.Audio.Cd;

/// <summary>
/// What the singer does, from the vocal <see cref="VocalSeparator"/> pulled out of a CD (#1153):
/// level, syllables, vowel and pitch per 50 ms frame for lip sync, and the melody's runs, long notes
/// and entries for the light desk. Pure C#, on the burn worker, ~10 ms frames boiled down to
/// <see cref="CdAnalysis.EnvRate"/> Hz.
/// </summary>
public static class VocalAnalyzer
{
    public sealed record Result(byte[] Frames, float[] Rises, float[] Falls, float[] Holds, float[] VocalIns);

    private const int Win = 1024, Hop = 220;           // at 22.05 kHz: 46 ms window, ~10 ms hop
    private const float MinHz = 70f, MaxHz = 1000f;    // a sung voice, bass to soprano's top
    private const float YinThreshold = 0.15f;
    private const float Gate = 0.08f;                  // of the track's loud vocal: below it is bleed, not singing

    /// <summary>Semitones a run must climb (or fall) to count, and its shortest / longest span, s.</summary>
    public const float RunSemitones = 4f, RunMin = 0.5f, RunMax = 8f;

    /// <summary>A long note: within this many semitones of its mean for at least <see cref="HoldMin"/> s.</summary>
    public const float HoldSpread = 0.6f, HoldMin = 1.2f;

    /// <summary>Gap in the singing, s, bridged inside one phrase.</summary>
    private const float PhraseGap = 0.25f;

    public static Result Analyse(float[] vocal, int rate, float bpm)
    {
        int frames = Math.Max(0, (vocal.Length - Win) / Hop + 1);
        var rms = new float[frames];
        var flux = new float[frames];
        var vowel = new float[frames];
        var hz = new float[frames];

        var re = new float[Win];
        var im = new float[Win];
        var prev = new float[Win / 2];
        var win = new float[Win];
        for (int i = 0; i < Win; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / Win);
        float binHz = rate / (float)Win;
        int f1a = (int)(300 / binHz), f1b = (int)(900 / binHz), f2a = (int)(1500 / binHz), f2b = (int)(3000 / binHz);
        int va = (int)(300 / binHz), vb = (int)(4000 / binHz);
        var diff = new float[(int)(rate / MinHz) + 2];

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
            float e1 = 0f, e2 = 0f, fl = 0f;
            for (int k = 1; k < Win / 2; k++)
            {
                float mag = MathF.Log(1f + 100f * MathF.Sqrt(re[k] * re[k] + im[k] * im[k]));
                if (k >= va && k <= vb) fl += Math.Max(0f, mag - prev[k]);
                prev[k] = mag;
                if (k >= f1a && k <= f1b) e1 += mag;
                else if (k >= f2a && k <= f2b) e2 += mag;
            }
            flux[f] = fl;
            vowel[f] = MathF.Log((e2 / (f2b - f2a + 1) + 1e-3f) / (e1 / (f1b - f1a + 1) + 1e-3f));
            hz[f] = rms[f] > 2e-3f ? Yin(vocal, s, rate, diff) : 0f;   // silence has no pitch, and YIN is the costly part
        }

        // per-track references: the loud vocal (99th percentile), the vowel's spread
        float loud = Percentile(rms, 0.99f);
        float fluxRef = Math.Max(1e-6f, Percentile(flux, 0.99f));
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

        // down to EnvRate: max level and onset, median pitch over voiced 10 ms frames, mean vowel
        double perOut = rate / (double)CdAnalysis.EnvRate / Hop;
        int outN = (int)(vocal.Length / (double)rate * CdAnalysis.EnvRate);
        var bytes = new byte[outN * 4];
        var pitch = new float[outN];
        var level = new float[outN];
        var buf = new List<float>();
        for (int o = 0; o < outN; o++)
        {
            int a = (int)(o * perOut), b = Math.Min(frames, (int)((o + 1) * perOut));
            float lv = 0f, on = 0f, vw = 0f;
            int sungN = 0;
            buf.Clear();
            for (int f = a; f < b; f++)
            {
                lv = Math.Max(lv, rms[f]);
                on = Math.Max(on, onset[f]);
                if (loud > 0f && rms[f] >= Gate * loud)
                {
                    vw += vowel[f];
                    sungN++;
                    if (hz[f] > 0f) buf.Add(Midi(hz[f]));
                }
            }
            bool singing = loud > 0f && lv >= Gate * loud;
            level[o] = singing ? Math.Min(1f, lv / loud) : 0f;
            bytes[4 * o] = (byte)(level[o] * 255f);
            bytes[4 * o + 1] = singing ? (byte)(Math.Min(1f, on / onsetRef) * 255f) : (byte)0;
            float v = sungN > 0 && vHi > vLo ? Math.Clamp((vw / sungN - vLo) / (vHi - vLo), 0f, 1f) : 0.5f;
            bytes[4 * o + 2] = (byte)(v * 255f);
            if (buf.Count * 2 >= Math.Max(1, b - a)) { buf.Sort(); pitch[o] = buf[buf.Count / 2]; }
        }
        // median of 3 over the pitch: one octave error in a frame is not a run
        var smooth = new float[outN];
        for (int o = 0; o < outN; o++)
        {
            if (pitch[o] == 0f) continue;
            float p0 = o > 0 && pitch[o - 1] > 0f ? pitch[o - 1] : pitch[o], p2 = o + 1 < outN && pitch[o + 1] > 0f ? pitch[o + 1] : pitch[o];
            smooth[o] = Median3(p0, pitch[o], p2);
            bytes[4 * o + 3] = (byte)Math.Clamp(MathF.Round(smooth[o]), 1f, 127f);
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
        float dt = 1f / CdAnalysis.EnvRate;
        int gap = (int)(PhraseGap * CdAnalysis.EnvRate);
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
        float dt = 1f / CdAnalysis.EnvRate;
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

    /// <summary>YIN (de Cheveigné & Kawahara 2002) on one window: the fundamental in Hz, or 0 when not periodic enough.</summary>
    private static float Yin(float[] x, int s, int rate, float[] d)
    {
        int tMin = (int)(rate / MaxHz), tMax = Math.Min(d.Length - 1, Win / 2);
        int w = Win - tMax;
        d[0] = 1f;
        float run = 0f;
        for (int tau = 1; tau <= tMax; tau++)
        {
            float sum = 0f;
            for (int i = 0; i < w; i++)
            {
                float v = x[s + i] - x[s + i + tau];
                sum += v * v;
            }
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

    private static float Percentile(float[] v, float q)
    {
        if (v.Length == 0) return 0f;
        var c = (float[])v.Clone();
        Array.Sort(c);
        return c[Math.Clamp((int)(q * (c.Length - 1)), 0, c.Length - 1)];
    }
}
