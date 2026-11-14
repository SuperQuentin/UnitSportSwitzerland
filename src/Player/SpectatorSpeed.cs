using System.Globalization;

namespace UnitSport.Player;

/// <summary>
/// The spectator camera's speed rules (#1796), Godot-free so the unit tests reach them.
///
/// <para>
/// The wheel sets up to <see cref="MaxSpeed"/>, 100 km/s (Geneva to Asia, ~6,500 km, in about a minute; once round
/// the Earth in about 7 minutes, a minute boosted), but what is flown is never more than <see cref="AltitudeCap"/>:
/// the altitude crossed in 2 s, at least the old cap, 3 km/s. Measured under <c>--frame earth</c> with <c>--fly</c>:
/// low down the frame re-anchors every 3 km, and 100 km/s there was 28 re-anchors a second (2.8 s of 15 s spent
/// shifting over an empty fill; on a loaded world each costs 27-52 ms, #1784) with nothing streamed in to see; in
/// Space (20 km re-anchors, no leaf streams above 60 km) 600 km/s, boosted, holds 60 fps. So the ground slides past
/// at the same angular rate at every altitude, as a map zooms.
/// </para>
/// </summary>
public static class SpectatorSpeed
{
    /// <summary>The slowest and fastest speed the wheel sets, m/s (before boost).</summary>
    public const float MinSpeed = 2f, MaxSpeed = 100_000f;

    /// <summary>The cap near the ground, m/s: the old wheel cap, one 3 km re-anchor a second.</summary>
    public const float GroundCap = 3000f;

    /// <summary>The cap grows with the altitude: this many m/s per metre up (the altitude crossed in 2 s).</summary>
    public const double CapPerMetre = 0.5;

    /// <summary>A notch of the wheel (or a press) multiplies or divides the speed by this.</summary>
    public const float Notch = 1.25f;

    /// <summary>The fastest the wheel's speed is flown at this altitude above the ellipsoid, m/s.</summary>
    public static float AltitudeCap(double altitude) =>
        (float)Math.Clamp(altitude * CapPerMetre, GroundCap, MaxSpeed);

    /// <summary>One notch faster: the speed asked for may pass the altitude's cap, so climbing then speeds up by itself.</summary>
    public static float Faster(float speed) => Math.Min(speed * Notch, MaxSpeed);

    /// <summary>One notch slower, from what is flown: past the cap a notch down is felt at once.</summary>
    public static float Slower(float speed, float cap) => Math.Max(Math.Min(speed, cap) / Notch, MinSpeed);

    /// <summary>A speed as it reads: "2.5 m/s", "150 m/s", "3.2 km/s", "600 km/s".</summary>
    public static string Format(float metresPerSecond)
    {
        var inv = CultureInfo.InvariantCulture;
        return metresPerSecond switch
        {
            < 9.95f => metresPerSecond.ToString("0.0", inv) + " m/s",
            < 999.5f => metresPerSecond.ToString("0", inv) + " m/s",
            < 99_950f => (metresPerSecond / 1000f).ToString("0.0", inv) + " km/s",
            _ => (metresPerSecond / 1000f).ToString("0", inv) + " km/s",
        };
    }
}
