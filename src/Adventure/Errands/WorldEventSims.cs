using System;
using System.Collections.Generic;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/world-events.md).

/// <summary>A point a helper stands at (LV95 metres).</summary>
public readonly record struct Spot(double E, double N);

/// <summary>
/// The runaway cow (#1339): it broke out and walks away from its herd on a bearing at 1.1 m/s. Helpers within 7 m push it: it
/// walks away from their centre at 1.4 m/s. It is back when it stands within 15 m of its herd's centre.
/// </summary>
public sealed class CowSim
{
    public const float LooseMps = 1.1f, PushedMps = 1.4f, PushM = 7, BackM = 15, MaxStrayM = 250;

    public double E, N;
    public readonly double HomeE, HomeN;
    public float Yaw;
    private readonly double _outE, _outN;

    public CowSim(double homeE, double homeN, double bearingU, double startM = 45)
    {
        HomeE = homeE; HomeN = homeN;
        (_outE, _outN) = (Math.Sin(bearingU * Math.Tau), Math.Cos(bearingU * Math.Tau));
        E = homeE + _outE * startM;
        N = homeN + _outN * startM;
        Yaw = (float)Math.Atan2(-_outE, -_outN);
    }

    public double HomeDistance => Math.Sqrt((E - HomeE) * (E - HomeE) + (N - HomeN) * (N - HomeN));

    public bool IsHome => HomeDistance <= BackM;

    /// <summary>One step; true when the cow is pushed by a helper this step.</summary>
    public bool Step(double dt, IReadOnlyList<Spot> helpers)
    {
        double se = 0, sn = 0;
        int count = 0;
        foreach (var h in helpers)
            if ((h.E - E) * (h.E - E) + (h.N - N) * (h.N - N) <= PushM * PushM) { se += h.E; sn += h.N; count++; }
        double dx, dn, speed;
        bool pushed = count > 0;
        if (pushed)
        {
            dx = E - se / count; dn = N - sn / count; speed = PushedMps;
        }
        else if (HomeDistance >= MaxStrayM) return false;
        else
        {
            dx = _outE; dn = _outN; speed = LooseMps;
        }
        double len = Math.Sqrt(dx * dx + dn * dn);
        if (len < 1e-6) return pushed;
        dx /= len; dn /= len;
        E += dx * speed * dt;
        N += dn * speed * dt;
        Yaw = (float)Math.Atan2(-dx, -dn);
        return pushed;
    }

    /// <summary>The world solves it: the cow walks home by itself (no reward).</summary>
    public void WalkHome(double dt)
    {
        double dx = HomeE - E, dn = HomeN - N, len = Math.Sqrt(dx * dx + dn * dn);
        if (len < 1e-6) return;
        E += dx / len * LooseMps * dt; N += dn / len * LooseMps * dt;
        Yaw = (float)Math.Atan2(-dx, -dn);
    }
}

/// <summary>
/// The lost hiker (#1339): found by talking, then follows the nearest helper within 40 m at 1.4 m/s, a few metres behind, to the
/// goal (a hut, a stop or a station). Done within 25 m of the goal.
/// </summary>
public sealed class HikerSim
{
    public const float WalkMps = 1.4f, FollowM = 40, BehindM = 3, GoalM = 25;

    public double E, N;
    public readonly double GoalE, GoalN;
    public bool Found;
    public float Yaw;
    public bool Walking { get; private set; }

    public HikerSim(double e, double n, double goalE, double goalN) { E = e; N = n; GoalE = goalE; GoalN = goalN; }

    public bool AtGoal => (GoalE - E) * (GoalE - E) + (GoalN - N) * (GoalN - N) <= GoalM * GoalM;

    public void Step(double dt, IReadOnlyList<Spot> helpers)
    {
        Walking = false;
        if (!Found || AtGoal) return;
        double best = FollowM * FollowM;
        Spot? lead = null;
        foreach (var h in helpers)
        {
            double d = (h.E - E) * (h.E - E) + (h.N - N) * (h.N - N);
            if (d < best) { best = d; lead = h; }
        }
        if (lead is not { } l) return;
        double dist = Math.Sqrt(best);
        if (dist <= BehindM) return;
        double dx = (l.E - E) / dist, dn = (l.N - N) / dist;
        double move = Math.Min(WalkMps * dt, dist - BehindM);
        E += dx * move; N += dn * move;
        Yaw = (float)Math.Atan2(-dx, -dn);
        Walking = true;
    }
}

/// <summary>
/// The avalanche rescue (#1339): one or two buried under a debris cone. Digging takes 8 s for one digger, 8 / n s for up to
/// three. Within 60 m of the cone a transceiver beeps, quicker the nearer (<see cref="BeepHz"/>).
/// </summary>
public sealed class AvalancheSim
{
    public const float DigS = 8, ConeM = 14, DetectM = 60;
    public const int MaxDiggers = 3;

    public readonly int Buried;
    public readonly Spot[] Spots;
    public readonly float[] Progress;

    public AvalancheSim(double e, double n, uint seed)
    {
        Buried = 1 + (int)(seed & 1);
        Spots = new Spot[Buried];
        Progress = new float[Buried];
        for (int i = 0; i < Buried; i++)
        {
            double u = ErrandHash.Unit(ErrandHash.Of(seed, i, 0xA7)), r = 3 + ErrandHash.Unit(ErrandHash.Of(seed, i, 0xA8)) * (ConeM - 6);
            var (se, sn) = WorldEventRules.Offset(e, n, u, r);
            Spots[i] = new Spot(se, sn);
        }
    }

    public bool Dug(int i) => Progress[i] >= 1f;

    public int DugCount { get { int c = 0; for (int i = 0; i < Buried; i++) if (Dug(i)) c++; return c; } }

    public bool Done => DugCount == Buried;

    /// <summary>The buried one nearest a point and how far, within <paramref name="within"/> m; -1 when none.</summary>
    public int Nearest(double e, double n, double within, out double dist)
    {
        int best = -1;
        dist = within;
        for (int i = 0; i < Buried; i++)
        {
            if (Dug(i)) continue;
            double d = Math.Sqrt((Spots[i].E - e) * (Spots[i].E - e) + (Spots[i].N - n) * (Spots[i].N - n));
            if (d <= dist) { dist = d; best = i; }
        }
        return best;
    }

    /// <summary>Adds <paramref name="dt"/> seconds of digging by <paramref name="diggers"/> people at a buried one.</summary>
    public void Dig(int i, int diggers, double dt)
    {
        if (i < 0 || i >= Buried || diggers <= 0 || Dug(i)) return;
        Progress[i] = Math.Min(1f, Progress[i] + (float)(dt * Math.Min(diggers, MaxDiggers) / DigS));
    }

    /// <summary>The transceiver's beeps a second at a distance: 1 Hz at the edge, 4 Hz on top of it; 0 beyond the detector's range.</summary>
    public static float BeepHz(double dist) => dist > DetectM ? 0f : 1f + 3f * (float)(1 - dist / DetectM);
}

/// <summary>
/// The small forest fire (#1339): six burning 4 m cells in a forest edge; every 15 s one spreads to a forest neighbour, up to 40.
/// Beaten out (2 s of beating a cell). Out: success. 40 burning: the fire brigade takes over. Cells are (i, j) steps of
/// <see cref="CellM"/> from the centre, packed in <see cref="Pack"/>.
/// </summary>
public sealed class FireSim
{
    public const float CellM = 4, BeatS = 2, SpreadS = 15;
    public const int Start = 6, Max = 40, Reach = 60;

    public readonly double E, N;
    private readonly uint _seed;
    private readonly SortedSet<int> _burning = new();
    private readonly HashSet<int> _out = new();
    private readonly Dictionary<int, float> _beat = new();
    private int _tick;
    private double _since;

    public FireSim(double e, double n, uint seed, Func<int, int, bool> forest)
    {
        E = e; N = n; _seed = seed;
        _burning.Add(Pack(0, 0));
        // the start: the centre and its nearest forest cells in a hash order
        var near = new List<(int I, int J)>();
        for (int i = -3; i <= 3; i++)
            for (int j = -3; j <= 3; j++)
                if ((i != 0 || j != 0) && forest(i, j)) near.Add((i, j));
        near.Sort((a, b) => ErrandHash.Of(seed, a.I, a.J).CompareTo(ErrandHash.Of(seed, b.I, b.J)));
        foreach (var (i, j) in near)
        {
            if (_burning.Count >= Start) break;
            _burning.Add(Pack(i, j));
        }
    }

    public static int Pack(int i, int j) => ((i + Reach) << 8) | (j + Reach);
    public static (int I, int J) Unpack(int key) => ((key >> 8) - Reach, (key & 0xFF) - Reach);

    public IReadOnlyCollection<int> Burning => _burning;
    public int Count => _burning.Count;
    public bool IsOut => _burning.Count == 0;
    public bool Overrun => _burning.Count >= Max;

    public (double E, double N) CellAt(int key)
    {
        var (i, j) = Unpack(key);
        return (E + i * CellM, N + j * CellM);
    }

    /// <summary>The burning cell nearest a point within <paramref name="within"/> m, or -1.</summary>
    public int Nearest(double e, double n, double within)
    {
        int best = -1;
        double bd = within * within;
        foreach (int key in _burning)
        {
            var (ce, cn) = CellAt(key);
            double d = (ce - e) * (ce - e) + (cn - n) * (cn - n);
            if (d <= bd) { bd = d; best = key; }
        }
        return best;
    }

    /// <summary>Time passes: every <see cref="SpreadS"/> s one cell spreads. True when the burning set changed.</summary>
    public bool Step(double dt, Func<int, int, bool> forest)
    {
        _since += dt;
        bool changed = false;
        while (_since >= SpreadS && !IsOut && !Overrun)
        {
            _since -= SpreadS;
            _tick++;
            changed |= Spread(forest);
        }
        return changed;
    }

    private bool Spread(Func<int, int, bool> forest)
    {
        // a burning cell, then its forest neighbours, both chosen by the seed and the tick: no shared random stream
        var cells = new List<int>(_burning);
        int from = cells[(int)(ErrandHash.Of(_seed, _tick, 1) % (ulong)cells.Count)];
        var (fi, fj) = Unpack(from);
        var around = new List<(int I, int J)> { (fi + 1, fj), (fi - 1, fj), (fi, fj + 1), (fi, fj - 1), (fi + 1, fj + 1), (fi - 1, fj - 1), (fi + 1, fj - 1), (fi - 1, fj + 1) };
        around.Sort((a, b) => ErrandHash.Of(_seed, _tick, a.I, a.J).CompareTo(ErrandHash.Of(_seed, _tick, b.I, b.J)));
        foreach (var (i, j) in around)
        {
            if (Math.Abs(i) >= Reach - 1 || Math.Abs(j) >= Reach - 1) continue;
            int key = Pack(i, j);
            if (_burning.Contains(key) || _out.Contains(key) || !forest(i, j)) continue;
            _burning.Add(key);
            return true;
        }
        return false;
    }

    /// <summary>Adds <paramref name="dt"/> seconds of beating by <paramref name="beaters"/> at a cell; true when it went out.</summary>
    public bool Beat(int key, int beaters, double dt)
    {
        if (!_burning.Contains(key) || beaters <= 0) return false;
        float b = _beat.GetValueOrDefault(key) + (float)(dt * beaters / BeatS);
        if (b < 1f) { _beat[key] = b; return false; }
        _beat.Remove(key);
        _burning.Remove(key);
        _out.Add(key);
        return true;
    }

    /// <summary>The cells as a flat int array for the wire.</summary>
    public int[] ToArray()
    {
        var a = new int[_burning.Count];
        _burning.CopyTo(a);
        return a;
    }
}
