using System;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// Where the camera is, as the debug overlay's top-right line writes it (#2033): LV95 E,N only inside Switzerland's LV95
/// extent near the ground; the body's latitude, longitude and true height over its reference everywhere else on or round
/// it (abroad, in orbit, out to its sphere of influence); past that the body's distance from its centre, or the Sun's.
/// The height is the geodetic one, never the world frame's Y (which a space anchor keeps near the camera: it read a
/// frozen "alt 10000 m" in orbit).
/// </summary>
public static class HudPlace
{
    /// <summary>LV95's extent written as LV95: Switzerland's box, metres (swissALTI3D's tiles, a few km of margin).</summary>
    public const double MinE = 2_480_000, MaxE = 2_840_000, MinN = 1_070_000, MaxN = 1_300_000;

    /// <summary>LV95 is written below this height over the ellipsoid, metres; higher, latitude and longitude.</summary>
    /// <remarks>Also the bound under which a caller asks for LV95 at all (with a longitude near Switzerland's: the
    /// projection has no meaning round the antimeridian).</remarks>
    public const double Lv95Ceiling = 20_000;

    /// <summary>Whether an LV95 place at a height over the ellipsoid is written as LV95.</summary>
    public static bool InLv95(double e, double n, double h) =>
        e >= MinE && e <= MaxE && n >= MinN && n <= MaxN && h < Lv95Ceiling;

    /// <summary>
    /// The line's place part. <paramref name="body"/> is the body the frame is on (the Sun far out), <paramref name="g"/>
    /// its geodetic place, <paramref name="lv95"/> the LV95 E,N and LHN95 height (above sea level) on the Earth near
    /// Switzerland (null elsewhere), <paramref name="fromCentre"/> the distance from the body's centre, metres.
    /// </summary>
    public static string Place(CelestialBody body, Geodetic g, (double East, double North, double Alt)? lv95, double fromCentre)
    {
        var ci = CultureInfo.InvariantCulture;
        if (body.Id == BodyId.Sun || fromCentre > body.SphereOfInfluence)
        {
            // far from every body: the nearest's (the frame's) distance from its centre; the Sun's in AU
            return body.Id == BodyId.Sun
                ? string.Format(ci, "Sun-centred {0:F4} AU", fromCentre / Ephemeris.AuMetres)
                : string.Format(ci, "{0} {1:N0} km from its centre", body.Name, fromCentre / 1000);
        }
        if (body.Id == BodyId.Earth && lv95 is { } p && InLv95(p.East, p.North, g.H))
            return string.Format(ci, "LV95 {0:F0},{1:F0}  alt {2:F0} m", p.East, p.North, p.Alt);
        return string.Format(ci, "{0} {1:F3}°{2} {3:F3}°{4}  h {5}", body.Name, Math.Abs(g.LatDeg), g.LatDeg >= 0 ? 'N' : 'S',
            Math.Abs(g.LonDeg), g.LonDeg >= 0 ? 'E' : 'W', Height(g.H));
    }

    /// <summary>A height: metres near the ground, km from 100 km up (an orbit), thousands separated.</summary>
    public static string Height(double h) => Math.Abs(h) < 100_000
        ? string.Format(CultureInfo.InvariantCulture, "{0:F0} m", h)
        : string.Format(CultureInfo.InvariantCulture, "{0:N0} km", h / 1000);
}
