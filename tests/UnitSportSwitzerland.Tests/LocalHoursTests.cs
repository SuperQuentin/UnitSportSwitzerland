using System;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// <c>/time</c> where the player stands (#1801, src/World/LocalHours.cs): the named hours off the real
/// sun at the place, the zone a player is in, and Switzerland exactly as before.
/// </summary>
public class LocalHoursTests
{
    private static readonly DateOnly June = new(2026, 6, 21), December = new(2026, 12, 21), March = new(2026, 3, 20);

    private static GlobalPos At(double lat, double lon) => GlobalPos.FromEcef(Ellipsoid.Wgs84.ToEcef(new Geodetic(lat, lon, 500)));

    private static float SunElevation(DateOnly date, double swissHour, GlobalPos at) => SunPath.ElevationAt(date, swissHour, at);

    private static double Named(string name, GlobalPos at, DateOnly date, bool realSun = true)
    {
        Assert.True(TimeCommand.TryParse(["set", name], out TimeRequest request, out string perr), perr);
        Assert.True(TimeCommand.Resolve(request, at, date, realSun, out double hour, out _, out string err), err);
        Assert.True(double.IsFinite(hour));
        return hour;
    }

    /// <summary>The hours <c>/time set</c> gave before #1801, under the real sun: Bern's rise and set, the rest fixed.</summary>
    private static double Before(string name, DateOnly date) => name switch
    {
        "sunrise" => SunPath.RiseOrSet(date, rise: true),
        "sunset" => SunPath.RiseOrSet(date, rise: false),
        "day" => 8,
        "noon" => 12,
        "night" => 21,
        _ => 0,
    };

    [Theory]
    [InlineData("sunrise")]
    [InlineData("day")]
    [InlineData("noon")]
    [InlineData("sunset")]
    [InlineData("night")]
    [InlineData("midnight")]
    public void Geneva_keeps_every_named_hour(string name)
    {
        var geneva = At(46.2044, 6.1432);
        foreach (var date in new[] { June, December, March })
        {
            Assert.Equal(Before(name, date), Named(name, geneva, date), 9);
            // the fixed day too, as the checks run it
            Assert.True(TimeCommand.TryParseHour(name, out double fixedHour));
            Assert.Equal(fixedHour, Named(name, geneva, date, realSun: false), 9);
        }
    }

    [Fact]
    public void Switzerland_reads_Swiss_time()
    {
        foreach (var (lat, lon) in new[] { (46.2044, 6.1432), (47.3769, 8.5417), (46.6, 10.43), (45.83, 9.03), (47.56, 7.59) })
        {
            var at = At(lat, lon);
            Assert.True(LocalHours.IsHome(at));
            Assert.True(LocalHours.ZoneAt(at, realSun: true).IsSwiss);
            Assert.True(TimeCommand.TryParse(["set", "21:30"], out TimeRequest request, out _));
            Assert.True(TimeCommand.Resolve(request, at, June, true, out double hour, out var zone, out _));
            Assert.Equal(21.5, hour, 9);
            Assert.True(zone.IsSwiss);
        }
        // the console has no place: home
        Assert.True(TimeCommand.TryParse(["set", "noon"], out TimeRequest noon, out _));
        Assert.True(TimeCommand.Resolve(noon, null, June, true, out double h, out _, out _));
        Assert.Equal(12, h, 9);
    }

    [Fact]
    public void A_place_120_degrees_east_has_its_noon_8_hours_earlier()
    {
        var far = At(LocalHours.BernLat, LocalHours.BernLon + 120);
        Assert.False(LocalHours.IsHome(far));
        foreach (var date in new[] { June, December })
        {
            double bern = LocalHours.BernNoon(date), noon = Named("noon", far, date);
            Assert.Equal(-8.0, TimeCommand.ShortWay(noon, bern), 3);
            // the sun is highest there then: a quarter of an hour either side it is lower
            float el = SunElevation(date, noon, far);
            Assert.True(el > SunElevation(date, noon - 0.25, far) && el > SunElevation(date, noon + 0.25, far));
            // and as high as Bern's at its own noon (same latitude)
            Assert.Equal(SunElevation(date, bern, LocalHours.Bern), el, 0);
            Assert.Equal(-8.0, TimeCommand.ShortWay(Named("midnight", far, date), TimeCommand.Wrap(bern + 12)), 3);
        }
        // its zone is UTC+8: 12:00 there is 04:00 UTC, 05:00 in a Swiss winter, 06:00 in a Swiss summer
        Assert.Equal(8, LocalHours.ZoneAt(far, realSun: true).UtcOffset);
        Assert.True(TimeCommand.TryParse(["set", "12:00"], out TimeRequest twelve, out _));
        Assert.True(TimeCommand.Resolve(twelve, far, December, true, out double winter, out _, out _));
        Assert.True(TimeCommand.Resolve(twelve, far, June, true, out double summer, out var zone, out _));
        Assert.Equal(5, winter, 9);
        Assert.Equal(6, summer, 9);
        Assert.Equal("12:00 UTC+8 (06:00 Swiss time)", TimeCommand.Describe(summer, zone, June));
        // under the fixed sun the sky is Switzerland's everywhere, so is the clock
        Assert.True(LocalHours.ZoneAt(far, realSun: false).IsSwiss);
    }

    [Theory]
    [InlineData(-4679383, 7259183)] // where #1801 was reported
    [InlineData(0, 0)]
    public void Far_named_hours_light_the_sky_as_they_say(double e, double n)
    {
        var at = GlobalPos.FromLv95(e, n, 500);
        foreach (var date in new[] { June, December, March })
        {
            double rise = Named("sunrise", at, date), set = Named("sunset", at, date);
            Assert.Equal(-0.833, SunElevation(date, rise, at), 1);
            Assert.Equal(-0.833, SunElevation(date, set, at), 1);
            Assert.True(SunElevation(date, rise + 0.1, at) > SunElevation(date, rise, at), "the sun climbs after sunrise");
            Assert.True(SunElevation(date, Named("day", at, date), at) > 5, "day is daylight");
            Assert.True(SunElevation(date, Named("noon", at, date), at) > SunElevation(date, Named("day", at, date), at));
            Assert.True(SunElevation(date, Named("night", at, date), at) < -6, "night is past civil dusk");
            Assert.True(SunElevation(date, Named("midnight", at, date), at) < 0);
        }
    }

    [Fact]
    public void The_polar_night_and_the_midnight_sun_say_so()
    {
        var svalbard = At(78.22, 15.65);
        Assert.False(LocalHours.IsHome(svalbard));
        foreach (var (date, word) in new[] { (December, "polar night"), (June, "midnight sun") })
            foreach (var name in new[] { "sunrise", "sunset" })
            {
                Assert.True(TimeCommand.TryParse(["set", name], out TimeRequest request, out _));
                Assert.False(TimeCommand.Resolve(request, svalbard, date, true, out double hour, out _, out string error));
                Assert.Contains(word, error);
                Assert.DoesNotContain("NaN", error);
                Assert.True(double.IsFinite(hour));
            }
        // a sunless day's day is its noon, a sunlit night's night its midnight
        Assert.Equal(Named("noon", svalbard, December), Named("day", svalbard, December), 9);
        Assert.Equal(Named("midnight", svalbard, June), Named("night", svalbard, June), 9);
    }

    [Theory]
    [InlineData("swiss", null)]
    [InlineData("CET", null)]
    [InlineData("utc", 0.0)]
    [InlineData("gmt", 0.0)]
    [InlineData("utc+8", 8.0)]
    [InlineData("UTC-3:30", -3.5)]
    [InlineData("gmt+5.75", 5.75)]
    [InlineData("utc+14", 14.0)]
    public void Zone_words_parse(string word, double? offset)
    {
        Assert.True(ClockZone.TryParse(word, out var zone));
        Assert.NotNull(zone);
        Assert.Equal(offset, zone!.Value.UtcOffset);
    }

    [Theory]
    [InlineData("utc+15")]
    [InlineData("utc-13")]
    [InlineData("+8")]
    [InlineData("utc+")]
    [InlineData("mars")]
    public void Bad_zone_words_are_refused(string word) => Assert.False(ClockZone.TryParse(word, out _));

    [Fact]
    public void Here_is_the_players_own_zone()
    {
        Assert.True(ClockZone.TryParse("here", out var zone));
        Assert.Null(zone);
    }

    [Fact]
    public void The_zone_word_rides_every_reading()
    {
        Assert.True(TimeCommand.TryParse(["set", "21:30", "utc+8"], out TimeRequest set, out _));
        Assert.Equal((TimeOp.Set, 21.5, 8.0), (set.Op, set.Value, set.Zone!.Value.UtcOffset!.Value));
        Assert.Null(set.Named);
        Assert.True(TimeCommand.TryParse(["14:00", "swiss"], out TimeRequest bare, out _));
        Assert.True(bare.Op == TimeOp.Set && bare.Zone!.Value.IsSwiss);
        Assert.True(TimeCommand.TryParse(["utc"], out TimeRequest query, out _));
        Assert.True(query.Op == TimeOp.Query && query.Zone!.Value.UtcOffset == 0);
        Assert.True(TimeCommand.TryParse(["query", "utc-5"], out TimeRequest q2, out _));
        Assert.Equal(-5, q2.Zone!.Value.UtcOffset);
        Assert.True(TimeCommand.TryParse(["set", "noon"], out TimeRequest noon, out _));
        Assert.Equal("noon", noon.Named);
        Assert.False(TimeCommand.TryParse(["set", "noon", "mars"], out _, out string err));
        Assert.Contains("mars", err);
        Assert.False(TimeCommand.TryParse(["add", "2", "utc"], out _, out _));
        // a typed zone at home: 12:00 UTC is 14:00 in a Swiss summer
        Assert.True(TimeCommand.TryParse(["set", "12:00", "utc"], out TimeRequest utcNoon, out _));
        Assert.True(TimeCommand.Resolve(utcNoon, LocalHours.Bern, June, true, out double h, out _, out _));
        Assert.Equal(14, h, 9);
    }

    [Fact]
    public void Zones_round_trip_through_the_Swiss_clock()
    {
        foreach (var offset in new[] { -12.0, -3.5, 0, 5.75, 8, 14 })
            foreach (var date in new[] { June, December })
            {
                var zone = new ClockZone(offset);
                for (double hour = 0; hour < 24; hour += 1.25)
                    Assert.Equal(0, TimeCommand.ShortWay(zone.FromSwiss(zone.ToSwiss(hour, date), date), hour), 9);
            }
    }
}
