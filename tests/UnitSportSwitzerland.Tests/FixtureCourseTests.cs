using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The synthetic driving courses (src/Terrain/Fixture, linked in): the tiles they serve must be
/// valid data the game reads like real tiles.
/// </summary>
public class FixtureCourseTests
{
    private const double StartE = 2583250, StartN = 1113250;

    public static IEnumerable<object[]> Courses => FixtureCourse.Names.Select(n => new object[] { n });

    [Fact]
    public void Unknown_course_is_null() => Assert.Null(FixtureCourse.Create("mollendruz"));

    [Fact]
    public void Split_pieces_stay_in_their_tile_and_share_ends()
    {
        // a diagonal across four tiles, one point exactly on a tile corner
        var line = new List<(double E, double N, double Z)> { (2583900, 1113900, 10), (2584000, 1114000, 11), (2584600, 1114500, 12), (2582500, 1112500, 13) };
        var pieces = FixtureChunkSource.SplitAtTiles(line);
        Assert.True(pieces.Count >= 4);
        for (int i = 0; i < pieces.Count; i++)
        {
            var (tile, piece) = pieces[i];
            Assert.All(piece, p => Assert.InRange(p.E, tile.MinE - 1e-6, tile.MinE + 1000 + 1e-6));
            Assert.All(piece, p => Assert.InRange(p.N, tile.MinN - 1e-6, tile.MaxN + 1e-6));
            if (i > 0) Assert.Equal(pieces[i - 1].Piece[^1], piece[0]);
        }
        Assert.Equal(line[0], pieces[0].Piece[0]);
        Assert.Equal(line[^1], pieces[^1].Piece[^1]);
    }

    [Theory]
    [MemberData(nameof(Courses))]
    public async Task Tiles_are_valid_and_the_ground_follows_the_road(string name)
    {
        var source = FixtureChunkSource.Create(name, StartE, StartN)!;
        var manifest = await source.LoadManifestAsync();
        Assert.Equal(source.Tiles.Count, manifest.Tiles.Count);
        Assert.Contains(TileId.FromLv95(StartE, StartN), source.Tiles);
        Assert.Equal(StartE, manifest.SuggestedOriginLv95.E);

        int roadPoints = 0;
        foreach (var id in source.Tiles)
        {
            var grid = (await source.LoadChunkAsync(id))!;
            var coarse = (await source.LoadCoarseChunkAsync(id))!;
            Assert.Equal(ChunkFormat.GridSize, grid.Size);
            // the coarse tile is the full one decimated, as the real companions are
            Assert.Equal(grid.HeightAt(500, 300), coarse.HeightAt(500, 300));
            Assert.NotNull(await source.LoadBuildingsAsync(id));
            Assert.NotNull(await source.LoadTreesAsync(id));
            foreach (var seg in (await source.LoadRoadsAsync(id))!.Segments)
                for (int i = 0; i < seg.PointCount; i++)
                {
                    float x = seg.Points[i * 3], y = seg.Points[i * 3 + 1], z = seg.Points[i * 3 + 2];
                    Assert.InRange(x, -1e-3f, 1000.001f);
                    Assert.InRange(z, -1e-3f, 1000.001f);
                    // the collision blend pulls the ground to the road within 3 m; it must not have to pull far
                    Assert.InRange(grid.SampleHeight(id.MinE + x, id.MaxN - z) - y, -2.0, 2.0);
                    roadPoints++;
                }
        }
        Assert.Equal(name != "flat", roadPoints > 0);
        Assert.Null(await source.LoadChunkAsync(new TileId(0, 0)));
    }

    [Fact]
    public void Hairpin_goes_downhill_at_seven_percent()
    {
        var road = FixtureCourse.Create("hairpin")!.Roads.Single().Points;
        double length = 0;
        for (int i = 1; i < road.Count; i++)
            length += Math.Sqrt(Math.Pow(road[i].X - road[i - 1].X, 2) + Math.Pow(road[i].Y - road[i - 1].Y, 2));
        Assert.InRange(length, 2800, 3200);
        Assert.Equal(-0.07 * length, road[^1].Z - road[0].Z, 1);
        // six legs: the road comes back past the start's meridian five times
        int crossings = 0;
        for (int i = 1; i < road.Count; i++)
            if ((road[i - 1].X < 225) != (road[i].X < 225)) crossings++;
        Assert.Equal(6, crossings);
    }
}
