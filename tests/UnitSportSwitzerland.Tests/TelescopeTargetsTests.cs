using System;
using UnitSport.Core;
using UnitSport.Items;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The telescope's target menu and info HUD (#1530, src/Items/TelescopeTargets.cs).</summary>
public class TelescopeTargetsTests
{
    private static readonly DateTime Noon = new(2026, 12, 23, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_menu_has_the_moon_planets_and_big_moons_but_never_the_sun()
    {
        Assert.DoesNotContain(SkyBody.Sun, TelescopeTargets.Menu);
        Assert.Equal(13, TelescopeTargets.Menu.Length);
        Assert.Contains(SkyBody.Titan, TelescopeTargets.Menu);
        Assert.Equal(TelescopeTargets.Menu.Length, new System.Collections.Generic.HashSet<SkyBody>(TelescopeTargets.Menu).Count);
    }

    [Fact]
    public void Stepping_wraps_round_and_starts_at_either_end()
    {
        Assert.Equal(SkyBody.Moon, TelescopeTargets.Step(null, 1));
        Assert.Equal(SkyBody.Titan, TelescopeTargets.Step(null, -1));
        Assert.Equal(SkyBody.Mercury, TelescopeTargets.Step(SkyBody.Moon, 1));
        Assert.Equal(SkyBody.Titan, TelescopeTargets.Step(SkyBody.Moon, -1));
        Assert.Equal(SkyBody.Moon, TelescopeTargets.Step(SkyBody.Titan, 1));
    }

    [Fact]
    public void Horizon_reads_north_east_and_height()
    {
        var (az, alt) = TelescopeTargets.Horizon(0, 0, -1);
        Assert.Equal(0, az, 6);
        Assert.Equal(0, alt, 6);
        Assert.Equal(90, TelescopeTargets.Horizon(1, 0, 0).Azimuth, 6);
        Assert.Equal(180, TelescopeTargets.Horizon(0, 0, 1).Azimuth, 6);
        Assert.Equal(270, TelescopeTargets.Horizon(-1, 0, 0).Azimuth, 6);
        Assert.Equal(45, TelescopeTargets.Horizon(0, 1, -1).Altitude, 6);
        Assert.True(TelescopeTargets.Horizon(0, -0.2, -1).Altitude < 0);
    }

    [Fact]
    public void The_moon_reads_like_a_moon()
    {
        var i = TelescopeTargets.Read(SkyBody.Moon, Noon);
        Assert.InRange(i.DistanceM / 1000, 356_000, 407_000);
        // 29-34 arcminutes wide
        Assert.InRange(i.ArcSec / 60, 29, 34.5);
        Assert.InRange(i.Magnitude, -12.8, -2);
        Assert.InRange(i.Lit, 0, 1);
        Assert.Equal(Ephemeris.MoonPhase(Noon).Lit, i.Lit, 0.02);
        Assert.NotNull(i.Waxing);
    }

    [Fact]
    public void A_full_moon_is_bright_and_a_new_one_is_not_lit()
    {
        // full moon 2026-12-24 01:28 UTC, new moon 2026-12-09 23:52 UTC
        var full = TelescopeTargets.Read(SkyBody.Moon, new DateTime(2026, 12, 24, 1, 28, 0, DateTimeKind.Utc));
        var dark = TelescopeTargets.Read(SkyBody.Moon, new DateTime(2026, 12, 9, 23, 52, 0, DateTimeKind.Utc));
        Assert.True(full.Lit > 0.99, $"lit {full.Lit}");
        Assert.True(dark.Lit < 0.01, $"lit {dark.Lit}");
        Assert.InRange(full.Magnitude, -12.9, -12.4);
    }

    [Theory]
    [InlineData(SkyBody.Mercury)]
    [InlineData(SkyBody.Venus)]
    [InlineData(SkyBody.Mars)]
    [InlineData(SkyBody.Jupiter)]
    [InlineData(SkyBody.Saturn)]
    [InlineData(SkyBody.Uranus)]
    [InlineData(SkyBody.Neptune)]
    [InlineData(SkyBody.Io)]
    [InlineData(SkyBody.Europa)]
    [InlineData(SkyBody.Ganymede)]
    [InlineData(SkyBody.Callisto)]
    [InlineData(SkyBody.Titan)]
    public void Every_menu_body_has_sane_readings(SkyBody body)
    {
        var i = TelescopeTargets.Read(body, Noon);
        Assert.InRange(i.Lit, 0, 1);
        Assert.InRange(i.Magnitude, -5, 15);
        Assert.InRange(i.DistanceM / Ephemeris.AuMetres, 0.3, 32);
        Assert.InRange(i.ArcSec, 0.1, 80);
        Assert.Null(i.Waxing);
    }

    [Fact]
    public void Jupiter_is_far_and_a_few_tens_of_arcseconds()
    {
        var i = TelescopeTargets.Read(SkyBody.Jupiter, Noon);
        Assert.InRange(i.DistanceM / Ephemeris.AuMetres, 3.9, 6.5);
        Assert.InRange(i.ArcSec, 29, 51);
        Assert.InRange(i.Magnitude, -3, -1.5);
    }

    [Fact]
    public void The_text_is_invariant_and_says_when_it_is_below_the_horizon()
    {
        var info = TelescopeTargets.Read(SkyBody.Jupiter, Noon);
        string up = TelescopeTargets.Format(SkyBody.Jupiter, info, (187.46, 34.2));
        string down = TelescopeTargets.Format(SkyBody.Jupiter, info, (10, -5));
        string none = TelescopeTargets.Format(SkyBody.Jupiter, info, null);
        var old = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-CH");
        try
        {
            Assert.Equal(up, TelescopeTargets.Format(SkyBody.Jupiter, info, (187.46, 34.2)));
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = old; }
        Assert.StartsWith("Jupiter\nmagnitude -", up);
        Assert.Contains("altitude +34.2°, azimuth 187°", up);
        Assert.Contains(" AU", up);
        Assert.DoesNotContain("below the horizon", up);
        Assert.Contains("altitude -5.0°", down);
        Assert.Contains("below the horizon", down);
        Assert.EndsWith("not in the sky", none);
    }
}
