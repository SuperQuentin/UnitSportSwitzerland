using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Castle matching (#859): a solid is a castle's when its real footprint holds one of the points
/// <c>TerrainPreprocessor --castles</c> wrote. Like <see cref="LandmarkTests"/>, the tiles are built
/// here; the generator resolved the points against the real tiles.
/// </summary>
public class CastleTests
{
    // a flat-topped box: four walls and a roof
    private static Building Box(float cx, float cz, float w, float d, float h)
    {
        float x0 = cx - w / 2, x1 = cx + w / 2, z0 = cz - d / 2, z1 = cz + d / 2;
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) => tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
        var corners = new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z1) };
        for (int i = 0; i < 4; i++)
        {
            var (ax, az) = corners[i];
            var (bx, bz) = corners[(i + 1) % 4];
            Tri(new Vector3(ax, 0, az), new Vector3(bx, 0, bz), new Vector3(bx, h, bz));
            Tri(new Vector3(ax, 0, az), new Vector3(bx, h, bz), new Vector3(ax, h, az));
        }
        Tri(new Vector3(x0, h, z0), new Vector3(x1, h, z0), new Vector3(x1, h, z1));
        Tri(new Vector3(x0, h, z0), new Vector3(x1, h, z1), new Vector3(x0, h, z1));
        return new Building { Kind = BuildingKind.Annex, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
    }

    private static (int Index, TileId Id, Vector2 At) Chillon()
    {
        int i = Array.FindIndex(Castles.List, c => c.Name == "Château de Chillon");
        var p = Castles.List[i].Points;
        var id = TileId.FromLv95(p[0], p[1]);
        return (i, id, new Vector2((float)(p[0] - id.MinE), (float)(id.MaxN - p[1])));
    }

    [Fact]
    public void The_list_holds_the_famous_ones_and_every_castle_has_points()
    {
        foreach (var name in new[] { "Château de Chillon", "Schloss Lenzburg", "Castelgrande", "Schloss Thun", "Château de Gruyères" })
            Assert.Contains(Castles.List, c => c.Name == name);
        Assert.All(Castles.List, c => Assert.True(c.Points.Length >= 2 && c.Points.Length % 2 == 0, c.Name));
    }

    [Fact]
    public void A_solid_covering_the_point_is_the_castle_and_its_neighbour_is_not()
    {
        var (castle, id, at) = Chillon();
        var tile = new BuildingTile { Id = id, Buildings = new() { Box(at.X, at.Y, 20, 20, 15), Box(at.X + 30, at.Y, 10, 10, 8) } };
        var match = Castles.Match(tile);
        Assert.NotNull(match);
        Assert.Equal(castle, match![0]);
        Assert.Equal(-1, match[1]);
        Assert.True(BuildingTypes.Detect(tile).IsCastle(0));
    }

    [Fact]
    public void A_point_off_every_footprint_matches_nothing()
    {
        // two solids round the point, as round a courtyard: the point is on neither
        var (_, id, at) = Chillon();
        var tile = new BuildingTile { Id = id, Buildings = new() { Box(at.X - 10, at.Y + 5, 10, 30, 10), Box(at.X + 5, at.Y + 10, 20, 10, 10) } };
        Assert.False(tile.Buildings[0].Covers(at.X, at.Y));
        Assert.Null(Castles.Match(tile));
    }

    [Fact]
    public void Castles_know_their_canton_and_kind()
    {
        var chillon = Castles.List.First(c => c.Name == "Château de Chillon");
        Assert.Equal("VD", chillon.Canton);
        Assert.Equal(CastleKind.Fortress, chillon.Kind);
        Assert.Equal(CastleRegion.Ticino, Castles.RegionOf(Castles.List.First(c => c.Name == "Castelgrande").Canton));
        // ruins are named as such, or tagged
        Assert.Contains(Castles.List, c => c.Kind == CastleKind.Ruin && c.Name.StartsWith("Ruine", StringComparison.Ordinal));
        // every tradition has castles of its own
        foreach (var region in Enum.GetValues<CastleRegion>())
            Assert.Contains(Castles.List, c => Castles.RegionOf(c.Canton) == region);
    }

    [Theory]
    [InlineData("VD", CastleRegion.Savoy)]
    [InlineData("GE", CastleRegion.Savoy)]
    [InlineData("", CastleRegion.Savoy)]
    [InlineData("NE", CastleRegion.Jura)]
    [InlineData("BE", CastleRegion.Plateau)]
    [InlineData("TG", CastleRegion.East)]
    [InlineData("VS", CastleRegion.Alpine)]
    [InlineData("GR", CastleRegion.Raetia)]
    [InlineData("TI", CastleRegion.Ticino)]
    public void A_canton_has_its_building_tradition(string canton, CastleRegion region) =>
        Assert.Equal(region, Castles.RegionOf(canton));

    [Fact]
    public void A_tile_far_from_every_castle_has_none()
    {
        var tile = new BuildingTile { Id = new TileId(0, 0), Buildings = new() { Box(500, 500, 20, 20, 10) } };
        Assert.Null(Castles.Match(tile));
    }
}
