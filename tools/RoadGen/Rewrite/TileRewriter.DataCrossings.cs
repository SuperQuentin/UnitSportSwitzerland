namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Pedestrian crossings from OSM (#700 phase 3, part of #292): a marked <c>highway=crossing</c> node on a junction
/// arm's TLM line within 30 m of the junction gets a yellow zebra across that arm (SSV 6.17; the bars of
/// <see cref="EmitCrossing"/>), at the kerb ends when the node is near the mouth (diagonal beside a tight corner),
/// else square where the node is. At traffic lights the #682 crosswalk is drawn where OSM has one even without a
/// sidewalk, and where OSM maps any crossing round the lights only there (#711). Kerb ramps, islands and traffic
/// yielding to pedestrians stay with #292.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>A crossing node this close to a junction (along the arm's line) belongs to that arm.</summary>
    private const double CrossingReach = 30.0;
    /// <summary>A crossing whose node lies within this of the mouth is drawn at the kerb ends; further out, where the node is.</summary>
    private const double CrossingAtKerb = 6.0;

    /// <summary>OSM's pedestrian crossings by TLM line (#700).</summary>
    internal sealed class CrossingNodes
    {
        private readonly Dictionary<(string Uuid, int Part), List<OsmNodesReader.Entry>> _byLine = new();
        public int Count { get; private set; }

        public static CrossingNodes? From(OsmNodesReader? nodes)
        {
            if (nodes is null) return null;
            var r = new CrossingNodes();
            foreach (var e in nodes.All)
            {
                // marked ones are drawn; an unmarked one (no paint, no signals) is kept as data only: at the lights it tells the
                // arm was mapped and has no crosswalk (#711). crossing=no is on no highway=crossing node, so never read
                if (e.Kind != OsmNodesReader.NodeKind.Crossing || !(Marked(e) || e.Value == "unmarked")) continue;
                if (!r._byLine.TryGetValue((e.Uuid, e.Part), out var list)) r._byLine[(e.Uuid, e.Part)] = list = new();
                list.Add(e);
                if (Marked(e)) r.Count++;
            }
            return r;
        }

        private static bool Marked(OsmNodesReader.Entry e) => e.Value is "zebra" or "marked" or "uncontrolled" or "traffic_signals";

        /// <summary>
        /// The nearest marked crossing on arm <paramref name="arm"/> of a junction: its distance from the mouth along the arm
        /// (negative inside it), null where none lies within <see cref="CrossingReach"/> of the junction on that arm.
        /// <paramref name="unmarked"/>: an unmarked one counts too (whether OSM maps any crossing there, #711).
        /// </summary>
        public double? OnArm(Junction j, int arm, RoadNetwork net, bool unmarked = false)
        {
            var a = j.Arms[arm];
            if (net.Links[a.LinkId].Tag is not Source { Key: { } key } source || source.Plan.Length < 2
                || !_byLine.TryGetValue((key.Uuid, key.Part), out var list)) return null;
            bool atEnd = PriorityPlanner.EndAt(net, j, a) == LinkEnd.End;
            double m = key.FromM + source.AlongOf(atEnd ? source.Plan[^1] : source.Plan[0]);
            double length = source.AlongOf(source.Plan[^1]) - source.AlongOf(source.Plan[0]);
            double? best = null;
            foreach (var e in list)
            {
                double d = atEnd ? m - e.Along : e.Along - m;   // out from the junction node along the arm
                // a node belongs to the nearer end of its link: on a short one, not to both junctions
                if (d < -1 || d > CrossingReach || d > length - d || !unmarked && !Marked(e)) continue;
                if (best is null || d < best) best = d;
            }
            return best - a.Trim;
        }
    }

    /// <summary>
    /// Zebras from OSM at junctions without lights (#700): every car-road arm with a marked crossing node near the
    /// junction. Returns how many it drew.
    /// </summary>
    private static int EmitDataCrossings(PriorityResult priority, RoadGenResult result, Dictionary<(int Node, int Arm), ArmLanes> pockets,
        CrossingNodes? crossings, HashSet<TileId> block, HashSet<TileId> wanted, Dictionary<TileId, List<RoadPaint>> paint,
        Dictionary<TileId, List<RoadAreaProp>> areas, SignalStats stats, Func<int, LinkEnd, bool, RoadSide> streetSideAt)
    {
        if (crossings is null) return 0;
        var net = result.Network;
        int drawn = 0;
        foreach (var (junction, plan) in priority.Plans)
        {
            if (JunctionRules.Of(plan.Kind).Has(JunctionRule.StopLine)) continue;   // the lights draw theirs with the stop line (#682)
            var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
            if (!block.Contains(home) || !wanted.Contains(home)) continue;
            for (int i = 0; i < junction.Arms.Count; i++)
            {
                var arm = junction.Arms[i];
                var link = net.Links[arm.LinkId];
                if (link.Tag is not Source source || InfoOf(link) is not { } info || !PriorityPlanner.IsCarRoad(info.Class)) continue;
                if (crossings.OnArm(junction, i, net) is not { } at || !block.Contains(source.Tile)) continue;
                var u = Vec2.FromHeading(arm.OutwardHeading);
                var right = u.Perp;   // the approaching driver's right
                var mid = (arm.Left + arm.Right) * 0.5;
                var lanes = pockets.GetValueOrDefault((junction.NodeId, i));
                double half = arm.HalfWidth;
                double lo = -(half + (lanes?.ExitWidening ?? 0)), hi = lanes?.Approach is { } layout ? half + layout.EdgeOut : half;
                var end = PriorityPlanner.EndAt(net, junction, arm);
                bool drawnRight = end == LinkEnd.End;
                var streetRight = streetSideAt(arm.LinkId, end, drawnRight);
                var streetLeft = streetSideAt(arm.LinkId, end, !drawnRight);
                // near the mouth: at the kerb ends, 0.5 m out (diagonal where a tight corner pulled one in); further out: where the node is
                bool atKerb = at <= CrossingAtKerb;
                double far = atKerb ? 0.5 + ZebraDepth + ZebraClear : at + ZebraDepth * 0.5 + ZebraClear;
                // over an exit hatch wide enough for a refuge: the zebra moves out along the arm until both islands stand on it,
                // none in the junction, where cars turn into the arm (#700, the user's review of Sion)
                var exitWayHere = lanes is { ExitWay: { } ew, ExitFar: false } ? ew : null;
                double? shift = exitWayHere?.RefugeShift(far - ZebraClear - ZebraDepth, far - ZebraClear);
                if (shift is > 0) { far += shift.Value; atKerb = false; }
                // no bars over the refuge (the user's rule); the exit lies on the approaching driver's left (negative across)
                var span = shift is null ? null : exitWayHere!.RefugeSpan(far - ZebraClear - ZebraDepth, far - ZebraClear);
                // (#711) the carriageway's edges where the zebra stands, measured: the widening the arm records is the one at its
                // mouth, which a shrunk exit or a taper does not keep out here (Sion: the bars ran 1.5 m on over the path)
                var station = mid + u * (far - ZebraClear - ZebraDepth * 0.5);
                if (!atKerb && CarriagewayAcross(source, station, right, areas.GetValueOrDefault(source.Tile)) is { } edges)
                {
                    if (CornerPlanner.Debug is { } dbg && junction.Centre.DistanceTo(new Vec2(dbg.E, dbg.N)) < 15)
                        Console.WriteLine($"[zebra] arm {i}: across {lo:F2}..{hi:F2} from the mouth, {edges.Lo:F2}..{edges.Hi:F2} measured; hatch at mouth {exitWayHere?.HatchAtMouth:F2}, at 3/6/9 m {exitWayHere?.HatchAt(3):F2}/{exitWayHere?.HatchAt(6):F2}/{exitWayHere?.HatchAt(9):F2}");
                    (lo, hi) = edges;
                }
                int before = stats.Crossings;
                EmitCrossing(paint, source, mid, u, right, far, lo, hi, streetRight, streetLeft, areas, stats,
                    insetLeft: atKerb ? junction.KerbInset.GetValueOrDefault((i, false)) : 0,
                    insetRight: atKerb ? junction.KerbInset.GetValueOrDefault((i, true)) : 0,
                    gap: span is { } sp ? (-sp.Far, -sp.Near) : null, linkId: arm.LinkId);
                if (stats.Crossings > before)
                {
                    drawn++;
                    // over an exit hatch: its stripes stop at the crosswalk, and where it is wide enough a refuge carries the
                    // walkers across in two goes (#700, the user's review: the zebra ran over the stripes)
                    if (exitWayHere is { } exitWay)
                    {
                        double zebraTo = far - ZebraClear, zebraFrom = zebraTo - ZebraDepth;
                        bool refuge = shift is not null && exitWay.Refuge(Get(areas, exitWay.Tile), zebraFrom, zebraTo);
                        if (refuge) { stats.Refuges++; exitWay.HasIsland = true; }   // (#711: the through guide passes it)
                        // the hatch starts behind the crosswalk: none of it, stripes or lines, between the mouth and the crosswalk
                        // (the user's rules), and it does not close there
                        exitWay.ClearHatch(Get(paint, exitWay.Tile), double.NegativeInfinity, zebraTo + (refuge ? 2.3 : 0.3));
                        exitWay.OpenAtCrosswalk(Get(paint, exitWay.Tile), double.NegativeInfinity, zebraTo + 0.1, refuge);
                    }
                }
            }
        }
        return drawn;
    }

    /// <summary>
    /// The carriageway across an arm at <paramref name="at"/> (LV95), as offsets along <paramref name="right"/> (negative to the
    /// left), 5 cm steps out from the line: on the road's own ribbon (the line's half width) or on a turn lane's widening in
    /// the line's tile (<paramref name="pavement"/>, tile-local). Null where the line is not there.
    /// </summary>
    private static (double Lo, double Hi)? CarriagewayAcross(Source source, Vec2 at, Vec2 right, List<RoadAreaProp>? pavement)
    {
        double half = source.Segment.Width * 0.5;
        var tile = source.Tile;
        var strips = (pavement ?? []).Where(a => a.Type == AreaPropType.Pavement && a.Vertices.Length >= 9).ToList();
        double DistanceToLine(Vec2 p)
        {
            double best = double.MaxValue;
            for (int k = 1; k < source.Plan.Length; k++) best = Math.Min(best, DistanceToSegment(p, source.Plan[k - 1], source.Plan[k]));
            return best;
        }
        bool OnStrip(Vec2 p)
        {
            double x = p.X - tile.MinE, z = tile.MaxN - p.Y;
            foreach (var a in strips)
            {
                var v = a.Vertices;
                for (int t = 0; t + 2 < a.Indices.Length; t += 3)
                {
                    int i0 = a.Indices[t] * 3, i1 = a.Indices[t + 1] * 3, i2 = a.Indices[t + 2] * 3;
                    double d1 = (x - v[i1]) * (v[i0 + 2] - v[i1 + 2]) - (v[i0] - v[i1]) * (z - v[i1 + 2]);
                    double d2 = (x - v[i2]) * (v[i1 + 2] - v[i2 + 2]) - (v[i1] - v[i2]) * (z - v[i2 + 2]);
                    double d3 = (x - v[i0]) * (v[i2 + 2] - v[i0 + 2]) - (v[i2] - v[i0]) * (z - v[i0 + 2]);
                    bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                    if (!(neg && pos)) return true;
                }
            }
            return false;
        }
        bool OnRoad(Vec2 p) => DistanceToLine(p) < half - 0.01 || OnStrip(p);
        if (!OnRoad(at)) return null;
        double Edge(double sign)
        {
            double l = 0;
            while (l < half + 12 && OnRoad(at + right * (sign * (l + 0.05)))) l += 0.05;
            return l;
        }
        return (-Edge(-1), Edge(1));
    }
}
