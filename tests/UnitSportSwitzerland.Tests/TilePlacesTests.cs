using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// A tile record's place on both worlds (#1704): <see cref="GlobalPos.OfTile"/> / <see cref="GlobalPos.InTile"/>, LV95 on an
/// LV95 tile and the leaf's anchor frame on an earth leaf's stand-in; and the LV95 entry points placing a stand-in's E/N.
/// </summary>
public class TilePlacesTests
{
    private static readonly CubeKey Geneva = CubeKey.FromLatLon(46.2044, 6.1432, LeafFrames.LeafLevel);

    [Fact]
    public void An_LV95_tile_place_is_its_LV95()
    {
        var tile = new TileId(2600, 1200);
        var p = GlobalPos.OfTile(tile, 250.5, 512.25, 730.75);
        Assert.True(p.DistanceTo(GlobalPos.FromLv95(2_600_250.5, 1_200_269.25, 512.25)) < 1e-6);
        var (x, y, z) = p.InTile(tile);
        Assert.Equal(250.5, x, 6);
        Assert.Equal(512.25, y, 6);
        Assert.Equal(730.75, z, 6);
        var (e, n, h) = p.InTileEn(tile);
        Assert.Equal(2_600_250.5, e, 6);
        Assert.Equal(1_200_269.25, n, 6);
        Assert.Equal(512.25, h, 6);
    }

    [Fact]
    public void A_stand_in_place_is_its_leaf_anchor_frame()
    {
        var tile = LeafStandIn.Id(Geneva);
        // the anchor's origin is the stand-in tile's (Offset, 0, Offset)
        var origin = GlobalPos.OfTile(tile, LeafStandIn.Offset, 0, LeafStandIn.Offset);
        Assert.True(origin.DistanceTo(GlobalPos.FromEcef(Geneva.Anchor.Origin)) < 1e-6);
        // 100 m east of the anchor is 100 m east on the ground, true north up the tile's -z
        var east = GlobalPos.OfTile(tile, LeafStandIn.Offset + 100, 0, LeafStandIn.Offset);
        var north = GlobalPos.OfTile(tile, LeafStandIn.Offset, 0, LeafStandIn.Offset - 100);
        Assert.Equal(100, origin.DistanceTo(east), 3);
        Assert.Equal(Math.PI / 2, origin.BearingTo(east), 3);
        Assert.Equal(0, origin.BearingTo(north), 3);
        var p = GlobalPos.OfTile(tile, 321.5, 47.25, 612.75);
        var (x, y, z) = p.InTile(tile);
        Assert.Equal(321.5, x, 5);
        Assert.Equal(47.25, y, 5);
        Assert.Equal(612.75, z, 5);
    }

    [Fact]
    public void LV95_notation_places_a_stand_in_record_by_its_tile()
    {
        var tile = LeafStandIn.Id(Geneva);
        var (e, n) = tile.Global(321.5, 612.75);
        Assert.True(e >= LeafStandIn.FirstPlaceE);
        var byName = GlobalPos.FromLv95(e, n, 47.25);
        Assert.True(byName.DistanceTo(GlobalPos.OfTile(tile, 321.5, 47.25, 612.75)) < 1e-6);
        // a real LV95 place is untouched
        Assert.True(GlobalPos.FromLv95(2_500_956, 1_117_569, 380).DistanceTo(GlobalPos.FromEcef(SwissGeodesy.ToEcef(2_500_956, 1_117_569, 380))) < 1e-9);
    }
}
