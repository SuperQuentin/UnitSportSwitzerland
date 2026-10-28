using System;
using UnitSport.Adventure.Together;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md).

/// <summary>
/// The errands' day (#1267): the Europe/Zurich calendar date as <c>yyyymmdd</c>, wherever the server runs
/// (<see cref="Zurich"/>). Cards turn over at midnight in Switzerland. A card lives until the end of the next day.
/// </summary>
public static class ErrandClock
{
    /// <summary>The Swiss calendar day of an instant (UTC).</summary>
    public static int Day(DateTime utc) => Zurich.DayAndWeek(utc).Day;

    /// <summary>The day <paramref name="days"/> after <paramref name="day"/> (negative: before).</summary>
    public static int AddDays(int day, int days) => ToDate(day).AddDays(days) is var d ? d.Year * 10000 + d.Month * 100 + d.Day : 0;

    /// <summary>Days from <paramref name="from"/> to <paramref name="to"/> (positive when <paramref name="to"/> is later).</summary>
    public static int Between(int from, int to) => (int)(ToDate(to) - ToDate(from)).TotalDays;

    /// <summary>Whether a card of <paramref name="cardDay"/> can still be taken or run on <paramref name="today"/>: that day and the next.</summary>
    public static bool Alive(int cardDay, int today) => Between(cardDay, today) is >= 0 and <= 1;

    private static DateTime ToDate(int day)
    {
        int y = day / 10000, m = day / 100 % 100, d = day % 100;
        return y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ? new DateTime(y, m, d) : DateTime.MinValue;
    }
}
