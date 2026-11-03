using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The call sites moved onto the tile key's frame in World, Farming, Vehicles, Birds, Items, Core, Net
/// and Concert (#1443, G0c) whose new form is not the old arithmetic written the other way round: each
/// is pinned here to what it replaced, bit for bit, so the move changes nothing in game.
/// </summary>
public class TileFrameMovesTests
{
    private static readonly TileId T = new(2583, 1113);

    private static void Same(double expected, double actual, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: {expected:R} vs {actual:R}");

    [Fact]
    public void A_raster_counted_north_from_the_south_west_corner_lands_where_MinN_plus_north_did()
    {
        // pistes (float metres east and north of the south-west corner) and field cells (4 m, rows northward):
        // Global(east, size - north) is MinE + east, MaxN - (size - north), which is MinN + north exactly
        // because size - north is exact for every float of a tile
        var rng = new Random(1443);
        var edge = new[] { 0f, 1000f, 0.5f, 999.99f, 1e-3f, 4e-6f, -0.25f, 1000.5f, 500.123f };
        foreach (float north in edge.Concat(Enumerable.Range(0, 200_000).Select(_ => (float)(rng.NextDouble() * 1010 - 5))))
        {
            float east = (float)(rng.NextDouble() * 1000);
            var (e, n) = T.Global(east, ChunkFormat.TileSizeM - north);
            Same(T.MinE + east, e, $"east {east:R}");
            Same(T.MinN + north, n, $"north {north:R}");
        }
        // the field chunk's node: its z as a float (FarmField.Draw)
        for (int r0 = 0; r0 <= 250; r0 += 25)
        {
            float z = (float)ChunkFormat.TileSizeM - r0 * FieldFormat.CellSize;
            Same(T.MinN + r0 * FieldFormat.CellSize, T.MaxN - z, $"row {r0}");
        }
    }

    [Fact]
    public void The_distance_outside_a_tile_is_the_same_from_its_local_frame()
    {
        // FarmField.EdgeDistance: the tile's square in its own frame against its LV95 corners
        var rng = new Random(7);
        for (int i = 0; i < 100_000; i++)
        {
            double e = T.MinE - 600 + rng.NextDouble() * 2200, n = T.MinN - 600 + rng.NextDouble() * 2200;
            double dx0 = Math.Max(0, Math.Max(T.MinE - e, e - (T.MinE + ChunkFormat.TileSizeM)));
            double dy0 = Math.Max(0, Math.Max(T.MinN - n, n - T.MaxN));
            var (x, z) = T.Local(e, n);
            double dx1 = Math.Max(0, Math.Max(-x, x - ChunkFormat.TileSizeM));
            double dy1 = Math.Max(0, Math.Max(z - ChunkFormat.TileSizeM, -z));
            Same(Math.Sqrt(dx0 * dx0 + dy0 * dy0), Math.Sqrt(dx1 * dx1 + dy1 * dy1), $"({e:R}, {n:R})");
        }
    }

    [Fact]
    public void Corners_of_a_span_of_tiles_come_from_its_two_end_tiles()
    {
        // FunBackdrop: lo's south-west corner and hi's north-east one
        var lo = new TileId(2583, 1113);
        var hi = new TileId(2586, 1115);
        var (minE, minN) = lo.Global(0, ChunkFormat.TileSizeM);
        var (maxE, maxN) = hi.Global(ChunkFormat.TileSizeM, 0);
        Same(lo.MinE, minE, "min E");
        Same(lo.MinN, minN, "min N");
        Same(hi.MinE + ChunkFormat.TileSizeM, maxE, "max E");
        Same(hi.MaxN, maxN, "max N");
    }

    [Theory]
    [InlineData(2583000, 1113000)]
    [InlineData(2580000, 1111000)]
    public void A_tile_transform_and_its_inverse_are_the_corner_added_and_taken_away(double oe, double on)
    {
        // BirdLife.NearestTreeTop, DebugOverlay.AddTile: xf * local and xf⁻¹ * world against corner ± v
        var origin = new OriginFrame(oe, on, 0);
        var xf = origin.TileTransform(T);
        var inverse = xf.AffineInverse();
        var corner = origin.ToWorld(T.MinE, T.MaxN, 0);
        var rng = new Random(3);
        for (int i = 0; i < 10_000; i++)
        {
            var v = new Vector3((float)(rng.NextDouble() * 1200 - 100), (float)(rng.NextDouble() * 400 - 50), (float)(rng.NextDouble() * 1200 - 100));
            Assert.Equal(v + corner, xf * v);
            Assert.Equal(v - corner, inverse * v);
        }
    }

    [Fact]
    public void A_neighbour_ring_walked_x_then_z_southward_keeps_the_old_east_then_north_order()
    {
        // ContainerRules, Traffic, DormantVehicles: de outer, dn inner ascending -> dx outer, dz descending
        const int r = 2;
        var before = new List<TileId>();
        for (int de = -r; de <= r; de++)
            for (int dn = -r; dn <= r; dn++)
                before.Add(new TileId(T.E + de, T.N + dn));
        var after = new List<TileId>();
        for (int dx = -r; dx <= r; dx++)
            for (int dz = r; dz >= -r; dz--)
                after.Add(T.Neighbour(dx, dz));
        Assert.Equal(before, after);

        // PisteLayer and FarmField: dn outer, de inner -> dz descending outer, dx inner
        before.Clear(); after.Clear();
        for (int dn = -r; dn <= r; dn++)
            for (int de = -r; de <= r; de++)
                before.Add(new TileId(T.E + de, T.N + dn));
        for (int dz = r; dz >= -r; dz--)
            for (int dx = -r; dx <= r; dx++)
                after.Add(T.Neighbour(dx, dz));
        Assert.Equal(before, after);
    }
}
