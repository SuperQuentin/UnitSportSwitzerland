using System;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// Swiss civil time (<c>Europe/Zurich</c>) to UTC, for the ephemeris (#1430, H6): the game's hour is
/// a clock on a Swiss wall, the sun's place is computed in UTC. CET is UTC+1, CEST UTC+2 from the
/// last Sunday of March to the last Sunday of October, changing at 01:00 UTC both ways (the EU rule
/// Switzerland follows since 1981). Computed here rather than through <c>TimeZoneInfo</c>, whose
/// zone database is not on every platform the game exports to.
/// </summary>
public static class SwissTime
{
    /// <summary>The UTC instant of <paramref name="hour"/> (0..24, local civil time) on <paramref name="date"/>.</summary>
    public static DateTime ToUtc(DateOnly date, double hour)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(hour);
        // try summer time first: the instant it gives is in summer time, or it is winter
        var summer = local.AddHours(-2);
        return InSummerTime(summer) ? summer : local.AddHours(-1);
    }

    /// <summary>Hours ahead of UTC at this UTC instant: 2 in summer time, 1 otherwise.</summary>
    public static int Offset(DateTime utc) => InSummerTime(utc) ? 2 : 1;

    /// <summary>Summer time is on at this UTC instant.</summary>
    public static bool InSummerTime(DateTime utc) =>
        utc >= LastSunday(utc.Year, 3).AddHours(1) && utc < LastSunday(utc.Year, 10).AddHours(1);

    private static DateTime LastSunday(int year, int month)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
        return last.AddDays(-(int)last.DayOfWeek);
    }
}
