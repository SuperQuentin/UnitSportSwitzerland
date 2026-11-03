using System;
using System.Globalization;
using System.Linq;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The sun and the moon (#1430, phase H6, src/Core/Ephemeris.cs) against Meeus' worked examples
/// and JPL Horizons, and Swiss civil time (src/Core/SwissTime.cs).
/// </summary>
public class EphemerisTests
{
    private const double Deg = Math.PI / 180;

    /// <summary>A Terrestrial Time instant as the UTC one <see cref="Ephemeris"/> takes.</summary>
    private static DateTime FromTt(int y, int mo, int d) =>
        new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc).AddSeconds(-Ephemeris.DeltaT);

    [Fact]
    public void The_sun_matches_Meeus_example_25a()
    {
        // 1992 October 13, 0h TD: alpha 198.38083 deg, delta -7.78507 deg, R 0.99766 AU
        var (ra, dec, dist) = Ephemeris.Equatorial(SkyBody.Sun, FromTt(1992, 10, 13));
        Assert.Equal(198.38083, (ra / Deg + 360) % 360, 0.002);
        Assert.Equal(-7.78507, dec / Deg, 0.002);
        Assert.Equal(0.99766, dist / Ephemeris.AuMetres, 0.00002);
    }

    [Fact]
    public void The_moon_matches_Meeus_example_47a()
    {
        // 1992 April 12, 0h TD: alpha 134.688470 deg, delta 13.768368 deg, 368,409.7 km
        var (ra, dec, dist) = Ephemeris.Equatorial(SkyBody.Moon, FromTt(1992, 4, 12));
        Assert.Equal(134.688470, ra / Deg, 0.001);
        Assert.Equal(13.768368, dec / Deg, 0.001);
        Assert.Equal(368409.7, dist / 1000, 0.5);
    }

    [Fact]
    public void Sidereal_time_matches_Meeus_example_12a()
    {
        // 1987 April 10, 0h UT: mean 13h10m46.3668s; apparent 13h10m46.1351s
        double gast = Ephemeris.SiderealTime(new DateTime(1987, 4, 10, 0, 0, 0, DateTimeKind.Utc)) / Deg;
        Assert.Equal((13 + 10 / 60.0 + 46.1351 / 3600) * 15, (gast + 360) % 360, 0.0003);
    }

    /// <summary>
    /// JPL Horizons, apparent airless azimuth and elevation (QUANTITIES 4) and the distance (20),
    /// for the sun (10) and the moon (301) from four Swiss towns, the height above WGS84.
    /// </summary>
    private static readonly (string Town, double Lat, double Lon, double H, string Utc, SkyBody Body, double Az, double El, double Au)[] Horizons =
    [
        ("Bern", 46.9511, 7.4386, 550, "2026-Jan-01 03:07:12", SkyBody.Sun, 79.695579198, -41.325687664, 0.98335250529277),
        ("Bern", 46.9511, 7.4386, 550, "2026-Feb-07 16:04:48", SkyBody.Sun, 241.504505928, 4.936146735, 0.98632650856967),
        ("Bern", 46.9511, 7.4386, 550, "2026-Mar-17 05:02:24", SkyBody.Sun, 84.274715491, -7.161667534, 0.99498026512359),
        ("Bern", 46.9511, 7.4386, 550, "2026-Apr-23 18:00:00", SkyBody.Sun, 284.388564401, 3.988267566, 1.00551798030278),
        ("Bern", 46.9511, 7.4386, 550, "2026-May-30 14:38:24", SkyBody.Sun, 253.502870156, 44.315258370, 1.01363458978900),
        ("Bern", 46.9511, 7.4386, 550, "2026-Jul-07 03:36:00", SkyBody.Sun, 53.297092589, -1.859290624, 1.01664505009093),
        ("Bern", 46.9511, 7.4386, 550, "2026-Aug-13 16:33:36", SkyBody.Sun, 268.986955780, 20.994675642, 1.01311169050364),
        ("Bern", 46.9511, 7.4386, 550, "2026-Sep-20 05:31:12", SkyBody.Sun, 90.620389428, 2.052227584, 1.00435798576419),
        ("Bern", 46.9511, 7.4386, 550, "2026-Oct-27 02:09:36", SkyBody.Sun, 61.734872925, -39.885356893, 0.99398710928724),
        ("Bern", 46.9511, 7.4386, 550, "2026-Dec-03 15:07:12", SkyBody.Sun, 230.965361784, 4.056323825, 0.98578608446072),
        ("Bern", 46.9511, 7.4386, 550, "2026-Jan-01 03:07:12", SkyBody.Moon, 288.310891572, 18.906045580, 0.00239815894731),
        ("Bern", 46.9511, 7.4386, 550, "2026-Feb-07 16:04:48", SkyBody.Moon, 2.723359048, -58.135976506, 0.00270081528053),
        ("Bern", 46.9511, 7.4386, 550, "2026-Mar-17 05:02:24", SkyBody.Moon, 106.470863044, -0.505246418, 0.00255020778030),
        ("Bern", 46.9511, 7.4386, 550, "2026-Apr-23 18:00:00", SkyBody.Moon, 196.623869342, 65.369480130, 0.00245789363112),
        ("Bern", 46.9511, 7.4386, 550, "2026-May-30 14:38:24", SkyBody.Moon, 85.172380804, -40.712579494, 0.00273839709456),
        ("Bern", 46.9511, 7.4386, 550, "2026-Jul-07 03:36:00", SkyBody.Moon, 153.147564408, 44.929453738, 0.00252289941271),
        ("Bern", 46.9511, 7.4386, 550, "2026-Aug-13 16:33:36", SkyBody.Moon, 256.108721713, 24.907179185, 0.00245898678501),
        ("Bern", 46.9511, 7.4386, 550, "2026-Sep-20 05:31:12", SkyBody.Moon, 325.982209547, -67.204763937, 0.00273629826105),
        ("Bern", 46.9511, 7.4386, 550, "2026-Oct-27 02:09:36", SkyBody.Moon, 237.902015203, 52.305091298, 0.00241441803479),
        ("Bern", 46.9511, 7.4386, 550, "2026-Dec-03 15:07:12", SkyBody.Moon, 286.649592919, -25.663577187, 0.00261850470007),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Jan-01 03:07:12", SkyBody.Sun, 79.266262083, -42.337440811, 0.98335306628269),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Feb-07 16:04:48", SkyBody.Sun, 240.585040318, 6.076744782, 0.98632566316294),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Mar-17 05:02:24", SkyBody.Sun, 83.420619665, -8.126981496, 0.99498097806060),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Apr-23 18:00:00", SkyBody.Sun, 283.518834430, 4.672380620, 1.00551747139598),
        ("Geneva", 46.2044, 6.1432, 375, "2026-May-30 14:38:24", SkyBody.Sun, 253.018164174, 45.383044204, 1.01363402666686),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Jul-07 03:36:00", SkyBody.Sun, 52.358617248, -3.019934274, 1.01664591343214),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Aug-13 16:33:36", SkyBody.Sun, 268.331172204, 21.902352659, 1.01311106114305),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Sep-20 05:31:12", SkyBody.Sun, 89.658437181, 1.163616954, 1.00435864801220),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Oct-27 02:09:36", SkyBody.Sun, 60.988867674, -41.024670727, 0.99398775414003),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Dec-03 15:07:12", SkyBody.Sun, 230.025245038, 5.218331385, 0.98578522226166),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Jan-01 03:07:12", SkyBody.Moon, 287.732043192, 19.525443011, 0.00239772534664),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Feb-07 16:04:48", SkyBody.Moon, 0.388409709, -58.896236929, 0.00270111339384),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Mar-17 05:02:24", SkyBody.Moon, 105.527827378, -1.155204222, 0.00255069291894),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Apr-23 18:00:00", SkyBody.Moon, 194.182171672, 66.341058455, 0.00245760371462),
        ("Geneva", 46.2044, 6.1432, 375, "2026-May-30 14:38:24", SkyBody.Moon, 84.800446913, -41.655067476, 0.00273893051601),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Jul-07 03:36:00", SkyBody.Moon, 151.040986830, 45.175486509, 0.00252277329832),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Aug-13 16:33:36", SkyBody.Moon, 255.417537014, 25.961717088, 0.00245828296918),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Sep-20 05:31:12", SkyBody.Moon, 322.324272637, -67.287389249, 0.00273632081664),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Oct-27 02:09:36", SkyBody.Moon, 237.166411308, 53.473407576, 0.00241390131119),
        ("Geneva", 46.2044, 6.1432, 375, "2026-Dec-03 15:07:12", SkyBody.Moon, 285.270106859, -25.017290585, 0.00261806527218),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Jan-01 03:07:12", SkyBody.Sun, 82.430273927, -39.341081694, 0.98335138044441),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Feb-07 16:04:48", SkyBody.Sun, 243.785728093, 3.254702228, 0.98632775888244),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Mar-17 05:02:24", SkyBody.Sun, 86.519991108, -5.134499467, 0.99497876299894),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Apr-23 18:00:00", SkyBody.Sun, 286.569124843, 1.917375863, 1.00551952168934),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-May-30 14:38:24", SkyBody.Sun, 256.493175112, 42.409023285, 1.01363561978316),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Jul-07 03:36:00", SkyBody.Sun, 55.518390432, -0.371760674, 1.01664394102624),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Aug-13 16:33:36", SkyBody.Sun, 271.295035307, 18.934201171, 1.01311313264118),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Sep-20 05:31:12", SkyBody.Sun, 92.799250243, 4.120574153, 1.00435644578907),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Oct-27 02:09:36", SkyBody.Sun, 64.910960306, -38.189444707, 0.99398612999472),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Dec-03 15:07:12", SkyBody.Sun, 233.247078953, 2.628636167, 0.98578714712468),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Jan-01 03:07:12", SkyBody.Moon, 290.374670795, 16.844680094, 0.00239961194541),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Feb-07 16:04:48", SkyBody.Moon, 8.210544116, -58.258750418, 0.00270086767759),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Mar-17 05:02:24", SkyBody.Moon, 108.671953502, 1.555728260, 0.00254867173190),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Apr-23 18:00:00", SkyBody.Moon, 203.323546705, 64.961822397, 0.00245801533465),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-May-30 14:38:24", SkyBody.Moon, 87.728998513, -38.697162804, 0.00273723229118),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Jul-07 03:36:00", SkyBody.Moon, 157.147706798, 46.101408977, 0.00252229164866),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Aug-13 16:33:36", SkyBody.Moon, 258.655295456, 22.953811193, 0.00246030670266),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Sep-20 05:31:12", SkyBody.Moon, 332.042962137, -68.522782662, 0.00273667804281),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Oct-27 02:09:36", SkyBody.Moon, 241.782381339, 50.663260950, 0.00241516653093),
        ("Mustair", 46.6290, 10.4470, 1250, "2026-Dec-03 15:07:12", SkyBody.Moon, 288.999411564, -27.707948633, 0.00261987824119),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Jan-01 03:07:12", SkyBody.Sun, 81.956224448, -40.419774799, 0.98335199304014),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Feb-07 16:04:48", SkyBody.Sun, 242.781525569, 4.487953492, 0.98632684290452),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Mar-17 05:02:24", SkyBody.Sun, 85.569356698, -6.166674861, 0.99497952800654),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Apr-23 18:00:00", SkyBody.Sun, 285.587830223, 2.637892582, 1.00551898467106),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-May-30 14:38:24", SkyBody.Sun, 255.974742226, 43.550109110, 1.01363500240560),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Jul-07 03:36:00", SkyBody.Sun, 54.497395010, -1.631239804, 1.01664487861332),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Aug-13 16:33:36", SkyBody.Sun, 270.558968130, 19.900742272, 1.01311245524891),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Sep-20 05:31:12", SkyBody.Sun, 91.723408676, 3.173236439, 1.00435715113480),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Oct-27 02:09:36", SkyBody.Sun, 64.128020766, -39.416517125, 0.99398683799899),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Dec-03 15:07:12", SkyBody.Sun, 232.226838930, 3.890744476, 0.98578620928998),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Jan-01 03:07:12", SkyBody.Moon, 289.707306318, 17.495370360, 0.00239915237164),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Feb-07 16:04:48", SkyBody.Moon, 5.774596457, -59.156766101, 0.00270121342351),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Mar-17 05:02:24", SkyBody.Moon, 107.609084324, 0.872906350, 0.00254918138744),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Apr-23 18:00:00", SkyBody.Moon, 200.941708448, 66.086718880, 0.00245768015350),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-May-30 14:38:24", SkyBody.Moon, 87.306049633, -39.700218189, 0.00273781331975),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Jul-07 03:36:00", SkyBody.Moon, 154.820924466, 46.434854844, 0.00252212800718),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Aug-13 16:33:36", SkyBody.Moon, 257.896648730, 24.082764860, 0.00245954339414),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Sep-20 05:31:12", SkyBody.Moon, 327.888038983, -68.719606620, 0.00273672546260),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Oct-27 02:09:36", SkyBody.Moon, 241.054812657, 51.921912236, 0.00241459358312),
        ("Chiasso", 45.8320, 9.0310, 240, "2026-Dec-03 15:07:12", SkyBody.Moon, 287.445830998, -27.031713148, 0.00261942360517),
    ];

    [Fact]
    public void Sun_and_moon_match_JPL_Horizons_from_four_towns()
    {
        foreach (var r in Horizons)
        {
            var utc = DateTime.SpecifyKind(DateTime.ParseExact(r.Utc, "yyyy-MMM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
            var h = Ephemeris.Topocentric(r.Body, utc, new Geodetic(r.Lat, r.Lon, r.H));
            double az = r.Az * Deg, el = r.El * Deg;
            double dot = h.East * Math.Sin(az) * Math.Cos(el) + h.North * Math.Cos(az) * Math.Cos(el) + h.Up * Math.Sin(el);
            double off = Math.Acos(Math.Min(1, dot)) / Deg;
            if (r.Body == SkyBody.Sun)
            {
                Assert.True(Math.Abs(h.Distance / Ephemeris.AuMetres - r.Au) < 1e-4, $"{r.Town} {r.Utc}: sun at {h.Distance / Ephemeris.AuMetres} AU, Horizons {r.Au}");
            }
            else
            {
                Assert.True(Math.Abs(h.Distance - r.Au * Ephemeris.AuMetres) < 50_000, $"{r.Town} {r.Utc}: moon at {h.Distance / 1000:F0} km, Horizons {r.Au * Ephemeris.AuMetres / 1000:F0}");
            }
            Assert.True(off < (r.Body == SkyBody.Sun ? 0.01 : 0.005), $"{r.Town} {r.Utc} {r.Body}: {off:F4} deg from Horizons (az {h.AzimuthDeg:F3}/{r.Az:F3}, el {h.ElevationDeg:F3}/{r.El:F3})");
        }
    }

    [Fact]
    public void Bern_sun_at_the_solstices_and_equinox()
    {
        // the plan's check: Bern's noon sun at the June solstice is 66.5 deg up (the fixed day said 62)
        var bern = new Geodetic(46.9511, 7.4386, 550);
        double Highest(DateOnly date)
        {
            double best = -90;
            for (double hour = 11; hour <= 15; hour += 1 / 60.0)
                best = Math.Max(best, Ephemeris.Topocentric(SkyBody.Sun, SwissTime.ToUtc(date, hour), bern).ElevationDeg);
            return best;
        }
        Assert.Equal(66.49, Highest(new DateOnly(2026, 6, 21)), 0.05);
        Assert.Equal(19.62, Highest(new DateOnly(2026, 12, 21)), 0.05);
        Assert.Equal(43.1, Highest(new DateOnly(2026, 3, 20)), 0.2);
    }

    /// <summary>Horizons' illuminated fraction (QUANTITIES 10) from Bern at the table's times; waxing from the 2026 lunations.</summary>
    [Theory]
    [InlineData("2026-01-01 03:07:12", 91.91592, true)]
    [InlineData("2026-02-07 16:04:48", 67.40592, false)]
    [InlineData("2026-03-17 05:02:24", 4.03127, false)]
    [InlineData("2026-04-23 18:00:00", 46.14973, true)]
    [InlineData("2026-05-30 14:38:24", 99.34360, true)]
    [InlineData("2026-07-07 03:36:00", 57.32064, false)]
    [InlineData("2026-08-13 16:33:36", 1.18821, true)]
    [InlineData("2026-09-20 05:31:12", 62.78081, true)]
    [InlineData("2026-10-27 02:09:36", 98.78426, false)]
    [InlineData("2026-12-03 15:07:12", 26.28922, false)]
    public void Moon_phase_matches_Horizons(string utc, double litPercent, bool waxing)
    {
        var at = DateTime.SpecifyKind(DateTime.ParseExact(utc, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var (lit, isWaxing) = Ephemeris.MoonPhase(at);
        Assert.Equal(litPercent / 100, lit, 0.01);
        Assert.Equal(waxing, isWaxing);
    }

    /// <summary>
    /// The moon's face from Bern: JPL Horizons' sub-observer longitude (east) and latitude
    /// (QUANTITIES 14, the libration) and the north pole's position angle (17, from ICRF north).
    /// </summary>
    [Theory]
    [InlineData("2026-01-01 03:07:12", 358.161386, -6.033781, 350.5117)]
    [InlineData("2026-02-07 16:04:48", 4.159256, 5.617309, 20.1802)]
    [InlineData("2026-03-17 05:02:24", 354.584058, 1.584402, 340.2214)]
    [InlineData("2026-04-23 18:00:00", 5.461357, -3.669580, 10.8855)]
    [InlineData("2026-05-30 14:38:24", 2.215542, 6.888749, 12.0702)]
    [InlineData("2026-07-07 03:36:00", 352.769292, -3.213961, 337.9244)]
    [InlineData("2026-08-13 16:33:36", 4.777215, 1.301513, 19.5853)]
    [InlineData("2026-09-20 05:31:12", 358.872432, 5.297161, 355.7550)]
    [InlineData("2026-10-27 02:09:36", 356.600995, -6.090675, 342.9513)]
    [InlineData("2026-12-03 15:07:12", 6.366648, 5.589458, 22.2393)]
    public void The_moons_face_matches_Horizons(string utc, double subLon, double subLat, double poleAngle)
    {
        var at = DateTime.SpecifyKind(DateTime.ParseExact(utc, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var (pole, prime, east) = Ephemeris.MoonAxes(at);
        var bern = Ellipsoid.Wgs84.ToEcef(new Geodetic(46.9511, 7.4386, 550));
        var moon = Ephemeris.Ecef(SkyBody.Moon, at);
        var v = bern - moon;
        double len = v.Length;
        static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        double lon = Math.Atan2(Dot(v, east), Dot(v, prime)) / Deg, lat = Math.Asin(Dot(v, pole) / len) / Deg;
        Assert.Equal(0, Math.IEEERemainder(lon - subLon, 360), 0.15);
        Assert.Equal(subLat, lat, 0.15);

        // the pole's position angle on the sky, from north through east (celestial north of date)
        var u = new EcefPos(-v.X / len, -v.Y / len, -v.Z / len);
        var z = new EcefPos(0, 0, 1);
        var north = z - new EcefPos(u.X * u.Z, u.Y * u.Z, u.Z * u.Z);
        var eastSky = new EcefPos(-u.Y, u.X, 0);   // Z x u
        var q = pole - new EcefPos(u.X * Dot(pole, u), u.Y * Dot(pole, u), u.Z * Dot(pole, u));
        double pa = Math.Atan2(Dot(q, eastSky) / Math.Sqrt(Dot(eastSky, eastSky)), Dot(q, north) / Math.Sqrt(Dot(north, north))) / Deg;
        Assert.Equal(0, Math.IEEERemainder(pa - poleAngle, 360), 0.5);
    }

    /// <summary>JPL Horizons from Bern (QUANTITIES 4 and 20) for the seven planets at the table's times.</summary>
    private static readonly (string Utc, SkyBody Body, double Az, double El, double Au)[] PlanetRows =
    [
        ("2026-Jan-01 03:07:12", SkyBody.Mercury, 90.644805647, -33.243300585, 1.37856123127135),
        ("2026-Feb-07 16:04:48", SkyBody.Mercury, 233.408689911, 14.484077827, 1.23528253708939),
        ("2026-Mar-17 05:02:24", SkyBody.Mercury, 100.053171742, 0.607590504, 0.65193581767846),
        ("2026-Apr-23 18:00:00", SkyBody.Mercury, 291.644708519, -15.248226709, 1.16365966909344),
        ("2026-May-30 14:38:24", SkyBody.Mercury, 236.855358708, 58.717237145, 1.12844178116159),
        ("2026-Jul-07 03:36:00", SkyBody.Mercury, 49.220303639, -11.028732220, 0.57424417773870),
        ("2026-Aug-13 16:33:36", SkyBody.Mercury, 282.444293612, 14.956042645, 1.16505765188610),
        ("2026-Sep-20 05:31:12", SkyBody.Mercury, 83.666316763, -14.620217693, 1.29338177787297),
        ("2026-Oct-27 02:09:36", SkyBody.Mercury, 50.291634149, -55.040069964, 0.73896843562792),
        ("2026-Dec-03 15:07:12", SkyBody.Mercury, 245.360931361, -1.963838346, 1.26197945120216),
        ("2026-Jan-01 03:07:12", SkyBody.Venus, 81.450004194, -40.794163112, 1.71001547452856),
        ("2026-Feb-07 16:04:48", SkyBody.Venus, 235.996021806, 10.495127428, 1.69261393317897),
        ("2026-Mar-17 05:02:24", SkyBody.Venus, 68.399833472, -13.316848085, 1.61689394092703),
        ("2026-Apr-23 18:00:00", SkyBody.Venus, 271.907350776, 26.984279132, 1.47405706503647),
        ("2026-May-30 14:38:24", SkyBody.Venus, 203.140293034, 66.280137463, 1.26526328125675),
        ("2026-Jul-07 03:36:00", SkyBody.Venus, 18.273425744, -27.303057401, 0.99623372928383),
        ("2026-Aug-13 16:33:36", SkyBody.Venus, 218.730967123, 31.826289142, 0.69862921954863),
        ("2026-Sep-20 05:31:12", SkyBody.Venus, 79.929988317, -35.192090244, 0.41433029010707),
        ("2026-Oct-27 02:09:36", SkyBody.Venus, 71.426217058, -38.684518902, 0.27342272369409),
        ("2026-Dec-03 15:07:12", SkyBody.Venus, 269.594036863, -12.880470305, 0.44441263018309),
        ("2026-Jan-01 03:07:12", SkyBody.Mars, 78.396439420, -43.380918174, 2.41062568633323),
        ("2026-Feb-07 16:04:48", SkyBody.Mars, 244.433633236, -1.433858125, 2.37038313417284),
        ("2026-Mar-17 05:02:24", SkyBody.Mars, 98.888775794, -2.990964906, 2.31765401152230),
        ("2026-Apr-23 18:00:00", SkyBody.Mars, 294.256369394, -16.630115827, 2.25805576948529),
        ("2026-May-30 14:38:24", SkyBody.Mars, 271.365120181, 17.775425646, 2.18711806580022),
        ("2026-Jul-07 03:36:00", SkyBody.Mars, 83.873807676, 23.614702608, 2.08754836600458),
        ("2026-Aug-13 16:33:36", SkyBody.Mars, 312.241937967, -4.370279483, 1.94224452522817),
        ("2026-Sep-20 05:31:12", SkyBody.Mars, 128.232068231, 56.250467401, 1.73647392052540),
        ("2026-Oct-27 02:09:36", SkyBody.Mars, 96.925220142, 30.472086523, 1.47189253792965),
        ("2026-Dec-03 15:07:12", SkyBody.Mars, 328.526291702, -24.972538414, 1.15451687407166),
        ("2026-Jan-01 03:07:12", SkyBody.Jupiter, 248.130354658, 48.117230295, 4.24229548323228),
        ("2026-Feb-07 16:04:48", SkyBody.Jupiter, 79.689551522, 21.916057531, 4.36464267318913),
        ("2026-Mar-17 05:02:24", SkyBody.Jupiter, 332.730346252, -15.480445511, 4.83724174385555),
        ("2026-Apr-23 18:00:00", SkyBody.Jupiter, 219.901115229, 60.949553882, 5.43335041804586),
        ("2026-May-30 14:38:24", SkyBody.Jupiter, 178.705744896, 64.789582402, 5.93870268988573),
        ("2026-Jul-07 03:36:00", SkyBody.Jupiter, 40.706549725, -12.550577398, 6.24559160655764),
        ("2026-Aug-13 16:33:36", SkyBody.Jupiter, 279.663442136, 16.181606911, 6.28114798159922),
        ("2026-Sep-20 05:31:12", SkyBody.Jupiter, 108.823759443, 38.084287657, 6.03712838894198),
        ("2026-Oct-27 02:09:36", SkyBody.Jupiter, 93.937665832, 23.320715173, 5.57290707319414),
        ("2026-Dec-03 15:07:12", SkyBody.Jupiter, 335.233094413, -26.396492171, 4.99279551439012),
        ("2026-Jan-01 03:07:12", SkyBody.Saturn, 328.995284413, -42.521155790, 9.71734709448858),
        ("2026-Feb-07 16:04:48", SkyBody.Saturn, 212.892939552, 35.683724106, 10.2355701219203),
        ("2026-Mar-17 05:02:24", SkyBody.Saturn, 78.179153361, -11.473930917, 10.4811809373478),
        ("2026-Apr-23 18:00:00", SkyBody.Saturn, 294.367263936, -19.398977732, 10.3825835293986),
        ("2026-May-30 14:38:24", SkyBody.Saturn, 281.514571099, -6.908814119, 9.98455175899321),
        ("2026-Jul-07 03:36:00", SkyBody.Saturn, 142.563223804, 40.369915045, 9.39458508413060),
        ("2026-Aug-13 16:33:36", SkyBody.Saturn, 29.483045614, -35.584503483, 8.81248132378401),
        ("2026-Sep-20 05:31:12", SkyBody.Saturn, 262.006702827, 10.678742685, 8.46561899541461),
        ("2026-Oct-27 02:09:36", SkyBody.Saturn, 252.411328107, 17.511341985, 8.51141775804291),
        ("2026-Dec-03 15:07:12", SkyBody.Saturn, 110.985184375, 19.532313508, 8.93426803473988),
        ("2026-Jan-01 03:07:12", SkyBody.Uranus, 290.036032368, 8.053917394, 18.7567886564531),
        ("2026-Feb-07 16:04:48", SkyBody.Uranus, 129.686399648, 53.807614717, 19.3120038206488),
        ("2026-Mar-17 05:02:24", SkyBody.Uranus, 22.006857218, -20.606577379, 19.9308532228522),
        ("2026-Apr-23 18:00:00", SkyBody.Uranus, 270.973775345, 26.936700908, 20.3665712145484),
        ("2026-May-30 14:38:24", SkyBody.Uranus, 258.794495324, 38.280261277, 20.4686959877022),
        ("2026-Jul-07 03:36:00", SkyBody.Uranus, 85.511697693, 24.800429293, 20.2118673412407),
        ("2026-Aug-13 16:33:36", SkyBody.Uranus, 334.970338921, -18.216186548, 19.6773847073913),
        ("2026-Sep-20 05:31:12", SkyBody.Uranus, 227.496621828, 56.872259784, 19.0504402487102),
        ("2026-Oct-27 02:09:36", SkyBody.Uranus, 205.338875583, 62.130370467, 18.5791982853339),
        ("2026-Dec-03 15:07:12", SkyBody.Uranus, 58.243910523, -0.478176068, 18.4533266163249),
        ("2026-Jan-01 03:07:12", SkyBody.Neptune, 326.723213218, -39.517183450, 30.0601590565449),
        ("2026-Feb-07 16:04:48", SkyBody.Neptune, 212.752176995, 36.999811530, 30.6158444586329),
        ("2026-Mar-17 05:02:24", SkyBody.Neptune, 79.897387201, -10.040303185, 30.8736597749145),
        ("2026-Apr-23 18:00:00", SkyBody.Neptune, 297.906904513, -23.622145360, 30.7431244680320),
        ("2026-May-30 14:38:24", SkyBody.Neptune, 285.868879718, -13.832819094, 30.2923497574615),
        ("2026-Jul-07 03:36:00", SkyBody.Neptune, 155.803945216, 40.949764651, 29.6754890834236),
        ("2026-Aug-13 16:33:36", SkyBody.Neptune, 42.183928920, -34.321437637, 29.1316973977665),
        ("2026-Sep-20 05:31:12", SkyBody.Neptune, 266.973553725, 2.760933291, 28.8793216387609),
        ("2026-Oct-27 02:09:36", SkyBody.Neptune, 256.905840664, 11.375460749, 29.0256181187682),
        ("2026-Dec-03 15:07:12", SkyBody.Neptune, 117.204937947, 22.331678974, 29.5223541729994),
    ];

    /// <summary>
    /// How far each planet may be from Horizons, degrees: JPL's table 1 is good to tens of
    /// arcseconds for the inner planets and Neptune, a few arcminutes for Jupiter and Saturn.
    /// </summary>
    private static double PlanetTolerance(SkyBody b) => b switch
    {
        SkyBody.Jupiter or SkyBody.Saturn => 0.12,
        SkyBody.Uranus => 0.03,
        _ => 0.02,
    };

    [Fact]
    public void Planets_match_JPL_Horizons_from_Bern()
    {
        var bern = new Geodetic(46.9511, 7.4386, 550);
        var worst = new System.Collections.Generic.Dictionary<SkyBody, double>();
        foreach (var r in PlanetRows)
        {
            var utc = DateTime.SpecifyKind(DateTime.ParseExact(r.Utc, "yyyy-MMM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
            var h = Ephemeris.Topocentric(r.Body, utc, bern);
            double az = r.Az * Deg, el = r.El * Deg;
            double dot = h.East * Math.Sin(az) * Math.Cos(el) + h.North * Math.Cos(az) * Math.Cos(el) + h.Up * Math.Sin(el);
            double off = Math.Acos(Math.Min(1, dot)) / Deg;
            worst[r.Body] = Math.Max(worst.GetValueOrDefault(r.Body), off);
            Assert.True(off < PlanetTolerance(r.Body), $"{r.Body} {r.Utc}: {off:F4} deg from Horizons");
            Assert.True(Math.Abs(h.Distance / Ephemeris.AuMetres - r.Au) / r.Au < 2e-3, $"{r.Body} {r.Utc}: {h.Distance / Ephemeris.AuMetres:F5} au, Horizons {r.Au:F5}");
        }
        Console.WriteLine(string.Join(", ", worst.Select(w => $"{w.Key} {w.Value:F4}")));
    }

    /// <summary>
    /// Each planet's face from Bern: Horizons' sub-observer longitude and latitude (QUANTITIES 14;
    /// west longitude for the planets) and its north pole's position angle (17, from ICRF north).
    /// The latitude is how far the pole tips towards us: Saturn's rings open by it.
    /// </summary>
    [Theory]
    [InlineData("2026-Jan-01 03:07:12", SkyBody.Mercury, 226.295138, -3.970730, 5.8743)]
    [InlineData("2026-Apr-23 18:00:00", SkyBody.Mercury, 93.148067, -1.579070, 331.4481)]
    [InlineData("2026-Aug-13 16:33:36", SkyBody.Mercury, 306.703727, 6.129869, 13.1983)]
    [InlineData("2026-Dec-03 15:07:12", SkyBody.Mercury, 164.916312, -2.134425, 20.5432)]
    [InlineData("2026-Jan-01 03:07:12", SkyBody.Venus, 324.286512, 0.954811, 357.1013)]
    [InlineData("2026-Apr-23 18:00:00", SkyBody.Venus, 271.230184, -1.653469, 346.9401)]
    [InlineData("2026-Aug-13 16:33:36", SkyBody.Venus, 204.360269, 2.372547, 22.8222)]
    [InlineData("2026-Dec-03 15:07:12", SkyBody.Venus, 32.927598, -0.540545, 20.5282)]
    [InlineData("2026-Jan-01 03:07:12", SkyBody.Mars, 244.516029, -7.952012, 19.8002)]
    [InlineData("2026-Apr-23 18:00:00", SkyBody.Mars, 70.908098, -24.710633, 328.3766)]
    [InlineData("2026-Aug-13 16:33:36", SkyBody.Mars, 32.215090, 3.535768, 334.2018)]
    [InlineData("2026-Dec-03 15:07:12", SkyBody.Mars, 12.317619, 22.828086, 11.5342)]
    [InlineData("2026-Jan-01 03:07:12", SkyBody.Jupiter, 89.860515, 1.577721, 10.4972)]
    [InlineData("2026-Apr-23 18:00:00", SkyBody.Jupiter, 207.018643, 1.509197, 9.0535)]
    [InlineData("2026-Aug-13 16:33:36", SkyBody.Jupiter, 68.766404, 0.641422, 17.4909)]
    [InlineData("2026-Dec-03 15:07:12", SkyBody.Jupiter, 305.958680, -0.430131, 22.1942)]
    [InlineData("2026-Jan-01 03:07:12", SkyBody.Saturn, 77.162219, -1.174813, 4.4381)]
    [InlineData("2026-Apr-23 18:00:00", SkyBody.Saturn, 294.903667, -8.118140, 3.5037)]
    [InlineData("2026-Aug-13 16:33:36", SkyBody.Saturn, 336.767247, -10.957803, 2.9589)]
    [InlineData("2026-Dec-03 15:07:12", SkyBody.Saturn, 22.348839, -7.469997, 3.5149)]
    [InlineData("2026-Jan-01 03:07:12", SkyBody.Uranus, 68.893999, 69.512714, 278.1440)]
    [InlineData("2026-Apr-23 18:00:00", SkyBody.Uranus, 347.049032, 71.232351, 280.8363)]
    [InlineData("2026-Aug-13 16:33:36", SkyBody.Uranus, 297.428473, 75.930446, 291.0065)]
    [InlineData("2026-Dec-03 15:07:12", SkyBody.Uranus, 237.155242, 74.234406, 286.7221)]
    [InlineData("2026-Jan-01 03:07:12", SkyBody.Neptune, 3.014625, -20.795938, 317.4064)]
    [InlineData("2026-Apr-23 18:00:00", SkyBody.Neptune, 100.528578, -19.592394, 316.1915)]
    [InlineData("2026-Aug-13 16:33:36", SkyBody.Neptune, 199.770542, -19.140523, 315.8769)]
    [InlineData("2026-Dec-03 15:07:12", SkyBody.Neptune, 296.138652, -20.012325, 316.6654)]
    public void The_planets_faces_match_Horizons(string utc, SkyBody body, double subLon, double subLat, double poleAngle)
    {
        var at = DateTime.SpecifyKind(DateTime.ParseExact(utc, "yyyy-MMM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var (pole, prime, east) = Ephemeris.Axes(body, at);
        var bern = Ellipsoid.Wgs84.ToEcef(new Geodetic(46.9511, 7.4386, 550));
        var v = bern - Ephemeris.Ecef(body, at);
        double len = v.Length;
        static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        double lonEast = Math.Atan2(Dot(v, east), Dot(v, prime)) / Deg, centric = Math.Asin(Dot(v, pole) / len);
        // Horizons' latitude is planetodetic: on the flattened body, steeper than the centre's
        var (re, rp) = Ephemeris.Radii(body);
        double lat = Math.Atan(Math.Tan(centric) * (re / rp) * (re / rp)) / Deg;
        Assert.Equal(subLat, lat, 0.3);
        // west longitude for a prograde planet, east for the retrograde ones (IAU)
        double lon = body is SkyBody.Venus or SkyBody.Uranus ? lonEast : -lonEast;
        Assert.True(Math.Abs(Math.IEEERemainder(lon - subLon, 360)) < 2.0, $"{body}: longitude {lon:F2}, Horizons {subLon:F2}");
        var u = new EcefPos(-v.X / len, -v.Y / len, -v.Z / len);
        var z = new EcefPos(0, 0, 1);
        var north = z - new EcefPos(u.X * u.Z, u.Y * u.Z, u.Z * u.Z);
        var eastSky = new EcefPos(-u.Y, u.X, 0);
        var q = pole - new EcefPos(u.X * Dot(pole, u), u.Y * Dot(pole, u), u.Z * Dot(pole, u));
        double pa = Math.Atan2(Dot(q, eastSky) / Math.Sqrt(Dot(eastSky, eastSky)), Dot(q, north) / Math.Sqrt(Dot(north, north))) / Deg;
        Assert.Equal(0, Math.IEEERemainder(pa - poleAngle, 360), 0.6);
    }

    /// <summary>JPL Horizons from Bern (QUANTITIES 4) for Jupiter's four big moons and Titan at the table's times.</summary>
    private static readonly (string Utc, SkyBody Body, double Az, double El)[] MoonRows =
    [
        ("2026-Jan-01 03:07:12", SkyBody.Io, 248.172560292, 48.098668252),
        ("2026-Feb-07 16:04:48", SkyBody.Io, 79.702021807, 21.934017842),
        ("2026-Mar-17 05:02:24", SkyBody.Io, 332.706609925, -15.474956523),
        ("2026-Apr-23 18:00:00", SkyBody.Io, 219.851358426, 60.957616304),
        ("2026-May-30 14:38:24", SkyBody.Io, 178.654542901, 64.785070139),
        ("2026-Jul-07 03:36:00", SkyBody.Io, 40.690919942, -12.565078335),
        ("2026-Aug-13 16:33:36", SkyBody.Io, 279.669831414, 16.178107124),
        ("2026-Sep-20 05:31:12", SkyBody.Io, 108.839581277, 38.107827921),
        ("2026-Oct-27 02:09:36", SkyBody.Io, 93.942548332, 23.330942194),
        ("2026-Dec-03 15:07:12", SkyBody.Io, 335.268536047, -26.393679449),
        ("2026-Jan-01 03:07:12", SkyBody.Europa, 248.125068465, 48.118214145),
        ("2026-Feb-07 16:04:48", SkyBody.Europa, 79.671328747, 21.893154828),
        ("2026-Mar-17 05:02:24", SkyBody.Europa, 332.773772092, -15.489729396),
        ("2026-Apr-23 18:00:00", SkyBody.Europa, 219.811085786, 60.964778563),
        ("2026-May-30 14:38:24", SkyBody.Europa, 178.752239901, 64.793538991),
        ("2026-Jul-07 03:36:00", SkyBody.Europa, 40.684869419, -12.570018694),
        ("2026-Aug-13 16:33:36", SkyBody.Europa, 279.697409567, 16.164519712),
        ("2026-Sep-20 05:31:12", SkyBody.Europa, 108.799078858, 38.047487278),
        ("2026-Oct-27 02:09:36", SkyBody.Europa, 93.948068887, 23.343161440),
        ("2026-Dec-03 15:07:12", SkyBody.Europa, 335.186571102, -26.400548026),
        ("2026-Jan-01 03:07:12", SkyBody.Ganymede, 248.229740475, 48.073092430),
        ("2026-Feb-07 16:04:48", SkyBody.Ganymede, 79.717116496, 21.956729800),
        ("2026-Mar-17 05:02:24", SkyBody.Ganymede, 332.656521776, -15.464103896),
        ("2026-Apr-23 18:00:00", SkyBody.Ganymede, 219.816128849, 60.962249188),
        ("2026-May-30 14:38:24", SkyBody.Ganymede, 178.744617521, 64.791740989),
        ("2026-Jul-07 03:36:00", SkyBody.Ganymede, 40.755375399, -12.506017083),
        ("2026-Aug-13 16:33:36", SkyBody.Ganymede, 279.667931802, 16.180212921),
        ("2026-Sep-20 05:31:12", SkyBody.Ganymede, 108.784502927, 38.025715842),
        ("2026-Oct-27 02:09:36", SkyBody.Ganymede, 93.911347911, 23.265928421),
        ("2026-Dec-03 15:07:12", SkyBody.Ganymede, 335.281095658, -26.392186769),
        ("2026-Jan-01 03:07:12", SkyBody.Callisto, 248.202441608, 48.090096659),
        ("2026-Feb-07 16:04:48", SkyBody.Callisto, 79.589848890, 21.785036811),
        ("2026-Mar-17 05:02:24", SkyBody.Callisto, 332.690511095, -15.475210174),
        ("2026-Apr-23 18:00:00", SkyBody.Callisto, 220.144700181, 60.906819905),
        ("2026-May-30 14:38:24", SkyBody.Callisto, 178.873085404, 64.806654010),
        ("2026-Jul-07 03:36:00", SkyBody.Callisto, 40.644779759, -12.604857839),
        ("2026-Aug-13 16:33:36", SkyBody.Callisto, 279.576013225, 16.225424847),
        ("2026-Sep-20 05:31:12", SkyBody.Callisto, 108.856406979, 38.132049899),
        ("2026-Oct-27 02:09:36", SkyBody.Callisto, 93.994665836, 23.437225549),
        ("2026-Dec-03 15:07:12", SkyBody.Callisto, 335.222568933, -26.398464886),
        ("2026-Jan-01 03:07:12", SkyBody.Titan, 329.011123519, -42.525546894),
        ("2026-Feb-07 16:04:48", SkyBody.Titan, 212.842946525, 35.697722305),
        ("2026-Mar-17 05:02:24", SkyBody.Titan, 78.202304299, -11.447545654),
        ("2026-Apr-23 18:00:00", SkyBody.Titan, 294.365695186, -19.404202368),
        ("2026-May-30 14:38:24", SkyBody.Titan, 281.480499989, -6.878425545),
        ("2026-Jul-07 03:36:00", SkyBody.Titan, 142.603413752, 40.391625513),
        ("2026-Aug-13 16:33:36", SkyBody.Titan, 29.487090519, -35.591896572),
        ("2026-Sep-20 05:31:12", SkyBody.Titan, 261.975689726, 10.710693306),
        ("2026-Oct-27 02:09:36", SkyBody.Titan, 252.452887536, 17.484994459),
        ("2026-Dec-03 15:07:12", SkyBody.Titan, 110.979917837, 19.520443292),
    ];

    /// <summary>
    /// Each big moon's offset from its planet on the sky against Horizons': within a tenth of its
    /// orbit's size (the circles leave out eccentricity and resonance).
    /// </summary>
    [Fact]
    public void Big_moons_sit_beside_their_planets_as_Horizons_has_them()
    {
        var bern = new Geodetic(46.9511, 7.4386, 550);
        static (double E, double N, double U) Dir(double az, double el) =>
            (Math.Sin(az * Deg) * Math.Cos(el * Deg), Math.Cos(az * Deg) * Math.Cos(el * Deg), Math.Sin(el * Deg));
        double worst = 0;
        foreach (var m in MoonRows)
        {
            var utc = DateTime.SpecifyKind(DateTime.ParseExact(m.Utc, "yyyy-MMM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
            var parent = Ephemeris.ParentOf(m.Body);
            var p = PlanetRows.First(r => r.Utc == m.Utc && r.Body == parent);
            var (pe, pn, pu) = Dir(p.Az, p.El);
            var (me, mn, mu) = Dir(m.Az, m.El);
            var hp = Ephemeris.Topocentric(parent, utc, bern);
            var hm = Ephemeris.Topocentric(m.Body, utc, bern);
            // the offsets moon - planet in the observer's frame, radians: Horizons' against ours
            double ex = me - pe, ey = mn - pn, ez = mu - pu;
            double cx = hm.East - hp.East, cy = hm.North - hp.North, cz = hm.Up - hp.Up;
            double err = Math.Sqrt((ex - cx) * (ex - cx) + (ey - cy) * (ey - cy) + (ez - cz) * (ez - cz));
            double orbit = Ephemeris.OrbitOf(m.Body) / hp.Distance;
            worst = Math.Max(worst, err / orbit);
            Assert.True(err < 0.1 * orbit, $"{m.Body} {m.Utc}: off by {err / orbit:P1} of its orbit");
        }
        Console.WriteLine($"worst {worst:P2} of an orbit");
    }

    [Fact]
    public void Swiss_civil_time_to_UTC()
    {
        static DateTime Utc(int mo, int d, int h, int mi) => new(2026, mo, d, h, mi, 0, DateTimeKind.Utc);
        Assert.Equal(Utc(6, 21, 10, 0), SwissTime.ToUtc(new DateOnly(2026, 6, 21), 12));
        Assert.Equal(Utc(12, 21, 11, 0), SwissTime.ToUtc(new DateOnly(2026, 12, 21), 12));
        // 2026: summer time from Sunday 29 March 01:00 UTC (02:00 CET) to Sunday 25 October 01:00 UTC (03:00 CEST)
        Assert.Equal(Utc(3, 29, 0, 30), SwissTime.ToUtc(new DateOnly(2026, 3, 29), 1.5));
        Assert.Equal(Utc(3, 29, 1, 30), SwissTime.ToUtc(new DateOnly(2026, 3, 29), 3.5));
        Assert.Equal(Utc(10, 24, 23, 30), SwissTime.ToUtc(new DateOnly(2026, 10, 25), 1.5));
        Assert.Equal(Utc(10, 25, 3, 0), SwissTime.ToUtc(new DateOnly(2026, 10, 25), 4));
        Assert.Equal(2, SwissTime.Offset(Utc(7, 1, 12, 0)));
        Assert.Equal(1, SwissTime.Offset(Utc(1, 1, 12, 0)));
        // the hour wraps into the next day
        Assert.Equal(Utc(1, 2, 0, 0), SwissTime.ToUtc(new DateOnly(2026, 1, 1), 25));
    }
}

/// <summary>The real sky in world axes (#1430, src/World/SunPath.cs): grid north, Bern's days.</summary>
public class RealSunPathTests
{
    /// <summary>
    /// World -Z is LV95 grid north. West of Bern true north leans east of it, east of Bern west:
    /// the meridian convergence, about (λ0 − λ)·sin φ.
    /// </summary>
    [Theory]
    [InlineData(2_500_000, 1_118_000, 6.143, 46.204)]    // Geneva
    [InlineData(2_830_000, 1_168_000, 10.447, 46.629)]   // Müstair
    [InlineData(2_600_000, 1_200_000, 7.4386, 46.951)]   // Bern
    public void Grid_azimuth_is_true_azimuth_turned_by_the_convergence(double e, double n, double lon, double lat)
    {
        var date = new DateOnly(2026, 8, 1);
        var sun = World.SunPath.Toward(SkyBody.Sun, date, 15, e, n, out float el);
        var geo = SwissGeodesy.ToWgs84(e, n, 500);
        var truth = Ephemeris.Topocentric(SkyBody.Sun, SwissTime.ToUtc(date, 15), geo);
        double gridAz = Math.Atan2(sun.X, -sun.Z) * 180 / Math.PI;
        double expected = (7.43958 - lon) * Math.Sin(lat * Math.PI / 180);
        Assert.Equal(expected, Math.IEEERemainder(gridAz - truth.AzimuthDeg, 360), 0.03);
        Assert.Equal(truth.ElevationDeg, el, 0.01);
        Assert.Equal(1f, sun.Length(), 1e-5);
    }

    /// <summary>
    /// The solstices' sunrise and sunset at Bern, Swiss civil time: JPL Horizons' R_T_S_ONLY with
    /// refraction, at sea level (at 550 m its horizon dips 0.75° and the day grows 12 minutes; the
    /// game's flat horizon has no dip). Horizons steps by the minute.
    /// </summary>
    [Theory]
    [InlineData(2026, 6, 21, 5 + 36 / 60.0, 21 + 29 / 60.0)]
    [InlineData(2026, 12, 21, 8 + 13 / 60.0, 16 + 44 / 60.0)]
    public void Bern_rises_and_sets_on_the_almanac(int y, int m, int d, double rise, double set)
    {
        var date = new DateOnly(y, m, d);
        Assert.Equal(rise, World.SunPath.RiseOrSet(date, rise: true), 0.04);
        Assert.Equal(set, World.SunPath.RiseOrSet(date, rise: false), 0.04);
    }
}
