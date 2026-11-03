using System;
using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Earth's curvature drawn round the camera (#187): the drop and the bulge the server counts.</summary>
public class CurvatureTests
{
    [Theory]
    // the apparent drop, refraction k = 0.13 (geometric: 8 cm, 7.8 m, 196 m, 283 m, 785 m)
    [InlineData(1_000, 0.0682)]
    [InlineData(10_000, 6.82)]
    [InlineData(50_000, 170.5)]
    [InlineData(60_000, 245.5)]
    [InlineData(100_000, 681.9)]
    public void The_ground_drops_away_with_distance(double distance, double drop)
    {
        Assert.Equal(drop, Curvature.Drop(distance), drop * 0.002);
        Assert.Equal(Curvature.Drop(distance), Curvature.DropSquared(distance * distance), 1e-9);
    }

    [Fact]
    public void The_apparent_radius_is_lv95s_sphere_enlarged_for_refraction()
    {
        Assert.Equal(7_331_972, Curvature.ApparentRadiusM, 1.0);
        Assert.Equal(1 / (2 * Curvature.ApparentRadiusM), Curvature.DropPerM2, 1e-15);
    }

    [Fact]
    public void The_bulge_is_highest_halfway_and_nothing_at_the_ends()
    {
        Assert.Equal(0, Curvature.Bulge(0, 60_000));
        Assert.Equal(0, Curvature.Bulge(60_000, 60_000));
        // halfway it is the drop of half the distance: D²/8R
        Assert.Equal(Curvature.Drop(30_000), Curvature.Bulge(30_000, 60_000), 1e-9);
        Assert.True(Curvature.Bulge(30_000, 60_000) > Curvature.Bulge(20_000, 60_000));
        // the line from an eye to a dropped target, lowered by the ground's own drop, is the bulge
        double s = 20_000, total = 60_000;
        double line = s / total * Curvature.Drop(total);
        Assert.Equal(line - Curvature.Drop(s), Curvature.Bulge(s, total), 1e-9);
    }

    // ---- at altitude (#1430, H1) ----

    [Fact]
    public void Refraction_fades_out_between_5_and_20_km()
    {
        Assert.Equal(Curvature.Refraction, Curvature.RefractionAt(0), 12);
        Assert.Equal(Curvature.Refraction, Curvature.RefractionAt(5_000), 12);
        Assert.Equal(0, Curvature.RefractionAt(20_000), 12);
        Assert.Equal(0, Curvature.RefractionAt(100_000), 12);
        double last = 1;
        for (double h = 0; h <= 25_000; h += 500)
        {
            double k = Curvature.RefractionAt(h);
            Assert.True(k <= last + 1e-15, $"refraction never grows with altitude ({h} m)");
            last = k;
        }
        Assert.Equal(Curvature.DropPerM2, Curvature.DropPerM2At(0), 15);
        Assert.Equal(1 / (2 * Curvature.EarthRadiusM), Curvature.DropPerM2At(30_000), 15);
    }

    [Fact]
    public void The_sphere_takes_over_between_20_and_50_km()
    {
        Assert.Equal(0, Curvature.SphereBlendAt(10_000));
        Assert.Equal(0, Curvature.SphereBlendAt(20_000));
        Assert.Equal(0.5, Curvature.SphereBlendAt(35_000), 9);
        Assert.Equal(1, Curvature.SphereBlendAt(50_000));
        Assert.Equal(800_238, Curvature.HorizonM(50_000), 1.0);
    }

    [Fact]
    public void The_exact_wrap_is_the_drop_near_the_camera()
    {
        // the same to second order: within a centimetre over 10 km at sea level
        double k0 = 1 / (2 * Curvature.EarthRadiusM);
        var drop = Curvature.Offset(10_000, 0, k0, 0);
        var exact = Curvature.Offset(10_000, 0, k0, 1);
        Assert.Equal(drop.Up, exact.Up, 0.01);
        Assert.True(Math.Abs(exact.Along) < 0.01, $"and hardly drawn nearer ({exact.Along} m)");
        // far out the sphere draws the ground nearer: 6 km at the horizon from 100 km up (the plan's table)
        var far = Curvature.Offset(1_134_000, 0, k0, 1);
        Assert.Equal(-6_000, far.Along, 500.0);
    }

    /// <summary>The shader's single-precision recipe against doubles: within a centimetre out to 1,000 km (the plan's bar).</summary>
    [Theory]
    [InlineData(1_000, 0)]
    [InlineData(1_000, 4_000)]
    [InlineData(100_000, 0)]
    [InlineData(100_000, 4_478)]
    [InlineData(1_000_000, 0)]
    [InlineData(1_000_000, 50_000)]
    public void The_shaders_float_maths_holds_to_a_centimetre(double flat, double altitude)
    {
        foreach (double blend in new[] { 0.0, 0.3, 1.0 })
        {
            double k = 1 / (2 * Curvature.EarthRadiusM);
            var d = Curvature.Offset(flat, altitude, k, blend);
            var f = Curvature.OffsetAsShader((float)flat, (float)altitude, (float)k, (float)blend);
            // the float of the input distance alone is worth this much at 1,000 km
            double slack = flat * 6e-8 * 2;
            Assert.True(Math.Abs(d.Along - f.Along) < 0.01 + slack, $"along: {d.Along:F4} / {f.Along:F4} (blend {blend})");
            Assert.True(Math.Abs(d.Up - f.Up) < 0.01 + slack * 0.2 + Math.Abs(d.Up) * 1.2e-7, $"up: {d.Up:F4} / {f.Up:F4} (blend {blend})");
        }
    }
}
