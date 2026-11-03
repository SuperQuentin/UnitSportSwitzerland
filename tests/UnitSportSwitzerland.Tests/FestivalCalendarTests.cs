using UnitSport.Occasions;
using UnitSport.Occasions.Festivals;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The festival framework's calendar (#1269 part 1): the two new schedule tokens against real dates (Sechselaeuten, Fasnacht,
/// Easter), the day index of an instance and when it is over (src/Occasions/OccasionWeekdays.cs, Festivals/FestivalCalendar.cs).
/// </summary>
public class FestivalCalendarTests
{
    // ---- Easter ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(2000, 4, 23)]
    [InlineData(2019, 4, 21)]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2028, 4, 16)]
    [InlineData(2030, 4, 21)]
    [InlineData(2038, 4, 25)]
    [InlineData(2100, 3, 28)]
    public void Easter_is_the_gregorian_sunday(int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), OccasionWeekdays.Easter(y));

    [Fact]
    public void Easter_is_always_a_sunday_between_march_22_and_april_25()
    {
        for (int y = 1900; y <= 2200; y++)
        {
            var e = OccasionWeekdays.Easter(y);
            Assert.Equal(DayOfWeek.Sunday, e.DayOfWeek);
            Assert.InRange(e, new DateOnly(y, 3, 22), new DateOnly(y, 4, 25));
        }
    }

    // ---- 04-mon#3: Sechselaeuten -------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 4, 20)]
    [InlineData(2027, 4, 19)]
    [InlineData(2028, 4, 17)]
    public void Sechselaeuten_is_the_third_monday_of_april(int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), OccasionWeekdays.Opening("04-mon#3", y));
        var day = new DateOnly(y, m, d);
        Assert.True(OccasionWeekdays.Contains("04-mon#3", "04-mon#3", day, out int startYear));
        Assert.Equal(y, startYear);
        Assert.False(OccasionWeekdays.Contains("04-mon#3", "04-mon#3", day.AddDays(1), out _));
        Assert.False(OccasionWeekdays.Contains("04-mon#3", "04-mon#3", day.AddDays(-7), out _));
        // the same weekday in May is not it: the token names its month
        Assert.False(OccasionWeekdays.Contains("04-mon#3", "04-mon#3", day.AddMonths(1), out _));
    }

    [Fact]
    public void A_month_weekday_counts_from_the_end_and_may_not_exist()
    {
        Assert.Equal(new DateOnly(2027, 4, 26), OccasionWeekdays.Opening("04-mon#-1", 2027));
        // April 2027 has four Mondays: no instance that year
        Assert.Null(OccasionWeekdays.Opening("04-mon#5", 2027));
        Assert.Equal(new DateOnly(2024, 4, 29), OccasionWeekdays.Opening("04-mon#5", 2024));
    }

    [Fact]
    public void A_month_weekday_with_a_tail_spills_over_the_year()
    {
        // the last Saturday of December 2026 is the 26th: +6 days ends on 1 January 2027
        Assert.True(OccasionWeekdays.Contains("12-sat#-1", "+6", new DateOnly(2027, 1, 1), out int y));
        Assert.Equal(2026, y);
        Assert.False(OccasionWeekdays.Contains("12-sat#-1", "+6", new DateOnly(2027, 1, 2), out _));
        Assert.True(OccasionWeekdays.Contains("12-sat#2", "+1", new DateOnly(2026, 12, 13), out _));
    }

    // ---- easter-46: Fasnacht -----------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 2, 18)]   // Ash Wednesday: Easter 5 April
    [InlineData(2027, 2, 10)]   // Easter 28 March
    [InlineData(2028, 3, 1)]    // Easter 16 April
    public void Ash_wednesday_is_46_days_before_easter(int y, int m, int d)
    {
        var ash = new DateOnly(y, m, d);
        Assert.Equal(DayOfWeek.Wednesday, ash.DayOfWeek);
        Assert.Equal(ash, OccasionWeekdays.Opening("easter-46", y));
        Assert.True(OccasionWeekdays.Contains("easter-46", "easter-46", ash, out int startYear));
        Assert.Equal(y, startYear);
        Assert.False(OccasionWeekdays.Contains("easter-46", "easter-46", ash.AddDays(1), out _));
    }

    [Theory]
    [InlineData(2026, 2, 12, 2, 17)]   // Luzern: Schmutziger Donnerstag (easter-52) through Fasnachtsdienstag
    [InlineData(2027, 2, 4, 2, 9)]
    [InlineData(2028, 2, 24, 2, 29)]   // 2028 is a leap year
    public void Luzern_fasnacht_runs_from_the_thursday_before_ash_wednesday_for_six_days(int y, int fm, int fd, int tm, int td)
    {
        var from = new DateOnly(y, fm, fd);
        Assert.Equal(DayOfWeek.Thursday, from.DayOfWeek);
        Assert.Equal(from, OccasionWeekdays.Opening("easter-52", y));
                var last = new DateOnly(y, tm, td);
        Assert.Equal(DayOfWeek.Tuesday, last.DayOfWeek);
        Assert.True(OccasionWeekdays.Contains("easter-52", "+5", from, out _));
        Assert.True(OccasionWeekdays.Contains("easter-52", "+5", last, out int startYear));
        Assert.Equal(y, startYear);
        Assert.False(OccasionWeekdays.Contains("easter-52", "+5", last.AddDays(1), out _));
        Assert.False(OccasionWeekdays.Contains("easter-52", "+5", from.AddDays(-1), out _));
    }

    [Theory]
    [InlineData(2026, 2, 23)]   // Basel: Morgestraich is the Monday after Ash Wednesday, easter-41
    [InlineData(2027, 2, 15)]
    [InlineData(2028, 3, 6)]
    public void Basel_fasnacht_opens_on_the_monday_after_ash_wednesday(int y, int m, int d)
    {
        var morgestraich = new DateOnly(y, m, d);
        Assert.Equal(DayOfWeek.Monday, morgestraich.DayOfWeek);
        Assert.Equal(morgestraich, OccasionWeekdays.Opening("easter-41", y));
        Assert.True(OccasionWeekdays.Contains("easter-41", "+2", morgestraich.AddDays(2), out _));
        Assert.False(OccasionWeekdays.Contains("easter-41", "+2", morgestraich.AddDays(3), out _));
    }

    [Fact]
    public void Easter_offsets_may_be_positive_or_zero()
    {
        Assert.Equal(new DateOnly(2026, 4, 5), OccasionWeekdays.Opening("easter", 2026));
        Assert.Equal(new DateOnly(2026, 4, 6), OccasionWeekdays.Opening("easter+1", 2026));
        Assert.Equal(new DateOnly(2026, 4, 3), OccasionWeekdays.Opening("easter-2", 2026));
    }

    [Theory]
    [InlineData("easter-46", true)]
    [InlineData("easter", true)]
    [InlineData("easter+1", true)]
    [InlineData("04-mon#3", true)]
    [InlineData("fri#1", true)]
    [InlineData("easter-x", false)]
    [InlineData("easterly", false)]
    [InlineData("13-mon#3", false)]
    [InlineData("04-xyz#3", false)]
    [InlineData("04-mon#0", false)]
    [InlineData("04-15", false)]
    [InlineData("2027-04-19", false)]
    [InlineData("", false)]
    public void The_token_shapes(string from, bool token) => Assert.Equal(token, OccasionWeekdays.IsToken(from));

    [Fact]
    public void A_monthly_token_has_no_festival_opening()
    {
        Assert.Null(OccasionWeekdays.Opening("fri#1", 2026));
        Assert.Null(FestivalCalendar.SpanOf("fri#1", "+1", 2026));
    }

    [Fact]
    public void The_old_tokens_behave_as_before()
    {
        Assert.True(OccasionWeekdays.Contains("fri#1", "+1", new DateOnly(2026, 11, 6), out _));
        Assert.True(OccasionWeekdays.Contains("fri#1", "+1", new DateOnly(2026, 11, 7), out _));
        Assert.False(OccasionWeekdays.Contains("fri#1", "+1", new DateOnly(2026, 11, 8), out _));
        // a tail that is a typo (a month) is refused, as before; sixteen days (Montreux Jazz, #1394) is the longest festival
        Assert.False(OccasionWeekdays.Contains("fri#1", "+16", new DateOnly(2026, 11, 6), out _));
        Assert.True(OccasionWeekdays.Contains("07-fri#1", "+15", new DateOnly(2026, 7, 18), out _));
    }

    // ---- the instance's days -----------------------------------------------------------------------

    private static readonly (string, string)[] August = [("08-14", "08-24")];

    [Fact]
    public void The_day_counts_from_the_first_date()
    {
        Assert.Equal(1, FestivalCalendar.Day(August, new DateOnly(2027, 8, 14)));
        Assert.Equal(5, FestivalCalendar.Day(August, new DateOnly(2027, 8, 18)));
        Assert.Equal(11, FestivalCalendar.Day(August, new DateOnly(2027, 8, 24)));
        Assert.Equal(0, FestivalCalendar.Day(August, new DateOnly(2027, 8, 13)));
        Assert.Equal(0, FestivalCalendar.Day(August, new DateOnly(2027, 8, 25)));
    }

    [Fact]
    public void A_window_over_new_year_is_one_instance()
    {
        var wrap = new (string, string)[] { ("12-28", "01-03") };
        var jan = FestivalCalendar.Containing(wrap, new DateOnly(2028, 1, 2));
        Assert.NotNull(jan);
        Assert.Equal(2027, jan!.Value.StartYear);
        Assert.Equal(6, jan.Value.Span.Day(new DateOnly(2028, 1, 2)));
        Assert.Equal(7, jan.Value.Span.Length);
        Assert.Equal(2027, FestivalCalendar.Containing(wrap, new DateOnly(2027, 12, 28))!.Value.StartYear);
    }

    [Fact]
    public void A_token_instance_has_a_start_and_an_end()
    {
        var span = FestivalCalendar.SpanOf("easter-52", "+5", 2027)!.Value;
        Assert.Equal(new DateOnly(2027, 2, 4), span.Start);
        Assert.Equal(new DateOnly(2027, 2, 9), span.End);
        Assert.Equal(6, span.Length);
        var sechs = FestivalCalendar.SpanOf("04-mon#3", "04-mon#3", 2027)!.Value;
        Assert.Equal(1, sechs.Length);
        Assert.Equal(new DateOnly(2027, 4, 19), sechs.Start);
    }

    [Fact]
    public void An_instance_ends_the_day_after_its_last_date()
    {
        Assert.False(FestivalCalendar.Ended(August, 2027, new DateOnly(2027, 8, 24)));
        Assert.True(FestivalCalendar.Ended(August, 2027, new DateOnly(2027, 8, 25)));
        Assert.True(FestivalCalendar.Ended(August, 2026, new DateOnly(2027, 8, 14)));   // last year's
        Assert.False(FestivalCalendar.Ended(August, 2027, new DateOnly(2027, 8, 1)));   // not begun is not over
        // no window knows the instance (an operator removed it): over, so its Batzen are never stuck
        Assert.True(FestivalCalendar.Ended(Array.Empty<(string, string)>(), 2027, new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void Instance_keys_are_taken_apart()
    {
        Assert.Equal(2027, FestivalCalendar.YearOf("aelplerfest-2027"));
        Assert.Equal("aelplerfest", FestivalCalendar.FestivalOf("aelplerfest-2027"));
        Assert.Equal("fasnacht-luzern", FestivalCalendar.FestivalOf("fasnacht-luzern-2028"));
        Assert.Equal(0, FestivalCalendar.YearOf("nonsense"));
    }

    [Fact]
    public void February_29_is_clamped_in_a_common_year()
    {
        var span = FestivalCalendar.SpanOf("02-29", "03-02", 2027)!.Value;
        Assert.Equal(new DateOnly(2027, 2, 28), span.Start);
        Assert.Equal(new DateOnly(2028, 2, 29), FestivalCalendar.SpanOf("02-29", "03-02", 2028)!.Value.Start);
    }
}
