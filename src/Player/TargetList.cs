using System;
using System.Globalization;
using UnitSport.Core;

namespace UnitSport.Player;

// Plain C# (linked into the unit tests).

/// <summary>
/// The bodies a spectator can aim at (#1850, docs/plans/planets.md section 8) and how the cue reads them: the order a
/// tap steps through, the distance and the time to go at the speed flown. The far markers (players on other bodies,
/// C3b) join the list later.
/// </summary>
public static class TargetList
{
    /// <summary>The order a tap steps through: home and the near ones first, then the Sun, the planets, their moons.</summary>
    public static readonly BodyId[] Order =
    [
        BodyId.Earth, BodyId.Moon, BodyId.Mars, BodyId.Sun, BodyId.Mercury, BodyId.Venus, BodyId.Jupiter, BodyId.Io,
        BodyId.Europa, BodyId.Ganymede, BodyId.Callisto, BodyId.Saturn, BodyId.Titan, BodyId.Uranus, BodyId.Neptune,
    ];

    /// <summary>
    /// The target after <paramref name="current"/>: the next body in <see cref="Order"/> but the one the anchor is on;
    /// after the last, none (null), then the first again.
    /// </summary>
    public static BodyId? Next(BodyId? current, BodyId anchor)
    {
        int start = current is { } c ? Array.IndexOf(Order, c) + 1 : 0;
        for (int i = start; i < Order.Length; i++)
            if (Order[i] != anchor) return Order[i];
        return null;
    }

    /// <summary>A distance to a surface, as the cue reads it: metres, km, thousands and millions of km, AU.</summary>
    public static string Distance(double m)
    {
        var inv = CultureInfo.InvariantCulture;
        double km = m / 1000;
        return m switch
        {
            < 1000 => m.ToString("0", inv) + " m",
            < 100_000 => km.ToString("0.0", inv) + " km",
            < 1e9 => km.ToString("#,0", inv).Replace(",", " ") + " km",
            < 0.1 * Au => (km / 1e6).ToString("0.0", inv) + " million km",
            _ => (m / Au).ToString("0.00", inv) + " AU",
        };
    }

    /// <summary>
    /// The trip's time at full speed, seconds: the spectator's own rule (<see cref="SpectatorSpeed.SurfaceCap"/>, half the
    /// distance to the nearest surface a second, 3 km/s to light speed) from <paramref name="startAltitudeM"/> over the body
    /// left to <paramref name="arriveM"/> over the target, <paramref name="gapM"/> between the two surfaces: speeding up
    /// on the way out, slowing on the way in, the nearer surface ruling. The Moon from 400 km: about half a minute.
    /// </summary>
    public static double TripSeconds(double gapM, double startAltitudeM = 400_000, double arriveM = 100_000)
    {
        double from = Math.Max(startAltitudeM, 1), to = gapM - arriveM;
        if (to <= from) return 0;
        // dt = dx / v(min(x, gap - x)), integrated in steps even in log of the distance to the nearer surface
        double t = 0;
        const int steps = 400;
        double mid = gapM / 2;
        foreach (var (a, b) in new[] { (from, Math.Max(from, Math.Min(mid, to))), (Math.Max(arriveM, gapM - to), Math.Max(arriveM, gapM - Math.Max(mid, from))) })
        {
            if (b <= a) continue;
            double la = Math.Log(a), lb = Math.Log(b);
            for (int i = 0; i < steps; i++)
            {
                double x0 = Math.Exp(la + (lb - la) * i / steps), x1 = Math.Exp(la + (lb - la) * (i + 1) / steps);
                t += (x1 - x0) / SpectatorSpeed.SurfaceCap(0.5 * (x0 + x1));
            }
        }
        return t;
    }

    /// <summary>A time to go at a speed, as the cue reads it: seconds to days; a dash at a standstill.</summary>
    public static string Time(double distanceM, double speedMps)
    {
        if (speedMps < 0.01 || !double.IsFinite(distanceM / speedMps)) return "—";
        return Duration(distanceM / speedMps);
    }

    /// <summary>A duration as the cue reads it: seconds to days.</summary>
    public static string Duration(double s)
    {
        var inv = CultureInfo.InvariantCulture;
        return s switch
        {
            < 60 => s.ToString("0", inv) + " s",
            < 3600 => (s / 60).ToString("0", inv) + " min",
            < 86_400 * 2 => (s / 3600).ToString("0.0", inv) + " h",
            _ => (s / 86_400).ToString("0", inv) + " days",
        };
    }

    /// <summary>The astronomical unit, metres.</summary>
    public const double Au = 1.495978707e11;
}
