using System;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>The bodies <see cref="Ephemeris"/> places.</summary>
public enum SkyBody { Sun, Moon, Mercury, Venus, Mars, Jupiter, Saturn, Uranus, Neptune, Io, Europa, Ganymede, Callisto, Titan }

/// <summary>
/// A body as seen from a place: a unit vector in the local east / north / up of the ellipsoid
/// (true north, not LV95 grid north), and how far it is. No refraction: the geometric sky.
/// </summary>
public readonly record struct Horizontal(double East, double North, double Up, double Distance)
{
    /// <summary>Degrees clockwise from true north, 0..360.</summary>
    public double AzimuthDeg => (Math.Atan2(East, North) * 180 / Math.PI + 360) % 360;

    /// <summary>Degrees above the horizon (negative below it).</summary>
    public double ElevationDeg => Math.Asin(Math.Clamp(Up, -1, 1)) * 180 / Math.PI;
}

/// <summary>
/// Where the sun and the moon are (#1430, phase H6 of <c>docs/plans/ellipsoid-handover.md</c>):
/// Meeus, <i>Astronomical Algorithms</i> (2nd ed.). The sun by chapter 25's low-precision theory
/// (~0.01°), the moon by chapter 47's series (ELP-2000/82 truncated, ~10″ in longitude), nutation
/// by chapter 22's short form and sidereal time by chapter 12. Topocentric through the Earth-fixed
/// frame, so the moon's parallax (up to a degree) is in, and a place in orbit works as well as one
/// on the ground. Polar motion is ignored (~10 m).
///
/// <para>
/// Checked against JPL Horizons at four Swiss towns (<c>EphemerisTests</c>): the sun within
/// 0.008° and 1e-4 AU, the moon within 0.0013° and 40 km (the series is truncated in distance).
/// </para>
/// </summary>
public static partial class Ephemeris
{
    public const double AuMetres = 149_597_870_700.0;

    /// <summary>TT − UT around 2026, seconds. Moves by ~1 s a decade; at the moon's pace 1 s is 0.0005°.</summary>
    public const double DeltaT = 69.2;

    private const double Deg = Math.PI / 180;

    private static readonly DateTime J2000 = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The Julian day of a UTC instant.</summary>
    public static double JulianDay(DateTime utc) => 2451545.0 + (utc - J2000).Ticks / (double)TimeSpan.TicksPerDay;

    /// <summary>
    /// The body's geocentric apparent place of date: right ascension and declination (radians, the
    /// true equator and equinox of date) and its distance in metres.
    /// </summary>
    public static (double Ra, double Dec, double Distance) Equatorial(SkyBody body, DateTime utc)
    {
        double t = (JulianDay(utc) + DeltaT / 86400.0 - 2451545.0) / 36525.0;
        if (body >= SkyBody.Io) return SatelliteEquatorial(body, t);
        if (body > SkyBody.Moon) return PlanetEquatorial(body, t);
        var (dPsi, eps) = Nutation(t);
        var (lon, lat, dist) = body == SkyBody.Sun ? Sun(t) : Moon(t);
        lon += dPsi;
        double sl = Math.Sin(lon), cl = Math.Cos(lon), se = Math.Sin(eps), ce = Math.Cos(eps);
        double ra = Math.Atan2(sl * ce - Math.Tan(lat) * se, cl);
        double dec = Math.Asin(Math.Sin(lat) * ce + Math.Cos(lat) * se * sl);
        return (ra, dec, dist);
    }

    /// <summary>The body in the Earth-fixed frame (the true equator of date, Greenwich), metres.</summary>
    public static EcefPos Ecef(SkyBody body, DateTime utc)
    {
        var (ra, dec, dist) = Equatorial(body, utc);
        double h = ra - SiderealTime(utc);
        double cd = Math.Cos(dec);
        return new EcefPos(dist * cd * Math.Cos(h), dist * cd * Math.Sin(h), dist * Math.Sin(dec));
    }

    /// <summary>The body as seen from <paramref name="at"/> (WGS84 latitude, longitude, ellipsoidal height).</summary>
    public static Horizontal Topocentric(SkyBody body, DateTime utc, Geodetic at) =>
        Local(Ecef(body, utc) - Ellipsoid.Wgs84.ToEcef(at), at);

    /// <summary>An Earth-fixed offset turned into the east / north / up of a place, normalised, with its length.</summary>
    public static Horizontal Local(EcefPos d, Geodetic at)
    {
        double phi = at.LatDeg * Deg, lam = at.LonDeg * Deg;
        double sp = Math.Sin(phi), cp = Math.Cos(phi), sl = Math.Sin(lam), cl = Math.Cos(lam);
        double e = -sl * d.X + cl * d.Y;
        double n = -sp * cl * d.X - sp * sl * d.Y + cp * d.Z;
        double u = cp * cl * d.X + cp * sl * d.Y + sp * d.Z;
        double len = Math.Sqrt(e * e + n * n + u * u);
        return new Horizontal(e / len, n / len, u / len, len);
    }

    /// <summary>
    /// The moon's own axes in the Earth-fixed frame (#1440): <c>Pole</c> its north pole, <c>Prime</c>
    /// the point 0° N 0° E of its surface (the near side's centre), <c>East</c> 90° E, all unit
    /// vectors. The IAU WGCCRE 2009 rotation model (pole and prime meridian with their periodic
    /// terms, in the ICRF), precessed to the equator of date (Meeus 21.3) and turned by sidereal
    /// time. Libration falls out of it: the sub-Earth point wanders ±7° round 0° / 0°.
    /// </summary>
    public static (EcefPos Pole, EcefPos Prime, EcefPos East) MoonAxes(DateTime utc)
    {
        double d = JulianDay(utc) + DeltaT / 86400.0 - 2451545.0, t = d / 36525.0;
        double E(double a, double b) => (a + b * d) * Deg;
        double e1 = E(125.045, -0.0529921), e2 = E(250.089, -0.1059842), e3 = E(260.008, 13.0120009),
            e4 = E(176.625, 13.3407154), e5 = E(357.529, 0.9856003), e6 = E(311.589, 26.4057084),
            e7 = E(134.963, 13.0649930), e8 = E(276.617, 0.3287146), e9 = E(34.226, 1.7484877),
            e10 = E(15.134, -0.1589763), e11 = E(119.743, 0.0036096), e12 = E(239.961, 0.1643573),
            e13 = E(25.053, 12.9590088);
        double a0 = 269.9949 + 0.0031 * t - 3.8787 * Math.Sin(e1) - 0.1204 * Math.Sin(e2) + 0.0700 * Math.Sin(e3)
            - 0.0172 * Math.Sin(e4) + 0.0072 * Math.Sin(e6) - 0.0052 * Math.Sin(e10) + 0.0043 * Math.Sin(e13);
        double d0 = 66.5392 + 0.0130 * t + 1.5419 * Math.Cos(e1) + 0.0239 * Math.Cos(e2) - 0.0278 * Math.Cos(e3)
            + 0.0068 * Math.Cos(e4) - 0.0029 * Math.Cos(e6) + 0.0009 * Math.Cos(e7) + 0.0008 * Math.Cos(e10)
            - 0.0009 * Math.Cos(e13);
        double w = 38.3213 + 13.17635815 * d - 1.4e-12 * d * d + 3.5610 * Math.Sin(e1) + 0.1208 * Math.Sin(e2)
            - 0.0642 * Math.Sin(e3) + 0.0158 * Math.Sin(e4) + 0.0252 * Math.Sin(e5) - 0.0066 * Math.Sin(e6)
            - 0.0047 * Math.Sin(e7) - 0.0046 * Math.Sin(e8) + 0.0028 * Math.Sin(e9) + 0.0052 * Math.Sin(e10)
            + 0.0040 * Math.Sin(e11) + 0.0019 * Math.Sin(e12) - 0.0044 * Math.Sin(e13);
        return Frame(a0, d0, w, utc, t);
    }

    /// <summary>
    /// A body's axes in the Earth-fixed frame from its IAU pole (<paramref name="a0"/>, <paramref name="d0"/>,
    /// ICRF, degrees) and prime meridian angle <paramref name="w"/> (degrees), precessed to the date
    /// (<paramref name="t"/>, Julian centuries) and turned by sidereal time at <paramref name="utc"/>.
    /// </summary>
    private static (EcefPos Pole, EcefPos Prime, EcefPos East) Frame(double a0, double d0, double w, DateTime utc, double t)
    {
        a0 *= Deg; d0 *= Deg; w *= Deg;

        // ICRF: the pole, the node of the body's equator on the ICRF equator, the prime meridian W past it
        var pole = new EcefPos(Math.Cos(d0) * Math.Cos(a0), Math.Cos(d0) * Math.Sin(a0), Math.Sin(d0));
        var node = new EcefPos(-Math.Sin(a0), Math.Cos(a0), 0);
        var across = Cross(pole, node);
        var prime = Scale(node, Math.Cos(w)) + Scale(across, Math.Sin(w));

        double gast = SiderealTime(utc);
        EcefPos ToEarth(EcefPos v) => Spin(Precess(v, t), -gast);
        var p = ToEarth(pole);
        var m = ToEarth(prime);
        return (p, m, Cross(p, m));
    }

    /// <summary>A J2000 (ICRF) direction to the mean equator and equinox of date (Meeus 21.3).</summary>
    private static EcefPos Precess(EcefPos v, double t)
    {
        double zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) / 3600 * Deg;
        double z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) / 3600 * Deg;
        double theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) / 3600 * Deg;
        double ra = Math.Atan2(v.Y, v.X), dec = Math.Asin(Math.Clamp(v.Z, -1, 1));
        double a = Math.Cos(dec) * Math.Sin(ra + zeta);
        double b = Math.Cos(theta) * Math.Cos(dec) * Math.Cos(ra + zeta) - Math.Sin(theta) * Math.Sin(dec);
        double c = Math.Sin(theta) * Math.Cos(dec) * Math.Cos(ra + zeta) + Math.Cos(theta) * Math.Sin(dec);
        double ra2 = Math.Atan2(a, b) + z, dec2 = Math.Asin(Math.Clamp(c, -1, 1));
        return new EcefPos(Math.Cos(dec2) * Math.Cos(ra2), Math.Cos(dec2) * Math.Sin(ra2), Math.Sin(dec2));
    }

    /// <summary>Turned by <paramref name="angle"/> about Z.</summary>
    private static EcefPos Spin(EcefPos v, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new EcefPos(c * v.X - s * v.Y, s * v.X + c * v.Y, v.Z);
    }

    private static EcefPos Cross(EcefPos a, EcefPos b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static EcefPos Scale(EcefPos a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    /// <summary>
    /// The fraction of the moon's disc that is lit, 0 (new) to 1 (full), and whether it is waxing.
    /// Geocentric; from the ground the difference is under a percent.
    /// </summary>
    public static (double Lit, bool Waxing) MoonPhase(DateTime utc)
    {
        double t = (JulianDay(utc) + DeltaT / 86400.0 - 2451545.0) / 36525.0;
        var (sunLon, _, sunDist) = Sun(t);
        var (moonLon, moonLat, moonDist) = Moon(t);
        // the elongation, then the phase angle at the moon (Meeus 48.2, 48.3)
        double psi = Math.Acos(Math.Cos(moonLat) * Math.Cos(moonLon - sunLon));
        double i = Math.Atan2(sunDist * Math.Sin(psi), moonDist - sunDist * Math.Cos(psi));
        double ahead = Math.IEEERemainder(moonLon - sunLon, 2 * Math.PI);
        return ((1 + Math.Cos(i)) / 2, ahead > 0);
    }

    /// <summary>Greenwich apparent sidereal time, radians (Meeus 12.4, plus the equation of the equinoxes).</summary>
    public static double SiderealTime(DateTime utc)
    {
        double jd = JulianDay(utc);
        double t = (jd - 2451545.0) / 36525.0;
        double gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0) + 0.000387933 * t * t - t * t * t / 38710000.0;
        var (dPsi, eps) = Nutation(t);
        return Math.IEEERemainder(gmst * Deg + dPsi * Math.Cos(eps), 2 * Math.PI);
    }

    /// <summary>Nutation in longitude and the true obliquity, radians (Meeus 22, the 0.5″ form).</summary>
    private static (double DPsi, double Eps) Nutation(double t)
    {
        double om = (125.04452 - 1934.136261 * t) * Deg;
        double l = (280.4665 + 36000.7698 * t) * Deg;
        double lm = (218.3165 + 481267.8813 * t) * Deg;
        double dPsi = -17.20 * Math.Sin(om) - 1.32 * Math.Sin(2 * l) - 0.23 * Math.Sin(2 * lm) + 0.21 * Math.Sin(2 * om);
        double dEps = 9.20 * Math.Cos(om) + 0.57 * Math.Cos(2 * l) + 0.10 * Math.Cos(2 * lm) - 0.09 * Math.Cos(2 * om);
        double eps0 = 23.439291111 - 0.013004167 * t - 1.6389e-7 * t * t + 5.0361e-7 * t * t * t;
        return (dPsi / 3600 * Deg, eps0 * Deg + dEps / 3600 * Deg);
    }

    /// <summary>
    /// The sun's geometric longitude less aberration (still without nutation), latitude 0, and its
    /// distance in metres (Meeus 25.2-25.5).
    /// </summary>
    private static (double Lon, double Lat, double Dist) Sun(double t)
    {
        double l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        double m = (357.52911 + 35999.05029 * t - 0.0001537 * t * t) * Deg;
        double e = 0.016708634 - 0.000042037 * t - 0.0000001267 * t * t;
        double c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m)
            + (0.019993 - 0.000101 * t) * Math.Sin(2 * m) + 0.000289 * Math.Sin(3 * m);
        double nu = m + c * Deg;
        double r = 1.000001018 * (1 - e * e) / (1 + e * Math.Cos(nu));
        double aberration = -20.4898 / 3600 / r;
        return ((l0 + c + aberration) * Deg, 0, r * AuMetres);
    }

    /// <summary>The moon's longitude (without nutation), latitude and distance in metres (Meeus 47).</summary>
    private static (double Lon, double Lat, double Dist) Moon(double t)
    {
        double t2 = t * t, t3 = t2 * t, t4 = t3 * t;
        double lp = 218.3164477 + 481267.88123421 * t - 0.0015786 * t2 + t3 / 538841 - t4 / 65194000;
        double d = (297.8501921 + 445267.1114034 * t - 0.0018819 * t2 + t3 / 545868 - t4 / 113065000) * Deg;
        double m = (357.5291092 + 35999.0502909 * t - 0.0001536 * t2 + t3 / 24490000) * Deg;
        double mp = (134.9633964 + 477198.8675055 * t + 0.0087414 * t2 + t3 / 69699 - t4 / 14712000) * Deg;
        double f = (93.2720950 + 483202.0175233 * t - 0.0036539 * t2 - t3 / 3526000 + t4 / 863310000) * Deg;
        double a1 = (119.75 + 131.849 * t) * Deg, a2 = (53.09 + 479264.290 * t) * Deg, a3 = (313.45 + 481266.484 * t) * Deg;
        double ecc = 1 - 0.002516 * t - 0.0000074 * t2;
        double lpr = lp * Deg;

        double sl = 0, sr = 0, sb = 0;
        foreach (var (cd, cm, cmp, cf, l, r) in LonDist)
        {
            double arg = cd * d + cm * m + cmp * mp + cf * f;
            double k = cm == 0 ? 1 : cm is 1 or -1 ? ecc : ecc * ecc;
            sl += l * k * Math.Sin(arg);
            sr += r * k * Math.Cos(arg);
        }
        foreach (var (cd, cm, cmp, cf, b) in Lat)
        {
            double k = cm == 0 ? 1 : cm is 1 or -1 ? ecc : ecc * ecc;
            sb += b * k * Math.Sin(cd * d + cm * m + cmp * mp + cf * f);
        }
        sl += 3958 * Math.Sin(a1) + 1962 * Math.Sin(lpr - f) + 318 * Math.Sin(a2);
        sb += -2235 * Math.Sin(lpr) + 382 * Math.Sin(a3) + 175 * Math.Sin(a1 - f) + 175 * Math.Sin(a1 + f)
            + 127 * Math.Sin(lpr - mp) - 115 * Math.Sin(lpr + mp);

        return ((lp + sl / 1e6) * Deg, sb / 1e6 * Deg, (385000.56 + sr / 1000) * 1000);
    }

    /// <summary>Meeus table 47.A: multiples of D, M, M′, F; the longitude (10⁻⁶ °) and distance (m) terms.</summary>
    private static readonly (int D, int M, int Mp, int F, int L, int R)[] LonDist =
    [
        (0, 0, 1, 0, 6288774, -20905355), (2, 0, -1, 0, 1274027, -3699111), (2, 0, 0, 0, 658314, -2955968),
        (0, 0, 2, 0, 213618, -569925), (0, 1, 0, 0, -185116, 48888), (0, 0, 0, 2, -114332, -3149),
        (2, 0, -2, 0, 58793, 246158), (2, -1, -1, 0, 57066, -152138), (2, 0, 1, 0, 53322, -170733),
        (2, -1, 0, 0, 45758, -204586), (0, 1, -1, 0, -40923, -129620), (1, 0, 0, 0, -34720, 108743),
        (0, 1, 1, 0, -30383, 104755), (2, 0, 0, -2, 15327, 10321), (0, 0, 1, 2, -12528, 0),
        (0, 0, 1, -2, 10980, 79661), (4, 0, -1, 0, 10675, -34782), (0, 0, 3, 0, 10034, -23210),
        (4, 0, -2, 0, 8548, -21636), (2, 1, -1, 0, -7888, 24208), (2, 1, 0, 0, -6766, 30824),
        (1, 0, -1, 0, -5163, -8379), (1, 1, 0, 0, 4987, -16675), (2, -1, 1, 0, 4036, -12831),
        (2, 0, 2, 0, 3994, -10445), (4, 0, 0, 0, 3861, -11650), (2, 0, -3, 0, 3665, 14403),
        (0, 1, -2, 0, -2689, -7003), (2, 0, -1, 2, -2602, 0), (2, -1, -2, 0, 2390, 10056),
        (1, 0, 1, 0, -2348, 6322), (2, -2, 0, 0, 2236, -9884), (0, 1, 2, 0, -2120, 5751),
        (0, 2, 0, 0, -2069, 0), (2, -2, -1, 0, 2048, -4950), (2, 0, 1, -2, -1773, 4130),
        (2, 0, 0, 2, -1595, 0), (4, -1, -1, 0, 1215, -3958), (0, 0, 2, 2, -1110, 0),
        (3, 0, -1, 0, -892, 3258), (2, 1, 1, 0, -810, 2616), (4, -1, -2, 0, 759, -1897),
        (0, 2, -1, 0, -713, -2117), (2, 2, -1, 0, -700, 2354), (2, 1, -2, 0, 691, 0),
        (2, -1, 0, -2, 596, 0), (4, 0, 1, 0, 549, -1423), (0, 0, 4, 0, 537, -1117),
        (4, -1, 0, 0, 520, -1571), (1, 0, -2, 0, -487, -1739), (2, 1, 0, -2, -399, 0),
        (0, 0, 2, -2, -381, -4421), (1, 1, 1, 0, 351, 0), (3, 0, -2, 0, -340, 0),
        (4, 0, -3, 0, 330, 0), (2, -1, 2, 0, 327, 0), (0, 2, 1, 0, -323, 1165),
        (1, 1, -1, 0, 299, 0), (2, 0, 3, 0, 294, 0), (2, 0, -1, -2, 0, 8752),
    ];

    /// <summary>Meeus table 47.B: multiples of D, M, M′, F; the latitude terms (10⁻⁶ °).</summary>
    private static readonly (int D, int M, int Mp, int F, int B)[] Lat =
    [
        (0, 0, 0, 1, 5128122), (0, 0, 1, 1, 280602), (0, 0, 1, -1, 277693), (2, 0, 0, -1, 173237),
        (2, 0, -1, 1, 55413), (2, 0, -1, -1, 46271), (2, 0, 0, 1, 32573), (0, 0, 2, 1, 17198),
        (2, 0, 1, -1, 9266), (0, 0, 2, -1, 8822), (2, -1, 0, -1, 8216), (2, 0, -2, -1, 4324),
        (2, 0, 1, 1, 4200), (2, 1, 0, -1, -3359), (2, -1, -1, 1, 2463), (2, -1, 0, 1, 2211),
        (2, -1, -1, -1, 2065), (0, 1, -1, -1, -1870), (4, 0, -1, -1, 1828), (0, 1, 0, 1, -1794),
        (0, 0, 0, 3, -1749), (0, 1, -1, 1, -1565), (1, 0, 0, 1, -1491), (0, 1, 1, 1, -1475),
        (0, 1, 1, -1, -1410), (0, 1, 0, -1, -1344), (1, 0, 0, -1, -1335), (0, 0, 3, 1, 1107),
        (4, 0, 0, -1, 1021), (4, 0, -1, 1, 833), (0, 0, 1, -3, 777), (4, 0, -2, 1, 671),
        (2, 0, 0, -3, 607), (2, 0, 2, -1, 596), (2, -1, 1, -1, 491), (2, 0, -2, 1, -451),
        (0, 0, 3, -1, 439), (2, 0, 2, 1, 422), (2, 0, -3, -1, 421), (2, 1, -1, 1, -366),
        (2, 1, 0, 1, -351), (4, 0, 0, 1, 331), (2, -1, 1, 1, 315), (2, -2, 0, -1, 302),
        (0, 0, 1, 3, -283), (2, 1, 1, -1, -229), (1, 1, 0, -1, 223), (1, 1, 0, 1, 223),
        (0, 1, -2, -1, -220), (2, 1, -1, -1, -220), (1, 0, 1, 1, -185), (2, -1, -2, -1, 181),
        (0, 1, 2, 1, -177), (4, 0, -2, -1, 176), (4, -1, -1, -1, 166), (1, 0, 1, -1, -164),
        (4, 0, 1, -1, 132), (1, 0, -1, -1, -119), (4, -1, 0, -1, 115), (2, -2, 0, 1, 107),
    ];
}
