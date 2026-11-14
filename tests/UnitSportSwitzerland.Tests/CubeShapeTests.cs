using System;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>A cube tiling for every body (#1848, tools/TerrainFormat/CubeShape.cs, docs/plans/planets.md section 4).</summary>
public class CubeShapeTests
{
    [Fact]
    public void The_earths_cube_is_cube_spheres_to_the_bit()
    {
        var rng = new Random(7);
        for (int k = 0; k < 2000; k++)
        {
            int f = rng.Next(6);
            double u = rng.NextDouble() * 2 - 1, v = rng.NextDouble() * 2 - 1, h = rng.NextDouble() * 9000 - 500;
            var a = CubeSphere.ToEcef(f, u, v, h);
            Assert.Equal(a, CubeShape.Earth.ToEcef(f, u, v, h));
            Assert.Equal(CubeSphere.FromEcef(a), CubeShape.Earth.FromEcef(a));
        }
        Assert.Same(CubeShape.Earth, CelestialBody.Earth.Cube);
        Assert.True(CubeShape.Earth.Orientation.SequenceEqual(CubeSphere.Orientation));
    }

    [Theory]
    [InlineData(BodyId.Moon)]
    [InlineData(BodyId.Mars)]
    [InlineData(BodyId.Io)]
    public void A_bodys_cube_round_trips_and_shares_its_edges_to_the_bit(BodyId id)
    {
        var cube = CelestialBody.Of(id).Cube;
        var rng = new Random(11);
        for (int k = 0; k < 2000; k++)
        {
            int f = rng.Next(6);
            double u = rng.NextDouble() * 1.98 - 0.99, v = rng.NextDouble() * 1.98 - 0.99, h = rng.NextDouble() * 20_000 - 9_000;
            var p = cube.ToEcef(f, u, v, h);
            var (f2, u2, v2, h2) = cube.FromEcef(p);
            Assert.Equal(f, f2);
            Assert.Equal(u, u2, 9);
            Assert.Equal(v, v2, 9);
            Assert.Equal(h, h2, 4);
            // its height is the body's: the point is h above the ellipsoid
            Assert.Equal(h, cube.Shape.ToGeodetic(p).H, 4);
        }
        // every edge vertex of a level is the same point from both faces that hold it
        const int level = 5;
        long n = 1L << level;
        for (int f = 0; f < 6; f++)
            for (long i = 0; i <= n; i++)
                foreach (var j in new[] { 0L, n })
                    for (int other = 0; other < 6; other++)
                    {
                        if (other == f || CubeSphere.LatticeOn(f, level, i, j, other) is not { } o) continue;
                        Assert.Equal(cube.LatticeEcef(f, level, i, j, 0), cube.LatticeEcef(other, level, o.I, o.J, 0));
                    }
    }

    [Fact]
    public void Another_bodys_cube_faces_its_prime_meridian_with_the_poles_on_faces_four_and_five()
    {
        var moon = CelestialBody.Moon.Cube;
        Assert.Equal(0, moon.FromLatLon(0, 0).Face);
        Assert.Equal((4, 0.0, 0.0), Round(moon.FromLatLon(90, 0)));
        Assert.Equal((5, 0.0, 0.0), Round(moon.FromLatLon(-90, 0)));
        // the near side's centre is face 0's centre, on the 1,737.4 km sphere
        var c = new CubeKey(0, 0, 0, 0).CentreOn(moon);
        Assert.Equal(0, c.LatDeg, 9);
        Assert.Equal(0, c.LonDeg, 9);
        Assert.Equal(1_737_400, moon.ToEcef(0, 0, 0, 0).X, 6);
    }

    private static (int, double, double) Round((int F, double U, double V) p) => (p.F, Math.Round(p.U, 9) + 0.0, Math.Round(p.V, 9) + 0.0);

    [Fact]
    public void A_moon_leaf_is_about_six_hundred_metres_and_its_anchor_on_the_moon()
    {
        var body = CelestialBody.Moon;
        var key = CubeKey.FromEcef(body.Cube, body.Shape.ToEcef(new Geodetic(26.1, 3.6, 0)), body.LeafLevel);
        Assert.Equal(12, key.Level);
        var a = key.VertexOn(body.Cube, 9, 0, 0, 0);
        var b = key.VertexOn(body.Cube, 9, 512, 0, 0);
        Assert.InRange(a.DistanceTo(b), 500, 800);
        var anchor = key.AnchorOn(body.Cube);
        Assert.Equal(body.Shape.A, anchor.Origin.Length, 3);
        Assert.Equal(key, CubeKey.FromEcef(body.Cube, anchor.Origin, body.LeafLevel));
    }
}
