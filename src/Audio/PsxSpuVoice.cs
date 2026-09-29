namespace UnitSport.Audio;

/// <summary>
/// PS1 SPU engine voice. The PlayStation was a sampler, not a synthesiser: an engine on it was a
/// recording, squeezed into 4-bit ADPCM and replayed at a pitch. So this voice does not build
/// oscillators; it takes the realistic model's <see cref="EngineFrame.Core"/> and degrades it the
/// way the console would have, in the same order the hardware does:
///
/// <list type="number">
/// <item>Treat the signal as recorded at ~11 kHz (every second sample kept): sample RAM was 512 KB,
/// so engine loops were stored low-rate and dull.</item>
/// <item>Encode it in real SPU ADPCM blocks (28 samples, one of 5 prediction filters, a 0..12 shift,
/// 4-bit residuals) and decode again. The closed-loop quantisation noise is the crunch.</item>
/// <item>Play it back through 4-point Gaussian interpolation at half phase steps: the SPU never used
/// linear or sinc interpolation, and the Gaussian is why it sounds muffled but smooth.</item>
/// <item>Quantise to 1/16384 (the SPU mixes in 16 bits with headroom) and add a small reverb tail
/// from the SPU's reverb unit, kept low so the engine stays legible.</item>
/// </list>
/// Costs about 65 multiply-adds per output sample for the brute-force filter/shift search, none of
/// it allocating: every buffer is a fixed array made in the constructor.
/// </summary>
public sealed class PsxSpuVoice : IChipVoice
{
    private const int BlockLen = 28;

    // SPU ADPCM prediction filters: predicted = s1 * K1 + s2 * K2 (previous two decoded samples).
    private static readonly float[] K1 = { 0f, 60f / 64f, 115f / 64f, 98f / 64f, 122f / 64f };
    private static readonly float[] K2 = { 0f, 0f, -52f / 64f, -55f / 64f, -60f / 64f };

    // Gaussian kernels for the two output phases per stored sample (0 and 0.5), over taps at
    // offsets -1, 0, +1, +2 from the newest-but-one sample. Built once from exp(-d^2 / 2 sigma^2).
    private static readonly float[] Gauss0 = MakeKernel(0f);
    private static readonly float[] Gauss5 = MakeKernel(0.5f);

    private static float[] MakeKernel(float phase)
    {
        const float sigma = 0.62f;
        var k = new float[4];
        float sum = 0;
        for (int i = 0; i < 4; i++)
        {
            float d = (i - 1) - phase;
            k[i] = MathF.Exp(-d * d / (2 * sigma * sigma));
            sum += k[i];
        }
        for (int i = 0; i < 4; i++) k[i] /= sum;
        return k;
    }

    // ADPCM state
    private readonly float[] _in = new float[BlockLen];     // 16-bit-scaled input being collected
    private readonly float[] _dec = new float[BlockLen];    // decoded block currently playing
    private readonly float[] _try = new float[BlockLen];
    private readonly float[] _best = new float[BlockLen];
    private int _n;          // samples collected
    private int _p = BlockLen; // playback index into _dec
    private float _s1, _s2;  // decoder history carried across blocks, as the hardware does

    // Gaussian history: h0 oldest .. h3 newest
    private float _h0, _h1, _h2, _h3;
    private bool _odd;

    // Reverb: two combs and an allpass, short so it reads as the SPU's small-room preset.
    private readonly float[] _c1 = new float[331];
    private readonly float[] _c2 = new float[467];
    private readonly float[] _ap = new float[97];
    private int _i1, _i2, _ia;
    private float _lp1, _lp2;

    public PsxSpuVoice(int seed) { }

    public float Next(in EngineFrame f)
    {
        float core = float.IsFinite(f.Core) ? Math.Clamp(f.Core, -1f, 1f) : 0f;
        float level = float.IsFinite(f.Level) ? f.Level : 0f;

        float dry;
        if (!_odd)
        {
            // A new stored (11 kHz) sample is consumed: play the decoded block, then feed the encoder.
            float s = _p < BlockLen ? _dec[_p++] : 0f;
            _h0 = _h1; _h1 = _h2; _h2 = _h3; _h3 = s;

            _in[_n++] = core * 32767f;
            if (_n == BlockLen)
            {
                EncodeBlock();
                _n = 0;
                _p = 0;
            }
            dry = _h0 * Gauss0[0] + _h1 * Gauss0[1] + _h2 * Gauss0[2] + _h3 * Gauss0[3];
        }
        else
        {
            dry = _h0 * Gauss5[0] + _h1 * Gauss5[1] + _h2 * Gauss5[2] + _h3 * Gauss5[3];
        }
        _odd = !_odd;

        float x = dry / 32768f;
        x = MathF.Round(x * 16384f) / 16384f;

        float wet = Reverb(x);
        float y = (x + 0.15f * wet) * level;
        if (!float.IsFinite(y)) y = 0f;
        return Math.Clamp(y, -1f, 1f);
    }

    /// <summary>
    /// Encodes <see cref="_in"/> as one SPU block by trying every filter and shift, keeping the
    /// choice with the least closed-loop squared error, and leaves the decoded result in
    /// <see cref="_dec"/>. Closed-loop (predicting from decoded, not original, samples) is what the
    /// real decoder sees, so the search measures the noise that will actually be heard.
    /// </summary>
    private void EncodeBlock()
    {
        float bestErr = float.MaxValue;
        float bs1 = _s1, bs2 = _s2;

        for (int flt = 0; flt < 5; flt++)
        {
            float k1 = K1[flt], k2 = K2[flt];
            for (int shift = 0; shift <= 12; shift++)
            {
                float step = 1 << (12 - shift);
                float s1 = _s1, s2 = _s2, err = 0;
                for (int i = 0; i < BlockLen; i++)
                {
                    float pred = s1 * k1 + s2 * k2;
                    float q = MathF.Round((_in[i] - pred) / step);
                    q = Math.Clamp(q, -8f, 7f);
                    float r = Math.Clamp(pred + q * step, -32768f, 32767f);
                    float e = _in[i] - r;
                    err += e * e;
                    _try[i] = r;
                    s2 = s1; s1 = r;
                }
                if (err < bestErr)
                {
                    bestErr = err;
                    Array.Copy(_try, _best, BlockLen);
                    bs1 = s1; bs2 = s2;
                }
            }
        }

        Array.Copy(_best, _dec, BlockLen);
        _s1 = bs1; _s2 = bs2;
    }

    private float Reverb(float x)
    {
        float a = _c1[_i1];
        _lp1 += 0.5f * (a - _lp1);                // damp the tail so it goes dark, like the SPU's
        _c1[_i1] = x + _lp1 * 0.62f;
        if (++_i1 >= _c1.Length) _i1 = 0;

        float b = _c2[_i2];
        _lp2 += 0.5f * (b - _lp2);
        _c2[_i2] = x + _lp2 * 0.58f;
        if (++_i2 >= _c2.Length) _i2 = 0;

        float c = 0.5f * (a + b);
        float v = _ap[_ia];
        float w = c + 0.5f * v;
        _ap[_ia] = w;
        if (++_ia >= _ap.Length) _ia = 0;
        return v - 0.5f * w;
    }
}
