using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The tile key's own frame (#1443, G0c, docs/notes/general/flat-lv95-ratchet.md): what code outside the
/// terrain format asks a tile instead of doing LV95 kilometre arithmetic. Today each answer is that
/// arithmetic, exactly; these tests pin them to it so moving a call site changes nothing.
/// </summary>
[Collection(FixtureWorld.Collection)]
public class TileKeyTests
{
    private static readonly TileId T = new(2583, 1113);

    [Fact]
    public void Local_is_metres_from_the_north_west_corner_with_z_southward_and_Global_inverts_it()
    {
        Assert.Equal((0.0, 0.0), T.Local(2583000, 1114000));
        Assert.Equal((250.5, 900.25), T.Local(2583250.5, 1113099.75));
        Assert.Equal((2583250.5, 1113099.75), T.Global(250.5, 900.25));
        Assert.Equal((2583500.0, 1113500.0), T.Centre);
    }

    [Fact]
    public void Contains_takes_the_west_and_south_edges_but_not_the_east_and_north()
    {
        Assert.True(T.Contains(2583000, 1113000));
        Assert.True(T.Contains(2583999.9, 1113999.9));
        Assert.False(T.Contains(2584000, 1113500));
        Assert.False(T.Contains(2583500, 1114000));
    }

    [Fact]
    public void Neighbour_steps_in_the_tiles_local_axes_x_east_z_south()
    {
        Assert.Equal(new TileId(2584, 1113), T.Neighbour(1, 0));
        Assert.Equal(new TileId(2582, 1113), T.Neighbour(-1, 0));
        Assert.Equal(new TileId(2583, 1112), T.Neighbour(0, 1));    // south
        Assert.Equal(new TileId(2583, 1114), T.Neighbour(0, -1));   // north
        Assert.Equal(new TileId(2584, 1114), T.Neighbour(1, -1));
    }

    [Fact]
    public void RingDistance_is_the_larger_axis()
    {
        Assert.Equal(0, T.RingDistance(T));
        Assert.Equal(1, T.RingDistance(T.Neighbour(1, 1)));
        Assert.Equal(3, T.RingDistance(new TileId(2580, 1112)));
    }

    [Theory]
    [InlineData(2583000, 1113000)]
    [InlineData(2580000, 1111000)]   // the tile 3 km away from the origin
    public void The_origin_places_tile_local_points_exactly_as_the_corner_arithmetic_did(double oe, double on)
    {
        var origin = new OriginFrame(oe, on, 0);
        var local = new Vector3(250.5f, 431.25f, 900.75f);
        // what call sites wrote before: ToWorld(MinE + x, MaxN - z, y)
        var before = origin.ToWorld(T.MinE + local.X, T.MaxN - local.Z, local.Y);
        Assert.Equal(before, origin.TileToWorld(T, local));
        Assert.Equal(origin.ToWorld(T.MinE, T.MaxN, 0), origin.TileTransform(T).Origin);
        Assert.Equal(Basis.Identity, origin.TileTransform(T).Basis);
        var back = origin.TileLocal(T, before);
        Assert.True((back - local).Length() < 1e-3f, $"{back} vs {local}");
    }

    // LV95 points round the tile: on its edges, just inside and outside, kilometres away, and random
    // ones over the whole of Switzerland with sub-millimetre fractions
    private static IEnumerable<(double E, double N)> Around()
    {
        double[] es = { T.MinE, T.MinE + 1000, T.MinE - 0.001, T.MinE + 999.999, T.MinE + 1000.001, T.MinE - 4321.5, T.MinE + 250.123456789 };
        double[] ns = { T.MinN, T.MaxN, T.MinN - 0.001, T.MaxN - 0.001, T.MaxN + 0.001, T.MaxN + 4321.5, T.MinN + 750.987654321 };
        foreach (var e in es) foreach (var n in ns) yield return (e, n);
        var rng = new Random(1443);
        for (int i = 0; i < 2000; i++) yield return (2_480_000 + rng.NextDouble() * 360_000, 1_070_000 + rng.NextDouble() * 230_000);
    }

    [Fact]
    public void Reach_tests_in_local_metres_give_the_bits_the_LV95_ones_did()
    {
        // G0c moved the "how far from the tile's square" and "box touches the tile" tests of src/Terrain
        // (CollisionReach, SiteSounds, LanePlanDebug, StadiumClip) into the tile's frame: LV95 differences
        // are exact (both sides within a factor 2, Sterbenz), so each rewrite is the same number
        foreach (var (e, n) in Around())
        {
            var (x, z) = T.Local(e, n);
            Assert.Equal(Math.Max(0, Math.Max(T.MinE - e, e - (T.MinE + 1000))), Math.Max(0, Math.Max(-x, x - 1000)));
            Assert.Equal(Math.Max(0, Math.Max(T.MinN - n, n - (T.MinN + 1000))), Math.Max(0, Math.Max(z - 1000, -z)));
            Assert.Equal(Math.Abs(Math.Clamp(e, T.MinE, T.MinE + 1000) - e), Math.Abs(Math.Clamp(x, 0, 1000) - x));
            Assert.Equal(Math.Abs(Math.Clamp(n, T.MaxN - 1000, T.MaxN) - n), Math.Abs(Math.Clamp(z, 0, 1000) - z));
            // a box from (e, n) to 37.5 m north-east of it
            double e1 = e + 37.5, n1 = n + 37.5;
            var (west, north) = T.Local(e, n1);
            var (east, south) = T.Local(e1, n);
            Assert.Equal(e1 >= T.MinE && e <= T.MinE + 1000 && n1 >= T.MinN && n <= T.MaxN,
                east >= 0 && west <= 1000 && north <= 1000 && south >= 0);
        }
    }

    [Fact]
    public void Tile_points_through_Global_are_the_corner_sums()
    {
        foreach (var (e, n) in Around())
        {
            // a float tile-local point, as the builders hold them
            float x = (float)(e - T.MinE), z = (float)(T.MaxN - n);
            Assert.Equal((T.MinE + x, T.MaxN - z), T.Global(x, z));
            Assert.Equal(((float)(e - T.MinE), (float)(T.MaxN - n)), ((float)T.Local(e, n).X, (float)T.Local(e, n).Z));
        }
    }

    [Fact]
    public void Touches_is_the_closed_box_test_and_Lv95Box_the_corners()
    {
        Assert.Equal((T.MinE, T.MinN, T.MinE + 1000, T.MaxN), T.Lv95Box);
        foreach (var (e, n) in Around())
        {
            double e1 = e + 37.5, n1 = n + 37.5;
            // what StadiumClip, the fixture's pistes and fields and the generator's seam wrote
            Assert.Equal(e1 >= T.MinE && e <= T.MinE + 1000 && n1 >= T.MinN && n <= T.MaxN, T.Touches(e, n, e1, n1));
            Assert.Equal(!(e < T.MinE || e > T.MinE + 1000 || n < T.MinN || n > T.MaxN), T.Touches(e, n, e, n));
        }
    }

    [Fact]
    public void South_west_ring_points_from_the_local_frame_are_the_old_ones()
    {
        // the field and piste formats hold rings from the tile's south-west corner (FixtureChunkSource)
        foreach (var (e, n) in Around())
        {
            var (x, z) = T.Local(e, n);
            Assert.Equal(n - T.MinN, 1000 - z);
            Assert.Equal((float)(e - T.MinE), (float)x);
        }
    }

    [Fact]
    public void A_tile_rectangle_lists_its_tiles_south_to_north_and_bounds_a_set()
    {
        var rect = new TileRect(2583, 1113, 2584, 1114);
        Assert.Equal(new[] { new TileId(2583, 1113), new TileId(2584, 1113), new TileId(2583, 1114), new TileId(2584, 1114) }, rect.Tiles());
        Assert.Equal(rect, TileRect.Bounding(rect.Tiles()));
        Assert.Null(TileRect.Bounding([]));
    }
}
