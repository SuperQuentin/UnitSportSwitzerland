using System.Globalization;

namespace UnitSport.Crafting;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// How long a placed campfire burns (#272). Its <c>PlacedObject.Payload</c> is the Unix time it was
/// lit, written by the server (a client's own value is ignored), so a fire keeps burning across a
/// server restart and every peer agrees on it to within its clock's drift.
/// </summary>
public static class CampfireClock
{
    /// <summary>A campfire burns this long, then it is ashes that anyone may clear.</summary>
    public const double BurnSeconds = 20 * 60;

    /// <summary>The payload of a fire lit at <paramref name="nowUnix"/>.</summary>
    public static string Lit(double nowUnix) => nowUnix.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>When it was lit; NaN for a payload that is not a time (such a fire counts as out).</summary>
    public static double LitAt(string payload) =>
        double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && double.IsFinite(t) ? t : double.NaN;

    /// <summary>Seconds of burning left at <paramref name="nowUnix"/>: 0 once out, never more than <see cref="BurnSeconds"/> (a clock behind the server's).</summary>
    public static double SecondsLeft(string payload, double nowUnix)
    {
        double lit = LitAt(payload);
        if (double.IsNaN(lit)) return 0;
        return Math.Clamp(lit + BurnSeconds - nowUnix, 0, BurnSeconds);
    }

    public static bool Burning(string payload, double nowUnix) => SecondsLeft(payload, nowUnix) > 0;
}
