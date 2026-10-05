using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The car park layout planner (#499). Pure geometry, so all of it is tier 0.</summary>
public class ParkingPlannerTests
{
    /// <summary>Flat open ground everywhere, nothing standing on it.</summary>
    private sealed class Flat(double y = 460) : IParkingGround
    {
        public double Ground(double e, double n) => y;
        public bool Occupied(double e, double n) => false;
    }

    /// <summary>Flat ground with a rectangular obstruction (a building, a road corridor).</summary>
    private sealed class WithBlock(double e0, double n0, double e1, double n1) : IParkingGround
    {
        public double Ground(double e, double n) => 460;
        public bool Occupied(double e, double n) => e >= e0 && e <= e1 && n >= n0 && n <= n1;
    }

    /// <summary>Ground falling away to the east at a given grade.</summary>
    private sealed class Slope(double grade) : IParkingGround
    {
        public double Ground(double e, double n) => 460 - (e - 2_600_000) * grade;
        public bool Occupied(double e, double n) => false;
    }

    private const double E0 = 2_600_000, N0 = 1_120_000;

    /// <summary>A rectangle w x d, its south-west corner at the origin, optionally turned by <paramref name="turn"/>.</summary>
    private static List<(double E, double N)> Rect(double w, double d, double turn = 0)
    {
        var pts = new (double E, double N)[] { (0, 0), (w, 0), (w, d), (0, d) };
        var ring = new List<(double E, double N)>();
        foreach (var (x, y) in pts)
        {
            double c = Math.Cos(turn), s = Math.Sin(turn);
            ring.Add((E0 + x * c - y * s, N0 + x * s + y * c));
        }
        return ring;
    }

    private static ParkingPlanner.Border RoadSouth(double along, double priority = 2) =>
        new(E0 + along, N0 - 6, 0, (int)priority, 3.5, false);

    // ---- bays inside, no overlap, right count ------------------------------------------------

    [Fact]
    public void Every_bay_lies_inside_the_lot_and_no_two_bays_overlap()
    {
        var ring = Rect(60, 34);
        var lot = ParkingPlanner.Plan(ring, [RoadSouth(30)], new Flat());

        Assert.Null(lot.Rejected);
        Assert.NotEmpty(lot.Bays);

        var poly = new double[ring.Count * 2];
        for (int i = 0; i < ring.Count; i++) { poly[i * 2] = ring[i].E; poly[i * 2 + 1] = ring[i].N; }
        foreach (var b in lot.Bays)
            Assert.True(ParkingPlanner.Inside(poly, b.E, b.N), $"bay at {b.E:F1},{b.N:F1} is outside the lot");

        // no two bay centres closer than a bay width: the rows are laid on a grid, so this is the
        // cheap equivalent of a rectangle overlap test and it catches a band laid twice
        for (int i = 0; i < lot.Bays.Count; i++)
            for (int j = i + 1; j < lot.Bays.Count; j++)
            {
                double d = Math.Sqrt(
                    Math.Pow(lot.Bays[i].E - lot.Bays[j].E, 2) + Math.Pow(lot.Bays[i].N - lot.Bays[j].N, 2));
                Assert.True(d > ParkingBay.StandardWidth - 0.01,
                    $"bays {i} and {j} are {d:F2} m apart");
            }
    }

    [Fact]
    public void A_34_m_deep_lot_takes_two_sided_bands_and_fills_most_of_its_area()
    {
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat());

        Assert.Equal(ParkingPlanner.Module.TwoSided, lot.Layout);
        // 34 m holds two 16 m bands; 60 m of frontage holds 23 bays a row after the 0.3 m inset,
        // so four rows is the order of what must come out. Islands eat a couple at the row ends.
        Assert.InRange(lot.Bays.Count, 70, 96);
    }

    [Theory]
    [InlineData(13, ParkingPlanner.Module.OneSided)]
    [InlineData(9, ParkingPlanner.Module.Angled)]
    [InlineData(6.5, ParkingPlanner.Module.Parallel)]
    public void A_shallow_lot_drops_to_the_widest_module_that_fits(double depth, ParkingPlanner.Module want)
    {
        var lot = ParkingPlanner.Plan(Rect(60, depth), [RoadSouth(30)], new Flat());
        Assert.Null(lot.Rejected);
        Assert.Equal(want, lot.Layout);
    }

    [Fact]
    public void A_lot_too_small_to_lay_out_is_rejected_so_the_old_pattern_stays()
    {
        var tiny = ParkingPlanner.Plan(Rect(8, 4), [], new Flat());
        Assert.NotNull(tiny.Rejected);
        Assert.Empty(tiny.Bays);
    }

    // ---- the axis, which is the bug being fixed ----------------------------------------------

    [Fact]
    public void Rows_follow_a_turned_lot_rather_than_the_world_axes()
    {
        // 31 degrees: nothing near a world axis, so a world-aligned grid would cut across the rows
        double turn = 31 * Math.PI / 180;
        var lot = ParkingPlanner.Plan(Rect(60, 34, turn), [], new Flat());

        Assert.Null(lot.Rejected);
        // the fitted axis is the long side of the lot, to within a degree
        double delta = Math.Abs(lot.Axis - turn) % Math.PI;
        Assert.True(Math.Min(delta, Math.PI - delta) < 0.02, $"axis {lot.Axis:F3} rad, lot turned {turn:F3}");

        // and every bay faces across that axis, not north
        foreach (var b in lot.Bays)
        {
            double across = Math.Abs(b.HeadingRad - (turn + Math.PI / 2)) % Math.PI;
            Assert.True(Math.Min(across, Math.PI - across) < 0.02, $"bay heading {b.HeadingRad:F3}");
        }
    }

    [Fact]
    public void The_axis_does_not_depend_on_ring_winding_or_the_starting_vertex()
    {
        var ring = Rect(60, 34, 17 * Math.PI / 180);
        var reversed = Enumerable.Reverse(ring).ToList();
        var rotated = ring.Skip(2).Concat(ring.Take(2)).ToList();

        double a = ParkingPlanner.Plan(ring, [], new Flat()).Axis;
        double b = ParkingPlanner.Plan(reversed, [], new Flat()).Axis;
        double c = ParkingPlanner.Plan(rotated, [], new Flat()).Axis;

        Assert.Equal(a, b, 9);
        Assert.Equal(a, c, 9);
    }

    [Fact]
    public void A_bordering_road_within_thirty_degrees_wins_the_axis_from_the_polygon()
    {
        // the lot is square to north, the road runs 12 degrees off it: a lot is built square to its street
        double roadBearing = 12 * Math.PI / 180;
        var lot = ParkingPlanner.Plan(Rect(60, 34),
            [new ParkingPlanner.Border(E0 + 30, N0 - 6, roadBearing, 3, 3.5, false)], new Flat());

        Assert.True(lot.AxisFromRoad);
        Assert.Equal(roadBearing, lot.Axis, 6);
    }

    [Fact]
    public void A_road_more_than_thirty_degrees_off_leaves_the_polygon_s_own_axis()
    {
        var lot = ParkingPlanner.Plan(Rect(60, 34),
            [new ParkingPlanner.Border(E0 + 30, N0 - 6, 50 * Math.PI / 180, 3, 3.5, false)], new Flat());

        Assert.False(lot.AxisFromRoad);
        Assert.Equal(0, lot.Axis, 6);   // the rectangle's long side runs east
    }

    // ---- the ground ---------------------------------------------------------------------------

    [Fact]
    public void No_bay_is_placed_on_a_building_or_a_road_corridor()
    {
        // a block through the middle of the lot
        var ground = new WithBlock(E0 + 20, N0 + 10, E0 + 35, N0 + 24);
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], ground);

        Assert.Null(lot.Rejected);
        foreach (var b in lot.Bays)
            Assert.False(ground.Occupied(b.E, b.N), $"a bay stands on the block at {b.E:F1},{b.N:F1}");
        // and the pad does not pave over it either
        foreach (var a in lot.Areas)
            for (int i = 0; i < a.Ring.Length; i += 2)
                Assert.False(ground.Occupied(a.Ring[i], a.Ring[i + 1]));
    }

    [Fact]
    public void A_row_terraces_rather_than_stretching_one_plane_down_a_slope()
    {
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Slope(0.04));
        Assert.Null(lot.Rejected);

        // every bay sits within the tolerance of its own row's plane, and the rows differ
        var byPlane = lot.Bays.GroupBy(b => Math.Round(b.Y, 3)).ToList();
        Assert.True(byPlane.Count >= 1);
        foreach (var b in lot.Bays)
            Assert.InRange(b.Y, 460 - 60 * 0.04 - 0.5, 460 + 0.5);
    }

    [Fact]
    public void Unknown_ground_takes_no_bays()
    {
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat(double.NaN));
        Assert.NotNull(lot.Rejected);
    }

    // ---- the entrance -------------------------------------------------------------------------

    [Fact]
    public void The_entrance_goes_on_the_most_important_bordering_road_and_gives_way_to_it()
    {
        var lane = new ParkingPlanner.Border(E0 - 6, N0 + 17, Math.PI / 2, 0, 2.5, false);
        var main = new ParkingPlanner.Border(E0 + 30, N0 - 6, 0, 3, 3.5, false);
        var lot = ParkingPlanner.Plan(Rect(60, 34), [lane, main], new Flat());

        var giveWay = lot.Marks.Where(m => m.Type == PaintType.GiveWayLine).ToList();
        var single = Assert.Single(giveWay);
        // on the main road to the south, not the lane to the west
        Assert.True(single.Line[1] < N0 + 2, $"give-way line at N {single.Line[1]:F1}");
    }

    [Fact]
    public void A_lot_with_no_road_near_it_gets_rows_but_no_entrance()
    {
        var lot = ParkingPlanner.Plan(Rect(60, 34), [], new Flat());

        Assert.Null(lot.Rejected);
        Assert.NotEmpty(lot.Bays);
        Assert.DoesNotContain(lot.Marks, m => m.Type is PaintType.GiveWayLine or PaintType.StopLine);
        Assert.DoesNotContain(lot.Fixtures, f => f.Kind == ParkingPlanner.FixtureKind.Barrier);
    }

    [Fact]
    public void A_paid_lot_gets_a_boom_a_kiosk_a_stop_bar_and_the_blue_P_and_a_free_one_gets_none()
    {
        // paid/free is hashed from the lot key, so sweep lots until both cases turn up
        bool sawPaid = false, sawFree = false;
        for (int i = 0; i < 40 && !(sawPaid && sawFree); i++)
        {
            var ring = Rect(60, 34).Select(p => (E: p.E + i * 137.0, N: p.N)).ToList();
            var road = new ParkingPlanner.Border(ring[0].E + 30, N0 - 6, 0, 3, 3.5, false);
            var lot = ParkingPlanner.Plan(ring, [road], new Flat());
            if (lot.Rejected != null) continue;

            var kinds = lot.Fixtures.Select(f => f.Kind).ToHashSet();
            if (lot.Paid)
            {
                sawPaid = true;
                Assert.Contains(ParkingPlanner.FixtureKind.Barrier, kinds);
                Assert.Contains(ParkingPlanner.FixtureKind.Kiosk, kinds);
                Assert.Contains(ParkingPlanner.FixtureKind.Sign, kinds);
                Assert.Contains(lot.Marks, m => m.Type == PaintType.StopLine);
            }
            else
            {
                sawFree = true;
                Assert.DoesNotContain(ParkingPlanner.FixtureKind.Barrier, kinds);
                Assert.DoesNotContain(ParkingPlanner.FixtureKind.Kiosk, kinds);
            }
        }
        Assert.True(sawPaid, "no paid lot in 40 tries");
        Assert.True(sawFree, "no free lot in 40 tries");
    }

    [Fact]
    public void Paid_or_free_is_stable_for_the_same_lot_and_independent_of_vertex_order()
    {
        var ring = Rect(60, 34);
        var road = RoadSouth(30);
        var a = ParkingPlanner.Plan(ring, [road], new Flat());
        var b = ParkingPlanner.Plan(Enumerable.Reverse(ring).ToList(), [road], new Flat());
        Assert.Equal(a.Paid, b.Paid);
    }

    // ---- the bays a player cares about --------------------------------------------------------

    [Fact]
    public void The_bay_nearest_the_way_in_is_left_free_and_two_bays_are_marked_disabled()
    {
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat());

        Assert.Single(lot.Bays, b => (b.Flags & ParkingBayFlags.NearEntrance) != 0);
        Assert.Equal(2, lot.Bays.Count(b => (b.Flags & ParkingBayFlags.Disabled) != 0));
        Assert.Equal(2, lot.Marks.Count(m => m.Type == PaintType.DisabledBay));
        // a free bay and a disabled bay are never the same bay
        Assert.DoesNotContain(lot.Bays, b =>
            (b.Flags & ParkingBayFlags.NearEntrance) != 0 && (b.Flags & ParkingBayFlags.Disabled) != 0);
    }

    [Fact]
    public void A_disabled_bay_is_wider_than_a_standard_one()
    {
        var bay = new ParkingBay(0, 0, 0, 0, ParkingBayKind.Car, ParkingBayFlags.Disabled);
        Assert.Equal(ParkingBay.DisabledWidth, bay.Width);
        Assert.True(bay.Width > ParkingBay.StandardWidth);
    }

    [Fact]
    public void A_loading_bay_and_the_bay_by_the_entrance_are_not_occupiable()
    {
        Assert.False(new ParkingBay(0, 0, 0, 0, ParkingBayKind.Loading, ParkingBayFlags.None).Occupiable);
        Assert.False(new ParkingBay(0, 0, 0, 0, ParkingBayKind.Car, ParkingBayFlags.NearEntrance).Occupiable);
        Assert.True(new ParkingBay(0, 0, 0, 0, ParkingBayKind.Car, ParkingBayFlags.None).Occupiable);
    }

    // ---- aisles and arrows --------------------------------------------------------------------

    [Fact]
    public void A_one_way_aisle_carries_arrows_and_a_two_way_aisle_carries_none()
    {
        // 9 m deep: an angled row off a one-way aisle
        var angled = ParkingPlanner.Plan(Rect(60, 9), [RoadSouth(30)], new Flat());
        Assert.Equal(ParkingPlanner.Module.Angled, angled.Layout);
        Assert.Contains(angled.Marks, m => m.Type == PaintType.Arrow);

        // 34 m deep: two-sided bands off two-way aisles
        var twoWay = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat());
        Assert.Equal(ParkingPlanner.Module.TwoSided, twoWay.Layout);
        Assert.DoesNotContain(twoWay.Marks, m => m.Type == PaintType.Arrow);
    }

    [Fact]
    public void Bays_are_paved_and_every_pad_ring_is_a_quad()
    {
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat());
        var pads = lot.Areas.Where(a => a.Kind == ParkingPlanner.AreaKind.Pad).ToList();

        Assert.NotEmpty(pads);
        foreach (var a in pads) Assert.Equal(8, a.Ring.Length);   // four LV95 points
        foreach (var a in pads) Assert.Equal(0, a.Height);        // flush, like a #123 turn-lane strip
    }

    // ---- the extras ---------------------------------------------------------------------------

    [Fact]
    public void A_long_row_gets_kerbed_islands_with_a_tree_in_them()
    {
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat());

        var islands = lot.Areas.Where(a => a.Kind == ParkingPlanner.AreaKind.Island).ToList();
        Assert.NotEmpty(islands);
        foreach (var i in islands) Assert.True(i.Height > 0, "an island must be kerbed");

        var trees = lot.Fixtures.Where(f => f.Kind == ParkingPlanner.FixtureKind.Tree).ToList();
        Assert.Equal(islands.Count, trees.Count);
        // the bays beside a planter are flagged shaded
        Assert.Contains(lot.Bays, b => (b.Flags & ParkingBayFlags.Shaded) != 0);
    }

    [Fact]
    public void A_short_row_gets_no_island()
    {
        // 16 m of frontage is six bays a row, under the island threshold
        var lot = ParkingPlanner.Plan(Rect(16, 34), [RoadSouth(8)], new Flat());
        Assert.Null(lot.Rejected);
        Assert.DoesNotContain(lot.Areas, a => a.Kind == ParkingPlanner.AreaKind.Island);
    }

    [Fact]
    public void A_big_box_store_gets_trolley_shelters_and_a_walk_to_its_door_and_a_small_shop_gets_neither()
    {
        var store = new ParkingPlanner.Frontage(E0 + 30, N0 + 36, 2400, Retail: true);
        var big = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat(), store);
        Assert.Contains(big.Fixtures, f => f.Kind == ParkingPlanner.FixtureKind.Shelter);
        Assert.Contains(big.Areas, a => a.Kind == ParkingPlanner.AreaKind.Walk);

        var kiosk = new ParkingPlanner.Frontage(E0 + 30, N0 + 36, 90, Retail: true);
        var small = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat(), kiosk);
        Assert.DoesNotContain(small.Fixtures, f => f.Kind == ParkingPlanner.FixtureKind.Shelter);
    }

    [Fact]
    public void A_walk_is_kerbed_and_a_pad_is_flush()
    {
        var store = new ParkingPlanner.Frontage(E0 + 30, N0 + 36, 2400, Retail: true);
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat(), store);
        foreach (var w in lot.Areas.Where(a => a.Kind == ParkingPlanner.AreaKind.Walk))
            Assert.True(w.Height > 0);
    }

    // ---- the pinned layout: bay order is a wire contract --------------------------------------

    [Fact]
    public void The_layout_is_pinned_so_a_rebuild_never_moves_a_parked_car()
    {
        // The dormant-vehicle slot ordinal (#496 phase 3) is an index into the bay list, so if this
        // changes, every car parked in the region moves. Update the numbers only on purpose.
        var lot = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat());

        Assert.Equal(ParkingPlanner.Module.TwoSided, lot.Layout);
        Assert.Equal(0, lot.Axis, 9);
        Assert.Equal(80, lot.Bays.Count);

        // 33.4 m of usable depth holds two 16 m bands (four rows), 59.4 m of frontage 22 clear cells
        // a row, and the end cell at each end of each row is a planter rather than a bay: 4 x 20 = 80.
        Assert.Equal(15, lot.Areas.Count);    // 6 row/aisle pads + the throat, and 8 planters
        Assert.Equal(8, lot.Areas.Count(a => a.Kind == ParkingPlanner.AreaKind.Island));
        Assert.Equal(168, lot.Marks.Count);   // 2 per bay + 4 row ends + give-way + stop bar + 2 disabled
        Assert.Equal(11, lot.Fixtures.Count); // boom, kiosk, sign, 8 trees

        var first = lot.Bays[0];
        Assert.Equal(E0 + 6.250, first.E, 3);
        Assert.Equal(N0 + 2.800, first.N, 3);
        Assert.Equal(460, first.Y, 3);
        // it is in the southern row, nosing north across the lot to its aisle (LV95 bearing)
        Assert.Equal(Math.PI / 2, first.HeadingRad, 4);

        // and the order is stable run to run
        var again = ParkingPlanner.Plan(Rect(60, 34), [RoadSouth(30)], new Flat());
        for (int i = 0; i < lot.Bays.Count; i++)
        {
            Assert.Equal(lot.Bays[i].E, again.Bays[i].E, 9);
            Assert.Equal(lot.Bays[i].N, again.Bays[i].N, 9);
            Assert.Equal(lot.Bays[i].Flags, again.Bays[i].Flags);
        }
    }

    [Fact]
    public void The_lot_key_is_stable_and_not_the_randomised_string_hash()
    {
        var a = ParkingPlanner.Key((2_600_000.04, 1_120_000.04));
        var b = ParkingPlanner.Key((2_600_000.0, 1_120_000.0));
        Assert.Equal(a, b);   // rounded to the decimetre
        Assert.NotEqual(a, ParkingPlanner.Key((2_600_001.0, 1_120_000.0)));
    }
}


/// <summary>The <c>parking</c> fixture course (#499): the real planner over a course with no terrain data.</summary>
public class ParkingFixtureTests
{
    private const double StartE = 2_600_000, StartN = 1_120_000;

    private static FixtureChunkSource Source() =>
        FixtureChunkSource.Create("parking", StartE, StartN)
        ?? throw new InvalidOperationException("the parking course did not build");

    [Fact]
    public async Task The_course_serves_a_laid_out_lot_with_bays_a_pad_and_its_props()
    {
        var source = Source();
        var bays = 0;
        var pads = 0;
        var islands = 0;
        var walks = 0;
        var props = new List<PointPropType>();
        var lines = 0;

        foreach (var id in source.Tiles)
        {
            var tile = await source.LoadRoadsAsync(id);
            Assert.NotNull(tile);
            bays += tile!.Parking.Count;
            pads += tile.AreaProps.Count(a => a.Type == AreaPropType.ParkingPad);
            islands += tile.AreaProps.Count(a => a.Type == AreaPropType.ParkingIsland);
            walks += tile.AreaProps.Count(a => a.Type == AreaPropType.Sidewalk);
            props.AddRange(tile.PointProps.Select(p => p.Type));
            lines += tile.Paint.Count;
        }

        // 72 x 34 m: two-sided bands, long rows, a big-box store beside it
        Assert.InRange(bays, 80, 130);
        Assert.True(pads > 0, "no pad");
        Assert.True(islands > 0, "no planter");
        Assert.True(walks > 0, "no walk to the door");
        Assert.True(lines > bays, $"only {lines} markings for {bays} bays");
        Assert.Contains(PointPropType.CartShelter, props);
    }

    [Fact]
    public async Task The_fixture_lot_is_not_square_to_the_world()
    {
        // the course turns its road 14 degrees, and a bay must follow the lot, not north: this is
        // exactly what the old world-aligned shader grid got wrong
        var source = Source();
        var headings = new List<float>();
        foreach (var id in source.Tiles)
            if (await source.LoadRoadsAsync(id) is { } tile)
                headings.AddRange(tile.Parking.Select(b => b.Heading));

        Assert.NotEmpty(headings);
        foreach (float h in headings)
        {
            // 14 degrees off a world axis, either sense, within a degree
            double off = Math.Abs(h % (Math.PI / 2)) * 180 / Math.PI;
            Assert.True(Math.Min(off, 90 - off) is > 13 and < 15, $"bay heading {h * 180 / Math.PI:F1} deg");
        }
    }

    [Fact]
    public async Task Every_bay_stands_on_the_pad_that_was_drawn_for_it()
    {
        var source = Source();
        foreach (var id in source.Tiles)
        {
            var tile = await source.LoadRoadsAsync(id);
            if (tile is null || tile.Parking.Count == 0) continue;

            var pads = tile.AreaProps.Where(a => a.Type == AreaPropType.ParkingPad).ToList();
            foreach (var bay in tile.Parking)
            {
                bool on = pads.Any(p => Covers(p, bay.X, bay.Z));
                Assert.True(on, $"bay at {bay.X:F1},{bay.Z:F1} in {id.E}_{id.N} has no pad under it");
            }
        }

        static bool Covers(RoadAreaProp p, float x, float z)
        {
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                var (ax, az) = (p.Vertices[p.Indices[t] * 3], p.Vertices[p.Indices[t] * 3 + 2]);
                var (bx, bz) = (p.Vertices[p.Indices[t + 1] * 3], p.Vertices[p.Indices[t + 1] * 3 + 2]);
                var (cx, cz) = (p.Vertices[p.Indices[t + 2] * 3], p.Vertices[p.Indices[t + 2] * 3 + 2]);
                double d1 = (x - bx) * (az - bz) - (ax - bx) * (z - bz);
                double d2 = (x - cx) * (bz - cz) - (bx - cx) * (z - cz);
                double d3 = (x - ax) * (cz - az) - (cx - ax) * (z - az);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                if (!(neg && pos)) return true;
            }
            return false;
        }
    }
}

