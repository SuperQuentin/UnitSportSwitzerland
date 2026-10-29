using UnitSport.Adventure.Alcohol;
using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Christmas;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Christmas market (#1348): the festival's dates and sites, the stalls, the wares and their prices, the stall-hop's pay, Batzen spent. Pure.</summary>
public class ChristmasTests
{
    private static ResolvedSite Site() => new("the test field", 2600000, 1200000, 90, false, null, "Christmas market at the test field");

    // ---- the festival ------------------------------------------------------------------------------

    [Fact]
    public void The_festival_is_in_the_catalogue_and_sound()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.ChristmasMarketId);
        Assert.NotNull(def);
        Assert.Empty(def!.Problems());
        Assert.Equal("christmasmarket", def.Id);
        Assert.Equal(29, def.Length);   // 25 November to 23 December
        Assert.True(def.IsOpen(ChristmasRules.StallGame, 1));
        Assert.False(def.IsOpen(ChristmasRules.SamichlausGame, ChristmasRules.SamichlausDay - 1));
        Assert.True(def.IsOpen(ChristmasRules.SamichlausGame, ChristmasRules.SamichlausDay));
    }

    [Fact]
    public void Day_twelve_is_the_sixth_of_december()
    {
        var span = FestivalCalendar.SpanOf("11-25", "12-23", 2027)!.Value;
        Assert.Equal(new DateOnly(2027, 11, 25), span.Start);
        Assert.Equal(new DateOnly(2027, 12, 23), span.End);
        Assert.Equal(ChristmasRules.SamichlausDay, span.Day(new DateOnly(2027, 12, 6)));
        Assert.Equal(1, span.Day(new DateOnly(2027, 11, 25)));
    }

    [Fact]
    public void The_market_is_held_in_the_first_real_place_the_region_has_and_moves_to_a_town_otherwise()
    {
        var def = FestivalCatalog.ChristmasMarket();
        Assert.Equal(6, def.Sites.Length);
        // the western region: Montreux (and Genève) but not Basel, Zürich, Bern or Luzern
        var montreux = FestivalSites.Resolve(def, (e, n) => e < 2570000 && n < 1160000, []);
        Assert.False(montreux!.Relocated);
        Assert.Contains("Montreux", montreux.Name);
        var basel = FestivalSites.Resolve(def, (_, _) => true, []);
        Assert.Contains("Basel", basel!.Name);
        var elsewhere = FestivalSites.Resolve(def, (e, n) => Math.Abs(e - 2700000) < 3000 && Math.Abs(n - 1230000) < 3000, [new TownRef("Wil", 2701000, 1231000, 400)]);
        Assert.True(elsewhere!.Relocated);
    }

    [Fact]
    public void A_market_is_worth_a_few_hundred_francs_and_never_a_travelcard()
    {
        var def = FestivalCatalog.ChristmasMarket();
        Assert.Equal(600, def.TokenCap);
        Assert.True(def.MilestoneTotal <= def.TokenCap * FestivalDef.MilestoneShare);
        // a day's stall-hop is eight tastes and the Marktgang, and that is the game's whole day cap
        Assert.Equal(ChristmasRules.DayMax, def.Game(ChristmasRules.StallGame)!.DayCap);
        Assert.Equal(80, ChristmasRules.DayMax);
        // a Samichlaus's bag fits under his own cap
        Assert.True(ChristmasRules.SamichlausBatzen <= def.Game(ChristmasRules.SamichlausGame)!.DayCap);
        // the cap is reached in under ten days of stall-hopping
        Assert.True(ChristmasRules.DayMax * 8 >= def.TokenCap);
    }

    // ---- the stalls ----------------------------------------------------------------------------------

    [Fact]
    public void Eight_stalls_two_of_each_ware_in_two_rows_facing_the_lane()
    {
        var site = Site();
        var stalls = ChristmasRules.Layout(site);
        Assert.Equal(8, stalls.Length);
        foreach (var kind in Enum.GetValues<StallKind>()) Assert.Equal(2, stalls.Count(s => s.Kind == kind));
        // each row offers all four wares
        Assert.Equal(4, stalls.Take(4).Select(s => s.Kind).Distinct().Count());
        Assert.Equal(4, stalls.Skip(4).Select(s => s.Kind).Distinct().Count());
        // a stall stands 4 m from the next along its row and the two rows 8 m apart, all inside the site
        for (int i = 0; i < stalls.Length; i++)
            for (int j = i + 1; j < stalls.Length; j++)
            {
                double d = Math.Sqrt(Math.Pow(stalls[i].E - stalls[j].E, 2) + Math.Pow(stalls[i].N - stalls[j].N, 2));
                Assert.True(d >= 3.99, $"stalls {i} and {j} are {d:F1} m apart");
            }
        Assert.All(stalls, s => Assert.True(site.DistanceTo(s.E, s.N) < site.RadiusM * 0.5));
        // the counters face the lane: the north row (+N) towards the south, the south row the other way
        Assert.All(stalls.Take(4), s => Assert.Equal(0f, s.Yaw));
        Assert.All(stalls.Skip(4), s => Assert.Equal(MathF.PI, s.Yaw, 3));
        Assert.True(stalls.Take(4).All(s => s.N > site.N) && stalls.Skip(4).All(s => s.N < site.N));
    }

    [Fact]
    public void In_front_of_a_stall_that_stall_is_the_nearest_and_far_away_there_is_none()
    {
        var site = Site();
        foreach (var s in ChristmasRules.Layout(site))
            Assert.Equal(s.Index, ChristmasRules.NearestStall(site, s.E, s.N + (s.Yaw == 0f ? -2.0 : 2.0)));
        Assert.Equal(-1, ChristmasRules.NearestStall(site, site.E + 70, site.N + 70));
        Assert.Equal(-1, ChristmasRules.NearestStall(site, site.E, site.N + 40));
    }

    [Fact]
    public void The_samichlaus_and_the_tree_stand_at_the_two_ends_of_the_lane()
    {
        var site = Site();
        var (se, sn) = ChristmasRules.SamichlausAt(site);
        var (te, tn) = ChristmasRules.TreeAt(site);
        Assert.True(se > site.E && te < site.E && sn == site.N && tn == site.N);
        Assert.True(ChristmasRules.NearSamichlaus(site, se - 1.5, sn));
        Assert.False(ChristmasRules.NearSamichlaus(site, site.E, site.N));
        // no stall is at the Samichlaus, so one key never means two things
        Assert.Equal(-1, ChristmasRules.NearestStall(site, se - 1.5, sn));
    }

    // ---- the wares -----------------------------------------------------------------------------------

    [Fact]
    public void Each_stall_sells_one_ware_at_a_price_in_batzen()
    {
        Assert.Equal(ItemId.GluehweinMug, ChristmasRules.ItemOf(StallKind.Gluehwein));
        Assert.Equal(ItemId.Raclette, ChristmasRules.ItemOf(StallKind.Raclette));
        Assert.Equal(ItemId.Grittibaenz, ChristmasRules.ItemOf(StallKind.Gritibaenz));
        Assert.Equal(ItemId.ChristmasBauble, ChristmasRules.ItemOf(StallKind.Ornaments));
        foreach (var kind in Enum.GetValues<StallKind>())
        {
            Assert.InRange(ChristmasRules.PriceOf(kind), 1, 20);
            Assert.NotEmpty(ChristmasRules.WareName(kind));
            Assert.NotEmpty(ChristmasRules.StallName(kind));
        }
        // a whole round of every ware is cheaper than the day's stall-hop pays: the market gives more than it takes
        Assert.True(Enum.GetValues<StallKind>().Sum(ChristmasRules.PriceOf) < ChristmasRules.DayMax);
    }

    [Fact]
    public void The_new_items_are_in_the_festivals_block_and_nothing_else_has_their_numbers()
    {
        Assert.Equal(501, (int)ItemId.GluehweinMug);
        Assert.Equal(502, (int)ItemId.ChristmasBauble);
        var values = Enum.GetValues<ItemId>().Select(i => (int)i).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.InRange((int)ItemId.GluehweinMug, 500, 539);
        Assert.InRange((int)ItemId.ChristmasBauble, 500, 539);
    }

    [Fact]
    public void A_mug_of_mulled_wine_is_two_decilitres_at_ten_percent()
    {
        Assert.Equal(200, AlcoholRules.GluehweinMl);
        Assert.Equal(0.10, AlcoholRules.GluehweinAbv, 3);
        Assert.Equal(15.8, AlcoholRules.GluehweinGrams, 1);
        Assert.Equal(Bac.Grams(200, 0.10), AlcoholRules.GluehweinGrams, 6);
    }

    // ---- the stall-hop --------------------------------------------------------------------------------

    [Fact]
    public void Stamps_are_a_mask_over_the_eight_stalls()
    {
        int mask = 0;
        Assert.False(ChristmasRules.Stamped(mask, 3));
        mask = ChristmasRules.Stamp(mask, 3);
        mask = ChristmasRules.Stamp(mask, 3);
        Assert.True(ChristmasRules.Stamped(mask, 3));
        Assert.False(ChristmasRules.Stamped(mask, 4));
        Assert.Equal(1, ChristmasRules.Count(mask));
        // a stall that does not exist stamps nothing
        Assert.Equal(mask, ChristmasRules.Stamp(mask, 8));
        Assert.Equal(mask, ChristmasRules.Stamp(mask, -1));
        Assert.False(ChristmasRules.Stamped(mask, 8));
        for (int i = 0; i < 8; i++) mask = ChristmasRules.Stamp(mask, i);
        Assert.Equal(ChristmasRules.AllStamps, mask);
        Assert.Equal(8, ChristmasRules.Count(mask));
    }

    [Fact]
    public void A_whole_day_of_stall_hopping_pays_the_cap_and_no_more()
    {
        var def = FestivalCatalog.ChristmasMarket();
        var inst = new FestivalInstance { Festival = def.Id };
        int total = 0;
        for (int i = 0; i < ChristmasRules.Stalls; i++)
            total += FestivalRules.Earn(def, inst, ChristmasRules.StallGame, 6, ChristmasRules.StampBatzen, ChristmasRules.StampPoints);
        total += FestivalRules.Earn(def, inst, ChristmasRules.StallGame, 6, ChristmasRules.MarktgangBatzen, ChristmasRules.MarktgangPoints);
        Assert.Equal(ChristmasRules.DayMax, total);
        Assert.Equal(ChristmasRules.DayMax, inst.Batzen);
        Assert.Equal(ChristmasRules.Stalls * ChristmasRules.StampPoints + ChristmasRules.MarktgangPoints, inst.Points);
        // nothing more that day, but the next day pays again
        Assert.Equal(0, FestivalRules.Earn(def, inst, ChristmasRules.StallGame, 6, ChristmasRules.StampBatzen, ChristmasRules.StampPoints));
        Assert.Equal(ChristmasRules.StampBatzen, FestivalRules.Earn(def, inst, ChristmasRules.StallGame, 7, ChristmasRules.StampBatzen, ChristmasRules.StampPoints));
    }

    [Fact]
    public void Spending_batzen_takes_them_from_the_purse_and_never_from_what_the_cap_counts()
    {
        var def = FestivalCatalog.ChristmasMarket();
        var inst = new FestivalInstance { Festival = def.Id };
        FestivalRules.Earn(def, inst, ChristmasRules.StallGame, 1, 8, 20);
        Assert.False(FestivalRules.Spend(inst, 9));
        Assert.Equal(8, inst.Batzen);
        Assert.True(FestivalRules.Spend(inst, 4));
        Assert.Equal(4, inst.Batzen);
        Assert.Equal(8, inst.Earned);
        Assert.False(FestivalRules.Spend(inst, -1));
        Assert.True(FestivalRules.Spend(inst, 4));
        Assert.Equal(0, inst.Batzen);
        // what is left converts at 1 Batzen = CHF 1: a bought mug is the price of a real one
        FestivalRules.Earn(def, inst, ChristmasRules.StallGame, 2, 8, 20);
        var book = new FestivalBook();
        book.Instances["christmasmarket-2027"] = inst;
        var paid = FestivalRules.Convert(book, _ => true);
        Assert.Equal(8, paid.Single().Chf);
    }
}
