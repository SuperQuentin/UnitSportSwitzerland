using System;
using System.Globalization;

namespace UnitSport.Avatar;

/// <summary>One wave on one body channel (#2055): <c>Base + Amp * shape</c> over a period of ½, 1, 2 or 4 beats.</summary>
public struct GenChan
{
    /// <summary>Index into <see cref="DanceGen.PeriodBeats"/>.</summary>
    public byte Period;
    /// <summary>Eighths of a cycle.</summary>
    public byte Phase;
    /// <summary>0 sine, 1 hold (a flattened sine), 2 snap (hit and decay), 3 saw (slow rise, quick return).</summary>
    public byte Shape;
    public float Amp, Base;
}

/// <summary>A wrist target (offset from the shoulder, x already signed per side) and the elbow's pole.</summary>
public struct GenArm { public float X, Y, Z, HX, HY, HZ; }

/// <summary>A foot target: ankle x/z, height above its rest height, toe turn, toe lift 0..1, heel raise.</summary>
public struct GenFoot { public float X, Y, Z, Turn, Lift, Heel; }

/// <summary>Every channel of one instant of a generated move, in the terms of <c>DanceCh</c>.</summary>
public struct GenFrame
{
    public float Px, Py, Pz, Tilt, Theta, Phi, Psi, ThN, PhH, ShL, ShR;
    public GenArm ArmL, ArmR;
    public GenFoot FootL, FootR;
}

/// <summary>One generated dance move: a few numbers, drawn once from stable hashes, evaluated every frame by <see cref="DanceGen.Sample"/>.</summary>
public sealed class GenMove
{
    public byte Style, Index, Energy;
    public GenChan Px, Py, Pz, Tilt, Theta, Phi, Psi, ThN, PhH, Sh;
    public bool ShAlt;
    /// <summary>0 both arms mirror, 1 the second arm runs two keys behind, 2 the second arm has its own keys.</summary>
    public byte ArmMode;
    /// <summary>Beats each arm key lasts: 1 or 2.</summary>
    public byte KeyBeats;
    public readonly byte[] KeysA = new byte[4], KeysB = new byte[4];
    /// <summary>The share of a key's time spent travelling to it; the rest holds.</summary>
    public float Snap;
    /// <summary>0 planted, 1 step-touch, 2 heel lift, 3 march, 4 kick.</summary>
    public byte LegKind;
    /// <summary>Foot turns: 0.5 = each foot once in two beats, 1 = each foot every beat.</summary>
    public float LegRate;
    public float LegAmp, Stance, Turn;
    /// <summary>The moving variant: how much of the body stays, how much the arms take over from the gait arms.</summary>
    public float KMov, ArmBlend;
    public uint Signature;
}

/// <summary>
/// The procedural dance move generator (#2055). Each style owns a fixed pool of
/// <see cref="PoolSize"/> moves, built once from FNV hashes of (style, index, salt) and small integer
/// tables, so every machine builds the same bytes and every move a player can get is in
/// <c>--emotecheck</c> and <c>--dancesheet gen:style</c>. A move is a handful of waves on the body
/// channels, a 4-key arm cycle drawn from 16 anchors, and a leg kind; <see cref="Check"/> throws away
/// (re-rolls with the next salt) any that would put a hand through the body, stretch an arm or a leg,
/// tip over or hardly move. Plain C# (no Godot types), so tier 0 tests cover it
/// (<c>DanceGenTests</c>); <c>HumanMeshBuilder.Generated.cs</c> lays a <see cref="GenFrame"/> onto the rig.
/// </summary>
public static class DanceGen
{
    public const int PoolSize = 32;
    /// <summary>Beats in a generated move's loop: two bars.</summary>
    public const float LoopBeats = 8f;
    public static readonly float[] PeriodBeats = { 0.5f, 1f, 2f, 4f };

    private const float AnkleHeight = 0.085f;
    private const int Styles = 7;
    private static readonly string[] StyleNames = { "Pop", "Rock", "Electronic", "HipHop", "Chill", "Folk", "RatDance" };

    // ---- anchors: out, up, fwd (torso frame from the shoulder, out mirrors), elbow pole, intensity ----
    private static readonly float[,] Anchor =
    {
        { 0.12f, -0.44f, 0.02f,   0.6f, -0.6f, -0.2f,   0 },   // 0 hang
        { 0.05f, -0.40f, 0.06f,   1.0f,  0.0f, -0.3f,   0 },   // 1 hands on hips
        { -0.10f, -0.10f, 0.30f,  0.6f, -0.6f, -0.2f,   1 },   // 2 clap, hands together
        { 0.10f,  0.00f, 0.42f,   0.5f, -0.8f, -0.1f,   1 },   // 3 straight ahead
        { 0.45f,  0.05f, 0.00f,   0.2f, -1.0f, -0.3f,   1 },   // 4 out to the side
        { 0.28f,  0.38f, 0.00f,   0.6f, -0.6f, -0.3f,   2 },   // 5 up and out
        { 0.12f,  0.45f, 0.05f,   0.8f, -0.2f, -0.3f,   2 },   // 6 up
        { -0.12f, -0.15f, 0.30f,  0.3f, -0.9f, -0.2f,   1 },   // 7 crossed over the chest
        { 0.00f,  0.30f, 0.02f,   1.0f,  0.0f, -0.3f,   1 },   // 8 by the ear
        { 0.00f, -0.12f, 0.28f,   0.8f, -0.6f, -0.2f,   0 },   // 9 on the chest
        { 0.10f, -0.30f, 0.30f,   0.6f, -0.6f, -0.2f,   0 },   // 10 low, forward
        { 0.15f,  0.20f, 0.38f,   0.6f, -0.7f, -0.2f,   2 },   // 11 forward and up
        { 0.32f, -0.30f, 0.00f,   0.5f, -0.7f, -0.4f,   0 },   // 12 low, to the side
        { 0.12f, -0.30f, -0.22f,  0.6f, -0.8f, -0.1f,   1 },   // 13 back
        { 0.04f,  0.00f, 0.48f,   0.6f, -0.8f, -0.2f,   2 },   // 14 punch
        { 0.33f, -0.22f, 0.26f,   0.6f, -0.6f, -0.2f,   1 },   // 15 wide, forward
    };

    private static ushort M(params int[] bits)
    {
        int m = 0;
        foreach (int b in bits) m |= 1 << b;
        return (ushort)m;
    }

    private sealed record Prof(int Calm, int Mid, ushort Mask, int[] Legs, int[] Shapes, int[] Snaps, int OneBeatKeys,
        float StanceLo, float StanceHi, float SquatLo, float SquatHi, float LeanLo, float LeanHi,
        float[] Gain, int[] Prob);

    // gains and probabilities (percent active) in the order Px Py Pz Tilt Theta Phi Psi Nod Head Sh
    private static readonly Prof[] Profiles =
    {
        // Pop: a bit of everything
        new(8, 14, M(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 15), new[] { 3, 4, 3, 2, 1 }, new[] { 5, 2, 2, 1 }, new[] { 2, 3, 3 }, 50,
            0.11f, 0.16f, 0.04f, 0.09f, 0.03f, 0.10f,
            new[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f }, new[] { 70, 100, 30, 40, 60, 70, 60, 80, 30, 50 }),
        // Rock: deep nods, hard hits, fists up
        new(4, 12, M(0, 1, 3, 4, 5, 6, 9, 10, 11, 14, 15), new[] { 3, 1, 1, 3, 1 }, new[] { 3, 1, 4, 2 }, new[] { 5, 3, 1 }, 60,
            0.12f, 0.17f, 0.06f, 0.12f, 0.10f, 0.28f,
            new[] { 0.8f, 1.1f, 0.9f, 0.8f, 1.2f, 0.9f, 0.8f, 1.6f, 0.8f, 1f }, new[] { 40, 100, 30, 20, 90, 40, 30, 95, 20, 50 }),
        // Electronic: snaps and holds, arms high, quick feet
        new(4, 10, M(0, 2, 3, 4, 5, 6, 7, 8, 11, 14, 15), new[] { 2, 3, 2, 3, 2 }, new[] { 2, 3, 5, 1 }, new[] { 5, 3, 1 }, 70,
            0.10f, 0.15f, 0.03f, 0.08f, 0.03f, 0.12f,
            new[] { 0.9f, 1f, 0.9f, 1f, 0.9f, 1f, 1.3f, 1f, 1.3f, 1f }, new[] { 50, 100, 30, 40, 50, 60, 85, 70, 70, 60 }),
        // HipHop: low and wide, the hips and shoulders lead
        new(6, 14, M(0, 1, 2, 3, 7, 8, 9, 10, 12, 13, 14, 15), new[] { 2, 4, 2, 2, 1 }, new[] { 3, 4, 3, 2 }, new[] { 2, 3, 3 }, 50,
            0.15f, 0.22f, 0.08f, 0.14f, 0.06f, 0.18f,
            new[] { 1.3f, 1.2f, 1f, 1.3f, 1f, 1.2f, 1f, 1f, 0.8f, 1.2f }, new[] { 85, 100, 30, 70, 60, 80, 50, 70, 30, 70 }),
        // Chill: slow sines, big gentle arcs
        new(16, 12, M(0, 1, 2, 3, 4, 7, 8, 9, 10, 12, 13), new[] { 5, 3, 2, 1, 0 }, new[] { 8, 1, 0, 1 }, new[] { 0, 1, 5 }, 15,
            0.10f, 0.15f, 0.02f, 0.06f, 0.02f, 0.08f,
            new[] { 0.8f, 0.7f, 0.8f, 0.8f, 0.8f, 0.9f, 0.9f, 0.8f, 0.9f, 0.8f }, new[] { 80, 90, 40, 50, 60, 80, 70, 60, 50, 50 }),
        // Folk: claps, hands on hips, skips
        new(10, 12, M(0, 1, 2, 3, 4, 5, 9, 10, 12, 15), new[] { 2, 3, 1, 3, 3 }, new[] { 4, 3, 2, 2 }, new[] { 3, 3, 2 }, 50,
            0.10f, 0.14f, 0.03f, 0.07f, 0.02f, 0.08f,
            new[] { 1f, 1f, 1.1f, 0.9f, 0.8f, 1f, 1f, 0.9f, 0.7f, 0.9f }, new[] { 60, 100, 40, 40, 40, 70, 60, 70, 20, 40 }),
        // RatDance: its own four moves, never generated
        new(0, 0, 0, new[] { 1, 0, 0, 0, 0 }, new[] { 1, 0, 0, 0 }, new[] { 1, 0, 0 }, 100,
            0.1f, 0.1f, 0f, 0f, 0f, 0f, new float[10], new int[10]),
    };

    private static readonly float[] Unit = { 0.04f, 0.035f, 0.04f, 0.015f, 0.10f, 0.08f, 0.24f, 0.14f, 0.16f, 0.03f };
    private static readonly float[] EnergyAmp = { 0.65f, 1f, 1.4f };
    private static readonly int[][] PeriodWeights = { new[] { 0, 2, 5, 3 }, new[] { 1, 4, 3, 1 }, new[] { 3, 4, 2, 0 } };

    // ---- the pools --------------------------------------------------------------------

    private static readonly GenMove[]?[] Pools = new GenMove[Styles][];
    private static readonly byte[]?[] Energies = new byte[Styles][];

    /// <summary>Moves a style has: <see cref="PoolSize"/>, none for the rat dance.</summary>
    public static int Count(int style) => (uint)style < Styles && Profiles[style].Mask != 0 ? PoolSize : 0;

    /// <summary>The style's whole pool, built on first use.</summary>
    public static GenMove[] Pool(int style)
    {
        if ((uint)style >= Styles || Count(style) == 0) return Array.Empty<GenMove>();
        return Pools[style] ??= Build(style);
    }

    /// <summary>The move <paramref name="index"/> (wrapped) of a style's pool, or null when it has none.</summary>
    public static GenMove? Get(int style, int index)
    {
        var p = Pool(style);
        return p.Length == 0 ? null : p[((index % p.Length) + p.Length) % p.Length];
    }

    /// <summary>The energy class of each move, 0 calm, 1 middle, 2 big (<c>DancePick</c> grades table moves the same way).</summary>
    public static ReadOnlySpan<byte> Energy(int style)
    {
        if ((uint)style >= Styles || Count(style) == 0) return ReadOnlySpan<byte>.Empty;
        return Energies[style] ??= Array.ConvertAll(Pool(style), m => m.Energy);
    }

    public static string Name(int style, int index) =>
        StyleNames[(uint)style < Styles ? style : 0] + "Gen" + (index % PoolSize).ToString("00", CultureInfo.InvariantCulture);

    private static GenMove[] Build(int style)
    {
        var pool = new GenMove[PoolSize];
        var seen = new System.Collections.Generic.HashSet<uint>();
        for (int i = 0; i < PoolSize; i++)
        {
            GenMove? best = null;
            for (int salt = 0; salt < 96; salt++)
            {
                var g = Make(style, i, salt);
                best = g;
                if (Check(g) == null && seen.Add(g.Signature)) break;
            }
            pool[i] = best!;
        }
        return pool;
    }

    // ---- hashing ----------------------------------------------------------------------

    private static uint Fnv(uint a, uint b)
    {
        uint h = 2166136261u;
        for (int i = 0; i < 4; i++) { h ^= (a >> (i * 8)) & 0xFFu; h *= 16777619u; }
        for (int i = 0; i < 4; i++) { h ^= (b >> (i * 8)) & 0xFFu; h *= 16777619u; }
        h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12;
        return h;
    }

    private struct Rng
    {
        private readonly uint _seed;
        private uint _n;
        public Rng(uint seed) { _seed = seed; _n = 0; }
        public uint Next() => Fnv(_seed, _n++);
        public int Below(int n) => (int)((Next() >> 4) % (uint)n);
        /// <summary>0..1 in thousandths.</summary>
        public float Unit() => (Next() >> 4) % 1000u / 1000f;
        public float Range(float lo, float hi) => lo + (hi - lo) * Unit();
        public int Weighted(int[] w)
        {
            int sum = 0;
            for (int i = 0; i < w.Length; i++) sum += w[i];
            int k = Below(Math.Max(sum, 1));
            for (int i = 0; i < w.Length; i++) { if (k < w[i]) return i; k -= w[i]; }
            return 0;
        }
    }

    // ---- generation -------------------------------------------------------------------

    private static byte EnergyOfIndex(Prof p, int index) => (byte)(index < p.Calm ? 0 : index < p.Calm + p.Mid ? 1 : 2);

    private static GenMove Make(int style, int index, int salt)
    {
        var p = Profiles[style];
        var r = new Rng(Fnv((uint)(style * 977 + index), (uint)salt * 7919u + 12345u));
        var g = new GenMove { Style = (byte)style, Index = (byte)index, Energy = EnergyOfIndex(p, index) };
        // later salts shrink everything, so a stubborn index always converges on a move that validates
        float shrink = Math.Max(0.35f, 1f - 0.012f * salt);
        float e = EnergyAmp[g.Energy] * shrink;
        var pw = PeriodWeights[g.Energy];

        Body(ref g.Px, 0, false);
        Body(ref g.Py, 1, false);
        Body(ref g.Pz, 2, false);
        Body(ref g.Tilt, 3, true);
        Body(ref g.Theta, 4, false);
        Body(ref g.Phi, 5, true);
        Body(ref g.Psi, 6, true);
        Body(ref g.ThN, 7, false);
        Body(ref g.PhH, 8, true);
        Body(ref g.Sh, 9, false);
        g.ShAlt = r.Below(2) == 0;
        // knees: a squat under the whole move, a lean forward; the dip itself rides on Py
        g.Py.Base = -r.Range(p.SquatLo, p.SquatHi) * Math.Min(1.15f, 0.8f + 0.2f * g.Energy);
        g.Theta.Base = r.Range(p.LeanLo, p.LeanHi);
        if (g.Py.Amp > 0f) { g.Py.Shape = 0; g.Py.Phase = (byte)(r.Below(10) < 6 ? 6 : r.Below(4) * 2); }

        // arms
        var allowed = new int[16];
        int n = 0;
        for (int a = 0; a < 16; a++)
            if ((p.Mask >> a & 1) != 0 && Anchor[a, 6] <= (g.Energy == 0 ? 1 : 2)) allowed[n++] = a;
        g.ArmMode = (byte)r.Below(3);
        g.KeyBeats = (byte)(r.Below(100) < p.OneBeatKeys ? 1 : 2);
        g.Snap = new[] { 0.25f, 0.5f, 1f }[r.Weighted(p.Snaps)];
        for (int k = 0; k < 4; k++)
        {
            g.KeysA[k] = (byte)allowed[r.Below(n)];
            g.KeysB[k] = (byte)allowed[r.Below(n)];
            // a key identical to the one before it would just be a hold; keep the cycle moving
            if (k > 0 && g.KeysA[k] == g.KeysA[k - 1]) g.KeysA[k] = (byte)allowed[(Array.IndexOf(allowed, g.KeysA[k]) + 1) % n];
            if (k > 0 && g.KeysB[k] == g.KeysB[k - 1]) g.KeysB[k] = (byte)allowed[(Array.IndexOf(allowed, g.KeysB[k]) + 1) % n];
        }

        // legs
        g.LegKind = (byte)r.Weighted(p.Legs);
        g.LegRate = r.Below(2) == 0 ? 0.5f : 1f;
        g.LegAmp = new[] { 0.6f, 0.9f, 1.2f }[g.Energy] * r.Range(0.8f, 1.1f) * shrink;
        g.Stance = r.Range(p.StanceLo, p.StanceHi);
        g.Turn = r.Range(0.05f, 0.30f);
        g.KMov = r.Range(0.4f, 0.7f);
        g.ArmBlend = r.Range(0.6f, 1f);
        g.Signature = Sign(g);
        return g;

        void Body(ref GenChan c, int ch, bool slow)
        {
            // draws are made whether or not the channel is active, so a style's tables can change one channel without moving the rest
            bool active = r.Below(100) < p.Prob[ch];
            int period = Math.Min(3, r.Weighted(pw) + (slow && r.Below(2) == 0 ? 1 : 0));
            byte phase = (byte)r.Below(8);
            byte shape = (byte)r.Weighted(p.Shapes);
            float amp = Unit[ch] * p.Gain[ch] * e * r.Range(0.6f, 1.4f);
            c = new GenChan { Period = (byte)period, Phase = phase, Shape = shape, Amp = active ? amp : 0f };
        }
    }

    private static uint Sign(GenMove g)
    {
        uint h = 2166136261u;
        void Mix(uint v) { h = (h ^ v) * 16777619u; h ^= h >> 13; }
        void Chan(in GenChan c) { Mix(c.Period); Mix(c.Phase); Mix(c.Shape); Mix((uint)BitConverter.SingleToInt32Bits(c.Amp)); Mix((uint)BitConverter.SingleToInt32Bits(c.Base)); }
        Chan(g.Px); Chan(g.Py); Chan(g.Pz); Chan(g.Tilt); Chan(g.Theta); Chan(g.Phi); Chan(g.Psi); Chan(g.ThN); Chan(g.PhH); Chan(g.Sh);
        Mix(g.ShAlt ? 1u : 0u); Mix(g.ArmMode); Mix(g.KeyBeats); Mix(g.LegKind);
        for (int k = 0; k < 4; k++) { Mix(g.KeysA[k]); Mix(g.KeysB[k]); }
        foreach (float f in new[] { g.Snap, g.LegRate, g.LegAmp, g.Stance, g.Turn, g.KMov, g.ArmBlend })
            Mix((uint)BitConverter.SingleToInt32Bits(f));
        return h;
    }

    // ---- evaluation (every frame: no allocation) --------------------------------------

    private static float Sm(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Wave(in GenChan c, float beats)
    {
        if (c.Amp == 0f) return c.Base;
        float turns = beats / PeriodBeats[c.Period] + c.Phase * 0.125f;
        float f = turns - MathF.Floor(turns);
        float v;
        switch (c.Shape)
        {
            case 1: v = Math.Clamp(3f * MathF.Sin(MathF.Tau * f), -1f, 1f); break;
            case 2: v = f < 0.1f ? Lerp(-1f, 1f, Sm(f / 0.1f)) : 2f * MathF.Exp(-6f * (f - 0.1f) / 0.9f) - 1f; break;
            case 3: v = f < 0.75f ? Lerp(1f, -1f, Sm(f / 0.75f)) : Lerp(-1f, 1f, Sm((f - 0.75f) / 0.25f)); break;
            default: v = MathF.Sin(MathF.Tau * f); break;
        }
        return c.Base + c.Amp * v;
    }

    private static void ArmAt(GenMove g, byte[] keys, float pos, float s, out GenArm a)
    {
        float fl = MathF.Floor(pos);
        int k = (int)fl & 3;
        float u = Sm((pos - fl) / g.Snap);
        int from = keys[(k + 3) & 3], to = keys[k];
        a.X = s * Lerp(Anchor[from, 0], Anchor[to, 0], u);
        a.Y = Lerp(Anchor[from, 1], Anchor[to, 1], u);
        a.Z = Lerp(Anchor[from, 2], Anchor[to, 2], u);
        a.HX = s * Lerp(Anchor[from, 3], Anchor[to, 3], u);
        a.HY = Lerp(Anchor[from, 4], Anchor[to, 4], u);
        a.HZ = Lerp(Anchor[from, 5], Anchor[to, 5], u);
    }

    private static void FootAt(GenMove g, float beats, float s, out GenFoot f)
    {
        float a = beats * g.LegRate + (s < 0f ? 0f : 0.5f);
        float bump = MathF.Max(0f, MathF.Sin(MathF.Tau * (a - MathF.Floor(a))));
        f = new GenFoot { X = s * g.Stance, Y = 0f, Z = 0f, Turn = g.Turn, Lift = 0f, Heel = 0f };
        switch (g.LegKind)
        {
            case 1: f.X = s * (g.Stance + 0.14f * g.LegAmp * bump); f.Y = 0.015f * bump; break;
            case 2: f.Heel = Math.Min(0.07f, 0.05f * g.LegAmp * bump); break;
            case 3: f.Y = 0.13f * g.LegAmp * bump; f.Z = 0.06f * bump; f.Lift = bump; break;
            case 4: f.Z = 0.25f * g.LegAmp * bump; f.Y = 0.09f * g.LegAmp * bump; f.Lift = bump; break;
        }
    }

    /// <summary>The move's channels at <paramref name="beats"/> into its two-bar loop (0..8).</summary>
    public static void Sample(GenMove g, float beats, ref GenFrame f)
    {
        f.Px = Wave(g.Px, beats); f.Py = Wave(g.Py, beats); f.Pz = Wave(g.Pz, beats); f.Tilt = Wave(g.Tilt, beats);
        f.Theta = Wave(g.Theta, beats); f.Phi = Wave(g.Phi, beats); f.Psi = Wave(g.Psi, beats);
        f.ThN = Wave(g.ThN, beats); f.PhH = Wave(g.PhH, beats);
        f.ShL = Wave(g.Sh, beats);
        if (g.ShAlt)
        {
            var other = g.Sh;
            other.Phase = (byte)((other.Phase + 4) & 7);
            f.ShR = Wave(other, beats);
        }
        else f.ShR = f.ShL;
        float pos = beats / g.KeyBeats;
        ArmAt(g, g.KeysA, pos, -1f, out f.ArmL);
        switch (g.ArmMode)
        {
            case 1: ArmAt(g, g.KeysA, pos + 2f, 1f, out f.ArmR); break;
            case 2: ArmAt(g, g.KeysB, pos, 1f, out f.ArmR); break;
            default: ArmAt(g, g.KeysA, pos, 1f, out f.ArmR); break;
        }
        FootAt(g, beats, -1f, out f.FootL);
        FootAt(g, beats, 1f, out f.FootR);
    }

    // ---- the validator ----------------------------------------------------------------

    /// <summary>The music layer's biggest gain on the channels (<c>Feel</c>: level 1, a chorus), assumed on top of the move.</summary>
    private const float Dyn = 1.12f;

    /// <summary>
    /// Null when the move is fine, else why not: sampled every eighth beat of its two bars, standing,
    /// with the music layer at its loudest. A wrist must be within 0.50 m of its shoulder, 0.21 m from
    /// the torso's axis (hip to neck), 0.15 m from the head's, the two wrists 0.085 m apart, the legs
    /// must reach their feet without the hip being pulled down, the centre of mass must stay over the
    /// grounded feet, and the whole thing must move. The exact rig runs the same limits in
    /// <c>--emotecheck</c>; the margins here are a few millimetres so a pass here passes there.
    /// </summary>
    public static string? Check(GenMove g)
    {
        var f = default(GenFrame);
        float minPy = 9f, maxPy = -9f, minPsi = 9f, maxPsi = -9f, minPhi = 9f, maxPhi = -9f, minPx = 9f, maxPx = -9f;
        float minFx = 9f, maxFx = -9f, maxLift = 0f;
        var loL = new float[3]; var hiL = new float[3]; var loR = new float[3]; var hiR = new float[3];
        for (int c = 0; c < 3; c++) { loL[c] = loR[c] = 9f; hiL[c] = hiR[c] = -9f; }
        const int Samples = 64;
        for (int i = 0; i < Samples; i++)
        {
            float beats = i * LoopBeats / Samples;
            Sample(g, beats, ref f);
            float px = f.Px * Dyn, py = f.Py * Dyn, pz = f.Pz * Dyn, tilt = f.Tilt * Dyn;
            float theta = f.Theta * Dyn, phi = f.Phi * Dyn, psi = f.Psi * Dyn, thN = f.ThN * Dyn;
            float hipY = 0.935f + py;
            if (hipY < 0.935f - 0.17f) return $"squat too deep at {beats}";

            // torso frame, as HumanMeshBuilder.Spine (level 2)
            Frame(theta, phi, psi, out var m);
            var hip = new V(px, hipY - 0.036f, pz);
            var neck = hip.Add(m.Rot(0f, 0.59f, 0f));
            float thH = 0.35f * theta + thN;
            float phH = f.PhH * Dyn;
            float uy = MathF.Cos(phH) * MathF.Cos(thH), uz = MathF.Cos(phH) * MathF.Sin(thH);
            float headTopY = neck.Y + uy * 0.255f;
            V wl = default, wr = default;
            for (int side = 0; side < 2; side++)
            {
                var arm = side == 0 ? f.ArmL : f.ArmR;
                float s = side == 0 ? -1f : 1f;
                float len = MathF.Sqrt(arm.X * arm.X + arm.Y * arm.Y + arm.Z * arm.Z);
                if (len > 0.50f) return $"arm reach {len:F2} at {beats}";
                var sh = hip.Add(m.Rot(s * 0.18f, 0.51f, 0f));
                sh.Y += side == 0 ? f.ShL : f.ShR;
                var w = sh.Add(m.Rot(arm.X, arm.Y, arm.Z));
                if (w.Y > hip.Y - 0.10f && w.Y < neck.Y + 0.05f)
                {
                    float d = Segment(w, hip, neck);
                    if (d < 0.215f) return $"hand in the torso ({d:F2}) at {beats}";
                }
                if (w.Y < headTopY - 0.01f && MathF.Sqrt(w.X * w.X + w.Z * w.Z) < 0.155f)
                    return $"hand in the head at {beats}";
                if (side == 0) wl = w; else wr = w;
            }
            if (wl.Sub(wr).Len() < 0.09f) return $"hands together ({wl.Sub(wr).Len():F2}) at {beats}";
            Track(loL, hiL, wl.Sub(hip)); Track(loR, hiR, wr.Sub(hip));

            // legs: the hip must not need pulling down to reach the feet (LegFit), and the legs must not be stretched
            float gx0 = 9f, gx1 = -9f, gz0 = 9f, gz1 = -9f;
            for (int side = 0; side < 2; side++)
            {
                var ft = side == 0 ? f.FootL : f.FootR;
                float s = side == 0 ? -1f : 1f;
                float ay = AnkleHeight + ft.Y + ft.Heel;
                float dx = ft.X - (px + s * 0.09f), dz = ft.Z - pz;
                float r2 = 0.845f * 0.845f - dx * dx - dz * dz;
                if (r2 < 0.40f) return $"foot out of reach at {beats}";
                if (hipY > ay - s * tilt + MathF.Sqrt(r2) + 0.002f) return $"legs too short at {beats}";
                minFx = Math.Min(minFx, ft.X); maxFx = Math.Max(maxFx, ft.X); maxLift = Math.Max(maxLift, ft.Y + ft.Heel);
                if (ft.Y < 0.03f)
                {
                    gx0 = Math.Min(gx0, ft.X); gx1 = Math.Max(gx1, ft.X);
                    gz0 = Math.Min(gz0, ft.Z); gz1 = Math.Max(gz1, ft.Z);
                }
            }
            if (gx0 > gx1) return $"no foot on the ground at {beats}";
            float comX = px + 0.25f * MathF.Sin(phi), comZ = pz + 0.30f * MathF.Sin(theta);
            if (comX < gx0 - 0.08f || comX > gx1 + 0.08f) return $"off balance sideways at {beats}";
            if (comZ < gz0 - 0.10f || comZ > gz1 + 0.16f) return $"off balance forward at {beats}";

            minPy = Math.Min(minPy, py); maxPy = Math.Max(maxPy, py);
            minPsi = Math.Min(minPsi, psi); maxPsi = Math.Max(maxPsi, psi);
            minPhi = Math.Min(minPhi, phi); maxPhi = Math.Max(maxPhi, phi);
            minPx = Math.Min(minPx, px); maxPx = Math.Max(maxPx, px);
        }
        float arms = Diagonal(loL, hiL) + Diagonal(loR, hiR);
        float body = 4f * (maxPy - minPy) + 0.5f * (maxPsi - minPsi) + (maxPhi - minPhi) + 2f * (maxPx - minPx)
            + (maxFx - minFx - 2f * g.Stance) + 2f * maxLift;
        if (arms + body < 0.5f) return $"hardly moves ({arms + body:F2})";
        return null;
    }

    private struct V
    {
        public float X, Y, Z;
        public V(float x, float y, float z) { X = x; Y = y; Z = z; }
        public readonly V Add(V o) => new(X + o.X, Y + o.Y, Z + o.Z);
        public readonly V Sub(V o) => new(X - o.X, Y - o.Y, Z - o.Z);
        public readonly float Len() => MathF.Sqrt(X * X + Y * Y + Z * Z);
    }

    /// <summary>The torso rotation of <c>HumanMeshBuilder.Spine</c> at level 2: pitch, roll, then yaw.</summary>
    private readonly struct Frame2
    {
        private readonly float _ct, _st, _cp, _sp, _cy, _sy;
        public Frame2(float theta, float phi, float psi)
        {
            _ct = MathF.Cos(theta); _st = MathF.Sin(theta);
            _cp = MathF.Cos(phi); _sp = MathF.Sin(phi);
            _cy = MathF.Cos(psi); _sy = MathF.Sin(psi);
        }
        public V Rot(float x, float y, float z)
        {
            float y1 = y * _ct - z * _st, z1 = y * _st + z * _ct;
            float x2 = x * _cp + y1 * _sp, y2 = -x * _sp + y1 * _cp;
            return new V(x2 * _cy + z1 * _sy, y2, -x2 * _sy + z1 * _cy);
        }
    }

    private static void Frame(float theta, float phi, float psi, out Frame2 m) => m = new Frame2(theta, phi, psi);

    private static float Segment(V p, V a, V b)
    {
        var ab = b.Sub(a); var ap = p.Sub(a);
        float u = Math.Clamp((ap.X * ab.X + ap.Y * ab.Y + ap.Z * ab.Z) / Math.Max(ab.X * ab.X + ab.Y * ab.Y + ab.Z * ab.Z, 1e-9f), 0f, 1f);
        return p.Sub(new V(a.X + ab.X * u, a.Y + ab.Y * u, a.Z + ab.Z * u)).Len();
    }

    private static void Track(float[] lo, float[] hi, V p)
    {
        lo[0] = Math.Min(lo[0], p.X); hi[0] = Math.Max(hi[0], p.X);
        lo[1] = Math.Min(lo[1], p.Y); hi[1] = Math.Max(hi[1], p.Y);
        lo[2] = Math.Min(lo[2], p.Z); hi[2] = Math.Max(hi[2], p.Z);
    }

    private static float Diagonal(float[] lo, float[] hi)
    {
        float x = hi[0] - lo[0], y = hi[1] - lo[1], z = hi[2] - lo[2];
        return MathF.Sqrt(x * x + y * y + z * z);
    }
}
