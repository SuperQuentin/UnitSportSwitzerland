using System;
using System.Globalization;
using UnitSport.Core;

namespace UnitSport.Items;

// Plain C# (linked into the unit tests).

/// <summary>
/// What the telescope can be pointed at from its menu (#1530) and what the info HUD says of it: the
/// moon, the planets and the big moons (never the sun: a telescope on it blinds), each read from the
/// ephemeris (<see cref="Ephemeris"/>): magnitude, distance, apparent size, the lit share and where it
/// stands above the horizon.
/// </summary>
public static class TelescopeTargets
{
    /// <summary>The menu, in order: the moon, the planets outwards, then the moons of Jupiter and Titan.</summary>
    public static readonly SkyBody[] Menu =
    [
        SkyBody.Moon, SkyBody.Mercury, SkyBody.Venus, SkyBody.Mars, SkyBody.Jupiter, SkyBody.Saturn, SkyBody.Uranus, SkyBody.Neptune,
        SkyBody.Io, SkyBody.Europa, SkyBody.Ganymede, SkyBody.Callisto, SkyBody.Titan,
    ];

    /// <summary>The menu entry <paramref name="by"/> places on from <paramref name="current"/> (none: the first, or the last going back), wrapping round.</summary>
    public static SkyBody Step(SkyBody? current, int by)
    {
        int i = current == null ? -1 : Array.IndexOf(Menu, current.Value);
        if (i < 0) return by >= 0 ? Menu[0] : Menu[^1];
        return Menu[((i + by) % Menu.Length + Menu.Length) % Menu.Length];
    }

    /// <summary>The name in the menu and the readout.</summary>
    public static string Name(SkyBody body) => body == SkyBody.Moon ? "Moon" : body.ToString();

    /// <summary>A target's readings. <c>Lit</c> is 0..1; <c>Waxing</c> is only known for the moon.</summary>
    public readonly record struct Info(double Magnitude, double DistanceM, double ArcSec, double Lit, bool? Waxing);

    /// <summary>
    /// Azimuth (degrees clockwise from the world's −Z, 0..360) and altitude (degrees, negative below) of a world direction:
    /// east +X, up +Y. The view turns the azimuth to true north at the scope (#1504).
    /// </summary>
    public static (double Azimuth, double Altitude) Horizon(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        double alt = len > 0 ? Math.Asin(Math.Clamp(y / len, -1, 1)) * 180 / Math.PI : 0;
        return ((Math.Atan2(x, -z) * 180 / Math.PI + 360) % 360, alt);
    }

    /// <summary>What the ephemeris gives of <paramref name="body"/> at <paramref name="utc"/>, geocentric.</summary>
    public static Info Read(SkyBody body, DateTime utc)
    {
        var p = Ephemeris.Ecef(body, utc);
        var s = Ephemeris.Ecef(SkyBody.Sun, utc);
        double dist = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        // the phase angle at the body, between the sun and the Earth
        double sx = s.X - p.X, sy = s.Y - p.Y, sz = s.Z - p.Z, sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        double cos = (-p.X * sx - p.Y * sy - p.Z * sz) / (dist * sl);
        double lit = (1 + Math.Clamp(cos, -1, 1)) / 2;
        double mag;
        bool? waxing = null;
        if (body == SkyBody.Moon)
        {
            // Allen: the full moon is -12.74, dimmer with the phase angle
            double a = Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
            mag = -12.73 + 0.026 * a + 4e-9 * Math.Pow(a, 4) + 5 * Math.Log10(dist / 384_400_000.0);
            waxing = Ephemeris.MoonPhase(utc).Waxing;
        }
        else mag = Ephemeris.Magnitude(body, utc);
        double arcsec = 2 * Math.Atan(Ephemeris.Radii(body).Equator / dist) * 180 / Math.PI * 3600;
        return new Info(mag, dist, arcsec, lit, waxing);
    }

    /// <summary>
    /// The info HUD's text: six or seven short lines. <paramref name="horizon"/> is the target's place
    /// (<see cref="Horizon"/>), or null when the sky shows none (the line then says so).
    /// </summary>
    public static string Format(SkyBody body, Info i, (double Azimuth, double Altitude)? horizon)
    {
        var c = CultureInfo.InvariantCulture;
        string distance = i.DistanceM < 1e10 ? $"{(i.DistanceM / 1000).ToString("#,0", c)} km" : $"{(i.DistanceM / Ephemeris.AuMetres).ToString("0.000", c)} AU";
        string size = i.ArcSec >= 120 ? $"{(i.ArcSec / 60).ToString("0.0", c)}'" : $"{i.ArcSec.ToString("0.0", c)}\"";
        string lit = $"lit {(i.Lit * 100).ToString("0", c)} %{(i.Waxing is { } w ? (w ? ", waxing" : ", waning") : "")}";
        return $"{Name(body)}\nmagnitude {i.Magnitude.ToString("+0.0;-0.0", c)}\ndistance {distance}\nsize {size}\n{lit}\n{Where(horizon)}";
    }

    /// <summary>The altitude / azimuth lines, or "not in the sky".</summary>
    public static string Where((double Azimuth, double Altitude)? horizon)
    {
        if (horizon is not { } h) return "not in the sky";
        var c = CultureInfo.InvariantCulture;
        return $"altitude {h.Altitude.ToString("+0.0;-0.0", c)}°, azimuth {h.Azimuth.ToString("0", c)}°" + (h.Altitude < 0 ? "\nbelow the horizon" : "");
    }
}
