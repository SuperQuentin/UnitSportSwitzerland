using UnitSport.Items;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// What an industrial site keeps out on its apron (#583 phase 3): a row of pallets along the
/// facade and, at a warehouse or a works, a forklift beside it. Pinned for the reason the fleet is
/// (<see cref="SiteYardTests"/>): none of it is ever sent, so every peer has to derive the same
/// stacks, under the same names, from the tile's bytes — and the server checks a forklift's
/// request against exactly this.
/// </summary>
public class SitePalletTests
{
    private static readonly TileId Tile = new(2600, 1120);
    private const int Warehouse = 3, Factory = 4, Depot = 5, Mechanic = 6, Dealership = 7;
    private const float Apron = 7f;
    private const int ForkliftKind = 193;

    /// <summary>A yard whose facade runs from <paramref name="from"/> to <paramref name="to"/> along it, turned 31°.</summary>
    private static SiteFront Front(int type, float from = -40f, float to = 40f, params DoorSpan[] doors) =>
        new(new SiteYard($"{Tile.E}_{Tile.N}_7", type, 300f, 470f, 300f, 0.54f, to - from + 6f, 26f), from, to, doors);

    private static List<YardPallet> Of(SiteFront front)
    {
        var into = new List<YardPallet>();
        SitePallets.ForSite(Tile, front, Apron, into);
        return into;
    }

    /// <summary>A point back in the yard's own frame: u along the facade, v out of the yard's middle.</summary>
    private static (float U, float V) Local(SiteYard y, double e, double n)
    {
        float dx = (float)(e - Tile.MinE) - y.X, dz = (float)(Tile.MaxN - n) - y.Z;
        float c = MathF.Cos(y.Heading), s = MathF.Sin(y.Heading);
        return (dx * c - dz * s, -dx * s - dz * c);
    }

    [Fact]
    public void The_same_front_gives_the_same_stacks_every_time()
    {
        var a = Of(Front(Warehouse));
        Assert.NotEmpty(a);
        Assert.Equal(a, Of(Front(Warehouse)));
    }

    [Fact]
    public void A_warehouse_stocks_its_apron_and_a_forecourt_or_a_body_shop_does_not()
    {
        int warehouse = Of(Front(Warehouse)).Count, depot = Of(Front(Depot)).Count;
        Assert.True(warehouse > depot && depot > 0, $"warehouse {warehouse}, depot {depot}");
        Assert.Empty(Of(Front(Dealership)));
        Assert.Empty(Of(Front(Mechanic)));
    }

    [Fact]
    public void Every_pallet_stands_along_the_facade_runners_along_it()
    {
        var front = Front(Warehouse, -12f, 31f);
        foreach (var p in Of(front))
        {
            var (u, v) = Local(front.Yard, p.E, p.N);
            // the facade is the yard's near edge less the apron
            Assert.Equal(-(front.Yard.Depth / 2 + Apron) + SitePallets.Out, v, 2);
            Assert.InRange(u, front.From, front.To);
            Assert.Equal(front.Yard.Heading, p.Yaw, 4);
            Assert.Equal(Pallets.YardId(front.Yard.Owner, p.Slot), p.Id);
        }
    }

    [Fact]
    public void No_pallet_stands_in_front_of_a_door()
    {
        var doors = new[] { new DoorSpan(-20f, 2f), new DoorSpan(-10f, 2f), new DoorSpan(0f, 1.5f), new DoorSpan(15f, 2f) };
        var front = Front(Warehouse, -40f, 40f, doors);
        var stacks = Of(front);
        Assert.NotEmpty(stacks);
        foreach (var p in stacks)
        {
            var (u, _) = Local(front.Yard, p.E, p.N);
            foreach (var d in doors)
                Assert.True(MathF.Abs(u - d.Along) >= d.Half + SitePallets.DoorClear - 0.01f, $"a pallet at {u:F1} m, a door at {d.Along}");
        }
    }

    [Fact]
    public void A_door_leaves_a_gap_and_renumbers_nothing()
    {
        var bare = Of(Front(Warehouse)).ToDictionary(p => p.Slot);
        var withDoor = Of(Front(Warehouse, -40f, 40f, new DoorSpan(0f, 2f)));
        Assert.True(withDoor.Count < bare.Count);
        foreach (var p in withDoor)
            Assert.Equal(bare[p.Slot], p);
    }

    [Fact]
    public void A_works_keeps_its_stock_at_one_end()
    {
        var front = Front(Factory);
        var us = Of(front).Select(p => (Local(front.Yard, p.E, p.N).U - front.From) / (front.To - front.From)).ToList();
        Assert.NotEmpty(us);
        Assert.True(us.All(t => t <= SitePallets.WorksEnd + 1e-3f) || us.All(t => t >= 1 - SitePallets.WorksEnd - 1e-3f),
            string.Join(", ", us.Select(t => t.ToString("F2"))));
    }

    [Fact]
    public void A_facade_too_short_for_one_pallet_has_none()
    {
        Assert.Empty(Of(Front(Warehouse, -0.7f, 0.7f)));
    }

    [Fact]
    public void A_warehouse_and_a_works_keep_a_forklift_beside_the_facade()
    {
        foreach (int type in new[] { Warehouse, Factory })
        {
            var front = Front(type, -12f, 31f);
            var lift = DormantSlots.ForkliftOf(Tile, front, ForkliftKind);
            Assert.NotNull(lift);
            var s = lift!.Value;
            Assert.Equal(ForkliftKind, s.KindId);
            Assert.Equal(DormantSlots.ForkliftOrdinal, s.Ordinal);
            Assert.Equal(front.Yard.Owner, s.Owner);
            var (u, v) = Local(front.Yard, s.E, s.N);
            // past one end of the facade, so in front of no door and of no pallet
            Assert.True(Math.Abs(u - front.To - 1.5f) < 0.01f || Math.Abs(u - front.From + 1.5f) < 0.01f, $"u {u:F2}");
            // on the apron: between the facade and the fleet's first row
            Assert.InRange(v, -(front.Yard.Depth / 2 + Apron), -front.Yard.Depth / 2);
            // forks to the building
            float facing = s.Yaw - front.Yard.Heading;
            Assert.Equal(MathF.PI, MathF.Abs(MathF.IEEERemainder(facing, MathF.Tau)), 3);
        }
        foreach (int type in new[] { Depot, Mechanic, Dealership })
            Assert.Null(DormantSlots.ForkliftOf(Tile, Front(type), ForkliftKind));
    }
}
