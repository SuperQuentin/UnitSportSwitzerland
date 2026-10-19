using System;
using System.Globalization;

namespace UnitSport.Core;

// The parsing is plain C# (linked into the unit tests); the process-wide date reads CmdArgs (Godot).

/// <summary>
/// The date the game's calendar is read at (#924): the occasions (Halloween, Christmas, the UFO
/// Friday), the farm month, the birds of the month, the fishing season, a shop's seasonal goods. The
/// real date in normal play; <c>--date YYYY-MM-DD</c> pins it, and a trailer render always runs on
/// its film's own date (<see cref="Trailer.TrailerDirector.FilmDate"/>), so the calendar is never
/// read during a render. The one reader of the real date for these (the clock-discipline guard).
/// Audio cues on the wall clock (church bells, a carol on the hour) are not the calendar.
/// </summary>
public static class Calendar
{
    /// <summary>The <c>--date YYYY-MM-DD</c> in <paramref name="args"/>, if any (and valid).</summary>
    public static DateOnly? DateFrom(string[] args)
    {
        int at = Array.IndexOf(args, "--date");
        return at >= 0 && at + 1 < args.Length
            && DateOnly.TryParseExact(args[at + 1].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

#if GODOT
    private static readonly Lazy<DateOnly?> PinnedDate = new(() =>
        DateFrom(CmdArgs.All) ?? (Trailer.TrailerDirector.Requested && !Trailer.TrailerDirector.Unpinned ? Trailer.TrailerDirector.FilmDate : null));

    /// <summary>The date pinned for this process (<c>--date</c>, or a film's), null in normal play.</summary>
    public static DateOnly? Pinned => PinnedDate.Value;

    /// <summary>Today, as the calendar reads it.</summary>
    public static DateOnly Today => Pinned ?? DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Now on <see cref="Today"/>: the pinned date at the real time of day, for code that wants a <see cref="DateTime"/>.</summary>
    public static DateTime Now => Pinned is { } d ? d.ToDateTime(TimeOnly.FromDateTime(DateTime.Now)) : DateTime.Now;

    /// <summary>The calendar month, 1-12.</summary>
    public static int Month => Today.Month;
#endif
}
