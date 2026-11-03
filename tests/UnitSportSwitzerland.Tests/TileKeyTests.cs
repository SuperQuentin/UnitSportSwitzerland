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
}
