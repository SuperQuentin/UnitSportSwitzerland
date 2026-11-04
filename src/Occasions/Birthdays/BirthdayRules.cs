// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/birthdays.md).

namespace UnitSport.Occasions.Birthdays;

/// <summary>
/// A player's birthday (#1423): a day and a month, and a year only if the player gave one (0 = not given), which is used for
/// nothing but the age in the chat line. <see cref="None"/> is "not entered".
/// </summary>
public readonly record struct Birthday(int Day, int Month, int Year = 0)
{
    public static readonly Birthday None = default;

    public bool IsSet => Month is >= 1 and <= 12 && Day >= 1 && Day <= BirthdayRules.DaysIn(Month);
    public bool HasYear => Year > 0;
}

/// <summary>The birthday rules (#1423): when it is one, the age, the lines, the limits. Pure, tier 0 (<c>BirthdayRulesTests</c>).</summary>
public static class BirthdayRules
{
    /// <summary>Slices in a birthday cake; one per player.</summary>
    public const int CakeSlices = 8;

    /// <summary>Metres from the cake a slice is taken at (the server checks with some slack).</summary>
    public const float SliceReach = 2.2f;

    /// <summary>Metres from the birthday player a wish is made at.</summary>
    public const float WishReach = 4f;

    /// <summary>Seconds the birthday fireworks last.</summary>
    public const double ShowSeconds = 60;

    /// <summary>Birthday fireworks one player may light in a day: the gift holds this many rockets.</summary>
    public const int ShowsPerDay = 3;

    /// <summary>Seconds between two of one player's shows (a show must end before the next).</summary>
    public const double ShowCooldown = ShowSeconds + 5;

    /// <summary>The cash in the gift, CHF.</summary>
    public const int GiftCash = 30;

    /// <summary>The oldest year the account page offers.</summary>
    public const int OldestYear = 1920;

    /// <summary>Days in a month, February counted with its 29th (a birthday may be on it).</summary>
    public static int DaysIn(int month) => month switch
    {
        2 => 29,
        4 or 6 or 9 or 11 => 30,
        >= 1 and <= 12 => 31,
        _ => 0,
    };

    /// <summary>Whether day, month and year make a birthday a player may enter on <paramref name="today"/>: a real date, the year 0 or
    /// between <see cref="OldestYear"/> and this year, and the 29 February only in a leap year when a year is given.</summary>
    public static bool Valid(int day, int month, int year, DateOnly today)
    {
        if (month is < 1 or > 12 || day < 1 || day > DaysIn(month)) return false;
        if (year == 0) return true;
        if (year < OldestYear || year > today.Year) return false;
        if (month == 2 && day == 29 && !DateTime.IsLeapYear(year)) return false;
        return new DateOnly(year, month, day) <= today;
    }

    /// <summary>Whether <paramref name="today"/> is the birthday: the same day and month, and a 29 February birthday falls on
    /// the 28th in a year with no 29th. Never before the year of birth.</summary>
    public static bool IsToday(Birthday b, DateOnly today)
    {
        if (!b.IsSet || (b.HasYear && today.Year < b.Year)) return false;
        if (b.Month == today.Month && b.Day == today.Day) return true;
        return b.Month == 2 && b.Day == 29 && today.Month == 2 && today.Day == 28 && !DateTime.IsLeapYear(today.Year);
    }

    /// <summary>The age the player turns on their birthday in <paramref name="today"/>'s year, or null with no year given.</summary>
    public static int? AgeIn(Birthday b, DateOnly today) => b.HasYear && today.Year >= b.Year ? today.Year - b.Year : null;

    /// <summary>Whether the gift is still to be given today: it is the birthday and no gift was given this year
    /// (<paramref name="giftYear"/> is the year of the last one). Changing the date never gives a second one in a year.</summary>
    public static bool GiftDue(Birthday b, int giftYear, DateOnly today) => IsToday(b, today) && giftYear < today.Year;

    /// <summary>Which of <paramref name="count"/> collectible hats the gift of <paramref name="year"/> holds: a new one each year, in turn.</summary>
    public static int CollectibleIndex(int year, int count) => count <= 0 ? 0 : ((year % count) + count) % count;

    /// <summary>"1st", "2nd", "3rd", "4th", "11th", "21st".</summary>
    public static string Ordinal(int n)
    {
        int tens = n % 100;
        string suffix = tens is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n + suffix;
    }

    /// <summary>The title screen's greeting: "Happy 30th birthday, Anna!", or "Happy birthday, Anna!" with no age.</summary>
    public static string Greeting(string name, int? age)
    {
        string who = string.IsNullOrWhiteSpace(name) ? "" : $", {name}";
        return age is > 0 ? $"Happy {Ordinal(age.Value)} birthday{who}!" : $"Happy birthday{who}!";
    }

    /// <summary>The chat line everyone reads when a birthday player joins (or the day turns): with the age when a year was given.</summary>
    public static string Announcement(string name, int? age) => age is > 0
        ? $"It's {name}'s birthday: {name} turns {age} today! Walk up and wish them a happy birthday."
        : $"It's {name}'s birthday today! Walk up and wish them a happy birthday.";

    /// <summary>What the birthday player reads on a wish: "Luca wished you a happy birthday (3 wishes today)".</summary>
    public static string Wished(string from, int count) =>
        count <= 1 ? $"{from} wished you a happy birthday!" : $"{from} wished you a happy birthday! ({count} wishes today)";

    /// <summary>The summary of the day's wishes: "12 people wished you a happy birthday today".</summary>
    public static string WishesToday(int count) => count switch
    {
        <= 0 => "Nobody has wished you a happy birthday yet today.",
        1 => "1 person wished you a happy birthday today.",
        _ => $"{count} people wished you a happy birthday today.",
    };

    /// <summary>A stable seed for one player's <paramref name="number"/>th show of a day (FNV-1a, the same on every run).</summary>
    public static int ShowSeed(string name, DateOnly day, int number)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in $"{name}|{day:yyyy-MM-dd}|{number}") { h ^= c; h *= 16777619; }
            return (int)(h & 0x7fffffff);
        }
    }
}
