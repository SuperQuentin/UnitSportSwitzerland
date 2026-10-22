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

    /// <summary>The vocal and what made it (<see cref="ModelName"/> or <see cref="Centre"/>).</summary>
    public static (float[] Vocal, string Source) Separate(short[] stereo, CancellationToken ct)
    {
        if (ModelName is { } name)
        {
            try { return (ToAnalysisRate(SeparateModel(stereo, ct)), name); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { GD.PushWarning($"[cd] vocal model failed, using the centre fallback: {e.Message}"); }
        }
        return (ToAnalysisRate(SeparateCentre(stereo, ct)), Centre);
    }

    // ---- MDX-Net through ONNX Runtime ------------------------------------------------------------

    /// <summary>
    /// UVR's MDX "demix" without overlap: the mix padded by n_fft/2 on each side and cut into chunks
    /// of hop x (frames - 1) samples, each chunk's STFT (torch layout: periodic Hann, centred with
    /// reflect padding, channels L re, L im, R re, R im, the lowest <c>dim_f</c> bins, the three
    /// lowest zeroed) through the network, which answers the vocal's spectrogram; inverted, the
    /// middle of each chunk kept. Returns the vocal, mono, at 44.1 kHz.
    /// </summary>
    private static float[] SeparateModel(short[] stereo, CancellationToken ct)
    {
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
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
        var vocal = new float[n + pad];
        var spec = new DenseTensor<float>(new[] { 1, 4, dimF, dimT });
        var outL = new float[chunk];
        var outR = new float[chunk];
        for (int start = 0; start < n + pad; start += gen)
        {
            ct.ThrowIfCancellationRequested();
            stft.Forward(l, r, start, chunk, spec, dimF, dimT);
            for (int c = 0; c < 4; c++)
                for (int k = 0; k < 3; k++)
                    for (int f = 0; f < dimT; f++) spec[0, c, k, f] = 0f;
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(input, spec) });
            var answer = results.First().AsTensor<float>();
            stft.Inverse(answer, dimF, dimT, chunk, outL, outR);
            int keep = Math.Min(gen, n + pad - start);
            for (int i = 0; i < keep; i++) vocal[start + i] = 0.5f * (outL[trim + i] + outR[trim + i]);
        }
        Array.Resize(ref vocal, n);
        return vocal;
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
        private readonly float[] _win, _chirpRe, _chirpIm, _kernRe, _kernIm, _aRe, _aIm, _xRe, _xIm;

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
            _aRe = new float[_m];
            _aIm = new float[_m];
            _xRe = new float[n];
            _xIm = new float[n];
        }

        /// <summary>X[k] = sum x[j] e^(-2 pi i jk/n) of (_xRe, _xIm), in place, any n.</summary>
        private void Dft()
        {
            Array.Clear(_aRe);
            Array.Clear(_aIm);
            for (int i = 0; i < _n; i++)
            {
                _aRe[i] = _xRe[i] * _chirpRe[i] - _xIm[i] * _chirpIm[i];
                _aIm[i] = _xRe[i] * _chirpIm[i] + _xIm[i] * _chirpRe[i];
            }
            Fft.Forward(_aRe, _aIm);
            for (int i = 0; i < _m; i++)
            {
                float r = _aRe[i] * _kernRe[i] - _aIm[i] * _kernIm[i];
                float im = _aRe[i] * _kernIm[i] + _aIm[i] * _kernRe[i];
                _aRe[i] = r; _aIm[i] = -im;   // conj: the inverse through the forward transform
            }
            Fft.Forward(_aRe, _aIm);
            for (int i = 0; i < _n; i++)
            {
                float cr = _aRe[i] / _m, ci = -_aIm[i] / _m;
                _xRe[i] = cr * _chirpRe[i] - ci * _chirpIm[i];
                _xIm[i] = cr * _chirpIm[i] + ci * _chirpRe[i];
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

        public void Forward(float[] l, float[] r, int start, int len, DenseTensor<float> spec, int dimF, int dimT)
        {
            int half = _n / 2;
            for (int f = 0; f < dimT; f++)
            {
                int c = f * _hop - half;
                for (int i = 0; i < _n; i++)
                {
                    _xRe[i] = At(l, start, len, c + i) * _win[i];
                    _xIm[i] = At(r, start, len, c + i) * _win[i];
                }
                Dft();
                for (int k = 0; k < dimF; k++)
                {
                    int j = (_n - k) % _n;
                    spec[0, 0, k, f] = 0.5f * (_xRe[k] + _xRe[j]);
                    spec[0, 1, k, f] = 0.5f * (_xIm[k] - _xIm[j]);
                    spec[0, 2, k, f] = 0.5f * (_xIm[k] + _xIm[j]);
                    spec[0, 3, k, f] = -0.5f * (_xRe[k] - _xRe[j]);
                }
            }
        }

        public void Inverse(Tensor<float> spec, int dimF, int dimT, int len, float[] outL, float[] outR)
        {
            int half = _n / 2;
            var sum = new float[len + _n];
            var sumR = new float[len + _n];
            var norm = new float[len + _n];
            for (int f = 0; f < dimT; f++)
            {
                Array.Clear(_xRe);
                Array.Clear(_xIm);
                // Z = L + iR over the full, Hermitian spectrum; bins past dim_f stay zero
                for (int k = 0; k < dimF && k <= half; k++)
                {
                    float lr = spec[0, 0, k, f], li = spec[0, 1, k, f], rr = spec[0, 2, k, f], ri = spec[0, 3, k, f];
                    if (k == 0 || k == half) { li = 0f; ri = 0f; }
                    _xRe[k] = lr - ri; _xIm[k] = li + rr;
                    if (k > 0 && k < half) { int j = _n - k; _xRe[j] = lr + ri; _xIm[j] = -li + rr; }
                }
                // inverse = conj(DFT(conj(Z))) / n
                for (int i = 0; i < _n; i++) _xIm[i] = -_xIm[i];
                Dft();
                int c = f * _hop;
                for (int i = 0; i < _n; i++)
                {
                    sum[c + i] += _xRe[i] / _n * _win[i];
                    sumR[c + i] += -_xIm[i] / _n * _win[i];
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
