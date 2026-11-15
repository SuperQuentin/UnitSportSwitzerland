using UnitSport.Core;
using UnitSport.Player;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The spectator's target list and its readouts (#1850, src/Player/TargetList.cs).</summary>
public class TargetListTests
{
    [Fact]
    public void A_tap_steps_through_the_bodies_but_the_one_you_are_on_and_wraps_through_none()
    {
        Assert.Equal(BodyId.Moon, TargetList.Next(null, BodyId.Earth));
        Assert.Equal(BodyId.Mars, TargetList.Next(BodyId.Moon, BodyId.Earth));
        // on the Moon, home comes first and the Moon is skipped
        Assert.Equal(BodyId.Earth, TargetList.Next(null, BodyId.Moon));
        Assert.Equal(BodyId.Mars, TargetList.Next(BodyId.Earth, BodyId.Moon));
        Assert.Null(TargetList.Next(BodyId.Neptune, BodyId.Earth));
        int n = 0;
        BodyId? t = null;
        do { t = TargetList.Next(t, BodyId.Earth); n++; } while (t != null && n < 50);
        Assert.Equal(TargetList.Order.Length, n);
    }

    [Fact]
    public void The_trip_takes_what_the_flight_checks_measured()
    {
        // --mooncheck: 400 km over the Earth to 100 km over the Moon in 27.5 s; --mooncheck mars: 860 s
        Assert.InRange(TargetList.TripSeconds(384_400_000 - 6_371_000 - 1_737_400), 20, 35);
        Assert.InRange(TargetList.TripSeconds(249.46e9 - 6_371_000 - 3_396_000), 800, 920);
        Assert.Equal(0, TargetList.TripSeconds(300_000));
    }

    [Fact]
    public void Distances_and_times_read_in_the_units_of_their_size()
    {
        Assert.Equal("850 m", TargetList.Distance(850));
        Assert.Equal("12.5 km", TargetList.Distance(12_500));
        Assert.Equal("376\u2009400 km", TargetList.Distance(376_400_000));
        Assert.Equal("4.2 million km", TargetList.Distance(4.2e9));
        Assert.Equal("1.67 AU", TargetList.Distance(1.67 * TargetList.Au));
        Assert.Equal("28 s", TargetList.Duration(28));
        Assert.Equal("14 min", TargetList.Duration(860));
        Assert.Equal("5.0 h", TargetList.Duration(18_000));
        Assert.Equal("\u2014", TargetList.Time(1000, 0));
    }
}
