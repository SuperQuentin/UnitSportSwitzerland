using System;
using System.Linq;
using UnitSport.Terrain.Bodies;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Saturn's ring chunks (#1854, src/Terrain/Bodies/RingField.cs).</summary>
public class RingFieldTests
{
    private const double Inner = 74_500_000, Outer = 140_220_000, Half = 200;

    private static RingField Field(Func<double, double>? opacity = null, ulong seed = 6) =>
        new(seed, Inner, Outer, Half, opacity ?? (_ => 1));

    [Fact]
    public void Every_client_makes_the_same_chunks_wherever_it_stands()
    {
        var a = Field().Near(100_000_000, 3_000, 10, 1500);
        var b = Field().Near(100_000_000, 3_000, 10, 1500);
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        // from another point nearby, the shared chunks are the same ones
        var c = Field().Near(100_000_000 + 800, 3_000, 10, 1500);
        var shared = a.Intersect(c).Count();
        Assert.True(shared > a.Count / 4, $"{shared} of {a.Count} shared");
        Assert.NotEqual(a, Field(seed: 7).Near(100_000_000, 3_000, 10, 1500));
    }

    [Fact]
    public void Chunks_stay_in_the_slab_and_in_reach()
    {
        var x = 120_000_000.0;
        var list = Field().Near(x, 0, 0, 1000);
        Assert.InRange(list.Count, 300, 3000);
        foreach (var c in list)
        {
            Assert.InRange(c.Z, -Half, Half);
            Assert.InRange(c.Radius, RingField.MinRadiusM, RingField.MaxRadiusM);
            double d = Math.Sqrt((c.X - x) * (c.X - x) + c.Y * c.Y + c.Z * c.Z);
            Assert.True(d <= 1000 + RingField.MaxRadiusM);
            double r = Math.Sqrt(c.X * c.X + c.Y * c.Y);
            Assert.InRange(r, Inner, Outer);
        }
        // most are small: the power law (half under 1.6 m at a slope of 1.5 from 1 m)
        Assert.True(list.Count(c => c.Radius < 2) > list.Count / 2);
        Assert.Contains(list, c => c.Radius > 10);
    }

    [Fact]
    public void The_count_follows_the_ring_s_opacity_and_nothing_is_made_far_from_it()
    {
        int dense = Field(_ => 1).Near(120_000_000, 0, 0, 1000).Count;
        int thin = Field(_ => 0.1).Near(120_000_000, 0, 0, 1000).Count;
        Assert.InRange(thin, dense * 0.05, dense * 0.2);
        Assert.Empty(Field(_ => 0).Near(120_000_000, 0, 0, 1000));
        Assert.Empty(Field().Near(120_000_000, 0, 5000, 1000));
        Assert.Empty(Field().Near(60_000_000, 0, 0, 1000));
    }

    [Fact]
    public void The_arc_wraps_at_the_seam()
    {
        // just either side of the +X axis (θ = 0 = 2π): the same cells answer
        var below = Field().Near(100_000_000, -10, 0, 600);
        var above = Field().Near(100_000_000, 10, 0, 600);
        Assert.True(below.Intersect(above).Count() > below.Count / 2);
    }
}
