using System;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Mars' heights turned from MOLA's areoid to the IAU ellipsoid (#1853, tools/TerrainPreprocessor/Areoid.cs).</summary>
public class AreoidTests
{
    private const int W = 5760, H = 2880;
    private const double Offset = 3_396_000;

    private static Areoid Grid(Func<double, double, double> radius)
    {
        var r = new short[W * H];
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
                r[j * W + i] = (short)Math.Round(radius(90 - (j + 0.5) / 16, (i + 0.5) / 16) - Offset);
        return new Areoid(r, W, H, Offset);
    }

    [Fact]
    public void An_areoid_that_is_the_ellipsoid_changes_nothing()
    {
        var shape = Ellipsoid.Mars;
        var areoid = Grid((lat, _) => Areoid.RadiusOf(shape, lat));
        var rng = new Random(2);
        for (int k = 0; k < 500; k++)
        {
            double lat = rng.NextDouble() * 179.8 - 89.9, lon = rng.NextDouble() * 360 - 180;
            Assert.InRange(areoid.Correction(lat, lon, shape), -1.5, 1.5);
        }
    }

    [Fact]
    public void The_ellipsoid_is_its_radii_at_the_equator_and_the_poles()
    {
        Assert.Equal(3_396_190, Areoid.RadiusOf(Ellipsoid.Mars, 0), 3);
        Assert.Equal(3_376_200, Areoid.RadiusOf(Ellipsoid.Mars, 90), 3);
        Assert.Equal(3_376_200, Areoid.RadiusOf(Ellipsoid.Mars, -90), 3);
    }

    [Fact]
    public void Longitudes_wrap_and_rows_run_from_the_north()
    {
        // a bump at 10 E, 20 N; read again through -350 and 370
        var areoid = Grid((lat, lon) => Offset + (Math.Abs(lat - 20) < 1 && Math.Abs(lon - 10) < 1 ? 1000 : 0));
        Assert.Equal(Offset + 1000, areoid.Radius(20, 10), 3);
        Assert.Equal(areoid.Radius(20, 10), areoid.Radius(20, -350), 6);
        Assert.Equal(areoid.Radius(20, 10), areoid.Radius(20, 370), 6);
        Assert.Equal(Offset, areoid.Radius(-20, 10), 3);
        // across the 0/360 seam: bilinear between the last column and the first
        Assert.Equal(Offset, areoid.Radius(0, 359.99), 3);
    }
}
