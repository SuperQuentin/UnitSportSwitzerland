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
