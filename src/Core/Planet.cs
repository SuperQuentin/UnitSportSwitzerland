using UnitSport.Terrain.Format;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// A body the world can be on (#1430, docs/plans/ellipsoid-handover.md): its shape, how hard it pulls
/// and how fast it turns. Only the Earth until the multiplanetary phase (H7). The ground game keeps
/// its tuned 9.81 m/s²; this is the gravity of the Space regime, the orbit integrator and the
/// spaceplane.
/// </summary>
public sealed class Planet
{
    /// <summary>WGS84: GM and the rotation rate of NIMA TR8350.2.</summary>
    public static readonly Planet Earth = new("Earth", Ellipsoid.Wgs84, 3.986004418e14, 7.292115e-5);

    public string Name { get; }

    public Ellipsoid Shape { get; }

    /// <summary>The gravitational parameter, m³/s².</summary>
    public double GM { get; }

    /// <summary>Rotation about its axis, rad/s (sidereal: one turn in ~23 h 56 min for the Earth).</summary>
    public double RotationRate { get; }

    public Planet(string name, Ellipsoid shape, double gm, double rotationRate)
    {
        Name = name;
        Shape = shape;
        GM = gm;
        RotationRate = rotationRate;
    }

    /// <summary>Point-mass gravity at a distance from the centre, m/s²: 8.68 at 400 km above the Earth.</summary>
    public double Gravity(double r) => GM / (r * r);

    /// <summary>The speed of a circular orbit at a distance from the centre, m/s: 7,669 at 400 km.</summary>
    public double CircularSpeed(double r) => Math.Sqrt(GM / r);
}
