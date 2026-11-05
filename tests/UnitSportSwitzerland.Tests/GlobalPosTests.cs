using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// <c>GlobalPos</c> as an Earth position (#1443 G2, A1 #1502): ECEF inside, its maths the Earth's in the local
/// east-up-south at the place (true north, true metres), LV95 only as a notation in and out.
/// </summary>
public class GlobalPosTests
{
    private static readonly GlobalPos Bern = GlobalPos.FromLv95(2_600_000.25, 1_200_000.75, 540.5);
    private static readonly GlobalPos Geneva = GlobalPos.FromLv95(2_500_123.5, 1_117_456.125, 372.25);

    private static GlobalPos AtGeodetic(double latDeg, double lonDeg, double h) =>
        GlobalPos.FromEcef(Ellipsoid.Wgs84.ToEcef(new Geodetic(latDeg, lonDeg, h)));

    [Fact]
    public void Lv95_notation_round_trips_to_a_tenth_of_a_millimetre()
    {
        var p = GlobalPos.FromLv95(2_581_234.5, 1_105_678.3125, 1_234.75);
        var l = p.Lv95;
        Assert.Equal(2_581_234.5, l.E, 4);
        Assert.Equal(1_105_678.3125, l.N, 4);
        Assert.Equal(1_234.75, l.H, 4);
        Assert.Equal(1_234.75, p.Alt, 4);
        Assert.Equal("2581234.5/1105678.3", l.ToString());
    }

    [Fact]
    public void The_wire_is_its_own_doubles()
    {
        var (x, y, z) = Geneva.ToWire();
        Assert.Equal(Geneva, GlobalPos.FromWire(x, y, z));
        Assert.Equal(Geneva.Ecef, new EcefPos(x, y, z));
    }

    [Theory]
    [InlineData(120.0, -340.0)]
    [InlineData(-2_500.0, 1_800.0)]
    [InlineData(4_000.0, 3_000.0)]
    public void A_level_offset_is_the_local_ENU(double east, double north)
    {
        foreach (var at in new[] { Bern, Geneva })
        {
            var other = at.Moved(east, north);
            var (x, _, z) = at.Enu.ToLocal(other.Ecef);
            Assert.Equal(east, x, 6);
            Assert.Equal(north, -z, 6);
            var (e, n, up) = at.OffsetTo(other);
            Assert.Equal(east, e, 6);
            Assert.Equal(north, n, 6);
            Assert.Equal(0, up, 6);
            // level: the same ellipsoidal height, not the tangent plane's (0.7 m below the curve 3 km out)
            Assert.Equal(at.Geodetic.H, other.Geodetic.H, 6);
        }
    }

    [Fact]
    public void Moving_and_the_offset_are_inverses_with_the_height()
    {
        var p = Geneva.Moved(7_300.5, -2_100.25, 812.5);
        var (e, n, up) = Geneva.OffsetTo(p);
        Assert.Equal(7_300.5, e, 6);
        Assert.Equal(-2_100.25, n, 6);
        Assert.Equal(812.5, up, 6);
        // straight above a place: no horizontal offset
        var above = Bern.WithAlt(Bern.Alt + 300);
        var (ae, an, aup) = Bern.OffsetTo(above);
        Assert.True(Math.Abs(ae) < 1e-6 && Math.Abs(an) < 1e-6, $"{ae}/{an}");
        Assert.Equal(300, aup, 3);
        Assert.Equal(Bern.Alt + 300, above.Alt, 6);
        Assert.True(Bern.HorizontalDistanceTo(above) < 1e-3, "nothing level between them");
    }

    [Theory]
    [InlineData(46.95, 7.44, 0.0)]
    [InlineData(46.95, 7.44, 30.0)]
    [InlineData(46.20, 6.14, 117.0)]
    [InlineData(-33.9, 151.2, 250.0)]
    public void The_level_distance_is_the_geodesic_to_a_millimetre_at_5_km(double lat, double lon, double azimuthDeg)
    {
        var a = AtGeodetic(lat, lon, 0);
        var b = a.Moved(5_000 * Math.Sin(azimuthDeg * Math.PI / 180), 5_000 * Math.Cos(azimuthDeg * Math.PI / 180));
        double geodesic = Vincenty(a.Geodetic, b.Geodetic);
        Assert.True(Math.Abs(a.HorizontalDistanceTo(b) - geodesic) < 1e-3, $"{a.HorizontalDistanceTo(b)} vs {geodesic}");
        Assert.Equal(a.HorizontalDistanceTo(b), b.HorizontalDistanceTo(a), 9);
        Assert.Equal(a.HorizontalDistanceTo(b) * a.HorizontalDistanceTo(b), a.HorizontalDistanceSquaredTo(b), 3);
        // the chord is a little shorter than the arc
        double chord = a.DistanceTo(b);
        Assert.Equal(a.Ecef.DistanceTo(b.Ecef), chord, 9);
        Assert.InRange(geodesic - chord, 0, 1e-3);
    }

    [Fact]
    public void A_vector_is_in_the_local_ENU()
    {
        var d = new Vector3(3.5f, 1.25f, -8f);
        var p = Geneva + d;
        var (x, y, z) = Geneva.Enu.ToLocal(p.Ecef);
        Assert.True(new Vector3((float)x, (float)y, (float)z).DistanceTo(d) < 1e-5f);
        Assert.True((p - Geneva).DistanceTo(d) < 1e-5f);
        var far = Geneva.Moved(600, -900, 20);
        Assert.True((Geneva + (far - Geneva)).DistanceTo(far) < 1e-3, "b + (a - b) is a");
    }

    [Fact]
    public void Bearings_are_from_true_north()
    {
        Assert.Equal(0, Geneva.BearingTo(Geneva.Moved(0, 100)), 9);
        Assert.Equal(Math.PI / 2, Geneva.BearingTo(Geneva.Moved(100, 0)), 9);
        // at Geneva the grid's north is ~0.9° off the true one: a kilometre north on the grid is ~16 m east or west
        var gridNorth = GlobalPos.FromLv95(Geneva.Lv95.E, Geneva.Lv95.N + 1000, Geneva.Alt);
        double deg = Geneva.BearingTo(gridNorth) * 180 / Math.PI;
        Assert.InRange(Math.Abs(deg), 0.8, 1.0);
        var (east, _, _) = Geneva.OffsetTo(gridNorth);
        Assert.True(Math.Abs(east) > 10, $"a km north on the grid is {east:F1} m east at Geneva");
        // and it is the convergence the grid's axes turn by there
        Assert.Equal(-Lv95Grid.TrueYaw(Geneva, 0f), Geneva.BearingTo(gridNorth), 4);
    }

    [Fact]
    public void Another_altitude_keeps_the_place()
    {
        var p = Geneva.WithAlt(1_000);
        Assert.Equal(1_000, p.Alt, 6);
        Assert.Equal(Geneva.Geodetic.LatDeg, p.Geodetic.LatDeg, 9);
        Assert.Equal(Geneva.Geodetic.LonDeg, p.Geodetic.LonDeg, 9);
    }

    [Fact]
    public void The_midpoint_of_two_places_on_the_ground_is_on_the_ground()
    {
        var a = AtGeodetic(46.5, 7.0, 600);
        var b = a.Moved(4_000, 3_000);
        var m = GlobalPos.Midpoint(a, b);
        Assert.Equal(600, m.Geodetic.H, 6);
        Assert.Equal(m.HorizontalDistanceTo(a), m.HorizontalDistanceTo(b), 4);
        Assert.Equal(2_500, m.HorizontalDistanceTo(a), 1);
    }

    [Fact]
    public void The_tile_under_a_place_is_its_LV95_kilometre()
    {
        Assert.Equal(TileId.FromLv95(2_600_000.25, 1_200_000.75), Bern.Tile);
        Assert.Equal(TileId.FromLv95(2_500_123.5, 1_117_456.125), Geneva.Tile);
    }

    [Fact]
    public void Its_maths_need_no_LV95_anywhere_on_the_Earth()
    {
        // 172.56° W, on Bern's antimeridian: LV95's projection breaks down there, the Earth maths do not
        var a = AtGeodetic(10, -172.56, 100);
        var b = a.Moved(1_200, -800, 50);
        var (e, n, up) = a.OffsetTo(b);
        Assert.Equal(1_200, e, 6);
        Assert.Equal(-800, n, 6);
        Assert.Equal(50, up, 6);
        Assert.True(double.IsFinite(a.HorizontalDistanceTo(b)) && double.IsFinite(a.BearingTo(b)));
        Assert.True(((a + new Vector3(5, 0, 0)) - a).DistanceTo(new Vector3(5, 0, 0)) < 1e-5f);
    }

    /// <summary>Vincenty's inverse on WGS84: the geodesic between two latitudes and longitudes, to well under a mm.</summary>
    private static double Vincenty(Geodetic p, Geodetic q)
    {
        double a = 6_378_137.0, f = 1 / 298.257223563, b = a * (1 - f);
        double L = (q.LonDeg - p.LonDeg) * Math.PI / 180;
        double u1 = Math.Atan((1 - f) * Math.Tan(p.LatDeg * Math.PI / 180)), u2 = Math.Atan((1 - f) * Math.Tan(q.LatDeg * Math.PI / 180));
        double su1 = Math.Sin(u1), cu1 = Math.Cos(u1), su2 = Math.Sin(u2), cu2 = Math.Cos(u2);
        double lambda = L, sigma = 0, ss = 0, cs = 0, ca2 = 0, c2m = 0;
        for (int i = 0; i < 200; i++)
        {
            double sl = Math.Sin(lambda), cl = Math.Cos(lambda);
            ss = Math.Sqrt(Math.Pow(cu2 * sl, 2) + Math.Pow(cu1 * su2 - su1 * cu2 * cl, 2));
            cs = su1 * su2 + cu1 * cu2 * cl;
            sigma = Math.Atan2(ss, cs);
            double sa = cu1 * cu2 * sl / ss;
            ca2 = 1 - sa * sa;
            c2m = ca2 != 0 ? cs - 2 * su1 * su2 / ca2 : 0;
            double c = f / 16 * ca2 * (4 + f * (4 - 3 * ca2));
            double next = L + (1 - c) * f * sa * (sigma + c * ss * (c2m + c * cs * (-1 + 2 * c2m * c2m)));
            bool done = Math.Abs(next - lambda) < 1e-14;
            lambda = next;
            if (done) break;
        }
        double uu = ca2 * (a * a - b * b) / (b * b);
        double A = 1 + uu / 16384 * (4096 + uu * (-768 + uu * (320 - 175 * uu)));
        double B = uu / 1024 * (256 + uu * (-128 + uu * (74 - 47 * uu)));
        double ds = B * ss * (c2m + B / 4 * (cs * (-1 + 2 * c2m * c2m) - B / 6 * c2m * (-3 + 4 * ss * ss) * (-3 + 4 * c2m * c2m)));
        return b * A * (sigma - ds);
    }
}
