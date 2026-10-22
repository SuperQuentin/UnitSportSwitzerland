using Godot;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace UnitSport.Audio.Cd;

/// <summary>
/// Pulls the singer out of a CD at burn time (#1153), for <see cref="VocalAnalyzer"/>. With the
/// MDX-Net model in <c>user://models/</c> (fetched by <c>tools/deploy/get-vocal-model.sh</c>, never
/// shipped: its weights carry no clear redistribution licence) it runs the network on the CPU
/// through ONNX Runtime; without it (a phone, a machine that never fetched it, a model that fails)
/// it keeps the centre-panned 150 Hz..4 kHz part of the mix, where a lead vocal usually sits.
/// Input is interleaved stereo 16-bit at 44.1 kHz; output is the vocal, mono, at
/// <see cref="CdBurner.AnalysisRate"/>. Runs on a worker, one separation at a time.
/// </summary>
public static class VocalSeparator
{
    public const int Rate = 44100;
    public const string Centre = "centre";

    private const string ModelFile = "Kim_Vocal_2.onnx";

    /// <summary>The model's STFT, as UVR's model data gives it for this one (n_fft 7680, hop 1024).</summary>
    private const int ModelFft = 7680, ModelHop = 1024;

    /// <summary>Where the model lives; resolved once, on the main thread (first asked by <see cref="CdAnalysis.IsStale"/> at load).</summary>
    private static readonly string ModelPath =
        System.IO.Path.Combine(ProjectSettings.GlobalizePath("user://models"), ModelFile);

    /// <summary>The model's name when it is here and usable on this machine, else null (the fallback runs).</summary>
    public static string? ModelName { get; } =
        !Core.Platform.IsMobile && File.Exists(ModelPath) ? System.IO.Path.GetFileNameWithoutExtension(ModelFile) : null;

    /// <summary>
    /// The vocal, mono at <see cref="CdBurner.AnalysisRate"/> for the analyser; what made it
    /// (<see cref="ModelName"/> or <see cref="Centre"/>); and the vocal stem as played, interleaved
    /// stereo 16-bit at 44.1 kHz like the input, for the voice/music mix (the instrumental is the
    /// mix minus it, <see cref="Instrumental"/>).
    /// </summary>
    public static (float[] Vocal, string Source, short[] Stem) Separate(short[] stereo, CancellationToken ct, bool useModel = true)
    {
        if (useModel && ModelName is { } name)
        {
            try
            {
                var stem = SeparateModel(stereo, ct);
                return (ToAnalysisRate(Mono(stem)), name, stem);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { GD.PushWarning($"[cd] vocal model failed, using the centre fallback: {e.Message}"); }
        }
        var centre = SeparateCentre(stereo, ct);
        var both = new short[2 * centre.Length];
        for (int i = 0; i < centre.Length; i++) both[2 * i] = both[2 * i + 1] = ToShort(centre[i]);
        return (ToAnalysisRate(centre), Centre, both);
    }

    /// <summary>The mix with the vocal taken out, sample by sample: the instrumental stem.</summary>
    public static short[] Instrumental(short[] mix, short[] vocal)
    {
        var inst = new short[Math.Min(mix.Length, vocal.Length)];
        for (int i = 0; i < inst.Length; i++) inst[i] = (short)Math.Clamp(mix[i] - vocal[i], short.MinValue, short.MaxValue);
        return inst;
    }

    private static short ToShort(float v) => (short)Math.Clamp(v * 32767f, -32768f, 32767f);

    private static float[] Mono(short[] stereo)
    {
        var m = new float[stereo.Length / 2];
        for (int i = 0; i < m.Length; i++) m[i] = (stereo[2 * i] + stereo[2 * i + 1]) / 65536f;
        return m;
    }

    // ---- MDX-Net through ONNX Runtime ------------------------------------------------------------

    /// <summary>
    /// UVR's MDX "demix" without overlap: the mix padded by n_fft/2 on each side and cut into chunks
    /// of hop x (frames - 1) samples, each chunk's STFT (torch layout: periodic Hann, centred with
    /// reflect padding, channels L re, L im, R re, R im, the lowest <c>dim_f</c> bins, the three
    /// lowest zeroed) through the network, which answers the vocal's spectrogram; inverted, the
    /// middle of each chunk kept. Returns the vocal, interleaved stereo 16-bit, at 44.1 kHz.
    /// </summary>
    private static short[] SeparateModel(short[] stereo, CancellationToken ct)
    {
        // all cores but two: a server burning a CD still has players to serve
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, System.Environment.ProcessorCount - 2),
        };
        using var session = new InferenceSession(ModelPath, options);
        string input = session.InputMetadata.Keys.First();
        int[] shape = session.InputMetadata[input].Dimensions;
        int dimF = shape[2], dimT = shape[3];

        int n = stereo.Length / 2;
        int chunk = ModelHop * (dimT - 1), trim = ModelFft / 2, gen = chunk - 2 * trim;
        int pad = gen - n % gen;
        int padded = trim + n + pad + trim;
        var l = new float[padded];
        var r = new float[padded];
        for (int i = 0; i < n; i++)
        {
            l[trim + i] = stereo[2 * i] / 32768f;
            r[trim + i] = stereo[2 * i + 1] / 32768f;
        }

        var stft = new Stft(ModelFft, ModelHop);
        var vocal = new short[2 * (n + pad)];
        var input4 = new float[4 * dimF * dimT];
        var answer = new float[4 * dimF * dimT];
        var spec = new DenseTensor<float>(input4, new[] { 1, 4, dimF, dimT });
        var outL = new float[chunk];
        var outR = new float[chunk];
        var transform = new System.Diagnostics.Stopwatch();
        var network = new System.Diagnostics.Stopwatch();
        for (int start = 0; start < n + pad; start += gen)
        {
            ct.ThrowIfCancellationRequested();
            transform.Start();
            stft.Forward(l, r, start, chunk, input4, dimF, dimT);
            transform.Stop();
            network.Start();
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(input, spec) });
            results.First().AsTensor<float>().ToDenseTensor().Buffer.Span.CopyTo(answer);
            network.Stop();
            transform.Start();
            stft.Inverse(answer, dimF, dimT, chunk, outL, outR);
            transform.Stop();
            int keep = Math.Min(gen, n + pad - start);
            for (int i = 0; i < keep; i++)
            {
                vocal[2 * (start + i)] = ToShort(outL[trim + i]);
                vocal[2 * (start + i) + 1] = ToShort(outR[trim + i]);
            }
        }
        Array.Resize(ref vocal, 2 * n);
        GD.Print($"[cd] vocal model: network {network.Elapsed.TotalSeconds:F1} s, transforms {transform.Elapsed.TotalSeconds:F1} s");
        return vocal;
    }

    /// <summary>
    /// <c>--beatcheck</c> part (#1153): a chunk of a chord through the model's STFT and back, without
    /// the network in between, comes back as it went in (the torch layout, Bluestein and the
    /// overlap-add agree); and the centre fallback keeps a centre-panned tone and drops one panned hard left.
    /// </summary>
    public static bool SelfCheck()
    {
        bool ok = true;
        const int dimT = 32, dimF = ModelFft / 2 + 1;
        int chunk = ModelHop * (dimT - 1);
        var l = new float[chunk];
        var r = new float[chunk];
        for (int i = 0; i < chunk; i++)
        {
            double t = i / (double)Rate;
            l[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * t) + 0.2 * Math.Sin(2 * Math.PI * 1375 * t));
            r[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 330 * t + 1) + 0.1 * Math.Sin(2 * Math.PI * 5000 * t));
        }
        var stft = new Stft(ModelFft, ModelHop);
        var spec = new float[4 * dimF * dimT];
        stft.Forward(l, r, 0, chunk, spec, dimF, dimT);
        var ol = new float[chunk];
        var or = new float[chunk];
        stft.Inverse(spec, dimF, dimT, chunk, ol, or);
        double err = 0;
        for (int i = 0; i < chunk; i++) err = Math.Max(err, Math.Max(Math.Abs(ol[i] - l[i]), Math.Abs(or[i] - r[i])));
        // the three lowest bins are zeroed on the way in, as UVR does: a 220 Hz tone barely feels it
        Check(err < 0.02, $"model STFT round trip, max error {err:F4}");

        // centre fallback: 440 Hz in the middle, 1 kHz hard left; the vocal keeps the first only
        int n = Rate * 2;
        var stereo = new short[2 * n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double mid = 0.3 * Math.Sin(2 * Math.PI * 440 * t), left = 0.3 * Math.Sin(2 * Math.PI * 1000 * t);
            stereo[2 * i] = (short)((mid + left) * 32767);
            stereo[2 * i + 1] = (short)(mid * 32767);
        }
        var v = SeparateCentre(stereo, CancellationToken.None);
        double keep = Band(v, 440), drop = Band(v, 1000);
        Check(keep > 0.1 && drop < 0.2 * keep, $"centre fallback keeps the centre ({keep:F3}) over the side ({drop:F3})");
        return ok;

        void Check(bool cond, string what)
        {
            GD.Print($"[vocalcheck] {(cond ? "ok  " : "FAIL")} {what}");
            ok &= cond;
        }

        // amplitude of one frequency in the middle second (a plain DFT bin)
        static double Band(float[] x, double hz)
        {
            double c = 0, s = 0;
            for (int i = Rate / 2; i < Rate * 3 / 2; i++) { c += x[i] * Math.Cos(2 * Math.PI * hz * i / Rate); s += x[i] * Math.Sin(2 * Math.PI * hz * i / Rate); }
            return 2 * Math.Sqrt(c * c + s * s) / Rate;
        }
    }

    // ---- centre-pan fallback -----------------------------------------------------------------------

    /// <summary>
    /// Per STFT bin (2048 / 512), how alike left and right are (1 for a centre-panned source, 0 for
    /// one side alone or out of phase), raised to a power so only the centre passes, between 150 Hz
    /// and 4 kHz. On a mono file everything is centre: the band alone is left, still the voice's.
    /// </summary>
    private static float[] SeparateCentre(short[] stereo, CancellationToken ct)
    {
        const int N = 2048, Hop = 512;
        int n = stereo.Length / 2;
        var outp = new float[n + N];
        var norm = new float[n + N];
        var win = new float[N];
        for (int i = 0; i < N; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / N);
        int k0 = (int)(150f * N / Rate), k1 = (int)(4000f * N / Rate);
        var re = new float[N];
        var im = new float[N];
        var mr = new float[N];
        var mi = new float[N];
        for (int s = 0; s + N <= n; s += Hop)
        {
            if ((s & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            for (int i = 0; i < N; i++)
            {
                re[i] = stereo[2 * (s + i)] / 32768f * win[i];
                im[i] = stereo[2 * (s + i) + 1] / 32768f * win[i];
            }
            Fft.Forward(re, im);   // left in re, right in im: split by symmetry below
            Array.Clear(mr);
            Array.Clear(mi);
            for (int k = k0; k <= k1; k++)
            {
                int j = (N - k) % N;
                float lr = 0.5f * (re[k] + re[j]), li = 0.5f * (im[k] - im[j]);
                float rr = 0.5f * (im[k] + im[j]), ri = -0.5f * (re[k] - re[j]);
                float dr = lr - rr, di = li - ri, sr = lr + rr, si = li + ri;
                float alike = 1f - MathF.Sqrt((dr * dr + di * di) / (sr * sr + si * si + 1e-12f));
                float m = alike <= 0f ? 0f : alike * alike * alike * alike;
                // the mid (L+R)/2, masked, mirrored to keep the output real
                mr[k] = 0.5f * sr * m; mi[k] = 0.5f * si * m;
                mr[j] = mr[k]; mi[j] = -mi[k];
            }
            // inverse via the forward transform: conj, forward, conj, / N
            for (int k = 0; k < N; k++) mi[k] = -mi[k];
            Fft.Forward(mr, mi);
            for (int i = 0; i < N; i++)
            {
                outp[s + i] += mr[i] / N * win[i];
                norm[s + i] += win[i] * win[i];
            }
        }
        var vocal = new float[n];
        for (int i = 0; i < n; i++) vocal[i] = norm[i] > 1e-3f ? outp[i] / norm[i] : 0f;
        return vocal;
    }

    /// <summary>44.1 kHz to 22.05 kHz: a short low-pass, then every other sample.</summary>
    private static float[] ToAnalysisRate(float[] x)
    {
        var y = new float[x.Length / 2];
        for (int i = 0; i < y.Length; i++)
        {
            int c = 2 * i;
            float a = c > 0 ? x[c - 1] : 0f, b = c + 1 < x.Length ? x[c + 1] : 0f;
            y[i] = 0.25f * a + 0.5f * x[c] + 0.25f * b;
        }
        return y;
    }

    /// <summary>
    /// torch.stft / istft (periodic Hann, centred, reflect padding, unnormalised) for any length, the
    /// way MDX-Net was trained: a Bluestein transform on the radix-2 <see cref="Fft"/>. Left and right
    /// go through one complex transform (left real, right imaginary) and are split by symmetry.
    /// </summary>
    private sealed class Stft
    {
        private readonly int _n, _hop, _m;
        private readonly float[] _win, _chirpRe, _chirpIm, _kernRe, _kernIm;
        private float[] _framesL = Array.Empty<float>(), _framesR = Array.Empty<float>();

        /// <summary>One thread's buffers: the frame (x) and the Bluestein convolution (a).</summary>
        private sealed class Scratch(int n, int m)
        {
            public readonly float[] XRe = new float[n], XIm = new float[n], ARe = new float[m], AIm = new float[m];
        }

        public Stft(int n, int hop)
        {
            _n = n;
            _hop = hop;
            _m = 1;
            while (_m < 2 * n - 1) _m <<= 1;
            _win = new float[n];
            for (int i = 0; i < n; i++) _win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            _chirpRe = new float[n];
            _chirpIm = new float[n];
            for (int i = 0; i < n; i++)
            {
                double a = Math.PI * ((long)i * i % (2L * n)) / n;
                _chirpRe[i] = (float)Math.Cos(a);
                _chirpIm[i] = (float)-Math.Sin(a);
            }
            _kernRe = new float[_m];
            _kernIm = new float[_m];
            for (int i = 0; i < n; i++)
            {
                _kernRe[i] = _chirpRe[i]; _kernIm[i] = -_chirpIm[i];
                if (i > 0) { _kernRe[_m - i] = _chirpRe[i]; _kernIm[_m - i] = -_chirpIm[i]; }
            }
            Fft.Forward(_kernRe, _kernIm);
        }

        private Scratch NewScratch() => new(_n, _m);

        /// <summary>X[k] = sum x[j] e^(-2 pi i jk/n) of the scratch's x, in place, any n.</summary>
        private void Dft(Scratch s)
        {
            float[] xRe = s.XRe, xIm = s.XIm, aRe = s.ARe, aIm = s.AIm;
            Array.Clear(aRe);
            Array.Clear(aIm);
            for (int i = 0; i < _n; i++)
            {
                aRe[i] = xRe[i] * _chirpRe[i] - xIm[i] * _chirpIm[i];
                aIm[i] = xRe[i] * _chirpIm[i] + xIm[i] * _chirpRe[i];
            }
            Fft.Forward(aRe, aIm);
            for (int i = 0; i < _m; i++)
            {
                float r = aRe[i] * _kernRe[i] - aIm[i] * _kernIm[i];
                float im = aRe[i] * _kernIm[i] + aIm[i] * _kernRe[i];
                aRe[i] = r; aIm[i] = -im;   // conj: the inverse through the forward transform
            }
            Fft.Forward(aRe, aIm);
            for (int i = 0; i < _n; i++)
            {
                float cr = aRe[i] / _m, ci = -aIm[i] / _m;
                xRe[i] = cr * _chirpRe[i] - ci * _chirpIm[i];
                xIm[i] = cr * _chirpIm[i] + ci * _chirpRe[i];
            }
        }

        /// <summary>A sample of <paramref name="x"/> at <paramref name="i"/> into a chunk of <paramref name="len"/> from <paramref name="start"/>, reflected at the chunk's ends (torch's centre padding).</summary>
        private static float At(float[] x, int start, int len, int i)
        {
            if (i < 0) i = -i;
            if (i >= len) i = 2 * (len - 1) - i;
            int j = start + i;
            return j < x.Length ? x[j] : 0f;
        }

        /// <summary>
        /// One chunk into the model's input, laid out [channel][bin][frame]; the three lowest bins
        /// zeroed as UVR does. Frames run on all cores: each writes its own column.
        /// </summary>
        public void Forward(float[] l, float[] r, int start, int len, float[] spec, int dimF, int dimT)
        {
            int plane = dimF * dimT;
            int half = _n / 2;
            Parallel.For(0, dimT, NewScratch, (f, _, s) =>
            {
                int c = f * _hop - half;
                for (int i = 0; i < _n; i++)
                {
                    s.XRe[i] = At(l, start, len, c + i) * _win[i];
                    s.XIm[i] = At(r, start, len, c + i) * _win[i];
                }
                Dft(s);
                for (int k = 0; k < dimF; k++)
                {
                    int j = (_n - k) % _n, at = k * dimT + f;
                    bool low = k < 3;
                    spec[at] = low ? 0f : 0.5f * (s.XRe[k] + s.XRe[j]);
                    spec[plane + at] = low ? 0f : 0.5f * (s.XIm[k] - s.XIm[j]);
                    spec[2 * plane + at] = low ? 0f : 0.5f * (s.XIm[k] + s.XIm[j]);
                    spec[3 * plane + at] = low ? 0f : -0.5f * (s.XRe[k] - s.XRe[j]);
                }
                return s;
            }, _ => { });
        }

        /// <summary>The network's answer back to the chunk's two channels: frames inverted on all cores, then overlap-added in order.</summary>
        public void Inverse(float[] spec, int dimF, int dimT, int len, float[] outL, float[] outR)
        {
            int half = _n / 2, plane = dimF * dimT;
            if (_framesL.Length != dimT * _n) { _framesL = new float[dimT * _n]; _framesR = new float[dimT * _n]; }
            float[] framesL = _framesL, framesR = _framesR;
            Parallel.For(0, dimT, NewScratch, (f, _, s) =>
            {
                Array.Clear(s.XRe);
                Array.Clear(s.XIm);
                // Z = L + iR over the full, Hermitian spectrum; bins past dim_f stay zero
                for (int k = 0; k < dimF && k <= half; k++)
                {
                    int at = k * dimT + f;
                    float lr = spec[at], li = spec[plane + at], rr = spec[2 * plane + at], ri = spec[3 * plane + at];
                    if (k == 0 || k == half) { li = 0f; ri = 0f; }
                    s.XRe[k] = lr - ri; s.XIm[k] = li + rr;
                    if (k > 0 && k < half) { int j = _n - k; s.XRe[j] = lr + ri; s.XIm[j] = -li + rr; }
                }
                // inverse = conj(DFT(conj(Z))) / n
                for (int i = 0; i < _n; i++) s.XIm[i] = -s.XIm[i];
                Dft(s);
                for (int i = 0; i < _n; i++)
                {
                    framesL[f * _n + i] = s.XRe[i] / _n * _win[i];
                    framesR[f * _n + i] = -s.XIm[i] / _n * _win[i];
                }
                return s;
            }, _ => { });
            var sum = new float[len + _n];
            var sumR = new float[len + _n];
            var norm = new float[len + _n];
            for (int f = 0; f < dimT; f++)
            {
                int c = f * _hop;
                for (int i = 0; i < _n; i++)
                {
                    sum[c + i] += framesL[f * _n + i];
                    sumR[c + i] += framesR[f * _n + i];
                    norm[c + i] += _win[i] * _win[i];
                }
            }
            for (int i = 0; i < len; i++)
            {
                float w = norm[i + half];
                outL[i] = w > 1e-8f ? sum[i + half] / w : 0f;
                outR[i] = w > 1e-8f ? sumR[i + half] / w : 0f;
            }
        }
    }
}
