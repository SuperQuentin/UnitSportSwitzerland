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
}
