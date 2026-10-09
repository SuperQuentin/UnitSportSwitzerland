using UnitSport.Crafting;
using UnitSport.Items;
using UnitSport.Loot;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The barracks items and the recruit's uniform (#716): stored numbers and where they are sold.</summary>
public class BarracksTests
{
    [Fact]
    public void Stored_numbers_are_pinned()
    {
        // ItemId is replicated and saved: append only, never renumber
        Assert.Equal(340, (int)ItemId.FarmStand);
        Assert.Equal(350, (int)ItemId.PlayingCards);
        Assert.Equal(351, (int)ItemId.PokerChips);
        Assert.Equal(352, (int)ItemId.BeerBottle);
        Assert.Equal(353, (int)ItemId.Gamelle);
        Assert.Equal(354, (int)ItemId.TazJacket);
        Assert.Equal(355, (int)ItemId.TazTrousers);
        Assert.Equal(356, (int)ItemId.ArmyTee);
    }

    [Fact]
    public void The_small_goods_are_sold_and_never_crafted()
    {
        var kiosk = ShopTables.Catalogue(ShopType.Kiosk).Select(l => l.Id).ToArray();
        Assert.Contains(ItemId.BeerBottle, kiosk);
        Assert.Contains(ItemId.PlayingCards, kiosk);
        Assert.Contains(ItemId.PokerChips, kiosk);
        Assert.Contains(ItemId.BeerBottle, ShopTables.Catalogue(ShopType.Grocery).Select(l => l.Id));
        Assert.Contains(ItemId.Gamelle, ShopTables.Catalogue(ShopType.Sport).Select(l => l.Id));
        foreach (var id in new[] { ItemId.PlayingCards, ItemId.PokerChips, ItemId.BeerBottle, ItemId.Gamelle })
            Assert.Contains(id, Recipes.NeverCrafted);
    }

    [Fact]
    public void The_uniform_is_sold_at_the_sport_shop_and_the_boutique()
    {
        foreach (var type in new[] { ShopType.Sport, ShopType.Boutique })
        {
            var ids = ShopTables.Catalogue(type).Select(l => l.Id).ToArray();
            Assert.Contains(ItemId.TazJacket, ids);
            Assert.Contains(ItemId.TazTrousers, ids);
            Assert.Contains(ItemId.ArmyTee, ids);
        }
        // the wardrobe's old range is untouched: the new looks lie outside it
        Assert.DoesNotContain(ItemId.TazJacket, ShopTables.PlainClothes);
    }

    [Fact]
    public void Existing_catalogue_lines_keep_their_slots()
    {
        // a line's index is the key of its sold count (ShopLedger): the barracks lines are appended
        var kiosk = ShopTables.Catalogue(ShopType.Kiosk);
        Assert.Equal(ItemId.Chocolate, kiosk[0].Id);
        Assert.Equal(ItemId.DuctTape, kiosk[6].Id);
        Assert.Equal(ItemId.BeerBottle, kiosk[7].Id);
        var grocery = ShopTables.Catalogue(ShopType.Grocery);
        Assert.Equal(ItemId.Flour, grocery[^2].Id);
        Assert.Equal(ItemId.BeerBottle, grocery[^1].Id);
    }
}
