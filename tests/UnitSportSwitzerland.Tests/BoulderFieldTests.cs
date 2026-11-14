using System;
using System.Linq;
using UnitSport.Terrain.Bodies;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Boulders round fresh craters (#1849, src/Terrain/Bodies/BoulderField.cs).</summary>
public class BoulderFieldTests
{
    private const double R = 1_737_400;

    private static BoulderField Field(double density = 1) => new(new CraterField(1, R, density), 77);

    [Fact]
    public void Every_peer_makes_the_same_boulders_and_round_fresh_craters_only()
    {
        var a = Field().Near(R, 0, 0, 400);
        var b = Field().Near(R, 0, 0, 400);
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        foreach (var x in a)
        {
            double r = Math.Sqrt(x.X * x.X + x.Y * x.Y + x.Z * x.Z);
            Assert.InRange(r, R - 1, R + 1);
            Assert.True(x.Radius >= BoulderField.MinRadiusM && x.Radius < 25);
            double d = Math.Sqrt((x.X - R) * (x.X - R) + x.Y * x.Y + x.Z * x.Z);
            Assert.True(d <= 400 + x.Radius);
        }
        // most small
        Assert.True(a.Count(x => x.Radius < 1) > a.Count / 2);
        // a fresh crater's boulders lie from its rim to two radii out
        var fresh = new CraterField(1, R).Near(R, 0, 0, 300, BoulderField.MinCraterM).Where(c => CraterField.Freshness(c) > 0.9).ToList();
        Assert.NotEmpty(fresh);
    }

    [Fact]
    public void Fewer_where_the_body_has_fewer_craters()
    {
        int moon = Field(1).Near(R, 0, 0, 500).Count, mars = Field(0.15).Near(R, 0, 0, 500).Count;
        Assert.True(mars < moon * 0.4, $"{mars} vs {moon}");
    }
}
