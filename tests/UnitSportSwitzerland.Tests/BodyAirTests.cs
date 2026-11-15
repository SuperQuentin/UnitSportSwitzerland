using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnitSport.Core;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Every body's air as data (#1853, src/Core/BodyAir.cs, docs/plans/planets.md sections 6 and 7).</summary>
public class BodyAirTests
{
    [Fact]
    public void The_Earths_air_is_what_the_shell_drew_before()
    {
        // the shader's defaults are the Earth's: a body change writes them back, so the Earth's sky never changes
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string shader = File.ReadAllText(Path.Combine(dir!.FullName, "shaders", "atmosphere.gdshader"));
        double[] Vec(string name)
        {
            var m = Regex.Match(shader, $@"uniform vec3 {name} = vec3\(([^)]*)\);");
            Assert.True(m.Success, name);
            return Array.ConvertAll(m.Groups[1].Value.Split(','), v => double.Parse(v.Trim(), CultureInfo.InvariantCulture));
        }
        double Float(string name)
        {
            var m = Regex.Match(shader, $@"uniform float {name} = ([^;]*);");
            Assert.True(m.Success, name);
            return double.Parse(m.Groups[1].Value.Trim(), CultureInfo.InvariantCulture);
        }
        var e = BodyAir.Earth;
        Assert.Equal(new[] { e.Fine.R, e.Fine.G, e.Fine.B }, Vec("beta_r"));
        Assert.Equal(new[] { e.Coarse.R, e.Coarse.G, e.Coarse.B }, Vec("beta_m"));
        Assert.Equal(new[] { e.FineAbsorption.R, e.FineAbsorption.G, e.FineAbsorption.B }, Vec("absorb_r"));
        Assert.Equal(e.FineHeightM, Float("h_r"));
        Assert.Equal(e.CoarseHeightM, Float("h_m"));
        Assert.Equal(e.CoarseG, Float("g_m"));
        Assert.Equal(e.CoarseExtinction, Float("m_ext"));
        Assert.Equal(e.SunPower, Float("sun_power"));
    }

    [Fact]
    public void Each_body_drags_by_its_own_air()
    {
        // the Earth keeps the 1976 standard atmosphere; Mars thins by e every 11.1 km from 0.020; the Moon has none
        Assert.Equal(Atmosphere.Density(100_000), CelestialBody.Earth.AirDensity(100_000));
        Assert.Equal(0.020, CelestialBody.Mars.AirDensity(0), 6);
        Assert.Equal(0.020 / Math.E, CelestialBody.Mars.AirDensity(11_100), 6);
        Assert.Equal(0, CelestialBody.Mars.AirDensity(130_000));
        Assert.Equal(0, CelestialBody.Moon.AirDensity(0));
        Assert.True(CelestialBody.Mars.HasAir);
        Assert.False(CelestialBody.Moon.HasAir);
        // at 100 km Mars' air is still ~0.4 % of the Earth's density there scaled to its thin air: a low orbit decays
        Assert.InRange(CelestialBody.Mars.AirDensity(100_000), 1e-6, 1e-5);
    }

    [Fact]
    public void Mars_sky_is_red_by_day_and_its_dust_takes_blue()
    {
        var m = BodyAir.Mars;
        // the fine part scatters red over blue (butterscotch), absorbs blue (a redder horizon); the coarse forward part
        // is the bluer one (the blue round the setting sun)
        Assert.True(m.Fine.R > m.Fine.G && m.Fine.G > m.Fine.B);
        Assert.True(m.FineAbsorption.B > m.FineAbsorption.G && m.FineAbsorption.G >= m.FineAbsorption.R);
        Assert.True(m.Coarse.B > m.Coarse.R && m.CoarseG > BodyAir.Earth.CoarseG);
        // a clear Martian day's vertical optical depth, 0.2-0.5 in the visible
        double tauRed = (m.Fine.R + m.FineAbsorption.R) * m.FineHeightM + m.Coarse.R * m.CoarseExtinction * m.CoarseHeightM;
        Assert.InRange(tauRed, 0.2, 0.5);
    }

    [Fact]
    public void The_sky_reads_as_space_above_its_band()
    {
        Assert.Equal(0, BodyAir.Mars.SpaceAt(0));
        Assert.Equal(1, BodyAir.Mars.SpaceAt(80_000));
        Assert.Equal(0.5, BodyAir.Mars.SpaceAt(40_000), 6);
    }
}
