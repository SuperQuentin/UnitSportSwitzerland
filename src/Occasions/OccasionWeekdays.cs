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

    /// <summary>Whether <paramref name="from"/> is a weekday token (<c>fri#1</c>, <c>04-mon#3</c>) or an Easter one (<c>easter-46</c>), so the window is this form.</summary>
    public static bool IsToken(string from) => TryParseToken(from, out _);

    /// <summary>
    /// Whether <paramref name="date"/> falls in the window <paramref name="from"/> .. <paramref name="to"/>.
    /// <paramref name="startYear"/> is the year the matching window opened (the instance key's year).
    /// </summary>
    public static bool Contains(string from, string to, DateOnly date, out int startYear)
    {
        startYear = date.Year;
        if (!TryParseToken(from, out var token)) return false;
        int extra = Extra(from, to);
        if (extra < 0) return false;
        if (token.Month == 0 && !token.Easter)
        {
            // the window opened this month, or (with a +N tail) late last month
            for (int back = 0; back <= 1; back++)
            {
                var month = new DateOnly(date.Year, date.Month, 1).AddMonths(-back);
                if (Nth(month.Year, month.Month, token.Day, token.Nth) is not { } opened) continue;
                if (date >= opened && date <= opened.AddDays(extra))
                {
                    startYear = opened.Year;
                    return true;
                }
            }
            return false;
        }
        // once a year: it opened this year, or (with a tail over new year) last year
        for (int back = 0; back <= 1; back++)
        {
            if (Opening(token, date.Year - back) is not { } opened) continue;
            if (date >= opened && date <= opened.AddDays(extra))
            {
                startYear = opened.Year;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The day a once-a-year token (<c>04-mon#3</c>, <c>easter-46</c>) opens in <paramref name="year"/>, or null for
    /// a monthly token (<c>fri#1</c> opens twelve times) or one that has no such day that year (a fifth Monday).
    /// </summary>
    public static DateOnly? Opening(string from, int year) =>
        TryParseToken(from, out var token) ? Opening(token, year) : null;

    private static DateOnly? Opening(Token token, int year)
    {
        if (token.Easter) return Easter(year).AddDays(token.Offset);
        return token.Month == 0 ? null : Nth(year, token.Month, token.Day, token.Nth);
    }

    /// <summary>
    /// Easter Sunday (the Gregorian calendar, the "Anonymous Gregorian" algorithm: Meeus/Jones/Butcher), valid for
    /// any Gregorian year.
    /// </summary>
    public static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100;
        int d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
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
    public static int ExtraDays(string from, string to) => Extra(from, to);

    private static int Extra(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || string.Equals(to, from.Trim(), StringComparison.OrdinalIgnoreCase)) return 0;
        if (to[0] == '+' && int.TryParse(to.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n <= MaxTail)
            return n;
        return -1;
    }

    /// <summary>A tail longer than this is a typo: the longest festival runs sixteen days (Montreux Jazz).</summary>
    private const int MaxTail = 15;

    /// <summary>A parsed token: <c>Month</c> 0 = every month; <c>Easter</c> with its <c>Offset</c> in days.</summary>
    private readonly record struct Token(int Month, DayOfWeek Day, int Nth, bool Easter, int Offset);

    private static bool TryParseToken(string s, out Token token)
    {
        token = default;
        s = s.Trim().ToLowerInvariant();
        if (s.StartsWith("easter", StringComparison.Ordinal))
        {
            string rest = s[6..];
            int offset = 0;
            if (rest.Length > 0 && !(rest[0] is '+' or '-' && int.TryParse(rest, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out offset))) return false;
            if (offset is < -100 or > 100) return false;
            token = new Token(0, DayOfWeek.Sunday, 0, true, offset);
            return true;
        }
        var parts = s.Split('#');
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int nth)
            || nth == 0 || nth is < -5 or > 5) return false;
        int month = 0;
        string name = parts[0];
        int dash = name.IndexOf('-');
        if (dash >= 0)
        {
            if (!int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out month) || month is < 1 or > 12)
                return false;
            name = name[(dash + 1)..];
        }
        int i = Array.IndexOf(Days, name);
        if (i < 0) return false;
        token = new Token(month, (DayOfWeek)i, nth, false, 0);
        return true;
    }
}
