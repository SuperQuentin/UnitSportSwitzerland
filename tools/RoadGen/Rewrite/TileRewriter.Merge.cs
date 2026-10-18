namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Junctions under 30 m apart whose cores overlap (#818 phase 5c, the user, Oct 15 2026: "merge only if overlapping"): each
/// group is planned again as one junction, its arms the members' arms but the short streets between them, which lie under
/// the one core. The old layout still sees the members (their priority plans stay as they were).
/// </summary>
public static partial class TileRewriter
{
    /// <summary>
    /// The junctions planned: each node on its own, then, where cores overlap, every group of them merged and the block
    /// planned again with the merged junctions in place of their members.
    /// </summary>
    private static List<JunctionPlan> PlanAndMerge(PriorityResult priority, RoadGenResult result,
        Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf, Dictionary<TileId, List<RoadSegment>> output,
        HashSet<TileId> block, HashSet<TileId> wanted, HashSet<int> near, Dictionary<TileId, ChunkGrid>? grids, Footprints buildings,
        Func<int, LinkEnd, bool, RoadSide> streetSide, OsmOverlayReader? overlay, Restrictions? restrictions, CrossingNodes? crossings,
        MergeStats stats)
    {
        var plans = PlanLayouts(priority, result, segmentOf, output, block, wanted, near, grids, buildings, streetSide, overlay, restrictions, null, crossings);
        // overlapping cores are first laid apart, each mouth held at half the street between (turns tighter than their vehicle):
        // only a group where that fails a check merges (the user, Oct 17: two junctions across a grass strip at Sion
        // 2505653,1137735 are two, not one paved triangle)
        // a car road joining an arm short of its mouth: its node and the junction are one (the user, Oct 18: a lane between two car
        // parks, a link of a divided road), merged where the streets between form a tree as any group
        var byNode = plans.ToDictionary(p => p.Node);
        var joins = new List<(JunctionPlan, JunctionPlan)>();
        foreach (var p in plans)
            if (p.Core is { } core)
                foreach (var a in p.Arms)
                    if (a.Axis is not null && core.Mouths.FirstOrDefault(m => m.Arm == a.Arm) is { } mouth
                        && NodeShortOfMouth(result.Network, a, mouth.Station) is int node && byNode.TryGetValue(node, out var q) && q != p)
                        joins.Add((p, q));
        var joined = joins.SelectMany(j => new[] { j.Item1, j.Item2 }).ToHashSet();
        var groups = JunctionPlan.OverlapGroups(plans, joins).Where(g => g.Any(p => p.CoresOverlap && p.CoreFails || joined.Contains(p))).ToList();
        stats.HeldApart += plans.Count(p => !p.Beyond && p.CoresOverlap && !p.CoreFails);
        var planned = plans.Select(p => p.Node).ToHashSet();
        if (groups.Count == 0) return plans;
        var merged = new PriorityResult();
        var gone = new HashSet<int>();
        foreach (var group in groups)
        {
            var nodes = group.Select(p => p.Node).ToHashSet();
            bool counted = group.Any(p => !p.Beyond);
            if (Merge(priority, nodes, result.Network, planned, out string? no) is { } m)
            {
                merged.Plans.Add(m);
                gone.UnionWith(nodes);
                if (counted) { stats.Merged++; stats.Members += nodes.Count; }
            }
            else if (counted) stats.Why[no!] = stats.Why.GetValueOrDefault(no!) + 1;
        }
        if (merged.Plans.Count == 0) return plans;
        merged.Plans.InsertRange(0, priority.Plans.Where(p => !gone.Contains(p.Junction.NodeId)));
        foreach (var (k, v) in priority.Yield) merged.Yield[k] = v;
        merged.SignalsFromData.UnionWith(priority.SignalsFromData);
        // (a merged junction homed beyond the block, near it, is planned as its members were)
        var nearMerged = new HashSet<int>(near);
        foreach (var (j, _) in merged.Plans.Where(p => p.Junction.Members is { } ms && ms.Any(near.Contains))) nearMerged.Add(j.NodeId);
        return PlanLayouts(merged, result, segmentOf, output, block, wanted, nearMerged, grids, buildings, streetSide, overlay, restrictions, null, crossings);
    }

    /// <summary>
    /// One junction of <paramref name="nodes"/> with its priority plan: the members' arms but the streets between two members (each
    /// under <see cref="InternalLink"/>), their signs and give-way rows on those arms, lit where any member is. Null (and why)
    /// where it cannot be: a member without its plan, not planned with the block, a roundabout, a longer street between two members,
    /// streets between members closing a loop (an island), or a centre outside every member's tile (the core's records go in
    /// one tile).
    /// </summary>
    private static (Junction Junction, PriorityPlanner.Plan Plan)? Merge(PriorityResult priority, HashSet<int> nodes, RoadNetwork net,
        HashSet<int> planned, out string? no)
    {
        no = null;
        var members = priority.Plans.Where(p => nodes.Contains(p.Junction.NodeId)).ToList();
        if (members.Count != nodes.Count || members.Any(p => p.Plan.Arms.Count != p.Junction.Arms.Count)) { no = "a member without its plan"; return null; }
        if (members.Any(p => !planned.Contains(p.Junction.NodeId))) { no = "a member beyond the block"; return null; }
        if (members.Any(p => p.Plan.Kind == PriorityPlanner.Kind.Roundabout)) { no = "a roundabout"; return null; }
        var inner = new HashSet<int>();
        foreach (var (j, _) in members)
            foreach (var arm in j.Arms)
            {
                var link = net.Links[arm.LinkId];
                if (!nodes.Contains(link.StartNode) || !nodes.Contains(link.EndNode)) continue;
                if (link.StartNode == link.EndNode || LinkLength(link) >= InternalLink) { no = "a longer street between two members"; return null; }
                inner.Add(arm.LinkId);
            }
        // streets between the members that close a loop enclose an island (a triangle, a divided road's square): real, so the
        // members stay as they are (the user, Oct 16, against the aerials); a tree of them (a pair, a chain) is one junction
        if (inner.Count >= nodes.Count) { no = "streets between members enclose an island"; return null; }
        var centre = new Vec2(members.Average(p => p.Junction.Centre.X), members.Average(p => p.Junction.Centre.Y));
        var home = TileId.FromLv95(centre.X, centre.Y);
        var rep = members.Where(p => Home(p.Junction) == home).OrderBy(p => p.Junction.Centre.DistanceTo(centre)).FirstOrDefault();
        if (rep.Junction is null) { no = "a centre outside its members' tiles"; return null; }

        var junction = new Junction { NodeId = rep.Junction.NodeId, Centre = centre, Layer = rep.Junction.Layer, Members = nodes };
        var plan = new PriorityPlanner.Plan
        {
            // lit where any member is (one set of lights for the whole core), else the strongest member's kind
            Kind = members.Max(p => p.Plan.Kind),
            CentreUrban = members.Any(p => p.Plan.CentreUrban),
        };
        foreach (var (j, p) in members)
        {
            for (int i = 0; i < j.Arms.Count; i++)
                if (!inner.Contains(j.Arms[i].LinkId))
                {
                    junction.Arms.Add(j.Arms[i]);
                    plan.Arms.Add(p.Arms[i]);
                }
            plan.Teeth.AddRange(p.Teeth.Where(t => !inner.Contains(t.LinkId)));
            plan.Signs.AddRange(p.Signs.Where(x => !inner.Contains(x.LinkId)));
            junction.Boundary.AddRange(j.Boundary);
        }
        // the members' arms in their order round the core, as one node's are
        var order = Enumerable.Range(0, junction.Arms.Count).OrderBy(k => Angles.Normalize(RingOf(junction, junction.Arms[k]))).ToList();
        var arms = order.Select(k => junction.Arms[k]).ToList();
        var planArms = order.Select(k => plan.Arms[k]).ToList();
        junction.Arms.Clear();
        junction.Arms.AddRange(arms);
        plan.Arms.Clear();
        plan.Arms.AddRange(planArms);
        return (junction, plan);
    }

    private static TileId Home(Junction j) => TileId.FromLv95(j.Centre.X, j.Centre.Y);

    /// <summary>Where an arm of a merged junction leaves its core, seen from the core's centre: its mouth 10 m on along it.</summary>
    private static double RingOf(Junction j, JunctionArm arm) =>
        ((arm.Left + arm.Right) * 0.5 + Vec2.FromHeading(arm.OutwardHeading) * 10 - j.Centre).Heading;

    /// <summary>The merges of a region (#818 phase 5c).</summary>
    public sealed class MergeStats
    {
        public int Merged, Members, HeldApart;
        public readonly Dictionary<string, int> Why = new();

        public string Summary() =>
            $"    merged cores (#818 phase 5c): {Merged:N0} of {Members:N0} junctions ({HeldApart:N0} overlapping ones laid apart, held at half the street between)"
            + (Why.Count > 0 ? $"; not merged: {string.Join(", ", Why.OrderBy(w => w.Key).Select(w => $"{w.Key} {w.Value:N0}"))}" : "");
    }
}
