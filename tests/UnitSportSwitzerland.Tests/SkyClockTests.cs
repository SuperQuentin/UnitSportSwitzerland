using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The sky's moment and the anchor across bodies (#1850, src/Core/SkyClock.cs, docs/plans/planets.md sections 2-3):
/// the bodies on their own clock, the Earth turned by the game hour with the ground's sun where it is today, no jump at
/// game midnight; the nearest surface of any body; a crossing that moves nothing.
/// </summary>
public class SkyClockTests
{
    private static readonly DateOnly Day = new(2027, 7, 15);
    private const double Deg = Math.PI / 180;

    private static double SunHourAngle(double earthAngle, DateTime bodiesUtc) =>
        Math.IEEERemainder(earthAngle - Ephemeris.Equatorial(SkyBody.Sun, bodiesUtc).Ra, 2 * Math.PI);

    [Fact]
    public void In_real_time_it_is_the_real_sky()
    {
        var utc = SwissTime.ToUtc(Day, 10.5);
        var m = SkyClock.Moment(utc, Day, 10.5);
        Assert.Equal(utc, m.Utc);
        Assert.Equal(SkyMoment.At(utc).EarthAngle, m.EarthAngle, 12);
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(12.0)]
    [InlineData(21.5)]
    public void The_ground_keeps_its_sun_whatever_the_bodies_time(double hour)
    {
        // the bodies three days and a bit on (a server that has run), the game hour as the ground's sky reads it
        var bodies = SwissTime.ToUtc(Day, 3.0).AddDays(3.2);
        var ground = SwissTime.ToUtc(Day, hour);
        var m = SkyClock.Moment(bodies, Day, hour);
        double today = SunHourAngle(Ephemeris.SiderealTime(ground), ground);
        Assert.Equal(today, SunHourAngle(m.EarthAngle, bodies), 9);
    }

    [Fact]
    public void Game_midnight_turns_the_earth_by_no_more_than_a_tenth_of_a_degree()
    {
        // the game hour wraps 24 -> 0 on the same calendar date; the bodies go on
        foreach (var month in new[] { 1, 4, 7, 11 })
        {
            var day = new DateOnly(2027, month, 15);
            var bodies = SwissTime.ToUtc(day, 23.0);
            double before = SkyClock.Moment(bodies, day, 24.0).EarthAngle;
            double after = SkyClock.Moment(bodies, day, 0.0).EarthAngle;
            double jump = Math.Abs(Math.IEEERemainder(after - before, 2 * Math.PI)) / Deg;
            Assert.True(jump < 0.15, $"{jump:F3}° in month {month}");
            // the old rule (the sidereal angle of the date and hour) jumped a sidereal day's 0.99°
            double old = Math.Abs(Math.IEEERemainder(Ephemeris.SiderealTime(SwissTime.ToUtc(day, 0.0)) - Ephemeris.SiderealTime(SwissTime.ToUtc(day, 24.0)), 2 * Math.PI)) / Deg;
            Assert.InRange(old, 0.95, 1.02);
        }
    }

    [Fact]
    public void A_pinned_date_runs_from_its_start_hour_on_sim_time_and_play_on_the_servers_clock()
    {
        Assert.Equal(SwissTime.ToUtc(Day, 10).AddSeconds(3600), SkyClock.BodiesUtc(Day, 10, 3600, 0));
        Assert.Equal(new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc),
            SkyClock.BodiesUtc(null, 10, 3600, (new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds));
    }

    [Fact]
    public void The_nearest_surface_is_the_earths_in_orbit_and_the_moons_near_it()
    {
        var m = SkyMoment.At(SwissTime.ToUtc(Day, 10));
        DVec3 leo = Ellipsoid.Wgs84.ToEcef(new Geodetic(46.5, 6.6, 400_000));
        var (body, d) = UnitSport.Core.Frames.NearestSurface(BodyId.Earth, leo, m);
        Assert.Equal(BodyId.Earth, body);
        Assert.Equal(400_000, d, 0);
        var overMoon = UnitSport.Core.Frames.Convert(BodyId.Moon, new DVec3(1_837_400, 0, 0), BodyId.Earth, m);
        (body, d) = UnitSport.Core.Frames.NearestSurface(BodyId.Earth, overMoon, m);
        Assert.Equal(BodyId.Moon, body);
        Assert.Equal(100_000, d, 0);
        // and from the Moon, the Earth is the other body round it
        var nearEarth = UnitSport.Core.Frames.Convert(BodyId.Earth, leo, BodyId.Moon, m);
        (body, d) = UnitSport.Core.Frames.NearestSurface(BodyId.Moon, nearEarth, m);
        Assert.Equal(BodyId.Earth, body);
        // from another body, the distance to the centre less the equatorial radius: Bern is ~11 km below it
        Assert.InRange(d, 380_000, 410_000);
    }

    [Fact]
    public void A_crossing_rebases_the_anchor_and_moves_nothing()
    {
        var m = SkyMoment.At(SwissTime.ToUtc(Day, 10));
        var keep = SkyClock.Live;
        SkyClock.Live = () => m;
        try
        {
            // an anchor 60,000 km from the Moon on the Earth's side, counted from the Earth
            var nearMoon = UnitSport.Core.Frames.Convert(BodyId.Moon, new DVec3(60_000_000, 0, 0), BodyId.Earth, m).AsEcef;
            var origin = new WorldOrigin(FrameAxes.Enu(nearMoon));
            var before = origin.Frame;
            // a place on the Earth, seen from there, before
            var home = GlobalPos.FromLv95(2_600_000, 1_200_000, 550);
            var homeBefore = before.ToWorld(home);
            var probe = new Vector3(1200, -300, 4500);
            var probePlace = before.ToGlobal(probe);

            origin.Rebase(BodyId.Moon, m);
            var after = origin.Frame;
            Assert.Equal(BodyId.Moon, origin.Body);
            Assert.Equal(before.Epoch + 1, after.Epoch);

            // nothing moved: a frame taken before maps to this one as the identity
            var shift = after.Since(before);
            Assert.True(shift.Transform.Origin.Length() < 0.01, $"{shift.Transform.Origin.Length()} m");
            Assert.True((shift.Transform.Basis.X - Vector3.Right).Length() < 1e-6);
            // a world point is the same place, now counted from the Moon
            var probeAfter = after.ToGlobal(probe);
            Assert.Equal(BodyId.Moon, probeAfter.Body);
            Assert.True(probeAfter.In(BodyId.Earth, m).DistanceTo(probePlace) < 1e-3);
            // and the Earth's place is drawn where it was (384,000 km away: a float's step there is ~30 m)
            Assert.True((after.ToWorld(home) - homeBefore).Length() < 64, $"{(after.ToWorld(home) - homeBefore).Length()} m");
            Assert.True(double.IsNaN(after.E));
        }
        finally
        {
            SkyClock.Live = keep;
        }
    }
}
