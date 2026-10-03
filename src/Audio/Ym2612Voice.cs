namespace UnitSport.Audio;

/// <summary>
/// Sega Mega Drive engine: one YM2612 four-operator FM channel plus the SN76489 PSG's white noise,
/// aimed at the gritty OutRun / Super Hang-On / Road Rash engine. Synthesised from the frame's
/// parameters (<see cref="EngineFrame.Core"/> is ignored: FM cannot play a recording).
///
/// <para>
/// <b>Algorithm 4</b> (OP1→OP2, OP3→OP4, OP2+OP4 heard) rather than 5. Two independent pairs give two
/// carriers that can be detuned a hair apart, and that slow beating between them is what makes an
/// engine sound like several cylinders rather than one tone. Algorithm 5 puts one modulator over three
/// carriers, so all three share the same spectrum and only differ in level: thicker, but static.
/// Here the pair with OP1's self-feedback supplies rasp, and the pair modulated at half the carrier
/// frequency (OP3 at ×0.5) supplies the sub-harmonic growl of a firing rate.
/// </para>
///
/// <para>
/// Authenticity is kept where it is audible: pitch is snapped to the chip's 11-bit F-number and 3-bit
/// block (so glides step), operators are a 1024-entry sine quantised to 14 bits, total level moves in
/// the chip's 0.75 dB steps, feedback averages the last two OP1 outputs, and the final DAC is 9 bit with
/// the "ladder effect" crossover gap that makes a Genesis buzz at low levels.
/// </para>
/// </summary>
public sealed class Ym2612Voice : IChipVoice
{
    const double Clock = 7670453.0;
    /// <summary>The chip's native sample rate (clock / 144); the phase step formula is defined in it.</summary>
    const double NativeRate = Clock / 144.0;
    const int TableSize = 1024;

    static readonly int[] SineTable = BuildSine();

    static int[] BuildSine()
    {
        var t = new int[TableSize];
        for (int i = 0; i < TableSize; i++)
            t[i] = (int)Math.Round(Math.Sin(2 * Math.PI * (i + 0.5) / TableSize) * 8191.0);
        return t;
    }

    // operator phases, in cycles
    double _p1, _p2, _p3, _p4;
    // OP1 feedback memory (last two outputs)
    int _fbA, _fbB;

    float _modEnv;      // retriggered on PulseStart: each combustion bites
    float _thumpEnv;    // helicopter FM thump
    float _burst;       // crackle burst envelope
    readonly float _modDecay = MathF.Exp(-1f / (0.028f * Dsp.Rate));
    readonly float _thumpDecay = MathF.Exp(-1f / (0.06f * Dsp.Rate));
    readonly float _burstDecay = MathF.Exp(-1f / (0.07f * Dsp.Rate));

    // PSG noise: 16-bit LFSR, white mode taps bits 0 and 3
    int _lfsr = 0x8000;
    double _noiseClock;
    float _noiseBit = 1f;

    // DC blocker
    float _dcX, _dcY;

    public Ym2612Voice(int seed)
    {
        // the PSG resets with only the top bit set; the seed just picks where in its sequence we start
        _lfsr = 0x8000 | (seed & 0x7FFF);
        _p3 = (seed * 0.37) % 1.0;
    }

    /// <summary>Snaps a frequency to what an (F-number, block) pair can actually produce.</summary>
    static double Quantise(double hz)
    {
        hz = Math.Clamp(hz, 8.0, 7000.0);
        // f = fnum * 2^(block-1) * NativeRate / 2^20 ; choose the lowest block that fits 11 bits
        for (int block = 0; block < 8; block++)
        {
            double unit = NativeRate * Math.Pow(2, block - 1) / 1048576.0;
            double fnum = Math.Round(hz / unit);
            if (fnum < 2048) return Math.Max(fnum, 1) * unit;
        }
        return hz;
    }

    /// <summary>Total level 0..127 in 0.75 dB steps to a linear gain.</summary>
    static float TlGain(int tl) => MathF.Pow(10f, -0.0375f * tl);

    static int Wrap(int i) => i & (TableSize - 1);

    /// <summary>One operator: sine at phase+modulation, 14-bit, scaled by a gain.</summary>
    static int Op(double phaseCycles, int modIn, float gain)
    {
        int idx = Wrap((int)(phaseCycles * TableSize) + (modIn >> 1));
        return (int)(SineTable[idx] * gain);
    }

    public float Next(in EngineFrame f)
    {
        float load = Math.Clamp(f.Load, 0f, 1f);
        float thr = Math.Clamp(f.Throttle, 0f, 1f);
        float rpm = Math.Clamp(f.Rpm01, 0f, 1f);

        if (f.PulseStart > 0.5f) { _modEnv = 1f; _thumpEnv = 1f; }
        _modEnv *= _modDecay;
        _thumpEnv *= _thumpDecay;
        if (f.Crackle > 0.5f) _burst = 1f;
        _burst *= _burstDecay;

        float tone = f.ToneHz > 1f ? f.ToneHz : 30f;
        float sub = f.SubHz > 1f ? f.SubHz : tone * 0.5f;

        // FM index rises with load and throttle: modulator TL 60 (gentle) .. 18 (about 5 rad)
        float index = Math.Clamp(0.12f + 0.55f * load + 0.3f * thr, 0f, 1f);
        int modTl = 60 - (int)(index * 42f);
        // the bite: each combustion briefly opens the modulators, in between they settle
        float bite = 0.4f + 0.6f * _modEnv;
        float modGain = TlGain(modTl) * bite;

        // feedback from 3 (idle) to 6 (full load)
        int fb = 3 + (int)MathF.Round(3f * load);

        double c1, c2, c3, c4;   // operator frequencies
        float carrier2Gain, carrier4Gain;
        float op3Gain;
        if (f.Turbine)
        {
            // whine carrier, a low-index shimmer over it, and a thump at the rotor body on each blade
            c2 = Quantise(tone);
            c1 = Quantise(tone * 1.41);
            c4 = Quantise(sub);
            c3 = Quantise(sub * 1.0);
            carrier2Gain = 0.45f;
            carrier4Gain = 0.85f * _thumpEnv;
            op3Gain = TlGain(20) * _thumpEnv;
            modGain = TlGain(58) * (0.5f + 0.5f * f.Pulse);
        }
        else
        {
            c2 = Quantise(tone);
            c1 = Quantise(tone * 1.41);
            c4 = Quantise(tone * 1.004);   // a hair off: beating between the two carriers
            c3 = Quantise(tone * 0.5);
            carrier2Gain = 0.55f + 0.45f * f.Pulse;
            carrier4Gain = 0.4f + 0.4f * f.Pulse;
            op3Gain = modGain;
        }

        _p1 = (_p1 + c1 / Dsp.Rate) % 1.0;
        _p2 = (_p2 + c2 / Dsp.Rate) % 1.0;
        _p3 = (_p3 + c3 / Dsp.Rate) % 1.0;
        _p4 = (_p4 + c4 / Dsp.Rate) % 1.0;

        // OP1 with self-feedback: average of last two outputs, shifted by the feedback level
        int fbMod = ((_fbA + _fbB) >> 1) >> (10 - fb);
        int o1 = Op(_p1, fbMod << 1, modGain);
        _fbB = _fbA; _fbA = o1;
        int o2 = Op(_p2, o1, carrier2Gain);
        int o3 = Op(_p3, 0, op3Gain);
        int o4 = Op(_p4, o3, carrier4Gain);
        float fm = (o2 + o4) / 16384f;   // both carriers summed, 14-bit each

        // PSG white noise, clocked from rpm, gated by the pulse
        double noiseHz = 1500.0 + 9000.0 * rpm + 12000.0 * _burst;
        _noiseClock += noiseHz / Dsp.Rate;
        while (_noiseClock >= 1.0)
        {
            _noiseClock -= 1.0;
            int fbBit = (_lfsr ^ (_lfsr >> 3)) & 1;
            _lfsr = (_lfsr >> 1) | (fbBit << 15);
            _noiseBit = (_lfsr & 1) != 0 ? 1f : -1f;
        }
        float gate = f.Turbine ? (0.15f + 0.85f * f.Pulse) : (0.25f + 0.75f * f.Pulse);
        float noiseLevel = (0.04f + 0.22f * Math.Clamp(f.Noise, 0f, 1f)) * gate + 0.22f * _burst;
        // the PSG's volume is 4 bits in 2 dB steps; quantise the level the same way
        noiseLevel = PsgLevel(noiseLevel);
        float mix = fm * 0.62f + _noiseBit * noiseLevel;

        // DAC: 9 bit with the ladder effect's crossover gap (codes near zero jump away from it)
        int code = (int)MathF.Round(Math.Clamp(mix, -1f, 0.996f) * 256f);
        float dac = code / 256f;
        if (code > 0) dac += 3f / 256f;
        else if (code < 0) dac -= 3f / 256f;

        // DC blocker: the crossover offset and asymmetry leave a bias
        float y = dac - _dcX + 0.995f * _dcY;
        _dcX = dac; _dcY = y;

        float o = Math.Clamp(y * 1.25f, -0.8f, 0.8f) * f.Level * f.SteamGate;
        return float.IsFinite(o) ? o : 0f;
    }

    static float PsgLevel(float v)
    {
        if (v <= 0.004f) return 0f;
        // nearest of 15 steps of -2 dB from full scale
        int step = (int)MathF.Round(-MathF.Log10(Math.Min(v, 1f)) * 10f);
        return step >= 15 ? 0f : MathF.Pow(10f, -0.1f * step);
    }
}
