using UnitSport.Interiors;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// An elevator's ride (#557): doors shut on the floor it leaves, every door shut while it travels,
/// the doors open on the floor it reaches, and nothing moves for a cabin standing still.
/// </summary>
public class LiftRideTests
{
    [Fact]
    public void AnIdleCabinHasItsOwnDoorsOpenAndNoOthers()
    {
        var idle = LiftRide.Idle(2);
        Assert.Equal(1f, idle.Doors(100, 2));
        Assert.Equal(0f, idle.Doors(100, 1));
        Assert.False(idle.Busy(100));
        Assert.Equal(2, idle.At(100));
    }

    [Fact]
    public void ARideClosesTravelsAndOpens()
    {
        var ride = new LiftRide(1, 4, 10);
        // the doors it leaves close
        Assert.Equal(1f, ride.Doors(10, 1));
        Assert.InRange(ride.Doors(10 + LiftRide.Close / 2, 1), 0.4f, 0.6f);
        // then everything is shut while it travels, and it is still at the floor it left
        double mid = (ride.Start + LiftRide.Close + ride.Arrive) / 2;
        Assert.True(ride.Travelling(mid));
        for (int f = 0; f < 6; f++) Assert.Equal(0f, ride.Doors(mid, f));
        Assert.Equal(1, ride.At(mid));
        // it arrives, its new floor's doors open, and it is free again once they have
        Assert.Equal(4, ride.At(ride.Arrive));
        Assert.Equal(0f, ride.Doors(ride.Arrive, 4));
        Assert.Equal(1f, ride.Doors(ride.Done, 4));
        Assert.True(ride.Busy(ride.Arrive));
        Assert.False(ride.Busy(ride.Done));
    }

    [Fact]
    public void AFlatDoorsLockOpensToItsOwnNumbersOnly()
    {
        var combo = InnerDoorLock.Combination("2584_1114_15", 6);
        Assert.Equal(InnerDoorLock.Tumblers, combo.Length);
        Assert.All(combo, v => Assert.InRange(v, 0, 99));
        // the same numbers on every peer, and another door's are not these
        Assert.Equal(combo, InnerDoorLock.Combination("2584_1114_15", 6));
        Assert.NotEqual(combo, InnerDoorLock.Combination("2584_1114_15", 7));
        Assert.True(InnerDoorLock.Opens("2584_1114_15", 6, combo));
        Assert.False(InnerDoorLock.Opens("2584_1114_15", 6, combo.Select(v => (v + 50) % 100).ToArray()));
        Assert.False(InnerDoorLock.Opens("2584_1114_15", 6, combo.Take(2).ToArray()));
    }

    [Fact]
    public void AFartherRideTakesLonger()
    {
        Assert.True(new LiftRide(0, 8, 0).Arrive > new LiftRide(0, 1, 0).Arrive);
        Assert.Equal(new LiftRide(5, 2, 0).Travel, new LiftRide(2, 5, 0).Travel);
    }
}
