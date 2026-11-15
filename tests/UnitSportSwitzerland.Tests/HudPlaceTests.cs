using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The debug overlay's place line (#2033, src/Core/HudPlace.cs): LV95 only in Switzerland near the ground.</summary>
public class HudPlaceTests
{
    private static readonly CelestialBody Earth = CelestialBody.Earth;

    [Fact]
    public void Geneva_on_the_ground_is_written_in_LV95_with_its_height_above_sea_level()
    {
        var s = HudPlace.Place(Earth, new Geodetic(46.2044, 6.1432, 425), (2_500_100, 1_117_900, 375), Earth.Shape.A + 425);
        Assert.Equal("LV95 2500100,1117900  alt 375 m", s);
    }

    [Fact]
    public void Abroad_on_the_ground_is_latitude_longitude_and_height()
    {
        // Paris: the LV95 projection gives numbers there, outside Switzerland's box
        var s = HudPlace.Place(Earth, new Geodetic(48.8566, 2.3522, 80), (2_190_000, 1_420_000, 35), Earth.Shape.A + 80);
        Assert.Equal("Earth 48.857°N 2.352°E  h 80 m", s);
    }

    [Fact]
    public void Over_Switzerland_but_high_up_is_latitude_longitude_and_height()
    {
        var s = HudPlace.Place(Earth, new Geodetic(46.5, 7.5, 25_000), (2_600_000, 1_200_000, 24_950), Earth.Shape.A + 25_000);
        Assert.StartsWith("Earth 46.500°N 7.500°E  h 25000 m", s);
    }

    [Fact]
    public void Low_Earth_orbit_writes_its_true_height_in_km()
    {
        var s = HudPlace.Place(Earth, new Geodetic(-12.25, 140.5, 408_000), null, Earth.Shape.A + 408_000);
        Assert.Equal("Earth 12.250°S 140.500°E  h 408 km", s);
    }

    [Fact]
    public void Past_the_Earths_sphere_of_influence_the_distance_from_its_centre()
    {
        double r = 1_000_000_000 + Earth.Shape.A;
        var s = HudPlace.Place(Earth, new Geodetic(10, 20, r - Earth.Shape.A), null, r);
        Assert.Equal("Earth 1,006,378 km from its centre", s);
    }

    [Fact]
    public void Inside_the_sphere_of_influence_in_deep_space_still_latitude_longitude()
    {
        var s = HudPlace.Place(Earth, new Geodetic(5, -60, 400_000_000), null, Earth.Shape.A + 400_000_000);
        Assert.Equal("Earth 5.000°N 60.000°W  h 400,000 km", s);
    }

    [Fact]
    public void On_the_Moon_its_latitude_longitude_and_height()
    {
        var moon = CelestialBody.Moon;
        var s = HudPlace.Place(moon, new Geodetic(-24.15, 3.5, 1_200), null, moon.Shape.A + 1_200);
        Assert.Equal("Moon 24.150°S 3.500°E  h 1200 m", s);
    }

    [Fact]
    public void The_Sun_centred_frame_in_AU()
    {
        var sun = CelestialBody.Sun;
        var s = HudPlace.Place(sun, new Geodetic(0, 0, 0), null, 1.5 * Ephemeris.AuMetres);
        Assert.Equal("Sun-centred 1.5000 AU", s);
    }

    [Theory]
    [InlineData(2_480_000, 1_070_000, 0, true)]
    [InlineData(2_479_999, 1_100_000, 0, false)]
    [InlineData(2_600_000, 1_300_001, 0, false)]
    [InlineData(2_600_000, 1_200_000, 19_999, true)]
    [InlineData(2_600_000, 1_200_000, 20_000, false)]
    public void The_LV95_box_and_ceiling(double e, double n, double h, bool inside) =>
        Assert.Equal(inside, HudPlace.InLv95(e, n, h));
}
