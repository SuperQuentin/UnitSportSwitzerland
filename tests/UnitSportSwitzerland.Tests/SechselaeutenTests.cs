using System.Text.Json;
using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Sechselaeuten;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Sechseläuten (#1358): the festival's dates and site, the draw from the real burns, the buckets, the pool shared by closeness, the round and what it tells. Pure.</summary>
public class SechselaeutenTests
{
    private static SechselaeutenRules.Bet Bet(string key, int bucket, int stake = 10, bool bot = false) => new(key, key, bucket, stake, bot);

    [Fact]
    public void The_festival_is_in_the_catalogue_and_sound()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.SechselaeutenId);
        Assert.NotNull(def);
        Assert.Empty(def!.Problems());
        Assert.Equal("sechselaeuten", def.Id);
        Assert.Equal(1, def.Length);
        foreach (string g in new[] { SechselaeutenRules.PurseGame, SechselaeutenRules.BetGame }) Assert.True(def.IsOpen(g, 1));
        Assert.Equal(200, def.TokenCap);
        Assert.Contains("Zürich", def.Sites[0].Name);
    }

    [Theory]
    [InlineData(2026, 4, 20)]
    [InlineData(2027, 4, 19)]
    [InlineData(2028, 4, 17)]
    public void It_is_the_third_monday_of_april_for_one_day(int year, int month, int day)
    {
        var def = FestivalCatalog.Get(FestivalCatalog.SechselaeutenId)!;
        var (from, to) = def.Schedule[0];
        var span = FestivalCalendar.SpanOf(from, to, year);
        Assert.NotNull(span);
        Assert.Equal(new DateOnly(year, month, day), span!.Value.Start);
        Assert.Equal(DayOfWeek.Monday, span.Value.Start.DayOfWeek);
        Assert.Equal(span.Value.Start, span.Value.End);
    }

    [Fact]
    public void The_light_is_the_sky_crossing_six_in_the_evening_and_never_a_first_look()
    {
        Assert.True(SechselaeutenRules.Lights(17.99, 18.01));
        Assert.False(SechselaeutenRules.Lights(null, 18.5));      // a first look in the evening waits for the next one
        Assert.False(SechselaeutenRules.Lights(18.1, 18.2));
        Assert.False(SechselaeutenRules.Lights(10, 12));
        Assert.False(SechselaeutenRules.Lights(21.9, 23));
        Assert.False(SechselaeutenRules.Lights(23.9, 0.1));       // the midnight wrap
    }

    [Fact]
    public void The_draw_is_a_function_of_the_seed_and_fits_the_real_burns()
    {
        Assert.Equal(SechselaeutenRules.Draw(7), SechselaeutenRules.Draw(7));
        Assert.NotEqual(SechselaeutenRules.Draw(7), SechselaeutenRules.Draw(8));
        var draws = Enumerable.Range(0, 20000).Select(i => SechselaeutenRules.Draw(i * 7919 + 13)).Order().ToArray();
        Assert.InRange(draws[draws.Length / 2], 12.3, 13.7);                                   // the median is about 13 minutes
        Assert.All(draws, d => Assert.InRange(d, SechselaeutenRules.MinMinutes, SechselaeutenRules.MaxMinutes));
        double over40 = draws.Count(d => d > 40) / (double)draws.Length;
        Assert.InRange(over40, 0.015, 0.06);                                                   // a long tail: roughly one in thirty
        Assert.InRange(draws.Count(d => d < 5) / (double)draws.Length, 0.02, 0.10);           // and a quick one now and then
        // every bucket is possible
        var hit = draws.Select(SechselaeutenRules.BucketOf).Distinct().Order().ToArray();
        Assert.Equal(Enumerable.Range(1, SechselaeutenRules.Buckets), hit);
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(5.0, 1)]
    [InlineData(5.01, 2)]
    [InlineData(13.4, 3)]
    [InlineData(55.0, 11)]
    [InlineData(55.1, 12)]
    [InlineData(75, 12)]
    public void Minutes_fall_in_five_minute_buckets(double minutes, int bucket) => Assert.Equal(bucket, SechselaeutenRules.BucketOf(minutes));

    [Fact]
    public void The_labels_read_as_minutes()
    {
        Assert.Equal("0-5 min", SechselaeutenRules.Label(1));
        Assert.Equal("10-15 min", SechselaeutenRules.Label(3));
        Assert.Equal("55+ min", SechselaeutenRules.Label(12));
        Assert.Equal(3, SechselaeutenRules.DefaultBucket);   // the median burn's bucket
    }

    [Fact]
    public void The_pool_is_shared_by_closeness_and_adds_up_exactly()
    {
        var bets = new[] { Bet("a", 3), Bet("b", 4), Bet("c", 8), Bet("d", 3), Bet("e", 12) };
        var shares = SechselaeutenRules.Share(bets, 13.4);   // bucket 3
        Assert.Equal(50, shares.Values.Sum());
        Assert.True(shares["a"] == shares["d"]);
        Assert.True(shares["a"] > shares["b"] && shares["b"] >= shares["c"] && shares["c"] >= shares["e"], string.Join(",", shares));
        Assert.True(shares["a"] > 20, "the exact bucket takes most of the pool");
        // the nearer bet always gets at least as much as the farther one
        foreach (double minutes in new[] { 2.0, 7.0, 13.0, 22.0, 33.0, 58.0 })
        {
            var s = SechselaeutenRules.Share(bets, minutes);
            Assert.Equal(50, s.Values.Sum());
            int hit = SechselaeutenRules.BucketOf(minutes);
            foreach (var x in bets)
                foreach (var y in bets)
                    if (Math.Abs(x.Bucket - hit) < Math.Abs(y.Bucket - hit)) Assert.True(s[x.Key] >= s[y.Key], $"{minutes}: {x.Key} nearer than {y.Key}");
        }
    }

    [Fact]
    public void A_lone_bettor_gets_the_stake_back_and_a_bot_wins_nothing()
    {
        Assert.Equal(10, SechselaeutenRules.Share([Bet("a", 9)], 4)["a"]);
        var shares = SechselaeutenRules.Share([Bet("a", 9), Bet("bot", 1, 0, true)], 4);
        Assert.Equal(10, shares["a"]);          // the bot's exact guess takes nothing: it staked nothing
        Assert.False(shares.ContainsKey("bot"));
        Assert.Empty(SechselaeutenRules.Share([Bet("bot", 1, 0, true)], 4));
        Assert.Empty(SechselaeutenRules.Share([], 4));
    }

    [Fact]
    public void The_shares_of_odd_pools_are_whole_batzen_and_the_same_every_time()
    {
        var bets = Enumerable.Range(0, 7).Select(i => Bet($"p{i}", 1 + i, 10 + i)).ToArray();
        var a = SechselaeutenRules.Share(bets, 17.2);
        var b = SechselaeutenRules.Share(bets.Reverse().ToArray(), 17.2);
        Assert.Equal(bets.Sum(x => x.Stake), a.Values.Sum());
        Assert.Equal(a.OrderBy(k => k.Key), b.OrderBy(k => k.Key));
    }

    [Fact]
    public void A_round_keeps_the_time_secret_until_the_head_bursts()
    {
        var round = new BooggRound("sechselaeuten-2027", 1);
        round.Add(Bet("lea", 3));
        round.Add(Bet("tom", 6));
        Assert.Equal(-1, round.Minutes);
        Assert.Equal(BooggPhase.Betting, round.Phase);
        Assert.False(round.Due(1e9));

        round.Light(100, 4242, 6.0, _ => 13.4375);
        Assert.Equal(BooggPhase.Burning, round.Phase);
        Assert.Equal(-1, round.Minutes);
        string json = JsonSerializer.Serialize(round.ViewFor("lea"));
        Assert.Contains("\"Minutes\":-1", json);
        // neither the minutes nor the moment of the bang (light + 13.4375 x 6 s = 180.625) are anywhere in what a player is told
        Assert.DoesNotContain("13.4", json);
        Assert.DoesNotContain("180.6", json);
        Assert.Equal(0, round.ViewFor("lea").DoneAt);
        Assert.Empty(round.ViewFor("lea").Board);
        Assert.Equal(100, round.ViewFor("lea").LitAt);                 // the light is public

        Assert.False(round.Due(180.6));
        Assert.True(round.Due(180.7));
        Assert.Equal(13.4375, round.Bang(181));
        Assert.Equal(BooggPhase.Done, round.Phase);
        var view = round.ViewFor("lea");
        Assert.Equal(13.4375, view.Minutes);
        Assert.Equal(181, view.DoneAt);
        Assert.Equal(2, view.Board.Length);
        Assert.Equal(round.Shares["lea"], view.Won);
        Assert.Equal(20, round.Shares.Values.Sum());
    }

    [Fact]
    public void Bets_close_at_the_light_and_are_one_a_player()
    {
        var round = new BooggRound("x-2027", 1);
        Assert.Null(round.WhyNotBet("a", 3));
        Assert.NotNull(round.WhyNotBet("a", 0));
        Assert.NotNull(round.WhyNotBet("a", 13));
        round.Add(Bet("a", 3));
        Assert.Contains("10-15", round.WhyNotBet("a", 5));
        Assert.Null(round.WhyNotBet("b", 5));
        round.Light(1, 1, 6);
        Assert.Contains("closed", round.WhyNotBet("b", 5));
        Assert.Equal(10, round.Pool);
    }

    [Fact]
    public void A_round_burns_for_the_minutes_it_drew_at_the_pace_of_the_clock()
    {
        var round = new BooggRound("x-2027", 1);
        round.Light(0, 99, 0.4, _ => 20);
        Assert.False(round.Due(7.9));
        Assert.True(round.Due(8.0));
        // the real draw at the real pace: the median burn is a minute and a quarter
        Assert.Equal(78, SechselaeutenRules.MedianMinutes * SechselaeutenRules.DefaultSecondsPerMinute);
    }

    [Fact]
    public void The_ground_is_a_function_of_the_site()
    {
        var site = new ResolvedSite("test", 2600000, 1200000, 120, false, null, "x");
        Assert.True(SechselaeutenRules.NearPyre(site, 2600000, 1200010));
        Assert.False(SechselaeutenRules.NearPyre(site, 2600000, 1200020));
        var w = SechselaeutenRules.WeibelAt(site);
        Assert.True(SechselaeutenRules.NearWeibel(site, w.E + 1, w.N));
        Assert.False(SechselaeutenRules.NearWeibel(site, w.E + 10, w.N));
        // the pyre's ring of banners is clear of the pyre and the Weibel
        for (int k = 0; k < SechselaeutenRules.Banners; k++)
        {
            var b = SechselaeutenRules.BannerAt(site, k);
            Assert.InRange(SechselaeutenRules.Dist(b.E, b.N, SechselaeutenRules.PyreAt(site)), SechselaeutenRules.BannerRing - 0.01, SechselaeutenRules.BannerRing + 0.01);
        }
    }

    [Fact]
    public void The_cervelat_is_in_the_festivals_item_block()
    {
        Assert.InRange((int)ItemId.BooggCervelat, 510, 514);
    }
}
