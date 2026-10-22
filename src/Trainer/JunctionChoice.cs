using UnitSport.Gpx;

namespace UnitSport.Trainer;

/// <summary>
/// One point of the way a training rides (#901): LV95 and the road's height, the road graph's node it is
/// (or -1, a course point between nodes), and the course point it is (or -1, off the course).
/// </summary>
public readonly record struct WayPoint(double E, double N, double H, int Node = -1, int Course = -1);

/// <summary>A road leaving a junction: its first node, its bearing (degrees, negative left), and whether the way takes it.</summary>
public readonly record struct Branch(int First, double Bearing, bool Taken);

/// <summary>A junction ahead on the way: its node, how far along the way, and the roads out of it, leftmost first.</summary>
public sealed record Junction(int Node, double Arc, IReadOnlyList<Branch> Branches)
{
    /// <summary>The branch the way takes, or -1 (the way leaves the roads here: a course ridden as recorded).</summary>
    public int Taken => Branches.ToList().FindIndex(b => b.Taken);
}

/// <summary>
/// Junctions on a training's way and the roads out of them (<c>docs/plans/home-trainer-rides.md</c>, design 1),
/// on the road network's walking graph. Pure: no Godot, unit tested (<c>JunctionChoiceTests</c>; not the road generator's junction planner).
///
/// <para>
/// The way is whatever the bike rides: the GPX course snapped onto the roads, or a road chosen off it.
/// A junction is a graph node with three roads or more that the way passes through; each road out of it
/// gets a bearing, measured <see cref="Probe"/> m in so a kerb's curve or a vertex's jitter does not
/// decide it. The road the rider came in by is not a branch.
/// </para>
/// </summary>
public static class JunctionChoice
{
    /// <summary>Metres into a road where its direction is measured.</summary>
    public const double Probe = 15;

    /// <summary>A way passing this close to a node goes through it.</summary>
    public const double Reach = 2.5;

    /// <summary>Within this many degrees of the incoming road (reversed) a road is the one the rider came by.</summary>
    private const double BackCone = 45;

    /// <summary>The way's own direction within this many degrees of a road is that road.</summary>
    private const double TakenCone = 40;

    /// <summary>
    /// The first junction on <paramref name="way"/> after <paramref name="fromArc"/> m (the rider) and before
    /// <paramref name="ahead"/> m more, or null. <paramref name="arc"/> is the way's cumulative length.
    /// </summary>
    public static Junction? Next(IWalkGraph g, IReadOnlyList<WayPoint> way, IReadOnlyList<double> arc,
        int fromIndex, double fromArc, double ahead)
    {
        for (int i = Math.Max(0, fromIndex); i + 1 < way.Count && arc[i] <= fromArc + ahead; i++)
        {
            var (a, b) = (way[i], way[i + 1]);
            if (a.Node >= 0 && b.Node >= 0)
            {
                // two graph nodes in a row: one road step, nothing between them
                if (arc[i + 1] > fromArc + 0.5 && Build(g, way, arc, b.Node, arc[i + 1]) is { } j) return j;
                continue;
            }
            double len = arc[i + 1] - arc[i];
            int steps = Math.Max(1, (int)Math.Ceiling(len / 2));
            for (int k = 0; k <= steps; k++)
            {
                double s = arc[i] + len * k / steps;
                if (s <= fromArc + 0.5) continue;
                double t = len > 1e-9 ? (s - arc[i]) / len : 0;
                double e = a.E + (b.E - a.E) * t, n = a.N + (b.N - a.N) * t;
                foreach (int node in g.NodesNear(e, n, Reach))
                    if (g.Next(node).Count >= 3 && Build(g, way, arc, node, s) is { } j) return j;
            }
        }
        return null;
    }

    private static Junction? Build(IWalkGraph g, IReadOnlyList<WayPoint> way, IReadOnlyList<double> arc, int node, double at)
    {
        if (g.Next(node).Count < 3) return null;
        var (je, jn, _) = g.Pos(node);
        var back = At(way, arc, at - Probe);
        var on = At(way, arc, at + Probe);
        var dirIn = (je - back.E, jn - back.N);
        bool hasOut = at + Probe <= arc[^1] + 1e-6;
        var dirOut = (on.E - je, on.N - jn);
        if (Length(dirIn) < 1) return null;

        var all = new List<(int First, double Bearing, double Back, double Out)>();
        foreach (var (to, _) in g.Next(node))
        {
            var end = Follow(g, node, to, Probe);
            var (ee, en, _) = g.Pos(end.Count > 0 ? end[^1] : to);
            var dir = (ee - je, en - jn);
            if (Length(dir) < 0.5) continue;
            all.Add((to, Bearing(dirIn, dir), Math.Abs(Bearing((-dirIn.Item1, -dirIn.Item2), dir)),
                hasOut && Length(dirOut) > 1 ? Math.Abs(Bearing(dirOut, dir)) : double.MaxValue));
        }
        // the road the rider comes in by is not a way out
        int came = -1;
        for (int i = 0; i < all.Count; i++)
            if (all[i].Back < BackCone && (came < 0 || all[i].Back < all[came].Back)) came = i;
        if (came >= 0) all.RemoveAt(came);
        if (all.Count < 2) return null;   // a bend with a stub, not a choice
        int taken = -1;
        for (int i = 0; i < all.Count; i++)
            if (all[i].Out < TakenCone && (taken < 0 || all[i].Out < all[taken].Out)) taken = i;
        var branches = all.Select((b, i) => new Branch(b.First, b.Bearing, i == taken)).OrderBy(b => b.Bearing).ToList();
        return new Junction(node, at, branches);
    }

    /// <summary>
    /// The branch one step to <paramref name="side"/> (-1 left, +1 right) of the one taken, or -1 when
    /// there is none. With none taken, the straight-ahead line is the reference.
    /// </summary>
    public static int Pick(Junction j, int side)
    {
        double from = j.Taken >= 0 ? j.Branches[j.Taken].Bearing : 0;
        const double Apart = 10;
        int best = -1;
        for (int i = 0; i < j.Branches.Count; i++)
        {
            double b = j.Branches[i].Bearing;
            bool onSide = side < 0 ? b < from - Apart : b > from + Apart;
            if (!onSide) continue;
            if (best < 0 || Math.Abs(b - from) < Math.Abs(j.Branches[best].Bearing - from)) best = i;
        }
        return best;
    }

    /// <summary>
    /// The nodes from <paramref name="next"/> on, leaving <paramref name="from"/> for it, straight-most at
    /// every junction, until <paramref name="length"/> m or a dead end. Never loops back on itself.
    /// </summary>
    public static List<int> Follow(IWalkGraph g, int from, int next, double length)
    {
        var path = new List<int> { next };
        var seen = new HashSet<int> { from, next };
        double done = Dist(g, from, next);
        int prev = from, cur = next;
        while (done < length)
        {
            int step = Straightest(g, prev, cur, seen);
            if (step < 0) break;
            done += Dist(g, cur, step);
            path.Add(step);
            seen.Add(step);
            prev = cur;
            cur = step;
        }
        return path;
    }

    /// <summary>The road out of <paramref name="node"/> that turns least, coming from <paramref name="prev"/>; -1 at a dead end.</summary>
    public static int Straightest(IWalkGraph g, int prev, int node, IReadOnlySet<int>? avoid = null)
    {
        var (pe, pn, _) = g.Pos(prev);
        var (ne, nn, _) = g.Pos(node);
        var dirIn = (ne - pe, nn - pn);
        int best = -1;
        double bestTurn = double.MaxValue;
        foreach (var (to, _) in g.Next(node))
        {
            if (to == prev || avoid?.Contains(to) == true) continue;
            var (te, tn, _) = g.Pos(to);
            double turn = Math.Abs(Bearing(dirIn, (te - ne, tn - nn)));
            if (turn < bestTurn) { bestTurn = turn; best = to; }
        }
        return best;
    }

    /// <summary>
    /// The shortest way along the roads from <paramref name="start"/> to the first node that
    /// <paramref name="isGoal"/> accepts (given the node it is reached from), never stepping straight back to
    /// <paramref name="notVia"/>, within <paramref name="limit"/> m. The nodes from start to goal, or null.
    /// </summary>
    public static List<int>? RouteTo(IWalkGraph g, int notVia, int start, Func<int, int, bool> isGoal, double limit)
    {
        var dist = new Dictionary<int, double> { [start] = 0 };
        var prev = new Dictionary<int, int>();
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(start, 0);
        while (queue.TryDequeue(out int node, out double cost))
        {
            if (cost > dist[node]) continue;
            if (node != start && isGoal(prev[node], node))
            {
                var path = new List<int>();
                for (int n = node; ; n = prev[n])
                {
                    path.Add(n);
                    if (n == start) break;
                }
                path.Reverse();
                return path;
            }
            foreach (var (to, step) in g.Next(node))
            {
                if (node == start && to == notVia) continue;
                double through = cost + step;
                if (through > limit || through >= dist.GetValueOrDefault(to, double.PositiveInfinity)) continue;
                dist[to] = through;
                prev[to] = node;
                queue.Enqueue(to, through);
            }
        }
        return null;
    }

    /// <summary>Signed angle from <paramref name="a"/> to <paramref name="b"/> (E, N), degrees: negative is a left turn.</summary>
    public static double Bearing((double E, double N) a, (double E, double N) b)
    {
        double cross = a.E * b.N - a.N * b.E;
        double dot = a.E * b.E + a.N * b.N;
        return -Math.Atan2(cross, dot) * 180 / Math.PI;
    }

    /// <summary>The way's point at <paramref name="s"/> m along it, clamped to its ends.</summary>
    public static (double E, double N, double H) At(IReadOnlyList<WayPoint> way, IReadOnlyList<double> arc, double s)
    {
        if (s <= arc[0]) return (way[0].E, way[0].N, way[0].H);
        if (s >= arc[^1]) return (way[^1].E, way[^1].N, way[^1].H);
        int lo = 0, hi = arc.Count - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (arc[mid] <= s) lo = mid; else hi = mid;
        }
        double span = arc[hi] - arc[lo];
        double t = span > 1e-9 ? (s - arc[lo]) / span : 0;
        return (way[lo].E + (way[hi].E - way[lo].E) * t, way[lo].N + (way[hi].N - way[lo].N) * t, way[lo].H + (way[hi].H - way[lo].H) * t);
    }

    /// <summary>A way's cumulative flat length at each point.</summary>
    public static List<double> Arcs(IReadOnlyList<WayPoint> way)
    {
        var arc = new List<double>(way.Count);
        double s = 0;
        for (int i = 0; i < way.Count; i++)
        {
            if (i > 0) s += Math.Sqrt((way[i].E - way[i - 1].E) * (way[i].E - way[i - 1].E) + (way[i].N - way[i - 1].N) * (way[i].N - way[i - 1].N));
            arc.Add(s);
        }
        return arc;
    }

    /// <summary>A graph node as a way point.</summary>
    public static WayPoint Point(IWalkGraph g, int node)
    {
        var (e, n, h) = g.Pos(node);
        return new WayPoint(e, n, h, node);
    }

    private static double Dist(IWalkGraph g, int a, int b)
    {
        var (ae, an, _) = g.Pos(a);
        var (be, bn, _) = g.Pos(b);
        return Math.Sqrt((ae - be) * (ae - be) + (an - bn) * (an - bn));
    }

    private static double Length((double E, double N) v) => Math.Sqrt(v.E * v.E + v.N * v.N);
}
