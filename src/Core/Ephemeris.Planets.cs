using System;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// The planets (#1444): JPL's approximate Keplerian elements (Standish, "Approximate Positions of
/// the Planets", table 1, valid 1800-2050, the mean ecliptic and equinox of J2000), the Earth as
/// the Earth-Moon barycentre less the moon's share, light time, annual aberration, then precessed to
/// the equator of date like the sun and the moon. Against JPL Horizons from Bern: within ~0.1° for
/// Jupiter and Saturn (the table's own error), better for the others (<c>EphemerisTests</c>).
/// </summary>
public static partial class Ephemeris
{
    /// <summary>The speed of light, au per day.</summary>
    private const double LightAuPerDay = 173.1446327;

    /// <summary>The Earth's mass over the moon's plus one: how far the barycentre is from the Earth's centre, as a share of the moon's distance.</summary>
    private const double EarthMoonRatio = 1 + 81.30056;

    /// <summary>
    /// a (au), e, I (deg), L (deg), long. of perihelion (deg), long. of node (deg), then each one's
    /// rate per Julian century. Mercury to Neptune, then the Earth-Moon barycentre.
    /// </summary>
    private static readonly double[][] Elements =
    [
        [0.38709927, 0.20563593, 7.00497902, 252.25032350, 77.45779628, 48.33076593,
            0.00000037, 0.00001906, -0.00594749, 149472.67411175, 0.16047689, -0.12534081],
        [0.72333566, 0.00677672, 3.39467605, 181.97909950, 131.60246718, 76.67984255,
            0.00000390, -0.00004107, -0.00078890, 58517.81538729, 0.00268329, -0.27769418],
        [1.52371034, 0.09339410, 1.84969142, -4.55343205, -23.94362959, 49.55953891,
            0.00001847, 0.00007882, -0.00813131, 19140.30268499, 0.44441088, -0.29257343],
        [5.20288700, 0.04838624, 1.30439695, 34.39644051, 14.72847983, 100.47390909,
            -0.00011607, -0.00013253, -0.00183714, 3034.74612775, 0.21252668, 0.20469106],
        [9.53667594, 0.05386179, 2.48599187, 49.95424423, 92.59887831, 113.66242448,
            -0.00125060, -0.00050991, 0.00193609, 1222.49362201, -0.41897216, -0.28867794],
        [19.18916464, 0.04725744, 0.77263783, 313.23810451, 170.95427630, 74.01692503,
            -0.00196176, -0.00004397, -0.00242939, 428.48202785, 0.40805281, 0.04240589],
        [30.06992276, 0.00859048, 1.77004347, -55.12002969, 44.96476227, 131.78422574,
            0.00026291, 0.00005105, 0.00035372, 218.45945325, -0.32241464, -0.00508664],
        [1.00000261, 0.01671123, -0.00001531, 100.46457166, 102.93768193, 0.0,
            0.00000562, -0.00004392, -0.01294668, 35999.37244981, 0.32327364, 0.0],
    ];

    private const int Barycentre = 7;

    /// <summary>The obliquity of J2000, the frame the elements are in.</summary>
    private static readonly double ObliquityJ2000 = 23.43928 * Deg;

    /// <summary>
    /// A body's own axes in the Earth-fixed frame, as <see cref="MoonAxes"/>: the IAU WGCCRE rotation
    /// models (2015; the gas giants' prime meridians are their System III), taken at the moment the
    /// light we see left the planet (Jupiter turns 26° in its 43 light-minutes).
    /// </summary>
    public static (Terrain.Format.EcefPos Pole, Terrain.Format.EcefPos Prime, Terrain.Format.EcefPos East) Axes(SkyBody body, DateTime utc) =>
        Axes(body, SkyMoment.At(utc));

    /// <summary>A body's axes in the Earth-fixed frame at a sky moment (#1850), as <see cref="Axes(SkyBody, DateTime)"/>.</summary>
    public static (Terrain.Format.EcefPos Pole, Terrain.Format.EcefPos Prime, Terrain.Format.EcefPos East) Axes(SkyBody body, SkyMoment m)
    {
        if (body == SkyBody.Moon) return MoonAxes(m);
        body = ParentOf(body);   // a big moon: its planet's frame (it keeps a face to it)
        double d = JulianDay(m.Utc) + DeltaT / 86400.0 - 2451545.0;
        if (body != SkyBody.Sun) d -= Equatorial(body, m.Utc).Distance / AuMetres / LightAuPerDay;
        double t = d / 36525.0;
        var (a0, d0, w) = Rotation(body, d, t);
        return Frame(a0, d0, w, m.EarthAngle, t);
    }

    /// <summary>The IAU pole (ICRF right ascension, declination) and prime meridian angle, degrees, at <paramref name="d"/> days and <paramref name="t"/> centuries from J2000.</summary>
    private static (double A0, double D0, double W) Rotation(SkyBody body, double d, double t)
    {
        double n = (357.85 + 52.316 * t) * Deg;   // Neptune's pole wobble
        return body switch
        {
            SkyBody.Sun => (286.13, 63.87, 84.176 + 14.1844 * d),
            SkyBody.Mercury => (281.0103 - 0.0328 * t, 61.4155 - 0.0049 * t, 329.5988 + 6.1385108 * d),
            SkyBody.Venus => (272.76, 67.16, 160.20 - 1.4813688 * d),
            SkyBody.Mars => (317.68143 - 0.1061 * t, 52.88650 - 0.0609 * t, 176.630 + 350.89198226 * d),
            SkyBody.Jupiter => (268.056595 - 0.006499 * t, 64.495303 + 0.002413 * t, 284.95 + 870.5360000 * d),
            SkyBody.Saturn => (40.589 - 0.036 * t, 83.537 - 0.004 * t, 38.90 + 810.7939024 * d),
            SkyBody.Uranus => (257.311, -15.175, 203.81 - 501.1600928 * d),
            _ => (299.36 + 0.70 * Math.Sin(n), 43.46 - 0.51 * Math.Cos(n), 249.978 + 541.1397757 * d - 0.48 * Math.Sin(n)),
        };
    }

    /// <summary>
    /// The big moons (#1444) as circles in their planet's equator: the semi-major axis (m), their
    /// longitude from the node of that equator on the ICRF's (degrees, fitted to JPL Horizons on
    /// 2026-01-01 0h TDB), their mean motion (degrees a day) and their radius (m). Good to a few
    /// percent of their orbit: their eccentricities (Titan's 0.03) and resonances are left out. Mars' two (#1853): fitted
    /// the same way, Phobos within 2° (its 0.015 eccentricity), Deimos within 0.05°; their 1° inclinations left out.
    /// </summary>
    private static readonly (SkyBody Parent, double A, double Lambda0, double Rate, double Radius)[] Satellites =
    [
        (SkyBody.Jupiter, 421_700_000, 333.294, 360 / 1.769137786, 1_821_600),
        (SkyBody.Jupiter, 671_034_000, 278.787, 360 / 3.551181041, 1_560_800),
        (SkyBody.Jupiter, 1_070_412_000, 343.003, 360 / 7.15455296, 2_634_100),
        (SkyBody.Jupiter, 1_882_709_000, 91.443, 360 / 16.6890184, 2_410_300),
        (SkyBody.Saturn, 1_221_870_000, 209.374, 360 / 15.945421, 2_574_730),
        (SkyBody.Mars, 9_376_000, 210.4, 360 / 0.31891023, 11_080),
        (SkyBody.Mars, 23_458_000, 18.56, 360 / 1.26244, 6_200),
    ];

    private const double SatelliteEpoch = 2461041.5;

    /// <summary>The planet a big moon goes round, or the body itself.</summary>
    public static SkyBody ParentOf(SkyBody body) => body >= SkyBody.Io ? Satellites[body - SkyBody.Io].Parent : body;

    /// <summary>A big moon's orbit radius, metres (0 for anything else).</summary>
    public static double OrbitOf(SkyBody body) => body >= SkyBody.Io ? Satellites[body - SkyBody.Io].A : 0;

    /// <summary>A big moon's apparent place: its planet's, plus its place on its circle as the light left it.</summary>
    private static (double Ra, double Dec, double Distance) SatelliteEquatorial(SkyBody body, double t)
    {
        var s = Satellites[body - SkyBody.Io];
        var (ra, dec, dist) = PlanetEquatorial(s.Parent, t);
        double tLeft = t - dist / AuMetres / LightAuPerDay / 36525.0;
        double d = tLeft * 36525.0;
        var (a0, d0, _) = Rotation(s.Parent, d, tLeft);
        a0 *= Deg;
        d0 *= Deg;
        var pole = new Terrain.Format.EcefPos(Math.Cos(d0) * Math.Cos(a0), Math.Cos(d0) * Math.Sin(a0), Math.Sin(d0));
        var node = new Terrain.Format.EcefPos(-Math.Sin(a0), Math.Cos(a0), 0);
        var across = Cross(pole, node);
        double lam = (s.Lambda0 + s.Rate * (d + 2451545.0 - SatelliteEpoch)) * Deg;
        var dir = Precess(Scale(node, Math.Cos(lam)) + Scale(across, Math.Sin(lam)), tLeft);
        var planet = new Terrain.Format.EcefPos(dist * Math.Cos(dec) * Math.Cos(ra), dist * Math.Cos(dec) * Math.Sin(ra), dist * Math.Sin(dec));
        var g = planet + Scale(dir, s.A);
        double len = g.Length;
        return (Math.Atan2(g.Y, g.X), Math.Asin(g.Z / len), len);
    }

    /// <summary>
    /// The ICRF (J2000 equator and equinox) axes in the Earth-fixed frame at <paramref name="utc"/>:
    /// precessed to the date and turned by sidereal time. The stars' catalogue places go through it.
    /// </summary>
    public static (Terrain.Format.EcefPos X, Terrain.Format.EcefPos Y, Terrain.Format.EcefPos Z) Celestial(SkyMoment m) =>
        CelestialAt(m.Utc, m.EarthAngle);

    public static (Terrain.Format.EcefPos X, Terrain.Format.EcefPos Y, Terrain.Format.EcefPos Z) Celestial(DateTime utc)
    {
        double t = (JulianDay(utc) + DeltaT / 86400.0 - 2451545.0) / 36525.0;
        double gast = SiderealTime(utc);
        Terrain.Format.EcefPos F(double x, double y, double z) => Spin(Precess(new Terrain.Format.EcefPos(x, y, z), t), -gast);
        return (F(1, 0, 0), F(0, 1, 0), F(0, 0, 1));
    }

    /// <summary>
    /// A planet's apparent visual magnitude, roughly (Mallama and Hilton's absolute magnitudes and
    /// phase terms; Saturn's rings by their tilt): what sizes its point by eye. Moons by their
    /// planet's distance, their own absolute magnitude.
    /// </summary>
    public static double Magnitude(SkyBody body, DateTime utc)
    {
        double t = (JulianDay(utc) + DeltaT / 86400.0 - 2451545.0) / 36525.0;
        var earth = EarthHeliocentric(t);
        int i = (ParentOf(body) - SkyBody.Mercury);
        var p = Heliocentric(i, t);
        double r = p.Length, delta = (p - earth).Length;
        // the phase angle at the planet, degrees
        double cos = Dot(p, p - earth) / (r * delta);
        double a = Math.Acos(Math.Clamp(cos, -1, 1)) / Deg;
        double h = body switch
        {
            SkyBody.Mercury => -0.613 + 0.06328 * a - 0.0016336 * a * a + 3.3644e-5 * a * a * a - 3.4265e-7 * a * a * a * a,
            SkyBody.Venus => -4.384 - 0.001044 * a + 3.687e-4 * a * a - 2.814e-6 * a * a * a + 8.938e-9 * a * a * a * a,
            SkyBody.Mars => -1.601 + 0.02267 * a - 1.302e-4 * a * a,
            SkyBody.Jupiter => -9.395 - 3.7e-4 * a + 6.16e-4 * a * a,
            SkyBody.Saturn => -8.914 - 1.825 * RingTilt(utc) + 0.026 * a,
            SkyBody.Uranus => -7.110 + 0.00009 * a,
            SkyBody.Neptune => -7.00,
            SkyBody.Io => -1.68, SkyBody.Europa => -1.41, SkyBody.Ganymede => -2.09, SkyBody.Callisto => -1.05,
            SkyBody.Phobos => 11.8, SkyBody.Deimos => 12.89,
            _ => -1.28,   // Titan
        };
        return h + 5 * Math.Log10(r * delta);
    }

    /// <summary>|sin| of the Earth's latitude over Saturn's equator: how open its rings are, 0..1.</summary>
    private static double RingTilt(DateTime utc)
    {
        var (pole, _, _) = Axes(SkyBody.Saturn, utc);
        var v = Ecef(SkyBody.Saturn, utc);
        return Math.Abs(Dot(pole, v) / v.Length);
    }

    private static double Dot(Terrain.Format.EcefPos a, Terrain.Format.EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>A body's equatorial and polar radii, metres (IAU): the gas giants are visibly flattened.</summary>
    public static (double Equator, double Pole) Radii(SkyBody body) => body switch
    {
        SkyBody.Sun => (695_700_000, 695_700_000),
        SkyBody.Moon => (1_737_400, 1_737_400),
        SkyBody.Mercury => (2_440_530, 2_438_260),
        SkyBody.Venus => (6_051_800, 6_051_800),
        SkyBody.Mars => (3_396_190, 3_376_200),
        SkyBody.Jupiter => (71_492_000, 66_854_000),
        SkyBody.Saturn => (60_268_000, 54_364_000),
        SkyBody.Uranus => (25_559_000, 24_973_000),
        SkyBody.Neptune => (24_764_000, 24_341_000),
        _ => (Satellites[body - SkyBody.Io].Radius, Satellites[body - SkyBody.Io].Radius),
    };

    /// <summary>A planet's apparent place of date: right ascension, declination (radians) and its distance (m).</summary>
    private static (double Ra, double Dec, double Distance) PlanetEquatorial(SkyBody body, double t)
    {
        int i = body - SkyBody.Mercury;
        var earth = EarthHeliocentric(t);
        // light time: where the planet was when the light left it
        var p = Heliocentric(i, t);
        var d = p - earth;
        for (int pass = 0; pass < 2; pass++)
        {
            double tau = d.Length / LightAuPerDay;
            p = Heliocentric(i, t - tau / 36525.0);
            d = p - earth;
        }
        double distance = d.Length;
        // annual aberration: the Earth's velocity over the speed of light, added to the direction
        const double h = 0.5;   // days
        var v = Scale(EarthHeliocentric(t + h / 36525.0) - EarthHeliocentric(t - h / 36525.0), 1 / (2 * h));
        var u = Scale(d, 1 / distance) + Scale(v, 1 / LightAuPerDay);
        // the J2000 ecliptic to its equator, then to the equator of date
        double ce = Math.Cos(ObliquityJ2000), se = Math.Sin(ObliquityJ2000);
        var eq = new Terrain.Format.EcefPos(u.X, u.Y * ce - u.Z * se, u.Y * se + u.Z * ce);
        eq = Scale(eq, 1 / eq.Length);
        var date = Precess(eq, t);
        return (Math.Atan2(date.Y, date.X), Math.Asin(Math.Clamp(date.Z, -1, 1)), distance * AuMetres);
    }

    /// <summary>The Earth's centre, au, J2000 ecliptic: the barycentre less the moon's share.</summary>
    private static Terrain.Format.EcefPos EarthHeliocentric(double t)
    {
        var bary = Heliocentric(Barycentre, t);
        var (lon, lat, dist) = Moon(t);
        double r = dist / AuMetres / EarthMoonRatio;
        return bary - new Terrain.Format.EcefPos(r * Math.Cos(lat) * Math.Cos(lon), r * Math.Cos(lat) * Math.Sin(lon), r * Math.Sin(lat));
    }

    /// <summary>Planet <paramref name="i"/>'s heliocentric place, au, the J2000 ecliptic (Standish's recipe).</summary>
    private static Terrain.Format.EcefPos Heliocentric(int i, double t)
    {
        var el = Elements[i];
        double a = el[0] + el[6] * t, e = el[1] + el[7] * t, inc = (el[2] + el[8] * t) * Deg;
        double l = el[3] + el[9] * t, peri = el[4] + el[10] * t, node = (el[5] + el[11] * t) * Deg;
        double w = peri * Deg - node, m = Math.IEEERemainder((l - peri) * Deg, 2 * Math.PI);
        // Kepler's equation by Newton's method
        double ecc = m + e * Math.Sin(m);
        for (int k = 0; k < 8; k++)
        {
            double step = (ecc - e * Math.Sin(ecc) - m) / (1 - e * Math.Cos(ecc));
            ecc -= step;
            if (Math.Abs(step) < 1e-12) break;
        }
        double x = a * (Math.Cos(ecc) - e), y = a * Math.Sqrt(1 - e * e) * Math.Sin(ecc);
        double cw = Math.Cos(w), sw = Math.Sin(w), cn = Math.Cos(node), sn = Math.Sin(node), ci = Math.Cos(inc), si = Math.Sin(inc);
        return new Terrain.Format.EcefPos(
            (cw * cn - sw * sn * ci) * x + (-sw * cn - cw * sn * ci) * y,
            (cw * sn + sw * cn * ci) * x + (-sw * sn + cw * cn * ci) * y,
            sw * si * x + cw * si * y);
    }
}
