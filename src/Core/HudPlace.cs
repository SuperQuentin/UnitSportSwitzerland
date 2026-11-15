using System;
using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// Where the camera is, as the debug overlay's top-right line writes it (#2033): LV95 E,N only inside Switzerland (a coarse
/// border) near the ground; the body's latitude, longitude and true height over its reference everywhere else on or round
/// it (abroad, in orbit, out to its sphere of influence); past that the body's distance from its centre, or the Sun's.
/// The height is the geodetic one, never the world frame's Y (which a space anchor keeps near the camera: it read a
/// frozen "alt 10000 m" in orbit).
/// </summary>
public static class HudPlace
{
    /// <summary>
    /// Switzerland's border, coarse (a few km), LV95 kilometres E,N clockwise from Chancy: the Lake of Geneva's middle, the
    /// Valais' French and Italian borders, Ticino, the Grisons, the Rhine and Lake Constance, Schaffhausen, Basel, the Jura.
    /// Chamonix, 7 km past Col de Balme, is outside.
    /// </summary>
    private static readonly (double E, double N)[] Border =
    [
        (2485, 1113), (2488, 1121), (2496, 1129), (2500, 1136), (2495, 1145), (2500, 1165), (2525, 1195), (2547, 1222),
        (2565, 1255), (2570, 1262), (2590, 1258), (2612, 1272), (2640, 1268), (2665, 1272), (2680, 1285), (2690, 1295),
        (2705, 1290), (2730, 1280), (2745, 1272), (2760, 1262), (2758, 1240), (2757, 1222), (2770, 1213), (2790, 1208),
        (2810, 1200), (2828, 1205), (2832, 1192), (2828, 1168), (2818, 1150), (2808, 1125), (2795, 1132), (2770, 1136),
        (2760, 1133), (2748, 1146), (2738, 1140), (2733, 1118), (2730, 1098), (2727, 1082), (2722, 1076), (2715, 1080),
        (2709, 1090), (2700, 1100), (2697, 1110), (2685, 1125), (2680, 1132), (2672, 1148), (2660, 1137), (2654, 1115),
        (2648, 1104), (2633, 1086), (2617, 1090), (2595, 1080), (2579, 1079), (2571, 1086), (2563, 1097), (2560, 1110),
        (2555, 1121), (2556, 1137), (2540, 1139), (2520, 1135), (2508, 1130), (2507, 1117), (2498, 1110),
    ];

    /// <summary>Whether an LV95 place (metres) is in Switzerland, by the coarse border.</summary>
    public static bool InSwitzerland(double e, double n)
    {
        double x = e / 1000, y = n / 1000;
        bool inside = false;
        for (int i = 0, k = Border.Length - 1; i < Border.Length; k = i++)
        {
            var (xi, yi) = Border[i];
            var (xk, yk) = Border[k];
            if ((yi > y) != (yk > y) && x < (xk - xi) * (y - yi) / (yk - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>LV95 is written below this height over the ellipsoid, metres; higher, latitude and longitude.</summary>
    /// <remarks>Also the bound under which a caller asks for LV95 at all (with a longitude near Switzerland's: the
    /// projection has no meaning round the antimeridian).</remarks>
    public const double Lv95Ceiling = 20_000;

    /// <summary>Whether an LV95 place at a height over the ellipsoid is written as LV95.</summary>
    public static bool InLv95(double e, double n, double h) => h < Lv95Ceiling && InSwitzerland(e, n);

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
