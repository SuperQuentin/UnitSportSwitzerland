using UnitSport.Core;
using UnitSport.Crafting;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Shops and PAUSA vending machines (#273): the pure tables and the server's ledger.</summary>
public class ShopTests
{
    /// <summary>Stand-in values (the game reads ItemDefs): 10 CHF a thing, a few real ones for the payment rule.</summary>
    private static float Value(ItemId id) => id switch
    {
        ItemId.Shotgun => 400, ItemId.Shells => 1, ItemId.Camera => 160, ItemId.Chocolate => 3,
        ItemId.FirstAidKit => 30, ItemId.IceTea => 3, _ => 10,
    };

    private static readonly ShopType[] Shops = Enum.GetValues<ShopType>().Where(t => t != ShopType.None).ToArray();

    [Fact]
    public void Prices_are_the_value_times_a_markup_of_1_1_to_1_4()
    {
        for (int i = 0; i < 2000; i++)
        {
            double m = ShopTables.Markup($"{i}_{i * 7}_{i % 13}");
            Assert.InRange(m, 1.1, 1.4);
        }
        Assert.Equal(11, ShopTables.Price(10, 1.1));
        Assert.Equal(14, ShopTables.Price(10, 1.4));
        Assert.Equal(1, ShopTables.Price(0.2f, 1.1));   // never free
        Assert.Equal(3, ShopTables.SellPrice(10));       // 35 %, rounded down
        Assert.Equal(0, ShopTables.SellPrice(0.5f));     // not worth buying back
    }

    [Fact]
    public void Payment_follows_price_and_shop()
    {
        foreach (var cashOnly in new[] { ShopType.Vending, ShopType.Kiosk, ShopType.Grocery })
            Assert.Equal(Payment.Cash, ShopTables.PaymentFor(cashOnly, ItemId.Cheese, 500));
        foreach (var t in new[] { ShopType.Pharmacy, ShopType.Hardware, ShopType.Sport, ShopType.Boutique, ShopType.Electronics, ShopType.Garage })
        {
            Assert.Equal(Payment.Cash, ShopTables.PaymentFor(t, ItemId.Bandage, 19));
            Assert.Equal(Payment.CashOrCard, ShopTables.PaymentFor(t, ItemId.FirstAidKit, 20));
        }
        // the shotgun goes on the card, whatever it costs, wherever
        Assert.Equal(Payment.Card, ShopTables.PaymentFor(ShopType.GunShop, ItemId.Shotgun, 500));
        Assert.Equal(Payment.Card, ShopTables.PaymentFor(ShopType.GunShop, ItemId.Shotgun, 5));
        Assert.True(ShopTables.Accepts(Payment.CashOrCard, PayWith.Card));
        Assert.False(ShopTables.Accepts(Payment.Cash, PayWith.Card));
        Assert.False(ShopTables.Accepts(Payment.Card, PayWith.Cash));
        // every line of every stock carries the rule it was rolled with
        foreach (var t in Shops)
            foreach (var line in ShopTables.Stock("2580_1110_7", 3, 99, t, Value, true))
                Assert.Equal(ShopTables.PaymentFor(t, line.Id, line.Price), line.Pay);
    }

    [Fact]
    public void Stock_is_a_function_of_building_furniture_and_epoch()
    {
        foreach (var t in Shops)
        {
            var a = ShopTables.Stock("2580_1110_7", 3, 99, t, Value, true);
            var b = ShopTables.Stock("2580_1110_7", 3, 99, t, Value, true);
            Assert.Equal(a, b);
            Assert.Equal(t == ShopType.Vending ? ShopTables.VendingSlots : ShopTables.Catalogue(t).Count, a.Count);
            Assert.Equal(Enumerable.Range(0, a.Count), a.Select(l => l.Slot));
            // a restock (or another counter) rolls something else, somewhere
            bool differs = Enumerable.Range(100, 20).Any(e => !ShopTables.Stock("2580_1110_7", 3, e, t, Value, true).SequenceEqual(a))
                && Enumerable.Range(0, 20).Any(f => !ShopTables.Stock("2580_1110_7", f + 10, 99, t, Value, true).SequenceEqual(a));
            Assert.True(differs, t.ToString());
        }
    }

    [Fact]
    public void A_machine_has_24_slots_of_3_to_8()
    {
        var stock = ShopTables.Stock("1_2_3", 4, 5, ShopType.Vending, Value, true);
        Assert.All(stock, l => Assert.InRange(l.Stock, ShopTables.VendingMin, ShopTables.VendingMax));
        Assert.All(stock, l => Assert.Equal(Payment.Cash, l.Pay));
        Assert.Equal("A1", ShopTables.SlotName(0));
        Assert.Equal("D6", ShopTables.SlotName(23));
        for (int s = 0; s < ShopTables.VendingSlots; s++) Assert.Equal(s, ShopTables.ParseSlot(ShopTables.SlotName(s).ToLowerInvariant()));
        Assert.Equal(-1, ShopTables.ParseSlot("E1"));
        Assert.Equal(-1, ShopTables.ParseSlot("A7"));
        Assert.Equal(-1, ShopTables.ParseSlot("A"));
    }

    [Fact]
    public void The_shotgun_is_sold_only_in_the_hunting_season()
    {
        for (int e = 0; e < 200; e++)
            Assert.All(ShopTables.Stock("9_9_9", 0, e, ShopType.GunShop, Value, false).Where(l => l.Id == ItemId.Shotgun),
                l => Assert.Equal(0, l.Stock));
        Assert.Equal(0, ShopTables.Chance(ShopType.GunShop, ItemId.Shotgun, false));
        Assert.True(ShopTables.Chance(ShopType.GunShop, ItemId.Shotgun, true) > 0.5);
    }

    /// <summary>The closed form against 20 000 sampled restocks per type (hashed seeds: consecutive ones correlate).</summary>
    [Fact]
    public void Chance_is_the_exact_odds_of_the_stock_roll()
    {
        const int N = 20000;
        foreach (var t in Shops)
        {
            var have = new Dictionary<ItemId, int>();
            for (int i = 0; i < N; i++)
            {
                string key = $"{Fnv.Hash("k" + i) & 0xffff}_{i}_{i % 7}";
                long epoch = (uint)Fnv.Hash("e" + i) % 100000;
                bool broken = t == ShopType.Vending && ShopTables.OutOfOrder(key, i % 5, epoch);
                foreach (var id in ShopTables.Stock(key, i % 5, epoch, t, Value, true).Where(l => l.Stock > 0 && !broken).Select(l => l.Id).Distinct())
                    have[id] = have.GetValueOrDefault(id) + 1;
            }
            foreach (var id in ShopTables.Catalogue(t).Select(l => l.Id).Distinct())
            {
                double exact = ShopTables.Chance(t, id, true);
                double seen = have.GetValueOrDefault(id) / (double)N;
                Assert.True(Math.Abs(exact - seen) < 0.015, $"{t} {id}: exact {exact:F3}, sampled {seen:F3}");
            }
            Assert.Equal(0, ShopTables.Chance(t, ItemId.Photo, true));
        }
    }

    [Fact]
    public void Shop_only_items_are_never_crafted()
    {
        foreach (var id in ShopTables.ShopOnly)
            Assert.Contains(id, Recipes.NeverCrafted);
        foreach (var r in Recipes.All)
            Assert.DoesNotContain(Recipes.Outputs(r), o => ShopTables.ShopOnly.Contains(o.Id));
        // the shells are bought or found, never made (#270)
        Assert.Contains(ItemId.Shells, Recipes.NeverCrafted);
        Assert.All(ShopTables.ShopOnly, id => Assert.Contains(id, ShopTables.Sold()));
    }

    [Fact]
    public void Vending_exclusives_are_only_in_machines()
    {
        foreach (var t in Shops.Where(t => t != ShopType.Vending))
            Assert.DoesNotContain(ShopTables.Catalogue(t), l => ShopTables.VendingOnly.Contains(l.Id));
        Assert.All(ShopTables.VendingOnly, id => Assert.True(ShopTables.Chance(ShopType.Vending, id, true) > 0.5, id.ToString()));
    }

    [Fact]
    public void Catalogues_are_well_formed()
    {
        foreach (var t in Shops)
        {
            var lines = ShopTables.Catalogue(t);
            Assert.NotEmpty(lines);
            Assert.All(lines, l => Assert.True(l.Id != ItemId.None && l.Id != ItemId.Francs && l.Chance > 0 && l.Chance <= 1 && l.Min >= 1 && l.Max >= l.Min, $"{t} {l}"));
            Assert.Equal(lines.Count, lines.Select(l => l.Id).Distinct().Count());
            Assert.False(ShopTables.Buys(t, ItemCategory.Money));
        }
        Assert.Empty(ShopTables.PlainClothes.Intersect(ShopTables.SpecialClothes));
        Assert.Equal(ItemId.NeonGloves - ItemId.CatEarsBlack + 1, ShopTables.PlainClothes.Length + ShopTables.SpecialClothes.Length);
        Assert.All(Enum.GetValues<ItemCategory>(), c => Assert.False(ShopTables.Buys(ShopType.Vending, c)));
    }

    [Fact]
    public void Shop_types_follow_the_weights()
    {
        const int N = 40000;
        foreach (bool rural in new[] { false, true })
        {
            var count = new Dictionary<ShopType, int>();
            for (int i = 0; i < N; i++)
            {
                var t = ShopTables.TypeFor($"{2500 + i % 300}_{1100 + i / 300}_{i % 41}", BuildingKind.Commercial, 120, false, rural);
                count[t] = count.GetValueOrDefault(t) + 1;
            }
            Assert.False(count.ContainsKey(ShopType.None));
            Assert.Equal(rural, count.ContainsKey(ShopType.GunShop));
            foreach (var t in Shops.Where(t => t is not (ShopType.Vending or ShopType.Garage)))
                Assert.True(Math.Abs(count.GetValueOrDefault(t) / (double)N - ShopTables.TypeShare(t, rural)) < 0.01, $"{t} rural={rural}");
        }
        Assert.Equal(ShopType.None, ShopTables.TypeFor("1_2_3", BuildingKind.Commercial, 200, bank: true, rural: true));
        Assert.Equal(ShopType.None, ShopTables.TypeFor("1_2_3", BuildingKind.House, 200, false, true));
        int garages = Enumerable.Range(0, 4000).Count(i => ShopTables.TypeFor($"7_8_{i}", BuildingKind.Garage, 60, false, false) == ShopType.Garage);
        Assert.InRange(garages, 800, 1200);
        Assert.Equal(0, Enumerable.Range(0, 400).Count(i => ShopTables.TypeFor($"7_8_{i}", BuildingKind.Garage, 20, false, false) != ShopType.None));
    }

    // ---- the ledger -----------------------------------------------------------------------------

    private sealed class Disk
    {
        public readonly Dictionary<string, string> Files = new();
        public int Writes;
        public ShopLedger.TileState? Load(string name) =>
            Files.TryGetValue(name, out var s) ? System.Text.Json.JsonSerializer.Deserialize<ShopLedger.TileState>(s) : null;
        public void Save(string name, ShopLedger.TileState t) { Files[name] = System.Text.Json.JsonSerializer.Serialize(t); Writes++; }
        public ShopLedger Ledger(int version = 11) => new(Load, Save, version);
    }

    private static readonly Func<int, bool> NoCard = _ => throw new InvalidOperationException("no card here");

    [Fact]
    public void Selling_out_a_slot_is_remembered_and_seen_by_the_next_buyer()
    {
        var disk = new Disk();
        var stock = ShopTables.Stock("2580_1110_7", 2, 50, ShopType.Grocery, Value, true);
        int slot = stock.FindIndex(l => l.Stock > 0 && l.Pay == Payment.Cash);
        int n = stock[slot].Stock;
        var shop = disk.Ledger();
        var first = shop.Buy("2580_1110_7", 2, 50, 50, stock, slot, n + 5, PayWith.Cash, NoCard);
        Assert.Equal(new BuyResult(BuyOutcome.Sold, n, n * stock[slot].Price, n), first);   // only what is there
        Assert.Equal(BuyOutcome.SoldOut, shop.Buy("2580_1110_7", 2, 50, 50, stock, slot, 1, PayWith.Cash, NoCard).Outcome);

        // the file is all a restarted server (or the next player) needs
        var again = disk.Ledger();
        Assert.Equal(0, ShopLedger.Left(stock, again.Sold("2580_1110_7", 2, 50, stock.Count))[slot]);
        Assert.Equal(BuyOutcome.SoldOut, again.Buy("2580_1110_7", 2, 50, 50, stock, slot, 1, PayWith.Cash, NoCard).Outcome);
        // other counters and other buildings in the tile are untouched
        Assert.All(again.Sold("2580_1110_8", 2, 50, stock.Count), s => Assert.Equal(0, s));
        Assert.All(again.Sold("2580_1110_7", 3, 50, stock.Count), s => Assert.Equal(0, s));

        // a restock period later it is full again; a panel from the old period is told so
        Assert.All(again.Sold("2580_1110_7", 2, 51, stock.Count), s => Assert.Equal(0, s));
        Assert.Equal(BuyOutcome.Restocked, again.Buy("2580_1110_7", 2, 50, 51, stock, slot, 1, PayWith.Cash, NoCard).Outcome);
        // and records of older plans (renumbered furniture) count for nothing
        Assert.All(disk.Ledger(version: 12).Sold("2580_1110_7", 2, 50, stock.Count), s => Assert.Equal(0, s));
    }

    [Fact]
    public void The_card_is_charged_by_the_server_and_a_decline_sells_nothing()
    {
        var disk = new Disk();
        var shop = disk.Ledger();
        var stock = new List<StockLine>
        {
            new(0, ItemId.Camera, 2, 180, Payment.CashOrCard),
            new(1, ItemId.Shotgun, 1, 500, Payment.Card),
            new(2, ItemId.Chocolate, 9, 4, Payment.Cash),
        };
        long account = 300;
        bool Charge(int amount) { if (amount > account) return false; account -= amount; return true; }

        Assert.Equal(BuyOutcome.Sold, shop.Buy("1_1_1", 0, 7, 7, stock, 0, 1, PayWith.Card, Charge).Outcome);
        Assert.Equal(120, account);
        Assert.Equal(BuyOutcome.Declined, shop.Buy("1_1_1", 0, 7, 7, stock, 0, 1, PayWith.Card, Charge).Outcome);
        Assert.Equal(1, shop.Sold("1_1_1", 0, 7, 3)[0]);   // the decline sold nothing
        Assert.Equal(BuyOutcome.WrongPayment, shop.Buy("1_1_1", 0, 7, 7, stock, 1, 1, PayWith.Cash, Charge).Outcome);
        Assert.Equal(BuyOutcome.WrongPayment, shop.Buy("1_1_1", 0, 7, 7, stock, 2, 1, PayWith.Card, Charge).Outcome);
        Assert.Equal(120, account);
        Assert.Equal(BuyOutcome.Refused, shop.Buy("1_1_1", 0, 7, 7, stock, 3, 1, PayWith.Cash, Charge).Outcome);
        Assert.Equal(BuyOutcome.Refused, shop.Buy("1_1_1", 0, 7, 7, stock, 2, 0, PayWith.Cash, Charge).Outcome);
    }

    [Fact]
    public void A_stuck_item_is_sold_and_drops_on_a_hit()
    {
        var shop = new Disk().Ledger();
        var stock = ShopTables.Stock("3_3_3", 5, 8, ShopType.Vending, Value, true);
        Assert.Equal(BuyOutcome.OutOfOrder, shop.Buy("3_3_3", 5, 8, 8, stock, 0, 1, PayWith.Cash, NoCard, outOfOrder: true).Outcome);
        var r = shop.Buy("3_3_3", 5, 8, 8, stock, 0, 1, PayWith.Cash, NoCard, stuck: true);
        Assert.Equal(BuyOutcome.Stuck, r.Outcome);
        Assert.Equal(1, r.SoldAfter);
        Assert.True(shop.HasStuck("3_3_3", 5, 8));
        Assert.False(shop.HasStuck("3_3_3", 5, 9));   // a restock clears the spiral
        Assert.Equal(new[] { 0, 0 }, shop.Bump("3_3_3", 5, 8, stock, bonus: true));   // the stuck one and a bonus
        Assert.Equal(2, shop.Sold("3_3_3", 5, 8, stock.Count)[0]);
        Assert.Empty(shop.Bump("3_3_3", 5, 8, stock, bonus: true));   // nothing caught any more
    }
}
