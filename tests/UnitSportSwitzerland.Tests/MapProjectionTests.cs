using System;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The oblique stereographic projection of the maps and GPX tracks (#1514, G2 C5,
/// tools/TerrainFormat/MapProjection.cs): EPSG's worked example, round trips, true scale and angles.
/// </summary>
public class MapProjectionTests
{
    /// <summary>EPSG Guidance Note 7-2, method 9809: Amersfoort / RD New, 53° N 6° E.</summary>
    [Fact]
    public void Epsg_worked_example()
    {
        double lat0 = 52 + 9 / 60.0 + 22.178 / 3600, lon0 = 5 + 23 / 60.0 + 15.5 / 3600;
        var p = new MapProjection(lat0, lon0, Ellipsoid.Bessel1841, 0.9999079);
        var (x, y) = p.Forward(53, 6);
        Assert.Equal(196_105.283 - 155_000, x, 3);
        Assert.Equal(557_057.739 - 463_000, y, 3);
        var (lat, lon) = p.Inverse(x, y);
        Assert.Equal(53, lat, 9);
        Assert.Equal(6, lon, 9);
    }

    [Theory]
    [InlineData(46.95, 7.44)]   // Bern
    [InlineData(48.39, -4.49)]  // Brest
    [InlineData(-33.9, 151.2)]  // Sydney
    [InlineData(64.1, -21.9)]   // Reykjavik
    [InlineData(0.0, 179.9)]    // across the antimeridian
    public void Round_trips_within_a_micrometre_over_a_thousand_km(double lat0, double lon0)
    {
        var p = new MapProjection(lat0, lon0);
        Assert.Equal(0, p.Forward(lat0, lon0).X, 6);
        Assert.Equal(0, p.Forward(lat0, lon0).Y, 6);
        for (double dx = -1_000_000; dx <= 1_000_000; dx += 250_000)
            for (double dy = -1_000_000; dy <= 1_000_000; dy += 250_000)
            {
                var (lat, lon) = p.Inverse(dx, dy);
                var (x, y) = p.Forward(lat, lon);
                Assert.True(Math.Abs(x - dx) < 1e-6 && Math.Abs(y - dy) < 1e-6, $"{dx},{dy} -> {x},{y}");
            }
    }

    /// <summary>Short distances on the map against the ellipsoid's, through the scale; and a right angle stays one.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50_000, 0)]
    [InlineData(-120_000, 80_000)]
    [InlineData(400_000, -300_000)]
    public void Conformal_and_scale_true(double x, double y)
    {
        var p = new MapProjection(46.5, 7.0);
        var ell = Ellipsoid.Wgs84;
        var (lat, lon) = p.Inverse(x, y);
        var o = ell.ToEcef(new Geodetic(lat, lon, 0));
        const double step = 10;
        var (lat1, lon1) = p.Inverse(x + step, y);
        var (lat2, lon2) = p.Inverse(x, y + step);
        var a = ell.ToEcef(new Geodetic(lat1, lon1, 0)) - o;
        var b = ell.ToEcef(new Geodetic(lat2, lon2, 0)) - o;
        double k = p.ScaleAt(x, y);
        Assert.Equal(step / k, a.Length, 4);
        Assert.Equal(step / k, b.Length, 4);
        double cos = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (a.Length * b.Length);
        Assert.True(Math.Abs(cos) < 1e-6, $"angle off a right one by {Math.Asin(cos) * 180 / Math.PI * 3600:F2}″");
    }

    [Fact]
    public void North_is_up_at_the_centre_and_turns_away_from_it()
    {
        var p = new MapProjection(46.5, 7.0);
        Assert.Equal(0, p.ConvergenceAt(0, 0), 4);
        Assert.Equal(0, p.ConvergenceAt(0, 100_000), 4);
        // east of the centre grid north turns west of true north, as a transverse projection's: sin(lat)·Δlon
        double east = p.ConvergenceAt(76_000, 0);
        Assert.InRange(-east, 0.6, 0.8);
    }
}
