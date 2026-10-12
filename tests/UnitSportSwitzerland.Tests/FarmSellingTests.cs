using UnitSport.Farming;
using UnitSport.Terrain.Format;
using UnitSport.Items;
using UnitSport.Loot;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Selling farm produce (#494, docs/notes/farming/selling.md): prices, buyers, contracts, the farm stand.</summary>
public class FarmSellingTests
{
    // the game's values (Items/Item.cs), for the ones the tests use
    private static float Value(ItemId id) => id switch
    {
        ItemId.Wheat => 25, ItemId.Barley => 22, ItemId.Maize => 22, ItemId.Potato => 5, ItemId.Rapeseed => 42,
        ItemId.SunflowerSeeds => 38, ItemId.SugarBeet => 3, ItemId.Carrot => 6, ItemId.HayBale => 40, ItemId.Peas => 28,
        ItemId.Flour => 7, ItemId.Roesti => 20, ItemId.Raclette => 22,
        _ => 10,
    };

    [Fact]
    public void Harvest_prices_sag_at_harvest_and_rise_in_spring()
    {
        Assert.Equal(FarmPrices.GlutFactor, FarmPrices.Season(ItemId.Wheat, 7));      // wheat is threshed in July
        Assert.Equal(FarmPrices.SpringFactor, FarmPrices.Season(ItemId.Wheat, 4));    // the stores run low
        Assert.Equal(1.0, FarmPrices.Season(ItemId.Wheat, 12));
        Assert.Equal(FarmPrices.GlutFactor, FarmPrices.Season(ItemId.SugarBeet, 10)); // beet lifted from October
        Assert.Equal(1.0, FarmPrices.Season(ItemId.Carrot, 4));                       // carrots are sold fresh, not stored
        Assert.Equal(FarmPrices.GlutFactor, FarmPrices.Season(ItemId.HayBale, 7));
        Assert.Equal(1.0, FarmPrices.Season(ItemId.Flour, 7));                        // products have no season
        Assert.True(FarmPrices.Delivery(25, ItemId.Wheat, 100, 4, null, 0) > FarmPrices.Delivery(25, ItemId.Wheat, 100, 7, null, 0));
    }

    [Fact]
    public void Wish_lists_are_keyed_by_co_op_and_week_and_change()
    {
        var a = FarmPrices.Wishes("2600_1200_7", 1000);
        Assert.Equal(a, FarmPrices.Wishes("2600_1200_7", 1000));     // every peer works out the same
        Assert.InRange(a.Length, 1, 2);
        Assert.Equal(a.Length, a.Select(w => w.Item).Distinct().Count());
        foreach (var w in a)
        {
            Assert.Contains(w.Item, FarmPrices.Harvests);
            Assert.InRange(w.Bonus, FarmPrices.WishMin, FarmPrices.WishMax);
            Assert.Equal(w.Bonus, FarmPrices.WishBonus("2600_1200_7", 1000, w.Item));
        }
        int changed = 0, two = 0;
        var seen = new HashSet<ItemId>();
        for (long week = 0; week < 60; week++)
        {
            var w = FarmPrices.Wishes("2600_1200_7", week);
            if (!w.SequenceEqual(FarmPrices.Wishes("2600_1200_7", week + 1))) changed++;
            if (w.Length == 2) two++;
            foreach (var x in w) seen.Add(x.Item);
        }
        Assert.True(changed > 40, $"the list changes week to week ({changed}/60)");
        Assert.InRange(two, 15, 45);
        Assert.True(seen.Count >= 8, $"over a year most crops are wanted somewhere ({seen.Count})");
        int differ = 0;
        for (long week = 0; week < 20; week++)
            if (!FarmPrices.Wishes("2600_1200_7", week).SequenceEqual(FarmPrices.Wishes("2601_1200_9", week))) differ++;
        Assert.True(differ > 12, $"two co-ops want different things ({differ}/20)");
    }

    [Fact]
    public void A_wanted_crop_pays_its_bonus_at_the_counter_and_by_the_load()
    {
        string coop = "2580_1200_3";
        long week = 77;
        var wish = FarmPrices.Wishes(coop, week)[0];
        float v = Value(wish.Item);
        int month = 12;
        double season = FarmPrices.Season(wish.Item, month);
        Assert.Equal((long)Math.Floor(v * season * wish.Bonus * 40), FarmPrices.Delivery(v, wish.Item, 40, month, coop, week));
        Assert.Equal((int)Math.Floor(v * 0.35 * season * wish.Bonus + 1e-9), FarmPrices.Counter(v, wish.Item, month, coop, week));
        // ShopTables goes through the same function
        Assert.Equal(FarmPrices.Delivery(v, wish.Item, 40, month, coop, week),
            ShopTables.DeliveryPrice(ItemCategory.Produce, wish.Item, v, 40, month, coop, week));
        Assert.Equal(FarmPrices.Counter(v, wish.Item, month, coop, week),
            ShopTables.CounterPrice(ShopType.FarmCoop, wish.Item, ItemCategory.Produce, v, month, coop, week));
        // not wanted: plain season price; another shop: the usual 35 %
        var other = FarmPrices.Harvests.First(h => FarmPrices.WishBonus(coop, week, h) == 1.0);
        Assert.Equal((long)Math.Floor(Value(other) * FarmPrices.Season(other, month) * 10), FarmPrices.Delivery(Value(other), other, 10, month, coop, week));
        Assert.Equal(ShopTables.SellPrice(22), ShopTables.CounterPrice(ShopType.Grocery, ItemId.Raclette, ItemCategory.Food, 22, 7, "x", 1));
        Assert.Equal(0, ShopTables.CounterPrice(ShopType.Grocery, ItemId.Wheat, ItemCategory.Produce, 25, 7, "x", 1));
        // the old call is the neutral price: full value
        Assert.Equal(250, ShopTables.DeliveryPrice(ItemCategory.Produce, 25, 10));
    }

    [Fact]
    public void Delivery_always_beats_the_counter_and_the_stand_sits_between()
    {
        foreach (var item in FarmPrices.Harvests)
            for (int month = 1; month <= 12; month++)
            {
                float v = Value(item);
                int counter = FarmPrices.Counter(v, item, month, null, 0), stand = FarmPrices.Stand(v, item, month);
                long load = FarmPrices.Delivery(v, item, 10, month, null, 0);
                Assert.True(load >= 10 * counter, $"{item} m{month}: {load} for ten delivered vs {counter} each at the counter");
                Assert.True(stand >= counter && stand <= Math.Ceiling(v * FarmPrices.Season(item, month)), $"{item} m{month}: stand {stand} vs counter {counter}");
            }
    }

    [Fact]
    public void Specialty_buyers_sit_at_their_real_places_and_pay_more()
    {
        Assert.Equal(2, FarmBuyers.All.Count(b => b.Goods.SequenceEqual(new[] { ItemId.SugarBeet })));
        foreach (var b in FarmBuyers.All)
        {
            Assert.InRange(b.E, 2480000, 2840000);   // inside Switzerland's LV95 box
            Assert.InRange(b.N, 1070000, 1300000);
            Assert.True(b.Premium > 1.0, $"{b.Name} pays more than the co-op");
            Assert.Same(b, FarmBuyers.At(b.E + b.Reach * 0.5, b.N));
            Assert.NotSame(b, FarmBuyers.At(b.E + b.Reach + 50, b.N + b.Reach + 50));
            Assert.Contains("OSM", b.Source);
        }
        var aarberg = FarmBuyers.ByKey("zucker-aarberg")!;
        Assert.InRange(aarberg.E, 2587000, 2588500);   // Aarberg BE
        Assert.InRange(aarberg.N, 1209000, 1210500);
        var frauenfeld = FarmBuyers.ByKey("zucker-frauenfeld")!;
        Assert.InRange(frauenfeld.E, 2707000, 2709000);   // Frauenfeld TG
        Assert.InRange(frauenfeld.N, 1267500, 1269000);
        Assert.True(aarberg.Buys(ItemId.SugarBeet) && !aarberg.Buys(ItemId.Wheat));
        Assert.Equal((long)Math.Floor(3 * 1.3 * 100), FarmPrices.Delivery(3, ItemId.SugarBeet, 100, 12, null, 0, aarberg.Premium));
        Assert.Equal("wheat, barley or maize", FarmBuyers.ByKey("swissmill-zuerich")!.GoodsText(i => i.ToString()));
    }

    [Fact]
    public void A_buyers_office_stands_by_its_road_clear_of_the_factory()
    {
        // a buyer at the middle of a tile, inside a 60 m square factory; a road runs east-west 70 m south of the point
        var tile = new TileId(2600, 1200);
        var b = new FarmBuyer("t", "Test Mill", "Here", 2600500, 1200500, 90f, new[] { ItemId.Wheat }, 1.1, "test");
        static float[] Square(double x0, double z0, double x1, double z1) => new[]
        {
            (float)x0, 0f, (float)z0, (float)x1, 0f, (float)z0, (float)x1, 0f, (float)z1,
            (float)x0, 0f, (float)z0, (float)x1, 0f, (float)z1, (float)x0, 0f, (float)z1,
        };
        var factory = new Building { Triangles = Square(470, 470, 530, 530) };   // tile-local: x east, z south
        var road = new RoadSegment { Class = RoadClass.Road, Width = 6f, Points = new[] { 300f, 432f, 570f, 700f, 432f, 570f } };
        var site = FarmBuyerSite.Pick(b, new[] { (tile, road) }, new[] { (tile, factory) })!.Value;
        Assert.Equal(432f, site.Altitude);
        Assert.InRange(site.E, 2600495, 2600505);                 // straight south of the point
        Assert.InRange(site.N, 1200500 - 70 + 3 + 5 - 0.1, 1200500 - 70 + 3 + 5 + 0.1);   // the road's north side, 5 m from its edge
        Assert.True(Math.Abs(Math.Abs(site.Yaw) - 0) < 0.01, $"faces south, to the road: yaw {site.Yaw}");
        // the factory reaching to the road leaves only the far side, which is out of the yard
        var big = new Building { Triangles = Square(380, 380, 620, 566) };
        Assert.Null(FarmBuyerSite.Pick(b, new[] { (tile, road) }, new[] { (tile, big) }));
        // a railway or a footpath is no access road
        var rail = new RoadSegment { Class = RoadClass.Railway, Width = 3f, Points = road.Points };
        Assert.Null(FarmBuyerSite.Pick(b, new[] { (tile, rail) }, new[] { (tile, factory) }));
        Assert.Equal("NE", FarmBuyers.Toward(100, 100));
        Assert.Equal("W", FarmBuyers.Toward(-100, 3));
        var near = FarmBuyers.FromHere(2587000, 1209000).First();
        Assert.Equal("zucker-aarberg", near.Buyer.Key);
    }

    [Fact]
    public void Orders_are_deterministic_and_sized_by_value()
    {
        var a = FarmContracts.Orders("2600_1200_7", 500, Value);
        Assert.Equal(a, FarmContracts.Orders("2600_1200_7", 500, Value));
        Assert.Equal(FarmContracts.PerWeek, a.Length);
        Assert.Equal(a.Length, a.Select(o => o.Item).Distinct().Count());
        foreach (var o in a)
        {
            Assert.InRange(o.Multiplier, 1.3, 1.8);
            Assert.InRange(o.Days, 2, 4);
            Assert.Equal(0, o.Count % 5);
            double worth = o.Count * Value(o.Item);
            Assert.True(o.Count == 400 || o.Count == 5 || worth is >= 450 and <= 1500, $"{o.Item} ×{o.Count}: {worth} CHF");
        }
        Assert.NotEqual(a, FarmContracts.Orders("2600_1200_7", 501, Value));
    }

    [Fact]
    public void A_contract_completes_by_deliveries_and_another_lapses()
    {
        double now = FarmCalendar.WeekSeconds * 600.0 + 100;
        long week = FarmCalendar.Week(now);
        var orders = FarmContracts.Orders("coop", week, Value);
        var mine = new List<FarmContract>();
        Assert.Null(FarmContracts.Accept(mine, "coop", week, orders[0], Value(orders[0].Item), now));
        Assert.NotNull(FarmContracts.Accept(mine, "coop", week, orders[0], Value(orders[0].Item), now));   // taken already
        Assert.NotNull(FarmContracts.Accept(mine, "coop", week - 1, orders[1], 1, now));                  // last week's
        Assert.Null(FarmContracts.Accept(mine, "coop", week, orders[1], Value(orders[1].Item), now));
        Assert.Null(FarmContracts.Accept(mine, "coop", week, orders[2], Value(orders[2].Item), now));
        Assert.Equal("You hold 3 contracts already.", FarmContracts.Accept(mine, "other", week, FarmContracts.Orders("other", week, Value)[0], 1, now));

        var first = mine[0];
        // another co-op or another crop does not count
        Assert.Equal(0, FarmContracts.Deliver(mine, "elsewhere", first.Item, 10, now + 1).Counted);
        // part, then the rest (more than needed: the extra is just sold)
        var step = FarmContracts.Deliver(mine, "coop", first.Item, first.Count - 1, now + 10);
        Assert.Equal(first.Count - 1, step.Counted);
        Assert.Empty(step.Completed);
        step = FarmContracts.Deliver(mine, "coop", first.Item, 7, now + 20);
        Assert.Equal(1, step.Counted);
        Assert.Single(step.Completed);
        Assert.Equal((int)Math.Floor((first.Multiplier - 1) * first.Count * first.Unit), FarmContracts.Bonus(step.Completed[0]));
        Assert.Equal(2, mine.Count);

        // the second lapses at its deadline; a delivery after it does not fill it
        var second = mine[0];
        Assert.Equal(0, FarmContracts.Deliver(mine, "coop", second.Item, second.Count, second.Deadline + 1).Counted);
        var gone = FarmContracts.Expire(mine, second.Deadline + 1);
        Assert.Contains(second, gone);
        Assert.DoesNotContain(second, mine);
        Assert.Equal(orders[0].Days * FarmCalendar.DaySeconds, first.Deadline - now, 3);
    }

    [Fact]
    public void A_stand_takes_produce_and_sells_it_over_time()
    {
        var s = new StandState { Id = 1, Owner = "A", RoadM = 8, Houses = 0, LastTick = 0 };
        Assert.True(FarmStandRules.Stockable(ItemId.Potato) && FarmStandRules.Stockable(ItemId.Roesti) && FarmStandRules.Stockable(ItemId.Flour));
        Assert.False(FarmStandRules.Stockable(ItemId.WheatSeed) || FarmStandRules.Stockable(ItemId.Fertiliser) || FarmStandRules.Stockable(ItemId.HayBale)
                     || FarmStandRules.Stockable(ItemId.Shotgun));
        Assert.Equal(10, FarmStandRules.Stock(s, ItemId.Potato, 10));
        Assert.Equal(30, FarmStandRules.Stock(s, ItemId.Potato, 35));   // one crate holds 40
        Assert.Equal(5, FarmStandRules.Stock(s, ItemId.Roesti, 5));
        Assert.Equal(0, FarmStandRules.Stock(s, ItemId.WheatSeed, 5));
        Assert.Equal(2, s.Slots.Count);

        // two days by a road in open country: 6 potatoes (12 rösti: cooked sells twice as fast, only 5 there)
        int Price(ItemId id) => FarmPrices.Stand(Value(id), id, 12);
        const double Day = FarmCalendar.DaySeconds;
        var sales = FarmStandRules.Advance(s, 2 * Day, Price);
        Assert.Equal(6, sales.Single(x => x.Item == ItemId.Potato).Count);
        Assert.Equal(5, sales.Single(x => x.Item == ItemId.Roesti).Count);
        Assert.Equal(34, s.Slots[0].Count);
        Assert.Equal(0, s.Slots[1].Count);
        Assert.Equal(6 * Price(ItemId.Potato) + 5 * Price(ItemId.Roesti), s.Cash);

        // a day, half at a time: fractions carry over, nothing lost
        FarmStandRules.Advance(s, 2.5 * Day, Price);
        FarmStandRules.Advance(s, 4 * Day, Price);
        Assert.Equal(28, s.Slots[0].Count);

        // a village round it sells faster, a stand far from the road slower
        Assert.True(FarmStandRules.PerDay(new StandState { RoadM = 8, Houses = 40 }, ItemId.Potato) == 3 * FarmStandRules.PerDay(new StandState { RoadM = 8 }, ItemId.Potato));
        Assert.True(FarmStandRules.RoadFactor(50) < FarmStandRules.RoadFactor(10));
        Assert.Equal(0, FarmStandRules.RoadFactor(FarmStandRules.RoadMax + 1));

        // a stand left two months catches up at most 10 days
        var t = new StandState { RoadM = 55, LastTick = 0 };
        FarmStandRules.Stock(t, ItemId.Potato, 40);
        FarmStandRules.Advance(t, 60 * Day, Price);
        int capped = (int)Math.Floor(FarmStandRules.PerDay(t, ItemId.Potato) * 10);
        Assert.True(capped < 40);
        Assert.Equal(40 - capped, t.Slots[0].Count);
        Assert.Equal(4, FarmStandRules.Take(t, 0, 4) + FarmStandRules.Take(t, 9, 4));
    }

    [Fact]
    public void The_farm_calendar_counts_24_minute_days()
    {
        Assert.Equal(0, FarmCalendar.Week(FarmCalendar.WeekSeconds - 1));
        Assert.Equal(1, FarmCalendar.Week(FarmCalendar.WeekSeconds));
        Assert.Equal("2 d 0 h", FarmCalendar.Left(2 * FarmCalendar.DaySeconds));
        Assert.Equal("1 h", FarmCalendar.Left(30));
        Assert.Equal("due", FarmCalendar.Left(0));
    }
}
