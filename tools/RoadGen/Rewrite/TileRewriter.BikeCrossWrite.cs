namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Layout;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// The riders' crossings of a planned junction (#818 phase 4). A bike lane going straight on follows its connector through the
/// core: yellow dashed edges, red on the half or halves where a car's connector crosses it (at the lights only where the two are
/// green together: the user's rule, #682); between two mouths facing each other (a jog) it goes straight across
/// (the user, Oct 16). A separated path follows the path's curve (the user's choice), a jog straight across: from
/// the arm's path at its mouth to the straight-on arm's, red over the carriageway by the same rule, its side's bands laid along
/// the curve up to the kerb with a sloped kerb strip there, and the corners told where the carried path meets the kerb. Where no
/// road joins on its side, the path's side runs on along the core's kerb instead of the sidewalk corner.
/// </summary>
public static partial class TileRewriter
{

    private static void WriteBikeCrossings(JunctionPlan jp, RoadGenResult result, Dictionary<int, (RoadSegment Segment, TileId Tile, RoadSegment Painted)> segmentOf,
        Dictionary<RoadSegment, List<RoadSegment>> finalPieces, SignalPlan? plan, RoadJunction? cap, Dictionary<TileId, List<RoadPaint>> paint,
        Dictionary<TileId, List<(RoadAreaProp Band, List<Vec2> Ring)>> bridges, Dictionary<TileId, List<CornerPlanner.PathEnd>> pathEnds, LayoutWriteStats stats,
        List<Vec2> landings)
    {
        var core = jp.Core!;
        var net = result.Network;
        var home = TileId.FromLv95(jp.Centre.X, jp.Centre.Y);
        var anchors = new List<(Vec2 At, float Height)>();
        foreach (var m in core.Mouths)
            if (net.Links[jp.Arms.First(a => a.Arm == m.Arm).LinkId].Tag is Source s) anchors.Add(((m.Left + m.Right) * 0.5, s.SampleHeight((m.Left + m.Right) * 0.5)));
        if (anchors.Count == 0) return;
        // on the cap's own surface where it has one (the strips' feet meet it with no step)
        float H(Vec2 p) => cap is null ? HeightAt(anchors, p) : CapHeightAt(cap, home, p);
        int PlanArm(int arm) => jp.Arms.FindIndex(a => a.Arm == arm);
        // a riders' way from one main arm to the other: along the main road, across the roads joining it
        bool AlongMain(int from, int to) => jp.MainArms.Contains(from) && jp.MainArms.Contains(to);
        // the group a connector's traffic goes on; -1 without lights
        int GroupOf(int arm, SignalMoves move, bool bike)
        {
            if (plan is null) return -1;
            int a = PlanArm(arm);
            int g = plan.Groups.FindIndex(x => x.Arm == a && (bike ? x.Kind == SignalGroupKind.Bike : x.Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow) && (x.Moves & move) != 0);
            if (g < 0 && bike) g = plan.Groups.FindIndex(x => x.Arm == a && x.Kind == SignalGroupKind.Car && (x.Moves & SignalMoves.Through) != 0);
            return g;
        }
        // where the car connectors cross a line (each hit's distance along it), those in conflict with the riders' group
        List<(double At, double Half)> Conflicts(List<Vec2> line, int ridersGroup)
        {
            var hits = new List<(double, double)>();
            foreach (var c in core.Connectors.Where(c => c.Kind == ConnectorKind.Car))
            {
                if (plan is not null && ridersGroup >= 0 && GroupOf(c.FromArm, c.Move, bike: false) is int g and >= 0 && !GreenTogether(plan, ridersGroup, g)) continue;
                double along = 0;
                for (int i = 0; i + 1 < line.Count; i++)
                {
                    for (int j = 0; j + 1 < c.Centre.Count; j++)
                        if (SegmentHit(line[i], line[i + 1], c.Centre[j], c.Centre[j + 1]) is { } u)
                            hits.Add((along + u * line[i].DistanceTo(line[i + 1]), c.Width * 0.5));
                    along += line[i].DistanceTo(line[i + 1]);
                }
            }
            return hits;
        }
        void Line(List<Vec2> pts, PaintType type, uint rgba, float width, float dash)
        {
            if (pts.Count < 2) return;
            Get(paint, home).Add(new RoadPaint
            {
                Shape = PaintShape.Polyline, Type = type, Rgba = rgba, Width = width, Dash = dash, Gap = dash,
                Vertices = RoadPaintGeometry.Simplify(Local(home, pts, H, 0f)),
            });
        }
        float lw = BikePlanner.LineWidth;

        // 1. bike lanes straight on, along their connector
        foreach (var c in core.Connectors.Where(c => c.Kind == ConnectorKind.Bike && c.Move == SignalMoves.Through))
        {
            // marked through the core only where the exit carries the riders' way on (the user, Oct 17): else they wait in their
            // box and ride on in the traffic, the connector unmarked
            if (jp.Arms.FirstOrDefault(x => x.Arm == c.ToArm) is not { Normal.CarriesBikesAway: true }) continue;
            // along its connector: the core's corners keep it paved (a straight line across the core cut a tight corner, the user,
            // Oct 17, J7 north-east); a path crossing goes straight across instead (below)
            List<Vec2> centre = c.Centre, left = c.Left, right = c.Right;
            Line(Inset(left, -lw * 0.5), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
            Line(Inset(right, lw * 0.5), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
            double length = Length(centre);
            // red on the half or halves where cars cross it in conflict (the old layout's rule, #682; the user, Oct 17); without lights
            // only along the main road (Oct 17: none across it, Sion 2505948,1137484)
            if (plan is not null || AlongMain(c.FromArm, c.ToArm))
            foreach (var (from, to) in RedRuns(Conflicts(centre, GroupOf(c.FromArm, SignalMoves.Through, bike: true)).Select(h => h.At / length).ToList()))
                Line(Cut(centre, from * length, to * length), PaintType.BikeCrossing, PaintEmitter.Red, (float)(Math.Min(c.Width, c.ToWidth) - 1.5 * lw - 2 * RedInset), 0);
            stats.BikeCrossings++;
        }

        var carriedSides = new List<(TileId Home, List<(RoadAreaProp Band, List<Vec2> Ring)> Bands)>();
        // 2. paths straight on, along the path's curve from one arm's path to the other's: each arm's side carried on from its
        // mouth to the kerb (PathsToKerb: every band to where it meets the carriageway, a sloped kerb there, the corners beside
        // starting where its outer edge meets the kerb), the crossing between the two kerb points
        foreach (var a in jp.Arms)
        {
            if (!a.Toward.TryGetValue(SignalMoves.Through, out int toArm) || jp.Arms.FirstOrDefault(x => x.Arm == toArm) is not { } b) continue;
            var ma = core.Mouths.FirstOrDefault(m => m.Arm == a.Arm);
            var mb = core.Mouths.FirstOrDefault(m => m.Arm == b.Arm);
            if (ma is null || mb is null || a.Axis is null || b.Axis is null) continue;
            // the path on the arriving traffic's right of A, and on the same side of B (its left, seen arriving there)
            var secA = ExteriorPlanner.SectionAt(a, 0);
            var secB = ExteriorPlanner.SectionAt(b, 0);
            if (PathBand(secA, right: true) is null || PathBand(secB, right: false) is null) continue;
            if (net.Links[a.LinkId].Tag is not Source srcA || net.Links[b.LinkId].Tag is not Source srcB) continue;
            var sideA = SideOfArm(a, right: true);
            var sideB = SideOfArm(b, right: false);
            if (!sideA.HasTrack || !sideB.HasTrack) continue;
            var ua = a.Axis.DirAt(ma.Station);
            var ub = b.Axis.DirAt(mb.Station);
            var ca = a.Axis.At(ma.Station, secA.CarriagewayRight);
            var cb = b.Axis.At(mb.Station, secB.CarriagewayLeft);
            var da = a.Axis.RightAt(ma.Station);
            var db = -b.Axis.RightAt(mb.Station);
            // where no road joins on that side, the side's bands run on along the core's kerb (the old layout's BridgePath):
            // the sidewalk corner they cover is dropped (Unbridged); profiles that differ keep the corner
            var between = jp.Arms.Where(x => x != a && x != b && Between(a.RingHeading, b.RingHeading, x.RingHeading)).ToList();
            if (between.Count == 0)
            {
                if (core.Corners.FirstOrDefault(c => c.ArmA == a.Arm && c.ArmB == b.Arm) is not { Shape: not CornerShape.Gore } corner) continue;
                var kerb = KerbAlong(ma.Right, corner.Kerb, mb.Left);
                if (BridgePath(home, sideA, sideB, ua, ub, (oa, ob) => OffsetAlong(kerb, oa, ob, da, db), H) is { } bands)
                {
                    Get(bridges, home).AddRange(bands);
                    stats.PathsThrough++;
                }
                continue;
            }
            // a crossed street with no sidewalk on the side facing a corner: only the path is carried round that corner, not the
            // sidewalk (the user, Oct 16: the bands filled the whole corner)
            double Ccw(ArmPlan x) { double d = (x.RingHeading - a.RingHeading) % (2 * Math.PI); return d < 0 ? d + 2 * Math.PI : d; }
            var nearA = between.MinBy(Ccw)!;
            var nearB = between.MaxBy(Ccw)!;
            if (SideOfArm(nearA, right: false).SidewalkDm == 0) sideA = sideA with { SidewalkDm = 0 };
            if (SideOfArm(nearB, right: true).SidewalkDm == 0) sideB = sideB with { SidewalkDm = 0 };
            bool OnRoad(Vec2 p) => Inside(core.Outline, p) || between.Any(x => OnArm(jp, x, p));
            if (PathsToKerb(home, OnRoad, sideA, sideB, ca, da, ua, 0, cb, db, ub, 0, ca.DistanceTo(cb) + 5, H, srcA.SampleHeight, srcB.SampleHeight, H) is not { } carried)
                continue;
            // as the old layout's carried sides: bands the sidewalk corners keep clear of, each side apart (laid after the loop)
            carriedSides.Add((home, carried.Bands.GetRange(0, carried.SplitAt)));
            carriedSides.Add((home, carried.Bands.GetRange(carried.SplitAt, carried.Bands.Count - carried.SplitAt)));
            foreach (var end in carried.Ends) Get(pathEnds, home).Add(end);
            stats.PathsCarried += 2;
            // the crossing: from kerb to kerb at the paths' middles, leaving and joining along the arms
            double pathA = sideA.BikeDm / 10.0, pathB = sideB.BikeDm / 10.0;
            var k0 = carried.KerbA(sideA.VergeDm / 10.0 + pathA * 0.5);
            var k1 = carried.KerbB(sideB.VergeDm / 10.0 + pathB * 0.5);
            double l = k0.DistanceTo(k1) / 3;
            var run = new List<Vec2>();
            if (Jog(-ua, ub)) run = Straight(k0, k1);
            else
                for (int k = 0; k <= 24; k++)
                {
                    double t = k / 24.0, mt = 1 - t;
                    run.Add(k0 * (mt * mt * mt) + (k0 - ua * l) * (3 * mt * mt * t) + (k1 - ub * l) * (3 * mt * t * t) + k1 * (t * t * t));
                }
            double width = Math.Min(pathA, pathB);
            landings.Add(k0);
            landings.Add(k1);
            // red on the half or halves where a car crosses it in the riders' phase (#682, the user, Oct 17; without lights only along
            // the main road), yellow edges all across
            int riders = GroupOf(a.Arm, SignalMoves.Through, bike: true);
            double runLength = Length(run);
            var reds = plan is null && !AlongMain(a.Arm, b.Arm) ? [] : RedRuns(Conflicts(run, riders).Select(h => h.At / runLength).ToList());
            foreach (var (from, to) in reds)
                Line(Cut(run, from * runLength, to * runLength), PaintType.BikeCrossing, PaintEmitter.Red, (float)(width - 2 * lw - 2 * RedInset), 0);
            Line(OffsetLine(run, width * 0.5 - lw * 0.5), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
            Line(OffsetLine(run, -(width * 0.5 - lw * 0.5)), PaintType.YellowDashed, PaintEmitter.Yellow, lw, BikePlanner.JunctionDash);
            stats.PathCrossings++;
        }
        stats.PathsCrossingInCorner += LayCarriedSides(carriedSides, bridges);

        // an arm's side at its mouth as the street planner laid it, seen arriving
        RoadSide SideOfArm(ArmPlan arm, bool right)
        {
            var so = segmentOf[arm.LinkId];
            var pieces = finalPieces.TryGetValue(so.Segment, out var ps) && ps.Count > 0 ? ps : [so.Segment];
            var (l, r) = SidesAtMouth(arm, arm.AtEnd ? pieces[^1] : pieces[0]);
            return right ? r : l;
        }
    }

    /// <summary>A path band beside a section's carriageway on one side: its centre's offset and its width; null without one.</summary>
    private static (double Centre, double Width)? PathBand(Section s, bool right)
    {
        double at = right ? s.CarriagewayRight : s.CarriagewayLeft;
        var bands = right ? s.Strips.Skip(s.LastCarriageway + 1) : s.Strips.Take(Math.Max(0, s.FirstCarriageway)).Reverse();
        foreach (var b in bands)
        {
            if (b.Kind == StripKind.Path) return (right ? at + b.Width * 0.5 : at - b.Width * 0.5, b.Width);
            if (b.Kind == StripKind.Sidewalk) return null;
            at += right ? b.Width : -b.Width;
        }
        return null;
    }

    /// <summary>Whether outward heading <paramref name="h"/> lies counter-clockwise from <paramref name="from"/> before <paramref name="to"/>: an arm on the right of traffic arriving by the first and going on by the second.</summary>
    private static bool Between(double from, double to, double h)
    {
        double span = ((to - from) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        double d = ((h - from) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        return d > 1e-3 && d < span - 1e-3;
    }

    /// <summary>Whether <paramref name="p"/> lies on arm <paramref name="a"/>'s carriageway within 30 m of its mouth.</summary>
    private static bool OnArm(JunctionPlan jp, ArmPlan a, Vec2 p)
    {
        var m = jp.Core!.Mouths.FirstOrDefault(x => x.Arm == a.Arm);
        if (m is null || a.Axis is null) return false;
        double s = a.Axis.StationOf(p) - m.Station;
        if (s < 0 || s > 30) return false;
        var q = a.Axis.PointAt(m.Station + s);
        double o = (p - q).Dot(a.Axis.RightAt(m.Station + s));
        var sec = ExteriorPlanner.SectionAt(a, s);
        return o >= sec.CarriagewayLeft && o <= sec.CarriagewayRight;
    }

    private static double Length(List<Vec2> line)
    {
        double sum = 0;
        for (int i = 1; i < line.Count; i++) sum += line[i].DistanceTo(line[i - 1]);
        return sum;
    }

    /// <summary>A path crossing ending this close to a core corner's kerb (m) lands on that corner.</summary>
    private const double LandingReach = 1.5;

    /// <summary>The distance from <paramref name="p"/> to the nearest point of a polyline.</summary>
    private static double DistanceToPolyline(List<Vec2> line, Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var ab = line[i + 1] - line[i];
            double l2 = ab.Dot(ab), t = l2 < 1e-12 ? 0 : Math.Clamp((p - line[i]).Dot(ab) / l2, 0, 1);
            best = Math.Min(best, p.DistanceTo(line[i] + ab * t));
        }
        return best;
    }

    /// <summary>The part of a polyline between two distances along it.</summary>
    private static List<Vec2> Cut(List<Vec2> line, double from, double to)
    {
        var result = new List<Vec2>();
        double s = 0;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            double len = line[i].DistanceTo(line[i + 1]), s1 = s + len;
            if (s1 >= from && s <= to && len > 1e-9)
            {
                if (result.Count == 0) result.Add(line[i] + (line[i + 1] - line[i]) * Math.Clamp((from - s) / len, 0, 1));
                if (s1 < to) result.Add(line[i + 1]);
                else { result.Add(line[i] + (line[i + 1] - line[i]) * Math.Clamp((to - s) / len, 0, 1)); break; }
            }
            s = s1;
        }
        return result;
    }

    /// <summary>A crossing turning less than this between its two ends is a jog, laid straight; more, a bend, laid along the curve.</summary>
    private static readonly double JogAngle = 10 * Math.PI / 180;

    private static bool Jog(Vec2 inward, Vec2 outward) =>
        inward.Length > 1e-9 && outward.Length > 1e-9 && inward.Normalized().Dot(outward.Normalized()) > Math.Cos(JogAngle);

    private static List<Vec2> Straight(Vec2 from, Vec2 to) => Enumerable.Range(0, 25).Select(k => from + (to - from) * (k / 24.0)).ToList();

    /// <summary>A polyline moved <paramref name="by"/> metres to its right (per-vertex bisector).</summary>
    private static List<Vec2> OffsetLine(List<Vec2> line, double by)
    {
        var result = new List<Vec2>(line.Count);
        for (int i = 0; i < line.Count; i++)
        {
            var d = line[Math.Min(line.Count - 1, i + 1)] - line[Math.Max(0, i - 1)];
            if (d.Length < 1e-9) { result.Add(line[i]); continue; }
            d = d / d.Length;
            result.Add(line[i] + new Vec2(d.Y, -d.X) * by);
        }
        return result;
    }

    /// <summary>A core's kerb from one mouth's corner point to the next one's, through the corner's own kerb, resampled every half metre or less.</summary>
    private static List<Vec2> KerbAlong(Vec2 from, List<Vec2> corner, Vec2 to)
    {
        var line = new List<Vec2> { from };
        foreach (var p in corner.Append(to))
            if (p.DistanceTo(line[^1]) > 1e-3) line.Add(p);
        var dense = new List<Vec2> { line[0] };
        for (int i = 0; i + 1 < line.Count; i++)
        {
            int n = Math.Max(1, (int)Math.Ceiling(line[i].DistanceTo(line[i + 1]) / 0.5));
            for (int k = 1; k <= n; k++) dense.Add(line[i] + (line[i + 1] - line[i]) * ((double)k / n));
        }
        return dense;
    }

    /// <summary>
    /// A kerb moved outward (to its right: the core's outline runs counter-clockwise) by an offset easing from
    /// <paramref name="oa"/> to <paramref name="ob"/> along it, every point kept (the bands' lines pair up); the ends move
    /// square off their arms (<paramref name="da"/>, <paramref name="db"/>), the inner points along their bisector, mitred.
    /// </summary>
    private static List<Vec2> OffsetAlong(List<Vec2> kerb, double oa, double ob, Vec2 da, Vec2 db)
    {
        double total = Math.Max(Length(kerb), 1e-9), along = 0;
        var result = new List<Vec2>(kerb.Count);
        for (int i = 0; i < kerb.Count; i++)
        {
            if (i > 0) along += kerb[i].DistanceTo(kerb[i - 1]);
            double o = oa + (ob - oa) * along / total;
            Vec2 n;
            if (i == 0) n = da;
            else if (i == kerb.Count - 1) n = db;
            else
            {
                Vec2 Right(Vec2 d) { d = d / Math.Max(d.Length, 1e-9); return new Vec2(d.Y, -d.X); }
                Vec2 n0 = Right(kerb[i] - kerb[i - 1]), n1 = Right(kerb[i + 1] - kerb[i]), sum = n0 + n1;
                n = sum.Length < 1e-9 ? n0 : sum / sum.Length;
                n = n / Math.Max(n.Dot(n0), 0.5);
            }
            result.Add(kerb[i] + n * o);
        }
        return result;
    }

    /// <summary>A connector edge moved <paramref name="by"/> to its right.</summary>
    private static List<Vec2> Inset(List<Vec2> line, double by) => OffsetLine(line, by);

    /// <summary>The unit normal on the right of a polyline's last stretch.</summary>
    private static Vec2 Normal(List<Vec2> line)
    {
        var d = line[^1] - line[^2];
        d = d / Math.Max(d.Length, 1e-9);
        return new Vec2(d.Y, -d.X);
    }

    /// <summary>Where segment a-b crosses c-d, as a fraction along a-b; null where they do not.</summary>
    private static double? SegmentHit(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        var r = b - a;
        var s = d - c;
        double den = r.Cross(s);
        if (Math.Abs(den) < 1e-12) return null;
        double t = (c - a).Cross(s) / den, u = (c - a).Cross(r) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? t : null;
    }
}

public static partial class TileRewriter
{
    /// <summary>
    /// The height of a written cap's surface at an LV95 point (#818): its triangle there, barycentric; off the cap, its nearest
    /// vertex's (paint and strips laid on a core stand on its surface, not on a blend of the mouths).
    /// </summary>
    private static float CapHeightAt(RoadJunction cap, TileId home, Vec2 p)
    {
        double x = p.X - home.MinE, z = home.MaxN - p.Y;
        var v = cap.Vertices;
        for (int t = 0; t + 2 < cap.Indices.Length; t += 3)
        {
            int a = cap.Indices[t] * 3, b = cap.Indices[t + 1] * 3, c = cap.Indices[t + 2] * 3;
            double d = (v[b + 2] - v[c + 2]) * (v[a] - v[c]) + (v[c] - v[b]) * (v[a + 2] - v[c + 2]);
            if (Math.Abs(d) < 1e-12) continue;
            double wa = ((v[b + 2] - v[c + 2]) * (x - v[c]) + (v[c] - v[b]) * (z - v[c + 2])) / d;
            double wb = ((v[c + 2] - v[a + 2]) * (x - v[c]) + (v[a] - v[c]) * (z - v[c + 2])) / d;
            double wc = 1 - wa - wb;
            if (wa >= -1e-6 && wb >= -1e-6 && wc >= -1e-6) return (float)(wa * v[a + 1] + wb * v[b + 1] + wc * v[c + 1]);
        }
        int best = 0;
        double near = double.MaxValue;
        for (int i = 0; i * 3 < v.Length; i++)
        {
            double dd = (v[i * 3] - x) * (v[i * 3] - x) + (v[i * 3 + 2] - z) * (v[i * 3 + 2] - z);
            if (dd < near) { near = dd; best = i; }
        }
        return v[best * 3 + 1];
    }
}
