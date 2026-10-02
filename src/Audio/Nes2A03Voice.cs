namespace UnitSport.Audio;

/// <summary>
/// Nintendo NES 2A03 APU engine voice. It is a synthesiser, not a sampler, so it ignores
/// <see cref="EngineFrame.Core"/> and rebuilds the engine from the frame parameters with the
/// chip's own limits: two pulse channels, a 4-bit triangle, a 15-bit LFSR noise channel, 4-bit
/// volumes and the real nonlinear mixer.
///
/// <para>
/// Why it sounds like a NES engine: every frequency is forced through an 11-bit period register,
/// so a rev climbs in audible steps (coarse at high pitch, where one period unit is a big
/// interval) instead of gliding; the triangle has no volume control at all, only on/off; and the
/// noise channel's short mode (bit0 xor bit6, a 93-step loop) is a pitched metallic buzz that
/// stands in for exhaust rasp, while long mode is plain hiss for rotor wash.
/// </para>
/// </summary>
public sealed class Nes2A03Voice : IChipVoice
{
    /// <summary>NTSC CPU clock, Hz. Every channel's pitch derives from it.</summary>
    private const double Cpu = 1789773.0;

    /// <summary>Real NTSC noise timer periods, CPU cycles between LFSR shifts.</summary>
    private static readonly int[] NoisePeriods =
        { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };

    /// <summary>The triangle's 32-step, 4-bit staircase: 15 down to 0 and back up to 15.</summary>
    private static readonly int[] TriSteps = BuildTriangle();

    private double _p1, _p2, _tri, _noiseAcc;
    private ushort _lfsr;
    private int _burst;
    private float _hp, _lp;
    private readonly float _hpA = Dsp.Coef(90f);
    private readonly float _lpA = Dsp.Coef(9000f);

    public Nes2A03Voice(int seed)
    {
        // The LFSR must never be zero (it would stay there); real hardware powers up at 1.
        uint h = (uint)seed * 2654435761u;
        _lfsr = (ushort)(1 | ((h >> 17) & 0x7FFE));
    }

    private static int[] BuildTriangle()
    {
        var t = new int[32];
        for (int i = 0; i < 16; i++) { t[i] = 15 - i; t[16 + i] = i; }
        return t;
    }

    /// <summary>
    /// Snaps a requested frequency to what the period register can produce, returning the period
    /// (0..2047). This quantisation is the whole "chip" character of a rev.
    /// </summary>
    private static int Period(double hz, double divider)
    {
        if (!(hz > 1.0)) return 2047;
        double p = Cpu / (divider * hz) - 1.0;
        return (int)Math.Clamp(Math.Round(p), 0.0, 2047.0);
    }

    public float Next(in EngineFrame f)
    {
        float load = Math.Clamp(f.Load, 0f, 1f);
        float pulse = Math.Clamp(f.Pulse, 0f, 1f);
        float rpm = Math.Clamp(f.Rpm01, 0f, 1f);
        float noiseAmt = Math.Clamp(f.Noise, 0f, 1f);

        // Duty gets thinner (nastier) as load rises; 50% is the mellow idle.
        double duty = load > 0.66f ? 0.125 : load > 0.33f ? 0.25 : 0.5;

        // ---- Pulse 1: the engine note. Pulse 2: detuned (piston) or an octave down (turbine) to beat.
        int per1 = Period(f.ToneHz, 16);
        int per2 = f.Turbine ? Math.Min(2047, per1 * 2 + 1) : Math.Min(2047, per1 + 2);
        double hz1 = Cpu / (16.0 * (per1 + 1));
        double hz2 = Cpu / (16.0 * (per2 + 1));
        _p1 += hz1 / Dsp.Rate; if (_p1 >= 1.0) _p1 -= Math.Floor(_p1);
        _p2 += hz2 / Dsp.Rate; if (_p2 >= 1.0) _p2 -= Math.Floor(_p2);

        // Envelope: the combustion / blade pulse shapes a 4-bit volume, like a hardware decay.
        float env = f.Turbine ? 0.55f + 0.45f * pulse : 0.45f + 0.55f * pulse;
        int vol1 = (int)Math.Round(15f * env * (0.6f + 0.4f * load));
        int vol2 = (int)Math.Round(15f * env * 0.6f * (0.5f + 0.5f * load));
        // Periods under 8 are muted by the hardware's sweep unit.
        int p1 = per1 < 8 ? 0 : (_p1 < duty ? vol1 : 0);
        int p2 = per2 < 8 ? 0 : (_p2 < 0.5 ? vol2 : 0);

        // ---- Triangle: body at SubHz. No volume register, so it is only ever on or off.
        int perT = Period(f.SubHz, 32);
        double hzT = Cpu / (32.0 * (perT + 1));
        _tri += hzT / Dsp.Rate; if (_tri >= 1.0) _tri -= Math.Floor(_tri);
        bool triOn = f.Turbine ? pulse > 0.3f : true;
        int tri = triOn ? TriSteps[(int)(_tri * 32.0) & 31] : 0;

        // ---- Noise: LFSR clocked at CPU/period, averaged over the clocks in one output sample
        // so the fast settings do not alias into whistles.
        bool shortMode = !f.Turbine && load > 0.45f;
        int idx = f.Turbine
            ? 11 + (int)(rpm * 3.99f)                    // slow, breathy rotor wash
            : 13 - (int)(rpm * 9.99f);                   // faster and higher as the revs climb
        idx = Math.Clamp(idx, 0, 15);
        int noiseVol;
        if (f.Crackle > 0f) _burst = Dsp.Rate * 4 / 1000;
        if (_burst > 0) { _burst--; noiseVol = 15; idx = Math.Min(idx, 6); shortMode = true; }
        else
        {
            float n = f.Turbine ? noiseAmt * (0.5f + 0.5f * pulse)
                                : noiseAmt * (0.3f + 0.7f * pulse) * (0.5f + 0.5f * load);
            noiseVol = (int)Math.Round(15f * Math.Clamp(n, 0f, 1f));
        }
        _noiseAcc += Cpu / NoisePeriods[idx] / Dsp.Rate;
        int clocks = (int)_noiseAcc;
        _noiseAcc -= clocks;
        int bitSum = 0;
        int shift = shortMode ? 6 : 1;
        for (int i = 0; i < clocks; i++)
        {
            int fb = (_lfsr & 1) ^ ((_lfsr >> shift) & 1);
            _lfsr = (ushort)((_lfsr >> 1) | (fb << 14));
            bitSum += (_lfsr & 1) ^ 1;   // output is high while bit 0 is clear
        }
        double noiseOut = clocks > 0
            ? (double)bitSum / clocks * noiseVol
            : ((_lfsr & 1) == 0 ? noiseVol : 0);

        // ---- The real nonlinear mixer (guarded against zero denominators).
        double pSum = p1 + p2;
        double pulseOut = pSum > 0 ? 95.88 / (8128.0 / pSum + 100.0) : 0.0;
        double tn = tri / 8227.0 + noiseOut / 12241.0;
        double tndOut = tn > 0 ? 159.79 / (1.0 / tn + 100.0) : 0.0;
        float x = (float)((pulseOut + tndOut) * 2.9);   // the APU mixer tops out low; matched to the other voices by --soundcheck RMS

        // ---- DC block (the NES output's ~90 Hz high-pass) then a light one-pole low-pass.
        _hp += _hpA * (x - _hp);
        float y = x - _hp;
        _lp += _lpA * (y - _lp);

        float o = _lp * Math.Clamp(f.Level, 0f, 1f) * f.SteamGate;
        return float.IsFinite(o) ? Math.Clamp(o, -1f, 1f) : 0f;
    }
}
