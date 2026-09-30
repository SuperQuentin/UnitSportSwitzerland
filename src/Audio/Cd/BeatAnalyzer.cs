using System;
using System.Globalization;

namespace UnitSport.Audio.Cd;

/// <summary>
/// Tempo, first-beat offset and style of a track, from its mono samples. Pure C#, no Godot:
/// runs on the burner's worker thread and in the <c>--beatcheck</c> self-test.
/// </summary>
public static class BeatAnalyzer
{
    private const int Win = 1024;
    private const int Hop = 256;
    private const int Bins = Win / 2;

    /// <summary>Samples between a frame's start and the transient the spectral flux fires on (tuned on clicks).</summary>
    private const float OnsetDelay = 576f;

    /// <summary>
    /// Analyses <paramref name="mono"/> at <paramref name="rate"/> Hz (22050 from the burner).
    /// </summary>
    /// <returns>Tempo in BPM (60..200), seconds to the first beat, style class, loudness 0..1.</returns>
    public static (float bpm, float beatOffset, MusicStyle style, float energy) Analyse(float[] mono, int rate)
    {
        if (mono == null || mono.Length == 0 || rate < 8000) return (120f, 0f, MusicStyle.Pop, 0f);

        double sumSq = 0;
        for (int i = 0; i < mono.Length; i++) sumSq += (double)mono[i] * mono[i];
        float rmsAll = (float)Math.Sqrt(sumSq / mono.Length);
        float energy = Math.Clamp(rmsAll / 0.3f, 0f, 1f);

        float fps = rate / (float)Hop;
        int n = mono.Length >= Win ? (mono.Length - Win) / Hop + 1 : 0;
        if (n < (int)(fps * 3f)) return (120f, 0f, MusicStyle.Pop, energy);

        // ---- Spectral flux, plus the per-frame features the style rules need ----
        float[] flux = new float[n];
        float[] frameRms = new float[n];
        float[] hann = new float[Win];
        for (int i = 0; i < Win; i++) hann[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / Win);
        float[] re = new float[Win], im = new float[Win];
        float[] magA = new float[Bins], magB = new float[Bins], prevLog = new float[Bins];
        int lowBin = Math.Clamp((int)(150f * Win / rate), 1, Bins - 1);
        double lowSum = 0, totSum = 0, cenSum = 0;

        for (int f = 0; f < n; f += 2)
        {
            bool two = f + 1 < n;
            int s0 = f * Hop, s1 = s0 + Hop;
            double ssA = 0, ssB = 0;
            for (int k = 0; k < Win; k++)
            {
                float a = mono[s0 + k];
                float b = two ? mono[s1 + k] : 0f;
                ssA += a * a;
                ssB += b * b;
                re[k] = a * hann[k];
                im[k] = b * hann[k];
            }
            frameRms[f] = (float)Math.Sqrt(ssA / Win);
            if (two) frameRms[f + 1] = (float)Math.Sqrt(ssB / Win);

            Fft.Forward(re, im);
            // Two real frames packed in one complex FFT: split them by symmetry.
            for (int k = 1; k < Bins; k++)
            {
                int j = Win - k;
                float ar = 0.5f * (re[k] + re[j]), ai = 0.5f * (im[k] - im[j]);
                float br = 0.5f * (im[k] + im[j]), bi = 0.5f * (re[j] - re[k]);
                magA[k] = MathF.Sqrt(ar * ar + ai * ai);
                magB[k] = MathF.Sqrt(br * br + bi * bi);
            }

            for (int w = 0; w < (two ? 2 : 1); w++)
            {
                float[] mag = w == 0 ? magA : magB;
                int idx = f + w;
                float fl = 0f, tot = 0f, low = 0f, cw = 0f;
                for (int k = 1; k < Bins; k++)
                {
                    float m = mag[k];
                    float lg = MathF.Log(1f + m);
                    float d = lg - prevLog[k];
                    if (d > 0f) fl += d;
                    prevLog[k] = lg;
                    tot += m;
                    cw += m * k;
                    if (k <= lowBin) low += m;
                }
                flux[idx] = idx == 0 ? 0f : fl;
                totSum += tot;
                lowSum += low;
                cenSum += cw;
            }
        }

        float lowRatio = totSum > 1e-9 ? (float)(lowSum / totSum) : 0f;
        float centroid = totSum > 1e-9 ? (float)(cenSum / totSum * rate / Win) : 0f;

        float rmsMean = 0f;
        for (int i = 0; i < n; i++) rmsMean += frameRms[i];
        rmsMean /= n;
        double rmsVar = 0;
        for (int i = 0; i < n; i++) { double d = frameRms[i] - rmsMean; rmsVar += d * d; }
        float rmsCv = rmsMean > 1e-6f ? (float)(Math.Sqrt(rmsVar / n) / rmsMean) : 0f;

        // ---- Onset envelope: flux minus its local mean (~0.5 s), half-wave rectified ----
        float[] env = LocalMeanSubtract(flux, Math.Max(3, (int)(fps * 0.5f)) | 1);
        double envSum = 0;
        for (int i = 0; i < n; i++) envSum += env[i];
        float envMean = (float)(envSum / n);
        if (envMean < 1e-9f) return (120f, 0f, MusicStyle.Chill, energy);

        // ---- Tempo: autocorrelation with a log-Gaussian prior around 120 BPM ----
        float[] acf = Autocorrelation(env, envMean, Math.Min(n - 2, (int)(8.5f * fps) + 4));
        int acfLen = acf.Length;
        int lagMin = Math.Max(2, (int)MathF.Floor(fps * 60f / 200f));
        int lagMax = Math.Min(acfLen - 2, (int)MathF.Ceiling(fps * 60f / 60f));
        if (lagMax <= lagMin + 2) return (120f, 0f, MusicStyle.Pop, energy);

        int bestLag = lagMin;
        float bestScore = float.MinValue;
        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            float bpmL = 60f * fps / lag;
            float oct = MathF.Log2(bpmL / 120f) / 0.9f;
            float score = MathF.Max(acf[lag], 0f) * MathF.Exp(-0.5f * oct * oct);
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }
        float lagF = Parabolic(acf, bestLag);

        // Half / double tempo: prefer the candidate nearest the 90..150 comfort zone when scores are close.
        (float lag, float val)[] cands =
        {
            PeakNear(acf, lagF, 1),
            PeakNear(acf, lagF * 2f, Math.Max(1, (int)(lagF * 0.06f))),   // half tempo
            PeakNear(acf, lagF * 0.5f, 1),                                // double tempo
        };
        float maxVal = Math.Max(cands[0].val, Math.Max(cands[1].val, cands[2].val));
        float chosenLag = cands[0].lag;
        float chosenDist = ComfortDistance(60f * fps / cands[0].lag);
        for (int c = 1; c < cands.Length; c++)
        {
            float b = 60f * fps / cands[c].lag;
            if (b < 60f || b > 200f || cands[c].val <= 0f) continue;
            if (cands[c].val < 0.85f * maxVal) continue;
            float d = ComfortDistance(b);
            if (d < chosenDist - 1e-3f) { chosenDist = d; chosenLag = cands[c].lag; }
        }
        (float lag, float val) chosen = PeakNear(acf, chosenLag, 1);
        float regularity = chosen.val;

        // Refine with the later multiples of the period: the error shrinks with each one.
        float period = chosen.lag;
        for (int m = 2; m <= 8; m++)
        {
            float target = m * period;
            if (target > acfLen - 3) break;
            (float lag, float val) p = PeakNear(acf, target, Math.Max(2, (int)(0.04f * target)));
            if (p.val <= 0.1f * chosen.val) break;
            period = p.lag / m;
        }
        float bpm = Math.Clamp(60f * fps / period, 60f, 200f);
        period = 60f * fps / bpm;

        // ---- Beat offset: comb sum over phases, snapped to nearby onset peaks ----
        float offset = BeatOffset(env, period, rate);

        // ---- Style ----
        float onsetsPerSec = CountOnsets(env, envMean) / (n / fps);
        MusicStyle style = Classify(bpm, onsetsPerSec, lowRatio, centroid, rmsCv, regularity, energy);
        return (bpm, offset, style, energy);
    }

    private static float ComfortDistance(float bpm) => bpm < 90f ? 90f - bpm : bpm > 150f ? bpm - 150f : 0f;

    private static float[] LocalMeanSubtract(float[] x, int window)
    {
        int n = x.Length;
        double[] pre = new double[n + 1];
        for (int i = 0; i < n; i++) pre[i + 1] = pre[i] + x[i];
        float[] y = new float[n];
        int h = window / 2;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - h), b = Math.Min(n, i + h + 1);
            float mean = (float)((pre[b] - pre[a]) / (b - a));
            float v = x[i] - mean;
            y[i] = v > 0f ? v : 0f;
        }
        return y;
    }

    /// <summary>Normalised autocorrelation of the mean-removed envelope, lags 0..maxLag (acf[0] = 1).</summary>
    private static float[] Autocorrelation(float[] env, float mean, int maxLag)
    {
        int n = env.Length;
        float[] e = new float[n];
        for (int i = 0; i < n; i++) e[i] = env[i] - mean;
        float[] r = new float[maxLag + 1];
        double r0 = 0;
        for (int i = 0; i < n; i++) r0 += (double)e[i] * e[i];
        r0 /= n;
        if (r0 < 1e-20) return r;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double s = 0;
            int cnt = n - lag;
            for (int i = 0; i < cnt; i++) s += e[i] * e[i + lag];
            r[lag] = (float)(s / cnt / r0);
        }
        return r;
    }

    /// <summary>Sub-frame peak position by parabolic interpolation around integer index <paramref name="l"/>.</summary>
    private static float Parabolic(float[] a, int l)
    {
        if (l < 1 || l >= a.Length - 1) return l;
        float y0 = a[l - 1], y1 = a[l], y2 = a[l + 1];
        float den = y0 - 2f * y1 + y2;
        if (den >= -1e-9f) return l;
        return l + Math.Clamp(0.5f * (y0 - y2) / den, -0.5f, 0.5f);
    }

    /// <summary>Highest autocorrelation peak within +-<paramref name="tol"/> lags of <paramref name="lag"/>, refined.</summary>
    private static (float lag, float val) PeakNear(float[] a, float lag, int tol)
    {
        int c = (int)MathF.Round(lag);
        int lo = Math.Max(1, c - tol), hi = Math.Min(a.Length - 2, c + tol);
        if (hi < lo) return (lag, 0f);
        int best = lo;
        for (int l = lo; l <= hi; l++) if (a[l] > a[best]) best = l;
        return (Parabolic(a, best), a[best]);
    }

    private static float BeatOffset(float[] env, float period, int rate)
    {
        int n = env.Length;
        int limit = (int)Math.Min(n - 1, 64 * (double)period);
        if (limit < 2 * period) limit = n - 1;
        int steps = Math.Max(1, (int)MathF.Ceiling(period));
        float bestSum = -1f;
        float bestPh = 0f;
        for (int ph = 0; ph < steps; ph++)
        {
            float sum = 0f;
            int cnt = 0;
            for (float t = ph; t < limit; t += period)
            {
                sum += Interp(env, t);
                cnt++;
            }
            if (cnt == 0) continue;
            sum /= cnt;
            if (sum > bestSum) { bestSum = sum; bestPh = ph; }
        }

        // Snap: median deviation of the nearest strong onset peak around each comb position.
        int reach = Math.Max(1, (int)(0.2f * period));
        float[] devs = new float[128];
        int nd = 0;
        float gate = 0.25f * bestSum;
        for (float t = bestPh; t < limit && nd < devs.Length; t += period)
        {
            int c = (int)MathF.Round(t);
            int lo = Math.Max(1, c - reach), hi = Math.Min(n - 2, c + reach);
            if (hi < lo) continue;
            int pk = lo;
            for (int i = lo; i <= hi; i++) if (env[i] > env[pk]) pk = i;
            if (env[pk] <= gate || env[pk] < env[pk - 1] || env[pk] < env[pk + 1]) continue;
            devs[nd++] = Parabolic(env, pk) - t;
        }
        float ph2 = bestPh;
        if (nd > 0)
        {
            Array.Sort(devs, 0, nd);
            ph2 += devs[nd / 2];
        }

        float periodSec = period * Hop / rate;
        float sec = (ph2 * Hop + OnsetDelay) / rate;
        sec %= periodSec;
        if (sec < 0f) sec += periodSec;
        if (sec > periodSec - 0.015f) sec = 0f;
        return sec;
    }

    private static float Interp(float[] a, float t)
    {
        int i = (int)t;
        if (i < 0 || i >= a.Length - 1) return i >= 0 && i < a.Length ? a[i] : 0f;
        float fr = t - i;
        return a[i] * (1f - fr) + a[i + 1] * fr;
    }

    private static int CountOnsets(float[] env, float mean)
    {
        float thr = 1.5f * mean;
        int count = 0, last = -100;
        for (int i = 1; i < env.Length - 1; i++)
        {
            if (env[i] > thr && env[i] > env[i - 1] && env[i] >= env[i + 1] && i - last >= 3)
            {
                count++;
                last = i;
            }
        }
        return count;
    }

    private static MusicStyle Classify(float bpm, float ops, float low, float centroid, float cv, float regularity, float energy)
    {
        if (energy < 0.08f) return MusicStyle.Chill;
        if (bpm >= 118f && bpm <= 150f && low >= 0.35f && regularity >= 0.5f && cv < 0.45f) return MusicStyle.Electronic;
        if (bpm >= 70f && bpm <= 105f && low >= 0.35f && ops >= 1.5f) return MusicStyle.HipHop;
        if (bpm >= 100f && bpm <= 180f && centroid >= 2000f && low < 0.35f && cv >= 0.3f) return MusicStyle.Rock;
        if (bpm < 100f && ops < 2f) return MusicStyle.Chill;
        if (bpm >= 90f && bpm <= 140f && centroid >= 600f && centroid < 2000f && low < 0.2f && ops >= 1.5f && ops <= 5f) return MusicStyle.Folk;
        return MusicStyle.Pop;
    }

    // ---------------------------------------------------------------- self-test

    /// <summary>Synthetic clicks at known tempos must come back at that tempo and phase.</summary>
    public static bool SelfCheck()
    {
        const int rate = 22050;
        const int len = rate * 20;
        bool ok = true;

        // 1: 128 BPM noise-burst clicks, first at 0.25 s, plus a noise floor.
        {
            float[] x = new float[len];
            var rng = new Rng(1);
            for (int i = 0; i < len; i++) x[i] = 0.02f * rng.Next();
            AddClicks(x, rate, 128f, 0.25f, 0.8f, rng);
            var (bpm, off, style, _) = Analyse(x, rate);
            bool pass = Math.Abs(bpm - 128f) <= 2f && CircDist(off, 0.25f, 60f / 128f) <= 0.03f;
            Report(1, bpm, off, style, pass);
            ok &= pass;
        }

        // 2: 90 BPM 60 Hz kicks on every beat, soft snare on every second beat.
        {
            float[] x = new float[len];
            var rng = new Rng(2);
            float per = 60f / 90f;
            int beat = 0;
            for (float t = 0.1f; t < 19.5f; t += per, beat++)
            {
                int s0 = (int)(t * rate);
                for (int k = 0; k < (int)(0.3f * rate) && s0 + k < len; k++)
                {
                    float tt = k / (float)rate;
                    x[s0 + k] += 0.8f * MathF.Sin(2f * MathF.PI * 60f * tt) * MathF.Exp(-tt / 0.12f);
                }
                if (beat % 2 == 1)
                {
                    for (int k = 0; k < (int)(0.15f * rate) && s0 + k < len; k++)
                        x[s0 + k] += 0.25f * rng.Next() * MathF.Exp(-(k / (float)rate) / 0.05f);
                }
            }
            var (bpm, off, style, _) = Analyse(x, rate);
            bool pass = bpm >= 88f && bpm <= 92f && (style == MusicStyle.HipHop || style == MusicStyle.Chill);
            Report(2, bpm, off, style, pass);
            ok &= pass;
        }

        // 3: 120 BPM clicks starting at 0.
        {
            float[] x = new float[len];
            var rng = new Rng(3);
            for (int i = 0; i < len; i++) x[i] = 0.01f * rng.Next();
            AddClicks(x, rate, 120f, 0f, 0.8f, rng);
            var (bpm, off, style, _) = Analyse(x, rate);
            bool pass = Math.Abs(bpm - 120f) <= 2f && off < 0.03f;
            Report(3, bpm, off, style, pass);
            ok &= pass;
        }
        return ok;
    }

    private static void Report(int n, float bpm, float off, MusicStyle style, bool pass) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[beatcheck] case{n} bpm={bpm:F1} offset={off:F3} style={style} {(pass ? "ok" : "FAIL")}"));

    private static float CircDist(float a, float b, float period)
    {
        float d = Math.Abs(a - b) % period;
        return Math.Min(d, period - d);
    }

    private static void AddClicks(float[] x, int rate, float bpm, float first, float amp, Rng rng)
    {
        float per = 60f / bpm;
        for (float t = first; t < x.Length / (float)rate - 0.1f; t += per)
        {
            int s0 = (int)MathF.Round(t * rate);
            int cl = (int)(0.05f * rate);
            for (int k = 0; k < cl && s0 + k < x.Length; k++)
                x[s0 + k] += amp * rng.Next() * MathF.Exp(-(k / (float)rate) / 0.008f);
        }
    }

    /// <summary>Tiny deterministic noise source in [-1,1].</summary>
    private sealed class Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed * 2654435761u + 12345u; }
        public float Next()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return (_s & 0xFFFFFF) / (float)0x7FFFFF - 1f;
        }
    }
}
