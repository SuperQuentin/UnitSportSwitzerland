using System.Globalization;

// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-framework.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>The dates one festival instance covers, both ends inclusive.</summary>
public readonly record struct FestivalSpan(DateOnly Start, DateOnly End)
{
    /// <summary>Days from the first date to the last, both counted.</summary>
    public int Length => End.DayNumber - Start.DayNumber + 1;

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>1 on the window's first date.</summary>
    public int Day(DateOnly date) => date.DayNumber - Start.DayNumber + 1;
}

/// <summary>
/// The festival's own reading of the schedule windows an <see cref="OccasionEntry"/> carries: where the instance began
/// and ends, which day of it a date is, and whether it is over. Pure. A window is <c>MM-DD</c>, <c>YYYY-MM-DD</c> or a token
/// that opens once a year (<see cref="OccasionWeekdays"/>); a monthly token (<c>fri#1</c>) has no festival.
/// </summary>
public static class FestivalCalendar
{
    /// <summary>The window that <b>opened in</b> <paramref name="startYear"/>, or null when it has none that year.</summary>
    public static FestivalSpan? SpanOf(string from, string to, int startYear)
    {
        if (OccasionWeekdays.IsToken(from))
        {
            if (OccasionWeekdays.Opening(from, startYear) is not { } opened) return null;
            int extra = OccasionWeekdays.ExtraDays(from, to);
            return extra < 0 ? null : new FestivalSpan(opened, opened.AddDays(extra));
        }
        if (TryFull(from, out var f) && TryFull(to, out var t))
            return f.Year == startYear && t >= f ? new FestivalSpan(f, t) : null;
        if (!TryMonthDay(from, out int fm, out int fd) || !TryMonthDay(to, out int tm, out int td)) return null;
        var start = Clamp(startYear, fm, fd);
        var end = Clamp(fm * 100 + fd <= tm * 100 + td ? startYear : startYear + 1, tm, td);
        return new FestivalSpan(start, end);
    }

    /// <summary>The window of <paramref name="windows"/> holding <paramref name="date"/>, with the year it opened in; null when none does.</summary>
    public static (FestivalSpan Span, int StartYear)? Containing(IEnumerable<(string From, string To)> windows, DateOnly date)
    {
        foreach (var (from, to) in windows)
            for (int back = 0; back <= 1; back++)
                if (SpanOf(from, to, date.Year - back) is { } span && span.Contains(date))
                    return (span, date.Year - back);
        return null;
    }

    /// <summary>The 1-based day of the instance <paramref name="date"/> is in, or 0 when no window holds it.</summary>
    public static int Day(IEnumerable<(string From, string To)> windows, DateOnly date) =>
        Containing(windows, date) is { } c ? c.Span.Day(date) : 0;

    /// <summary>
    /// Whether the instance opened in <paramref name="startYear"/> is over on <paramref name="today"/>: the day after its last date, or
    /// any later. An instance no window knows (an operator removed it) counts as over, so Batzen are never stuck.
    /// </summary>
    public static bool Ended(IEnumerable<(string From, string To)> windows, int startYear, DateOnly today)
    {
        foreach (var (from, to) in windows)
            if (SpanOf(from, to, startYear) is { } span) return today > span.End;
        return true;
    }

    /// <summary>The year in <c>"test-2027"</c>, or 0.</summary>
    public static int YearOf(string instance)
    {
        int dash = instance.LastIndexOf('-');
        return dash >= 0 && int.TryParse(instance.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int y) ? y : 0;
    }

    /// <summary>The festival id in <c>"test-2027"</c>.</summary>
    public static string FestivalOf(string instance)
    {
        int dash = instance.LastIndexOf('-');
        return dash > 0 ? instance[..dash] : instance;
    }

    private static DateOnly Clamp(int year, int month, int day) => new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    private static bool TryFull(string s, out DateOnly date) =>
        DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryMonthDay(string s, out int month, out int day)
    {
        month = day = 0;
        var parts = s.Trim().Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out month)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out day))
            return false;
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2000, month);
    }
}
