using System;
using UnitSport.Terrain.Bodies;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The Moon's generated craters (#1849, src/Terrain/Bodies/CraterField.cs, docs/plans/planets.md section 5).</summary>
public class CraterFieldTests
{
    private const double R = 1_737_400;
    private static readonly CraterField Field = new(seed: 1, R);

    [Fact]
    public void The_same_point_is_the_same_ground_whoever_asks()
    {
        var other = new CraterField(seed: 1, R);
        var rng = new Random(3);
        for (int k = 0; k < 200; k++)
        {
            var (x, y, z) = Point(rng);
            Assert.Equal(Field.At(x, y, z), other.At(x, y, z));
            // only the direction counts: a point above the ground reads the same crater as the ground under it
            Assert.Equal(Field.At(x, y, z), Field.At(x * 1.01, y * 1.01, z * 1.01), 6);
        }
        Assert.NotEqual(Field.At(R, 0, 0) + Field.At(0, R, 0), new CraterField(seed: 2, R).At(R, 0, 0) + new CraterField(seed: 2, R).At(0, R, 0));
    }

    [Fact]
    public void A_bowl_is_low_in_the_middle_high_at_its_rim_and_gone_past_two_radii()
    {
        Assert.Equal(-10, CraterField.Profile(0, depth: 10, rim: 2), 9);
        Assert.Equal(2, CraterField.Profile(1, 10, 2), 9);
        Assert.Equal(2, CraterField.Profile(1.0000001, 10, 2), 4);   // no step at the rim
        Assert.Equal(0, CraterField.Profile(2, 10, 2), 9);
        Assert.True(CraterField.Profile(1.5, 10, 2) is > 0 and < 2);
    }

    [Fact]
    public void There_are_as_many_craters_as_the_equilibrium_says()
    {
        // the 20-40 m bin over a patch of ground: N(>20 m) - N(>40 m) = 0.154 (0.02^-2 - 0.04^-2) = 289 a km²
        int bin = 0;
        for (double d = CraterField.MinDiameterM; d < 20; d *= 2) bin++;
        double cell = 3 * 40;
        long n = 0, cells = 0;
        long c0 = (long)Math.Floor(R / cell);
        for (long j = -20; j < 20; j++)
            for (long k = -20; k < 20; k++)
                for (long i = c0 - 1; i <= c0 + 1; i++)
                {
                    foreach (var _ in Field.CratersIn(bin, i, j, k)) n++;
                    if (i == c0) cells++;
                }
        // the craters kept near the sphere over 40 x 40 cells of 120 m (2.3 km²)
        double km2 = cells * cell * cell / 1e6;
        Assert.InRange(n / km2, 289 * 0.8, 289 * 1.2);
    }

    [Fact]
    public void The_ground_is_pitted_but_not_torn()
    {
        // along a 2 km line at 1 m steps: some bowls deeper than a metre, no step between neighbours over 2 m
        double min = 0, max = 0, steepest = 0, last = Field.At(R, 0, 0);
        for (int s = 1; s <= 2000; s++)
        {
            double h = Field.At(R, s, 0);
            min = Math.Min(min, h);
            max = Math.Max(max, h);
            steepest = Math.Max(steepest, Math.Abs(h - last));
            last = h;
        }
        Assert.True(min < -1, $"deepest {min:F2} m");
        Assert.True(max > 0.2, $"highest rim {max:F2} m");
        Assert.True(steepest < 2, $"{steepest:F2} m between metres");
        // the bins over 40 m left out: shallower
        Assert.True(Math.Abs(Field.At(R, 1234, 0, largest: 40)) <= Math.Abs(Field.At(R, 1234, 0)) + 50);
    }

    private static (double, double, double) Point(Random rng)
    {
        double lat = Math.Asin(rng.NextDouble() * 2 - 1), lon = rng.NextDouble() * 2 * Math.PI;
        return (R * Math.Cos(lat) * Math.Cos(lon), R * Math.Cos(lat) * Math.Sin(lon), R * Math.Sin(lat));
    }
}
