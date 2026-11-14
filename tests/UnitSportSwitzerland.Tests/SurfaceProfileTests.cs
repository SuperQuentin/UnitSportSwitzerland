using System;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The bodies' relief families under their data (#1853, #1854, src/Terrain/Bodies/SurfaceProfile.cs).</summary>
public class SurfaceProfileTests
{
    private static (double X, double Y, double Z) Point(double r, double lat, double lon)
    {
        double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
        return (r * Math.Cos(la) * Math.Cos(lo), r * Math.Cos(la) * Math.Sin(lo), r * Math.Sin(la));
    }

    [Fact]
    public void Each_family_by_body_and_the_rest_none()
    {
        Assert.IsType<SurfaceProfile.Dunes>(SurfaceProfile.For(BodyId.Mars, 3_396_190));
        Assert.IsType<SurfaceProfile.Ridges>(SurfaceProfile.For(BodyId.Europa, 1_560_800));
        Assert.IsType<SurfaceProfile.Grooves>(SurfaceProfile.For(BodyId.Ganymede, 2_634_100));
        Assert.IsType<SurfaceProfile.LavaFlows>(SurfaceProfile.For(BodyId.Io, 1_821_600));
        Assert.Null(SurfaceProfile.For(BodyId.Moon, 1_737_400));
        Assert.Null(SurfaceProfile.For(BodyId.Earth, 6_378_137));
    }

    [Fact]
    public void Mars_dunes_cover_about_a_third_a_few_metres_high_the_same_everywhere()
    {
        var dunes = (SurfaceProfile.Dunes)SurfaceProfile.For(BodyId.Mars, 3_396_190)!;
        var rng = new Random(5);
        int sandy = 0, n = 3000;
        double top = 0;
        for (int i = 0; i < n; i++)
        {
            var (x, y, z) = Point(3_396_190, Math.Asin(rng.NextDouble() * 2 - 1) * 180 / Math.PI, rng.NextDouble() * 360);
            if (dunes.Sand(x, y, z) > 0.5) sandy++;
            double h = dunes.At(x, y, z);
            Assert.InRange(h, 0, 5.0001);
            top = Math.Max(top, h);
            Assert.Equal(h, SurfaceProfile.For(BodyId.Mars, 3_396_190)!.At(x, y, z));
        }
        Assert.InRange(sandy / (double)n, 0.15, 0.5);
        Assert.True(top > 4);
        // the medium ground's 16 m cells keep dunes 120 m apart; a far coarser mesh drops them
        var p = Point(3_396_190, 10, 20);
        Assert.Equal(0, dunes.At(p.X, p.Y, p.Z, smallest: 100));
    }

    [Fact]
    public void Titan_has_its_sand_seas_only_round_the_equator()
    {
        var dunes = (SurfaceProfile.Dunes)SurfaceProfile.For(BodyId.Titan, 2_574_730)!;
        for (int lon = 0; lon < 360; lon += 15)
        {
            var (x, y, z) = Point(2_574_730, 60, lon);
            Assert.Equal(0, dunes.Sand(x, y, z));
        }
    }

    [Fact]
    public void Ridges_grooves_and_flows_stay_in_their_heights()
    {
        var rng = new Random(9);
        foreach (var (body, r, lo, hi) in new[] { (BodyId.Europa, 1_560_800.0, -20.0, 220.0), (BodyId.Ganymede, 2_634_100.0, -61.0, 61.0), (BodyId.Io, 1_821_600.0, -75.0, 75.0) })
        {
            var profile = SurfaceProfile.For(body, r)!;
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < 2000; i++)
            {
                var (x, y, z) = Point(r, Math.Asin(rng.NextDouble() * 2 - 1) * 180 / Math.PI, rng.NextDouble() * 360);
                double h = profile.At(x, y, z);
                min = Math.Min(min, h);
                max = Math.Max(max, h);
            }
            Assert.InRange(min, lo, hi);
            Assert.InRange(max, lo, hi);
            Assert.True(max - min > 20, $"{body}: {min:F0}..{max:F0}");
        }
    }
}
