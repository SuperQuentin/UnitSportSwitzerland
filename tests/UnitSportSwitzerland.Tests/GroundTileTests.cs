using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The ground's opaque tile key (#1532, G2 C1): what <c>IWorldGround</c>'s events and per-tile lookups carry
/// instead of an LV95 <see cref="TileId"/>. In the flat world it is exactly the tile it was made from, so a
/// subscriber keyed by it, or converting back to the LV95 key, sees the same tiles as before.
/// </summary>
public class GroundTileTests
{
    private static readonly TileId T = new(2583, 1113);

    [Fact]
    public void A_flat_tile_gives_back_the_LV95_tile_it_was_made_from()
    {
        Assert.Equal(T, GroundTile.FromLv95(T).Lv95Tile);
        Assert.Equal(new TileId(-1, 0), GroundTile.FromLv95(new TileId(-1, 0)).Lv95Tile);
    }

    [Fact]
    public void Tiles_are_equal_exactly_when_their_LV95_tiles_are()
    {
        Assert.Equal(GroundTile.FromLv95(T), GroundTile.FromLv95(new TileId(2583, 1113)));
        Assert.True(GroundTile.FromLv95(T) == GroundTile.FromLv95(T));
        Assert.True(GroundTile.FromLv95(T) != GroundTile.FromLv95(T.Neighbour(1, 0)));
        Assert.NotEqual(GroundTile.FromLv95(T), GroundTile.FromLv95(T.Neighbour(0, 1)));
    }

    [Fact]
    public void A_tile_is_a_dictionary_key_and_names_itself_as_its_LV95_tile()
    {
        var seen = new Dictionary<GroundTile, int> { [GroundTile.FromLv95(T)] = 1 };
        Assert.True(seen.ContainsKey(GroundTile.FromLv95(new TileId(2583, 1113))));
        Assert.False(seen.ContainsKey(GroundTile.FromLv95(T.Neighbour(1, 1))));
        Assert.Equal(T.ToString(), GroundTile.FromLv95(T).ToString());
    }
}
