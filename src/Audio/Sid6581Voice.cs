namespace UnitSport.Audio;

/// <summary>
/// Commodore 64 SID 6581 engine voice. Like the real chip it is a synthesiser, not a sampler, so
/// it ignores <see cref="EngineFrame.Core"/> and builds the engine from the frame's parameters.
///
/// <para>
/// The recipe is the one C64 racing games used: a sawtooth fundamental, a second pulse oscillator
/// hard-synced to it and swept upward with revs (the sync "growl" — the slave is reset every time
/// the master wraps, so the timbre is a harmonic series that slides as the ratio changes), a 23-bit
/// LFSR noise voice for exhaust, and a resonant 12 dB/oct state-variable low-pass whose cutoff opens
/// with revs and load. The 6581's filter was notoriously non-linear and dark; a tanh in the
/// feedback path stands in for that grit (the later 8580 was clean, which is why it sounds different).
/// </para>
///
/// <para>
/// Pitch goes through the 16-bit frequency register at the PAL clock, and phases are 24-bit
/// accumulators with 12-bit outputs, so the tuning steps and the lo-fi quantisation are authentic
/// rather than an approximation. No allocation in <see cref="Next"/>.
/// </para>
/// </summary>
public sealed class Sid6581Voice : IChipVoice
{
    private const double PalClock = 985248.0;
    private const uint AccMask = 0xFFFFFF;
    /// <summary>Accumulator units gained per output sample per register unit: clock / sample rate.</summary>
    private const double ClockPerSample = PalClock / Dsp.Rate;

    // Oscillator accumulators (24-bit).
    private uint _acc1, _acc2, _acc3;
    // 23-bit noise shift register.
    private uint _lfsr = 0x7FFFF8;
    private int _noiseOut;
    // Envelope and crackle burst.
    private float _env, _crackle, _gate;
    // Slow PWM LFO phase (cycles).
    private float _lfo;
    // Filter state and DC blocker.
    private float _low, _band;
    private float _dcX, _dcY;

    public Sid6581Voice(int seed)
    {
        // A different starting phase per engine so two identical engines do not add coherently.
        uint s = (uint)seed * 2654435761u;
        _acc1 = s & AccMask;
        _acc3 = (s >> 3) & AccMask;
        _lfsr = (0x7FFFF8u ^ (s & 0x3FFFFu)) & 0x7FFFFF;
        if (_lfsr == 0) _lfsr = 0x7FFFF8;
        _lfo = (s & 0xFF) / 255f;
    }

    /// <summary>Hz to the SID's 16-bit frequency register (freq = reg * clock / 2^24), then to a per-sample step.</summary>
    private static uint Step(float hz)
    {
        if (!(hz > 0f)) return 0;
        double reg = Math.Round(hz * 16777216.0 / PalClock);
        if (reg > 65535) reg = 65535;
        return (uint)(reg * ClockPerSample);
    }

    /// <summary>Clocks the LFSR on a rising bit 19 of the noise accumulator, as the chip does.</summary>
    private void AdvanceNoise(uint step)
    {
        uint before = _acc3;
        _acc3 = (_acc3 + step) & AccMask;
        if ((before & 0x80000) == 0 && (_acc3 & 0x80000) != 0)
        {
            uint bit = ((_lfsr >> 22) ^ (_lfsr >> 17)) & 1;
            _lfsr = ((_lfsr << 1) | bit) & 0x7FFFFF;
            // Output is taken from eight tap bits, not the register's end - that is the SID's noise colour.
            int o = (int)(((_lfsr >> 22) & 1) << 7 | ((_lfsr >> 20) & 1) << 6 | ((_lfsr >> 16) & 1) << 5
                | ((_lfsr >> 13) & 1) << 4 | ((_lfsr >> 11) & 1) << 3 | ((_lfsr >> 7) & 1) << 2
                | ((_lfsr >> 4) & 1) << 1 | ((_lfsr >> 2) & 1));
            _noiseOut = o;
        }
    }

    public float Next(in EngineFrame f)
    {
        float rpm = Math.Clamp(f.Rpm01, 0f, 1f);
        float load = Math.Clamp(f.Load, 0f, 1f);
        float pulse = Math.Clamp(f.Pulse, 0f, 1f);

        // --- envelope: fast attack, slower release, retriggered on each pulse start ---
        if (f.PulseStart > 0.5f) { _gate = 1f; _env = Math.Max(_env, 0.5f); }
        _gate *= 0.9990f;
        float target = 0.45f + 0.55f * Math.Max(pulse, _gate);
        _env += (target - _env) * (target > _env ? 0.35f : 0.0035f);

        if (f.Crackle > 0.5f) _crackle = 1f;
        _crackle *= 0.9985f;
        if (_crackle < 1e-4f) _crackle = 0f;

        float raw;
        if (f.Turbine)
        {
            // Helicopter: triangle whine, noise chopped by the blade pass, pulse thump at SubHz.
            uint s1 = Step(f.ToneHz);
            _acc1 = (_acc1 + s1) & AccMask;
            int tri = (int)(_acc1 >> 11);
            if ((_acc1 & 0x800000) != 0) tri = 0xFFF - tri;
            tri &= 0xFFF;
            float whine = (tri - 2048) / 2048f;

            uint s2 = Step(f.SubHz);
            _acc2 = (_acc2 + s2) & AccMask;
            float thump = ((_acc2 >> 12) >= 0x600 ? 1f : -1f) * pulse;

            AdvanceNoise(Step(2500f + 5000f * f.Noise));
            float nz = (_noiseOut - 128) / 128f;
            float chop = pulse * (0.4f + 0.6f * f.Noise) + 1.2f * _crackle;

            raw = 0.30f * whine + 0.50f * thump + 0.80f * nz * chop;
        }
        else
        {
            // Osc1: sawtooth fundamental.
            uint s1 = Step(f.ToneHz);
            uint prev1 = _acc1;
            _acc1 = (_acc1 + s1) & AccMask;
            bool wrapped = _acc1 < prev1;
            float saw = ((int)(_acc1 >> 12) - 2048) / 2048f;

            // Osc2: pulse, hard-synced to osc1 at a ratio that climbs with revs and load.
            float ratio = 1.5f + 2.0f * Math.Clamp(0.6f * rpm + 0.4f * load, 0f, 1f);
            uint s2 = Step(f.ToneHz * ratio);
            _acc2 = wrapped ? 0u : (_acc2 + s2) & AccMask;
            _lfo += 0.7f / Dsp.Rate;
            if (_lfo >= 1f) _lfo -= 1f;
            float lfo = MathF.Sin(_lfo * 6.2831853f);
            int pw = (int)(2048 + 900 * lfo + 700 * load);
            pw = Math.Clamp(pw, 256, 3840);
            float sq = ((int)(_acc2 >> 12) >= pw ? 1f : -1f);

            // Osc3: noise for the exhaust, louder on each firing pulse and on backfires.
            AdvanceNoise(Step(3000f + 9000f * rpm));
            float nz = (_noiseOut - 128) / 128f;
            float nAmp = (0.25f * f.Noise + 0.55f * pulse * f.Noise + 0.10f * load) + 1.3f * _crackle;

            raw = 0.55f * saw + 0.40f * sq + nz * nAmp * 0.6f;
        }

        raw *= _env;

        // --- 6581 state-variable low-pass, 12 dB/oct ---
        float drive = rpm * (0.4f + 0.6f * load);
        float cutoff = 200f + 2800f * drive + 1500f * _crackle;
        float fc = 2f * MathF.Sin(MathF.PI * Math.Min(cutoff, 6000f) / Dsp.Rate);
        const float q = 1f / 0.7f;
        float high = raw - _low - q * _band;
        _band += fc * high;
        // The 6581 filter saturates; tanh on the band path gives its darker, grittier bite.
        _band = MathF.Tanh(_band * 1.2f) / 1.2f;
        _low += fc * _band;
        _low = Math.Clamp(_low, -4f, 4f);
        _band = Math.Clamp(_band, -4f, 4f);
        if (float.IsNaN(_low) || float.IsNaN(_band)) { _low = 0; _band = 0; }

        // --- DC removal (the chip's voices are unsigned; the filter and env shift the mean) ---
        float y = _low - _dcX + 0.995f * _dcY;
        _dcX = _low;
        _dcY = y;

        float o = Math.Clamp(y * 0.8f, -0.8f, 0.8f) * f.Level;
        return float.IsNaN(o) ? 0f : o;
    }
}
