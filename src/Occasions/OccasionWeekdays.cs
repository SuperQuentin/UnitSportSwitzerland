using System.Globalization;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

namespace UnitSport.Occasions;

/// <summary>
/// The "n-th weekday of the month" form of a schedule window (#863): <c>fri#1</c> is the first
/// Friday of every month, <c>sat#-1</c> the last Saturday. Its <c>To</c> is either the same token
/// (that day only) or <c>+N</c>: that day and the N days after it, so a night that runs past
/// midnight can be given its morning (<c>fri#1</c> .. <c>+1</c>). Pure, so the tests walk it.
/// </summary>
public static class OccasionWeekdays
{
    private static readonly string[] Days = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    /// <summary>Whether <paramref name="from"/> is a weekday token (<c>fri#1</c>), so the window is this form.</summary>
    public static bool IsToken(string from) => TryParse(from, out _, out _);

    /// <summary>
    /// Whether <paramref name="date"/> falls in the window <paramref name="from"/> .. <paramref name="to"/>.
    /// <paramref name="startYear"/> is the year the matching window opened (the instance key's year).
    /// </summary>
    public static bool Contains(string from, string to, DateOnly date, out int startYear)
    {
        startYear = date.Year;
        if (!TryParse(from, out var day, out int nth)) return false;
        int extra = Extra(from, to);
        if (extra < 0) return false;
        // the window opened this month, or (with a +N tail) late last month
        for (int back = 0; back <= 1; back++)
        {
            var month = new DateOnly(date.Year, date.Month, 1).AddMonths(-back);
            if (Nth(month.Year, month.Month, day, nth) is not { } opened) continue;
            if (date >= opened && date <= opened.AddDays(extra))
            {
                startYear = opened.Year;
                return true;
            }
        }
        return false;
    }

    /// <summary>The <paramref name="nth"/> <paramref name="day"/> of a month (negative counts from its end), or null.</summary>
    public static DateOnly? Nth(int year, int month, DayOfWeek day, int nth)
    {
        if (nth == 0 || nth > 5 || nth < -5) return null;
        int days = DateTime.DaysInMonth(year, month);
        if (nth > 0)
        {
            var first = new DateOnly(year, month, 1);
            int offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
            int d = 1 + offset + (nth - 1) * 7;
            return d <= days ? new DateOnly(year, month, d) : null;
        }
        var last = new DateOnly(year, month, days);
        int back = ((int)last.DayOfWeek - (int)day + 7) % 7;
        int dl = days - back + (nth + 1) * 7;
        return dl >= 1 ? new DateOnly(year, month, dl) : null;
    }

    /// <summary>The days after the opening one: 0 for the same token or an empty To, N for <c>+N</c>, -1 if unreadable.</summary>
    private static int Extra(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || string.Equals(to, from.Trim(), StringComparison.OrdinalIgnoreCase)) return 0;
        if (to[0] == '+' && int.TryParse(to.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n <= 6)
            return n;
        return -1;
    }

    private static bool TryParse(string s, out DayOfWeek day, out int nth)
    {
        day = DayOfWeek.Sunday;
        nth = 0;
        var parts = s.Trim().ToLowerInvariant().Split('#');
        if (parts.Length != 2) return false;
        int i = Array.IndexOf(Days, parts[0]);
        if (i < 0 || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nth)) return false;
        day = (DayOfWeek)i;
        return nth != 0 && nth is >= -5 and <= 5;
    }
}
