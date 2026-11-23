using System;
using System.Linq;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The cover below another body's data (#2016, src/Terrain/Bodies/BodyCover.cs).</summary>
public class BodyCoverTests
{
    private const double MoonR = 1_737_400, MarsR = 3_389_500;

    [Fact]
    public void Every_landable_body_has_its_family_and_the_earth_none()
    {
        Assert.Equal(BodyCover.Kind.Regolith, BodyCover.For(BodyId.Moon));
        Assert.Equal(BodyCover.Kind.Mars, BodyCover.For(BodyId.Mars));
        Assert.Equal(BodyCover.Kind.Sulphur, BodyCover.For(BodyId.Io));
        Assert.Equal(BodyCover.Kind.Ice, BodyCover.For(BodyId.Europa));
        Assert.Equal(BodyCover.Kind.Titan, BodyCover.For(BodyId.Titan));
        Assert.Equal(BodyCover.Kind.None, BodyCover.For(BodyId.Earth));
        Assert.Equal(BodyCover.Kind.None, BodyCover.For(BodyId.Jupiter));
    }

    [Fact]
    public void Mars_caps_grow_in_their_winter_and_shrink_to_the_residual_in_summer()
    {
        var northSummer = BodyCover.IceEdges(BodyId.Mars, 25.2);
        var southSummer = BodyCover.IceEdges(BodyId.Mars, -25.2);
        Assert.Equal(80, northSummer.North, 1);
        Assert.Equal(52, northSummer.South, 1);
        Assert.Equal(55, southSummer.North, 1);
        Assert.Equal(85, southSummer.South, 1);
        Assert.Equal((90.0, 90.0), BodyCover.IceEdges(BodyId.Moon, 0));
    }

    [Fact]
    public void Fresh_craters_rays_are_bright_round_them_and_gone_past_their_reach()
    {
        var crater = new CraterField.Crater(MoonR, 0, 0, 50, 20, 4);
        Assert.Equal(0.7, BodyCover.Ray(crater, 1, MoonR, 10, 0), 3);
        Assert.True(BodyCover.Ray(crater, 1, MoonR, 60, 0) > 0.5, "the blanket just past the rim");
        Assert.Equal(0, BodyCover.Ray(crater, 1, MoonR, 50 * BodyCover.RayReach + 1, 0));
        Assert.Equal(0, BodyCover.Ray(crater, 0, MoonR, 60, 0));
        // rays: round the crater at three radii, some bright and some dark
        var round = Enumerable.Range(0, 360).Select(a => BodyCover.Ray(crater, 1, MoonR, 150 * Math.Cos(a * Math.PI / 180), 150 * Math.Sin(a * Math.PI / 180))).ToArray();
        Assert.True(round.Max() > 0.4 && round.Min() < 0.05, $"rays {round.Min():F2}..{round.Max():F2}");
    }

    [Fact]
    public void A_region_finds_the_fresh_craters_and_answers_the_same_on_every_peer()
    {
        var craters = new CraterField(1, MoonR, 1);
        var at = new EcefPos(MoonR, 0, 0);
        var a = new BodyCover.Region(craters, null, at, 300, 5);
        var b = new BodyCover.Region(craters, null, at, 300, 5);
        Assert.True(a.Craters > 10, $"{a.Craters} fresh craters");
        int bright = 0;
        for (int i = -30; i <= 30; i++)
            for (int j = -30; j <= 30; j++)
            {
                var n = new EcefPos(MoonR, i * 8, j * 8);
                var sa = a.At(n, MoonR);
                Assert.Equal(sa, b.At(n, MoonR));
                Assert.InRange(sa.Rays, 0, 1);
                Assert.Equal(0, sa.Sand);
                if (sa.Rays > 0.2) bright++;
            }
        // most of the ground is old regolith, some of it bright ejecta
        Assert.InRange(bright, 10, 61 * 61 / 2);
    }

    [Fact]
    public void Mars_sand_is_the_dune_fields_own_mask()
    {
        var dunes = (SurfaceProfile.Dunes)SurfaceProfile.For(BodyId.Mars, MarsR)!;
        var region = new BodyCover.Region(null, dunes, new EcefPos(MarsR, 0, 0), 100, 40);
        int sandy = 0;
        for (int i = 0; i < 400; i++)
        {
            var n = new EcefPos(MarsR, i * 997.0, i * 613.0);
            var s = region.At(n, MarsR);
            Assert.Equal(dunes.Sand(n.X, n.Y, n.Z), s.Sand, 5);
            if (s.Sand > 0.5) sandy++;
        }
        Assert.InRange(sandy, 20, 300);
    }
}
