using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The fun world's course (#1396, src/Terrain/Fixture/FunCourse.cs): what the game reads off it must stand where it
/// is meant to, and the source must hand the venues, the arena, the fleet and the slope to the game as it reads them.
/// </summary>
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
}
