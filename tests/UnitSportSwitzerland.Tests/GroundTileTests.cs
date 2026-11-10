using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The ground's opaque tile key (#1532, G2 C1): what <c>IWorldGround</c>'s events and per-tile lookups carry
/// instead of an LV95 <see cref="TileId"/>. In the flat world it is exactly the tile it was made from, so a
/// subscriber keyed by it, or by its content's name, sees the same tiles as before.
/// </summary>
public class GroundTileTests
{
    private static readonly TileId T = new(2583, 1113);

    [Fact]
    public void A_flat_tile_gives_back_the_LV95_tile_it_was_made_from()
    {
        Assert.Equal(T, GroundTile.Flat(T).FlatId);
        Assert.Equal(new TileId(-1, 0), GroundTile.Flat(new TileId(-1, 0)).FlatId);
    }

    [Fact]
    public void Tiles_are_equal_exactly_when_their_LV95_tiles_are()
    {
        Assert.Equal(GroundTile.Flat(T), GroundTile.Flat(new TileId(2583, 1113)));
        Assert.True(GroundTile.Flat(T) == GroundTile.Flat(T));
        Assert.True(GroundTile.Flat(T) != GroundTile.Flat(T.Neighbour(1, 0)));
        Assert.NotEqual(GroundTile.Flat(T), GroundTile.Flat(T.Neighbour(0, 1)));
    }

    [Fact]
    public void A_tile_is_a_dictionary_key_and_names_itself_as_its_LV95_tile()
    {
        var seen = new Dictionary<GroundTile, int> { [GroundTile.Flat(T)] = 1 };
        Assert.True(seen.ContainsKey(GroundTile.Flat(new TileId(2583, 1113))));
        Assert.False(seen.ContainsKey(GroundTile.Flat(T.Neighbour(1, 1))));
        Assert.Equal(T.ToString(), GroundTile.Flat(T).ToString());
    }

    [Fact]
    public void Its_content_is_filed_under_the_flat_tile_or_the_leaf_s_stand_in()
    {
        // #1684: what IWorldGround.ContentName gives (Content loads, DoorIndex, BuildingKey)
        Assert.Equal(T, GroundTile.Flat(T).ContentName);
        var leaf = CubeKey.FromLatLon(46.2, 6.15, UnitSport.Terrain.Leaves.LeafFrames.LeafLevel);
        var tile = GroundTile.FromLeaf(leaf);
        Assert.Equal(UnitSport.Terrain.Leaves.LeafStandIn.Id(leaf), tile.ContentName);
        Assert.Equal(default, tile.FlatId);   // a leaf has no LV95 tile
    }
}
