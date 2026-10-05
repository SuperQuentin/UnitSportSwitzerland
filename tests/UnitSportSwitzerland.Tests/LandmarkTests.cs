using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Landmark detection (#501): the nine IKEA stores recognised from a tile's own bytes.
///
/// <para>
/// These pin the rules, not the real data: the tiles are built here, because real tiles are a
/// 100 GB download and neither the generated world nor the fixture world has a building in it. The
/// nine coordinates were checked against real swissBUILDINGS3D tiles when they were written, and
/// <c>--ikeacheck</c> is what re-checks them; what is tested here is that a store-shaped box at a
/// noted point is found, that a mall, a tower or a neighbour is not, and that the answer cannot
/// depend on how the tiles happen to be cut.
/// </para>
/// </summary>
public class LandmarkTests
{
    /// <summary>A flat-topped box, as swissBUILDINGS3D would draw a shed: walls plus a roof.</summary>
    private static Building Box(double cx, double cz, float w, float d, float h, BuildingKind kind = BuildingKind.Commercial)
    {
        float x0 = (float)(cx - w / 2), x1 = (float)(cx + w / 2);
        float z0 = (float)(cz - d / 2), z1 = (float)(cz + d / 2);
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            tris.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
        }
        // four walls, two triangles each
        var corners = new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z1) };
        for (int i = 0; i < 4; i++)
        {
            var (ax, az) = corners[i];
            var (bx, bz) = corners[(i + 1) % 4];
            Tri(new Vector3(ax, 0, az), new Vector3(bx, 0, bz), new Vector3(bx, h, bz));
            Tri(new Vector3(ax, 0, az), new Vector3(bx, h, bz), new Vector3(ax, h, az));
        }
        // a flat roof
        Tri(new Vector3(x0, h, z0), new Vector3(x1, h, z0), new Vector3(x1, h, z1));
        Tri(new Vector3(x0, h, z0), new Vector3(x1, h, z1), new Vector3(x0, h, z1));
        return new Building { Kind = kind, MinY = 0, MaxY = h, Triangles = tris.ToArray() };
    }

    /// <summary>A tile holding the given buildings, in the frame every tile file uses.</summary>
    private static BuildingTile Tile(TileId id, params Building[] buildings) =>
        new() { Id = id, Buildings = buildings.ToList() };

    /// <summary>Where a store's point falls in its own tile: X east, Z south from the NW corner.</summary>
    private static (TileId Id, double X, double Z) Local(Landmark l)
    {
        var id = TileId.FromLv95(l.E, l.N);
        return (id, l.E - id.MinE, id.MaxN - l.N);
    }

    [Fact]
    public void There_are_nine_stores_and_they_are_all_in_Switzerland()
    {
        Assert.Equal(9, Landmarks.Ikea.Length);
        Assert.Equal(Landmarks.Ikea.Length, Landmarks.Ikea.Select(l => l.Name).Distinct().Count());
        foreach (var l in Landmarks.Ikea)
        {
            Assert.Equal(BuildingType.Ikea, l.Type);
            // the LV95 box Switzerland sits in
            Assert.InRange(l.E, 2480000, 2840000);
            Assert.InRange(l.N, 1070000, 1300000);
            // E/N really are the row's own lat/lon
            var (e, n) = SwissProjection.ToLv95(l.Lat, l.Lon);
            Assert.Equal(e, l.E, 1);
            Assert.Equal(n, l.N, 1);
        }
    }

    [Fact]
    public void No_two_stores_could_ever_claim_the_same_building()
    {
        // two points within a store's own length of each other would make the match order-dependent
        foreach (var a in Landmarks.Ikea)
            foreach (var b in Landmarks.Ikea)
            {
                if (a.Name == b.Name) continue;
                Assert.True(Math.Sqrt(Math.Pow(a.E - b.E, 2) + Math.Pow(a.N - b.N, 2)) > 2000,
                    $"{a.Name} and {b.Name} are too close to tell apart");
            }
    }

    [Fact]
    public void A_store_shaped_box_on_the_point_is_the_store()
    {
        foreach (var l in Landmarks.Ikea)
        {
            var (id, x, z) = Local(l);
            var tile = Tile(id, Box(x, z, 160, 110, 20));
            var map = BuildingTypes.For(tile);
            Assert.Equal(BuildingType.Ikea, map.TypeOf(0));
            Assert.Equal(BuildingPart.Store, map.PartOf(0));
            var group = map.GroupOf(0);
            Assert.NotNull(group);
            Assert.Equal(new[] { 0 }, group!.Members);
        }
    }

    [Fact]
    public void A_box_nowhere_near_a_store_is_nothing()
    {
        var l = Landmarks.Ikea[0];
        var (id, x, z) = Local(l);
        // same tile, same shape, 600 m away: its footprint does not cover the point
        var tile = Tile(id, Box(x + 600, z, 160, 110, 20));
        Assert.Equal(BuildingType.None, BuildingTypes.For(tile).TypeOf(0));
    }

    [Fact]
    public void A_tile_with_no_store_in_it_has_no_landmark()
    {
        // Bern's Bundesplatz, which is not an IKEA
        var id = TileId.FromLv95(2600600, 1199500);
        var tile = Tile(id, Box(500, 500, 160, 110, 20), Box(700, 300, 90, 70, 14));
        var map = BuildingTypes.For(tile);
        Assert.Empty(map.Groups);
    }

    [Theory]
    // a shopping centre's tower, and a flat block: too tall
    [InlineData(120f, 90f, 60f)]
    [InlineData(80f, 60f, 34f)]
    // a whole mall, and an airport-sized complex: too big
    [InlineData(400f, 200f, 20f)]
    [InlineData(700f, 380f, 25f)]
    // the garden centre, a trolley shelter, a substation: too small
    [InlineData(60f, 40f, 12f)]
    [InlineData(20f, 10f, 10f)]
    // a long low shed: big enough in plan, but no store is 6 m tall
    [InlineData(200f, 60f, 6f)]
    public void The_shape_band_keeps_out_what_is_not_a_store(float w, float d, float h)
    {
        var l = Landmarks.Ikea[0];
        var (id, x, z) = Local(l);
        var tile = Tile(id, Box(x, z, w, d, h));
        Assert.Equal(BuildingType.None, BuildingTypes.For(tile).TypeOf(0));
    }

    [Fact]
    public void Only_one_tile_of_a_region_ever_claims_a_store()
    {
        // The uniqueness the design rests on, and the reason matching is containment rather than
        // proximity. Near() deliberately offers a point to more than one tile -- St. Gallen's point
        // is 0.4 m from a tile edge and its store is in the tile next door -- so what has to hold
        // is that only the tile whose solid actually covers the point claims it. Here every tile of
        // the 3x3 ring holds a store-shaped solid, and only one of them covers the point.
        foreach (var l in Landmarks.Ikea)
        {
            var own = TileId.FromLv95(l.E, l.N);
            int claimed = 0;
            for (int de = -1; de <= 1; de++)
                for (int dn = -1; dn <= 1; dn++)
                {
                    var t = new TileId(own.E + de, own.N + dn);
                    var at = Landmarks.LocalPoint(t, l);
                    // the real store, in the point's own tile, covering the point; and in every
                    // other tile a decoy of the same size and shape, 300 m off the point
                    var box = de == 0 && dn == 0
                        ? Box(at.X, at.Y, 160, 110, 20)
                        : Box(at.X + 300, at.Y + 300, 160, 110, 20);
                    if (BuildingTypes.For(Tile(t, box)).TypeOf(0) == BuildingType.Ikea) claimed++;
                }
            Assert.Equal(1, claimed);
        }
    }

    [Fact]
    public void Near_reaches_into_the_neighbours_and_stops()
    {
        foreach (var l in Landmarks.Ikea)
        {
            var own = TileId.FromLv95(l.E, l.N);
            for (int de = -2; de <= 2; de++)
                for (int dn = -2; dn <= 2; dn++)
                {
                    var t = new TileId(own.E + de, own.N + dn);
                    // worked out here independently of Near: the tile grown by Reach
                    bool expected =
                        l.E >= t.MinE - Landmarks.Reach && l.E <= t.MinE + 1000 + Landmarks.Reach &&
                        l.N >= t.MinN - Landmarks.Reach && l.N <= t.MaxN + Landmarks.Reach;
                    Assert.Equal(expected, Landmarks.Near(t).Any(x => x.Name == l.Name));
                }
        }
    }

    [Fact]
    public void A_box_that_stops_short_of_the_point_is_not_the_store()
    {
        // the strictness the uniqueness guarantee is bought with: 20 m off the wall is a miss, and
        // --ikeacheck is what reports it rather than a wrong building being dressed as an IKEA
        var l = Landmarks.Ikea[0];
        var (id, x, z) = Local(l);
        // 160 m wide, so a box centred 70 m east of the point still covers it by 10 m
        Assert.Equal(BuildingType.Ikea, BuildingTypes.For(Tile(id, Box(x + 70, z, 160, 110, 20))).TypeOf(0));
        // centred 90 m east, its west wall is 10 m past the point: a miss, not a near miss
        Assert.Equal(BuildingType.None, BuildingTypes.For(Tile(id, Box(x + 90, z, 160, 110, 20))).TypeOf(0));
    }

    [Fact]
    public void Of_two_qualifying_solids_the_one_holding_the_point_wins()
    {
        var l = Landmarks.Ikea[0];
        var (id, x, z) = Local(l);
        // #0 is bigger but its footprint misses the point; #1 is smaller and covers it
        var tile = Tile(id, Box(x + 300, z, 220, 150, 22), Box(x, z, 120, 80, 18));
        var map = BuildingTypes.For(tile);
        Assert.Equal(BuildingType.None, map.TypeOf(0));
        Assert.Equal(BuildingType.Ikea, map.TypeOf(1));
    }

    [Fact]
    public void Detection_is_deterministic_and_does_not_depend_on_building_order()
    {
        var l = Landmarks.Ikea[3];
        var (id, x, z) = Local(l);
        var store = Box(x, z, 150, 120, 21);
        var other = Box(x + 400, z + 300, 90, 70, 15);

        var first = BuildingTypes.Detect(Tile(id, store, other));
        var second = BuildingTypes.Detect(Tile(id, store, other));
        Assert.Equal(first.TypeOf(0), second.TypeOf(0));
        Assert.Equal(BuildingType.Ikea, first.TypeOf(0));

        // the same two solids the other way round: the store is still the store
        var swapped = BuildingTypes.Detect(Tile(id, other, store));
        Assert.Equal(BuildingType.None, swapped.TypeOf(0));
        Assert.Equal(BuildingType.Ikea, swapped.TypeOf(1));
    }

    [Fact]
    public void A_store_does_not_stop_a_church_in_the_same_tile_from_being_one()
    {
        var l = Landmarks.Ikea[0];
        var (id, x, z) = Local(l);
        var store = Box(x, z, 160, 110, 20);
        var nave = Box(x + 400, z + 400, 24, 12, 11, BuildingKind.Sacral);
        var map = BuildingTypes.For(Tile(id, store, nave));
        Assert.Equal(BuildingType.Ikea, map.TypeOf(0));
        Assert.Equal(BuildingType.Church, map.TypeOf(1));
    }

    [Fact]
    public void Stored_numbers_are_pinned()
    {
        // these end up in stored plans and in saved loot records: changing one silently
        // re-reads every plan as something else
        Assert.Equal(8, (int)BuildingType.Ikea);
        Assert.Equal(3, (int)BuildingPart.Store);
        Assert.Equal(11, (int)UnitSport.Loot.ShopType.Ikea);
        Assert.Equal(200, (int)UnitSport.Items.ItemId.Blahaj);
        // and #497's industrial sites keep 3-7, whichever branch lands first
        Assert.Equal(2, (int)BuildingType.Bank);
    }
}
