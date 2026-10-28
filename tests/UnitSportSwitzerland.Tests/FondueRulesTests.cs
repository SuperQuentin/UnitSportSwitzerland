using UnitSport.Adventure.Together;
using UnitSport.Items;
using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The shared pot's rules and its book (#1266 part 5).</summary>
public class FondueRulesTests
{
    [Fact]
    public void IngredientsGiveTheirBuffs()
    {
        byte plain = FondueRules.BuffMask(false, false);
        Assert.True(FondueRules.Has(plain, StaminaBuff.Fed));
        Assert.False(FondueRules.Has(plain, StaminaBuff.Warm));
        byte all = FondueRules.BuffMask(true, true);
        Assert.True(FondueRules.Has(all, StaminaBuff.Warm) && FondueRules.Has(all, StaminaBuff.Hearty));
        Assert.Equal(ItemId.WhiteWine, FondueRules.ItemOf(Ingredient.Wine));
        Assert.Equal(Ingredient.Kirsch, FondueRules.Of(ItemId.Kirsch));
        Assert.Null(FondueRules.Of(ItemId.Apple));
        Assert.Equal(480, (int)ItemId.WhiteWine);
        Assert.Equal(481, (int)ItemId.Kirsch);
    }

    [Fact]
    public void DipsAndTheFeast()
    {
        Assert.Equal(12, FondueRules.Dips(2));
        Assert.Equal(2, FondueRules.CheeseNeeded(1));
        Assert.Equal(5, FondueRules.CheeseNeeded(5));
        Assert.Equal(600f, FondueRules.FedSeconds(1f));
        Assert.Equal(150f, FondueRules.FedSeconds(FondueRules.Quality(0.9f, 0f, splitted: true)));
        Assert.Equal(0.6f, FondueRules.Quality(0.8f, 0.2f, false), 4);
    }

    [Fact]
    public void BreadLossOdds()
    {
        Assert.Equal(0.03, FondueRules.BreadLossChance(1f, false), 6);
        Assert.Equal(0.15, FondueRules.BreadLossChance(0f, false), 6);
        Assert.Equal(0.20, FondueRules.BreadLossChance(0f, true), 6);
    }

    [Fact]
    public void ForfeitsWithAndWithoutCashAndUnderMild()
    {
        Assert.True(FondueRules.Allowed(ForfeitMode.All, Forfeit.Round, 15, 3));
        Assert.False(FondueRules.Allowed(ForfeitMode.All, Forfeit.Round, 14, 3));
        Assert.False(FondueRules.Allowed(ForfeitMode.Mild, Forfeit.Round, 1000, 3));
        Assert.True(FondueRules.Allowed(ForfeitMode.Mild, Forfeit.Yodel, 0, 3));
        Assert.False(FondueRules.Allowed(ForfeitMode.Off, Forfeit.Line, 0, 3));
        Assert.Equal(ForfeitMode.Mild, FondueRules.ParseMode("mild"));
        Assert.Equal(ForfeitMode.All, FondueRules.ParseMode(null));
        Assert.Equal(15, FondueRules.RoundCost(3));
    }

    [Fact]
    public void TheRacletteWindow()
    {
        Assert.Equal(1f, FondueRules.ScrapeQ(0.0));
        Assert.Equal(1f, FondueRules.ScrapeQ(0.29));
        Assert.True(FondueRules.ScrapeQ(-1.0) > FondueRules.ScrapeQ(1.0), "raw beats burnt");
        Assert.Equal(0.1f, FondueRules.ScrapeQ(3.0));
        double p = FondueRules.BubblePeriod(17);
        Assert.InRange(p, 3, 5);
        Assert.Equal(0.0, FondueRules.SincePeak(17, p * 4), 6);
        Assert.True(FondueRules.SincePeak(17, p * 4 - 0.2) < 0);
    }

    [Fact]
    public void APotFromStartToFeast()
    {
        var book = new PotBook();
        var p = book.Start(1, PotSpot.Campfire, "fire:1", 0, 0, 0, raclette: false, now: 0)!;
        Assert.Null(book.Start(1, PotSpot.Hut, "hut:x", 0, 0, 0, false, 0));
        Assert.Null(book.Sit(p, 2));
        Assert.Same(p, book.Of(2));
        Assert.NotNull(book.Add(p, 3, Ingredient.Cheese));          // not seated
        Assert.Null(book.Add(p, 1, Ingredient.Cheese));
        Assert.False(p.Cooking);
        Assert.Null(book.Add(p, 2, Ingredient.Cheese));
        Assert.True(p.Cooking);
        Assert.Equal(12, p.DipsLeft);
        Assert.Equal(DipResult.NoBread, PotBook.Dip(p, 1, 1, 0.99));
        Assert.Null(book.Add(p, 1, Ingredient.Bread));
        Assert.NotNull(book.Add(p, 1, Ingredient.Bread));            // one in hand
        Assert.Null(book.Add(p, 1, Ingredient.Wine));
        Assert.NotNull(book.Add(p, 2, Ingredient.Wine));
        Assert.Equal(DipResult.Ok, PotBook.Dip(p, 1, 1, 0.99));
        Assert.Equal(DipResult.TooSoon, PotBook.Dip(p, 1, 2, 0.99));
        Assert.Equal(DipResult.LostBread, PotBook.Dip(p, 1, 3.5, 0.0));
        Assert.Equal(DipResult.NoBread, PotBook.Dip(p, 1, 6, 0.99));
        Assert.Equal(1, p.BreadsLost);
        Assert.Equal(10, p.DipsLeft);
        Assert.Equal((byte)(1 << 1 | 1 << 2 | 1 << 3), p.SeatBits(1));   // put in cheese, bread and wine; no bread in hand now
        Assert.Null(book.Add(p, 2, Ingredient.Bread));
        for (int i = 0; i < 10; i++) PotBook.Dip(p, 2, 10 + i * 2.1, 0.99);
        Assert.True(PotBook.Done(p));
        book.End(p);
        Assert.Null(book.Of(1));
    }

    [Fact]
    public void StirringCountsOncePerPeerAndNotForACrowd()
    {
        var book = new PotBook();
        var p = book.Start(1, PotSpot.Hut, "hut:a", 0, 0, 0, false, 0)!;
        book.Sit(p, 2);
        book.Sit(p, 3);
        Assert.False(book.Stir(p, 1, 0.9f, 0.5));                      // not cooking yet
        for (int i = 0; i < 3; i++) book.Add(p, 1, Ingredient.Cheese);
        Assert.True(book.Stir(p, 1, 0.9f, 1.0));
        Assert.False(book.Stir(p, 1, 0.9f, 1.5));                      // too soon for that peer
        Assert.True(book.Stir(p, 2, 0.9f, 1.6));
        Assert.False(book.Stir(p, 3, 0.9f, 2.0));                      // the third spoon in 3 s
        Assert.True(book.Stir(p, 3, 0.9f, 4.2));
        Assert.Equal(3, p.Qs.Count);
        // idle with no wine: the meter rises 0.06 a second until it splits
        for (int i = 0; i < 40; i++) PotBook.Tick(p, 1f, 10 + i);
        Assert.True(p.Splitted);
        Assert.False(book.Stir(p, 1, 0.9f, 60));
        Assert.True(p.Quality <= FondueRules.SplitCap);
    }

    [Fact]
    public void EveryoneLeavingEndsIt()
    {
        var book = new PotBook();
        var p = book.Start(1, PotSpot.Venue, "v:1", 0, 0, 0, false, 0)!;
        book.Sit(p, 2);
        book.Leave(p, 1);
        Assert.False(p.Ended);
        book.PeerLeft(2);
        Assert.True(p.Ended);
        Assert.Empty(book.All);
    }

    [Fact]
    public void RacletteNeedsPotatoes()
    {
        var book = new PotBook();
        var p = book.Start(1, PotSpot.Campfire, "fire:2", 0, 0, 0, raclette: true, 0)!;
        book.Sit(p, 2);
        book.Add(p, 1, Ingredient.Cheese);
        book.Add(p, 2, Ingredient.Cheese);
        Assert.False(p.Cooking);
        Assert.NotNull(book.Add(p, 1, Ingredient.Bread));
        for (int i = 0; i < 4; i++) book.Add(p, 1, Ingredient.Potato);
        Assert.True(p.Cooking);
        Assert.Equal(DipResult.Ok, PotBook.Dip(p, 2, 1, 0.0));   // no bread to lose at a raclette
    }
}
