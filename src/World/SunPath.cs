using Godot;

namespace UnitSport.World;

// Godot's managed maths only (Vector3, Mathf): linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// Where the sun is by the hour (#1225): the model <see cref="DayNight"/> lights the world with,
/// pulled out so the thermals (<see cref="AirLift"/>) read the very same sky. A simple Swiss summer
/// day rather than an ephemeris: up at <see cref="Sunrise"/>, due south and <see cref="NoonElevation"/>
/// high at noon, down at <see cref="Sunset"/>.
///
/// <para>
/// Axes are the world's: +X east, -Z north, +Y up. The vector points from the ground to the sun.
/// </para>
/// </summary>
public static class SunPath
{
    public const float Sunrise = 6f, Sunset = 18f, NoonElevation = 62f;

    /// <summary>
    /// How far round the sun is: 0 at sunrise, 1 at sunset, 2 at the next sunrise. Day and night each
    /// take half the circle however long they last, so the default 6 / 18 reduces to (h - 6) / 12.
    /// </summary>
    public static float Phase(float hour, float rise = Sunrise, float set = Sunset)
    {
        float dayLength = set - rise;
        return hour >= rise && hour <= set
            ? (hour - rise) / dayLength
            : 1f + Mathf.PosMod(hour - set, 24f) / (24f - dayLength);
    }

    /// <summary>
    /// The direction to the sun and its elevation in degrees (negative at night: the sun below the
    /// horizon). The numbers <c>DayNight</c> has always produced, to the last bit.
    /// </summary>
    public static Vector3 Direction(float hour, out float elevationDeg,
        float rise = Sunrise, float set = Sunset, float noonElevation = NoonElevation)
    {
        float phase = Phase(hour, rise, set);
        // azimuth measured clockwise from north; world +X is east and -Z is north
        float az = Mathf.DegToRad(90f + phase * 180f);
        elevationDeg = noonElevation * Mathf.Sin(phase * Mathf.Pi);
        float el = Mathf.DegToRad(elevationDeg);
        return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el));
    }

    /// <summary><see cref="Direction(float, out float, float, float, float)"/> for the default day.</summary>
    public static Vector3 Direction(double hour) => Direction((float)hour, out _);
}
