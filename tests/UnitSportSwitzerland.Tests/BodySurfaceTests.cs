using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The body's own numbers the Earth's code used as constants (#1848, H7b): the same values, so nothing on the Earth moves.</summary>
public class BodySurfaceTests
{
    [Fact]
    public void The_Earths_leaf_level_and_mean_radius_are_the_constants_its_code_always_used()
    {
        Assert.Equal(14, CelestialBody.Earth.LeafLevel);
        Assert.Equal(CelestialBody.EarthLeafLevel, CelestialBody.Earth.LeafLevel);
        Assert.Equal(6_371_008.8, CelestialBody.Earth.MeanRadius);
    }

    [Fact]
    public void Another_bodys_mean_radius_is_its_ellipsoids_and_its_leaf_about_as_large_as_the_Earths()
    {
        var moon = CelestialBody.Moon;
        Assert.Equal((2 * moon.Shape.A + moon.Shape.B) / 3, moon.MeanRadius);
        Assert.Equal(12, moon.LeafLevel);
        Assert.Equal(13, CelestialBody.Mars.LeafLevel);
    }
}
