using System;
using System.Collections.Generic;
using UnitSport.Core;

namespace UnitSport.Net.Regions;

/// <summary>
/// Which server region each body belongs to (#1456, G2a of #1443). The dedicated server runs its
/// physics in several local frames, one per cluster of bodies (bots and players' proxies): bodies
/// within <see cref="LinkM"/> of each other always share a region, so two bodies that can touch are
/// in one physics space; a region's members part only past <see cref="SplitM"/> (no flicker on the
/// border). The main region is today's frame at the manifest origin: every body within
/// <see cref="HomeM"/> of it, or linked to one that is, stays there, so play near the start runs
/// exactly as before. Measured in Earth distances (<see cref="GlobalPos"/>, true metres since #1502). A new
/// region's anchor is the cluster's centre: the whole LV95 km in a flat world, the Earth mean (the ECEF mean
/// on the ellipsoid) when regions are ENU frames (<see cref="Anchoring"/>, #1513). Pure, unit-tested
/// (<c>RegionPlannerTests</c>).
/// </summary>
public static class RegionPlanner
{
    /// <summary>Bodies this close always share a region.</summary>
    public const double LinkM = 2000;
    /// <summary>Bodies already in one region stay linked up to this.</summary>
    public const double SplitM = 6000;
    /// <summary>The main region holds every body this close to its origin.</summary>
    public const double HomeM = 20000;
    /// <summary>A body already in the main region stays home up to this.</summary>
    public const double HomeReleaseM = HomeM + SplitM;
    /// <summary>A region whose members reach past this from its anchor, and whose centre has drifted half as far, re-anchors.</summary>
    public const double ReanchorM = 8000;
    /// <summary>The id of the main region (never dropped, never re-anchored).</summary>
    public const int Main = 0;
    /// <summary>The region of a body not placed yet.</summary>
    public const int None = -1;

    /// <summary>How a new region's anchor is chosen (#1513).</summary>
    public enum Anchoring
    {
        /// <summary>The whole LV95 km nearest the members' mean, at altitude 0: a flat frame's origin.</summary>
        Lv95Km,
        /// <summary>The members' ECEF mean put on the ellipsoid (height 0): an ENU frame's origin.</summary>
        Earth,
    }

    /// <summary>A body: its id, where it is, and the region it is in now (<see cref="None"/> for a new one).</summary>
    public readonly record struct Body(long Id, GlobalPos At, int Region);

    /// <summary>A region that exists: its id and the anchor of its frame.</summary>
    public readonly record struct Region(int Id, GlobalPos Anchor);

    /// <summary>The decision: each body's region, the regions to make (with their anchors) and those no body needs.</summary>
    public sealed class Plan
    {
        public Dictionary<long, int> RegionOf { get; } = new();
        public List<Region> Create { get; } = new();
        public List<int> Drop { get; } = new();
    }

    /// <summary>
    /// Clusters <paramref name="bodies"/> and gives each cluster a region: the main one when it
    /// reaches home, else the existing region most of its members are in (a region goes to one
    /// cluster only, the larger), else a new one anchored at the cluster's centre (<paramref name="anchoring"/>).
    /// New ids are taken from <paramref name="nextId"/>.
    /// </summary>
    public static Plan Decide(IReadOnlyList<Body> bodies, IReadOnlyList<Region> regions, GlobalPos mainAnchor, ref int nextId,
        Anchoring anchoring = Anchoring.Lv95Km)
    {
        var plan = new Plan();
        int n = bodies.Count;
        // one union-find over the bodies plus a virtual node for home (index n)
        var parent = new int[n + 1];
        for (int i = 0; i <= n; i++) parent[i] = i;
        for (int i = 0; i < n; i++)
        {
            var a = bodies[i];
            double home = a.Region == Main ? HomeReleaseM : HomeM;
            if (a.At.HorizontalDistanceSquaredTo(mainAnchor) <= home * home) Union(parent, i, n);
            for (int j = i + 1; j < n; j++)
            {
                var b = bodies[j];
                double link = a.Region == b.Region && a.Region != None ? SplitM : LinkM;
                if (a.At.HorizontalDistanceSquaredTo(b.At) <= link * link) Union(parent, i, j);
            }
        }

        // the clusters, largest first: a split region stays with its larger part
        var clusters = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(parent, i);
            if (!clusters.TryGetValue(root, out var list)) clusters[root] = list = new List<int>();
            list.Add(i);
        }
        var ordered = new List<KeyValuePair<int, List<int>>>(clusters);
        ordered.Sort((x, y) => y.Value.Count != x.Value.Count ? y.Value.Count.CompareTo(x.Value.Count) : x.Value[0].CompareTo(y.Value[0]));

        var anchors = new Dictionary<int, GlobalPos>();
        foreach (var r in regions) anchors[r.Id] = r.Anchor;
        var claimed = new HashSet<int> { Main };
        int homeRoot = Find(parent, n);
        foreach (var (root, members) in ordered)
        {
            int region = root == homeRoot ? Main : Claim(bodies, members, anchors, claimed);
            if (region != Main && region != None && Drifted(bodies, members, anchors[region], anchoring))
            {
                claimed.Remove(region);
                region = None;
            }
            if (region == None)
            {
                region = nextId++;
                var anchor = Centre(bodies, members, anchoring);
                anchors[region] = anchor;
                plan.Create.Add(new Region(region, anchor));
            }
            claimed.Add(region);
            foreach (int i in members) plan.RegionOf[bodies[i].Id] = region;
        }
        foreach (var r in regions)
            if (r.Id != Main && !claimed.Contains(r.Id)) plan.Drop.Add(r.Id);
        return plan;
    }

    /// <summary>The existing region most of <paramref name="members"/> are in and no larger cluster took, or <see cref="None"/>.</summary>
    private static int Claim(IReadOnlyList<Body> bodies, List<int> members, Dictionary<int, GlobalPos> anchors, HashSet<int> claimed)
    {
        int best = None, bestCount = 0;
        var counts = new Dictionary<int, int>();
        foreach (int i in members)
        {
            int r = bodies[i].Region;
            if (r == None || claimed.Contains(r) || !anchors.ContainsKey(r)) continue;
            int c = counts[r] = counts.GetValueOrDefault(r) + 1;
            if (c > bestCount || c == bestCount && r < best) { best = r; bestCount = c; }
        }
        return best;
    }

    /// <summary>Whether a region's members reach past <see cref="ReanchorM"/> and their centre has moved half as far from the anchor.</summary>
    private static bool Drifted(IReadOnlyList<Body> bodies, List<int> members, GlobalPos anchor, Anchoring anchoring)
    {
        double far = 0;
        foreach (int i in members) far = Math.Max(far, bodies[i].At.HorizontalDistanceTo(anchor));
        return far > ReanchorM && Centre(bodies, members, anchoring).HorizontalDistanceTo(anchor) > ReanchorM / 2;
    }

    /// <summary>
    /// A frame's anchor for <paramref name="members"/>: their mean position snapped to the whole LV95 km at altitude 0
    /// (a flat frame), or their Earth mean, the mean of their ECEF positions put on the ellipsoid at height 0 (an ENU frame:
    /// no LV95 in it, so it holds anywhere on the Earth).
    /// </summary>
    public static GlobalPos Centre(IReadOnlyList<Body> bodies, List<int> members, Anchoring anchoring = Anchoring.Lv95Km)
    {
        if (anchoring == Anchoring.Earth)
        {
            double x = 0, y = 0, z = 0;
            foreach (int i in members) { var p = bodies[i].At.Ecef; x += p.X; y += p.Y; z += p.Z; }
            return EarthMean(new Terrain.Format.EcefPos(x / members.Count, y / members.Count, z / members.Count));
        }
        double e = 0, nn = 0;
        foreach (int i in members) { var l = bodies[i].At.Lv95; e += l.E; nn += l.N; }   // the anchor is an LV95 km (a flat frame)
        return GlobalPos.FromLv95(Math.Round(e / members.Count / 1000) * 1000, Math.Round(nn / members.Count / 1000) * 1000, 0);
    }

    /// <summary>An ECEF point (a mean, below the ground) put on the ellipsoid along its normal: height 0 there.</summary>
    public static GlobalPos EarthMean(Terrain.Format.EcefPos mean)
    {
        var g = Terrain.Format.Ellipsoid.Wgs84.ToGeodetic(mean);
        return GlobalPos.FromEcef(Terrain.Format.Ellipsoid.Wgs84.ToEcef(g with { H = 0 }));
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i) i = parent[i] = parent[parent[i]];
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
    }
}
