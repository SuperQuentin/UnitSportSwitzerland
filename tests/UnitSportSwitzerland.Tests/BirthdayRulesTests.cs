using UnitSport.Occasions.Birthdays;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The birthday rules (#1423, src/Occasions/Birthdays/BirthdayRules.cs, linked in).</summary>
public class BirthdayRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 1);

    [Fact]
    public void The_birthday_is_the_same_day_and_month_any_year()
    {
        var b = new Birthday(1, 11, 1996);
        Assert.True(BirthdayRules.IsToday(b, Today));
        Assert.True(BirthdayRules.IsToday(b, new DateOnly(2031, 11, 1)));
        Assert.False(BirthdayRules.IsToday(b, new DateOnly(2026, 11, 2)));
        Assert.False(BirthdayRules.IsToday(b, new DateOnly(2026, 10, 1)));
        Assert.True(BirthdayRules.IsToday(new Birthday(1, 11), Today));
    }

    [Fact]
    public void Nothing_entered_is_never_a_birthday()
    {
        Assert.False(Birthday.None.IsSet);
        Assert.False(BirthdayRules.IsToday(Birthday.None, Today));
        Assert.False(BirthdayRules.IsToday(new Birthday(31, 4), new DateOnly(2026, 4, 30)));
        Assert.False(BirthdayRules.GiftDue(Birthday.None, 0, Today));
    }

    [Fact]
    public void The_29_february_falls_on_the_28th_in_a_year_without_one()
    {
        var leap = new Birthday(29, 2, 2004);
        Assert.True(BirthdayRules.IsToday(leap, new DateOnly(2027, 2, 28)));
        Assert.False(BirthdayRules.IsToday(leap, new DateOnly(2027, 3, 1)));
        Assert.True(BirthdayRules.IsToday(leap, new DateOnly(2028, 2, 29)));
        Assert.False(BirthdayRules.IsToday(leap, new DateOnly(2028, 2, 28)));
    }

    [Fact]
    public void The_age_needs_the_year()
    {
        Assert.Equal(30, BirthdayRules.AgeIn(new Birthday(1, 11, 1996), Today));
        Assert.Null(BirthdayRules.AgeIn(new Birthday(1, 11), Today));
    }

    [Fact]
    public void Only_real_past_dates_are_valid()
    {
        Assert.True(BirthdayRules.Valid(29, 2, 0, Today));
        Assert.True(BirthdayRules.Valid(29, 2, 2004, Today));
        Assert.False(BirthdayRules.Valid(29, 2, 2003, Today));
        Assert.False(BirthdayRules.Valid(31, 6, 0, Today));
        Assert.False(BirthdayRules.Valid(0, 6, 0, Today));
        Assert.False(BirthdayRules.Valid(1, 13, 0, Today));
        Assert.False(BirthdayRules.Valid(2, 11, 2026, Today));
        Assert.True(BirthdayRules.Valid(1, 11, 2026, Today));
        Assert.False(BirthdayRules.Valid(1, 1, 1900, Today));
    }

    [Fact]
    public void One_gift_a_year_whatever_the_date_is_changed_to()
    {
        var b = new Birthday(1, 11);
        Assert.True(BirthdayRules.GiftDue(b, 0, Today));
        Assert.True(BirthdayRules.GiftDue(b, 2025, Today));
        Assert.False(BirthdayRules.GiftDue(b, 2026, Today));
        // given today, then the date moved to tomorrow: still no second gift this year
        Assert.False(BirthdayRules.GiftDue(new Birthday(2, 11), 2026, new DateOnly(2026, 11, 2)));
        Assert.True(BirthdayRules.GiftDue(new Birthday(2, 11), 2026, new DateOnly(2027, 11, 2)));
    }

    [Fact]
    public void Each_year_gives_the_next_collectible()
    {
        var seen = new HashSet<int>();
        for (int y = 2026; y < 2031; y++) seen.Add(BirthdayRules.CollectibleIndex(y, 5));
        Assert.Equal(5, seen.Count);
        Assert.NotEqual(BirthdayRules.CollectibleIndex(2026, 5), BirthdayRules.CollectibleIndex(2027, 5));
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(102, "102nd")]
    [InlineData(111, "111th")]
    public void Ordinals(int n, string text) => Assert.Equal(text, BirthdayRules.Ordinal(n));

    [Fact]
    public void The_lines_carry_the_age_only_when_given()
    {
        Assert.Equal("Happy 30th birthday, Anna!", BirthdayRules.Greeting("Anna", 30));
        Assert.Equal("Happy birthday, Anna!", BirthdayRules.Greeting("Anna", null));
        Assert.Equal("Happy birthday!", BirthdayRules.Greeting("", null));
        Assert.Contains("turns 30", BirthdayRules.Announcement("Anna", 30));
        Assert.DoesNotContain("turns", BirthdayRules.Announcement("Anna", null));
        Assert.Equal("12 people wished you a happy birthday today.", BirthdayRules.WishesToday(12));
        Assert.Equal("1 person wished you a happy birthday today.", BirthdayRules.WishesToday(1));
    }

    [Fact]
    public void A_show_seed_is_stable_and_differs_per_show()
    {
        Assert.Equal(BirthdayRules.ShowSeed("Anna", Today, 1), BirthdayRules.ShowSeed("Anna", Today, 1));
        Assert.NotEqual(BirthdayRules.ShowSeed("Anna", Today, 1), BirthdayRules.ShowSeed("Anna", Today, 2));
        Assert.True(BirthdayRules.ShowSeed("Anna", Today, 1) >= 0);
    }
}

/// <summary>The birthday fireworks (#1423, <c>FireworkShow.Birthday</c>).</summary>
public class BirthdayShowTests
{
    [Fact]
    public void A_birthday_show_is_a_minute_from_its_seed_with_no_swiss_cross()
    {
        var a = UnitSport.Occasions.Festivals.FireworkShow.Birthday(7);
        var b = UnitSport.Occasions.Festivals.FireworkShow.Birthday(7);
        Assert.Equal(a.Shells, b.Shells);
        Assert.Equal(BirthdayRules.ShowSeconds, a.Duration);
        Assert.All(a.Shells, s => Assert.InRange(s.T, 0, a.Duration));
        Assert.All(a.Shells, s => Assert.NotEqual(UnitSport.Occasions.Festivals.ShellKind.SwissCross, s.Kind));
        Assert.Equal(28, a.Shells.Length);
        Assert.NotEqual(a.Shells, UnitSport.Occasions.Festivals.FireworkShow.Birthday(8).Shells);
    }
}
