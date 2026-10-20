namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// The junction planner's output written into the tiles (#818 phase 3, under <c>--junctions new</c>; the user's decision, Oct 13
/// 2026): at every planned single-node junction whose checks pass the core is its cap, the arms' streets are trimmed (or run on)
/// to the core's mouths, and the pieces each transition covers carry <see cref="RoadAttrFlags.InTransition"/> with their
/// <c>TRNS</c> records and the transition's paint; the street's own paint is laid again on what is left of it. The old pocket
/// layout and the exterior (stop lines, crosswalks, poles, corners, signs) are not built there: phase 4 lays them from the core.
/// </summary>
public static partial class TileRewriter
{
    /// <summary>What the planner wrote, for the region report.</summary>
    public sealed class LayoutWriteStats
    {
        public int Junctions, Skipped, Transitions, Pieces, Lines, Marks, Trimmed, Extended, Orphaned, CoreVertices, CapVertices, Bars, Refuges, Signs, Ramps, Signals, SignalsInvalid, Poles, CoreLines, BikeCrossings, PathCrossings, PathsCarried, PathsThrough, PathsCrossingInCorner, CornersLanded, Paths, PathsLeftOut, PathRides, InnerEmptied, Aprons, Fallbacks, RingsAsLaid, Caps;
        /// <summary>Written junctions with a transition (LV95), for the seam check and pictures.</summary>
        public readonly List<Vec2> WithTransitions = new();
        public readonly List<Vec2> OrphanedAt = new();
        /// <summary>The merged cores written (#818 phase 5c) and their node counts.</summary>
        public readonly List<(Vec2 At, int Nodes)> MergedAt = new();
        /// <summary>The roundabouts written whole (#854).</summary>
        public readonly List<Vec2> RingsAt = new();
        public readonly SortedDictionary<string, int> Why = new(StringComparer.Ordinal);

        public string Format() => string.Create(CultureInfo.InvariantCulture,
            $"    junction planner written (#818 phase 3): {Junctions:N0} junctions as cores ({Aprons:N0} flush aprons of service roads; {Fallbacks:N0} plain cores where the planned one failed its checks, {RingsAsLaid:N0} rings written as laid, {Caps:N0} today's caps), {Skipped:N0} left to the old layout ({string.Join(", ", Why.Select(kv => $"{kv.Key} {kv.Value}"))}); ")
            + string.Create(CultureInfo.InvariantCulture, $"{Transitions:N0} transitions over {Pieces:N0} pieces, {Lines:N0} lines and {Marks:N0} marks; arms trimmed to their mouth {Trimmed:N0}, run on to it {Extended:N0}; pieces lost by a later cut {Orphaned:N0}{(OrphanedAt.Count > 0 ? " (at " + string.Join("; ", OrphanedAt.Take(6).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.X:F0},{v.Y:F0}"))) + ")" : "")}; cap vertices {CapVertices:N0} of the cores' {CoreVertices:N0}; exterior: {Bars:N0} bars, {Refuges:N0} refuges, {Signs:N0} signs, {Ramps:N0} crosswalks with lowered kerbs; lights {Signals:N0} ({SignalsInvalid:N0} plans invalid), {Poles:N0} poles, {CoreLines:N0} lines through the cores, {BikeCrossings:N0} bike lanes through them, {PathCrossings:N0} path crossings ({PathsCarried:N0} ends carried to the kerb, {PathsCrossingInCorner:N0} of them crossing another in a corner: left to it), {PathsThrough:N0} paths carried along a kerb, {CornersLanded:N0} corners a path crossing ends on; paths for traffic (JPTH) {Paths:N0} ({PathRides:N0} riders' ways from path to path), left out {PathsLeftOut:N0}; merged cores written {MergedAt.Count:N0} (of {MergedAt.Sum(m => m.Nodes):N0} nodes{(MergedAt.Count > 0 ? "; at " + string.Join(" ", MergedAt.GroupBy(m => m.Nodes).OrderBy(g => g.Key).SelectMany(g => g.OrderBy(m => m.At.X).Take(4)).Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.At.X:F0},{m.At.Y:F0}x{m.Nodes}"))) : "")}), streets under them {InnerEmptied:N0}; roundabouts written whole {RingsAt.Count:N0}{(RingsAt.Count > 0 ? " (at " + string.Join(" ", RingsAt.OrderBy(v => v.X).Take(8).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.X:F0},{v.Y:F0}"))) + ")" : "")} (#854)")
            + (WithTransitions.Count > 0 ? "\n      with transitions at " + string.Join("; ", WithTransitions.Take(12).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.X:F0},{v.Y:F0}"))) : "");
    }

    /// <summary>One piece of a street from a junction outward: its link, tile, whole segment and piece, and whether it is drawn toward the junction.</summary>
    private sealed record StreetPiece(int Link, TileId Tile, RoadSegment Whole, RoadSegment Piece, bool TowardJunction)
    {
        public double Length => RoadPaintGeometry.Length(Piece.Points);
    }

    /// <summary>A polyline (x, y, z per point) with a point added at each distance along it (plan metres) where none lies within 0.2 m.</summary>
    private static float[] WithPointsAt(float[] points, IEnumerable<double> distances)
    {
        var list = new List<float>(points);
        foreach (double d in distances.OrderByDescending(x => x))
        {
            double run = 0;
            for (int i = 0; i + 1 < list.Count / 3; i++)
            {
                float x0 = list[i * 3], z0 = list[i * 3 + 2], x1 = list[i * 3 + 3], z1 = list[i * 3 + 5];
                double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                if (run + len < d) { run += len; continue; }
                double f = len < 1e-6 ? 0 : (d - run) / len;
                if (f * len > 0.2 && (1 - f) * len > 0.2)
                    list.InsertRange(i * 3 + 3, [(float)(x0 + (x1 - x0) * f), (float)(list[i * 3 + 1] + (list[i * 3 + 4] - list[i * 3 + 1]) * f), (float)(z0 + (z1 - z0) * f)]);
                break;
            }
        }
        return list.ToArray();
    }

    /// <summary>Whether an arriving bike lane lies between two arriving car lanes at a mouth (a through bike lane beside a right pocket).</summary>
    private static bool BikeBetweenCars(Section s)
    {
        for (int k = 1; k + 1 < s.Strips.Count; k++)
            if (s.Strips[k] is { Kind: StripKind.BikeLane, Way: StripWay.In } && s.Strips[k - 1] is { IsCarLane: true, Way: StripWay.In }
                && s.Strips[k + 1] is { IsCarLane: true, Way: StripWay.In } && (s.Strips[k].Moves & SignalMoves.Through) != 0) return true;
        return false;
    }

    /// <summary>
    /// The junctions the planner may write: a core with its checks passed (kerbs inside the mouths, connectors inside the
    /// outline, an outline not crossing itself), every arm a car road of the block, no core overlapping its neighbour's (a merged
    /// core is phase 5), every street a transition runs on in the block and at grade, and the far junction of every transition shared
    /// with one written too.
    /// </summary>
    private static HashSet<int> WritableLayouts(List<JunctionPlan> plans, RoadGenResult result, Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf,
        LayoutWriteStats stats)
    {
        var junctions = result.Junctions.ToDictionary(j => j.NodeId);
        var why = new Dictionary<int, string>();
        foreach (var jp in plans)
        {
            string? no = null;
            // a ring its builder refuses (an arm running past it): laid as any junction instead (#854)
            if (jp.Core is { Refused: not null } && jp.Merged?.Ring is not null)
            {
                jp.Core = CorePlanner.Plan(jp);
                CorePlanner.Lift(jp.Core, m => result.Network.Links[jp.Arms.First(a => a.Arm == m.Arm).LinkId].Tag is Source source
                    ? source.SampleHeight((m.Left + m.Right) * 0.5) : 0);
            }
            // a core failing its checks: a ring written as laid (its island, its teeth), any other as a plain core from its mouths
            // (#854, the user, Oct 20: so the old layout can go)
            if (jp.Core is { Refused: null } failing && jp.CoreFails)
            {
                if (jp.Merged?.Ring is not null)
                {
                    failing.AsLaid = true;
                    if (!jp.Beyond) stats.RingsAsLaid++;
                }
                else
                {
                    jp.Core = CorePlanner.Fallback(jp, failing, a => JoinLimit(result, a));
                    CorePlanner.Lift(jp.Core, m => result.Network.Links[jp.Arms.First(a => a.Arm == m.Arm).LinkId].Tag is Source source
                        ? source.SampleHeight((m.Left + m.Right) * 0.5) : 0);
                    if (!jp.Beyond) stats.Fallbacks++;
                }
            }
            if (jp.Core is not { } core) no = "no core";
            else if (core.Refused is { } refused) no = refused;
            // two junctions under InternalLink apart stay two cores where both fit on the link between (the user, Oct 15), held at
            // half of it where they wanted more (Oct 17); a group where that fails a check was merged (TileRewriter.Merge), or
            // could not be: "cores overlap"
            else if (jp.CoresOverlap && jp.CoreFails)
            {
                no = "cores overlap";
                if (Environment.GetEnvironmentVariable("OVERLAPDBG") is not null && !jp.Beyond)
                    Console.WriteLine(FormattableString.Invariant($"[overlap] {jp.Centre.X:F0},{jp.Centre.Y:F0} merged {jp.Merged is not null} local {jp.Local} arms {jp.Arms.Count} kerbs {core.KerbsNotTangent} outside {core.ConnectorsOutside} simple {core.Simple}"));
            }
            else if (jp.CoreFails)
                no = jp.Arms.Any(a => a.Internal) ? "a check failed next to a junction under 30 m" : "a check failed";
            // (a path meeting a roundabout's node is its crossing's: no arm of it, #854)
            else if ((jp.Merged ?? junctions.GetValueOrDefault(jp.Node)) is not { } j
                || (j.Ring is null ? Directions(j.Arms.Where(x => !Degenerate(result.Network.Links[x.LinkId])).Select(x => x.OutwardHeading))
                    : j.Arms.Count(x => InfoOf(result.Network.Links[x.LinkId]) is { } i && Junctions.PriorityPlanner.IsCarRoad(i.Class)))
                    != (j.Ring is null ? Directions(jp.Arms.Select(x => x.Heading)) : jp.Arms.Count))
                no = "a path or track joins";
            else if (jp.Arms.Any(a => a.Axis is null || !core.Mouths.Any(m => m.Arm == a.Arm))) no = "an arm without a mouth";
            // a road joining an arm short of its mouth: a plain core, its mouths held in short of it (#854, the user, Oct 20)
            else if (jp.Merged?.Ring is null && !core.IsFallback && jp.Arms.Any(a => RoadJoinsShortOfMouth(result, a, core.Mouths.First(m => m.Arm == a.Arm).Station))
                && CorePlanner.Fallback(jp, core, a => JoinLimit(result, a)) is { Simple: true } held
                && !jp.Arms.Any(a => RoadJoinsShortOfMouth(result, a, held.Mouths.First(m => m.Arm == a.Arm).Station)))
            {
                jp.Core = held;
                CorePlanner.Lift(jp.Core, m => result.Network.Links[jp.Arms.First(a => a.Arm == m.Arm).LinkId].Tag is Source source
                    ? source.SampleHeight((m.Left + m.Right) * 0.5) : 0);
                if (!jp.Beyond) stats.Fallbacks++;
                if (jp.Arms.Any(MissesMouth)) no = "a transition misses its mouth";
            }
            else if (jp.Arms.Any(a => RoadJoinsShortOfMouth(result, a, core.Mouths.First(m => m.Arm == a.Arm).Station)))
            {
                no = "a road joins short of a mouth";
                if (Environment.GetEnvironmentVariable("JOINDBG") is not null && !jp.Beyond)
                    foreach (var a in jp.Arms)
                        if (NodeShortOfMouth(result, a, core.Mouths.First(m => m.Arm == a.Arm).Station) is int node)
                        {
                            var n = result.Network.Nodes[node];
                            var kinds = n.Approaches.Select(x => InfoOf(result.Network.Links[x.LinkId]) is { } i ? (Junctions.PriorityPlanner.IsCarRoad(i.Class) ? "car" : i.Class.ToString()) : "?");
                            Console.WriteLine(FormattableString.Invariant($"[join] {jp.Centre.X:F0},{jp.Centre.Y:F0} merged {jp.Merged is not null} node {n.Position.X:F0},{n.Position.Y:F0} at {a.Axis!.StationPast(n.Position):F1} of mouth {core.Mouths.First(m => m.Arm == a.Arm).Station:F1}: {string.Join(" ", kinds)} planned {plans.Any(p => p.Nodes.Contains(node))}"));
                        }
            }
            else if (jp.Arms.Any(MissesMouth)) no = "a transition misses its mouth";
            else
                foreach (var a in jp.Arms)
                    foreach (int link in a.StreetOn.Select(x => x.Link).Prepend(a.LinkId))
                        if (!segmentOf.TryGetValue(link, out var so)) no ??= "a street beyond the block";
                        // (a transition over a bridge or tunnel is not written yet; an arm keeping its normal section has none: its
                        // bridge is only trimmed to the mouth, #854)
                        else if ((so.Segment.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0 && a.Transition is not null) no ??= "a bridge or tunnel";
            // what no plain core can hold (a road joining right at the node, a path's stub meeting it): today's cap, written by the
            // planner (#854, the user, Oct 20)
            if (no is "a road joins short of a mouth" or "a path or track joins" && jp.Merged is null && junctions.GetValueOrDefault(jp.Node) is { } today
                && CorePlanner.Cap(jp, today, jp.Core?.Connectors ?? []) is { Simple: true } capCore)
            {
                jp.Core = capCore;
                CorePlanner.Lift(jp.Core, m => result.Network.Links[jp.Arms.First(a => a.Arm == m.Arm).LinkId].Tag is Source source
                    ? source.SampleHeight((m.Left + m.Right) * 0.5) : 0);
                if (!jp.Beyond) stats.Caps++;
                no = null;
            }
            // (a ring or a merged core a road joins right at, or a stub meets: written as laid, the joining node's own cap meeting
            // its mouth as today's caps' do, a stub's end under its paving)
            if (no is "a road joins short of a mouth" or "a path or track joins" && jp.Merged is not null && jp.Core is { } ringCore)
            {
                ringCore.AsLaid = true;
                if (!jp.Beyond) stats.RingsAsLaid++;
                no = null;
            }
            if (no is not null && jp.Merged?.Ring is not null) no = "a roundabout: " + no;
            if (no is not null) why[jp.Node] = no;
            // (WHYDBG=1: every junction left to the old layout, where and why)
            if (no is not null && !jp.Beyond && Environment.GetEnvironmentVariable("WHYDBG") is not null)
                Console.WriteLine(FormattableString.Invariant($"[why] {jp.Centre.X:F0},{jp.Centre.Y:F0} {jp.Arms.Count} arms{(jp.Merged is { Ring: null } ? " merged" : "")}{(jp.Local ? " local" : "")}: {no}")
                    + (no == "a path or track joins" && (jp.Merged ?? junctions.GetValueOrDefault(jp.Node)) is { } jj
                        ? " (" + string.Join(" ", jj.Arms.Select(x => InfoOf(result.Network.Links[x.LinkId])?.Class.ToString() ?? "?")) + ")" : ""));
            if (no is not null && DebugJunction is { } dbg && Math.Abs(dbg.E - jp.Centre.X) < 30 && Math.Abs(dbg.N - jp.Centre.Y) < 30)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[junction] node {jp.Node} at {jp.Centre.X:F1},{jp.Centre.Y:F1} left to the old layout: {no}"
                    + $" (links {string.Join(" ", jp.Arms.SelectMany(a => a.StreetOn.Select(x => x.Link).Prepend(a.LinkId)).Select(l => segmentOf.ContainsKey(l) ? $"{l}" : $"{l}?"))})"));
        }
        var ok = plans.Where(p => !why.ContainsKey(p.Node)).Select(p => p.Node).ToHashSet();
        var planOf = plans.SelectMany(p => p.Arms.Select(a => (a, p))).ToDictionary(x => x.a, x => x.p);
        // a transition shared with the junction beyond is written by both or neither
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var a in plans.SelectMany(p => p.Arms))
                if (a.Transition is { Far: { } far } t && planOf.TryGetValue(t.Arm, out var pa) && planOf.TryGetValue(far, out var pb)
                    && ok.Contains(pa.Node) != ok.Contains(pb.Node))
                {
                    int drop = ok.Contains(pa.Node) ? pa.Node : pb.Node;
                    ok.Remove(drop);
                    why[drop] = "the junction beyond is not written";
                    changed = true;
                }
        }
        // (the block's own junctions counted: one planned for what it lays in the block is its own block's)
        var beyond = plans.Where(p => p.Beyond).Select(p => p.Node).ToHashSet();
        foreach (var (node, w) in why)
        {
            if (beyond.Contains(node)) continue;
            stats.Why[w] = stats.Why.GetValueOrDefault(w) + 1;
            stats.Skipped++;
        }
        // a merged core is written for all its members: the old layout lays none of them (#818 phase 5c)
        foreach (var p in plans.Where(p => p.Merged is not null && ok.Contains(p.Node)).ToList()) ok.UnionWith(p.Nodes);
        return ok;
    }

    /// <summary>
    /// Another road joins the arm's street short of its mouth (the user, Oct 14 2026): a node the street runs on through within
    /// <see cref="JoinMargin"/> past the mouth where anything else meets it (a road dividing, a link, a track). The junction spans
    /// several nodes: the old layout keeps it until phase 5 plans such cores whole.
    /// </summary>
    private static bool RoadJoinsShortOfMouth(RoadGenResult result, ArmPlan a, double mouth) => NodeShortOfMouth(result, a, mouth) is not null;

    /// <summary>The node <see cref="RoadJoinsShortOfMouth"/> finds: the first along the arm's street where anything else joins.</summary>
    private static int? NodeShortOfMouth(RoadGenResult result, ArmPlan a, double mouth)
    {
        var net = result.Network;
        var l0 = net.Links[a.LinkId];
        var nodes = new List<(int Link, int Node)> { (a.LinkId, a.AtEnd ? l0.StartNode : l0.EndNode) };
        foreach (var (link, atEnd) in a.StreetOn) { var l = net.Links[link]; nodes.Add((link, atEnd ? l.StartNode : l.EndNode)); }
        // (a street looping back to the junction's own node is no road joining it)
        int self = a.AtEnd ? l0.EndNode : l0.StartNode;
        foreach (var (link, node) in nodes)
        {
            if (node < 0 || node >= net.Nodes.Count) break;
            if (node == self) continue;
            if (a.Axis!.StationPast(net.Nodes[node].Position) > mouth + JoinMarginAt(result, link, node)) break;
            if (net.Nodes[node].Degree > 2) return node;
        }
        return null;
    }

    /// <summary>
    /// A car lane of the arm's mouth whose middle lies on no car lane where its transition starts (#818: two pockets merged back to
    /// back across a short street, <c>TransitionPlanner.Joined</c>, can end on a hatch there): the core's paths would leave off a lane.
    /// </summary>
    private static bool MissesMouth(ArmPlan a)
    {
        if (a.Transition is null) return false;
        static List<(double From, double To)> Lanes(Section s) =>
            Enumerable.Range(0, s.Strips.Count).Where(k => s.Strips[k].IsCarLane && s.Strips[k].Width > 0.1).Select(s.Span).ToList();
        var got = Lanes(ExteriorPlanner.SectionAt(a, 0));
        return Lanes(a.Mouth).Any(l => !got.Any(g => (l.From + l.To) * 0.5 > g.From + 0.05 && (l.From + l.To) * 0.5 < g.To - 0.05));
    }

    /// <summary>A link from a node back to itself under a metre long (a stitch left on a tile's border): no arm.</summary>
    private static bool Degenerate(RoadLink l) => l.StartNode == l.EndNode && PolylineLength(l.Centreline) < 1;

    /// <summary>How many directions a junction's arms leave in (within 2 degrees one).</summary>
    private static int Directions(IEnumerable<double> headings)
    {
        var kept = new List<double>();
        foreach (double h in headings)
            if (!kept.Any(k => Math.Abs(Angles.Normalize(h - k)) < 2 * Math.PI / 180)) kept.Add(h);
        return kept.Count;
    }

    /// <summary>
    /// How far out an arm's mouth may stand: short of the first node along its street where anything else joins it, by that node's
    /// join margin (as <see cref="NodeShortOfMouth"/> reckons it); unbounded where none does.
    /// </summary>
    private static double JoinLimit(RoadGenResult result, ArmPlan a)
    {
        var net = result.Network;
        var l0 = net.Links[a.LinkId];
        var nodes = new List<(int Link, int Node)> { (a.LinkId, a.AtEnd ? l0.StartNode : l0.EndNode) };
        foreach (var (link, atEnd) in a.StreetOn) { var l = net.Links[link]; nodes.Add((link, atEnd ? l.StartNode : l.EndNode)); }
        int self = a.AtEnd ? l0.EndNode : l0.StartNode;
        foreach (var (link, node) in nodes)
        {
            if (node < 0 || node >= net.Nodes.Count) break;
            if (node == self || net.Nodes[node].Degree <= 2) continue;
            return a.Axis!.StationPast(net.Nodes[node].Position) - JoinMarginAt(result, link, node) - 0.1;
        }
        return double.MaxValue;
    }

    /// <summary>A road joining an arm's street this far past its mouth (m, its crosswalk's reach) still joins the junction.</summary>
    private const double JoinMargin = 4.0;

    /// <summary>A slip lane (TLM's Verbindung) is its own roadway: one leaving or joining this far past a mouth (m) is outside it (#854).</summary>
    private const double SlipMargin = 1.0;

    /// <summary>
    /// The join margin at a node the street reaches along <paramref name="link"/>: where only slip lanes join it there,
    /// <see cref="SlipMargin"/> or the node's own cap's trim on the street (its lanes stop there), else <see cref="JoinMargin"/>.
    /// </summary>
    private static double JoinMarginAt(RoadGenResult result, int link, int node)
    {
        var net = result.Network;
        bool slip = net.Nodes[node].Approaches.All(x => InfoOf(net.Links[x.LinkId]) is { } i && (Junctions.PriorityPlanner.IsCarRoad(i.Class) || i.Class == RoadClass.Link))
            && net.Nodes[node].Approaches.Count(x => InfoOf(net.Links[x.LinkId]) is { } i && Junctions.PriorityPlanner.IsCarRoad(i.Class)) <= 2;
        if (!slip) return JoinMargin;
        var caps = CapsOf.GetValue(result, r => r.Junctions.GroupBy(j => j.NodeId).ToDictionary(g => g.Key, g => g.First()));
        double trim = caps.TryGetValue(node, out var cap) ? cap.Arms.Where(x => x.LinkId == link).Select(x => x.Trim).DefaultIfEmpty(0).Max() : 0;
        return Math.Max(SlipMargin, trim + 0.5);
    }

    /// <summary>Today's caps by node, per result (<see cref="JoinMarginAt"/>).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoadGenResult, Dictionary<int, Junctions.Junction>> CapsOf = new();

    /// <summary>A tile's transitions whose segment it still holds (a check: any other is counted and left out, its piece drawn plain).</summary>
    private static List<RoadTransition> Placed(TileId id, List<RoadTransition>? transitions, List<RoadSegment> segments, LayoutWriteStats stats)
    {
        if (transitions is null || transitions.Count == 0) return [];
        var held = new HashSet<RoadSegment>(segments, ReferenceEqualityComparer.Instance);
        var placed = transitions.Where(t => held.Contains(t.Segment)).ToList();
        stats.Orphaned += transitions.Count - placed.Count;
        foreach (var t in transitions.Where(t => !held.Contains(t.Segment)))
            stats.OrphanedAt.Add(new Vec2(id.MinE + t.Segment.Points[0], id.MaxN - t.Segment.Points[2]));
        return placed;
    }

    /// <summary>The priority plans without the junctions the planner writes: what the old passes lay out.</summary>
    private static PriorityResult WithoutWritten(PriorityResult priority, HashSet<int> written)
    {
        if (written.Count == 0) return priority;
        var rest = new PriorityResult();
        rest.Plans.AddRange(priority.Plans.Where(p => !written.Contains(p.Junction.NodeId)));
        foreach (var (k, v) in priority.Yield) rest.Yield[k] = v;
        rest.SignalsFromData.UnionWith(priority.SignalsFromData);
        return rest;
    }

    /// <summary>
    /// Writes the planner's junctions (<see cref="WritableLayouts"/>): caps, trims, transitions and their paint. Runs once the
    /// street pieces are final and before the bike lanes are painted on them (they skip a transition's pieces: its paint has them).
    /// </summary>
    private static void WriteLayouts(List<JunctionPlan> plans, HashSet<int> written, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf, Dictionary<RoadSegment, List<RoadSegment>> finalPieces,
        Dictionary<TileId, List<RoadSegment>> output, Dictionary<int, (TileId Home, RoadJunction Cap)> capOf, Dictionary<TileId, List<RoadJunction>> caps,
        HashSet<RoadJunction> writtenCaps, Dictionary<TileId, List<RoadPaint>> paint, Dictionary<int, (double Station, bool Start, bool End)> paintStation,
        Dictionary<TileId, List<RoadTransition>> transitionsOf, Dictionary<TileId, List<RoadAreaProp>> areas, Dictionary<TileId, List<RoadPointProp>> signs,
        Dictionary<TileId, List<RoadSignal>> signals, Dictionary<TileId, List<RoadApproach>> approaches, Cantons? cantons, SignalStats signalStats, LayoutWriteStats stats,
        Dictionary<TileId, Dictionary<RoadJunction, List<CornerPlanner.CornerInput>>> coreCorners, Dictionary<TileId, List<CornerPlanner.PathEnd>> pathEnds,
        Dictionary<TileId, List<(RoadAreaProp Band, List<Vec2> Ring)>> bridges, Dictionary<TileId, List<RoadJunctionPath>> pathsOf)
    {
        var net = result.Network;
        // in a fixed order, whichever block writes them (#818: a tile's records alike from every block, its nodes numbered apart)
        var mine = plans.Where(p => written.Contains(p.Node)).OrderBy(p => p.Centre.X).ThenBy(p => p.Centre.Y).ToList();
        var touched = new HashSet<int>();
        var capOfPlan = new Dictionary<JunctionPlan, (TileId Home, RoadJunction Cap)>();
        // (a link the mouth passed whole has none left: it lies under the core)
        List<RoadSegment> PiecesOf(RoadSegment whole) => finalPieces.TryGetValue(whole, out var p) ? p : [whole];
        // the street from an arm's junction outward, piece by piece
        List<StreetPiece> Street(ArmPlan a)
        {
            var list = new List<StreetPiece>();
            foreach (var (link, atEnd) in a.StreetOn.Prepend((a.LinkId, a.AtEnd)))
            {
                var so = segmentOf[link];
                var pieces = PiecesOf(so.Segment);
                // junction at the link's last point: drawn toward it, so the pieces run backwards from it
                foreach (var piece in atEnd ? Enumerable.Reverse(pieces) : pieces) list.Add(new StreetPiece(link, so.Tile, so.Segment, piece, atEnd));
            }
            return list;
        }
        // replaces a whole's pieces in its tile and in finalPieces
        void Replace(RoadSegment whole, TileId tile, List<RoadSegment> pieces)
        {
            var old = PiecesOf(whole);
            var list = output[tile];
            if (old.Count == 0) return;
            int first = list.FindIndex(x => ReferenceEquals(x, old[0]));
            if (first < 0) return;
            list.RemoveRange(first, old.Count);
            list.InsertRange(first, pieces);
            finalPieces[whole] = pieces;
        }
        static RoadSegment Copy(RoadSegment s, float[] points, RoadAttributes? attributes = null) => new()
        {
            Class = s.Class, Surface = s.Surface, Flags = s.Flags, Width = s.Width, Points = points, Attributes = attributes ?? s.Attributes,
        };

        // (phase 5, #709) every connector of a written core as a path for traffic (JPTH): from its lane at its arm's mouth to the exit's,
        // each end named by the arm's piece at the mouth and the lane's offset there, its points on the cap's surface
        void WritePaths(JunctionPlan jp, TileId home, RoadJunction cap, SignalPlan? plan, short signalIndex, IEnumerable<Connector> connectors)
        {
            var core = jp.Core!;
            int PlanArm(int arm) => jp.Arms.FindIndex(x => x.Arm == arm);
            int GroupOf(int arm, SignalMoves move, ConnectorKind kind)
            {
                if (plan is null) return -1;
                int a = PlanArm(arm);
                bool bike = kind is ConnectorKind.Bike or ConnectorKind.Path;
                int g = plan.Groups.FindIndex(x => x.Arm == a && (bike ? x.Kind == SignalGroupKind.Bike : x.Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow) && (x.Moves & move) != 0);
                if (g < 0 && bike) g = plan.Groups.FindIndex(x => x.Arm == a && x.Kind == SignalGroupKind.Car && (x.Moves & SignalMoves.Through) != 0);
                return g;
            }
            // an end: where the arm's road meets the mouth, on its centreline (the axis), the lane's offset right of a driver arriving
            JunctionPathEnd EndOf(int arm, double offset)
            {
                var a = jp.Arms.First(x => x.Arm == arm);
                var p = a.Axis!.PointAt(core.Mouths.First(m => m.Arm == arm).Station);
                return new JunctionPathEnd((float)(p.X - home.MinE), CapHeightAt(cap, home, p), (float)(home.MaxN - p.Y), (float)offset);
            }
            foreach (var c in connectors)
            {
                if (c.Centre.Count < 2) { stats.PathsLeftOut++; continue; }
                var points = RoadPaintGeometry.Simplify(Local(home, c.Centre, p => CapHeightAt(cap, home, p), 0f));
                var from = EndOf(c.FromArm, c.FromOffset);
                var to = EndOf(c.ToArm, c.ToOffset);
                int g = GroupOf(c.FromArm, c.Move, c.Kind);
                Get(pathsOf, home).Add(new RoadJunctionPath
                {
                    Junction = cap, From = from, To = to, Move = c.Move,
                    Kind = c.Kind switch { ConnectorKind.Bike => JunctionPathKind.Bike, ConnectorKind.Path => JunctionPathKind.Path, _ => JunctionPathKind.Car },
                    Signal = g >= 0 ? signalIndex : (short)-1, Group = (byte)Math.Max(0, g), Width = (float)c.Width, ToWidth = (float)c.ToWidth, Points = points,
                });
                stats.Paths++;
            }
        }

        // 1. the cores as caps
        foreach (var jp in mine)
        {
            var core = jp.Core!;
            if (!capOf.TryGetValue(jp.Node, out var old) || core.Outline.Count < 3 || core.Outline.Count > ushort.MaxValue || core.Triangles.Count < 3) continue;
            var home = old.Home;
            // the outline as the cap keeps it: its kerbs sampled every half metre simplified to the tolerance of the sides (the
            // mouths' corners kept, where the ribbons meet it), triangulated again
            var keep = SimplifiedRing(core.Outline, core.Heights, core.Mouths.SelectMany(m => new[] { m.Left, m.Right }).ToList());
            var ring = keep.Select(i => core.Outline[i]).ToList();
            var triangles = keep.Count == core.Outline.Count ? core.Triangles : Junctions.EarClip.Triangulate(ring);
            if (triangles.Count < 3) (keep, ring, triangles) = (Enumerable.Range(0, core.Outline.Count).ToList(), core.Outline, core.Triangles);
            var vertices = new float[ring.Count * 3];
            for (int i = 0; i < ring.Count; i++)
            {
                vertices[i * 3] = (float)(ring[i].X - home.MinE);
                vertices[i * 3 + 1] = (float)core.Heights[keep[i]];
                vertices[i * 3 + 2] = (float)(home.MaxN - ring[i].Y);
            }
            stats.CoreVertices += core.Outline.Count;
            stats.CapVertices += ring.Count;
            var cap = new RoadJunction { Class = old.Cap.Class, Layer = old.Cap.Layer, Vertices = vertices, Indices = triangles.Select(i => (ushort)i).ToArray() };
            var list = caps[home];
            list[list.IndexOf(old.Cap)] = cap;
            writtenCaps.Add(cap);
            capOfPlan[jp] = (home, cap);
            if (jp.Merged is not null) stats.MergedAt.Add((jp.Centre, jp.Nodes.Count()));
            // a merged core's other members have no cap of their own: the core covers them
            foreach (int node in jp.Nodes)
                if (node != jp.Node && capOf.TryGetValue(node, out var member)) caps[member.Home].Remove(member.Cap);
            stats.Junctions++;
            // a roundabout laid whole (#854): its splitter islands on the cap, its central island for the ring as laid (wider than
            // TLM's line, so the shaper's island is replaced)
            foreach (var splitter in core.Islands)
            {
                var tris = Junctions.EarClip.Triangulate(splitter);
                if (tris.Count < 3) continue;
                Get(areas, home).Add(new RoadAreaProp
                {
                    Type = AreaPropType.Island, Variant = 2, Flags = PropFlags.Solid, Height = RoundaboutShaper.IslandKerb,
                    Vertices = splitter.SelectMany(p => new[] { (float)(p.X - home.MinE), CapHeightAt(cap, home, p), (float)(home.MaxN - p.Y) }).ToArray(),
                    Indices = tris.Select(i => (ushort)i).ToArray(),
                });
                stats.Refuges++;
            }
            if (jp.Merged?.Ring is { } shaped)
            {
                stats.RingsAt.Add(shaped.Centre);
                var at = TileId.FromLv95(shaped.Centre.X, shaped.Centre.Y);
                if (areas.TryGetValue(at, out var props))
                    props.RemoveAll(x => x.Type == AreaPropType.Island && x.Variant is 0 or 1 && x.Vertices.Length >= 3
                        && Math.Abs(x.Vertices[0] + at.MinE - shaped.Centre.X) < 1.5 && Math.Abs(at.MaxN - x.Vertices[2] - shaped.Centre.Y) < 1.5);
                if (RoundaboutShaper.Island(shaped with { Width = RoundaboutCore.RingWidth(shaped.Width) }, at) is { } centre) Get(areas, at).Add(centre);
            }
            if (JunctionList is { } listFile)
                File.AppendAllText(listFile, string.Create(CultureInfo.InvariantCulture,
                    $"{jp.Centre.X:F0},{jp.Centre.Y:F0} arms {jp.Arms.Count} lights {(jp.Lights ? 1 : 0)} left {jp.Arms.Count(a => a.HasLeft)} right {jp.Arms.Count(a => a.HasRight)} split {jp.Arms.Count(a => Math.Abs(a.Split) > 0.01)} bikelanes {jp.Arms.Count(a => a.Mouth.Strips.Any(x => x.Kind == StripKind.BikeLane))} paths {jp.Arms.Count(a => a.Mouth.Strips.Any(x => x.Kind == StripKind.Path))} nodes {jp.Nodes.Count()} between {jp.Arms.Count(a => BikeBetweenCars(a.Mouth))}\n"));
        }

        // a piece run on toward the node along its arm's axis from station <from> (its junction end) to <to> (the mouth), a point a
        // metre, its heights from its line's own; null where its line has no source
        float[]? RunOn(ArmPlan a, StreetPiece sp, double from, double to)
        {
            if (net.Links[sp.Link].Tag is not Source source) return null;
            var p = sp.Piece.Points;
            var extra = new List<float>();
            for (double s = to; s < from - 0.05; s += 1)
            {
                var q = a.Axis!.PointAt(s);
                extra.Add((float)(q.X - sp.Tile.MinE)); extra.Add(source.SampleHeight(q)); extra.Add((float)(sp.Tile.MaxN - q.Y));
            }
            // the junction end first, outward: drawn toward the junction they go after the piece's points, reversed
            if (!sp.TowardJunction) return [.. extra, .. p];
            var back = new List<float>(p);
            for (int k = extra.Count - 3; k >= 0; k -= 3) back.AddRange(extra.Skip(k).Take(3));
            return back.ToArray();
        }

        // 2. every arm's street trimmed to its mouth, or run on to it along the axis
        foreach (var jp in mine)
            foreach (var a in jp.Arms)
            {
                double mouth = jp.Core!.Mouths.First(m => m.Arm == a.Arm).Station;
                var street = Street(a);
                if (street.Count == 0) continue;
                var first = street[0];
                var p = first.Piece.Points;
                int endAt = first.TowardJunction ? p.Length - 3 : 0;
                var end = new Vec2(first.Tile.MinE + p[endAt], first.Tile.MaxN - p[endAt + 2]);
                double delta = mouth - a.Axis!.StationPast(end);
                touched.Add(a.LinkId);
                if (delta > 0.01)
                {
                    // cut off the junction end, piece by piece, each at the mouth's station along the axis (a street run on through a
                    // node has a gap there, today's cap for that node: lengths alone cut that much too far)
                    var byWhole = new Dictionary<RoadSegment, List<RoadSegment>>(ReferenceEqualityComparer.Instance);
                    foreach (var sp in street)
                    {
                        var v = sp.Piece.Points;
                        int nearAt = sp.TowardJunction ? v.Length - 3 : 0;
                        double sNear = a.Axis.StationPast(new Vec2(sp.Tile.MinE + v[nearAt], sp.Tile.MaxN - v[nearAt + 2]));
                        var pieces = byWhole.TryGetValue(sp.Whole, out var got) ? got : byWhole[sp.Whole] = PiecesOf(sp.Whole).ToList();
                        int at = pieces.FindIndex(x => ReferenceEquals(x, sp.Piece));
                        if (sNear >= mouth - 0.01)
                        {
                            // past a node the street was cut back for (today's cap there): run on back to the mouth
                            if (sNear > mouth + 0.05 && RunOn(a, sp, sNear, mouth) is { } on) { pieces[at] = Copy(sp.Piece, on); touched.Add(sp.Link); }
                            break;
                        }
                        double len = sp.Length, left = mouth - sNear;
                        touched.Add(sp.Link);
                        if (len <= left + 0.05) { pieces.RemoveAt(at); continue; }
                        var points = sp.TowardJunction ? StreetPlanner.Slice(sp.Piece, 0, len - left) : StreetPlanner.Slice(sp.Piece, left, double.PositiveInfinity);
                        pieces[at] = Copy(sp.Piece, points);
                        break;
                    }
                    // (a link the mouth passes whole goes: it lies under the core)
                    foreach (var (whole, pieces) in byWhole)
                        Replace(whole, segmentOf[street.First(x => ReferenceEquals(x.Whole, whole)).Link].Tile, pieces);
                    stats.Trimmed++;
                }
                else if (delta < -0.01 && RunOn(a, first, mouth - delta, mouth) is { } points)
                {
                    // run on toward the node along the axis
                    var pieces = PiecesOf(first.Whole).ToList();
                    pieces[pieces.FindIndex(x => ReferenceEquals(x, first.Piece))] = Copy(first.Piece, points);
                    Replace(first.Whole, first.Tile, pieces);
                    stats.Extended++;
                }
            }

        // a merged core's streets between its members lie under it: none left (#818 phase 5c)
        var mergedOf = mine.Where(p => p.Merged is not null).SelectMany(p => p.Nodes.Select(n => (n, p))).ToDictionary(x => x.n, x => x.p);
        // (a roundabout's ring too, whatever node splits it: a tile's edge, #854)
        var rings = mine.Where(p => p.Merged?.Ring is not null).ToList();
        bool OnRing(int node, JunctionPlan p) => node >= 0 && p.Merged!.Ring is { } r && Math.Abs(net.Nodes[node].Position.DistanceTo(r.Centre) - r.Radius) <= 2.5;
        bool Under(RoadLink l) => mergedOf.TryGetValue(l.StartNode, out var ps) && mergedOf.TryGetValue(l.EndNode, out var pe) && ps == pe
            || rings.Any(p => OnRing(l.StartNode, p) && OnRing(l.EndNode, p));
        if (mergedOf.Count > 0)
            for (int link = 0; link < net.Links.Count; link++)
                if (Under(net.Links[link]) && segmentOf.TryGetValue(link, out var so))
                {
                    Replace(so.Segment, so.Tile, []);
                    touched.Add(link);
                    stats.InnerEmptied++;
                }

        // a street's piece split at <at> m from its junction end: the junction side returned, the yield flags kept on their ends
        RoadSegment? Split(StreetPiece sp, double at)
        {
            double len = sp.Length;
            if (at < 0.05 || at > len - 0.05) return null;
            var near = sp.TowardJunction ? StreetPlanner.Slice(sp.Piece, len - at, double.PositiveInfinity) : StreetPlanner.Slice(sp.Piece, 0, at);
            var far = sp.TowardJunction ? StreetPlanner.Slice(sp.Piece, 0, len - at) : StreetPlanner.Slice(sp.Piece, at, double.PositiveInfinity);
            if (near.Length < 6 || far.Length < 6) return null;
            var a = sp.Piece.Attributes;
            var nearPiece = Copy(sp.Piece, near, a with { Flags = a.Flags & ~(sp.TowardJunction ? RoadAttrFlags.YieldAtStart : RoadAttrFlags.YieldAtEnd) });
            var farPiece = Copy(sp.Piece, far, a with { Flags = a.Flags & ~(sp.TowardJunction ? RoadAttrFlags.YieldAtEnd : RoadAttrFlags.YieldAtStart) });
            var pieces = PiecesOf(sp.Whole).ToList();
            int i = pieces.FindIndex(x => ReferenceEquals(x, sp.Piece));
            pieces.RemoveAt(i);
            pieces.InsertRange(i, sp.TowardJunction ? [farPiece, nearPiece] : [nearPiece, farPiece]);
            Replace(sp.Whole, sp.Tile, pieces);
            return nearPiece;
        }
        // the street split at <at> m out from the arm's mouth
        void SplitStreetAt(ArmPlan a, double at)
        {
            double d = 0;
            foreach (var sp in Street(a))
            {
                double len = sp.Length;
                if (at > d + 0.05 && at < d + len - 0.05) { Split(sp, at - d); return; }
                d += len;
                if (d > at) return;
            }
        }

        // 2b. the kerbs lowered where a crosswalk crosses the sides (#711): its stretch and 0.6 m either way, its own pieces
        foreach (var jp in mine)
            foreach (var ex in jp.Exterior)
            {
                if (ex.Crosswalk is not { } cw) continue;
                double from = Math.Max(0, cw.From - RampMargin), to = cw.To + RampMargin;
                SplitStreetAt(ex.Arm, from);
                SplitStreetAt(ex.Arm, to);
                double d = 0;
                var lower = new List<StreetPiece>();
                foreach (var sp in Street(ex.Arm))
                {
                    double len = sp.Length;
                    if (d >= from - 0.06 && d + len <= to + 0.06) lower.Add(sp);
                    d += len;
                    if (d > to) break;
                }
                foreach (var sp in lower)
                {
                    var pieces = PiecesOf(sp.Whole).ToList();
                    int i = pieces.FindIndex(x => ReferenceEquals(x, sp.Piece));
                    if (i < 0) continue;
                    // where the walkers cross, the path takes the grass strips' width and its sloped kerb meets the road (as Lower, #711)
                    static RoadSide Fold(RoadSide s) => s.HasTrack ? s with { BikeDm = (byte)(s.BikeDm + s.VergeDm + s.BufferDm), VergeDm = 0, BufferDm = 0 } : s;
                    var at = sp.Piece.Attributes;
                    pieces[i] = Copy(sp.Piece, sp.Piece.Points, at with { Flags = at.Flags | RoadAttrFlags.LoweredKerbs, Left = Fold(at.Left), Right = Fold(at.Right) });
                    Replace(sp.Whole, sp.Tile, pieces);
                    touched.Add(sp.Link);
                }
                stats.Ramps++;
            }

        // 3. the transitions: every one cut at its end first (so no cut splits a piece another one flagged), then the pieces each
        // covers flagged, each with its TRNS record and the paint on it
        var all =mine.SelectMany(p => p.Arms).Select(a => a.Transition).OfType<Transition>().Distinct().ToList();
        List<(StreetPiece Piece, double From, double To)> Cover(Transition t, bool cut)
        {
            var street = Street(t.Arm);
            double d = 0, length = t.Length;
            var covered = new List<(StreetPiece Piece, double From, double To)>();
            for (int k = 0; k < street.Count && d < length - 0.01; k++)
            {
                var sp = street[k];
                double len = sp.Length;
                // the far mouth of a shared transition: the last piece covers to its end
                bool last = t.Far is not null && (k == street.Count - 1 || d + len >= length - 0.5);
                // cut where the transition ends: the junction side is the transition's
                if (cut && !last && d + len > length + 0.01 && Split(sp, length - d) is { } near)
                {
                    sp = sp with { Piece = near };
                    len = sp.Length;
                }
                // a piece another transition already holds (the two overlap there: a street too short for both) stays with it
                if (!sp.Piece.Attributes.Has(RoadAttrFlags.InTransition)) covered.Add((sp, d, Math.Min(length, d + len)));
                touched.Add(sp.Link);
                d += len;
            }
            return covered;
        }
        foreach (var t in all) Cover(t, cut: true);
        foreach (var t in all)
        {
            var covered = Cover(t, cut: false);
            var clear = ClearOf(t, mine);
            var clearRiders = ClearOf(t, mine, riders: true);
            foreach (var (sp, from, to) in covered)
            {
                // a point at every station the transition's widths change at: the game builds the section at the piece's points only,
                // so a kerb between two far points ran straight past a pocket's taper (the user, Oct 17)
                double pieceLen = sp.Length;
                var at = t.Stations.Where(s => s > from + 0.05 && s < to - 0.05).Select(s => sp.TowardJunction ? pieceLen - (s - from) : s - from);
                var flagged = Copy(sp.Piece, WithPointsAt(sp.Piece.Points, at), sp.Piece.Attributes with { Flags = sp.Piece.Attributes.Flags | RoadAttrFlags.InTransition });
                var pieces = PiecesOf(sp.Whole).ToList();
                pieces[pieces.FindIndex(x => ReferenceEquals(x, sp.Piece))] = flagged;
                Replace(sp.Whole, sp.Tile, pieces);
                var rt = PieceTransition(t, flagged, from, to, sp.TowardJunction);
                Get(transitionsOf, sp.Tile).Add(rt);
                stats.Pieces++;
                var into = Get(paint, sp.Tile);
                foreach (var l in t.Markings?.Lines ?? [])
                    foreach (var (lf, lt) in Without(l.From, l.To, l.Rgba == PaintEmitter.Yellow ? clearRiders : clear))
                    {
                        double f = Math.Max(lf, from), e = Math.Min(lt, to);
                        if (e - f < 0.05) continue;
                        var (pf, pe) = sp.TowardJunction ? (to - e, to - f) : (f - from, e - from);
                        int n = t.Strips.Count;
                        into.Add(RoadPaint.AlongTransition(rt, sp.TowardJunction ? l.Boundary : n - l.Boundary, l.Type, l.Rgba, l.Width, l.Dash, l.Gap,
                            sp.TowardJunction ? l.Offset : -l.Offset, pf, pe));
                        stats.Lines++;
                    }
            }
            Marks(t, covered, paint, stats, clear);
            if (mine.First(p => p.Arms.Contains(t.Arm)) is { } owner && !stats.WithTransitions.Contains(owner.Centre)) stats.WithTransitions.Add(owner.Centre);
            stats.Transitions++;
        }

        // 4. the streets' own paint laid again on what the transitions leave of them
        foreach (int link in touched)
        {
            if (!segmentOf.TryGetValue(link, out var so) || !paintStation.TryGetValue(link, out var ps)) continue;
            var into = Get(paint, so.Tile);
            into.RemoveAll(p => ReferenceEquals(p.Segment, so.Painted));
            var pieces = PiecesOf(so.Segment);
            double s = 0;
            for (int i = 0; i < pieces.Count;)
            {
                if (pieces[i].Attributes.Has(RoadAttrFlags.InTransition)) { s += RoadPaintGeometry.Length(pieces[i].Points); i++; continue; }
                int j = i;
                var points = new List<float>();
                double runFrom = s;
                while (j < pieces.Count && !pieces[j].Attributes.Has(RoadAttrFlags.InTransition))
                {
                    var pp = pieces[j].Points;
                    points.AddRange(points.Count == 0 ? pp : pp.Skip(3));
                    s += RoadPaintGeometry.Length(pp);
                    j++;
                }
                if (points.Count >= 6)
                    PaintEmitter.Emit(Copy(so.Painted, points.ToArray()), ps.Station + runFrom, into, i == 0 && ps.Start, j == pieces.Count && ps.End, bikeLanes: false);
                i = j;
            }
        }

        // 4b. the sidewalk corners each core gives its corner builder (phase 4): between two arms, their first pieces' ends at the
        // mouths and the core's kerb from arm A's mouth corner round to arm B's, on the core's heights
        foreach (var jp in mine)
        {
            if (!capOfPlan.TryGetValue(jp, out var hc)) continue;
            var (home, cap) = hc;
            var core = jp.Core!;
            var anchors = new List<(Vec2 At, float Height)>();
            foreach (var m in core.Mouths)
                if (net.Links[jp.Arms.First(a => a.Arm == m.Arm).LinkId].Tag is Source s) anchors.Add(((m.Left + m.Right) * 0.5, s.SampleHeight((m.Left + m.Right) * 0.5)));
            Vec2 Local2(Vec2 p) => new(p.X - home.MinE, p.Y - home.MaxN);
            Vec2? EndOf(ArmPlan a)
            {
                var street = Street(a);
                if (street.Count == 0 || street[0].Tile != home) return null;
                var p = street[0].Piece.Points;
                int at = street[0].TowardJunction ? p.Length - 3 : 0;
                return new Vec2(p[at], -p[at + 2]);
            }
            var list = new List<CornerPlanner.CornerInput>();
            foreach (var c in core.Corners)
            {
                if (c.Shape == CornerShape.Gore || c.Kerb.Count == 0) continue;
                var a = jp.Arms.First(x => x.Arm == c.ArmA);
                var b = jp.Arms.First(x => x.Arm == c.ArmB);
                if (EndOf(a) is not { } ea || EndOf(b) is not { } eb) continue;
                var ma = core.Mouths.First(m => m.Arm == c.ArmA);
                var mb = core.Mouths.First(m => m.Arm == c.ArmB);
                var line = new List<(Vec2 P, float Y)>();
                foreach (var p in c.Kerb.Prepend(ma.Right).Append(mb.Left))
                    if (line.Count == 0 || line[^1].P.DistanceTo(Local2(p)) > 0.01) line.Add((Local2(p), HeightAt(anchors, p)));
                if (line.Count < 2) continue;
                list.Add(new CornerPlanner.CornerInput(ea, eb, new CornerPlanner.KerbArc(line, a.Axis!.DirAt(ma.Station), b.Axis!.DirAt(mb.Station))));
            }
            if (!coreCorners.TryGetValue(home, out var byCap)) coreCorners[home] = byCap = new(ReferenceEqualityComparer.Instance);
            // (a paved apron: flush, no sidewalk corner round its core, #854)
            // (today's cap written as a core keeps today's sidewalk corners)
            if (core.IsCap) continue;
            byCap[cap] = jp.Apron ? [] : list;
            if (jp.Apron && !jp.Beyond)
            {
                stats.Aprons++;
                if (Environment.GetEnvironmentVariable("APRONDBG") is not null) Console.WriteLine(FormattableString.Invariant($"[apron] {jp.Centre.X:F0},{jp.Centre.Y:F0} {jp.Arms.Count} arms"));
            }
        }

        // 5. the exterior (phase 4): every arm's bars, refuge and signs
        foreach (var jp in mine)
        {
            WriteExterior(jp, result, segmentOf, finalPieces, paint, areas, signs, signalStats, stats);
            var capHere = capOfPlan.TryGetValue(jp, out var hc2) ? hc2.Cap : null;
            WriteCoreLines(jp, result, capHere, paint, stats);
            var plan = jp.Lights ? WriteSignals(jp, result, signals, approaches, cantons, signalStats, stats) : null;
            short signalIndex = plan is null || !capOfPlan.TryGetValue(jp, out var hs) ? (short)-1 : (short)(Get(signals, hs.Home).Count - 1);
            if (capHere is not null && capOfPlan.TryGetValue(jp, out var hp))
                WritePaths(jp, hp.Home, capHere, plan, signalIndex, jp.Core!.Connectors);
            var landings = new List<Vec2>();
            var rides = new List<Connector>();
            WriteBikeCrossings(jp, result, segmentOf, finalPieces, plan, capHere, paint, bridges, pathEnds, stats, landings, rides);
            // the riders' ways from path to path (#971), as junction paths beside the core's
            if (capHere is not null && capOfPlan.TryGetValue(jp, out var hr) && rides.Count > 0)
            {
                WritePaths(jp, hr.Home, capHere, plan, signalIndex, rides);
                stats.PathRides += rides.Count;
            }
            // a corner a path crossing ends on is laid as beside a crosswalk (the user, Oct 14 2026)
            if (landings.Count == 0 || !capOfPlan.TryGetValue(jp, out var hc3) || !coreCorners.TryGetValue(hc3.Home, out var byCap3)
                || !byCap3.TryGetValue(hc3.Cap, out var inputs)) continue;
            for (int k = 0; k < inputs.Count; k++)
            {
                var line = inputs[k].Arc.Line.Select(q => new Vec2(q.P.X + hc3.Home.MinE, q.P.Y + hc3.Home.MaxN)).ToList();
                if (landings.Any(p => DistanceToPolyline(line, p) < LandingReach)) { inputs[k] = inputs[k] with { Landing = true }; stats.CornersLanded++; }
            }
        }
    }

    /// <summary>
    /// A transition's record over one piece it covers, from station <paramref name="from"/> to <paramref name="to"/> (from its
    /// mouth): its stations there along the piece's drawing, its strips seen along the drawing (mirrored where the piece is drawn
    /// away from the junction), every number rounded as the file keeps it.
    /// </summary>
    private static RoadTransition PieceTransition(Transition t, RoadSegment piece, double from, double to, bool towardJunction)
    {
        float pieceLength = SegmentSections.Stations(piece)[^1];
        var knots = t.Stations.Where(s => s > from + 0.01 && s < to - 0.01).Prepend(from).Append(to).ToList();
        // the piece's stations, ascending along its drawing, scaled onto its own length
        double span = Math.Max(1e-6, to - from);
        var order = knots.Select(s => (Piece: (towardJunction ? to - s : s - from) / span * pieceLength, At: Math.Min(s, t.Length))).OrderBy(x => x.Piece).ToList();
        var stations = order.Select(x => RoadTransition.Cm(x.Piece)).ToArray();
        stations[0] = 0;
        stations[^1] = RoadTransition.Cm(pieceLength);
        int n = t.Strips.Count;
        int first = t.Strips.FindIndex(x => x.Strip.InCarriageway), last = t.Strips.FindLastIndex(x => x.Strip.InCarriageway);
        var strips = new List<RoadTransitionStrip>(n);
        for (int j = 0; j < n; j++)
        {
            int k = towardJunction ? j : n - 1 - j;
            var x = t.Strips[k].Strip;
            sbyte travel = x.Way == StripWay.None || x.Kind == StripKind.Hatch ? (sbyte)0 : (x.Way == StripWay.In) == towardJunction ? (sbyte)1 : (sbyte)-1;
            // a band is the piece's own (the street planner's: a gap for a driveway, a narrower sidewalk) changed as the transition
            // changes it from the normal section: a transition changes widths, the street decides the rest
            double? own = null;
            if (k < first || k > last)
            {
                // the side it lies on, along the piece's drawing
                bool rightOfDrawing = (k > last) == towardJunction;
                own = BandWidth(rightOfDrawing ? piece.Attributes.Right : piece.Attributes.Left, x.Kind);
            }
            double normal = 0;
            foreach (var y in t.Arm.Normal.Strips) if (y.Id == x.Id) normal = y.Width;
            strips.Add(new RoadTransitionStrip
            {
                Kind = x.Kind, Id = x.Id, Travel = travel, Moves = x.Moves, Flags = x.SharedWithBikes ? TransitionStripFlags.SharedWithBikes : 0,
                Widths = order.Select(o => RoadTransition.Cm(own is { } w ? Math.Max(0, w + t.WidthAt(k, o.At) - normal) : t.WidthAt(k, o.At))).ToArray(),
            });
        }
        return new RoadTransition
        {
            Segment = piece, Stations = stations, Strips = strips, Anchor = towardJunction ? t.Anchor : n - t.Anchor,
            Centre = order.Select(o => RoadTransition.Cm(towardJunction ? t.CentreAt(o.At) : -t.CentreAt(o.At))).ToArray(),
        };
    }

    /// <summary>A cap's outline may leave its kerb by this much in plan and in height (as the sides' vertices, <c>RoadStreetBuilder</c>).</summary>
    /// <summary>A crosswalk lowers the kerbs either side this far past its bars each way (#711).</summary>
    private const double RampMargin = 0.6;

    private const double CapPlanTolerance = 0.015, CapHeightTolerance = 0.01;

    /// <summary>
    /// The indices of a ring's points worth keeping (Douglas-Peucker in plan and height on each run between two points that stay):
    /// every point of <paramref name="fixedAt"/> stays.
    /// </summary>
    private static List<int> SimplifiedRing(List<Vec2> ring, List<double> heights, List<Vec2> fixedAt)
    {
        int n = ring.Count;
        var keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = fixedAt.Any(f => f.DistanceTo(ring[i]) < 1e-6);
        if (!keep.Any(k => k)) keep[0] = true;
        void Run(int a, int b)
        {
            // points strictly between a and b round the ring
            int len = (b - a + n) % n;
            if (len < 2) return;
            double worst = 0;
            int at = -1;
            var pa = ring[a];
            var ab = ring[b] - pa;
            double l2 = ab.LengthSquared;
            for (int s = 1; s < len; s++)
            {
                int i = (a + s) % n;
                double t = l2 < 1e-12 ? 0 : Math.Clamp((ring[i] - pa).Dot(ab) / l2, 0, 1);
                double plan = ring[i].DistanceTo(pa + ab * t) / CapPlanTolerance;
                double up = Math.Abs(heights[a] + (heights[b] - heights[a]) * t - heights[i]) / CapHeightTolerance;
                double off = Math.Max(plan, up);
                if (off > 1 && off > worst) { worst = off; at = i; }
            }
            if (at < 0) return;
            keep[at] = true;
            Run(a, at);
            Run(at, b);
        }
        var fixedIdx = Enumerable.Range(0, n).Where(i => keep[i]).ToList();
        for (int k = 0; k < fixedIdx.Count; k++) Run(fixedIdx[k], fixedIdx[(k + 1) % fixedIdx.Count]);
        return Enumerable.Range(0, n).Where(i => keep[i]).ToList();
    }

    /// <summary>A band's width on a side as the street planner laid it (0 where the side has none).</summary>
    private static double BandWidth(RoadSide side, StripKind kind) => kind switch
    {
        StripKind.Sidewalk => side.SidewalkDm / 10.0,
        StripKind.Path => side.HasTrack ? side.BikeDm / 10.0 : 0,
        StripKind.Verge => side.HasTrack ? side.VergeDm / 10.0 : 0,
        StripKind.Buffer => side.HasTrack ? side.BufferDm / 10.0 : 0,
        _ => 0,
    };

    /// <summary>
    /// What a transition's paint keeps clear of (#818 phase 4): its arms' exteriors, in its own stations: at the lights everything
    /// up to the cars' line (lane lines end at a stop line), else the crosswalk and a little either side.
    /// </summary>
    private static List<(double From, double To)> ClearOf(Transition t, List<JunctionPlan> plans, bool riders = false)
    {
        var clear = new List<(double, double)>();
        foreach (var jp in plans)
            foreach (var ex in jp.Exterior)
            {
                bool near = ex.Arm == t.Arm;
                if (!near && ex.Arm != t.Far) continue;
                (double From, double To)? zone = null;
                // a bike lane's lines (<riders>) run on to its riders' line, every other one ends at the cars' line
                double stop = ex.Bars.Where(b => riders ? b.Kind == BarKind.RidersLine : b.Kind is BarKind.StopLine or BarKind.RidersLine).Select(b => b.To).DefaultIfEmpty(0).Max();
                if (jp.Lights && stop > 0) zone = (0, stop);
                else if (ex.Crosswalk is { } cw) zone = (cw.From - 0.3, cw.To + 0.3);
                if (zone is not { } z) continue;
                clear.Add(near ? z : (t.Length - z.To, t.Length - z.From));
            }
        return clear;
    }

    /// <summary>The pieces of [<paramref name="from"/>, <paramref name="to"/>] outside every range of <paramref name="clear"/>.</summary>
    private static List<(double From, double To)> Without(double from, double to, List<(double From, double To)> clear)
    {
        var parts = new List<(double, double)> { (from, to) };
        foreach (var (c0, c1) in clear)
        {
            var next = new List<(double, double)>();
            foreach (var (a, b) in parts)
            {
                if (c1 <= a || c0 >= b) { next.Add((a, b)); continue; }
                if (c0 > a) next.Add((a, c0));
                if (c1 < b) next.Add((c1, b));
            }
            parts = next;
        }
        return parts;
    }

    /// <summary>
    /// A transition's hatch stripes, arrows and bike symbols as geometry on the pieces it covers: each point (station, offset)
    /// found on its piece, on the road's height there.
    /// </summary>
    private static void Marks(Transition t, List<(StreetPiece Piece, double From, double To)> covered, Dictionary<TileId, List<RoadPaint>> paint, LayoutWriteStats stats,
        List<(double From, double To)> clear)
    {
        if (t.Markings is not { } m || covered.Count == 0) return;
        // a point of the transition on its piece: tile-local position, and the drawing's forward direction there
        (TileId Tile, float X, float Y, float Z, float Fx, float Fz, bool Toward)? At(double s, double o)
        {
            var hit = covered.FirstOrDefault(c => s >= c.From - 1e-6 && s <= c.To + 1e-6);
            if (hit.Piece is null) return null;
            var sp = hit.Piece;
            var p = sp.Piece.Points;
            double len = sp.Length, span = Math.Max(1e-6, hit.To - hit.From);
            double along = (sp.TowardJunction ? hit.To - s : s - hit.From) / span * len;
            double travelled = 0;
            for (int i = 0; i + 1 < sp.Piece.PointCount; i++)
            {
                float ax = p[i * 3], az = p[i * 3 + 2], bx = p[i * 3 + 3], bz = p[i * 3 + 5];
                double seg = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                if (travelled + seg < along - 1e-6 && i + 2 < sp.Piece.PointCount) { travelled += seg; continue; }
                double u = seg < 1e-9 ? 0 : Math.Clamp((along - travelled) / seg, 0, 1);
                float fx = (float)((bx - ax) / Math.Max(seg, 1e-9)), fz = (float)((bz - az) / Math.Max(seg, 1e-9));
                // right of the drawing (x east, z south): (-fz, fx); a piece drawn away from the junction sees offsets mirrored
                double off = sp.TowardJunction ? o : -o;
                float x = (float)(ax + (bx - ax) * u - fz * off), z = (float)(az + (bz - az) * u + fx * off);
                float y = (float)(p[i * 3 + 1] + (p[i * 3 + 4] - p[i * 3 + 1]) * u);
                return (sp.Tile, x, y, z, fx, fz, sp.TowardJunction);
            }
            return null;
        }
        // a hatch's stripes one mesh, as the old layout's (#406): a run of them in one tile, broken where the exterior keeps clear
        var mesh = new List<float>();
        var indices = new List<ushort>();
        (int Strip, TileId Tile)? run = null;
        void Flush()
        {
            if (run is { } r && indices.Count > 0)
                Get(paint, r.Tile).Add(new RoadPaint { Shape = PaintShape.Triangles, Type = PaintType.Hatch, Rgba = PaintEmitter.White, Vertices = mesh.ToArray(), Indices = indices.ToArray() });
            mesh.Clear();
            indices.Clear();
            run = null;
        }
        foreach (var (strip, quad) in m.Stripes)
        {
            var pts = quad.Select(q => At(q.S, q.O)).ToList();
            if (pts.Any(q => q is null) || pts.Select(q => q!.Value.Tile).Distinct().Count() > 1 || quad.Any(q => clear.Any(c => q.S > c.From && q.S < c.To)))
            {
                Flush();
                continue;
            }
            if (run != (strip, pts[0]!.Value.Tile)) { Flush(); run = (strip, pts[0]!.Value.Tile); }
            ushort b = (ushort)(mesh.Count / 3);
            mesh.AddRange(pts.SelectMany(q => new[] { q!.Value.X, q.Value.Y, q.Value.Z }));
            indices.AddRange([b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3)]);
            stats.Marks++;
        }
        Flush();
        foreach (var mark in m.Marks)
        {
            // a mark clear of the exterior: none in its stretch
            double markFrom = mark.Station - (mark.Bike ? BikePlanner.SymbolSize : PaintEmitter.ArrowLength);
            if (clear.Any(c => markFrom < c.To && mark.Station > c.From)) continue;
            var (f0, f1) = t.SpanAt(mark.Strip, mark.Station);
            double o = (f0 + f1) * 0.5;
            if (mark.Bike)
            {
                // the symbol a metre along the lane, toward the mouth (the riders arriving) or away from it
                double s0 = mark.Station - BikePlanner.SymbolSize, s1 = mark.Station;
                if (At(s0, o) is not { } a || At(s1, o) is not { } b || a.Tile != b.Tile) continue;
                // the riders go toward the mouth (an arriving lane, s falling) or away from it; the glyph reads along the drawing
                bool ridersAlongDrawing = mark.TowardFar ? !a.Toward : a.Toward;
                var (p0, p1) = a.Toward ? (b, a) : (a, b);
                Get(paint, a.Tile).Add(new RoadPaint
                {
                    Shape = PaintShape.Polyline, Type = PaintType.BikeSymbol, Rgba = PaintEmitter.Yellow, Width = BikePlanner.SymbolSize,
                    Variant = ridersAlongDrawing ? (byte)0 : RoadPaintGeometry.BikeReversed, Vertices = [p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z],
                });
                stats.Marks++;
                continue;
            }
            // an arrow from its tail toward the mouth
            double tipAt = mark.Station - PaintEmitter.ArrowLength;
            if (At(mark.Station, o) is not { } tail || At(tipAt, o) is not { } tip || tail.Tile != tip.Tile) continue;
            float dx = tip.X - tail.X, dz = tip.Z - tail.Z;
            Get(paint, tail.Tile).Add(PaintEmitter.Arrow(tail.X, tail.Y, tail.Z, dx, dz, mark.Arrow, tip.Y));
            stats.Marks++;
        }
    }
}
