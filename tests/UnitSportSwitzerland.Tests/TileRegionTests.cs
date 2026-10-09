using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Which source features a set of tiles needs (#678). The readers themselves are checked on the
/// real swissTLM3D file, by building the same tiles with and without them and comparing the bytes;
/// what lives here is the test that decides what they keep.
/// </summary>
public class TileRegionTests
{
    // two tiles far apart, as a map made of two areas is: Geneva and the Valais
    private static readonly TileId Geneva = new(2500, 1118), Valais = new(2583, 1109);

    private static TileRegion Two(double marginM = 0) => new(new[] { Geneva, Valais }, marginM);

    [Fact]
    public void The_box_spans_every_tile()
    {
        var r = Two();
        Assert.Equal(2_500_000, r.MinE);
        Assert.Equal(2_584_000, r.MaxE);
        Assert.Equal(1_109_000, r.MinN);
        Assert.Equal(1_119_000, r.MaxN);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void A_feature_between_the_two_areas_is_not_wanted()
    {
        // inside the box, 40 km from either tile
        Assert.False(Two().Touches(2_540_000, 2_540_050, 1_113_000, 1_113_050));
        Assert.False(Two(TileRegion.RingM).Touches(2_540_000, 2_540_050, 1_113_000, 1_113_050));
    }

    [Fact]
    public void A_feature_inside_a_tile_is_wanted()
    {
        Assert.True(Two().Touches(2_500_400, 2_500_450, 1_118_400, 1_118_450));
        Assert.True(Two().Touches(2_583_999, 1_109_001));
    }

    [Theory]
    // the box test of the R-tree query includes its edges, so a feature that ends on the lattice
    // line of a tile touches it, from any side
    [InlineData(2_499_900, 2_500_000, 1_118_500, 1_118_600)] // ends on the west edge
    [InlineData(2_501_000, 2_501_100, 1_118_500, 1_118_600)] // starts on the east edge
    [InlineData(2_500_500, 2_500_600, 1_117_900, 1_118_000)] // ends on the south edge
    [InlineData(2_500_500, 2_500_600, 1_119_000, 1_119_100)] // starts on the north edge
    [InlineData(2_501_000, 2_501_000, 1_119_000, 1_119_000)] // the north-east corner alone
    public void A_feature_on_the_edge_of_a_tile_touches_it(double minX, double maxX, double minY, double maxY)
        => Assert.True(Two().Touches(minX, maxX, minY, maxY));

    [Fact]
    public void A_feature_a_metre_past_the_edge_does_not()
    {
        Assert.False(Two().Touches(2_499_900, 2_499_999, 1_118_500, 1_118_600));
        Assert.False(Two().Touches(2_501_001, 2_501_100, 1_118_500, 1_118_600));
    }

    [Fact]
    public void The_margin_reaches_one_tile_around_and_no_further()
    {
        var ring = Two(TileRegion.RingM);
        Assert.True(ring.Touches(2_499_200, 2_499_300, 1_118_500, 1_118_600));  // in the tile to the west
        Assert.True(ring.Touches(2_499_000, 1_117_000));                        // its far corner, edge included
        Assert.False(ring.Touches(2_498_900, 2_498_999, 1_118_500, 1_118_600)); // two tiles away
    }

    [Fact]
    public void A_feature_larger_than_the_region_touches_it_when_it_covers_a_tile()
    {
        // a lake: more tiles under its box than the region holds, which takes the other loop
        Assert.True(Two().Touches(2_490_000, 2_560_000, 1_110_000, 1_150_000));
        Assert.False(Two().Touches(2_510_000, 2_570_000, 1_120_000, 1_150_000));
    }

    [Fact]
    public void A_rectangle_of_tiles_keeps_everything_its_box_does()
    {
        // on a map that is one rectangle the tile test must change nothing: whatever the box test
        // of the query lets through touches a tile
        var tiles = new List<TileId>();
        for (int e = 2580; e <= 2584; e++)
            for (int n = 1108; n <= 1111; n++)
                tiles.Add(new TileId(e, n));
        var r = new TileRegion(tiles);

        var rng = new Random(678);
        for (int i = 0; i < 5000; i++)
        {
            double x = 2_578_000 + rng.NextDouble() * 9_000, y = 1_106_000 + rng.NextDouble() * 8_000;
            double w = rng.NextDouble() * 1_500, h = rng.NextDouble() * 1_500;
            bool inBox = x + w >= r.MinE && x <= r.MaxE && y + h >= r.MinN && y <= r.MaxN;
            Assert.Equal(inBox, r.Touches(x, x + w, y, y + h));
        }
    }

    [Fact]
    public void An_empty_region_is_refused()
        => Assert.Throws<ArgumentException>(() => new TileRegion(Array.Empty<TileId>()));
}
