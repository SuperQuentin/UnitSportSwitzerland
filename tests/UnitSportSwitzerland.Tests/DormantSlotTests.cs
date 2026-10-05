using UnitSport.Terrain.Format;
using UnitSport.Vehicles;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Where the dormant vehicles stand (#499, the layer #496 phase 3 consumes). The industrial plan's
/// own risk list calls a client/server fleet disagreement "a new consistency surface", so this is
/// pinned: the placement is a pure function of the tile's bytes and must stay one.
/// </summary>
public class DormantSlotTests
{
    private static readonly TileId Tile = new(2600, 1120);
    private static readonly int[] Kinds = { 8, 9, 10, 11 };

    /// <summary>A row of bays, as the planner would write them into PARK.</summary>
    private static List<ParkingBay> Row(int count, ParkingBayFlags flags = ParkingBayFlags.None)
    {
        var bays = new List<ParkingBay>(count);
        for (int i = 0; i < count; i++)
            bays.Add(new ParkingBay(10f + i * 2.5f, 460f, 500f, 1.5708f, ParkingBayKind.Car, flags));
        return bays;
    }

    private static List<VehicleSlot> Slots(List<ParkingBay> bays)
    {
        var into = new List<VehicleSlot>();
        DormantSlots.ForParking(Tile, bays, Kinds, into);
        return into;
    }

    [Fact]
    public void The_same_tile_gives_the_same_fleet_every_time()
    {
        var bays = Row(40);
        var a = Slots(bays);
        var b = Slots(bays);

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void A_slot_is_pinned_to_its_bay_so_a_rebuild_never_moves_a_parked_car()
    {
        // Update these numbers only on purpose: every car in the region moves with them.
        var slots = Slots(Row(40));
        Assert.Equal(38, slots.Count);   // this tile's fill came out high

        var first = slots[0];
        Assert.Equal(1, first.Ordinal);       // bay 0's own roll came out above the tile's fill
        Assert.Equal(Tile.MinE + 12.5, first.E, 3);
        Assert.Equal(Tile.MaxN - 500.0, first.N, 3);
        Assert.Equal(460, first.Height, 3);
        Assert.Equal("2600_1120", first.Owner);
        Assert.Equal("veh_slot_2600_1120_1", first.NodeName);
    }

    [Fact]
    public void A_slot_keeps_its_ordinal_when_a_neighbouring_bay_fills_or_empties()
    {
        // the per-bay roll is hashed off the bay's own position, so changing one bay cannot
        // reshuffle the rest — which would move every car in the lot
        var slots = Slots(Row(40));
        var shorter = Slots(Row(39));

        foreach (var s in shorter)
            Assert.Contains(slots, t => t.Ordinal == s.Ordinal && t.E == s.E && t.KindId == s.KindId);
    }

    [Fact]
    public void A_bay_that_cannot_be_occupied_never_gets_a_car()
    {
        foreach (var flag in (ParkingBayFlags[])[ParkingBayFlags.NearEntrance, ParkingBayFlags.Blocked])
            Assert.Empty(Slots(Row(40, flag)));

        var loading = Row(40).Select(b => b with { Kind = ParkingBayKind.Loading }).ToList();
        Assert.Empty(Slots(loading));
    }

    [Fact]
    public void A_lot_is_neither_empty_nor_bumper_to_bumper()
    {
        // across many tiles the fill stays inside the band, and both busy and quiet lots turn up
        double lowest = 1, highest = 0;
        for (int e = 0; e < 60; e++)
        {
            var into = new List<VehicleSlot>();
            DormantSlots.ForParking(new TileId(2500 + e, 1130), Row(60), Kinds, into);
            double fill = into.Count / 60.0;
            lowest = Math.Min(lowest, fill);
            highest = Math.Max(highest, fill);
        }
        // the per-bay roll scatters either side of the tile's own fill, so allow a little slack
        Assert.InRange(lowest, 0.0, DormantSlots.MinFill + 0.2);
        Assert.InRange(highest, DormantSlots.MaxFill - 0.25, 1.0);
    }

    [Fact]
    public void Every_car_is_an_ordinary_road_car_facing_one_way_out_of_its_bay()
    {
        var slots = Slots(Row(60));
        Assert.NotEmpty(slots);
        foreach (var s in slots)
        {
            Assert.Contains(s.KindId, Kinds);
            Assert.InRange(s.Paint, (byte)0, (byte)7);
            // the bay faces 1.5708; a car in it noses either in or out, never across
            double off = Math.Min(Math.Abs(s.Yaw - 1.5708), Math.Abs(s.Yaw - (1.5708 + Math.PI)));
            Assert.True(off < 1e-3, $"yaw {s.Yaw}");
        }
        // and both directions actually occur
        Assert.Contains(slots, s => s.Yaw < 2);
        Assert.Contains(slots, s => s.Yaw > 2);
    }

    /// <summary>
    /// The node name has to survive an owner that is not a tile. A car park's owner is its tile
    /// (<c>E_N</c>); an industrial yard's is a building (<c>E_N_Index</c>, #496 phase 3). The parser
    /// in `DormantVehicles.SlotOf` first demanded exactly five underscore parts, which silently
    /// dropped every yard vehicle — this pins the shape both providers must produce.
    /// </summary>
    [Theory]
    [InlineData("2600_1120", 4, "veh_slot_2600_1120_4")]        // a car park: owner is a tile
    [InlineData("2600_1120_7", 0, "veh_slot_2600_1120_7_0")]    // a yard: owner is a building
    [InlineData("2600_1120_7", 13, "veh_slot_2600_1120_7_13")]
    public void A_slot_name_carries_an_owner_of_any_length_and_the_ordinal_last(
        string owner, int ordinal, string want)
    {
        var slot = new VehicleSlot(owner, ordinal, 0, 0, 0, 0, 8, 0, false);
        Assert.Equal(want, slot.NodeName);

        // the ordinal is the last part and the owner is everything between the prefix and it,
        // which is what the parser relies on
        var parts = slot.NodeName.Split('_');
        Assert.True(parts.Length >= 5);
        Assert.Equal("veh", parts[0]);
        Assert.Equal("slot", parts[1]);
        Assert.Equal(ordinal, int.Parse(parts[^1]));
        Assert.Equal(owner, string.Join('_', parts[2..^1]));
    }

    [Fact]
    public void A_trailer_slot_carries_which_trailer_it_is_and_how_loaded()
    {
        // RideKind.Trailer alone says "a trailer"; WHICH one lives in the TrailerCatalog code,
        // and the load is physical (a timber trailer's logs, a tanker's slosh) — #70, #496 phase 3
        var lone = new VehicleSlot("2600_1120_7", 0, 0, 0, 0, 0, 120, 0, false, Train: (3 + 1) | (80 << 8));
        Assert.Equal(120, lone.KindId);
        Assert.NotEqual(0, lone.Train);

        // a coupled artic is ONE slot: a tractor kind with a trailer code, never two side by side
        var artic = new VehicleSlot("2600_1120_7", 1, 0, 0, 0, 0, 96, 0, false, Train: 1, Load: 0.8f);
        Assert.NotEqual(120, artic.KindId);
        Assert.NotEqual(0, artic.Train);
        Assert.Equal(0.8f, artic.Load);

        // and a car park's cars carry neither
        Assert.Equal(0, new VehicleSlot("2600_1120", 0, 0, 0, 0, 0, 8, 0, false).Train);
    }

    [Fact]
    public void A_tile_with_no_bays_and_a_catalogue_with_no_cars_both_give_nothing()
    {
        Assert.Empty(Slots(new List<ParkingBay>()));
        var into = new List<VehicleSlot>();
        DormantSlots.ForParking(Tile, Row(40), Array.Empty<int>(), into);
        Assert.Empty(into);
    }
}
