using System.Globalization;

namespace UnitSport.World;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// Reading back what a dedicated server wrote about its environment clock
/// (<c>user://world_clock.cfg</c>, #579). Pure string work, kept out of <see cref="WorldClock"/> so
/// the part that actually goes wrong — the fallback for a file written before the env counter
/// existed — can be unit-tested without Godot's <c>ConfigFile</c>.
///
/// <para>
/// Why it matters on restart: the saved quantity is the <b>monotonic</b> counter, not the wrapped
/// hour. The hour alone loses the day count, and anything growing or burning in environment time
/// would reset with it — a campfire lit before the restart would come back unlit.
/// </para>
/// </summary>
public static class ClockSave
{
    /// <summary>
    /// The clock a saved file describes, or null when there is nothing usable in it.
    ///
    /// <para>
    /// <paramref name="envNow"/> and <paramref name="hourShift"/> are the #579 format. A file with
    /// neither but a <paramref name="hour"/> was written before it: the sky is kept by turning that
    /// hour into the shift, and the counter starts at zero rather than inventing a day count
    /// nothing ever recorded. Either way the day length must parse, or the file tells us nothing.
    /// </para>
    /// </summary>
    public static (double EnvNow, double HourShift, float MinutesPerDay)? Parse(
        string envNow, string hourShift, string hour, string minutesPerDay)
    {
        if (!TryFloat(minutesPerDay, out float mpd) || mpd < 0) return null;

        if (TryDouble(envNow, out double env) && TryDouble(hourShift, out double shift) && env >= 0)
            return (env, shift, mpd);

        return TryDouble(hour, out double h) ? (0, TimeCommand.Wrap(h) * 3600.0, mpd) : null;
    }

    /// <summary>Invariant culture: the French locale would read "1.5" as 15.</summary>
    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
}
