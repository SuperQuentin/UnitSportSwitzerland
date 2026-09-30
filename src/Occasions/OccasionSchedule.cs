using System.Globalization;

namespace UnitSport.Occasions;

/// <summary>
/// Which occasions a calendar date falls in. Pure — no Godot, no clock — so
/// <see cref="OccasionProbe"/> can walk it across the year boundaries it has to get right.
/// </summary>
public static class OccasionSchedule
{
    /// <summary>
    /// The enabled entries whose schedule contains <paramref name="date"/>, each with its
    /// instance key. Several may match: occasions stack.
    /// </summary>
    public static List<(OccasionEntry Entry, string Instance)> Evaluate(IEnumerable<OccasionEntry> entries, DateOnly date)
    {
        var result = new List<(OccasionEntry, string)>();
        foreach (var e in entries)
        {
            if (!e.Enabled) continue;
            foreach (var w in e.Schedule)
                if (Contains(w, date, out int startYear))
                {
                    result.Add((e, InstanceKey(e.Id, startYear)));
                    break;
                }
        }
        return result;
    }

    /// <summary>
    /// "halloween-2026". Keyed by the year the window <i>opened</i>, so a Christmas running
    /// 20 December to 6 January is one instance, not two, and the hunt does not reset at midnight
    /// on new year's eve.
    /// </summary>
    public static string InstanceKey(string id, int startYear) => $"{id}-{startYear}";

    /// <summary>The instance key an occasion forced on outside its schedule gets: this year's.</summary>
    public static string ForcedInstance(OccasionEntry e, DateOnly date)
    {
        foreach (var w in e.Schedule)
            if (Contains(w, date, out int y)) return InstanceKey(e.Id, y);
        return InstanceKey(e.Id, date.Year);
    }

    public static bool Contains(DateWindow window, DateOnly date, out int startYear)
    {
        startYear = date.Year;

        if (TryFull(window.From, out var from) && TryFull(window.To, out var to))
        {
            startYear = from.Year;
            return date >= from && date <= to;
        }

        if (!TryMonthDay(window.From, out int fm, out int fd) || !TryMonthDay(window.To, out int tm, out int td))
            return false;

        int md = date.Month * 100 + date.Day;
        int f = fm * 100 + fd, t = tm * 100 + td;
        if (f <= t) return md >= f && md <= t;

        // wraps over new year: the part after new year belongs to last year's instance
        if (md >= f) return true;
        if (md <= t) { startYear = date.Year - 1; return true; }
        return false;
    }

    private static bool TryFull(string s, out DateOnly date) =>
        DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>"MM-DD", validated against a leap year so 02-29 is accepted.</summary>
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
