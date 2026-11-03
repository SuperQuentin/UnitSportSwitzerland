using UnitSport.Terrain.Format;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>A vector of doubles: positions, velocities and accelerations of the orbit integrator (#1430 H5).</summary>
public readonly record struct DVec3(double X, double Y, double Z)
{
    public static readonly DVec3 Zero = new(0, 0, 0);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public DVec3 Normalized()
    {
        double l = Length;
        return l > 0 ? this / l : Zero;
    }

    public double Dot(DVec3 o) => X * o.X + Y * o.Y + Z * o.Z;
    public DVec3 Cross(DVec3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);

    public static DVec3 operator +(DVec3 a, DVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static DVec3 operator -(DVec3 a, DVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static DVec3 operator -(DVec3 a) => new(-a.X, -a.Y, -a.Z);
    public static DVec3 operator *(DVec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static DVec3 operator *(double k, DVec3 a) => a * k;
    public static DVec3 operator /(DVec3 a, double k) => new(a.X / k, a.Y / k, a.Z / k);

    public static implicit operator DVec3(EcefPos p) => new(p.X, p.Y, p.Z);
    public EcefPos AsEcef => new(X, Y, Z);

    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}

/// <summary>A craft's state in the inertial frame (ECI), metres and m/s, doubles.</summary>
public readonly record struct OrbitState(DVec3 R, DVec3 V)
{
    /// <summary>Specific orbital energy, J/kg: v²/2 − GM/r. Constant on a coast; what the drift test measures.</summary>
    public double Energy(Planet p) => V.LengthSquared / 2 - p.GM / R.Length;
}

/// <summary>
/// Orbits in doubles (#1430 H5, docs/plans/ellipsoid-handover.md section 5). The inertial frame (ECI) has
/// ECEF's axes at sim time 0 and does not turn: ECEF = ECI turned by the Earth rotation angle ω·t about Z,
/// t the shared sim time (<see cref="SimClock"/>), so every peer and every time scale agrees on it.
/// Gravity is the point mass GM/r² toward the centre; velocity Verlet steps it (symplectic: over many orbits
/// the energy wanders but never drifts away).
/// </summary>
public static class Orbit
{
    /// <summary>The Earth rotation angle at a sim time, radians (one sidereal turn in 86,164 s).</summary>
    public static double RotationAngle(Planet p, double simSeconds) => Math.IEEERemainder(p.RotationRate * simSeconds, 2 * Math.PI);

    /// <summary>Gravity at an inertial (or Earth-fixed: it is radial) position, m/s².</summary>
    public static DVec3 Gravity(Planet p, DVec3 r)
    {
        double r2 = r.LengthSquared;
        double rl = Math.Sqrt(r2);
        return r * (-p.GM / (r2 * rl));
    }

    /// <summary>
    /// One velocity-Verlet step of <paramref name="dt"/> seconds under gravity plus a constant
    /// <paramref name="thrust"/> acceleration (m/s², inertial axes) held over the step.
    /// </summary>
    public static OrbitState Step(Planet p, in OrbitState s, DVec3 thrust, double dt)
    {
        var a0 = Gravity(p, s.R) + thrust;
        var r = s.R + s.V * dt + a0 * (0.5 * dt * dt);
        var a1 = Gravity(p, r) + thrust;
        return new OrbitState(r, s.V + (a0 + a1) * (0.5 * dt));
    }

    /// <summary>An inertial vector at a sim angle as Earth-fixed (a direction, or a position).</summary>
    public static DVec3 ToEcef(DVec3 eci, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new DVec3(c * eci.X + s * eci.Y, -s * eci.X + c * eci.Y, eci.Z);
    }

    /// <summary>An Earth-fixed vector at a sim angle as inertial.</summary>
    public static DVec3 ToEci(DVec3 ecef, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new DVec3(c * ecef.X - s * ecef.Y, s * ecef.X + c * ecef.Y, ecef.Z);
    }

    /// <summary>
    /// The state seen from the turning Earth: position, and the velocity relative to the ground (the
    /// inertial one less ω × r: 317 m/s eastward at Bern's latitude for a craft standing still on it).
    /// </summary>
    public static (DVec3 R, DVec3 V) ToEcef(Planet p, in OrbitState s, double angle)
    {
        var spin = new DVec3(0, 0, p.RotationRate);
        return (ToEcef(s.R, angle), ToEcef(s.V - spin.Cross(s.R), angle));
    }

    /// <summary>An Earth-fixed position and ground-relative velocity as an inertial state (adds ω × r).</summary>
    public static OrbitState FromEcef(Planet p, DVec3 r, DVec3 v, double angle)
    {
        var spin = new DVec3(0, 0, p.RotationRate);
        var ri = ToEci(r, angle);
        return new OrbitState(ri, ToEci(v, angle) + spin.Cross(ri));
    }

    /// <summary>
    /// A circular orbit through <paramref name="r"/> (inertial), flying along <paramref name="toward"/> as
    /// projected on the horizontal: speed √(GM/r). Prograde (east) is the cheap way round.
    /// </summary>
    public static OrbitState Circular(Planet p, DVec3 r, DVec3 toward)
    {
        var up = r.Normalized();
        var along = toward - up * toward.Dot(up);
        // straight up or down asked: east
        if (along.LengthSquared < 1e-12) along = new DVec3(0, 0, 1).Cross(up);
        if (along.LengthSquared < 1e-12) along = new DVec3(0, 1, 0);
        return new OrbitState(r, along.Normalized() * p.CircularSpeed(r.Length));
    }

    /// <summary>Orbital period of a circular or elliptic orbit of semi-major axis <paramref name="a"/>, s: 5,554 at 400 km.</summary>
    public static double Period(Planet p, double a) => 2 * Math.PI * Math.Sqrt(a * a * a / p.GM);

    /// <summary>
    /// The orbit's lowest and highest distances from the centre, metres (apoapsis infinite when it escapes),
    /// and its semi-major axis (negative on an escape).
    /// </summary>
    public static (double Periapsis, double Apoapsis, double A) Apsides(Planet p, in OrbitState s)
    {
        double energy = s.Energy(p);
        double a = -p.GM / (2 * energy);
        double h2 = s.R.Cross(s.V).LengthSquared;
        double e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h2 / (p.GM * p.GM)));
        double peri = h2 / (p.GM * (1 + e));
        double apo = e < 1 ? a * (1 + e) : double.PositiveInfinity;
        return (peri, apo, a);
    }
}
