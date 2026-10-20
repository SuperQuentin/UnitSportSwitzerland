using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.TestRegion;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Intersections laid out from lane data (#700), on the test region's J6 (a 2+2 artery whose OSM
/// <c>turn:lanes</c> are assigned to its own lanes) and J7 (<c>lanes:forward=3</c> on the way before the
/// lights: a double left pocket; the east way marks no left lane: no pocket).
/// </summary>
public class LaneDataRegionTests(SignalTestRegionFixture region) : IClassFixture<SignalTestRegionFixture>
{
    private RoadApproach Approach(string junction, bool east, bool west = false, bool north = false, bool south = false)
    {
        var j = region.Junction(junction);
        var tile = region.Tile(j.E, j.N);
        int k = tile.Signals.Select((s, i) => (s, i)).MinBy(x =>
            Math.Pow(TileId.FromLv95(j.E, j.N).MinE + x.s.X - j.E, 2) + Math.Pow(TileId.FromLv95(j.E, j.N).MaxN - x.s.Z - j.N, 2)).i;
        return tile.Approaches.Single(a => a.Signal == k
            && (east ? Math.Cos(a.Heading) > 0.95 : west ? Math.Cos(a.Heading) < -0.95 : north ? Math.Sin(a.Heading) > 0.95 : Math.Sin(a.Heading) < -0.95));
    }

    private static string Cars(RoadApproach a) =>
        string.Join("|", a.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).Select(l =>
            (l.Moves.HasFlag(SignalMoves.Left) ? "L" : "") + (l.Moves.HasFlag(SignalMoves.Through) ? "T" : "") + (l.Moves.HasFlag(SignalMoves.Right) ? "R" : "")));

    [Fact]
    public void The_artery_is_twelve_metres_and_its_lanes_are_assigned_in_place()
    {
        var j = region.Junction("J6");
        var tile = region.Tile(j.E, j.N);
        // 2 + 2 lanes: 4 x 3 m
        Assert.Contains(tile.Segments, s => s.Class == RoadClass.Major && Math.Abs(s.Width - 12f) < 0.01f);

        foreach (var arm in new[] { Approach("J6", east: false, west: true), Approach("J6", east: true) })
        {
            Assert.Equal("L|TR", Cars(arm));
            var cars = arm.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).ToList();
            // the carriageway's own lanes, a lane's width apart, there all along: nothing widened
            Assert.Equal(3f, cars[1].Offset - cars[0].Offset, 2);
            Assert.All(cars, l => Assert.True(l.TaperFrom >= l.FullFrom));
        }
        // the data's arrows on them: a left arrow in the left lane and a straight + right in the other
        var variants = tile.Paint.Where(p => p.Type == PaintType.Arrow).Select(p => (PaintArrow)p.Variant).ToHashSet();
        Assert.Contains(PaintArrow.Left, variants);
        Assert.Contains(PaintArrow.Straight | PaintArrow.Right, variants);
    }

    [Fact]
    public void J10s_bike_lanes_lie_between_from_data_and_its_right_turns_go_with_the_through_green()
    {
        // OSM's bicycle:lanes puts each bike lane between the through lane and the right pocket (#854): the planner's own rule
        // would put them at the kerb (every arm turns left, as at J1), and the right turn runs with the through traffic
        foreach (var (e, w, nn, so) in new[] { (true, false, false, false), (false, true, false, false), (false, false, true, false), (false, false, false, true) })
        {
            var a = Approach("J10", e, w, nn, so);
            var j = region.Junction("J10");
            var plan = region.Tile(j.E, j.N).Signals[a.Signal].Plan;
            Assert.Equal("L|T|R", Cars(a));
            int through = a.Lanes.FindIndex(l => l.Kind == ApproachLaneKind.Car && l.Moves == SignalMoves.Through);
            int right = a.Lanes.FindIndex(l => l.Kind == ApproachLaneKind.Car && l.Moves == SignalMoves.Right);
            Assert.Contains(a.Lanes.Skip(through + 1).Take(right - through - 1), l => l.Kind == ApproachLaneKind.Bike);
            Assert.False(plan.ThroughWithRightHeld(a.SignalArm));
            Assert.DoesNotContain(plan.Groups, g => g.Arm == a.SignalArm && g.Kind == SignalGroupKind.Bike);
        }
        // J1, the same crossroads by the rule: kerbside, a bike signal, the right arrow held while the riders go
        var j1 = Approach("J1", east: false, west: true);
        var j1j = region.Junction("J1");
        Assert.True(region.Tile(j1j.E, j1j.N).Signals[j1.Signal].Plan.ThroughWithRightHeld(j1.SignalArm));
    }

    [Fact]
    public void The_cross_road_gets_its_pocket_from_data()
    {
        Assert.Equal("L|TR", Cars(Approach("J6", east: false, south: true)));
        Assert.Equal("L|TR", Cars(Approach("J6", east: false, north: true)));
    }

    [Fact]
    public void A_double_left_pocket_from_lanes_forward_3_and_the_turn_lanes()
    {
        var west = Approach("J7", east: false, west: true);
        Assert.Equal("L|L|TR", Cars(west));
        var cars = west.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).ToList();
        // two pocket lanes of one lane width, the left-turn bike lane between them and the through lane
        float lane = cars[1].Offset - cars[0].Offset;
        Assert.InRange(lane, 2.8f, 4.2f);
        Assert.True(cars[2].Offset - cars[1].Offset >= lane - 0.01f);
        Assert.True(cars[0].Offset < cars[1].Offset);
        // the pocket lanes appear together: a lane change out of the through lane, not two
        Assert.All(cars.Take(2), l => Assert.Equal(SignalMoves.Left, l.Moves));
    }

    [Fact]
    public void No_left_pocket_where_the_data_marks_no_left_lane()
    {
        // a road leaves to the left of the east approach, but its way says through | through
        Assert.Equal("T", Cars(Approach("J7", east: true)));
    }

    [Fact]
    public void Same_turn_lanes_stay_apart_through_the_junction_with_a_dashed_line()
    {
        var j = region.Junction("J7");
        var id = TileId.FromLv95(j.E, j.N);
        double cx = j.E - id.MinE, cz = id.MaxN - j.N;
        var guides = region.Tile(j.E, j.N).Paint.Where(p => p.Type == PaintType.WhiteDashed && p.Dash == 1f && p.Shape == PaintShape.Polyline).ToList();
        // the cross road's two lanes straight across, each way: a line along the road, 2 m off its centre line, from one side of the junction to the other
        int straight = guides.Count(p =>
        {
            var v = p.Vertices;
            double x0 = v[0] - cx, z0 = v[2] - cz, x1 = v[^3] - cx, z1 = v[^1] - cz;
            return Math.Abs(x0 - x1) < 0.2 && Math.Abs(Math.Abs(x0) - 3) < 1.2 && Math.Sign(z0) != Math.Sign(z1) && Math.Abs(z1 - z0) > 15;
        });
        Assert.Equal(2, straight);
        // the double left from the west into the north arm's two lanes: one line from the west arm's side to the north arm's
        Assert.Contains(guides, p => p.Vertices[0] - cx < -5 && p.Vertices[^1] - cz < -5 && p.Vertices.Length > 12);
    }

    [Fact]
    public void A_double_left_lead_in_hatch_is_at_most_one_lane_wide()
    {
        var j = region.Junction("J7");
        var id = TileId.FromLv95(j.E, j.N);
        double cz = id.MaxN - j.N;
        // the west approach's hatch (south of the centre line, west of the junction): no stripe further out than one lane
        var hatch = region.Tile(j.E, j.N).Paint.Where(p => p.Type == PaintType.Hatch && p.Vertices[0] < j.E - id.MinE - 20
            && Enumerable.Range(0, p.Vertices.Length / 3).Average(i => p.Vertices[i * 3 + 2]) > cz - 3).ToList();
        Assert.NotEmpty(hatch);
        // across the road, from its own centre line (a split lead-in, #700, moves that off the axis)
        // each stripe runs from the centre line to the border at 45 degrees: its extent across is the hatch's width there
        var stripes = hatch.SelectMany(p => Enumerable.Range(0, p.Vertices.Length / 12).Select(q => Enumerable.Range(q * 4, 4).Select(i => (double)p.Vertices[i * 3 + 2]).ToList())).ToList();
        double widest = stripes.Max(s => s.Max() - s.Min());
        // (#818: its edge runs straight from its tip to the closing line's far end, a lane's width less the slant's share there;
        // the second left lane opens beside it from the through lanes, the user again Oct 18)
        Assert.InRange(widest, 2.0, 3.6);
    }

    /// <summary>Whether a plan point (tile-local x east, z south) lies on a junction cap or a flush pavement patch.</summary>
    private static bool Paved(RoadTile tile, double x, double z)
    {
        static bool In(float[] v, ushort[] idx, double x, double z)
        {
            for (int k = 0; k + 2 < idx.Length; k += 3)
            {
                double ax = v[idx[k] * 3], az = v[idx[k] * 3 + 2], bx = v[idx[k + 1] * 3], bz = v[idx[k + 1] * 3 + 2], cx = v[idx[k + 2] * 3], cz = v[idx[k + 2] * 3 + 2];
                double d1 = (x - bx) * (az - bz) - (ax - bx) * (z - bz), d2 = (x - cx) * (bz - cz) - (bx - cx) * (z - cz), d3 = (x - ax) * (cz - az) - (cx - ax) * (z - az);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                if (!(neg && pos)) return true;
            }
            return false;
        }
        return tile.Junctions.Any(j => In(j.Vertices, j.Indices, x, z))
            || tile.AreaProps.Any(a => a.Type == AreaPropType.Pavement && In(a.Vertices, a.Indices, x, z));
    }

    [Fact]
    public void A_corner_no_right_turn_rounds_stays_tight()
    {
        // J7's east approach has no right turn (turn:lanes through|through): its corner into the north arm stays tight;
        // the north-west corner (the north arm's approach turns right into the west arm there) keeps its kerb arc
        var j = region.Junction("J7");
        var id = TileId.FromLv95(j.E, j.N);
        var tile = region.Tile(j.E, j.N);
        double cx = j.E - id.MinE, cz = id.MaxN - j.N;
        // (#818) at the mouths the north arm runs from 7.5 m west to 6 m east, the east arm's north edge lies 5.75 m north and
        // the west arm's 7.75 m: a point a metre out from where the edges meet stays off a tight corner, inside a kerb arc
        Assert.False(Paved(tile, cx + 7, cz - 6.75), "the north-east corner is paved past its edges: still rounded");
        Assert.True(Paved(tile, cx - 8.5, cz - 8.75), "the north-west corner should keep its kerb arc");
    }

    /// <summary>The zebras (yellow bar triangles) within <paramref name="radius"/> m of a point, each as its bars' corner points.</summary>
    private static List<List<(double X, double Z)>> Zebras(RoadTile tile, double cx, double cz, double radius) =>
        tile.Paint.Where(p => p.Type == PaintType.YellowSolid && p.Shape == PaintShape.Triangles)
            .Select(p => Enumerable.Range(0, p.Vertices.Length / 3).Select(i => ((double)p.Vertices[i * 3], (double)p.Vertices[i * 3 + 2])).ToList())
            .Where(pts => pts.All(q => Math.Abs(q.Item1 - cx) < radius && Math.Abs(q.Item2 - cz) < radius)).ToList();

    [Fact]
    public void Marked_osm_crossings_get_zebras_and_unmarked_ones_none()
    {
        // the T 52 m south of J3, without lights: zebras on the stem (north) and the west arm, none on the east arm (unmarked)
        var j3 = region.Junction("J3-");
        var id = TileId.FromLv95(j3.E, j3.N - 52);
        double cx = j3.E - id.MinE, cz = id.MaxN - (j3.N - 52);
        var zebras = Zebras(region.Tile(j3.E, j3.N - 52), cx, cz, 25);
        Assert.Equal(2, zebras.Count);
        Assert.Contains(zebras, z => z.Average(q => q.Z) < cz - 3);   // on the stem, north of the T
        Assert.Contains(zebras, z => z.Average(q => q.X) < cx - 3);   // on the west arm
        Assert.DoesNotContain(zebras, z => z.Average(q => q.X) > cx + 3);
    }

    [Fact]
    public void At_lights_with_crossing_data_the_crosswalks_go_only_on_the_mapped_arms()
    {
        // J3 has sidewalks on its north arm, but OSM maps its only crossing on the west arm (#711)
        var j3 = region.Junction("J3-");
        var id = TileId.FromLv95(j3.E, j3.N);
        double cx = j3.E - id.MinE, cz = id.MaxN - j3.N;
        var zebras = Zebras(region.Tile(j3.E, j3.N), cx, cz, 30);
        Assert.Contains(zebras, z => z.Average(q => q.X) < cx - 3);   // on the west arm
        Assert.DoesNotContain(zebras, z => z.Average(q => q.Z) < cz - 3);   // none on the north arm
        // a pedestrian signal only where there is a crosswalk: on the west arm
        var signal = region.Tile(j3.E, j3.N).Signals.MinBy(s => Math.Abs(s.X - cx) + Math.Abs(s.Z - cz))!;
        var walk = signal.Plan.Groups.Where(g => g.Kind == SignalGroupKind.Pedestrian).ToList();
        Assert.Single(walk);
        Assert.True(Math.Abs(Math.Cos(signal.Plan.Arms[walk[0].Arm].Heading) + 1) < 0.1, "the pedestrian group should cross the west arm");
        Assert.All(signal.Poles.Where(p => (p.Flags & SignalPoleFlags.Pedestrian) != 0), p => Assert.Equal(walk[0].Arm, p.Arm));
    }

    [Fact]
    public void At_lights_a_left_turn_with_its_bike_lane_is_guided_and_the_centre_line_is_solid_before_the_stop_line()
    {
        // J3 (#711, the user's review): the west and east pockets' left turns go with a left-turn bike lane, each guided by
        // dashed lines. #818 (the user's review): the cars' inner edge is guided only toward a raised island on the exit side, the
        // line between cars and bikes only into an exit that carries the bikes away: four guides here. The north arm (one lane
        // each way) has its centre line solid before the stop line
        var j3 = region.Junction("J3-");
        var id = TileId.FromLv95(j3.E, j3.N);
        double cx = j3.E - id.MinE, cz = id.MaxN - j3.N;
        var paint = region.Tile(j3.E, j3.N).Paint;
        bool Near(RoadPaint p, double r) => Enumerable.Range(0, p.Vertices.Length / 3)
            .All(i => Math.Abs(p.Vertices[i * 3] - cx) < r && Math.Abs(p.Vertices[i * 3 + 2] - cz) < r);
        int guides = paint.Count(p => p.Type == PaintType.WhiteDashed && p.Dash == 1f && p.Gap == 1f && Near(p, 30));
        Assert.True(guides >= 4, $"{guides} left-turn guides");
        // the north arm runs north (z smaller); its centre line is on its axis, solid from the stop line 20 m out
        Assert.Contains(paint, p => p.Type == PaintType.WhiteSolid && p.Shape == PaintShape.Polyline && p.Vertices.Length >= 6
            && Enumerable.Range(0, p.Vertices.Length / 3).All(i => Math.Abs(p.Vertices[i * 3] - cx) < 0.5 && p.Vertices[i * 3 + 2] < cz - 10)
            && p.Vertices.Where((_, k) => k % 3 == 2).Max() - p.Vertices.Where((_, k) => k % 3 == 2).Min() > 18);
    }

    [Fact]
    public void A_crossing_beside_a_tight_corner_runs_square_outside_the_mouth()
    {
        // J7 has no sidewalks: its zebras come from the data, on the north and east arms beside the tight north-east corner.
        // (#818) a crosswalk lies square across its arm outside the mouth, the tight corner or not (no longer diagonal, #700)
        var j = region.Junction("J7");
        var id = TileId.FromLv95(j.E, j.N);
        double cx = j.E - id.MinE, cz = id.MaxN - j.N;
        var zebras = Zebras(region.Tile(j.E, j.N), cx, cz, 45);
        Assert.Equal(2, zebras.Count);
        // the north arm's: its bars run north-south, the band's two ends level
        var north = zebras.Single(z => z.Average(q => q.Z) < cz - 8);
        double west = north.Where(q => q.X < cx - 4).Average(q => q.Z), east = north.Where(q => q.X > cx + 4).Average(q => q.Z);
        Assert.True(Math.Abs(east - west) < 0.3, $"not square: {east - west:F1} m");
    }

    [Fact]
    public void Hatch_stripes_lean_the_way_they_push_the_traffic()
    {
        // J3b's east arm (#818): one hatch strip south of the arriving lanes, its exit part at the mouth (its south edge moving, the
        // departing lane's, traffic driving east) and further east the lead-in into the left pocket (its north edge moving, the
        // arriving lanes', traffic driving west). Going with the traffic, a stripe runs out toward its lane (#406): both lean
        // their north end west. J1's west arm: the exit hatch north of the pocket (traffic driving west, the lane north) the same.
        int exit = 0, leadIn = 0, west = 0;
        void Lean(string name, double fromX, double toX, Action count)
        {
            var j = region.Junction(name);
            var id = TileId.FromLv95(j.E, j.N);
            double cx = j.E - id.MinE;
            foreach (var hatch in region.Tile(j.E, j.N).Paint.Where(p => p.Type == PaintType.Hatch))
            {
                var v = hatch.Vertices;
                for (int q = 0; q + 3 < v.Length / 3; q += 4)
                {
                    var pts = Enumerable.Range(q, 4).Select(i => (X: (double)v[i * 3], Z: (double)v[i * 3 + 2])).ToList();
                    double x = pts.Average(p => p.X) - cx;
                    if (x < fromX || x > toX) continue;
                    var north = pts.OrderBy(p => p.Z).Take(2).ToList();
                    var south = pts.OrderBy(p => p.Z).Skip(2).ToList();
                    Assert.True(north.Average(p => p.X) < south.Average(p => p.X),
                        $"{name}: a stripe at {x:F1} m leans its north end east: {string.Join(" ", pts.Select(p => $"({p.X - cx:F1},{p.Z:F1})"))}");
                    count();
                }
            }
        }
        Lean("J3b", 5, 34, () => exit++);
        Lean("J3b", 40, 100, () => leadIn++);
        Lean("J1", -80, -5, () => west++);
        Assert.True(exit > 3 && leadIn > 3 && west > 3, $"{exit} exit and {leadIn} lead-in stripes at J3b, {west} at J1");
    }
}
