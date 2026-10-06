using Godot;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;
using Xunit;
using Sites = UnitSportSwitzerland.Tests.ConstructionSiteTests;

namespace UnitSport.Tests;

/// <summary>
/// The machines parked on a building site (#616) are dormant vehicles like a car park's: every peer
/// works out the same ones from the same tile and wakes the same one by name, so the slots are a
/// pure function of the site plan, named by the plan's own ordinals.
/// </summary>
public class DormantConstructionTests
{
    private static readonly TileId Tile = new(2600, 1120);
    private static readonly int[] Cars = { 8, 9, 10, 11 };
    private const int Excavator = 194, Loader = 195;

    private static int? KindOf(MachineRole role, ulong roll) => role switch
    {
        MachineRole.Excavator => Excavator,
        MachineRole.WheelLoader => Loader,
        _ => null,
    };

    /// <summary>A big site digging its foundations: 1.5 m of a five-storey block measured, the street to the south.</summary>
    private static ConstructionSite Digging(float cx = 250, float cz = 300) =>
        Sites.Plan($"{Tile.E}_{Tile.N}_3", Sites.Box(cx, cz, 34, 22, 1.5f, 5), new Vector2(cx, cz + 40));

    private static List<VehicleSlot> Slots(params ConstructionSite[] sites)
    {
        var into = new List<VehicleSlot>();
        DormantSlots.ForConstruction(Tile, sites, KindOf, Cars, into);
        return into;
    }

    [Fact]
    public void The_same_site_always_parks_the_same_machines()
    {
        var first = Slots(Digging());
        Assert.NotEmpty(first);
        for (int i = 0; i < 20; i++) Assert.Equal(first, Slots(Digging()));
    }

    [Fact]
    public void A_site_digging_its_foundations_parks_an_excavator_and_a_loader()
    {
        var site = Digging();
        Assert.Equal(SitePhase.Foundations, site.Phase);
        var kinds = Slots(site).Select(s => s.KindId).ToList();
        Assert.Contains(Excavator, kinds);
        Assert.Contains(Loader, kinds);
    }

    [Fact]
    public void A_slot_is_its_machine_where_the_plan_put_it()
    {
        var site = Digging() with
        {
            Machines = new[]
            {
                new MachineSlot(MachineRole.Excavator, 0, new Vector2(120, 340), 1.25f),
                // nothing drives as a tipper yet: its place stays empty, and the ordinals after it hold
                new MachineSlot(MachineRole.Tipper, 2, new Vector2(140, 340), 0f),
                new MachineSlot(MachineRole.Van, 4, new Vector2(160, 350), -0.5f),
            },
        };
        var slots = Slots(site);
        Assert.Equal(2, slots.Count);

        var dig = slots[0];
        Assert.Equal(site.Key, dig.Owner);
        Assert.Equal(0, dig.Ordinal);
        Assert.Equal(Excavator, dig.KindId);
        Assert.Equal(Tile.MinE + 120, dig.E, 3);
        Assert.Equal(Tile.MaxN - 340, dig.N, 3);
        Assert.Equal(1.25f, dig.Yaw, 4);
        Assert.False(dig.Van);
        Assert.Equal($"veh_slot_{site.Key}_0", dig.NodeName);

        // the crew's van is a car of the lot's, flagged as a van, at a heading in [0, 2π)
        var van = slots[1];
        Assert.Equal(4, van.Ordinal);
        Assert.Contains(van.KindId, Cars);
        Assert.True(van.Van);
        Assert.InRange(van.Yaw, 0f, MathF.Tau);
        Assert.Equal(-0.5f + MathF.Tau, van.Yaw, 4);
    }

    [Fact]
    public void Two_sites_on_one_tile_name_their_machines_apart()
    {
        var a = Digging();
        var b = Digging(650, 700) with { Key = $"{Tile.E}_{Tile.N}_9" };
        var names = Slots(a, b).Select(s => s.NodeName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains(names, n => n.Contains("_3_"));
        Assert.Contains(names, n => n.Contains("_9_"));
    }

    [Fact]
    public void Without_cars_a_van_is_left_out_and_nothing_else_changes()
    {
        var site = Digging();
        var with = Slots(site).Where(s => !s.Van).ToList();
        var into = new List<VehicleSlot>();
        DormantSlots.ForConstruction(Tile, new[] { site }, KindOf, Array.Empty<int>(), into);
        Assert.Equal(with, into);
    }
}
