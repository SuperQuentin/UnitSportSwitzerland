using System;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Another body's ground under your feet (#1850, src/Terrain/Bodies/BodyHeights.cs, docs/plans/planets.md section 4).</summary>
public class BodyHeightsTests
{
    private const int Level = 2, Bits = 3;
    private static readonly CubeShape Cube = CubeShape.Moon;
    private static readonly double R = Cube.Shape.A;

    /// <summary>Every tile of the level with heights from <paramref name="h"/> of the lattice point (face u, v).</summary>
    private static Func<CubeKey, CubeTile?> Tiles(Func<double, double, float> h) => key =>
    {
        int side = (1 << Bits) + 1;
        var heights = new float[side * side];
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                double u = CubeSphere.Lattice(Level + Bits, ((long)key.X << Bits) + i);
                double v = CubeSphere.Lattice(Level + Bits, ((long)key.Y << Bits) + j);
                heights[j * side + i] = h(u, v);
            }
        return new CubeTile { Key = key, Bits = Bits, Heights = heights };
    };

    [Fact]
    public void The_data_is_bilinear_between_the_lattice_points_and_exact_on_them()
    {
        // a height linear in u and v: bilinear reads it back anywhere on a face
        var heights = new BodyHeights(Cube, Level, Tiles((u, v) => (float)(1000 * u - 500 * v)), craters: null);
        var rng = new Random(5);
        for (int k = 0; k < 200; k++)
        {
            double u = rng.NextDouble() * 1.8 - 0.9, v = rng.NextDouble() * 1.8 - 0.9;
            var n = Cube.Normal(1, u, v);
            Assert.Equal(1000 * u - 500 * v, heights.Data(n), 2);
        }
    }

    [Fact]
    public void Where_the_pyramid_has_nothing_the_ground_is_the_reference_with_its_craters()
    {
        var craters = new CraterField(seed: 1, R);
        var heights = new BodyHeights(Cube, Level, _ => null, craters);
        var n = Cube.Normal(0, 0.1, 0.2);
        Assert.Equal(0, heights.Data(n));
        Assert.Equal(craters.At(n.X, n.Y, n.Z), heights.At(n), 9);
    }

    [Fact]
    public void A_flat_ground_curves_away_from_the_square_as_the_body_does()
    {
        var heights = new BodyHeights(Cube, Level, Tiles((_, _) => 100), craters: null);
        const int cells = 64;
        const double cell = 4;
        var near = Cube.ToEcef(2, 0.3, -0.2, 5000);
        var (frame, y) = heights.Grid(near, cells, cell);
        int side = cells + 1;
        // the frame stands on the ground under the point
        Assert.Equal(R + 100, frame.Origin.Length, 3);
        Assert.Equal(0, y[cells / 2 * side + cells / 2], 3);
        // a corner 181 m off drops by d² / 2(R + h): 9.4 mm, to the millimetre
        double d = cells / 2 * cell * Math.Sqrt(2);
        Assert.Equal(-d * d / (2 * (R + 100)), y[0], 3);
        Assert.Equal(y[0], y[side * side - 1], 4);
    }

    [Fact]
    public void A_square_is_the_same_ground_on_every_peer_and_twice_on_one()
    {
        var a = new BodyHeights(Cube, Level, Tiles((u, v) => (float)(300 * Math.Sin(7 * u) * Math.Cos(5 * v))), new CraterField(seed: 1, R));
        var b = new BodyHeights(Cube, Level, Tiles((u, v) => (float)(300 * Math.Sin(7 * u) * Math.Cos(5 * v))), new CraterField(seed: 1, R));
        var near = Cube.ToEcef(4, -0.4, 0.6, 200);
        var (fa, ya) = a.Grid(near, 32, 2);
        var (fb, yb) = b.Grid(near, 32, 2);
        Assert.Equal(fa.Origin, fb.Origin);
        Assert.Equal(ya, yb);
        Assert.Equal(ya, a.Grid(near, 32, 2).Y);
        // the craters are on it: the 2 m grid is not the smooth data's
        var smooth = new BodyHeights(Cube, Level, Tiles((u, v) => (float)(300 * Math.Sin(7 * u) * Math.Cos(5 * v))), craters: null).Grid(near, 32, 2).Y;
        Assert.NotEqual(smooth, ya);
    }
}
