using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Ropeways;

/// <summary>
/// What carries the rider (#912). swissTLM3D does not tell a gondola from an aerial tramway: both are
/// <see cref="Gondola"/> until OSM says <see cref="CableCar"/> (#915). <b>Append-only</b>.
/// </summary>
public enum RopewayKind : byte { Gondola, Chair, TBar, RopeTow, CableCar }

/// <summary>
/// One lift (#912): its cable in LV95 (east, north) with the surveyed cable altitude, from the
/// bottom station to the top, and how it runs: the kind's defaults, then what OSM says of it
/// (<see cref="Apply"/>, #915). Pure, so the joining and the motion are unit-tested.
/// </summary>
public sealed class RopewayLine
{
    public RopewayKind Kind { get; private set; }
    public double[] E { get; }
    public double[] N { get; }
    public float[] Alt { get; }
    /// <summary>Distance along the cable, flat, from the bottom station.</summary>
    public float[] Arc { get; }
    public float Length => Arc[^1];
    /// <summary>The bottom station rounded to a metre: the same on every peer, the line's name in play.</summary>
    public long Key { get; }
    /// <summary><see cref="Key"/> folded to an int, never 0: what a rider replicates.</summary>
    public int Id { get; }

    /// <summary>Cable speed, m/s.</summary>
    public float Speed { get; private set; }
    /// <summary>Between two carriers on a loop, m.</summary>
    public float Spacing { get; private set; }
    /// <summary>The carrier's floor or seat below the cable, m (0 for a drag lift: the rod reaches the snow).</summary>
    public float Hang { get; private set; }
    /// <summary>The return side's (or the second cabin's track's) offset to the left of the way up, m.</summary>
    public float Side { get; set; }
    /// <summary>Seats on a chair, places in a cabin.</summary>
    public int Seats { get; private set; }
    /// <summary>The name OSM gives it, or empty.</summary>
    public string Name { get; private set; } = "";

    /// <summary>A cable car: two cabins shuttling on their own tracks, not a loop of carriers.</summary>
    public bool Shuttle => Kind == RopewayKind.CableCar;

    public RopewayLine(RopewayKind kind, double[] e, double[] n, float[] alt)
    {
        // bottom first
        if (alt[^1] < alt[0])
        {
            Array.Reverse(e); Array.Reverse(n); Array.Reverse(alt);
        }
        E = e; N = n; Alt = alt;
        Arc = new float[e.Length];
        for (int i = 1; i < e.Length; i++)
            Arc[i] = Arc[i - 1] + (float)Math.Sqrt((e[i] - e[i - 1]) * (e[i] - e[i - 1]) + (n[i] - n[i - 1]) * (n[i] - n[i - 1]));
        Key = (long)Math.Round(e[0]) * 10_000_000L + (long)Math.Round(n[0]);
        Id = (int)(Key ^ (Key >> 31)) | 1;
        SetKind(kind);
        Side = DefaultSide(kind);
    }

    private void SetKind(RopewayKind kind)
    {
        Kind = kind;
        (Speed, Spacing, Hang, Seats) = kind switch
        {
            RopewayKind.Gondola => (5f, 45f, 3.2f, 8),
            RopewayKind.CableCar => (8f, 0f, 4.5f, 60),
            RopewayKind.Chair => (4.5f, 22f, 2.3f, 4),
            RopewayKind.TBar => (3f, 14f, 0f, 2),
            _ => (2f, 8f, 0f, 1),
        };
    }

    private static float DefaultSide(RopewayKind kind) => kind switch
    {
        RopewayKind.Gondola => 3f, RopewayKind.CableCar => 8f, RopewayKind.Chair => 2.5f, RopewayKind.TBar => 1.5f, _ => 1f,
    };

    /// <summary>
    /// What OSM says of this lift (#915): its true kind, its speed from the ride's duration (else a
    /// fixed-grip chair is slow), its seats and its name. A return side found by twin joining is kept.
    /// </summary>
    public void Apply(RopewayInfo info)
    {
        bool twin = Side != DefaultSide(Kind);
        SetKind(info.Type switch
        {
            "cable_car" => RopewayKind.CableCar,
            "gondola" or "mixed_lift" => RopewayKind.Gondola,
            "chair_lift" => RopewayKind.Chair,
            "drag_lift" or "t-bar" or "j-bar" or "platter" => RopewayKind.TBar,
            "rope_tow" or "magic_carpet" => RopewayKind.RopeTow,
            _ => Kind,
        });
        if (!twin) Side = DefaultSide(Kind);
        if (info.Minutes > 0) Speed = Math.Clamp(Length / (info.Minutes * 60f), 0.8f, 12f);
        else if (Kind == RopewayKind.Chair && info.Detachable == false) Speed = 2.3f;
        if (info.Seats > 0) Seats = info.Seats;
        Name = info.Name;
    }

    /// <summary>The cable at <paramref name="s"/> m from the bottom (clamped), and its flat direction up the line (unit east, north).</summary>
    public (double E, double N, float Alt, float DirE, float DirN) At(float s)
    {
        s = Math.Clamp(s, 0f, Length);
        int i = Array.BinarySearch(Arc, s);
        if (i < 0) i = ~i;
        i = Math.Clamp(i, 1, Arc.Length - 1);
        float seg = Arc[i] - Arc[i - 1];
        float t = seg < 1e-4f ? 0f : (s - Arc[i - 1]) / seg;
        double de = E[i] - E[i - 1], dn = N[i] - N[i - 1];
        float len = (float)Math.Sqrt(de * de + dn * dn);
        return (E[i - 1] + de * t, N[i - 1] + dn * t, Alt[i - 1] + (Alt[i] - Alt[i - 1]) * t,
            len < 1e-4f ? 0f : (float)(de / len), len < 1e-4f ? 1f : (float)(dn / len));
    }
}

/// <summary>Joins the per-tile pieces of the ropeways into whole lines (#912), one line per lift (#915).</summary>
public static class RopewayJoin
{
    /// <summary>One clipped piece: (east, north, altitude) triplets in LV95.</summary>
    public readonly record struct Piece(RopewayKind Kind, double[] Points);

    private static (long, long) Snap(double e, double n) => ((long)Math.Round(e * 2), (long)Math.Round(n * 2));

    /// <summary>
    /// Chains pieces that share an end (to 0.5 m, as a tile seam leaves them) into lines, each once,
    /// bottom station first. Lines shorter than <paramref name="minLength"/> are dropped (a scrap at a
    /// tile corner). Twins, the two cables of one lift that swissTLM3D draws side by side, become one
    /// (<see cref="MergeTwins"/>). <paramref name="info"/>, when given, is applied to each line.
    /// </summary>
    public static List<RopewayLine> Join(IReadOnlyList<Piece> pieces, float minLength = 30f, RopewayInfoIndex? info = null)
    {
        var ends = new Dictionary<(long, long), List<int>>();
        void AddEnd(double e, double n, int piece)
        {
            var k = Snap(e, n);
            if (!ends.TryGetValue(k, out var list)) ends[k] = list = new List<int>();
            list.Add(piece);
        }
        for (int p = 0; p < pieces.Count; p++)
        {
            var pts = pieces[p].Points;
            if (pts.Length < 6) continue;
            AddEnd(pts[0], pts[1], p);
            AddEnd(pts[^3], pts[^2], p);
        }

        var used = new bool[pieces.Count];
        var lines = new List<RopewayLine>();
        // start from an end no other piece shares: the station, not a seam; then any left (a loop)
        for (int pass = 0; pass < 2; pass++)
            for (int p = 0; p < pieces.Count; p++)
            {
                if (used[p] || pieces[p].Points.Length < 6) continue;
                var pts = pieces[p].Points;
                bool startFree = ends[Snap(pts[0], pts[1])].Count == 1, endFree = ends[Snap(pts[^3], pts[^2])].Count == 1;
                if (pass == 0 && !startFree && !endFree) continue;
                var e = new List<double>();
                var n = new List<double>();
                var a = new List<float>();
                int cur = p;
                bool forward = pass == 1 || startFree;
                while (true)
                {
                    used[cur] = true;
                    var q = pieces[cur].Points;
                    int count = q.Length / 3;
                    for (int k = 0; k < count; k++)
                    {
                        int i = forward ? k : count - 1 - k;
                        if (e.Count > 0 && k == 0) continue;   // the shared end, once
                        e.Add(q[i * 3]); n.Add(q[i * 3 + 1]); a.Add((float)q[i * 3 + 2]);
                    }
                    var tail = Snap(e[^1], n[^1]);
                    int next = -1;
                    foreach (int o in ends[tail])
                        if (!used[o] && pieces[o].Kind == pieces[p].Kind) { next = o; break; }
                    if (next < 0) break;
                    var np = pieces[next].Points;
                    forward = Snap(np[0], np[1]) == tail;
                    cur = next;
                }
                var line = new RopewayLine(pieces[p].Kind, e.ToArray(), n.ToArray(), a.ToArray());
                if (line.Length >= minLength) lines.Add(line);
            }
        // which OSM lift each line is: two lines that are two different lifts are never twins
        var matched = new Dictionary<RopewayLine, RopewayInfo?>();
        foreach (var line in lines) matched[line] = info?.Match(line.E[0], line.N[0], line.E[^1], line.N[^1]);
        lines = MergeTwins(lines, sameLift: (a, b) => matched[a] == matched[b]);
        foreach (var line in lines)
            if (matched[line] is { } found) line.Apply(found);
        return lines;
    }

    /// <summary>
    /// Two lines of one kind whose bottom stations and top stations both lie within
    /// <paramref name="reach"/> m of each other are one lift drawn as its two cables: the first is
    /// kept, the gap between them its return side. Unless <paramref name="sameLift"/> says they are
    /// two lifts: OSM names Médran 2 and Médran 4, two gondolas side by side above Verbier.
    /// </summary>
    public static List<RopewayLine> MergeTwins(List<RopewayLine> lines, double reach = 40,
        Func<RopewayLine, RopewayLine, bool>? sameLift = null)
    {
        var dropped = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (dropped[i]) continue;
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (dropped[j] || lines[j].Kind != lines[i].Kind || sameLift?.Invoke(lines[i], lines[j]) == false) continue;
                var a = lines[i];
                var b = lines[j];
                double bottom = Math.Sqrt((a.E[0] - b.E[0]) * (a.E[0] - b.E[0]) + (a.N[0] - b.N[0]) * (a.N[0] - b.N[0]));
                double top = Math.Sqrt((a.E[^1] - b.E[^1]) * (a.E[^1] - b.E[^1]) + (a.N[^1] - b.N[^1]) * (a.N[^1] - b.N[^1]));
                if (bottom > reach || top > reach) continue;
                dropped[j] = true;
                a.Side = (float)Math.Max(2, (bottom + top) / 2);
            }
        }
        var kept = new List<RopewayLine>();
        for (int i = 0; i < lines.Count; i++) if (!dropped[i]) kept.Add(lines[i]);
        return kept;
    }
}

/// <summary>
/// The carriers of a lift as a pure function of the sim clock (#912): every peer sees the same chair
/// at the same place with nothing sent.
/// <list type="bullet">
/// <item>A loop (chairs, gondolas, T-bars, rope tows): carrier i is <c>(sim·speed + i·2L/n) mod 2L</c>
/// along the loop, up the line while under L, back down the return side after.</item>
/// <item>A cable car (#915): two cabins, each on its own track, one going up while the other comes
/// down, waiting <see cref="Dwell"/> s at each station.</item>
/// </list>
/// </summary>
public static class RopewayMotion
{
    /// <summary>A cable car's wait at each station, s.</summary>
    public const float Dwell = 25f;

    public static int Count(RopewayLine line) =>
        line.Shuttle ? 2 : Math.Max(2, (int)Math.Round(2 * line.Length / line.Spacing));

    /// <summary>Carrier <paramref name="i"/>'s place on a loop, 0..2L.</summary>
    public static float Loop(RopewayLine line, int i, double sim)
    {
        double loop = 2.0 * line.Length;
        double s = (sim * line.Speed + i * loop / Count(line)) % loop;
        return (float)(s < 0 ? s + loop : s);
    }

    /// <summary>A cable car's round trip: up, a wait at the top, down, a wait at the bottom.</summary>
    private static double Period(RopewayLine line) => 2.0 * (line.Length / line.Speed + Dwell);

    /// <summary>
    /// Where carrier <paramref name="i"/> is: <c>S</c> metres from the bottom along the line, on the
    /// return side or the second track (<c>OnSide</c>), and whether it is on its way up with riders
    /// (<c>Up</c>). A cabin waiting at either station is not on its way up.
    /// </summary>
    public static (float S, bool OnSide, bool Up) Carrier(RopewayLine line, int i, double sim)
    {
        if (!line.Shuttle)
        {
            float l = Loop(line, i, sim);
            return l <= line.Length ? (l, false, true) : (2 * line.Length - l, true, false);
        }
        double period = Period(line), travel = line.Length / line.Speed;
        double t = (sim + i * period / 2) % period;
        if (t < 0) t += period;
        bool side = i == 1;
        if (t < travel) return ((float)(t * line.Speed), side, true);
        if (t < travel + Dwell) return (line.Length, side, false);
        if (t < 2 * travel + Dwell) return ((float)(line.Length - (t - travel - Dwell) * line.Speed), side, false);
        return (0f, side, false);
    }

    /// <summary>
    /// Carrier <paramref name="i"/> as a point: offset left of the way up on the return side or the
    /// second track, and the flat way it faces (east, north). A loop's carrier faces its travel; a
    /// cable car's cabin always faces up its track.
    /// </summary>
    public static (double E, double N, float Alt, float DirE, float DirN, bool Up) Place(RopewayLine line, int i, double sim)
    {
        var (s, side, up) = Carrier(line, i, sim);
        var (e, n, alt, de, dn) = line.At(s);
        if (side) { e -= dn * line.Side; n += de * line.Side; }
        bool faceUp = up || line.Shuttle;
        return (e, n, alt, faceUp ? de : -de, faceUp ? dn : -dn, up);
    }

    /// <summary>Seconds until carrier <paramref name="i"/> leaves the bottom station: 0 while it is on its way up.</summary>
    public static double UntilDeparture(RopewayLine line, int i, double sim)
    {
        if (!line.Shuttle)
        {
            float l = Loop(line, i, sim);
            return l <= line.Length ? 0 : (2 * line.Length - l) / line.Speed;
        }
        double period = Period(line), travel = line.Length / line.Speed;
        double t = (sim + i * period / 2) % period;
        if (t < 0) t += period;
        return t < travel ? 0 : period - t;
    }

    /// <summary>The carrier that leaves the bottom station next, and in how many seconds.</summary>
    public static (int Carrier, double Wait) Next(RopewayLine line, double sim)
    {
        int best = 0;
        double bestWait = double.MaxValue;
        if (line.Shuttle)
        {
            double period = Period(line);
            for (int i = 0; i < 2; i++)
            {
                double t = (sim + i * period / 2) % period;
                if (t < 0) t += period;
                double wait = (period - t) % period;
                if (wait < bestWait) { bestWait = wait; best = i; }
            }
            return (best, bestWait);
        }
        double loop = 2.0 * line.Length;
        for (int i = 0, n = Count(line); i < n; i++)
        {
            double wait = (loop - Loop(line, i, sim)) % loop / line.Speed;
            if (wait < bestWait) { bestWait = wait; best = i; }
        }
        return (best, bestWait);
    }
}
