using UnitSport.Crafting;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The farm co-op and the produce recipes (#494): pure tables. Item values are checked by <c>--invcheck</c>.</summary>
public class FarmEconomyTests
{
    private static readonly ItemId[] Seeds =
    {
        ItemId.WheatSeed, ItemId.BarleySeed, ItemId.MaizeSeed, ItemId.SeedPotato, ItemId.RapeSeed,
        ItemId.SunflowerSeed, ItemId.SugarBeetSeed, ItemId.VegetableSeeds, ItemId.PeaSeed,
    };

    [Fact]
    public void The_co_op_is_only_in_the_countryside()
    {
        Assert.Equal(0, ShopTables.TypeShare(ShopType.FarmCoop, rural: false));
        Assert.True(ShopTables.TypeShare(ShopType.FarmCoop, rural: true) > 0.05);
        int rural = 0, town = 0, barnRural = 0, barnTown = 0;
        for (int i = 0; i < 4000; i++)
        {
            string key = $"{2500 + i % 300}_{1100 + i / 300}_{i % 41}";
            if (ShopTables.TypeFor(key, BuildingKind.Commercial, 120, false, true) == ShopType.FarmCoop) rural++;
            if (ShopTables.TypeFor(key, BuildingKind.Commercial, 120, false, false) == ShopType.FarmCoop) town++;
            if (ShopTables.TypeFor(key, BuildingKind.Agricultural, 300, false, true) == ShopType.FarmCoop) barnRural++;
            if (ShopTables.TypeFor(key, BuildingKind.Agricultural, 300, false, false) == ShopType.FarmCoop) barnTown++;
        }
        Assert.True(rural > 100 && town == 0, $"village shops: {rural} rural, {town} in town");
        Assert.True(barnRural > 300 && barnTown == 0, $"big barns: {barnRural} rural, {barnTown} in town");
        // a small shed is never one; a barn is not a shop in town
        Assert.Equal(ShopType.None, ShopTables.TypeFor("1_2_3", BuildingKind.Agricultural, ShopTables.CoopMinArea - 1, false, true));
        Assert.True(ShopTables.RuralOnly(ShopType.FarmCoop) && ShopTables.RuralOnly(ShopType.GunShop));
        Assert.Equal("Farm co-op", ShopTables.Name(ShopType.FarmCoop));
    }

    [Fact]
    public void The_co_op_sells_seeds_the_hoe_and_fertiliser()
    {
        var sold = ShopTables.Catalogue(ShopType.FarmCoop).Select(l => l.Id).ToHashSet();
        Assert.All(Seeds, s => Assert.Contains(s, sold));
        Assert.Contains(ItemId.Hoe, sold);
        Assert.Contains(ItemId.Fertiliser, sold);
        // fertiliser is bought, never made
        Assert.Contains(ItemId.Fertiliser, Recipes.NeverCrafted);
        // cash under 20 CHF, the card too above; nothing here is card-only
        Assert.Equal(Payment.Cash, ShopTables.PaymentFor(ShopType.FarmCoop, ItemId.WheatSeed, 15));
        Assert.Equal(Payment.CashOrCard, ShopTables.PaymentFor(ShopType.FarmCoop, ItemId.Hoe, 30));
    }

    [Fact]
    public void The_co_op_buys_produce_and_nothing_else()
    {
        foreach (var c in Enum.GetValues<ItemCategory>())
            Assert.Equal(c == ItemCategory.Produce, ShopTables.Buys(ShopType.FarmCoop, c));
        // no other shop buys produce
        foreach (var t in Enum.GetValues<ShopType>().Where(t => t != ShopType.FarmCoop))
            Assert.False(ShopTables.Buys(t, ItemCategory.Produce), t.ToString());
    }

    [Fact]
    public void A_delivered_load_pays_the_full_value_a_counter_sale_35_percent()
    {
        // a 50 kg sack of wheat is 25 CHF: ten sacks are 250, not 87
        Assert.Equal(250, ShopTables.DeliveryPrice(ItemCategory.Produce, 25f, 10));
        Assert.Equal(80, 10 * ShopTables.SellPrice(25f));   // 8 each at the counter
        Assert.True(ShopTables.DeliveryPrice(ItemCategory.Produce, 25f, 10) > 10 * ShopTables.SellPrice(25f));
        Assert.Equal(7, ShopTables.DeliveryPrice(ItemCategory.Produce, 2.5f, 3));            // rounded down for the load
        Assert.Equal(0, ShopTables.DeliveryPrice(ItemCategory.Food, 25f, 10));                // not produce
        Assert.Equal(0, ShopTables.DeliveryPrice(ItemCategory.Produce, 25f, 0));
        Assert.Equal(0, ShopTables.DeliveryPrice(ItemCategory.Produce, 0f, 5));
    }

    [Fact]
    public void Farm_recipes_are_well_formed()
    {
        var farm = Recipes.All.Where(r => Recipes.Outputs(r).Any(o => (int)o.Id >= 300)).ToArray();
        Assert.NotEmpty(farm);
        Assert.Equal(Recipes.All.Length, Recipes.All.Select(r => r.Key).Distinct().Count());
        // milling is at the workbench, cooking at a fire, a seed from a harvest by hand
        Assert.Equal(Station.Workbench, Recipes.All.First(r => r.Out == ItemId.Flour).Station);
        Assert.Equal(Station.Workbench, Recipes.All.First(r => r.Out == ItemId.RapeseedOil).Station);
        foreach (var dish in new[] { ItemId.BakedPotato, ItemId.Roesti, ItemId.Polenta, ItemId.Popcorn, ItemId.VegetableSoup, ItemId.Raclette })
            Assert.Equal(Station.Fire, Recipes.All.First(r => r.Out == dish).Station);
        Assert.Equal(Station.Hands, Recipes.All.First(r => r.Out == ItemId.WheatSeed).Station);
        Assert.Equal(Station.Fire, Recipes.All.First(r => r.Out == ItemId.Bread && r.Station == Station.Fire).Station);
        // every seed can be kept from a harvest of its crop, and the harvests come from fields, not recipes
        Assert.All(Seeds, s => Assert.Contains(Recipes.All, r => r.Out == s));
        Assert.All(new[] { ItemId.Wheat, ItemId.Potato, ItemId.Carrot, ItemId.HayBale }, h => Assert.DoesNotContain(Recipes.All, r => r.Out == h));
        // nothing is made from nothing
        Assert.All(farm, r => Assert.NotEmpty(r.In));
    }

    [Fact]
    public void Seeds_and_harvests_are_not_what_the_co_op_sells_back_for_free()
    {
        // the co-op's own stock never lists a harvest (it buys those), so buying then delivering is no loop
        var sold = ShopTables.Catalogue(ShopType.FarmCoop).Select(l => l.Id).ToHashSet();
        foreach (var h in new[] { ItemId.Wheat, ItemId.Barley, ItemId.Maize, ItemId.Potato, ItemId.Rapeseed, ItemId.SunflowerSeeds,
                                  ItemId.SugarBeet, ItemId.Carrot, ItemId.HayBale, ItemId.Peas, ItemId.Flour })
            Assert.DoesNotContain(h, sold);
    }
}
