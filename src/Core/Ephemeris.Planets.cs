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
