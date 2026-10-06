using UnitSport.Terrain.Format;
using UnitSport.Vehicles;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// What stands in an industrial site's yard (#496 phase 3, #516): the second provider for the
/// dormant layer #499 built. Pinned for the same reason <see cref="DormantSlotTests"/> is — the
/// fleet is never sent, so every peer has to derive the identical one from the tile's bytes.
/// </summary>
public class SiteYardTests
{
    private static readonly TileId Tile = new(2600, 1120);

    // `Interiors.BuildingType` as the ints `SiteYard.SiteType` carries
    private const int Warehouse = 3, Factory = 4, Depot = 5, Mechanic = 6, Dealership = 7;

    private static readonly int[] Cars = { 8, 9, 10, 11 };
    private static readonly int[] Heavies = { 96, 97 };
    // TrailerCatalog codes: (index + 1) | load% << 8
    private static readonly int[] Trailers = { 1, 1 | (55 << 8), 2 | (100 << 8), 3 };
    private const int TrailerKind = 120;

    private static SiteYard Yard(int type, float heading = 0f, float w = 40f, float d = 34f) =>
        new($"{Tile.E}_{Tile.N}_7", type, 300f, 470f, 300f, heading, w, d);

    private static List<VehicleSlot> Slots(params SiteYard[] yards)
    {
        var into = new List<VehicleSlot>();
        DormantSlots.ForSite(Tile, yards, Cars, Heavies, Trailers, TrailerKind, into);
        return into;
    }

    [Fact]
    public void The_same_yard_gives_the_same_fleet_every_time()
    {
        var first = Slots(Yard(Depot));
        Assert.NotEmpty(first);
        for (int i = 0; i < 4; i++) Assert.Equal(first, Slots(Yard(Depot)));
    }

    [Fact]
    public void Two_yards_on_one_tile_do_not_share_a_name()
    {
        var a = new SiteYard("2600_1120_7", Depot, 300f, 470f, 300f, 0f, 40f, 34f);
        var b = new SiteYard("2600_1120_9", Depot, 700f, 470f, 700f, 0f, 40f, 34f);
        var slots = Slots(a, b);
        Assert.Equal(slots.Select(s => s.NodeName).Distinct().Count(), slots.Count);
        // the owner is what separates them: ordinals restart at 0 for each
        Assert.Contains(slots, s => s.Owner == a.Owner);
        Assert.Contains(slots, s => s.Owner == b.Owner);
    }

    [Fact]
    public void An_owner_is_a_building_key_whose_first_two_parts_are_its_tile()
    {
        // the one constraint DormantVehicles.SlotOf puts on a provider
        foreach (var s in Slots(Yard(Depot)))
        {
            var p = s.Owner.Split('_');
            Assert.Equal(3, p.Length);
            Assert.Equal(Tile.E, int.Parse(p[0]));
            Assert.Equal(Tile.N, int.Parse(p[1]));
            // and the node name still parses: prefix, owner, ordinal last
            var n = s.NodeName.Split('_');
            Assert.True(n.Length >= 5);
            Assert.Equal(s.Ordinal, int.Parse(n[^1]));
        }
    }

    [Fact]
    public void A_slot_keeps_its_ordinal_however_full_the_yard_is()
    {
        // ordinals come from the grid cell, not from a running count of what was placed, so a
        // vehicle never changes name because a neighbouring cell filled or emptied
        var slots = Slots(Yard(Depot));
        foreach (var s in slots) Assert.InRange(s.Ordinal, 0, 400);
        Assert.Equal(slots.Select(s => s.Ordinal).Distinct().Count(), slots.Count);
    }

    [Fact]
    public void A_hauliers_yard_holds_trailers_tractors_and_whole_artics()
    {
        var slots = Slots(Yard(Depot, w: 60f, d: 68f));
        Assert.Contains(slots, s => s.KindId == TrailerKind && s.Train != 0);          // on its legs
        Assert.Contains(slots, s => s.KindId != TrailerKind && s.Train != 0);          // coupled
        Assert.Contains(slots, s => Heavies.Contains(s.KindId) && s.Train == 0);       // bare tractor
        Assert.Contains(slots, s => Cars.Contains(s.KindId));                          // somebody drove in
    }

    [Fact]
    public void A_coupled_artic_is_one_slot_not_two()
    {
        // the whole point of Train riding on a truck kind: the pin angles are parked state
        var coupled = Slots(Yard(Depot, w: 60f, d: 68f))
            .Where(s => s.Train != 0 && s.KindId != TrailerKind).ToList();
        Assert.NotEmpty(coupled);
        foreach (var s in coupled) Assert.Contains(s.KindId, Heavies);
    }

    [Fact]
    public void A_lone_trailer_is_never_given_the_tractors_own_load()
    {
        // Load is the tractor's payload; a trailer's is in Train's high byte. A lone trailer has no
        // tractor, so Load must stay 0 or it reads as a physics bug that nothing reports.
        foreach (var s in Slots(Yard(Warehouse, w: 60f, d: 68f)).Where(s => s.KindId == TrailerKind))
        {
            Assert.NotEqual(0, s.Train);
            Assert.Equal(0f, s.Load);
        }
    }

    [Fact]
    public void A_warehouse_yard_is_mostly_trailers_and_a_body_shop_has_none()
    {
        var warehouse = Slots(Yard(Warehouse, w: 60f, d: 68f));
        int trailers = warehouse.Count(s => s.KindId == TrailerKind);
        Assert.True(trailers > warehouse.Count / 2, $"{trailers} of {warehouse.Count} are trailers");

        // a body shop and a works park cars, not goods vehicles
        foreach (int type in new[] { Mechanic, Factory })
            foreach (var s in Slots(Yard(type, w: 60f, d: 68f)))
            {
                Assert.Contains(s.KindId, Cars);
                Assert.Equal(0, s.Train);
            }
    }

    [Fact]
    public void A_dealerships_stock_all_faces_the_same_way_and_a_working_yard_does_not()
    {
        var forecourt = Slots(Yard(Dealership, heading: 1.1f, w: 40f, d: 17f));
        Assert.NotEmpty(forecourt);
        Assert.All(forecourt, s => Assert.Equal(1.1f, s.Yaw, 3));
        Assert.All(forecourt, s => Assert.False(s.Van));   // no vans in a stock row

        var works = Slots(Yard(Factory, heading: 1.1f, w: 60f, d: 40f));
        Assert.Contains(works, s => Math.Abs(s.Yaw - 1.1f) > 1f);   // the other way round
    }

    [Fact]
    public void A_forecourt_is_fuller_than_a_works_car_park()
    {
        // same ground, different trade: the fill is what makes a dealership read as one
        int forecourt = Slots(Yard(Dealership, w: 48f, d: 36f)).Count;
        int works = Slots(Yard(Factory, w: 48f, d: 36f)).Count;
        Assert.True(forecourt > works, $"forecourt {forecourt} vs works {works}");
    }

    [Fact]
    public void Every_slot_stands_inside_its_own_yard()
    {
        foreach (float heading in new[] { 0f, 0.7f, 2.5f, 4.9f })
        {
            var yard = Yard(Depot, heading, 48f, 40f);
            foreach (var s in Slots(yard))
            {
                // back to tile-local, then into the yard's own frame
                float x = (float)(s.E - Tile.MinE) - yard.X, z = (float)(Tile.MaxN - s.N) - yard.Z;
                float cos = MathF.Cos(heading), sin = MathF.Sin(heading);
                float u = x * cos - z * sin, v = -x * sin - z * cos;
                Assert.InRange(u, -yard.Width / 2 - 0.01f, yard.Width / 2 + 0.01f);
                Assert.InRange(v, -yard.Depth / 2 - 0.01f, yard.Depth / 2 + 0.01f);
            }
        }
    }

    [Fact]
    public void A_yard_too_small_for_one_vehicle_gives_nothing()
    {
        Assert.Empty(Slots(Yard(Depot, w: 3f, d: 3f)));
        Assert.Empty(Slots(Yard(Dealership, w: 2f, d: 40f)));
        // and no cars in the catalogue means no fleet at all, as for a car park
        var into = new List<VehicleSlot>();
        DormantSlots.ForSite(Tile, new[] { Yard(Depot) }, Array.Empty<int>(), Heavies, Trailers, TrailerKind, into);
        Assert.Empty(into);
    }

    [Fact]
    public void A_site_with_no_goods_vehicles_in_the_catalogue_still_parks_cars()
    {
        // the yard must not vanish because this build knows no trailers
        var into = new List<VehicleSlot>();
        DormantSlots.ForSite(Tile, new[] { Yard(Depot, w: 60f, d: 68f) },
            Cars, Array.Empty<int>(), Array.Empty<int>(), TrailerKind, into);
        Assert.NotEmpty(into);
        Assert.All(into, s => Assert.Contains(s.KindId, Cars));
    }
}
