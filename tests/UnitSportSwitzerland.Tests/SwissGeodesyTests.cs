using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// LV95 to the Earth and back (#1430, tools/TerrainFormat/SwissGeodesy.cs and Ellipsoid.cs): the
/// conversions the ellipsoid handover stands on (docs/plans/ellipsoid-handover.md, phase H2).
/// </summary>
public class SwissGeodesyTests
{
    /// <summary>
    /// swisstopo's REFRAME <c>lv95towgs84</c> (geodesy.geo.admin.ch/reframe, 2026-11-03): LV95 E, N
    /// and a height above Bessel 1841 (it applies no geoid) to WGS84 longitude, latitude and
    /// ellipsoidal height. The last three are outside Switzerland (France by Geneva, Bavaria, the
    /// Massif Central): the projection and the datum shift are exact there too.
    /// </summary>
    public static readonly TheoryData<double, double, double, double, double, double> Reframe = new()
    {
        { 2600000, 1200000, 500, 7.438632495274896, 46.951082876677035, 549.6221912773326 },   // Bern
        { 2700000, 1100000, 600, 8.730497093224367, 46.044130357582304, 650.6033397335559 },   // swisstopo's example
        { 2500000, 1118000, 400, 6.142954238534248, 46.20600707201724, 452.4841335406527 },   // Geneva
        { 2830000, 1168000, 1250, 10.44236849336027, 46.62362243860653, 1297.2122088028118 },   // Val Müstair
        { 2641500, 1158000, 4158, 7.979970787722568, 46.57199228957682, 4208.0877408813685 },   // Jungfrau
        { 2611000, 1267000, 260, 7.584759286412181, 47.553625489017406, 307.9074512133375 },   // Basel
        { 2722000, 1077000, 240, 9.00868870360435, 45.83366065786579, 290.7878398858011 },   // Chiasso
        { 2690000, 1284000, 400, 8.63765281096477, 47.70042594749196, 446.51541693042964 },   // Schaffhausen
        { 2746000, 1254000, 670, 9.373614385458522, 47.42060706645866, 716.4789556665346 },   // St. Gallen
        { 2594000, 1120000, 500, 7.360870793612215, 46.231415241492606, 551.4766727788374 },   // Sion
        { 2759000, 1191000, 590, 9.523642070928974, 46.851125193152974, 637.7727702548727 },   // Chur
        { 2538000, 1152000, 500, 6.630726939177208, 46.516422048613904, 551.3723340947181 },   // Lausanne
        { 2683000, 1248000, 410, 8.537690368078762, 47.37760730495958, 457.48256183601916 },   // Zürich
        { 2593000, 1245000, 430, 7.345973880082433, 47.35580991515431, 478.6430982109159 },   // Delémont
        { 2624000, 1097000, 1600, 7.74856539758226, 46.024121253910465, 1651.6228486681357 },   // Zermatt
        { 2822000, 1205000, 1840, 10.355950499910781, 46.95893051857708, 1886.494246906601 },   // Samnaun
        { 2485000, 1110000, 375, 5.950612435112744, 46.13165667007641, 427.79327752254903 },   // France, by Geneva
        { 2900000, 1300000, 500, 11.4431010875687, 47.78161939988664, 542.9132056487724 },   // Bavaria
        { 2300000, 1000000, 200, 3.6285814521968804, 45.08643662348808, 256.3538488484919 },   // Massif Central
    };

    /// <summary>
    /// REFRAME <c>lhn95tobessel</c> (CHGeo2004) at points between the grid's nodes: LV95 E, N and the
    /// geoid above Bessel, metres.
    /// </summary>
    public static readonly TheoryData<double, double, double> Geoid = new()
    {
        { 2700000, 1100000, -0.7079 },
        { 2500000, 1118000, -2.6628 },  // Geneva
        { 2830000, 1168000, 4.1654 },   // Val Müstair
        { 2641500, 1158000, 2.1741 },   // Jungfrau
        { 2611000, 1267000, 1.0556 },   // Basel
        { 2722000, 1077000, -3.7030 },  // Chiasso
        { 2690000, 1284000, 1.4293 },   // Schaffhausen
        { 2746000, 1254000, 0.3947 },   // St. Gallen
        { 2594000, 1120000, 0.4495 },   // Sion
        { 2759000, 1191000, 1.0895 },   // Chur
        { 2538000, 1152000, -1.4995 },  // Lausanne
        { 2683000, 1248000, -0.1414 },  // Zürich
        { 2593000, 1245000, 0.5761 },   // Delémont
        { 2624000, 1097000, 2.8635 },   // Zermatt
        { 2822000, 1205000, 4.5916 },   // Samnaun
        { 2485000, 1110000, -3.0019 },  // France, by Geneva
        { 2560000, 1095000, 0.4654 },   // Grand Combin
        { 2780000, 1140000, 3.0257 },   // Bernina
    };

    [Theory]
    [MemberData(nameof(Reframe))]
    public void Projection_and_datum_shift_match_REFRAME_to_the_millimetre(double e, double n, double hBessel, double lon, double lat, double hEll)
    {
        var (bLat, bLon) = SwissGeodesy.ToBessel(e, n);
        var g = Ellipsoid.Wgs84.ToGeodetic(Ellipsoid.Bessel1841.ToEcef(bLat, bLon, hBessel) + SwissGeodesy.DatumShift);
        double north = (g.LatDeg - lat) * 111_200, east = (g.LonDeg - lon) * 111_200 * Math.Cos(lat * Math.PI / 180);
        Assert.True(Math.Abs(north) < 0.002 && Math.Abs(east) < 0.002 && Math.Abs(g.H - hEll) < 0.002,
            $"off by {east * 1000:F2} mm east, {north * 1000:F2} mm north, {(g.H - hEll) * 1000:F2} mm up");
    }

    [Theory]
    [MemberData(nameof(Geoid))]
    public void The_geoid_grid_matches_REFRAME_between_its_nodes(double e, double n, double geoid)
    {
        double off = SwissGeodesy.BesselGeoid(e, n) - geoid;
        Assert.True(Math.Abs(off) < 0.25, $"geoid off by {off:F3} m");
    }

    [Fact]
    public void An_LHN95_height_lands_on_the_ellipsoid_through_the_geoid()
    {
        // Bern, 500 m above sea level: 499.366 m above Bessel (REFRAME), so 549.622 - 0.634 on WGS84
        var g = SwissGeodesy.ToWgs84(2_600_000, 1_200_000, 500);
        Assert.Equal(549.6222 - 0.6336, g.H, 2);
    }

    [Fact]
    public void The_projection_centre_is_Bern_old_observatory_on_Bessel()
    {
        var (lat, lon) = SwissGeodesy.ToBessel(SwissGeodesy.FalseEasting, SwissGeodesy.FalseNorthing);
        Assert.Equal(46 + 57 / 60.0 + 8.66 / 3600, lat * 180 / Math.PI, 12);
        Assert.Equal(7 + 26 / 60.0 + 22.50 / 3600, lon * 180 / Math.PI, 12);
        Assert.Equal(6_378_815.904, SwissGeodesy.R, 3);
    }

    [Theory]
    [InlineData(2600000, 1200000, 500)]
    [InlineData(2500000, 1118000, 372)]
    [InlineData(2830000, 1168000, 1250)]
    [InlineData(2641500, 1158000, 100_000)]     // 100 km up
    [InlineData(2641500, 1158000, 400_000)]     // low orbit
    [InlineData(2600000, 1200000, -3_000)]      // the interiors' depth
    [InlineData(3_600_000, 1_700_000, 10_000)]  // 1,100 km out, over the Baltic
    [InlineData(1_600_000, 200_000, 0)]          // 1,400 km out, Morocco
    public void LV95_to_ECEF_and_back_is_exact(double e, double n, double h)
    {
        var p = SwissGeodesy.ToEcef(e, n, h);
        var (e2, n2, h2) = SwissGeodesy.FromEcef(p);
        Assert.True(Math.Abs(e2 - e) < 1e-4 && Math.Abs(n2 - n) < 1e-4 && Math.Abs(h2 - h) < 1e-4,
            $"drift {e2 - e:E2} / {n2 - n:E2} / {h2 - h:E2} m");
        var g = SwissGeodesy.ToWgs84(e, n, h);
        var (e3, n3, h3) = SwissGeodesy.FromWgs84(g);
        Assert.True(Math.Abs(e3 - e) < 1e-4 && Math.Abs(n3 - n) < 1e-4 && Math.Abs(h3 - h) < 1e-4,
            $"drift via WGS84 {e3 - e:E2} / {n3 - n:E2} / {h3 - h:E2} m");
    }

    [Theory]
    [InlineData(46.9480, 7.4474)]   // Bern
    [InlineData(46.5197, 6.6323)]   // Lausanne
    [InlineData(45.9766, 7.6586)]   // Matterhorn
    [InlineData(47.6950, 8.6380)]   // Schaffhausen
    [InlineData(46.2380, 6.3010)]   // Veigy-Foncenex, France
    public void The_approximate_SwissProjection_agrees_within_its_metre(double lat, double lon)
    {
        var (e, n) = SwissProjection.ToLv95(lat, lon);
        var (e2, n2, _) = SwissGeodesy.FromWgs84(new Geodetic(lat, lon, 500));
        Assert.True(Math.Abs(e2 - e) < 1.5 && Math.Abs(n2 - n) < 1.5, $"approximate off by {e - e2:F3} / {n - n2:F3} m");
    }

    [Theory]
    [InlineData(46.9480, 7.4474)]    // Bern
    [InlineData(46.2044, 6.1432)]    // Geneva
    [InlineData(48.8566, 2.3522)]    // Paris, 435 km out
    [InlineData(48.3904, -4.4861)]   // Brest, 909 km out
    [InlineData(41.9192, 8.7386)]    // Ajaccio
    public void Lat_lon_to_LV95_and_back_is_exact_far_from_Bern(double lat, double lon)
    {
        // #1443: the way in for OSM, IGN and GPS points; the inverse at the matching height returns them
        var (e, n) = SwissGeodesy.ToLv95(lat, lon);
        var (lat2, lon2) = SwissGeodesy.ToLatLon(e, n);
        Assert.True(Math.Abs(lat2 - lat) < 1e-8 && Math.Abs(lon2 - lon) < 1e-8, $"drift {(lat2 - lat) * 111e3:F4} / {(lon2 - lon) * 76e3:F4} m");
    }

    [Theory]
    [InlineData(46.9480, 7.4474)]
    [InlineData(48.3904, -4.4861)]
    public void The_height_a_lat_lon_is_taken_at_moves_it_by_at_most_HorizontalPerMetre(double lat, double lon)
    {
        // the datums' normals are not parallel: a point converted at 3,000 m lands a little off one at 500 m
        var (e0, n0) = SwissGeodesy.ToLv95(lat, lon, 500);
        var (e1, n1) = SwissGeodesy.ToLv95(lat, lon, 3500);
        double perMetre = Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0)) / 3000;
        Assert.True(perMetre < SwissGeodesy.HorizontalPerMetre, $"{perMetre:E2} m per metre");
    }

    [Fact]
    public void The_approximate_SwissProjection_is_far_off_in_France()
    {
        // why everything from outside goes through SwissGeodesy (#1443): Brest by the polynomials
        var (e, n) = SwissProjection.ToLv95(48.3904, -4.4861);
        var (e2, n2) = SwissGeodesy.ToLv95(48.3904, -4.4861);
        Assert.True(Math.Sqrt((e - e2) * (e - e2) + (n - n2) * (n - n2)) > 200);
    }

    [Fact]
    public void ECEF_of_the_equator_and_the_poles()
    {
        var w = Ellipsoid.Wgs84;
        var p = w.ToEcef(new Geodetic(0, 0, 0));
        Assert.Equal(6_378_137.0, p.X, 6);
        Assert.Equal(0.0, p.Y, 6);
        Assert.Equal(0.0, p.Z, 6);
        var pole = w.ToEcef(new Geodetic(90, 0, 100));
        Assert.Equal(6_356_752.3142, w.B, 4);
        Assert.Equal(w.B + 100, pole.Z, 6);
        var back = w.ToGeodetic(new EcefPos(0, 0, -w.B - 10));
        Assert.Equal(-90.0, back.LatDeg, 12);
        Assert.Equal(10.0, back.H, 6);
    }

    [Theory]
    [InlineData(46.95, 7.44, 0)]
    [InlineData(-33.9, 151.2, -5_000)]
    [InlineData(89.99999, -45, 1_000)]
    [InlineData(0.00001, 179.9999, 35_786_000)]  // geostationary
    [InlineData(-12.3, -77.0, 384_400_000)]      // as far as the moon
    public void Geodetic_to_ECEF_and_back_is_exact(double lat, double lon, double h)
    {
        var w = Ellipsoid.Wgs84;
        var g = w.ToGeodetic(w.ToEcef(new Geodetic(lat, lon, h)));
        double dLat = (g.LatDeg - lat) * 111_200, dLon = (g.LonDeg - lon) * 111_200 * Math.Cos(lat * Math.PI / 180);
        Assert.True(Math.Abs(dLat) < 1e-6 * Math.Max(1, h / 1e6) && Math.Abs(dLon) < 1e-6 * Math.Max(1, h / 1e6) && Math.Abs(g.H - h) < 1e-5 * Math.Max(1, h / 1e6),
            $"drift {dLat:E2} m north, {dLon:E2} m east, {g.H - h:E2} m up");
    }

    [Fact]
    public void LV95_round_trips_over_the_whole_Earth_but_a_sliver_on_Berns_antimeridian()
    {
        // #1480: 172.6° W to 180° came back up to 31 km off before the longitude was wrapped
        double worst = 0;
        int outside = 0;
        for (double lat = -89; lat <= 89; lat += 2)
            for (double lon = -180; lon < 180; lon += 0.5)
            {
                var p = Ellipsoid.Wgs84.ToEcef(new Geodetic(lat, lon, 400_000));
                if (!SwissGeodesy.InDomain(p)) { outside++; continue; }
                var (e, n, h) = SwissGeodesy.FromEcef(p);
                worst = Math.Max(worst, SwissGeodesy.ToEcef(e, n, h).DistanceTo(p));
            }
        Assert.True(worst < 1e-3, $"{worst:E2} m off");
        // only the sliver within ~0.13° of Bern's antimeridian is outside: one column of the grid (172.5° W)
        Assert.Equal(90, outside);
        double anti = 7.4396 - 180;
        Assert.False(SwissGeodesy.InDomain(Ellipsoid.Wgs84.ToEcef(new Geodetic(10, anti + 0.05, 0))));
        Assert.True(SwissGeodesy.InDomain(Ellipsoid.Wgs84.ToEcef(new Geodetic(10, anti + 0.2, 0))));
    }

    [Fact]
    public void A_point_in_the_sliver_is_reported_not_quietly_converted()
    {
        EcefPos? seen = null;
        SwissGeodesy.OutsideDomain = p => seen = p;
        try
        {
            var p = Ellipsoid.Wgs84.ToEcef(new Geodetic(10, 7.4396 - 180 + 0.05, 400_000));
            SwissGeodesy.FromEcef(p);
            Assert.Equal(p, seen);
            seen = null;
            SwissGeodesy.FromEcef(SwissGeodesy.ToEcef(2_600_000, 1_200_000, 500));
            Assert.Null(seen);
        }
        finally { SwissGeodesy.OutsideDomain = null; }
    }
}

/// <summary>The Earth as a body (#1430, src/Core/Planet.cs).</summary>
public class PlanetTests
{
    [Fact]
    public void Earth_pulls_and_orbits_as_published()
    {
        var earth = UnitSport.Core.Planet.Earth;
        Assert.Equal(9.798, earth.Gravity(earth.Shape.A), 3);              // at the equator, no spin
        Assert.Equal(8.69, earth.Gravity(6_371_000 + 400_000), 2);         // a 400 km orbit
        Assert.Equal(7_673, earth.CircularSpeed(6_371_000 + 400_000), 0);
        Assert.Equal(86_164.1, 2 * Math.PI / earth.RotationRate, 1);       // a sidereal day
    }
}
