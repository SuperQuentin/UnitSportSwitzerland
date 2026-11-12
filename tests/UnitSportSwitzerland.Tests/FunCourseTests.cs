using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The fun world's course (#1396, src/Terrain/Fixture/FunCourse.cs): what the game reads off it must stand where it
/// is meant to, and the source must hand the venues, the arena, the fleet and the slope to the game as it reads them.
/// </summary>
[Collection(FixtureWorld.Collection)]
public class FunCourseTests
{
    private const double StartE = 2583250, StartN = 1113250;

    private static readonly FixtureCourse Course = FunCourse.Create();

    private static bool InBlock(FixtureCourse.Block b, double x, double y) =>
        b.Turn == 0 && Math.Abs(x - b.X) < b.Width / 2 && Math.Abs(y - b.Y) < b.Depth / 2;

    [Fact]
    public void Every_venue_stands_in_a_commercial_block_big_enough_for_a_hall()
    {
        Assert.Equal(6, Course.Venues.Count);
        foreach (var v in Course.Venues)
        {
            var block = Assert.Single(Course.Blocks, b => InBlock(b, v.X, v.Y));
            Assert.Equal(BuildingKind.Commercial, block.Kind);
            // a hall venue takes the whole building only up to 20 m tall (Interiors/Venues.WholeBuilding)
            Assert.InRange(block.Height, 3f, 20f);
            // its front (south wall) on the boulevard's pavement, with room for a terrace before the kerb
            Assert.Equal(FunCourse.FrontY, block.Y - block.Depth / 2, 3);
        }
        Assert.Contains(Course.Venues, v => v.Type == VenueType.Bowling);
        Assert.Contains(Course.Venues, v => v.Type == VenueType.Cinema);
        Assert.Contains(Course.Venues, v => v.Type == VenueType.Arcade);
        Assert.Contains(Course.Venues, v => v.Type == VenueType.Cafe && v.Flags.HasFlag(VenueFlags.Outdoor));
    }

    [Fact]
    public void No_parked_vehicle_stands_in_a_block_or_in_the_water()
    {
        Assert.NotEmpty(Course.Fleet);
        foreach (var p in Course.Fleet)
        {
            Assert.DoesNotContain(Course.Blocks, b => InBlock(b, p.X, p.Y));
            Assert.True(double.IsNaN(FunCourse.Water(p.X, p.Y).Level), $"vehicle {p.Kind} at {p.X}/{p.Y} is in the lake");
        }
    }

    [Fact]
    public void The_slope_lies_on_the_hill_and_the_lift_runs_up_beside_it()
    {
        var slope = Assert.Single(Course.Slopes);
        double bottom = FunCourse.Ground(-700, slope.Outline.Min(p => p.Y)), top = FunCourse.Ground(-700, slope.Outline.Max(p => p.Y));
        Assert.True(top - bottom > 90, $"the slope drops {top - bottom:F0} m");
        var lift = Assert.Single(Course.Roads, r => r.Class == RoadClass.SkiLift).Points;
        Assert.True(lift[^1].Z - lift[0].Z > 90);
        Assert.All(slope.Outline, p => Assert.True(p.X < FunCourse.LiftX));
    }

    [Fact]
    public void The_rings_fly_above_the_ground_in_reach_of_each_other()
    {
        var rings = FunCourse.Rings;
        for (int i = 0; i < rings.Length; i++)
        {
            var (x, y, h) = rings[i];
            Assert.True(FunCourse.Flat + h - FunCourse.Ground(x, y) > FunCourse.RingRadius + 5, $"ring {i + 1} touches the ground");
            if (i == 0) continue;
            double leg = Math.Sqrt(Math.Pow(x - rings[i - 1].X, 2) + Math.Pow(y - rings[i - 1].Y, 2));
            Assert.InRange(leg, 150, 700);
        }
    }

    [Fact]
    public void The_grand_circuit_is_a_closed_lap_round_everything_else()
    {
        var lap = FunCourse.Circuit();
        Assert.Equal(lap[0].X, lap[^1].X, 3);
        Assert.Equal(lap[0].Y, lap[^1].Y, 3);
        double length = 0, turned = 0;
        for (int i = 1; i < lap.Count; i++)
        {
            double step = Math.Sqrt(Math.Pow(lap[i].X - lap[i - 1].X, 2) + Math.Pow(lap[i].Y - lap[i - 1].Y, 2));
            Assert.InRange(step, 0.05, FixtureCourse.Pen.Step + 0.01);   // no jump where a corner was too tight for its radius
            length += step;
            if (i < 2) continue;
            double a0 = Math.Atan2(lap[i - 1].Y - lap[i - 2].Y, lap[i - 1].X - lap[i - 2].X), a1 = Math.Atan2(lap[i].Y - lap[i - 1].Y, lap[i].X - lap[i - 1].X);
            turned += Math.IEEERemainder(a1 - a0, 2 * Math.PI);
        }
        Assert.InRange(length, 8000, 11000);
        Assert.Equal(2 * Math.PI, turned, 1);   // once round, anticlockwise, never crossing itself back
        // clear of every other area by more than its half width
        var others = Course.Roads.Where(r => r.Class != RoadClass.SkiLift && r.Points.Count != lap.Count).SelectMany(r => r.Points).ToList();
        var (px0, py0, px1, py1) = FunCourse.PvpBox;
        foreach (var (x, y, _) in lap.Where((_, i) => i % 5 == 0))
        {
            Assert.True(double.IsNaN(FunCourse.Water(x, y).Level), $"in the lake at {x:F0}/{y:F0}");
            Assert.DoesNotContain(Course.Blocks, b => Math.Abs(x - b.X) < b.Width / 2 + 10 && Math.Abs(y - b.Y) < b.Depth / 2 + 10);
            Assert.False(x > px0 - 20 && x < px1 + 20 && y > py0 - 20 && y < py1 + 20);
            Assert.False(x > FunCourse.StuntX0 - 20 && x < FunCourse.StuntX1 + 20 && y > FunCourse.StuntY0 - 20 && y < FunCourse.StuntY1 + 20);
            Assert.False(x > FunCourse.RunwayX0 - 20 && x < FunCourse.RunwayX1 + 20 && Math.Abs(y - FunCourse.RunwayY) < 60);
            Assert.DoesNotContain(others, p => Math.Abs(p.X - x) < 30 && Math.Abs(p.Y - y) < 30);
        }
    }

    [Fact]
    public void The_pvp_block_holds_its_cover_and_no_venue()
    {
        var (x0, y0, x1, y1) = FunCourse.PvpBox;
        bool In(double x, double y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;
        Assert.True(Course.Blocks.Count(b => In(b.X, b.Y)) >= 10);
        Assert.DoesNotContain(Course.Venues, v => In(v.X, v.Y));
        Assert.False(In(0, 0));
    }

    [Fact]
    public async Task The_source_hands_over_the_venues_the_arena_the_fleet_and_the_slope()
    {
        try
        {
            var source = FixtureChunkSource.Create("fun", StartE, StartN)!;
            Assert.Contains(UnitSport.Terrain.Stadiums.StadiumCatalog.All, s => s.Name == FunCourse.Arena.Name);
            Assert.Equal(Course.Fleet.Count, FixtureChunkSource.Fleet.Values.Sum(l => l.Count));
            Assert.All(FixtureChunkSource.Fleet, kv => Assert.All(kv.Value, s => Assert.Equal(kv.Key, TileId.FromLv95(s.E, s.N))));
            var foot = TileId.FromLv95(StartE - 700, StartN + 400);
            var pistes = await source.LoadPistesAsync(foot);
            Assert.NotNull(pistes);
            Assert.NotNull(pistes!.At(StartE - 700, StartN + 400));
            Assert.Null(pistes.At(StartE - 300, StartN + 400));
            // the course's roads take their own widths: the race track is wider than its class's default
            var track = (await source.LoadRoadsAsync(TileId.FromLv95(StartE + FunCourse.TrackX0 + 100, StartN + FunCourse.TrackY0)))!
                .Segments.Where(s => s.Class == RoadClass.Major);
            Assert.Contains(track, s => s.Width == FunCourse.TrackWidth);
        }
        finally
        {
            FixtureChunkSource.ForgetWorld();
        }
        Assert.DoesNotContain(UnitSport.Terrain.Stadiums.StadiumCatalog.All, s => s.Name == FunCourse.Arena.Name);
        Assert.Empty(FixtureChunkSource.Fleet);
    }

    /// <summary>
    /// #1649: played on its leaves, the fleet stands where the leaves have its course points (their LV95 notation is the
    /// latitude and longitude the leaves' plane gives), facing the course's way there, not where the flat world's grid
    /// metres put it: the two part by metres across the course.
    /// </summary>
    [Fact]
    public void On_earth_the_fleet_stands_where_the_leaves_have_its_course_points()
    {
        try
        {
            var source = FixtureChunkSource.Create("fun", StartE, StartN, earth: true)!;
            Assert.Same(source.Place, CoursePlace.Active);
            var leaves = FixtureLeaves.At(source);
            Assert.Same(source.Place.Earth, leaves.Source);
            var slots = FixtureChunkSource.Fleet.Values.SelectMany(l => l).OrderBy(s => s.Ordinal).ToList();
            Assert.Equal(Course.Fleet.Count, slots.Count);
            double parted = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var (p, s) = (Course.Fleet[i], slots[i]);
                var (lat, lon) = leaves.Source.ToLatLon(p.X, p.Y);
                var (e, n) = Lv95Source.Instance.FromLatLon(lat, lon);
                // the placement's map about the start against the plane itself: a few millimetres across the course
                Assert.InRange(Math.Abs(s.E - e) + Math.Abs(s.N - n), 0, 0.01);
                Assert.Equal(TileId.FromLv95(s.E, s.N), FixtureChunkSource.Fleet.Single(kv => kv.Value.Contains(s)).Key);
                // the yaw on the grid, carried back onto the course: the course's heading
                double grid = -s.Yaw;
                var (x1, y1) = source.Place.Course(s.E + 10 * Math.Sin(grid), s.N + 10 * Math.Cos(grid));
                double back = Math.Atan2(x1 - p.X, y1 - p.Y) * 180 / Math.PI;
                Assert.InRange(Math.Abs(Math.IEEERemainder(back - p.HeadingDeg, 360)), 0, 0.01);
                parted = Math.Max(parted, Math.Abs(s.E - (StartE + p.X)) + Math.Abs(s.N - (StartN + p.Y)));
            }
            Assert.True(parted > 0.5, $"the earth placement is the grid's ({parted:F2} m at most)");
            // the tiles are laid the same way: the race track's road where the plane has it, and back
            var corner = (FunCourse.TrackX0, FunCourse.TrackY0);
            var (te, tn) = source.Place.Lv95(corner.Item1, corner.Item2);
            var (xe, xn) = source.Place.Exact(corner.Item1, corner.Item2);
            Assert.InRange(Math.Abs(te - xe) + Math.Abs(tn - xn), 0, 0.01);
            var (bx, by) = source.Place.Course(te, tn);
            Assert.InRange(Math.Abs(bx - corner.Item1) + Math.Abs(by - corner.Item2), 0, 1e-6);
            Assert.InRange(source.Place.Turn * 180 / Math.PI, -1, 1);
            Assert.NotEqual(0, source.Place.Turn);
        }
        finally
        {
            FixtureChunkSource.ForgetWorld();
        }
        Assert.Null(CoursePlace.Active);
    }

    /// <summary>
    /// #1649: the snow slope on the fun course's leaves, as the cube piste stage writes a leaf's: its record on each leaf
    /// the slope reaches, every slot vertex inside its outline, the slope's whole area covered once, through the codec.
    /// </summary>
    [Fact]
    public void The_slope_is_on_its_leaves()
    {
        try
        {
            var source = FixtureChunkSource.Create("fun", StartE, StartN, earth: true)!;
            var leaves = FixtureLeaves.At(source);
            var slope = Assert.Single(Course.Slopes);
            var touched = new HashSet<CubeKey>();
            double minX = slope.Outline.Min(p => p.X), maxX = slope.Outline.Max(p => p.X);
            double minY = slope.Outline.Min(p => p.Y), maxY = slope.Outline.Max(p => p.Y);
            for (double x = minX; x <= maxX + 50; x += 50)
                for (double y = minY; y <= maxY + 50; y += 50)
                    touched.Add(SourceOnLeaf.LeafOf(leaves.Source, Math.Min(x, maxX), Math.Min(y, maxY)));
            double covered = 0;
            foreach (var key in touched)
            {
                var content = leaves.Build(key)!;
                var pistes = content.Pistes;
                Assert.NotNull(pistes);
                Assert.Contains(pistes!.Records, r => r.Name == slope.Name && r.IsArea == (slope.Line == null));
                var on = new SourceOnLeaf(LeafFrames.Swiss, key, leaves.Source);
                int n = on.Side;
                Assert.Equal(n * n, pistes.Slot.Length);
                for (int j = 0; j + 1 < n; j++)
                    for (int i = 0; i + 1 < n; i++)
                    {
                        if (pistes.Slot[i + j * n] == 0) continue;
                        var (x, y) = on.SourceAt(i, j);
                        Assert.True(FixtureChunkSource.Inside(slope.Outline, x, y));
                        // the cell from this vertex east and north, in course metres
                        var (xe, ye) = on.SourceAt(i + 1, j);
                        var (xn, yn) = on.SourceAt(i, j + 1);
                        covered += Math.Abs((xe - x) * (yn - y) - (ye - y) * (xn - x));
                    }
                var back = LeafPistes.Decode(LeafPistes.Encode(pistes, content.Heights.Bits));
                Assert.Equal(pistes.Slot, back.Slot);
                Assert.Equal(pistes.Records.Count, back.Records.Count);
            }
            double area = 0;
            for (int i = 0, j = slope.Outline.Length - 1; i < slope.Outline.Length; j = i++)
                area += (slope.Outline[j].X + slope.Outline[i].X) * (slope.Outline[j].Y - slope.Outline[i].Y);
            area = Math.Abs(area) / 2;
            Assert.InRange(covered / area, 0.9, 1.1);
        }
        finally
        {
            FixtureChunkSource.ForgetWorld();
        }
    }
}
