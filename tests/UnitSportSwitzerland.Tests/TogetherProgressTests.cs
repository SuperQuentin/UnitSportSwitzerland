using System.Text.Json.Nodes;
using UnitSport.Adventure;
using UnitSport.Adventure.Together;
using UnitSport.Items;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The week, the punch card and the mentors (#1266 parts 6-7).</summary>
public class TogetherProgressTests
{
    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void ZurichDaysAcrossDst()
    {
        // winter: UTC+1; 23:30 UTC on 1 Feb is 00:30 on the 2nd in Zurich
        Assert.Equal(20270202, Zurich.DayAndWeek(Utc(2027, 2, 1, 23, 30)).Day);
        // summer: UTC+2; 22:30 UTC on 1 July is 00:30 on the 2nd
        Assert.Equal(20270702, Zurich.DayAndWeek(Utc(2027, 7, 1, 22, 30)).Day);
        Assert.Equal(20270701, Zurich.DayAndWeek(Utc(2027, 7, 1, 21, 30)).Day);
        // the change night, 28 March 2027: 00:59 UTC is CET (01:59), 01:00 UTC is CEST (03:00)
        Assert.Equal(1, Zurich.Local(Utc(2027, 3, 28, 0, 59)).Hour);
        Assert.Equal(3, Zurich.Local(Utc(2027, 3, 28, 1, 0)).Hour);
        // and back on 31 October 2027
        Assert.Equal(2, Zurich.Local(Utc(2027, 10, 31, 0, 59)).Hour);
        Assert.Equal(2, Zurich.Local(Utc(2027, 10, 31, 1, 0)).Hour);
    }

    [Fact]
    public void IsoWeeksMondayInZurichAndWeek53()
    {
        // Sunday 23:30 UTC in winter is Monday 00:30 in Zurich: the new week
        var sunday = Zurich.DayAndWeek(Utc(2027, 1, 10, 22, 30));
        var monday = Zurich.DayAndWeek(Utc(2027, 1, 10, 23, 30));
        Assert.Equal(sunday.Week + 1, monday.Week);
        // 2026 has 53 ISO weeks: 31 Dec 2026 is week 53 of 2026, 1 Jan 2027 too
        Assert.Equal(202653, Zurich.DayAndWeek(Utc(2026, 12, 31, 12)).Week);
        Assert.Equal(202653, Zurich.DayAndWeek(Utc(2027, 1, 1, 12)).Week);
        Assert.Equal(202701, Zurich.DayAndWeek(Utc(2027, 1, 4, 12)).Week);
    }

    [Fact]
    public void AStampOnceADayAndTheWeeksChestOnceAWeek()
    {
        var p = new TogetherProgress();
        var t0 = Utc(2027, 5, 3, 8);   // a Monday
        int stamps = 0, chests = 0;
        for (int s = 0; s < 3600; s += 60)
        {
            var e = p.Add(60, t0.AddSeconds(s));
            if (e.Stamp) stamps++;
            if (e.WeekChest) chests++;
        }
        Assert.Equal(1, stamps);
        Assert.Equal(1, chests);
        // the next day: a second stamp, no second chest
        for (int s = 0; s < 3600; s += 60)
        {
            var e = p.Add(60, t0.AddDays(1).AddSeconds(s));
            if (e.Stamp) stamps++;
            if (e.WeekChest) chests++;
        }
        Assert.Equal(2, stamps);
        Assert.Equal(1, chests);
        // next week: a chest again
        Assert.True(p.Add(TogetherProgress.WeekChestSeconds, t0.AddDays(7)).WeekChest);
    }

    [Fact]
    public void StampsNeverResetAndCardsFillAtTen()
    {
        var p = new TogetherProgress();
        var day = Utc(2027, 1, 1, 12);
        TogetherProgress.Earned last = default;
        for (int i = 0; i < 10; i++)
        {
            // weeks apart: nothing is lost by a gap
            last = p.Add(TogetherProgress.StampSeconds, day.AddDays(i * 17));
            Assert.True(last.Stamp);
        }
        Assert.True(last.CardFull);
        Assert.Equal(1, p.Cards);
        Assert.Equal(0, p.Stamps);
        Assert.Equal("Gspänli", last.Title);
        Assert.False(last.Hat);
        p.Cards = 9;
        p.Stamps = 9;
        var tenth = p.Add(TogetherProgress.StampSeconds, day.AddDays(400));
        Assert.True(tenth.CardFull && tenth.Hat);
        Assert.Equal("Unzertrennlich", tenth.Title);
        Assert.Equal(ItemId.StammgastHat, TogetherProgress.CardHat);
    }

    [Fact]
    public void FriendsAreShortKeysCappedAt128()
    {
        var p = new TogetherProgress();
        Assert.Equal("9f2c41", TogetherProgress.Short("9f2c41aa77"));
        Assert.Equal("abc123", TogetherProgress.Short("acct:ABC123-4"));
        Assert.Equal("", TogetherProgress.Short("peer:7"));
        for (int i = 0; i < 200; i++) p.Meet(i.ToString("x6"));
        Assert.Equal(TogetherProgress.MaxFriends, p.Friends.Count);
        Assert.Equal(72.ToString("x6"), p.Friends[0]);   // the oldest went first
        Assert.False(p.Meet(199.ToString("x6")));
    }

    [Fact]
    public void TheSectionRoundTripsAndAnEmptyOneReads()
    {
        var book = new PlayerProgress("k");
        var t = book.Get(TogetherProgress.Section);
        t.Stamps = 3;
        t.Friends.Add("abcdef");
        t.Done["summit"] = 4;
        book.Touch(TogetherProgress.Section);
        var json = AdventureBook.ToNode(book).ToJsonString();
        var back = AdventureBook.FromJson(json).Get(TogetherProgress.Section);
        Assert.Equal(3, back.Stamps);
        Assert.Equal(4, back.Done["summit"]);
        Assert.Contains("abcdef", back.Friends);
        // a v0 empty section (an object with nothing in it) reads as fresh
        var fresh = new PlayerProgress("j");
        fresh.SetRaw("together", 1, new JsonObject());
        Assert.Equal(0, fresh.Get(TogetherProgress.Section).Cards);
    }

    // ---- mentors ------------------------------------------------------------------------------------

    private static TogetherProgress Veteran(string m)
    {
        var p = new TogetherProgress();
        for (int i = 0; i < MentorRules.VeteranTimes; i++) MentorRules.Did(p, m);
        return p;
    }

    [Fact]
    public void NewcomerAndVeteran()
    {
        var vet = Veteran(MentorRules.Summit);
        var fresh = new TogetherProgress();
        Assert.True(MentorRules.Veteran(vet, MentorRules.Summit));
        Assert.False(MentorRules.Veteran(fresh, MentorRules.Summit));
        Assert.True(MentorRules.Newcomer(fresh, MentorRules.Summit));
        Assert.False(MentorRules.Newcomer(vet, MentorRules.Summit));
        Assert.True(MentorRules.Counts(MentorRules.Summit, "03ab7e11", fresh, "9f2c4100", vet));
        Assert.False(MentorRules.Counts(MentorRules.Glacier, "03ab7e11", fresh, "9f2c4100", vet));   // a summit veteran is no glacier one
    }

    [Fact]
    public void OncePerPairAndOncePerNewcomer()
    {
        var vet = Veteran(MentorRules.Summit);
        var a = new TogetherProgress();
        var (got, mentor, title) = MentorRules.Apply(MentorRules.Summit, "03ab7e11", a, vet);
        Assert.Equal(ItemId.ErstbesteigungCap, got);
        Assert.Equal(ItemId.BergfuehrerCap, mentor);
        Assert.Null(title);
        Assert.Contains("summit:03ab7e", vet.Mentees);
        Assert.False(MentorRules.Counts(MentorRules.Summit, "03ab7e11", a, "9f2c4100", vet), "the same newcomer, again");
        var b = new TogetherProgress();
        var (_, second, _) = MentorRules.Apply(MentorRules.Summit, "77aa0011", b, vet);
        Assert.Null(second);   // the guide's cap only the first time
        // and another veteran cannot mentor a mentored newcomer again
        Assert.False(MentorRules.Counts(MentorRules.Summit, "03ab7e11", a, "55555555", Veteran(MentorRules.Summit)));
    }

    [Fact]
    public void SessionKeysNeverCount()
    {
        var vet = Veteran(MentorRules.Ride);
        Assert.False(MentorRules.Counts(MentorRules.Ride, "peer:3", new TogetherProgress(), "9f2c4100", vet));
        Assert.False(MentorRules.Counts(MentorRules.Ride, "03ab7e11", new TogetherProgress(), "peer:4", vet));
        Assert.False(MentorRules.Counts(MentorRules.Ride, "local", new TogetherProgress(), "9f2c4100", vet));
    }

    [Fact]
    public void TitlesAtFiveAndTwenty()
    {
        var vet = Veteran(MentorRules.Build);
        string? title = null;
        for (int i = 0; i < 5; i++) title = MentorRules.Apply(MentorRules.Build, $"{i:x6}00", new TogetherProgress(), vet).Title;
        Assert.Equal("Bergführer", title);
        Assert.Equal("Bergführer mit eidg. Fachausweis", MentorRules.Title(20));
    }

    [Fact]
    public void ItemIdsStayInTheBlockAndLeaveTheAlcoholPlansFree()
    {
        var ids = new[] { ItemId.WhiteWine, ItemId.Kirsch, ItemId.ErstbesteigungCap, ItemId.GletscherScarf, ItemId.MitfahrerGloves, ItemId.ZimmermannCap,
            ItemId.BergfuehrerCap, ItemId.SeilschaftScarf, ItemId.FahrlehrerGloves, ItemId.PolierHat, ItemId.StammgastHat };
        Assert.All(ids, id => Assert.InRange((int)id, 480, 499));
        Assert.DoesNotContain(ids, id => (int)id is >= 486 and <= 488);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(MentorRules.All, m => Assert.Contains(m.Newcomer, ids));
    }
}
