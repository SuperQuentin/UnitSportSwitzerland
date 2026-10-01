using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

namespace UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Roundabouts (#122) as one object. TLM draws a ring as a polygon of chords between its arms
/// (<c>kreisel</c>, or OSM <c>junction=roundabout</c>: the <see cref="RoadAttrFlags.Roundabout"/>
/// lines); #117 already orients them counter-clockwise and #121 makes the ring the main road of
/// every node on it. Here, before the block's lines enter the graph:
/// <list type="bullet">
/// <item>the ring's lines are grouped by shared ends and a circle is fitted to all their points;
/// a group that is not a closed, round ring (a large oval, a ring cut by the halo) is left as it
/// came;</item>
/// <item>each ring line's plan is rebuilt as an arc of that circle, its ends at one angle per
/// node so the ring stays closed; heights stay the original line's (nearest point);</item>
/// <item>an arm ending on the old ring is moved onto the new one;</item>
/// <item>the ring is returned for its central island (<see cref="Island"/>).</item>
/// </list>
/// </summary>
public static class RoundaboutShaper
{
    public sealed class Stats
    {
        public int Groups, Rounded, Mini, NotClosed, NotRound, BadRadius, Lines, ArmEnds, Islands;
        public double MaxShift, SumRadius;

        public string Format() => string.Create(CultureInfo.InvariantCulture, $"""
              roundabouts (#122): ring groups {Groups:N0} (by attribute), rounded {Rounded:N0} (mini {Mini:N0}), left as drawn: not closed {NotClosed:N0}, not round {NotRound:N0}, radius out of range {BadRadius:N0}
                ring lines rebuilt {Lines:N0}, arm ends moved onto the ring {ArmEnds:N0}, largest shift {MaxShift:F2} m, mean ring radius {(Rounded > 0 ? SumRadius / Rounded : 0):F1} m; islands written {Islands:N0}
            """);
    }

    /// <summary>A rounded ring: centre (LV95), centreline radius, carriageway width, height samples by angle.</summary>
    public sealed record Ring(Vec2 Centre, double Radius, double Width, List<(double Angle, double Height)> Heights)
    {
        /// <summary>The island's radius: the ring's centreline radius less half its carriageway.</summary>
        public double Inner => Radius - Width * 0.5;

        public bool Mini => Inner < MiniIsland;

        /// <summary>The ring road's height at an angle: the nearest sample's.</summary>
        public double HeightAt(double angle)
        {
            double best = double.MaxValue, h = Heights.Count > 0 ? Heights[0].Height : 0;
            foreach (var (a, y) in Heights)
            {
                double d = Math.Abs(Math.IEEERemainder(a - angle, 2 * Math.PI));
                if (d < best) { best = d; h = y; }
            }
            return h;
        }
    }

    /// <summary>Centreline radius range of a ring taken as a roundabout.</summary>
    public const double MinRadius = 4, MaxRadius = 60;

    /// <summary>
    /// Below this island radius it is a mini-roundabout: a flush painted centre that a lorry may
    /// drive over, no raised island. TLM's rings in Riddes and Sion run 4.7-8.4 m about their
    /// centre, 3.3 m wide, so their islands are 3-7 m across the radius.
    /// </summary>
    public const double MiniIsland = 2.5;

    /// <summary>Raised island's kerb, the street kerb of #119.</summary>
    public const float IslandKerb = 0.12f;

    private const double JoinM = 0.5;
    private const double ArcStepM = 2.0;
    private const double MaxArcStep = 10 * Math.PI / 180;

    public static List<Ring> Shape(List<CrossSectionPlanner.Line> lines, Stats stats)
    {
        var rings = new List<Ring>();
        var ringLines = lines.Where(IsRing).ToList();
        if (ringLines.Count == 0) return rings;

        foreach (var group in Groups(ringLines))
        {
            stats.Groups++;
            var pts = group.SelectMany(l => l.Shifted).ToList();
            var (centre, radius, rms) = FitCircle(pts);
            if (!(radius >= MinRadius && radius <= MaxRadius)) { stats.BadRadius++; continue; }
            if (rms > Math.Max(0.4, 0.06 * radius)) { stats.NotRound++; continue; }
            if (!Closed(group, centre, radius)) { stats.NotClosed++; continue; }

            // one angle per node, so the lines meeting there still meet
            var nodes = new List<(Vec2 At, double Angle)>();
            double NodeAngle(Vec2 p)
            {
                foreach (var (at, a) in nodes) if (at.DistanceTo(p) <= JoinM) return a;
                double angle = Math.Atan2(p.Y - centre.Y, p.X - centre.X);
                nodes.Add((p, angle));
                return angle;
            }

            var heights = new List<(double, double)>();
            foreach (var l in group)
            {
                var plan = l.Shifted;
                double a0 = NodeAngle(plan[0]), a1 = NodeAngle(plan[^1]);
                // which way the line goes round: the sum of its turns about the centre
                double swept = 0;
                for (int i = 1; i < plan.Length; i++)
                    swept += Math.IEEERemainder(Angle(plan[i], centre) - Angle(plan[i - 1], centre), 2 * Math.PI);
                double delta = Math.IEEERemainder(a1 - a0, 2 * Math.PI);
                if (Math.Sign(delta) != Math.Sign(swept) && Math.Abs(swept) > 1e-6) delta += Math.Sign(swept) * 2 * Math.PI;
                if (Math.Abs(delta) < 1e-6) continue;

                int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(delta) * radius / ArcStepM, Math.Abs(delta) / MaxArcStep)));
                var arc = new Vec2[steps + 1];
                for (int s = 0; s <= steps; s++)
                {
                    double a = a0 + delta * s / steps;
                    arc[s] = new Vec2(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a));
                }
                foreach (var p in plan)
                    stats.MaxShift = Math.Max(stats.MaxShift, Math.Abs(p.DistanceTo(centre) - radius));
                l.Shifted = arc;
                stats.Lines++;

                // the original line's heights, by angle, for the island's rim
                var seg = l.Segment;
                for (int i = 0; i < seg.PointCount && i < l.Plan.Length; i++)
                    heights.Add((Angle(l.Plan[i], centre), seg.Points[i * 3 + 1]));
            }

            // arms ending on the old ring: onto the new one, at the node they met
            foreach (var l in lines)
            {
                if (group.Contains(l) || l.Shifted.Length < 2) continue;
                foreach (bool start in (ReadOnlySpan<bool>)[true, false])
                {
                    var end = start ? l.Shifted[0] : l.Shifted[^1];
                    if (Math.Abs(end.DistanceTo(centre) - radius) > 2.5) continue;
                    var node = nodes.Where(n => n.At.DistanceTo(end) <= 2.5).OrderBy(n => n.At.DistanceTo(end)).FirstOrDefault();
                    if (node.At == default) continue;
                    var moved = l.Shifted.ToArray();
                    moved[start ? 0 : moved.Length - 1] = new Vec2(centre.X + radius * Math.Cos(node.Angle), centre.Y + radius * Math.Sin(node.Angle));
                    l.Shifted = moved;
                    stats.ArmEnds++;
                }
            }

            float width = group.Max(l => l.Width > 0 ? l.Width : l.Segment.Width);
            var ring = new Ring(centre, radius, width, heights);
            rings.Add(ring);
            stats.Rounded++;
            stats.SumRadius += radius;
            if (ring.Mini) stats.Mini++;
        }
        return rings;
    }

    /// <summary>
    /// The central island in <paramref name="id"/>'s frame, or null: a fan of the circle inside the
    /// ring's inner edge, each rim vertex at the ring road's height there, raised by
    /// <see cref="IslandKerb"/> (variant 0, solid); a mini-roundabout's is flush and painted
    /// (variant 1, no kerb, traversable).
    /// </summary>
    public static RoadAreaProp? Island(Ring ring, TileId id)
    {
        double inner = ring.Inner;
        if (inner < 1.0) return null;
        int rim = Math.Clamp((int)Math.Ceiling(2 * Math.PI * inner / ArcStepM), 12, 48);
        var v = new float[(rim + 1) * 3];
        double sum = 0;
        for (int k = 0; k < rim; k++)
        {
            // counter-clockwise in plan (E, N); the island's own order does not matter, it is drawn two-sided
            double a = 2 * Math.PI * k / rim;
            double e = ring.Centre.X + inner * Math.Cos(a), n = ring.Centre.Y + inner * Math.Sin(a);
            double y = ring.HeightAt(a);
            sum += y;
            v[(k + 1) * 3] = (float)(e - id.MinE);
            v[(k + 1) * 3 + 1] = (float)y;
            v[(k + 1) * 3 + 2] = (float)(id.MaxN - n);
        }
        v[0] = (float)(ring.Centre.X - id.MinE);
        v[1] = (float)(sum / rim);
        v[2] = (float)(id.MaxN - ring.Centre.Y);
        var idx = new ushort[rim * 3];
        for (int k = 0; k < rim; k++)
        {
            idx[k * 3] = 0;
            idx[k * 3 + 1] = (ushort)(k + 1);
            idx[k * 3 + 2] = (ushort)((k + 1) % rim + 1);
        }
        bool mini = ring.Mini;
        return new RoadAreaProp
        {
            Type = AreaPropType.Island,
            Variant = mini ? (byte)1 : (byte)0,
            Flags = mini ? PropFlags.None : PropFlags.Solid,
            Height = mini ? 0f : IslandKerb,
            Vertices = v,
            Indices = idx,
        };
    }

    private static bool IsRing(CrossSectionPlanner.Line l) =>
        (l.Segment.Attributes.Has(RoadAttrFlags.Roundabout) || l.Osm is { Roundabout: true })
        && (l.Segment.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) == 0
        && l.Segment.Class <= RoadClass.Lane && l.Shifted.Length >= 2;

    private static double Angle(Vec2 p, Vec2 c) => Math.Atan2(p.Y - c.Y, p.X - c.X);

    /// <summary>Ring lines grouped by shared ends (within <see cref="JoinM"/>).</summary>
    private static List<List<CrossSectionPlanner.Line>> Groups(List<CrossSectionPlanner.Line> ring)
    {
        var parent = Enumerable.Range(0, ring.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int a = 0; a < ring.Count; a++)
            for (int b = a + 1; b < ring.Count; b++)
            {
                var pa = ring[a].Shifted; var pb = ring[b].Shifted;
                if (pa[0].DistanceTo(pb[0]) <= JoinM || pa[0].DistanceTo(pb[^1]) <= JoinM
                    || pa[^1].DistanceTo(pb[0]) <= JoinM || pa[^1].DistanceTo(pb[^1]) <= JoinM)
                    parent[Find(a)] = Find(b);
            }
        return ring.Select((l, i) => (l, i)).GroupBy(x => Find(x.i)).Select(g => g.Select(x => x.l).ToList()).ToList();
    }

    /// <summary>A closed ring: no gap of more than 60 degrees between its points, about the fitted centre.</summary>
    private static bool Closed(List<CrossSectionPlanner.Line> group, Vec2 centre, double radius)
    {
        var angles = new List<double>();
        foreach (var l in group)
        {
            var p = l.Shifted;
            for (int i = 0; i < p.Length; i++)
            {
                angles.Add(Angle(p[i], centre));
                if (i == 0) continue;
                // chords: their midpoints count too, so a two-point line still covers its arc
                angles.Add(Angle((p[i] + p[i - 1]) * 0.5, centre));
            }
        }
        angles.Sort();
        double gap = angles[0] + 2 * Math.PI - angles[^1];
        for (int i = 1; i < angles.Count; i++) gap = Math.Max(gap, angles[i] - angles[i - 1]);
        return gap <= 60 * Math.PI / 180;
    }

    /// <summary>Algebraic (Kåsa) circle fit about the points' mean: centre, radius, RMS radial residual.</summary>
    private static (Vec2 Centre, double Radius, double Rms) FitCircle(List<Vec2> pts)
    {
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
        foreach (var p in pts)
        {
            double u = p.X - mx, v = p.Y - my;
            suu += u * u; svv += v * v; suv += u * v;
            suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
        }
        double det = suu * svv - suv * suv;
        if (Math.Abs(det) < 1e-9) return (new Vec2(mx, my), 0, double.MaxValue);
        double bu = 0.5 * (suuu + suvv), bv = 0.5 * (svvv + svuu);
        double uc = (bu * svv - bv * suv) / det, vc = (suu * bv - suv * bu) / det;
        var centre = new Vec2(mx + uc, my + vc);
        double r = pts.Average(p => p.DistanceTo(centre));
        double rms = Math.Sqrt(pts.Average(p => (p.DistanceTo(centre) - r) * (p.DistanceTo(centre) - r)));
        return (centre, r, rms);
    }
}
