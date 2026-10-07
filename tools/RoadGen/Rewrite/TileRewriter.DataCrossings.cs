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
/// sidewalk. Kerb ramps, islands and traffic yielding to pedestrians stay with #292.
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
                // marked ones only: an unmarked crossing (or crossing=no, informal) has no paint
                if (e.Kind != OsmNodesReader.NodeKind.Crossing || e.Value is not ("zebra" or "marked" or "uncontrolled" or "traffic_signals")) continue;
                if (!r._byLine.TryGetValue((e.Uuid, e.Part), out var list)) r._byLine[(e.Uuid, e.Part)] = list = new();
                list.Add(e);
                r.Count++;
            }
            return r;
        }

        /// <summary>
        /// The nearest marked crossing on arm <paramref name="arm"/> of a junction: its distance from the mouth along the arm
        /// (negative inside it), null where none lies within <see cref="CrossingReach"/> of the junction on that arm.
        /// </summary>
        public double? OnArm(Junction j, int arm, RoadNetwork net)
        {
            var a = j.Arms[arm];
            if (net.Links[a.LinkId].Tag is not Source { Key: { } key } source || source.Plan.Length < 2
                || !_byLine.TryGetValue((key.Uuid, key.Part), out var list)) return null;
            bool atEnd = PriorityPlanner.EndAt(net, j, a) == LinkEnd.End;
            double m = key.FromM + source.AlongOf(atEnd ? source.Plan[^1] : source.Plan[0]);
            double? best = null;
            foreach (var e in list)
            {
                double d = atEnd ? m - e.Along : e.Along - m;   // out from the junction node along the arm
                if (d < -1 || d > CrossingReach) continue;
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
            if (plan.Kind == PriorityPlanner.Kind.Signal) continue;   // the lights draw their own (#682)
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
                double lo = -(half + (lanes?.ExitWidening ?? 0)), hi = lanes?.Approach is { } layout ? half + layout.Edge() - layout.Half : half;
                var end = PriorityPlanner.EndAt(net, junction, arm);
                bool drawnRight = end == LinkEnd.End;
                var streetRight = streetSideAt(arm.LinkId, end, drawnRight);
                var streetLeft = streetSideAt(arm.LinkId, end, !drawnRight);
                // near the mouth: at the kerb ends, 0.5 m out (diagonal where a tight corner pulled one in); further out: where the node is
                bool atKerb = at <= CrossingAtKerb;
                double far = atKerb ? 0.5 + ZebraDepth + ZebraClear : at + ZebraDepth * 0.5 + ZebraClear;
                int before = stats.Crossings;
                EmitCrossing(paint, source, mid, u, right, far, lo, hi, streetRight, streetLeft, areas, stats,
                    insetLeft: atKerb ? junction.KerbInset.GetValueOrDefault((i, false)) : 0,
                    insetRight: atKerb ? junction.KerbInset.GetValueOrDefault((i, true)) : 0);
                if (stats.Crossings > before) drawn++;
            }
        }
        return drawn;
    }
}
