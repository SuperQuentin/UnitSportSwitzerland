using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Building pads graded into the tile heights (#1419).</summary>
public class BuildingPadTests
{
    private const int N = ChunkFormat.GridSize;

    private static ushort[] Slope(TileId tile, double rise)
    {
        var h = new ushort[N * N];
        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
                h[r * N + c] = ChunkFormat.Quantize(500 + rise * (tile.MinE + c - 2600000));   // rises eastwards across tiles
        return h;
    }

    private static BuildingPad.Footprint Square(double e0, double n0, double e1, double n1, double level) => new()
    {
        Edges = [e0, n0, e1, n0, e1, n0, e1, n1, e1, n1, e0, n1, e0, n1, e0, n0],
        Level = level, MinE = e0, MinN = n0, MaxE = e1, MaxN = n1, Closed = true,
    };

    private static double Height(ushort[] h, int c, int r) => ChunkFormat.Dequantize(h[r * N + c]);

    [Fact]
    public void Ground_level_is_the_cheapest_candidate_and_the_median_on_even_ground()
    {
        Assert.Equal(500.0, BuildingPad.ChooseGround([500, 500, 500, 500]), 6);
        // a slope: a steep site is seated low, so the uphill side is buried rather than the downhill side left standing on air
        var samples = Enumerable.Range(0, 21).Select(i => 500.0 + i * 0.5).ToArray();   // 10 m of fall over the footprint
        double g = BuildingPad.ChooseGround(samples);
        Assert.InRange(g, 500.0, 505.01);
    }

    [Fact]
    public void Pad_levels_inside_fades_past_the_shoulder_and_leaves_far_ground_alone()
    {
        var tile = new TileId(2600, 1100);
        var h = Slope(tile, 0.05);   // 5 cm per metre eastwards
        var before = (ushort[])h.Clone();
        // a 10 m square at columns 500..510, rows 500..510; the level is the ground at its centre
        double level = Height(h, 505, 505);
        var f = Square(tile.MinE + 500, tile.MaxN - 510, tile.MinE + 510, tile.MaxN - 500, level);
        int changed = BuildingPad.Grade(tile, h, [f]);
        Assert.True(changed > 0);
        for (int c = 500; c <= 510; c++)
            Assert.Equal(level, Height(h, c, 505), 1);   // inside: the level, within quantisation
        // past the reach nothing moves
        double reach = BuildingPad.Reach + 1;
        Assert.Equal(before[505 * N + (int)(510 + reach)], h[505 * N + (int)(510 + reach)]);
        Assert.Equal(before[505 * N + (int)(500 - reach)], h[505 * N + (int)(500 - reach)]);
        // no step in the result either: neighbouring cells differ by about the natural 5 cm per metre, plus the fade's slope
        for (int c = 505; c < 510 + BuildingPad.Reach + 1; c++)
            Assert.True(Math.Abs(Height(h, c + 1, 505) - Height(h, c, 505)) < 0.45, $"a step at column {c}");
    }

    [Fact]
    public void No_cell_moves_more_than_the_cap()
    {
        var tile = new TileId(2600, 1100);
        var h = Slope(tile, 0.05);
        var before = (ushort[])h.Clone();
        // a level 20 m above the ground
        BuildingPad.Grade(tile, h, [Square(tile.MinE + 500, tile.MaxN - 510, tile.MinE + 510, tile.MaxN - 500, Height(h, 505, 505) + 20)]);
        for (int k = 0; k < h.Length; k++)
            Assert.True(Math.Abs(ChunkFormat.Dequantize(h[k]) - ChunkFormat.Dequantize(before[k])) <= BuildingPad.Cap + 0.08);
    }

    [Fact]
    public void A_pad_across_a_tile_border_gives_the_same_heights_in_both_tiles()
    {
        var west = new TileId(2600, 1100);
        var east = new TileId(2601, 1100);
        var hw = Slope(west, 0.05);
        var he = Slope(east, 0.05);
        // a footprint across the border, and another neighbour 4 m from it: order fixed
        var a = Square(west.MinE + 995, west.MaxN - 520, west.MinE + 1005, west.MaxN - 500, 507.0);
        var b = Square(west.MinE + 1009, west.MaxN - 523, west.MinE + 1017, west.MaxN - 503, 508.2);
        BuildingPad.Grade(west, hw, [a, b]);
        BuildingPad.Grade(east, he, [a, b]);
        for (int r = 0; r < N; r++)
            Assert.Equal(hw[r * N + (N - 1)], he[r * N]);
        Assert.NotEqual(Slope(west, 0.05)[505 * N + N - 1], hw[505 * N + N - 1]);
    }

    [Fact]
    public void Footprint_comes_from_the_wall_bottoms_and_a_closed_outline_tells_inside()
    {
        // a 10 x 6 box, base at y = 100, walls up to 106; tile-local X east, Z south
        float[] Quad(float x0, float z0, float x1, float z1) =>
            [x0, 100, z0, x1, 100, z1, x1, 106, z1, x0, 100, z0, x1, 106, z1, x0, 106, z0];
        var tris = new List<float>();
        tris.AddRange(Quad(100, 100, 110, 100));
        tris.AddRange(Quad(110, 100, 110, 106));
        tris.AddRange(Quad(110, 106, 100, 106));
        tris.AddRange(Quad(100, 106, 100, 100));
        // a flat roof: ignored for the outline, kept as plan
        tris.AddRange([100, 106, 100, 110, 106, 100, 110, 106, 106]);
        var tile = new TileId(2600, 1100);
        var b = new Building { Kind = BuildingKind.House, MinY = 100, MaxY = 106, Triangles = [.. tris] };
        var f = BuildingPad.FootprintOf(b, tile);
        Assert.NotNull(f);
        Assert.True(f!.Closed);
        Assert.Equal(100 + BuildingPad.FoundationDepth, f.Level, 6);
        Assert.True(f.SignedDistance(tile.MinE + 105, tile.MaxN - 103) < 0);
        Assert.Equal(2.0, f.SignedDistance(tile.MinE + 112, tile.MaxN - 103), 6);
    }
}
