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
        HashSet<TileId> block, HashSet<TileId> wanted, HashSet<int> near, IReadOnlyList<RoundaboutShaper.Ring> rings,
        Dictionary<TileId, ChunkGrid>? grids, Footprints buildings,
        Func<int, LinkEnd, bool, RoadSide> streetSide, OsmOverlayReader? overlay, Restrictions? restrictions, CrossingNodes? crossings,
        MergeStats stats)
    {
        // a tight roundabout is one junction, its ring's nodes its members (#854, the user, Oct 18): laid whole by RoundaboutCore
        bool Planned(Junction j) => block.Contains(Home(j)) && wanted.Contains(Home(j)) || near.Contains(j.NodeId);
        if (rings.Count > 0 && Rings(priority, rings, result.Network, Planned, stats) is { } withRings)
        {
            priority = withRings;
            near = new HashSet<int>(near);
            foreach (var (j, _) in priority.Plans) if (j.Ring is not null && j.Members!.Any(near.Contains)) near.Add(j.NodeId);
        }
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
        var sets = new List<HashSet<int>>();
        foreach (var group in groups)
        {
            var nodes = group.Select(p => p.Node).ToHashSet();
            bool counted = group.Any(p => !p.Beyond);
            if (Merge(priority, nodes, result.Network, planned, out string? no) is not null)
            {
                sets.Add(nodes);
                if (counted) { stats.Merged++; stats.Members += nodes.Count; }
            }
            else
            {
                if (counted) stats.Why[no!] = stats.Why.GetValueOrDefault(no!) + 1;
                if (counted && Environment.GetEnvironmentVariable("OVERLAPDBG") is not null)
                    foreach (var p in group.Where(p => !p.Beyond))
                        Console.WriteLine(FormattableString.Invariant($"[overlap-group] {p.Centre.X:F0},{p.Centre.Y:F0} {group.Count} members: {no}"));
            }
        }
        if (sets.Count == 0) return plans;
        var again = Replan(sets);
        // the merged cores' mouths reach further than their members' did: a junction they now reach short of a mouth joins them
        // too, and the block is planned again (#854)
        for (int round = 0; round < 2; round++)
        {
            var byAnyNode = new Dictionary<int, JunctionPlan>();
            foreach (var p in again) foreach (int n in p.Nodes) byAnyNode[n] = p;
            var pairs = new List<(JunctionPlan, JunctionPlan)>();
            foreach (var p in again)
                if (p.Core is { } core)
                    foreach (var a in p.Arms)
                        if (a.Axis is not null && core.Mouths.FirstOrDefault(m => m.Arm == a.Arm) is { } mouth
                            && NodeShortOfMouth(result.Network, a, mouth.Station) is int node && byAnyNode.TryGetValue(node, out var q) && q != p
                            && p.Merged?.Ring is null && q.Merged?.Ring is null)
                            pairs.Add((p, q));
            if (pairs.Count == 0) break;
            var grown = new List<HashSet<int>>(sets.Select(x => new HashSet<int>(x)));
            bool changed = false;
            foreach (var (p, q) in pairs)
            {
                var both = p.Nodes.Concat(q.Nodes).ToHashSet();
                var touching = grown.Where(x => x.Overlaps(both)).ToList();
                foreach (var x in touching) { both.UnionWith(x); grown.Remove(x); }
                if (Merge(priority, both, result.Network, planned, out _) is not null) { grown.Add(both); changed = true; }
                else grown.AddRange(touching);
            }
            if (!changed) break;
            sets = grown;
            stats.Regrown++;
            again = Replan(sets);
        }
        stats.ByMembers += again.Count(p => !p.Beyond && p.Core?.ByMembers == true);
        return again;

        // the block planned again with each set of nodes one merged junction
        List<JunctionPlan> Replan(List<HashSet<int>> nodeSets)
        {
            var merged = new PriorityResult();
            var gone = new HashSet<int>();
            foreach (var nodes in nodeSets)
                if (Merge(priority, nodes, result.Network, planned, out _) is { } m)
                {
                    merged.Plans.Add(m);
                    gone.UnionWith(nodes);
                }
            merged.Plans.InsertRange(0, priority.Plans.Where(p => !gone.Contains(p.Junction.NodeId)));
            foreach (var (k, v) in priority.Yield) merged.Yield[k] = v;
            merged.SignalsFromData.UnionWith(priority.SignalsFromData);
            // (a merged junction homed beyond the block, near it, is planned as its members were)
            var nearMerged = new HashSet<int>(near);
            foreach (var (j, _) in merged.Plans.Where(p => p.Junction.Members is { } ms && ms.Any(near.Contains))) nearMerged.Add(j.NodeId);
            return PlanLayouts(merged, result, segmentOf, output, block, wanted, nearMerged, grids, buildings, streetSide, overlay, restrictions, null, crossings, byNode);
        }
    }

    /// <summary>
    /// One junction of <paramref name="nodes"/> with its priority plan: the members' arms but the streets between two members (each
    /// under <see cref="InternalLink"/>), their signs and give-way rows on those arms, lit where any member is. Null (and why)
    /// where it cannot be: a member without its plan, not planned with the block, a roundabout, a longer street between two members,
    /// streets between members closing a loop (an island), or a centre outside every member's tile (the core's records go in
    /// one tile).
    /// </summary>
    private static (Junction Junction, PriorityPlanner.Plan Plan)? Merge(PriorityResult priority, HashSet<int> nodes, RoadNetwork net,
        HashSet<int> planned, out string? no, RoundaboutShaper.Ring? ring = null)
    {
        no = null;
        var members = priority.Plans.Where(p => nodes.Contains(p.Junction.NodeId)).ToList();
        if (members.Count != nodes.Count || members.Any(p => p.Plan.Arms.Count != p.Junction.Arms.Count)) { no = "a member without its plan"; return null; }
        if (members.Any(p => !planned.Contains(p.Junction.NodeId))) { no = "a member beyond the block"; return null; }
        if (ring is null && members.Any(p => p.Plan.Kind == PriorityPlanner.Kind.Roundabout)) { no = "a roundabout"; return null; }
        var inner = new HashSet<int>();
        foreach (var (j, _) in members)
            foreach (var arm in j.Arms)
            {
                var link = net.Links[arm.LinkId];
                // (a ring's links are its own whatever node splits them: a tile's edge, #854)
                bool OnRing(int node) => ring is { } r && node >= 0 && Math.Abs(net.Nodes[node].Position.DistanceTo(r.Centre) - r.Radius) <= 2.5;
                if (!(nodes.Contains(link.StartNode) || OnRing(link.StartNode)) || !(nodes.Contains(link.EndNode) || OnRing(link.EndNode))) continue;
                if (link.StartNode == link.EndNode || ring is null && LinkLength(link) >= InternalLink) { no = "a longer street between two members"; return null; }
                inner.Add(arm.LinkId);
            }
        // streets between the members that close a loop enclose an island (a triangle, a divided road's square): real, so the
        // members stay as they are (the user, Oct 16, against the aerials); a tree of them (a pair, a chain) is one junction
        if (ring is null && inner.Count >= nodes.Count) { no = "streets between members enclose an island"; return null; }
        var centre = ring?.Centre ?? new Vec2(members.Average(p => p.Junction.Centre.X), members.Average(p => p.Junction.Centre.Y));
        var home = TileId.FromLv95(centre.X, centre.Y);
        var rep = members.Where(p => Home(p.Junction) == home).OrderBy(p => p.Junction.Centre.DistanceTo(centre)).FirstOrDefault();
        if (rep.Junction is null) { no = "a centre outside its members' tiles"; return null; }

        var junction = new Junction { NodeId = rep.Junction.NodeId, Centre = centre, Layer = rep.Junction.Layer, Members = nodes, Ring = ring };
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

    /// <summary>
    /// The priority plans with each tight roundabout (<see cref="RoundaboutCore.Tight"/>) one junction: the ring's nodes (the
    /// roundabout plans standing on its circle) merged, every other link an arm. A ring some of whose nodes are not planned with
    /// the block, or that will not merge, keeps its nodes. Null where no ring changed anything.
    /// </summary>
    private static PriorityResult? Rings(PriorityResult priority, IReadOnlyList<RoundaboutShaper.Ring> rings, RoadNetwork net,
        Func<Junction, bool> planned, MergeStats stats)
    {
        var made = new List<(Junction, PriorityPlanner.Plan)>();
        var gone = new HashSet<int>();
        foreach (var ring in rings)
        {
            if (!RoundaboutCore.Tight(ring.Radius, ring.Width)) continue;
            var members = priority.Plans.Where(p => p.Plan.Kind == PriorityPlanner.Kind.Roundabout && !gone.Contains(p.Junction.NodeId)
                && Math.Abs(p.Junction.Centre.DistanceTo(ring.Centre) - ring.Radius) <= 2.5).ToList();
            if (members.Count < 2) continue;
            var nodes = members.Select(p => p.Junction.NodeId).ToHashSet();
            // a splitter island's tip: a node off the ring that two of the ring's nodes lead to directly (TLM draws an arm's entry
            // and exit apart up to there)
            var leadsTo = new Dictionary<int, int>();
            foreach (var (j, _) in members)
                foreach (var arm in j.Arms)
                {
                    var l = net.Links[arm.LinkId];
                    int other = l.StartNode == j.NodeId ? l.EndNode : l.StartNode;
                    if (other >= 0 && !nodes.Contains(other)) leadsTo[other] = leadsTo.GetValueOrDefault(other) + 1;
                }
            foreach (var (tip, count) in leadsTo)
                if (count >= 2 && priority.Plans.FirstOrDefault(p => p.Junction.NodeId == tip) is { Junction: not null } t && !gone.Contains(tip)
                    && t.Junction.Centre.DistanceTo(ring.Centre) <= ring.Radius + ring.Width * 0.5 + RoundaboutCore.TipReach)
                {
                    members.Add(t);
                    nodes.Add(tip);
                }
            if (!members.All(p => planned(p.Junction))) continue;
            if (Merge(priority, nodes, net, nodes, out string? no, ring) is { } m)
            {
                made.Add(m);
                gone.UnionWith(nodes);
                stats.Rings++;
            }
            else stats.Why["a roundabout: " + no] = stats.Why.GetValueOrDefault("a roundabout: " + no) + 1;
        }
        if (made.Count == 0) return null;
        var result = new PriorityResult();
        result.Plans.AddRange(made);
        result.Plans.AddRange(priority.Plans.Where(p => !gone.Contains(p.Junction.NodeId)));
        foreach (var (k, v) in priority.Yield) result.Yield[k] = v;
        result.SignalsFromData.UnionWith(priority.SignalsFromData);
        return result;
    }

    private static TileId Home(Junction j) => TileId.FromLv95(j.Centre.X, j.Centre.Y);

    /// <summary>Where an arm of a merged junction leaves its core, seen from the core's centre: its mouth 10 m on along it.</summary>
    private static double RingOf(Junction j, JunctionArm arm) =>
        ((arm.Left + arm.Right) * 0.5 + Vec2.FromHeading(arm.OutwardHeading) * 10 - j.Centre).Heading;

    /// <summary>The merges of a region (#818 phase 5c).</summary>
    public sealed class MergeStats
    {
        public int Merged, Members, HeldApart, Rings, ByMembers, Regrown;
        public readonly Dictionary<string, int> Why = new();

        public string Summary() =>
            $"    merged cores (#818 phase 5c): {Merged:N0} of {Members:N0} junctions ({ByMembers:N0} laid member by member, #854; grown again {Regrown:N0} times where a merged core reached another junction; {HeldApart:N0} overlapping ones laid apart, held at half the street between); tight roundabouts laid whole {Rings:N0} (#854)"
            + (Why.Count > 0 ? $"; not merged: {string.Join(", ", Why.OrderBy(w => w.Key).Select(w => $"{w.Key} {w.Value:N0}"))}" : "");
    }
}
