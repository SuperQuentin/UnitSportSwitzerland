using UnitSport.Terrain.Format;
using UnitSport.Vehicles;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The marina boats as dormant slots (#554): <see cref="DormantSlots.ForMarina"/> turns a tile's
/// jetties into the same moored boats #383's <c>MarinaBoats</c> placed, named so every peer agrees.
/// </summary>
public class MarinaSlotTests
{
    private const float Level = 372.1f, Bed = 368f;
    private const int Speedboat = 121, Jetski = 122;

    // a 120 m jetty running north from the middle of tile 2500/1118, so it has its full four boats
    private static readonly TileId Tile = new(2500, 1118);

    private static Jetty Long(string id, double e = 2500500, double n = 1118400) => new()
    {
        Id = id,
        Ribbon = new PierRibbon { Kind = PierKind.Jetty, Width = 2.2,
            Points = { new double[] { e, n, Level + 0.4 }, new double[] { e, n + 120, Level + 0.4 } } },
    };

    private static List<VehicleSlot> Slots(IReadOnlyList<Jetty> jetties, Func<double, double, (float, float)?>? water = null)
    {
        var list = new List<VehicleSlot>();
        DormantSlots.ForMarina(Tile, jetties, water ?? ((_, _) => (Level, Bed)), Speedboat, Jetski, list);
        return list;
    }

    [Fact]
    public void Every_chosen_berth_with_water_is_a_boat_at_its_keel()
    {
        var jetty = Long("{J1}");
        var berths = jetty.BoatBerths();
        var slots = Slots(new[] { jetty });

        Assert.Equal(berths.Count, slots.Count);
        Assert.True(slots.Count > 0);
        for (int k = 0; k < slots.Count; k++)
        {
            var (b, s) = (berths[k], slots[k]);
            Assert.Equal(k, s.Ordinal);
            Assert.Equal(b.E, s.E, 6);
            Assert.Equal(b.N, s.N, 6);
            Assert.Equal(b.Speedboat ? Speedboat : Jetski, s.KindId);
            // floating: the keel under the still level by the boat's own draught
            Assert.Equal(Level - (b.Speedboat ? DormantSlots.SpeedboatDraught : DormantSlots.JetskiDraught), s.Height, 4);
            // the yaw MarinaBoats placed them with, -heading, kept in 0..2pi
            double yaw = -b.Heading * Math.PI / 180;
            Assert.Equal(0, Math.IEEERemainder(s.Yaw - yaw, 2 * Math.PI), 4);
            Assert.InRange(s.Yaw, 0, 2 * Math.PI);
            Assert.True(s.Respawns, "a harbour is restocked");
        }
    }

    [Fact]
    public void The_owner_is_the_tile_and_the_jetty_so_two_jetties_never_share_a_name()
    {
        var a = Slots(new[] { Long("{A}", 2500300) });
        var b = Slots(new[] { Long("{B}", 2500700) });
        Assert.All(a.Concat(b), s => Assert.StartsWith("2500_1118_m", s.Owner));
        // the node name parses back: the tile is the owner's first two parts, the ordinal the last
        Assert.All(a, s => Assert.Matches(@"^veh_slot_2500_1118_m[0-9a-f]{8}_\d+$", s.NodeName));
        Assert.Empty(a.Select(s => s.NodeName).Intersect(b.Select(s => s.NodeName)));
    }

    [Fact]
    public void A_shallow_berth_is_dropped_and_the_others_keep_their_names()
    {
        var jetty = Long("{J2}");
        var all = Slots(new[] { jetty });
        Assert.True(all.Count >= 2);
        var dry = all[0];
        // 0.5 m of water under the first berth: not enough to float a boat
        var some = Slots(new[] { jetty }, (e, n) => Math.Abs(e - dry.E) < 0.01 && Math.Abs(n - dry.N) < 0.01
            ? (Level, Level - 0.5f) : (Level, Bed));
        Assert.Equal(all.Count - 1, some.Count);
        Assert.Equal(all.Skip(1).Select(s => s.NodeName), some.Select(s => s.NodeName));
        // and unknown water (no layer there) is no boat either
        Assert.Empty(Slots(new[] { jetty }, (_, _) => null));
    }

    [Fact]
    public void A_jetty_belongs_to_the_tile_its_middle_is_in()
    {
        // its middle north of the tile, so it is the neighbour's
        Assert.Empty(Slots(new[] { Long("{J3}", 2500500, 1118950) }));
        Assert.NotEmpty(Slots(new[] { Long("{J3}") }));
    }

    [Fact]
    public void The_same_jetty_gives_the_same_fleet_every_time()
    {
        var jetty = Long("{J4}");
        Assert.Equal(Slots(new[] { jetty }), Slots(new[] { jetty }));
    }
}
