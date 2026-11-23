using System;
using System.Linq;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Scatter beyond the boulders (#2016, src/Terrain/Bodies/ScatterField.cs).</summary>
public class ScatterFieldTests
{
    private const double MoonR = 1_737_400, MarsR = 3_389_500;

    [Fact]
    public void Each_profile_has_its_fields_and_io_and_venus_none()
    {
        Assert.Equal(new[] { "rocks", "pebbles" }, ScatterField.For(BodyId.Mars, MarsR, SurfaceProfile.For(BodyId.Mars, MarsR)).Select(f => f.Of.Name));
        Assert.Equal(ScatterField.Kind.IceBlock, ScatterField.For(BodyId.Europa, 1_560_800, null).Single().Of.Kind);
        Assert.Equal(ScatterField.Kind.Cobble, ScatterField.For(BodyId.Titan, 2_574_730, SurfaceProfile.For(BodyId.Titan, 2_574_730)).Single().Of.Kind);
        Assert.Single(ScatterField.For(BodyId.Moon, MoonR, null));
        Assert.Empty(ScatterField.For(BodyId.Io, 1_821_600, null));
        Assert.Empty(ScatterField.For(BodyId.Venus, 6_051_800, null));
        Assert.Empty(ScatterField.For(BodyId.Earth, 6_371_000, null));
    }

    [Fact]
    public void Every_peer_lays_the_same_stones_on_the_ground_within_reach()
    {
        var field = ScatterField.For(BodyId.Moon, MoonR, null)[0];
        var a = field.Near(MoonR, 0, 0, 30);
        var b = ScatterField.For(BodyId.Moon, MoonR, null)[0].Near(MoonR, 0, 0, 30);
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        foreach (var s in a)
        {
            Assert.InRange(Math.Sqrt(s.X * s.X + s.Y * s.Y + s.Z * s.Z), MoonR - 0.01, MoonR + 0.01);
            Assert.InRange(s.Radius, field.Of.MinRadius, field.Of.MaxRadius);
            Assert.True(Math.Sqrt((s.X - MoonR) * (s.X - MoonR) + s.Y * s.Y + s.Z * s.Z) <= 30.001);
        }
        // a square metre at a full share holds PerM2 of them; the share is 0.35..1
        double perM2 = a.Count / (Math.PI * 30 * 30);
        Assert.InRange(perM2, field.Of.PerM2 * 0.25, field.Of.PerM2 * 1.2);
        // most small: N(>r) ∝ r⁻²
        Assert.True(a.Count(s => s.Radius < 2 * field.Of.MinRadius) > a.Count / 2);
    }

    [Fact]
    public void A_stone_is_found_whichever_point_nearby_asks()
    {
        var field = ScatterField.For(BodyId.Europa, 1_560_800, null)[0];
        var near = field.Near(1_560_800, 0, 0, 200);
        var shifted = field.Near(1_560_800, 100, 0, 200);
        var both = near.Intersect(shifted).ToList();
        Assert.NotEmpty(both);
        Assert.All(near.Where(s => Math.Abs(s.Y - 50) < 40 && Math.Abs(s.Z) < 40), s => Assert.Contains(s, shifted));
    }

    [Fact]
    public void Mars_rocks_follow_the_thermal_inertia_guess()
    {
        Assert.Equal(0, ScatterField.RockAbundance(100));
        Assert.Equal(1, ScatterField.RockAbundance(800));
        Assert.True(ScatterField.RockAbundance(250) < 0.15, "sand nearly bare");
        var dunes = (SurfaceProfile.Dunes)SurfaceProfile.For(BodyId.Mars, MarsR)!;
        var noise = new ScatterField.Noise(MarsR, 7);
        double lo = double.MaxValue, hi = 0;
        for (int i = 0; i < 500; i++)
        {
            double a = i * 0.013, b = i * 0.0071;
            double ti = ScatterField.ThermalInertia(MarsR * Math.Cos(a) * Math.Cos(b), MarsR * Math.Sin(a) * Math.Cos(b), MarsR * Math.Sin(b), dunes, noise);
            lo = Math.Min(lo, ti);
            hi = Math.Max(hi, ti);
        }
        // the TES map's range: dusty mantles under 150, rocky plains past 400
        Assert.True(lo < 150 && hi > 400, $"{lo:F0}..{hi:F0}");
    }
}
