using System;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// The ephemeris' geometric side, for the frame tree (#1847, <see cref="Frames"/>): where each body's centre is from
/// its parent's and how its own axes stand, in the ICRF (the J2000 equator and equinox), metres. No light time, no
/// aberration, no nutation: where a body <i>is</i>, which is where a craft flies to, not where its light seems to come
/// from (that stays the sky's, <see cref="Equatorial"/>). The same series as the sky's, so the two agree to the light
/// time (the moon: 1.3 s of its motion, 1.3 km; Mars at 1 au: 8 min of it).
/// </summary>
public static partial class Ephemeris
{
    /// <summary>Julian centuries of TT from J2000 at a UTC instant.</summary>
    private static double Centuries(DateTime utc) => (JulianDay(utc) + DeltaT / 86400.0 - 2451545.0) / 36525.0;

    /// <summary>
    /// <paramref name="body"/>'s centre from its parent's (the Sun's for a planet, the Earth's for the moon, its planet's
    /// for a big moon), ICRF axes, metres. The Sun's is zero; the Earth is <see cref="EarthFromSun"/>.
    /// </summary>
    public static EcefPos FromParent(SkyBody body, DateTime utc)
    {
        double t = Centuries(utc);
        return body switch
        {
            SkyBody.Sun => new EcefPos(0, 0, 0),
            SkyBody.Moon => MoonFromEarth(t),
            >= SkyBody.Io => SatelliteFromPlanet(body, t),
            _ => EclipticToIcrf(Heliocentric(body - SkyBody.Mercury, t), AuMetres),
        };
    }

    /// <summary>The Earth's centre from the Sun's (the barycentre less the moon's share), ICRF axes, metres.</summary>
    public static EcefPos EarthFromSun(DateTime utc) => EclipticToIcrf(EarthHeliocentric(Centuries(utc)), AuMetres);

    /// <summary>
    /// A body's own axes in the ICRF, as unit vectors: <c>Pole</c> its north pole, <c>Prime</c> 0° N 0° E of its
    /// surface, <c>East</c> 90° E. The IAU rotation models of <see cref="Axes"/>, at the instant itself (no light
    /// time). A big moon keeps a face to its planet: its pole is the planet's, its prime meridian points at it.
    /// </summary>
    public static (EcefPos Pole, EcefPos Prime, EcefPos East) IcrfAxes(SkyBody body, DateTime utc)
    {
        double t = Centuries(utc), d = t * 36525.0;
        EcefPos pole, prime;
        if (body >= SkyBody.Io)
        {
            var parent = Satellites[body - SkyBody.Io].Parent;
            var (pa0, pd0, _) = Rotation(parent, d, t);
            (pole, _) = IcrfPolePrime(pa0, pd0, 0);
            var toPlanet = SatelliteFromPlanet(body, t);
            var inward = Scale(toPlanet, -1);
            double along = Dot(inward, pole);
            prime = inward - Scale(pole, along);
            prime = Scale(prime, 1 / prime.Length);
        }
        else
        {
            var (a0, d0, w) = body == SkyBody.Moon ? MoonRotation(d, t) : Rotation(body, d, t);
            (pole, prime) = IcrfPolePrime(a0, d0, w);
        }
        return (pole, prime, Cross(pole, prime));
    }

    /// <summary>
    /// The ICRF axes in the Earth-fixed frame, as <see cref="Celestial"/>, but turned by a given Greenwich sidereal
    /// angle (radians) rather than the one of <paramref name="utc"/>: the sky clock's Earth (<see cref="SkyMoment"/>).
    /// </summary>
    public static (EcefPos X, EcefPos Y, EcefPos Z) CelestialAt(DateTime utc, double gast)
    {
        double t = Centuries(utc);
        EcefPos F(double x, double y, double z) => Spin(Precess(new EcefPos(x, y, z), t), -gast);
        return (F(1, 0, 0), F(0, 1, 0), F(0, 0, 1));
    }

    /// <summary>A J2000 ecliptic vector (au, or any unit) in the ICRF's equatorial axes, times <paramref name="scale"/>.</summary>
    private static EcefPos EclipticToIcrf(EcefPos v, double scale)
    {
        double ce = Math.Cos(ObliquityJ2000), se = Math.Sin(ObliquityJ2000);
        return new EcefPos(v.X * scale, (v.Y * ce - v.Z * se) * scale, (v.Y * se + v.Z * ce) * scale);
    }

    /// <summary>
    /// The moon's centre from the Earth's, ICRF, metres: Meeus 47's longitude and latitude (the mean ecliptic and
    /// equinox of date) to the mean equator of date by the mean obliquity, then precessed back to J2000.
    /// </summary>
    private static EcefPos MoonFromEarth(double t)
    {
        var (lon, lat, dist) = Moon(t);
        double eps = MeanObliquity(t);
        double cl = Math.Cos(lat), x = cl * Math.Cos(lon), y = cl * Math.Sin(lon), z = Math.Sin(lat);
        double ce = Math.Cos(eps), se = Math.Sin(eps);
        var ofDate = new EcefPos(x, y * ce - z * se, y * se + z * ce);
        return Scale(Unprecess(ofDate, t), dist);
    }

    /// <summary>The mean obliquity of the ecliptic of date, radians (Meeus 22.2, as <see cref="Nutation"/> has it).</summary>
    private static double MeanObliquity(double t) =>
        (23.439291111 - 0.013004167 * t - 1.6389e-7 * t * t + 5.0361e-7 * t * t * t) * Deg;

    /// <summary>A direction of the mean equator of date back to J2000: <see cref="Precess"/>'s transpose.</summary>
    private static EcefPos Unprecess(EcefPos v, double t)
    {
        var cx = Precess(new EcefPos(1, 0, 0), t);
        var cy = Precess(new EcefPos(0, 1, 0), t);
        var cz = Precess(new EcefPos(0, 0, 1), t);
        return new EcefPos(Dot(cx, v), Dot(cy, v), Dot(cz, v));
    }

    /// <summary>A big moon's centre from its planet's, ICRF, metres: its circle in the planet's equator, at the instant.</summary>
    private static EcefPos SatelliteFromPlanet(SkyBody body, double t)
    {
        var s = Satellites[body - SkyBody.Io];
        double d = t * 36525.0;
        var (a0, d0, _) = Rotation(s.Parent, d, t);
        a0 *= Deg;
        d0 *= Deg;
        var pole = new EcefPos(Math.Cos(d0) * Math.Cos(a0), Math.Cos(d0) * Math.Sin(a0), Math.Sin(d0));
        var node = new EcefPos(-Math.Sin(a0), Math.Cos(a0), 0);
        var across = Cross(pole, node);
        double lam = (s.Lambda0 + s.Rate * (d + 2451545.0 - SatelliteEpoch)) * Deg;
        return Scale(Scale(node, Math.Cos(lam)) + Scale(across, Math.Sin(lam)), s.A);
    }
}
