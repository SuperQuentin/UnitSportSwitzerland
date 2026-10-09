using System;
using System.Collections.Generic;
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
        var r = AnalyseFull(mono, rate);
        return (r.bpm, r.beatOffset, r.style, r.energy);
    }

    /// <summary>
    /// <see cref="Analyse"/> plus the <see cref="CdAnalysis"/> block (envelope, downbeat, sections, #725).
    /// The block is laid on the grid <paramref name="gridBpm"/>/<paramref name="gridOffset"/> when
    /// <paramref name="gridBpm"/> &gt; 0 (a CD whose grid is set by hand, or a backfill on the stored
    /// grid), else on the tempo and offset found here. Null only for input too short to analyse.
    /// </summary>
    public static (float bpm, float beatOffset, MusicStyle style, float energy, CdAnalysis? analysis) AnalyseFull(
        float[] mono, int rate, float gridBpm = 0f, float gridOffset = 0f)
    {
        if (mono == null || mono.Length == 0 || rate < 8000) return (120f, 0f, MusicStyle.Pop, 0f, null);

        double sumSq = 0;
        for (int i = 0; i < mono.Length; i++) sumSq += (double)mono[i] * mono[i];
        float rmsAll = (float)Math.Sqrt(sumSq / mono.Length);
        float energy = Math.Clamp(rmsAll / 0.3f, 0f, 1f);

        float fps = rate / (float)Hop;
        int n = mono.Length >= Win ? (mono.Length - Win) / Hop + 1 : 0;
        if (n < (int)(fps * 3f)) return (120f, 0f, MusicStyle.Pop, energy, null);

        // ---- Spectral flux, plus the per-frame features the style rules and the CdAnalysis need ----
        float[] flux = new float[n];
        float[] frameRms = new float[n];
        var ft = new Features(n);
        int[] bandOf = BandMap(rate, out int[] bandCount);
        float[] bandAcc = new float[NBands];
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
                float fl = 0f, tot = 0f, low = 0f, cw = 0f, lfl = 0f;
                Array.Clear(bandAcc);
                for (int k = 1; k < Bins; k++)
                {
                    float m = mag[k];
                    float lg = MathF.Log(1f + m);
                    float d = lg - prevLog[k];
                    if (d > 0f) fl += d;
                    prevLog[k] = lg;
                    tot += m;
                    cw += m * k;
                    if (k <= lowBin)
                    {
                        low += m;
                        if (d > 0f) lfl += d;
                    }
                    int band = bandOf[k];
                    if (band >= 0) bandAcc[band] += m;
                }
                flux[idx] = idx == 0 ? 0f : fl;
                ft.LowFlux[idx] = idx == 0 ? 0f : lfl;
                ft.Low[idx] = low;
                ft.Tot[idx] = tot;
                ft.Cen[idx] = cw;
                for (int b = 0; b < NBands; b++)
                    ft.Bands[idx * NBands + b] = MathF.Log(1f + bandAcc[b] / Math.Max(1, bandCount[b]));
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
        int meanWin = Math.Max(3, (int)(fps * 0.5f)) | 1;
        float[] env = LocalMeanSubtract(flux, meanWin);
        ft.Rms = frameRms;
        ft.Env = env;
        ft.LowEnv = LocalMeanSubtract(ft.LowFlux, meanWin);
        CdAnalysis Block(float bpmFound, float offFound) =>
            Describe(mono, rate, ft, gridBpm > 0f ? gridBpm : bpmFound, gridBpm > 0f ? gridOffset : offFound);
        double envSum = 0;
        for (int i = 0; i < n; i++) envSum += env[i];
        float envMean = (float)(envSum / n);
        if (envMean < 1e-9f) return (120f, 0f, MusicStyle.Chill, energy, Block(120f, 0f));

        // ---- Tempo: autocorrelation with a log-Gaussian prior around 120 BPM ----
        float[] acf = Autocorrelation(env, envMean, Math.Min(n - 2, (int)(8.5f * fps) + 4));
        int acfLen = acf.Length;
        int lagMin = Math.Max(2, (int)MathF.Floor(fps * 60f / 200f));
        int lagMax = Math.Min(acfLen - 2, (int)MathF.Ceiling(fps * 60f / 60f));
        if (lagMax <= lagMin + 2) return (120f, 0f, MusicStyle.Pop, energy, Block(120f, 0f));

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
        return (bpm, offset, style, energy, Block(bpm, offset));
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

    // ---------------------------------------------------------------- CdAnalysis (#725)

    private const int NBands = 12;

    /// <summary>Per-bar feature vector: RMS, &lt;150 Hz share, log centroid, then the log band energies.</summary>
    private const int FeatDims = 3 + NBands;

    /// <summary>Half the Foote checkerboard kernel, in bars (the kernel spans 8).</summary>
    private const int KernelHalf = 4;

    private const int MinSectionBars = 4;

    /// <summary>Per-STFT-frame features kept for the <see cref="CdAnalysis"/> block.</summary>
    private sealed class Features
    {
        public readonly int N;
        public readonly float[] LowFlux, Low, Tot, Cen, Bands;
        public float[] Rms = Array.Empty<float>(), Env = Array.Empty<float>(), LowEnv = Array.Empty<float>();

        public Features(int n)
        {
            N = n;
            LowFlux = new float[n];
            Low = new float[n];
            Tot = new float[n];
            Cen = new float[n];
            Bands = new float[n * NBands];
        }
    }

    /// <summary>FFT bin to one of <see cref="NBands"/> log-spaced bands over 40 Hz..10 kHz, -1 outside.</summary>
    private static int[] BandMap(int rate, out int[] count)
    {
        int[] map = new int[Bins];
        count = new int[NBands];
        float lo = 40f, hi = Math.Min(10000f, rate * 0.5f);
        float span = MathF.Log(hi / lo);
        for (int k = 0; k < Bins; k++)
        {
            float f = k * rate / (float)Win;
            if (k == 0 || f < lo || f >= hi) { map[k] = -1; continue; }
            int b = Math.Clamp((int)(NBands * MathF.Log(f / lo) / span), 0, NBands - 1);
            map[k] = b;
            count[b]++;
        }
        return map;
    }

    /// <summary>First STFT frame whose centre is at or after <paramref name="t"/> seconds.</summary>
    private static int FrameAt(double t, int rate) => (int)Math.Ceiling((t * rate - Win * 0.5) / Hop);

    private static CdAnalysis Describe(float[] mono, int rate, Features ft, float bpm, float offset)
    {
        double dur = mono.Length / (double)rate;
        string env = Envelope(mono, rate, ft, dur);
        double per = bpm > 0f ? 60.0 / bpm : 0.0;
        int downbeat = per > 0.0 ? Downbeat(ft, rate, per, offset, dur) : 0;
        float[] starts = { 0f };
        int[] kinds = { (int)SectionKind.Groove };
        if (per > 0.0) (starts, kinds) = Sections(ft, rate, per, offset + downbeat * per, dur);
        return new CdAnalysis
        {
            Version = CdAnalysis.CurrentVersion, Env = env, Downbeat = downbeat,
            SectionStarts = starts, SectionKinds = kinds,
        };
    }

    /// <summary>20 Hz frames: RMS loudness and the peak low-band onset in each, both normalised per track.</summary>
    private static string Envelope(float[] mono, int rate, Features ft, double dur)
    {
        int er = CdAnalysis.EnvRate;
        int m = Math.Max(1, (int)Math.Ceiling(dur * er));
        float[] lv = new float[m], kk = new float[m];
        for (int i = 0; i < m; i++)
        {
            long s0 = (long)i * rate / er, s1 = Math.Min(mono.Length, (long)(i + 1) * rate / er);
            double ss = 0;
            for (long s = s0; s < s1; s++) ss += (double)mono[s] * mono[s];
            lv[i] = s1 > s0 ? (float)Math.Sqrt(ss / (s1 - s0)) : 0f;
        }
        for (int f = 0; f < ft.N; f++)
        {
            // the flux fires OnsetDelay samples after the frame start: that is when the hit is heard
            int i = (int)((f * (double)Hop + OnsetDelay) / rate * er);
            if (i < m && ft.LowEnv[f] > kk[i]) kk[i] = ft.LowEnv[f];
        }
        float lref = Reference(lv), kref = Reference(kk);
        byte[] b = new byte[2 * m];
        for (int i = 0; i < m; i++)
        {
            b[2 * i] = ToByte(lv[i] / lref);
            b[2 * i + 1] = ToByte(kk[i] / kref);
        }
        return Convert.ToBase64String(b);
    }

    /// <summary>What reads as full scale: the 99th percentile, or most of the peak when hits are rare.</summary>
    private static float Reference(float[] x)
    {
        float max = 0f;
        foreach (float v in x) max = Math.Max(max, v);
        if (max <= 1e-12f) return 1f;
        return Math.Max(Percentile(x, 0.99f), 0.6f * max);
    }

    private static float Percentile(float[] x, float q)
    {
        float[] c = (float[])x.Clone();
        Array.Sort(c);
        return c[Math.Clamp((int)(q * (c.Length - 1)), 0, c.Length - 1)];
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>
    /// Which beat of four starts a bar: the phase whose beats carry the most low-band onset plus
    /// spectral change (log band energies of the beat against the beat before: chords and bass
    /// notes move on the one).
    /// </summary>
    private static int Downbeat(Features ft, int rate, double per, float offset, double dur)
    {
        int nBeats = (int)Math.Floor((dur - offset) / per) + 1;
        if (nBeats < 8) return 0;
        float[] low = new float[nBeats], change = new float[nBeats];
        float[] prev = new float[NBands], cur = new float[NBands];
        bool hasPrev = false;
        for (int b = 0; b < nBeats; b++)
        {
            double t = offset + b * per;
            if (t < 0.0) continue;
            int c = (int)Math.Round((t * rate - OnsetDelay) / Hop);
            float pk = 0f;
            for (int f = Math.Max(0, c - 2); f <= Math.Min(ft.N - 1, c + 2); f++) pk = Math.Max(pk, ft.LowEnv[f]);
            low[b] = pk;
            if (!MeanBands(ft, rate, t, t + per, cur)) { hasPrev = false; continue; }
            if (hasPrev)
            {
                float d = 0f;
                for (int k = 0; k < NBands; k++) { float e = cur[k] - prev[k]; d += e * e; }
                change[b] = MathF.Sqrt(d);
            }
            (prev, cur) = (cur, prev);
            hasPrev = true;
        }
        NormaliseByMean(low);
        NormaliseByMean(change);
        int best = 0;
        float bestScore = float.MinValue;
        for (int k = 0; k < 4; k++)
        {
            float s = 0f;
            int cnt = 0;
            for (int b = k; b < nBeats; b += 4) { s += low[b] + change[b]; cnt++; }
            if (cnt > 0 && s / cnt > bestScore) { bestScore = s / cnt; best = k; }
        }
        return best;
    }

    /// <summary>Mean log band energies of the frames centred in [t0, t1); false when there are none.</summary>
    private static bool MeanBands(Features ft, int rate, double t0, double t1, float[] into)
    {
        int f0 = Math.Max(0, FrameAt(t0, rate)), f1 = Math.Min(ft.N, FrameAt(t1, rate));
        Array.Clear(into);
        if (f1 <= f0) return false;
        for (int f = f0; f < f1; f++)
            for (int k = 0; k < NBands; k++) into[k] += ft.Bands[f * NBands + k];
        for (int k = 0; k < NBands; k++) into[k] /= f1 - f0;
        return true;
    }

    private static void NormaliseByMean(float[] x)
    {
        double s = 0;
        foreach (float v in x) s += v;
        float mean = x.Length > 0 ? (float)(s / x.Length) : 0f;
        if (mean <= 1e-12f) return;
        for (int i = 0; i < x.Length; i++) x[i] /= mean;
    }

    /// <summary>
    /// Sections on the bar grid starting at <paramref name="first"/> (the first downbeat): one
    /// feature vector per bar, z-scored, a cosine self-similarity matrix, Foote checkerboard novelty
    /// over 8 bars, peaks above an adaptive threshold at least <see cref="MinSectionBars"/> apart.
    /// Each section is labelled by its loudness against the track's bars, and loud sections that
    /// sound alike (the repeated loud part) become <see cref="SectionKind.Chorus"/>.
    /// </summary>
    private static (float[] starts, int[] kinds) Sections(Features ft, int rate, double per, double first, double dur)
    {
        double barLen = 4.0 * per;
        int nb = first < dur ? (int)Math.Floor((dur - first) / barLen) : 0;
        if (nb < 2 * KernelHalf) return (new[] { 0f }, new[] { (int)SectionKind.Groove });

        const int D = FeatDims;
        float[] feat = new float[nb * D];
        float[] barRms = new float[nb];
        float[] bands = new float[NBands];
        for (int j = 0; j < nb; j++)
        {
            double t0 = first + j * barLen;
            int f0 = Math.Max(0, FrameAt(t0, rate)), f1 = Math.Min(ft.N, FrameAt(t0 + barLen, rate));
            if (f1 <= f0) continue;
            double rms = 0, low = 0, tot = 0, cen = 0;
            for (int f = f0; f < f1; f++) { rms += ft.Rms[f]; low += ft.Low[f]; tot += ft.Tot[f]; cen += ft.Cen[f]; }
            MeanBands(ft, rate, t0, t0 + barLen, bands);
            int o = j * D;
            barRms[j] = (float)(rms / (f1 - f0));
            feat[o] = barRms[j];
            feat[o + 1] = tot > 1e-12 ? (float)(low / tot) : 0f;
            feat[o + 2] = tot > 1e-12 ? MathF.Log(1f + (float)(cen / tot * rate / Win)) : 0f;
            Array.Copy(bands, 0, feat, o + 3, NBands);
        }

        // z-score each dimension over the bars (a floor keeps a flat dimension from blowing up its noise)
        for (int d = 0; d < D; d++)
        {
            double s = 0, s2 = 0;
            for (int j = 0; j < nb; j++) { float v = feat[j * D + d]; s += v; s2 += (double)v * v; }
            double mean = s / nb;
            double sd = Math.Sqrt(Math.Max(0.0, s2 / nb - mean * mean));
            sd = Math.Max(sd, 0.02 * Math.Abs(mean) + 1e-9);
            for (int j = 0; j < nb; j++) feat[j * D + d] = (float)((feat[j * D + d] - mean) / sd);
        }

        float[] sim = new float[nb * nb];
        for (int i = 0; i < nb; i++)
            for (int j = i; j < nb; j++)
                sim[i * nb + j] = sim[j * nb + i] = Cosine(feat, i * D, feat, j * D, D);

        // Foote novelty: boundary i = bar i starts a new section
        float[] nov = new float[nb];
        double sigma2 = 2.0 * (KernelHalf * 0.5) * (KernelHalf * 0.5);
        int lo = 2, hi = nb - 2;
        double ns = 0, ns2 = 0;
        for (int i = lo; i <= hi; i++)
        {
            double sum = 0, wsum = 0;
            for (int a = -KernelHalf; a < KernelHalf; a++)
            {
                int ia = i + a;
                if (ia < 0 || ia >= nb) continue;
                for (int b = -KernelHalf; b < KernelHalf; b++)
                {
                    int ib = i + b;
                    if (ib < 0 || ib >= nb) continue;
                    double w = Math.Exp(-((a + 0.5) * (a + 0.5) + (b + 0.5) * (b + 0.5)) / sigma2);
                    sum += ((a < 0) == (b < 0) ? w : -w) * sim[ia * nb + ib];
                    wsum += w;
                }
            }
            nov[i] = wsum > 0 ? (float)(sum / wsum) : 0f;
            ns += nov[i];
            ns2 += (double)nov[i] * nov[i];
        }
        int cnt = hi - lo + 1;
        double nMean = ns / cnt, nSd = Math.Sqrt(Math.Max(0.0, ns2 / cnt - nMean * nMean));
        float thr = (float)Math.Max(0.1, nMean + 0.5 * nSd);

        var cands = new List<int>();
        for (int i = lo; i <= hi; i++)
        {
            if (nov[i] <= thr) continue;
            if (i > lo && nov[i] < nov[i - 1]) continue;
            if (i < hi && nov[i] < nov[i + 1]) continue;
            cands.Add(i);
        }
        cands.Sort((x, y) => nov[y].CompareTo(nov[x]));
        var cuts = new List<int>();
        foreach (int c in cands)
        {
            bool far = true;
            foreach (int a in cuts) if (Math.Abs(a - c) < MinSectionBars) { far = false; break; }
            if (far) cuts.Add(c);
        }
        cuts.Sort();

        int count = cuts.Count + 1;
        float[] starts = new float[count];
        int[] from = new int[count + 1];
        for (int s = 1; s < count; s++)
        {
            starts[s] = (float)(first + cuts[s - 1] * barLen);
            from[s] = cuts[s - 1];
        }
        from[count] = nb;

        // loudness label against the track's bars
        float p10 = Percentile(barRms, 0.1f), p90 = Percentile(barRms, 0.9f);
        double rmsMean = 0;
        foreach (float v in barRms) rmsMean += v;
        rmsMean /= nb;
        float spread = p90 - p10;
        int[] kinds = new int[count];
        float[] secMean = new float[count * D];
        for (int s = 0; s < count; s++)
        {
            double r = 0;
            int len = from[s + 1] - from[s];
            for (int j = from[s]; j < from[s + 1]; j++)
            {
                r += barRms[j];
                for (int d = 0; d < D; d++) secMean[s * D + d] += feat[j * D + d] / len;
            }
            float u = spread >= 0.15f * rmsMean ? (float)((r / len - p10) / spread) : 0.5f;
            kinds[s] = (int)(u < 0.33f ? SectionKind.Calm : u > 0.67f ? SectionKind.Peak : SectionKind.Groove);
        }
        bool[] chorus = new bool[count];
        for (int a = 0; a < count; a++)
            for (int b = a + 1; b < count; b++)
                if (kinds[a] == (int)SectionKind.Peak && kinds[b] == (int)SectionKind.Peak
                    && Cosine(secMean, a * D, secMean, b * D, D) > 0.8f)
                    chorus[a] = chorus[b] = true;
        for (int s = 0; s < count; s++) if (chorus[s]) kinds[s] = (int)SectionKind.Chorus;
        return (starts, kinds);
    }

    private static float Cosine(float[] x, int xo, float[] y, int yo, int len)
    {
        double xy = 0, xx = 0, yy = 0;
        for (int k = 0; k < len; k++)
        {
            float a = x[xo + k], b = y[yo + k];
            xy += a * b;
            xx += a * a;
            yy += b * b;
        }
        return xx > 1e-12 && yy > 1e-12 ? (float)(xy / Math.Sqrt(xx * yy)) : 0f;
    }

    // ---------------------------------------------------------------- self-test

    /// <summary>Synthetic clicks at known tempos must come back at that tempo and phase, with the right downbeat, sections and envelope.</summary>
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

        // 4, 5: a louder low kick every 4th beat from a known beat gives the downbeat, and the
        // envelope's kick peaks sit on the clicks.
        ok &= DownbeatCase(4, 120f, 0.25f, 1);
        ok &= DownbeatCase(5, 100f, 0.4f, 3);

        // 6: 16 quiet bars, then 16 loud, spectrally different bars: one boundary, Calm then Peak.
        {
            const float bpm = 120f, first = 0.1f;
            float per = 60f / bpm;
            int total = (int)((first + 33 * 4 * per) * rate);   // 32 bars and a spare one
            float[] x = new float[total];
            var rng = new Rng(6);
            float change = first + 64 * per;   // beat 64 = bar 16
            for (int i = 0; i < total; i++)
            {
                float t = i / (float)rate;
                x[i] = 0.01f * rng.Next();
                if (t < change) x[i] += 0.03f * MathF.Sin(2f * MathF.PI * 3000f * t);
                else
                    for (int h = 1; h <= 6; h++) x[i] += 0.25f / h * MathF.Sin(2f * MathF.PI * 110f * h * t);
            }
            int beat = 0;
            for (float t = first; t < total / (float)rate - 0.3f; t += per, beat++)
            {
                bool loud = t >= change - 1e-3f;
                AddClick(x, rate, t, loud ? 0.7f : 0.15f, rng);
                if (loud && beat % 4 == 0) AddKick(x, rate, t, 0.8f);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (gotBpm, _, _, _, an) = AnalyseFull(x, rate);
            long ms = sw.ElapsedMilliseconds;
            bool pass = an != null && an.SectionStarts.Length == 2
                && Math.Abs(an.SectionStarts[1] - change) <= 4f * per + 0.05f
                && an.SectionKinds[0] == (int)SectionKind.Calm && an.SectionKinds[1] == (int)SectionKind.Peak;
            float quiet = 0f, loudLv = 0f;
            if (an != null)
            {
                byte[] e = an.EnvBytes();
                int c = (int)(change * CdAnalysis.EnvRate);
                quiet = MeanLevel(e, 20, c - 20);
                loudLv = MeanLevel(e, c + 20, e.Length / 2 - 20);
                pass &= quiet < loudLv;

                // the block survives the JSON file and the RPC dictionary
                var cd = new CdInfo(1, "t", 66f, gotBpm, 0.1f, MusicStyle.Pop, 0.5f, Analysis: an);
                foreach (var back in new[] { CdInfo.FromJson(cd.ToJson()), CdInfo.FromDict(cd.ToDict()) })
                    pass &= back?.Analysis is { } b && b.Version == an.Version && b.Env == an.Env && b.Downbeat == an.Downbeat
                        && b.SectionStarts.AsSpan().SequenceEqual(an.SectionStarts) && b.SectionKinds.AsSpan().SequenceEqual(an.SectionKinds);
            }
            string sections = an == null ? "none" : string.Join(",", Array.ConvertAll(an.SectionStarts,
                s => s.ToString("F1", CultureInfo.InvariantCulture)));
            string kinds = an == null ? "" : string.Join(",", Array.ConvertAll(an.SectionKinds, k => ((SectionKind)k).ToString()));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[beatcheck] case6 bpm={gotBpm:F1} sections={sections} kinds={kinds} level quiet={quiet:F0} loud={loudLv:F0} ({ms} ms for {total / rate} s) {(pass ? "ok" : "FAIL")}"));
            ok &= pass;
        }

        // 7: a CD without a block reads as nothing, the runtime helpers stay safe.
        {
            var cd = new CdInfo(1, "t", 10f, 120f, 0f, MusicStyle.Pop, 0.5f);
            bool pass = !CdAnalysisRuntime.Sample(cd, 1.0, out _, out _)
                && CdAnalysisRuntime.SectionAt(cd, 1.0, out int si) == SectionKind.Groove && si == 0
                && CdAnalysisRuntime.BeatInBar(cd, -1) == 3;
            var back = CdInfo.FromJson(cd.ToJson());
            pass &= back != null && back.Analysis == null;
            Console.WriteLine($"[beatcheck] case7 no analysis {(pass ? "ok" : "FAIL")}");
            ok &= pass;
        }
        return ok;
    }

    private static bool DownbeatCase(int n, float bpm, float first, int kickPhase)
    {
        const int rate = 22050;
        const int len = rate * 24;
        float[] x = new float[len];
        var rng = new Rng((uint)n);
        for (int i = 0; i < len; i++) x[i] = 0.02f * rng.Next();
        float per = 60f / bpm;
        var clicks = new List<float>();
        var kicks = new List<float>();
        int beat = 0;
        for (float t = first; t < len / (float)rate - 0.3f; t += per, beat++)
        {
            AddClick(x, rate, t, 0.5f, rng);
            clicks.Add(t);
            if (beat % 4 == kickPhase) { AddKick(x, rate, t, 0.8f); kicks.Add(t); }
        }
        var (gotBpm, off, _, _, an) = AnalyseFull(x, rate);
        bool pass = an != null && Math.Abs(gotBpm - bpm) <= 2f && CircDist(off, first, per) <= 0.03f && an.Downbeat == kickPhase;

        // every kick shows within a frame, and every strong kick frame is within a frame of a click
        int missed = 0, stray = 0;
        if (an != null)
        {
            byte[] e = an.EnvBytes();
            int m = e.Length / 2;
            foreach (float t in kicks)
            {
                int c = (int)(t * CdAnalysis.EnvRate);
                int pk = 0;
                for (int i = Math.Max(0, c - 1); i <= Math.Min(m - 1, c + 1); i++) pk = Math.Max(pk, e[2 * i + 1]);
                if (pk < 128) missed++;
            }
            for (int i = 0; i < m; i++)
            {
                if (e[2 * i + 1] < 128) continue;
                bool near = false;
                foreach (float t in clicks) if (Math.Abs((int)(t * CdAnalysis.EnvRate) - i) <= 1) { near = true; break; }
                if (!near) stray++;
            }
            pass &= missed == 0 && stray == 0;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[beatcheck] case{n} bpm={gotBpm:F1} offset={off:F3} downbeat={an?.Downbeat ?? -1} (want {kickPhase}) kicks missed={missed} stray={stray} {(pass ? "ok" : "FAIL")}"));
        return pass;
    }

    private static float MeanLevel(byte[] env, int from, int to)
    {
        double s = 0;
        int c = 0;
        for (int i = Math.Max(0, from); i < Math.Min(env.Length / 2, to); i++) { s += env[2 * i]; c++; }
        return c > 0 ? (float)(s / c) : 0f;
    }

    private static void AddClick(float[] x, int rate, float t, float amp, Rng rng)
    {
        int s0 = (int)MathF.Round(t * rate);
        int cl = (int)(0.05f * rate);
        for (int k = 0; k < cl && s0 + k < x.Length; k++)
            x[s0 + k] += amp * rng.Next() * MathF.Exp(-(k / (float)rate) / 0.008f);
    }

    private static void AddKick(float[] x, int rate, float t, float amp)
    {
        int s0 = (int)MathF.Round(t * rate);
        for (int k = 0; k < (int)(0.3f * rate) && s0 + k < x.Length; k++)
        {
            float tt = k / (float)rate;
            x[s0 + k] += amp * MathF.Sin(2f * MathF.PI * 60f * tt) * MathF.Exp(-tt / 0.12f);
        }
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
