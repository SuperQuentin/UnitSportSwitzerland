using System.Globalization;

namespace UnitSport.Crafting;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// How long a placed campfire burns (#272), in <b>environment seconds</b> since #579. Its
/// <c>PlacedObject.Payload</c> is the environment time it was lit, written by the server (a
/// client's own value is ignored), so a fire keeps burning across a server restart —
/// <c>World.WorldClock</c> persists its counter for exactly this.
///
/// <para>
/// Environment time, not the wall clock: a fire burning down is a world process, so it slows with
/// the world (<c>docs/notes/core/three-clocks.md</c>). That also removes the reason the payload
/// used to be a Unix stamp — it was only ever Unix so it could outlive the server, and
/// <c>WorldClock.EnvNow</c> now does that itself.
/// </para>
/// </summary>
public static class CampfireClock
{
    /// <summary>
    /// A campfire burns half a day of environment time, then it is ashes that anyone may clear:
    /// one lit at dusk is out by the small hours. Env units, so it means the same thing whatever
    /// the day length — at the default 24 min a day that is about 12 real minutes at 1x speed,
    /// where the old wall-clock value was 20 real minutes flat.
    /// </summary>
    public const double BurnEnvSeconds = 12 * 3600;

    /// <summary>Marks a payload as environment seconds, telling it from a pre-#579 Unix stamp.</summary>
    private const char EnvMark = 'e';

    /// <summary>The payload of a fire lit at <paramref name="envNow"/> environment seconds.</summary>
    public static string Lit(double envNow) => EnvMark + envNow.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// When it was lit, in environment seconds; NaN for a payload that is not one, and such a fire
    /// counts as out.
    ///
    /// <para>
    /// A bare number is a Unix stamp written before #579. There is no honest way to turn one into
    /// environment seconds — the two counters share no origin — so an old save's fires read as
    /// ashes anyone may clear, rather than as fires lit impossibly far in the future that would
    /// burn for ever.
    /// </para>
    /// </summary>
    public static double LitAt(string payload) =>
        payload.Length > 1 && payload[0] == EnvMark
        && double.TryParse(payload[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
        && double.IsFinite(t) ? t : double.NaN;

    /// <summary>Environment seconds of burning left at <paramref name="envNow"/>: 0 once out, never more than <see cref="BurnEnvSeconds"/>.</summary>
    public static double SecondsLeft(string payload, double envNow)
    {
        double lit = LitAt(payload);
        if (double.IsNaN(lit)) return 0;
        return Math.Clamp(lit + BurnEnvSeconds - envNow, 0, BurnEnvSeconds);
    }

    public static bool Burning(string payload, double envNow) => SecondsLeft(payload, envNow) > 0;
}
