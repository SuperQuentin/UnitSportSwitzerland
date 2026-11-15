using System;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The local day (#1992, src/Core/SkyClock.cs, src/Core/Frames.cs, docs/plans/planets.md section 2): standing on another
/// body, it takes the Earth's extra turn, so its sky crosses about once a game day; a moon's parent stays still in its sky;
/// at real time nothing changes; the turn fades out before the sphere of influence's edge.
/// </summary>
public class LocalDayTests
{
    private static readonly DateOnly Day = new(2027, 7, 15);
    private const double Deg = Math.PI / 180;

    /// <summary>A moment whose game Earth is <paramref name="ahead"/> radians past the true one, the local day on <paramref name="body"/>.</summary>
    private static SkyMoment Ahead(BodyId body, double ahead, double weight = 1)
    {
        var utc = SwissTime.ToUtc(Day, 10);
        var m = new SkyMoment(utc, Ephemeris.SiderealTime(utc) + ahead);
        return SkyClock.WithLocal(m, new SkyClock.LocalDay(body, weight));
    }

    /// <summary>Where <paramref name="target"/>'s centre is in <paramref name="body"/>'s fixed axes.</summary>
    private static DVec3 Seen(BodyId body, BodyId target, SkyMoment m) => Frames.Convert(target, DVec3.Zero, body, m);

    [Fact]
    public void At_real_time_there_is_no_extra_turn()
    {
        var utc = SwissTime.ToUtc(Day, 10);
        var m = SkyClock.WithLocal(SkyMoment.At(utc), new SkyClock.LocalDay(BodyId.Moon, 1));
        Assert.Equal(0, m.LocalTurn, 9);
        // the Sun where the true frame tree has it, to a metre at 1 AU
        Assert.True((Seen(BodyId.Moon, BodyId.Sun, m) - Seen(BodyId.Moon, BodyId.Sun, SkyMoment.At(utc))).Length < 1);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(-2.9)]
    public void The_earth_stays_still_in_the_moons_sky_and_the_sun_moves(double ahead)
    {
        var still = Ahead(BodyId.Moon, 0);
        var turned = Ahead(BodyId.Moon, ahead);
        Assert.Equal(ahead, turned.LocalTurn, 9);
        // the Earth: the same place in the Moon's axes (tidal lock kept), to a metre
        var e0 = Seen(BodyId.Moon, BodyId.Earth, still);
        var e1 = Seen(BodyId.Moon, BodyId.Earth, turned);
        Assert.True((e1 - e0).Length < 1, $"the Earth moved {(e1 - e0).Length:F1} m");
        // the Sun: turned about the Moon's pole (its Z) by the extra turn, westward, its height above the equator kept
        var s0 = Seen(BodyId.Moon, BodyId.Sun, still);
        var s1 = Seen(BodyId.Moon, BodyId.Sun, turned);
        double lon0 = Math.Atan2(s0.Y, s0.X), lon1 = Math.Atan2(s1.Y, s1.X);
        // the subsolar point goes west as the body turns east; the Earth's parallax off the Moon's spin axis is ~0.15°
        Assert.Equal(-ahead, Math.IEEERemainder(lon1 - lon0, 2 * Math.PI), 2);
        Assert.Equal(s0.Z / s0.Length, s1.Z / s1.Length, 3);
    }

    [Fact]
    public void A_planet_turns_but_keeps_its_orbit()
    {
        var still = Ahead(BodyId.Mars, 0);
        var turned = Ahead(BodyId.Mars, 1.0);
        // Mars' centre from the Sun is where it is: only its axes turn
        Assert.True((Frames.Offset(BodyId.Sun, BodyId.Mars, turned) - Frames.Offset(BodyId.Sun, BodyId.Mars, still)).Length < 1);
        var s0 = Seen(BodyId.Mars, BodyId.Sun, still);
        var s1 = Seen(BodyId.Mars, BodyId.Sun, turned);
        Assert.Equal(-1.0, Math.IEEERemainder(Math.Atan2(s1.Y, s1.X) - Math.Atan2(s0.Y, s0.X), 2 * Math.PI), 6);
    }

    [Fact]
    public void A_retrograde_body_turns_backward()
    {
        var s0 = Seen(BodyId.Venus, BodyId.Sun, Ahead(BodyId.Venus, 0));
        var s1 = Seen(BodyId.Venus, BodyId.Sun, Ahead(BodyId.Venus, 1.0));
        Assert.Equal(1.0, Math.IEEERemainder(Math.Atan2(s1.Y, s1.X) - Math.Atan2(s0.Y, s0.X), 2 * Math.PI), 6);
    }

    [Fact]
    public void Only_the_local_body_turns()
    {
        // on the Moon, Mars' own axes are its true ones; the Earth is never given the local turn (its day is its own angle)
        var m = Ahead(BodyId.Moon, 1.0);
        Assert.Equal(Frames.Orientation(BodyId.Mars, Ahead(BodyId.Moon, 0)), Frames.Orientation(BodyId.Mars, m));
        var earth = Ahead(BodyId.Earth, 1.0);
        Assert.Equal(0, earth.LocalTurn);
        Assert.Equal(0, Ahead(BodyId.Sun, 1.0).LocalTurn);
    }

    [Fact]
    public void The_turn_fades_out_before_the_sphere_of_influence_ends()
    {
        double soi = CelestialBody.Of(BodyId.Moon).SphereOfInfluence;
        Assert.Equal(1, SkyClock.LocalWeight(BodyId.Moon, 1_740_000));
        Assert.Equal(1, SkyClock.LocalWeight(BodyId.Moon, SkyClock.LocalFadeIn * soi));
        Assert.InRange(SkyClock.LocalWeight(BodyId.Moon, 0.4 * soi), 0.01, 0.99);
        Assert.Equal(0, SkyClock.LocalWeight(BodyId.Moon, SkyClock.LocalFadeOut * soi));
        Assert.Equal(0, SkyClock.LocalWeight(BodyId.Moon, soi));
        Assert.Equal(0, SkyClock.LocalWeight(BodyId.Earth, 7_000_000));
        // at the sphere's edge the frame tree is the true one: a crossing re-expresses nothing differently
        var edge = Ahead(BodyId.Moon, 2.0, SkyClock.LocalWeight(BodyId.Moon, soi));
        Assert.Equal(0, edge.LocalTurn);
    }

    [Fact]
    public void At_the_normal_day_the_moons_sun_crosses_in_about_24_minutes()
    {
        // the extra turn's rate at the 24-minute day: the Earth turned once a game day (1,440 real s) by the sun, less
        // its true sidereal rate; the Moon's own synodic rate adds on
        double game = 2 * Math.PI / 1440, sidereal = 2 * Math.PI / 86164.1, synodic = 2 * Math.PI / (29.530589 * 86400);
        double moonDay = 2 * Math.PI / (game - sidereal + synodic) / 60;
        Assert.InRange(moonDay, 24.0, 24.8);
    }
}
