namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Layout;

/// <summary>
/// Tram tracks as approach lanes of the junction planner's cores (#1057). A tram track is in the rail layer, not an arm: it runs
/// through a core on its own line. Each way a tram takes through a written core (a track entering it across one arm's mouth and
/// leaving across another's, over the switches inside, the way its trams run: <see cref="TramTracks"/>) becomes a tram path
/// (<c>JPTH</c> kind <see cref="JunctionPathKind.Tram"/>, on the track itself, its ends on the track where it crosses the mouths),
/// with its move as the plans name the turns; at lights each approach a tram enters by gets a tram group (<see cref="SignalArm.TramMoves"/>),
/// a tram lane on its <c>LANE</c> record (where the track crosses the cars' line), a tram head on a pole, and its ways as conflict
/// lines for the timing (<see cref="ConflictTimes.Mover.Tram"/>). Rules: <c>docs/notes/tools/tram-approaches.md</c>.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>
    /// One way a tram takes through a core: the arm it enters by and the arm it leaves by (<see cref="ArmPlan.Arm"/>), the move,
    /// its points from where its track crosses the entry mouth to where it crosses the exit's (LV95, on the track the trams run on),
    /// how far right of its line's centre that track lies at each end (a line mapped with two tracks), and its track before the
    /// entry, outward (for its stop line).
    /// </summary>
    internal sealed record TramWay(int FromArm, int ToArm, SignalMoves Move, List<Vec2> Points, double FromOffset, double ToOffset, List<Vec2> Before);

    /// <summary>A block's tram tracks, each the way its trams run (both ways where it carries both): for the cores' tram ways.</summary>
    internal sealed class TramNet
    {
        /// <summary>The tracks as the trams run them (LV95), each with how far right of its line's centre it lies.</summary>
        public readonly List<(Vec2[] Points, double Offset)> Lines = new();
        /// <summary>Each line's height at a point (its source line's, as the rewrite writes it).</summary>
        public readonly List<Func<Vec2, float>> Height = new();
        private readonly Dictionary<(long, long), List<int>> _starts = new(), _ends = new();

        private const double Snap = 0.5;

        public TramNet(RoadGenResult result)
        {
            var raw = new List<(Vec2[] Points, bool Double, RoadFlags Flags, Func<Vec2, float> Height)>();
            foreach (var ribbon in result.Ribbons)
            {
                if (ribbon.Stations.Count < 2 || result.Network.Links[ribbon.LinkId].Tag is not Source s) continue;
                var seg = s.Segment;
                if (seg.Class != RoadClass.Railway || (seg.Flags & RoadFlags.Tramway) == 0 || (seg.Flags & (RoadFlags.Disused | RoadFlags.Funicular)) != 0) continue;
                raw.Add((ribbon.Stations.Select(x => x.Position).ToArray(), (seg.Flags & RoadFlags.DoubleTrack) != 0, seg.Flags, s.SampleHeight));
            }
            var ways = TramTracks.Directions(raw.Select(r => r.Points.Select(p => (p.X, p.Y)).ToArray()).ToList(), raw.Select(r => r.Double).ToList());
            for (int i = 0; i < raw.Count; i++)
            {
                var (p, dbl, flags, height) = raw[i];
                double offset = RoadFormat.TrackOffset(flags);
                // a line of two tracks: each way on its right one; a single track its own way, or both
                if (dbl) { Add(Shift(p, offset), offset, height); Add(Shift(p.Reverse().ToArray(), offset), offset, height); continue; }
                if (ways[i] >= 0) Add(p, 0, height);
                if (ways[i] <= 0) Add(p.Reverse().ToArray(), 0, height);
            }
        }

        private void Add(Vec2[] points, double offset, Func<Vec2, float> height)
        {
            int i = Lines.Count;
            Lines.Add((points, offset));
            Height.Add(height);
            Bin(_starts, points[0]).Add(i);
            Bin(_ends, points[^1]).Add(i);
        }

        private static List<int> Bin(Dictionary<(long, long), List<int>> map, Vec2 p)
        {
            var key = ((long)Math.Round(p.X / Snap), (long)Math.Round(p.Y / Snap));
            if (!map.TryGetValue(key, out var list)) map[key] = list = new();
            return list;
        }

        /// <summary>The tracks starting (or ending) within a snap cell or two of <paramref name="p"/>.</summary>
        public IEnumerable<int> StartingAt(Vec2 p) => Near(_starts, p);
        public IEnumerable<int> EndingAt(Vec2 p) => Near(_ends, p);

        private static IEnumerable<int> Near(Dictionary<(long, long), List<int>> map, Vec2 p)
        {
            long x = (long)Math.Round(p.X / Snap), y = (long)Math.Round(p.Y / Snap);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (map.TryGetValue((x + dx, y + dy), out var list))
                        foreach (int i in list) yield return i;
        }

        /// <summary>A polyline moved <paramref name="offset"/> m to the right of its direction.</summary>
        private static Vec2[] Shift(Vec2[] p, double offset)
        {
            var q = new Vec2[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                var d = (i + 1 < p.Length ? p[i + 1] - p[i] : p[i] - p[i - 1]) + (i > 0 && i + 1 < p.Length ? p[i] - p[i - 1] : Vec2.Zero);
                var right = -d.Normalized().Perp;
                q[i] = p[i] + right * offset;
            }
            return q;
        }
    }

    /// <summary>A tram crosses a mouth within this of its line between the kerbs (m).</summary>
    private const double TramMouthReach = 3.0;

    /// <summary>A tram's track carries on over a switch inside a core only this straight (cos of the angle): the game's trains' rule.</summary>
    private const double TramStraightOn = 0.5;

    /// <summary>
    /// A track on its own bed belongs to the arm it runs beside (#1161) within this of the carriageway's edge past the carriageway's
    /// own width (m): a central reservation between a divided road's carriageways, a bed beside the road.
    /// </summary>
    public const double TramBedMargin = 4.0;

    /// <summary>...and runs this close to the arm's way (cos of the angle: 30 degrees).</summary>
    private const double TramBedParallel = 0.87;

    /// <summary>...crossing the core's outline this far inside (-) or outside (+) the arm's mouth along its axis (m).</summary>
    private const double TramBedInside = 6, TramBedOutside = 20;

    /// <summary>
    /// The attach rule (#1161, the user: "isn't it just like a bike lane?") on one arm: a track crossing a core's outline at
    /// <paramref name="p"/>, running <paramref name="dir"/>, beside the mouth <paramref name="left"/>-<paramref name="right"/>
    /// (its carriageway's kerbs; <paramref name="outward"/> the arm's way out): how far it lies past the carriageway's edge (m), or
    /// null where it does not belong to the arm: running the arm's way (into the junction where it enters, out where it leaves) and
    /// where the arm carries cars that way (<paramref name="lanesThatWay"/>), within <see cref="TramBedMargin"/> past the
    /// carriageway's width, across from the mouth.
    /// </summary>
    public static double? Alongside(Vec2 left, Vec2 right, Vec2 outward, Vec2 p, Vec2 dir, bool entering, bool lanesThatWay)
    {
        if (!lanesThatWay || dir.Dot(entering ? -outward : outward) < TramBedParallel) return null;
        double width = left.DistanceTo(right);
        if (width < 1e-6) return null;
        var across = (right - left) / width;
        var mid = (left + right) * 0.5;
        double along = (p - mid).Dot(outward);
        if (along < -TramBedInside || along > TramBedOutside) return null;
        double t = (p - left).Dot(across);
        double gap = t < 0 ? -t : t > width ? t - width : 0;
        return gap <= width + TramBedMargin ? gap : null;
    }

    /// <summary>The arm of <paramref name="jp"/> a track on its own bed runs beside where it crosses the core's outline (#1161), -1 none: the nearest.</summary>
    private static int ArmAlongside(JunctionPlan jp, Vec2 p, Vec2 dir, bool entering)
    {
        int best = -1;
        double near = double.MaxValue;
        foreach (var m in jp.Core!.Mouths)
        {
            if (jp.Arms.FirstOrDefault(a => a.Arm == m.Arm) is not { Axis: { } axis } a || a.Internal) continue;
            bool lanes = a.Mouth.CarLanes(entering ? StripWay.In : StripWay.Out).Any();
            if (Alongside(m.Left, m.Right, axis.DirAt(m.Station), p, dir, entering, lanes) is double gap && gap < near) (near, best) = (gap, m.Arm);
        }
        return best;
    }

    /// <summary>
    /// Every way a tram takes through <paramref name="jp"/>'s core (see <see cref="TramWay"/>); ways that do not run from one arm's
    /// mouth to another's (a track across a corner, back out by the arm it came in by) are counted and left out.
    /// </summary>
    internal static List<TramWay> TramWays(JunctionPlan jp, TramNet net, LayoutWriteStats stats)
    {
        var ways = new List<TramWay>();
        if (jp.Core is not { Outline.Count: >= 3 } core || net.Lines.Count == 0) return ways;
        var ring = core.Outline;
        double reach = ring.Max(p => p.DistanceTo(jp.Centre)) + 5;
        // (a divided road's carriageways are two arms side by side, #1161: there an arm is a way out only where its cars leave by
        // it, so a tram straight on beside them goes "through" to the carriageway leaving its way)
        bool Twin(ArmPlan a) => jp.Arms.Any(b => b != a && Math.Abs(Math.IEEERemainder(b.Heading - a.Heading, 2 * Math.PI)) < Math.PI / 6);
        var arms = jp.Arms.Select(a => new SignalArm(a.Heading, true, !Twin(a) || a.Mouth.CarLanes(StripWay.Out).Any())).ToList();
        int MouthAt(Vec2 p, Vec2 dir, bool entering)
        {
            int best = -1;
            double near = TramMouthReach;
            foreach (var m in core.Mouths)
            {
                double d = Polyline.PointSegmentDistance(p, m.Left, m.Right);
                if (d < near) (near, best) = (d, m.Arm);
            }
            return best >= 0 ? best : ArmAlongside(jp, p, dir, entering);
        }
        for (int l = 0; l < net.Lines.Count; l++)
        {
            var (p, offset) = net.Lines[l];
            if (!p.Any(q => q.DistanceTo(jp.Centre) < reach)) continue;
            for (int i = 0; i + 1 < p.Length; i++)
            {
                if (PriorityPlanner.Inside(ring, p[i]) || !PriorityPlanner.Inside(ring, p[i + 1])) continue;
                var entry = Crossing(ring, p[i], p[i + 1]);
                var before = new List<Vec2> { entry };
                for (int k = i; k >= 0; k--) before.Add(p[k]);
                ExtendBack(net, l, before, 80);
                foreach (var (points, last) in Trace(net, ring, l, i + 1, [entry], 0))
                {
                    var exit = points[^1];
                    int from = MouthAt(entry, (p[i + 1] - p[i]).Normalized(), true), to = MouthAt(exit, (points[^1] - points[^2]).Normalized(), false);
                    if (from < 0 || to < 0 || from == to) { stats.TramWaysLeftOut++; continue; }
                    int fk = jp.Arms.FindIndex(a => a.Arm == from), tk = jp.Arms.FindIndex(a => a.Arm == to);
                    var move = SignalPlan.Turn(arms, fk, tk);
                    double toOffset = net.Lines[last].Offset;
                    ways.Add(new TramWay(from, to, move, Polyline.Simplify(points, 0.02), offset, -toOffset, before));
                }
            }
        }
        return ways;
    }

    /// <summary>
    /// The track on from point <paramref name="k"/> of line <paramref name="l"/> to where it leaves the core (<paramref name="ring"/>),
    /// over the switches inside it: every way out, each its points and the line it leaves on.
    /// </summary>
    private static IEnumerable<(List<Vec2> Points, int Last)> Trace(TramNet net, IReadOnlyList<Vec2> ring, int l, int k, List<Vec2> sofar, int depth)
    {
        var p = net.Lines[l].Points;
        var points = new List<Vec2>(sofar);
        for (; k < p.Length; k++)
        {
            if (!PriorityPlanner.Inside(ring, p[k]))
            {
                points.Add(Crossing(ring, p[k], p[k - 1]));
                yield return (points, l);
                yield break;
            }
            points.Add(p[k]);
        }
        // the line ends inside the core: on over every track carrying straight on from its end (a switch), not back
        if (depth >= 6 || Polyline.Length(points) > 250) yield break;
        var dir = (p[^1] - p[^2]).Normalized();
        foreach (int next in net.StartingAt(p[^1]).Distinct())
        {
            var q = net.Lines[next].Points;
            if (next == l || q.Length < 2 || (q[1] - q[0]).Normalized().Dot(dir) < TramStraightOn) continue;
            // (a line two ways: not its own other way)
            if (q[^1].DistanceTo(p[0]) < 0.5 && q[0].DistanceTo(p[^1]) < 0.5) continue;
            foreach (var way in Trace(net, ring, next, 1, points, depth + 1)) yield return way;
        }
    }

    /// <summary>
    /// Where a tram way's track crosses its approach's stop line (<paramref name="stop"/> m out from the mouth at
    /// <paramref name="mouth"/>): the point, and how far right of the arm's axis it lies as an arriving driver sees it; null where
    /// its track does not reach back that far.
    /// </summary>
    internal static (Vec2 At, double Lateral)? TramStop(ArmPlan a, double mouth, double stop, TramWay w)
    {
        var axis = a.Axis!;
        double target = mouth + stop;
        for (int k = 1; k < w.Before.Count; k++)
        {
            double s0 = axis.StationPast(w.Before[k - 1]), s1 = axis.StationPast(w.Before[k]);
            if (s1 < target || s1 - s0 < 1e-6) continue;
            var q = Vec2.Lerp(w.Before[k - 1], w.Before[k], Math.Clamp((target - s0) / (s1 - s0), 0, 1));
            return (q, (q - axis.PointAt(target)).Dot(axis.RightAt(target)));
        }
        return null;
    }

    /// <summary>The tram group of plan arm <paramref name="k"/> for <paramref name="move"/> (#1057), -1 none.</summary>
    public static int TramGroup(SignalPlan? plan, int k, SignalMoves move) =>
        plan is null ? -1 : plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Tram && g.Arm == k && (g.Moves & move) != 0);

    /// <summary>
    /// Writes a core's tram ways as tram paths (<c>JPTH</c>, #1057): on the track the trams run on, its ends where the track's line
    /// crosses each mouth (the centre of a line mapped with two tracks, the path's offsets right of it), heights on the cap, its
    /// group the approach's tram group at lights.
    /// </summary>
    private static void WriteTramPaths(JunctionPlan jp, TileId home, RoadJunction cap, SignalPlan? plan, short signalIndex, List<TramWay> ways,
        Dictionary<TileId, List<RoadJunctionPath>> pathsOf, LayoutWriteStats stats)
    {
        if (ways.Count == 0) return;
        stats.TramCores++;
        foreach (var w in ways)
        {
            int fk = jp.Arms.FindIndex(a => a.Arm == w.FromArm);
            int g = TramGroup(plan, fk, w.Move);
            var pts = w.Points;
            // (each end on its line's centre: the track lies its offset to the right of the way, Vec2.Perp is to the left)
            var start = pts[0] + (pts[1] - pts[0]).Normalized().Perp * w.FromOffset;
            var end = pts[^1] - (pts[^1] - pts[^2]).Normalized().Perp * w.ToOffset;
            JunctionPathEnd End(Vec2 p, double offset) => new((float)(p.X - home.MinE), CapHeightAt(cap, home, p), (float)(home.MaxN - p.Y), (float)offset);
            Get(pathsOf, home).Add(new RoadJunctionPath
            {
                Junction = cap, From = End(start, w.FromOffset), To = End(end, w.ToOffset), Move = w.Move, Kind = JunctionPathKind.Tram,
                Signal = g >= 0 ? signalIndex : (short)-1, Group = (byte)Math.Max(0, g), Width = TramWidth, ToWidth = TramWidth,
                Points = RoadPaintGeometry.Simplify(Local(home, pts, p => CapHeightAt(cap, home, p), 0f)),
            });
            stats.TramPaths++;
            if (g >= 0) stats.TramLitPaths++;
        }
    }

    /// <summary>A tram's width (m): a 2.3-2.4 m low-floor car.</summary>
    private const float TramWidth = 2.4f;

    /// <summary>
    /// The tram ways as conflict lines for the timing (#979, #1057): each from where its track crosses its approach's stop line,
    /// in its tram group; none for a way whose arm has no line or no tram group.
    /// </summary>
    internal static List<ConflictTimes.Line> TramLines(JunctionPlan jp, SignalPlan plan, List<TramWay> ways, double length = ConflictTimes.TramLength)
    {
        var lines = new List<ConflictTimes.Line>();
        foreach (var w in ways)
        {
            int k = jp.Arms.FindIndex(a => a.Arm == w.FromArm);
            int g = TramGroup(plan, k, w.Move);
            if (g < 0) continue;
            var a = jp.Arms[k];
            var ex = jp.Exterior.FirstOrDefault(x => x.Arm == a);
            double mouth = jp.Core!.Mouths.First(m => m.Arm == a.Arm).Station;
            var points = new List<Vec2>();
            if (ex?.CarStop is { } stop && TramStop(a, mouth, stop, w) is var (at, _)) points.Add(at);
            points.AddRange(w.Points);
            lines.Add(new ConflictTimes.Line(g, ConflictTimes.Mover.Tram, points, TramWidth + 0.2, ConflictTimes.TramClear, ConflictTimes.TramEnter,
                length, plan.PassingTime(g)));
        }
        return lines;
    }

    /// <summary>
    /// Each lit approach a tram enters by gets a tram lane on its <c>LANE</c> record (#1057): where its track crosses the cars'
    /// line, from the street's own arriving lane as the record counts, its trams' moves.
    /// </summary>
    private static void TramLanes(JunctionPlan jp, List<TramWay> ways, List<RoadApproach> records, short signalIndex, LayoutWriteStats stats)
    {
        foreach (var byTrack in ways.GroupBy(w => (w.FromArm, Math.Round(w.Points[0].X, 1), Math.Round(w.Points[0].Y, 1))))
        {
            var w = byTrack.First();
            int k = jp.Arms.FindIndex(a => a.Arm == w.FromArm);
            var a = jp.Arms[k];
            var ex = jp.Exterior.FirstOrDefault(x => x.Arm == a);
            if (ex?.CarStop is not { } stop || records.FirstOrDefault(r => r.Signal == signalIndex && r.SignalArm == k) is not { } record) continue;
            double mouth = jp.Core!.Mouths.First(m => m.Arm == a.Arm).Station;
            if (TramStop(a, mouth, stop, w) is not var (_, lateral)) continue;
            var moves = SignalMoves.None;
            foreach (var x in byTrack) moves |= x.Move;
            // (left to right as the record keeps its lanes)
            var lane = new ApproachLane((float)(lateral - record.LaneCentre), 0f, 0f, moves, ApproachLaneKind.Tram);
            int at = record.Lanes.FindIndex(l => l.Offset > lane.Offset);
            record.Lanes.Insert(at < 0 ? record.Lanes.Count : at, lane);
            stats.TramLanes++;
        }
    }

    /// <summary>Per plan arm, the trams' moves from it and where their track crosses its stop line (m right of the axis), for the plan and the poles.</summary>
    internal static Dictionary<int, (SignalMoves Moves, double Lateral)> TramArms(JunctionPlan jp, List<TramWay> ways)
    {
        var result = new Dictionary<int, (SignalMoves Moves, double Lateral)>();
        foreach (var w in ways)
        {
            int k = jp.Arms.FindIndex(a => a.Arm == w.FromArm);
            var a = jp.Arms[k];
            var ex = jp.Exterior.FirstOrDefault(x => x.Arm == a);
            double mouth = jp.Core!.Mouths.First(m => m.Arm == a.Arm).Station;
            double lateral = ex?.CarStop is { } stop && TramStop(a, mouth, stop, w) is var (_, l) ? l
                : (w.Points[0] - a.Axis!.PointAt(mouth)).Dot(a.Axis.RightAt(mouth));
            var had = result.GetValueOrDefault(k);
            result[k] = (had.Moves | w.Move, had.Moves == SignalMoves.None ? lateral : had.Lateral);
        }
        return result;
    }

    /// <summary>
    /// Whether a tram track runs between two nodes <paramref name="a"/> and <paramref name="b"/> (#1161: within the circle the street
    /// between them spans: across it, on a central reservation between a divided road's carriageways, or turning off it).
    /// </summary>
    internal static bool TramAcross(TramNet net, Vec2 a, Vec2 b)
    {
        double reach = a.DistanceTo(b) * 0.5;
        var mid = (a + b) * 0.5;
        foreach (var (p, _) in net.Lines)
            for (int i = 0; i + 1 < p.Length; i++)
                if (Polyline.PointSegmentDistance(mid, p[i], p[i + 1]) <= reach) return true;
        return false;
    }

    /// <summary>Where segment <paramref name="a"/>-<paramref name="b"/> (from outside to inside) crosses the ring: bisected.</summary>
    private static Vec2 Crossing(IReadOnlyList<Vec2> ring, Vec2 a, Vec2 b)
    {
        bool aIn = PriorityPlanner.Inside(ring, a);
        for (int k = 0; k < 24; k++)
        {
            var m = (a + b) * 0.5;
            if (PriorityPlanner.Inside(ring, m) == aIn) a = m; else b = m;
        }
        return (a + b) * 0.5;
    }

    /// <summary>Carries a track's points back (outward from a core) over the track it comes from, up to <paramref name="length"/> m.</summary>
    private static void ExtendBack(TramNet net, int l, List<Vec2> before, double length)
    {
        for (int depth = 0; depth < 8 && Polyline.Length(before) < length; depth++)
        {
            var p = net.Lines[l].Points;
            var dir = (p[1] - p[0]).Normalized();
            int from = -1;
            double straightest = TramStraightOn;
            foreach (int prev in net.EndingAt(p[0]).Distinct())
            {
                var q = net.Lines[prev].Points;
                if (prev == l || q.Length < 2 || q[0].DistanceTo(p[^1]) < 0.5 && q[^1].DistanceTo(p[0]) < 0.5) continue;
                double straight = (q[^1] - q[^2]).Normalized().Dot(dir);
                if (straight > straightest) (straightest, from) = (straight, prev);
            }
            if (from < 0) return;
            var r = net.Lines[from].Points;
            for (int k = r.Length - 2; k >= 0; k--) before.Add(r[k]);
            l = from;
        }
    }

    // ---- tram stops ----------------------------------------------------------------------------

    /// <summary>A stop serves the tracks within this of its point (m): swissTLM3D's stop point lies on the platform or in the street.</summary>
    private const double TramStopReach = 15;

    /// <summary>A stop's sign stands this far right of its track's centre (m): past the tram's side, on the platform.</summary>
    private const double TramStopAside = 2.6;

    /// <summary>The sign's plate top over its foot (m).</summary>
    public const float TramStopHeight = 2.6f;

    /// <summary>The stops of <c>tram_stops.tsv</c> (#1057: <c>name, e, n</c>, a header row), LV95.</summary>
    internal static List<(string Name, Vec2 At)> ReadTramStops(string path) => ReadStopFile(path);

    /// <summary>
    /// The tram stops of a region (#1057): OSM's (<c>osm_tram_stops.tsv</c> beside <paramref name="path"/>, from <c>--osm-overlay</c>:
    /// <c>railway=tram_stop</c>, tram stop positions) and swissTLM3D's (<paramref name="path"/>, <c>--tram-stops</c>) where OSM has
    /// none within <see cref="TramStopMerge"/> m: OSM's lie on the track. Either file may be missing.
    /// </summary>
    internal static List<(string Name, Vec2 At)> ReadAllTramStops(string path)
    {
        string osmPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "osm_tram_stops.tsv");
        var osm = File.Exists(osmPath) ? ReadStopFile(osmPath) : [];
        var tlm = File.Exists(path) ? ReadStopFile(path) : [];
        return [.. osm, .. tlm.Where(t => !osm.Any(o => o.At.DistanceTo(t.At) < TramStopMerge))];
    }

    /// <summary>A swissTLM3D stop this close to an OSM one is the same stop (m).</summary>
    private const double TramStopMerge = 25;

    private static List<(string Name, Vec2 At)> ReadStopFile(string path)
    {
        var list = new List<(string, Vec2)>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.StartsWith("name\t", StringComparison.Ordinal)) continue;
            var f = line.Split('\t');
            if (f.Length >= 3 && double.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double e)
                && double.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n))
                list.Add((f[0], new Vec2(e, n)));
        }
        return list;
    }

    /// <summary>
    /// A stop sign (#1057, <see cref="PointPropType.TramStop"/>) by every track a tram stop serves, each way its trams run: on the
    /// trams' right, past their side, facing them; the game's trams stop with their front at it. Two stop points of one stop (one
    /// per platform) give one sign per track: the nearer.
    /// </summary>
    private static void EmitTramStops(List<(string Name, Vec2 At)> stops, TramNet net, HashSet<TileId> block, Dictionary<TileId, List<RoadPointProp>> signs, LayoutWriteStats stats)
    {
        var best = new Dictionary<(int Line, int Stop), (double Distance, Vec2 At, Vec2 Dir, float Y)>();
        var placed = new List<(Vec2 At, Vec2 Dir)>();
        for (int s = 0; s < stops.Count; s++)
        {
            var at = stops[s].At;
            for (int l = 0; l < net.Lines.Count; l++)
            {
                var p = net.Lines[l].Points;
                double near = TramStopReach;
                (Vec2 Q, Vec2 Dir)? hit = null;
                for (int i = 0; i + 1 < p.Length; i++)
                {
                    var ab = p[i + 1] - p[i];
                    double l2 = ab.LengthSquared;
                    if (l2 < 1e-9) continue;
                    double t = Math.Clamp((at - p[i]).Dot(ab) / l2, 0, 1);
                    var q = p[i] + ab * t;
                    double d = q.DistanceTo(at);
                    if (d < near) (near, hit) = (d, (q, ab / Math.Sqrt(l2)));
                }
                if (hit is not var (point, dir)) continue;
                best[(l, s)] = (near, point, dir, net.Height[l](point));
            }
        }
        // per track, the nearest stop point; one sign per track and stop name within 40 m
        foreach (var ((l, s), (_, q, dir, y)) in best.OrderBy(kv => kv.Value.Distance))
        {
            if (placed.Any(x => x.At.DistanceTo(q) < 40 && x.Dir.Dot(dir) > 0.7)) continue;
            placed.Add((q, dir));
            var foot = q - dir.Perp * TramStopAside;
            var home = TileId.FromLv95(foot.X, foot.Y);
            if (!block.Contains(home)) continue;
            // facing the arriving trams: its front the way they come from (a prop's heading: 0 faces -Z, north)
            var facing = -dir;
            float heading = (float)Math.Atan2(-facing.X, facing.Y);
            Get(signs, home).Add(new RoadPointProp(PointPropType.TramStop, 0, PropFlags.Solid,
                (float)(foot.X - home.MinE), y, (float)(home.MaxN - foot.Y), heading, TramStopHeight));
            stats.TramStops++;
        }
    }

    /// <summary>
    /// How far each tram group of <paramref name="plan"/> leads the car groups starting with it (#1057, the user's leading signal):
    /// counted by length (1 s, the riders' 3 s for a car turning across it, none), the junctions with none listed.
    /// </summary>
    private static void CountTramLeads(JunctionPlan jp, SignalPlan plan, LayoutWriteStats stats)
    {
        float Ahead(float s, float t) => ((s - t) % plan.Cycle + plan.Cycle) % plan.Cycle;
        List<float> Starts(int g) => plan.Groups[g].Intervals.Where((iv, k) => iv.Aspect == SignalAspect.Green
            && !(k == 0 && iv.From < 1e-3f && plan.Groups[g].Intervals[^1].Aspect == SignalAspect.Green)).Select(iv => iv.From).ToList();
        for (int t = 0; t < plan.Groups.Count; t++)
        {
            if (plan.Groups[t].Kind != SignalGroupKind.Tram) continue;
            var trams = Starts(t);
            float most = 0;
            // at each car green start while the tram is already green: since its own green started (its latest start before)
            for (int h = 0; h < plan.Groups.Count; h++)
                if (plan.Groups[h].Kind == SignalGroupKind.Car && trams.Count > 0)
                    foreach (float s in Starts(h))
                        if (plan.State(t, s - 0.05 - plan.Offset) == SignalAspect.Green)
                            most = Math.Max(most, trams.Select(x => Ahead(s, x)).Min());
            if (most >= SignalPlan.BikeLead - 0.05f) stats.TramLeadsLong++;
            else if (most >= SignalPlan.TramLead - 0.05f) stats.TramLeads++;
            else
            {
                stats.TramNoLead++;
                if (stats.TramNoLeadAt.Count < 8) stats.TramNoLeadAt.Add(new Vec2(Math.Round(jp.Centre.X), Math.Round(jp.Centre.Y)));
            }
        }
    }
}
