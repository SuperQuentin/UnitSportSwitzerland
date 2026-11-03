using UnitSport.Items;

namespace UnitSport.Adventure.Together;

/// <summary>
/// The <c>together</c> section (#1266 parts 6-7): seconds played together this ISO week and this day (Europe/Zurich),
/// the week's chest, the punch card's stamps and cards (never reset), the friends met (short keys, never sent to others),
/// the mentor milestones done and mentored, and the pots shared. Rolled over lazily by <see cref="Roll"/>.
/// </summary>
public sealed class TogetherProgress
{
    public static readonly SectionKey<TogetherProgress> Section = new("together", Version: 1);

    /// <summary>The ISO week (year x 100 + week) the week's seconds count for.</summary>
    public int Week { get; set; }
    public int WeekSeconds { get; set; }
    /// <summary>This week's chest was granted.</summary>
    public bool WeekChest { get; set; }
    /// <summary>The Zurich calendar day (yyyymmdd) the day's seconds count for.</summary>
    public int Day { get; set; }
    public int DaySeconds { get; set; }
    /// <summary>This day's stamp was given.</summary>
    public bool Stamped { get; set; }
    /// <summary>Stamps on the card now (0..9), and the cards filled; neither ever resets.</summary>
    public int Stamps { get; set; }
    public int Cards { get; set; }
    /// <summary>The first six hex characters of each proven partner's key, the oldest first, 128 at most.</summary>
    public List<string> Friends { get; set; } = new();
    /// <summary>Mentor milestones this player did (any way), and how often.</summary>
    public List<string> Firsts { get; set; } = new();
    public Dictionary<string, int> Done { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Milestones mentored, by milestone; the pairs that counted ("summit:03ab7e").</summary>
    public Dictionary<string, int> Mentored { get; set; } = new(StringComparer.Ordinal);
    public List<string> Mentees { get; set; } = new();
    /// <summary>Whether this player was mentored for a milestone already (once ever).</summary>
    public List<string> MentoredAs { get; set; } = new();
    public int Fondues { get; set; }
    public int Splits { get; set; }
    public int BreadLost { get; set; }

    /// <summary>Seconds together in a week for its chest.</summary>
    public const int WeekChestSeconds = 30 * 60;
    /// <summary>Seconds together in a day for a stamp.</summary>
    public const int StampSeconds = 20 * 60;
    public const int StampsPerCard = 10;
    public const int MaxFriends = 128;

    /// <summary>The titles of cards 1, 3, 5 and 10.</summary>
    public static string? CardTitle(int cards) => cards switch
    {
        1 => "Gspänli",
        3 => "Seilschaft",
        5 => "Stammtisch",
        10 => "Unzertrennlich",
        _ => null,
    };

    /// <summary>The tenth card's felt hat.</summary>
    public const ItemId CardHat = ItemId.StammgastHat;

    /// <summary>
    /// A new day or week by the Zurich clock (lazily, at the first tick after midnight): the day's and week's seconds start
    /// again; stamps and cards stay. True when anything rolled.
    /// </summary>
    public bool Roll(DateTime utc)
    {
        var (day, week) = Zurich.DayAndWeek(utc);
        bool rolled = false;
        if (day != Day) { Day = day; DaySeconds = 0; Stamped = false; rolled = true; }
        if (week != Week) { Week = week; WeekSeconds = 0; WeekChest = false; rolled = true; }
        return rolled;
    }

    /// <summary>What playing together this tick earned.</summary>
    public readonly record struct Earned(bool WeekChest, bool Stamp, bool CardFull, string? Title, bool Hat);

    /// <summary>
    /// <paramref name="seconds"/> more played together at <paramref name="utc"/>: rolls the day and week first, then gives the
    /// week's chest at 30 min (once a week), a stamp at 20 min of a day (once a day), and on the tenth stamp a full card.
    /// </summary>
    public Earned Add(int seconds, DateTime utc)
    {
        Roll(utc);
        DaySeconds += seconds;
        WeekSeconds += seconds;
        bool chest = false, stamp = false, full = false;
        string? title = null;
        bool hat = false;
        if (!WeekChest && WeekSeconds >= WeekChestSeconds) { WeekChest = true; chest = true; }
        if (!Stamped && DaySeconds >= StampSeconds)
        {
            Stamped = true;
            stamp = true;
            if (++Stamps >= StampsPerCard)
            {
                Stamps = 0;
                Cards++;
                full = true;
                title = CardTitle(Cards);
                hat = Cards == 10;
            }
        }
        return new Earned(chest, stamp, full, title, hat);
    }

    /// <summary>Remembers a partner's short key (the distinct friends count, the mentor pairs): the oldest out past 128.</summary>
    public bool Meet(string shortKey)
    {
        if (string.IsNullOrEmpty(shortKey) || Friends.Contains(shortKey)) return false;
        Friends.Add(shortKey);
        while (Friends.Count > MaxFriends) Friends.RemoveAt(0);
        return true;
    }

    /// <summary>The short key the section remembers a player by: the first six hex characters of its proven key (an account's id).</summary>
    public static string Short(string key)
    {
        string k = key.StartsWith("acct:", StringComparison.Ordinal) ? key[5..] : key;
        var s = new string(k.Where(Uri.IsHexDigit).Take(6).ToArray()).ToLowerInvariant();
        return s.Length == 6 ? s : "";
    }
}

/// <summary>Europe/Zurich time without a tz database (CET, CEST from the last Sunday of March to the last of October, at 01:00 UTC).</summary>
public static class Zurich
{
    public static DateTime Local(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var start = LastSunday(utc.Year, 3).AddHours(1);
        var end = LastSunday(utc.Year, 10).AddHours(1);
        return utc.AddHours(utc >= start && utc < end ? 2 : 1);
    }

    private static DateTime LastSunday(int year, int month)
    {
        var d = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
        return d.AddDays(-(int)d.DayOfWeek);
    }

    /// <summary>The Zurich calendar day (yyyymmdd) and ISO week (year x 100 + week, Monday 00:00 local) of an instant.</summary>
    public static (int Day, int Week) DayAndWeek(DateTime utc)
    {
        var l = Local(utc);
        int day = l.Year * 10000 + l.Month * 100 + l.Day;
        int week = System.Globalization.ISOWeek.GetYear(l) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(l);
        return (day, week);
    }
}
