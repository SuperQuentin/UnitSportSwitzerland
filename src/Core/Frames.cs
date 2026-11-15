using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>A rotation in doubles (#1847): its columns are one frame's axes seen in another's.</summary>
public readonly record struct DMat3(DVec3 C0, DVec3 C1, DVec3 C2)
{
    public static readonly DMat3 Identity = new(new DVec3(1, 0, 0), new DVec3(0, 1, 0), new DVec3(0, 0, 1));

    /// <summary>A vector of the inner frame in the outer one.</summary>
    public DVec3 Apply(DVec3 v) => C0 * v.X + C1 * v.Y + C2 * v.Z;

    /// <summary>A vector of the outer frame in the inner one (the transpose: a rotation's inverse).</summary>
    public DVec3 ApplyInverse(DVec3 v) => new(C0.Dot(v), C1.Dot(v), C2.Dot(v));
}

/// <summary>
/// A moment of the sky (#1847, docs/plans/planets.md section 2): <see cref="Utc"/> is when the bodies are (where
/// each one is on its orbit, how the Moon and the planets are turned), <see cref="EarthAngle"/> how the Earth is
/// turned (Greenwich's sidereal angle, radians). Real time has them in step (<see cref="At"/>); the game's 24-minute
/// day turns the Earth by its hour while the bodies keep their own pace, so they are two numbers. Both come from the
/// server-owned clocks, so every peer has the same moment.
///
/// <para>
/// <b>The local day</b> (#1992, docs/plans/planets.md section 2): standing on another body, that body takes the extra
/// turn the game's day gives the Earth (<see cref="LocalTurn"/>, radians about <see cref="Local"/>'s pole, in its own
/// spin's sense), so every body has a short day as the Earth has. A moon's orbit round its parent turns with it, so the
/// parent hangs still in its sky (tidal lock). It is this peer's view only: places on a body are body-fixed, the same on
/// every peer whatever its turn, and it fades to nothing before the body's sphere of influence ends
/// (<see cref="SkyClock.LocalWeight"/>), where a crossing re-expresses positions.
/// </para>
/// </summary>
public readonly record struct SkyMoment(DateTime Utc, double EarthAngle, BodyId Local = BodyId.Earth, double LocalTurn = 0)
{
    /// <summary>Whether <paramref name="body"/> takes the local day's turn at this moment.</summary>
    public bool Turns(BodyId body) => body == Local && LocalTurn != 0 && body != BodyId.Earth;

    /// <summary>The sky at a real instant: the bodies and the Earth's turn both at <paramref name="utc"/>.</summary>
    public static SkyMoment At(DateTime utc) => new(utc, Ephemeris.SiderealTime(utc));

    /// <summary>The sky the ground draws today (H6): a calendar date and a Swiss civil hour.</summary>
    public static SkyMoment Calendar(DateOnly date, double hour) => At(SwissTime.ToUtc(date, hour));
}

/// <summary>
/// The frame tree (#1847, docs/plans/planets.md section 3), doubles throughout. Its root is the Sun's inertial frame
/// (the ICRF's axes at the Sun's centre); every body has its centre placed from its parent's
/// (<see cref="Ephemeris.FromParent"/>) and its body-fixed axes (X toward 0° N 0° E, Y toward 90° E, Z its north
/// pole: ECEF for the Earth). A position on a body is three doubles in that body's fixed frame
/// (<see cref="GlobalPos"/>): the Earth's ECEF is the Earth's.
///
/// <para>
/// Offsets go up only to the two bodies' nearest common ancestor, so the Moon from the Earth never passes through
/// heliocentric metres (2·10¹¹ m, a 30 µm step): Earth-fixed to Moon-fixed and back holds to micrometres. The Sun's
/// "fixed" frame is its inertial one (nothing stands on it; a craft between the planets is held in it).
/// </para>
///
/// <para>
/// Spheres of influence: a place belongs to the deepest body whose sphere holds it (<see cref="SphereAt"/>), entered
/// at <see cref="CelestialBody.SphereOfInfluence"/> and left only past <see cref="LeaveFactor"/> times it.
/// </para>
/// </summary>
public static class Frames
{
    /// <summary>A sphere of influence is left only this far out of it, a share of its radius, so its edge never flickers.</summary>
    public const double LeaveFactor = 1.05;

    /// <summary>The half-step of the central difference that gives the bodies' velocities, seconds.</summary>
    public const double VelocityStep = 60;

    /// <summary><paramref name="body"/>'s centre from its parent's centre, ICRF axes, metres (zero for the Sun).</summary>
    public static DVec3 FromParent(BodyId body, DateTime utc) => body switch
    {
        BodyId.Sun => DVec3.Zero,
        BodyId.Earth => Ephemeris.EarthFromSun(utc),
        _ => Ephemeris.FromParent(CelestialBody.Of(body).Sky!.Value, utc),
    };

    /// <summary>
    /// <paramref name="body"/>'s centre from its parent's at a sky moment (#1992): a moon taking the local day's turn has
    /// its orbit turned with it about its own pole, so its parent stays where it was in its sky.
    /// </summary>
    public static DVec3 FromParent(BodyId body, SkyMoment m)
    {
        var v = FromParent(body, m.Utc);
        if (!m.Turns(body) || CelestialBody.Of(body).Parent is not { } parent || parent == BodyId.Sun) return v;
        var (pole, _, _) = Ephemeris.IcrfAxes(CelestialBody.Of(body).Sky!.Value, m.Utc);
        return Turn(v, pole, SignedTurn(body, m.LocalTurn));
    }

    /// <summary><paramref name="to"/>'s centre seen from <paramref name="from"/>'s, ICRF axes, metres, at a sky moment (the local day's turn included, #1992).</summary>
    public static DVec3 Offset(BodyId from, BodyId to, SkyMoment m) => Offset(from, to, m.Utc, m);

    /// <summary><paramref name="to"/>'s centre seen from <paramref name="from"/>'s, ICRF axes, metres: the true one (no local day's turn).</summary>
    public static DVec3 Offset(BodyId from, BodyId to, DateTime utc) => Offset(from, to, utc, null);

    private static DVec3 Offset(BodyId from, BodyId to, DateTime utc, SkyMoment? m)
    {
        if (from == to) return DVec3.Zero;
        var up = Ancestry(from);
        var down = Ancestry(to);
        // the nearest common ancestor: the first of `to`'s line that is also on `from`'s
        int common = down.FindIndex(up.Contains);
        var meet = down[common];
        var sum = DVec3.Zero;
        for (int i = 0; i < common; i++) sum += m is { } a ? FromParent(down[i], a) : FromParent(down[i], utc);
        foreach (var b in up)
        {
            if (b == meet) break;
            sum -= m is { } a ? FromParent(b, a) : FromParent(b, utc);
        }
        return sum;
    }

    /// <summary>The local day's turn in <paramref name="body"/>'s own spin's sense (#1992): Venus and Uranus turn backward.</summary>
    private static double SignedTurn(BodyId body, double turn) => CelestialBody.Of(body).Spin < 0 ? -turn : turn;

    /// <summary><paramref name="v"/> turned by <paramref name="angle"/> about the unit axis <paramref name="k"/> (Rodrigues).</summary>
    private static DVec3 Turn(DVec3 v, DVec3 k, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return v * c + k.Cross(v) * s + k * (k.Dot(v) * (1 - c));
    }

    /// <summary>
    /// How fast <paramref name="to"/>'s centre moves seen from <paramref name="from"/>'s, ICRF axes, metres per real
    /// second (a central difference over ±<see cref="VelocityStep"/> s): its true orbital velocity, ~1.02 km/s for the
    /// Moon from the Earth. What a sphere-of-influence handover takes off or gives back (section 2).
    /// </summary>
    public static DVec3 Velocity(BodyId from, BodyId to, DateTime utc) =>
        (Offset(from, to, utc.AddSeconds(VelocityStep)) - Offset(from, to, utc.AddSeconds(-VelocityStep))) / (2 * VelocityStep);

    /// <summary>
    /// <paramref name="body"/>'s fixed axes in the ICRF at a sky moment (columns: X, Y, Z). The Earth by the moment's
    /// own sidereal angle (<see cref="SkyMoment.EarthAngle"/>) and precession; the rest by their IAU models at
    /// <see cref="SkyMoment.Utc"/>; the Sun's are the ICRF's.
    /// </summary>
    public static DMat3 Orientation(BodyId body, SkyMoment m)
    {
        if (body == BodyId.Sun) return DMat3.Identity;
        if (body == BodyId.Earth)
        {
            // the ICRF's axes in ECEF are the rows of the ECEF axes in the ICRF
            var (x, y, z) = Ephemeris.CelestialAt(m.Utc, m.EarthAngle);
            return new DMat3(new DVec3(x.X, y.X, z.X), new DVec3(x.Y, y.Y, z.Y), new DVec3(x.Z, y.Z, z.Z));
        }
        var (pole, prime0, east0) = Ephemeris.IcrfAxes(CelestialBody.Of(body).Sky!.Value, m.Utc);
        DVec3 prime = prime0, east = east0;
        if (m.Turns(body))
        {
            // the local day (#1992): the body turned on about its pole, eastward in its own spin's sense
            double a = SignedTurn(body, m.LocalTurn), c = Math.Cos(a), s = Math.Sin(a);
            (prime, east) = (prime * c + east * s, east * c - prime * s);
        }
        return new DMat3(prime, east, pole);
    }

    /// <summary>A point of <paramref name="from"/>'s fixed frame in <paramref name="to"/>'s fixed frame, metres.</summary>
    public static DVec3 Convert(BodyId from, DVec3 p, BodyId to, SkyMoment m)
    {
        if (from == to) return p;
        var icrf = Orientation(from, m).Apply(p) - Offset(from, to, m);
        return Orientation(to, m).ApplyInverse(icrf);
    }

    /// <summary>A direction of <paramref name="from"/>'s fixed frame in <paramref name="to"/>'s (turned only).</summary>
    public static DVec3 ConvertDirection(BodyId from, DVec3 d, BodyId to, SkyMoment m) =>
        from == to ? d : Orientation(to, m).ApplyInverse(Orientation(from, m).Apply(d));

    /// <summary>
    /// The body whose sphere of influence holds a point given in <paramref name="current"/>'s fixed frame: up to the
    /// parent while past <see cref="LeaveFactor"/> times the sphere, then down into any child whose sphere holds it.
    /// </summary>
    public static BodyId SphereAt(BodyId current, DVec3 p, SkyMoment m)
    {
        var body = current;
        while (CelestialBody.Of(body).Parent is { } parent && p.Length > LeaveFactor * CelestialBody.Of(body).SphereOfInfluence)
        {
            p = Convert(body, p, parent, m);
            body = parent;
        }
        for (bool moved = true; moved;)
        {
            moved = false;
            foreach (var child in CelestialBody.All)
            {
                if (child.Parent != body) continue;
                var q = Convert(body, p, child.Id, m);
                if (q.Length >= child.SphereOfInfluence) continue;
                body = child.Id;
                p = q;
                moved = true;
                break;
            }
        }
        return body;
    }

    /// <summary>
    /// The nearest surface to a point given in <paramref name="body"/>'s fixed frame (#1850): among the body, its
    /// children, its parent and its parent's other children, the one whose surface is closest, and how far it is
    /// (metres; the height over the ellipsoid for <paramref name="body"/> itself, the distance to the centre less the
    /// equatorial radius for the others; never below 0). What the spectator's speed and the cruise drive's
    /// v = lever · d / τ measure (section 8); a giant's "surface" is its 1-bar level, the Sun's its photosphere.
    /// </summary>
    public static (BodyId Body, double Distance) NearestSurface(BodyId body, DVec3 p, SkyMoment m)
    {
        var self = CelestialBody.Of(body);
        var best = (body, Math.Max(0, self.Shape.ToGeodetic(p.AsEcef).H));
        void Try(BodyId other)
        {
            if (other == body) return;
            double d = Math.Max(0, Convert(body, p, other, m).Length - CelestialBody.Of(other).Shape.A);
            if (d < best.Item2) best = (other, d);
        }
        foreach (var b in CelestialBody.All)
            if (b.Parent == body || (self.Parent is { } parent && (b.Id == parent || b.Parent == parent)))
                Try(b.Id);
        return best;
    }

    /// <summary>The body, its parent, its parent's parent, up to the Sun.</summary>
    private static List<BodyId> Ancestry(BodyId body)
    {
        var line = new List<BodyId>(4) { body };
        while (CelestialBody.Of(line[^1]).Parent is { } p) line.Add(p);
        return line;
    }
}
