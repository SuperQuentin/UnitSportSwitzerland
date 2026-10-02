using UnitSport.Crafting;
using UnitSport.Items;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The recipe table and the craft step (#271): data sanity, and no item made or lost by accident.</summary>
public class CraftingTests
{
    /// <summary>A pack with no slots to run out of, unless <see cref="Room"/> is set: then the rest is "dropped".</summary>
    private sealed class FakeStore : IItemStore
    {
        public readonly Dictionary<ItemId, int> Held = new();
        public readonly Dictionary<ItemId, int> Dropped = new();
        public int Room = int.MaxValue;

        public int Count(ItemId id) => Held.GetValueOrDefault(id);

        public void Take(ItemId id, int count)
        {
            Assert.True(Count(id) >= count, $"took {count} {id} with only {Count(id)}");
            Held[id] = Count(id) - count;
        }

        public void Give(ItemId id, int count)
        {
            int fits = Math.Min(count, Room);
            Room -= fits;
            Held[id] = Count(id) + fits;
            Dropped[id] = Dropped.GetValueOrDefault(id) + count - fits;
        }
    }

    private static Recipe Find(ItemId output) => Recipes.All.First(r => r.Out == output && !r.Salvage);

    [Fact]
    public void Every_recipe_is_well_formed()
    {
        foreach (var r in Recipes.All)
        {
            Assert.True(r.Count > 0, r.Key);
            Assert.True(r.Seconds > 0, r.Key);
            Assert.NotEmpty(r.In);
            Assert.All(r.In, i => Assert.True(i.Count > 0 && i.Id != ItemId.None, r.Key));
            Assert.All(Recipes.Outputs(r), o => Assert.True(o.Count > 0 && o.Id != ItemId.None, r.Key));
            // nothing makes itself: that would be an endless loop in a "craft all"
            Assert.DoesNotContain(r.In, i => Recipes.Outputs(r).Any(o => o.Id == i.Id));
            // one item per input line, so the panel's "have / need" is never split
            Assert.Equal(r.In.Length, r.In.Select(i => i.Id).Distinct().Count());
        }
        Assert.Equal(Recipes.All.Length, Recipes.All.Select(r => r.Key).Distinct().Count());
    }

    [Fact]
    public void Shop_only_items_are_never_crafted()
    {
        foreach (var r in Recipes.All)
            Assert.DoesNotContain(Recipes.Outputs(r), o => Recipes.NeverCrafted.Contains(o.Id));
    }

    [Fact]
    public void Salvage_takes_one_part_at_a_workbench()
    {
        var salvage = Recipes.All.Where(r => r.Salvage).ToList();
        Assert.NotEmpty(salvage);
        Assert.All(salvage, r => Assert.Single(r.In));
        Assert.All(salvage, r => Assert.Equal(Station.Workbench, r.Station));
    }

    [Fact]
    public void Craft_takes_inputs_and_gives_outputs()
    {
        var store = new FakeStore();
        store.Held[ItemId.Cloth] = 7;
        int made = Recipes.Craft(store, Find(ItemId.Bandage), 10, Station.Hands);
        Assert.Equal(3, made);                          // 7 cloth / 2 per batch
        Assert.Equal(1, store.Count(ItemId.Cloth));
        Assert.Equal(6, store.Count(ItemId.Bandage));   // two per batch
    }

    [Fact]
    public void Craft_refuses_away_from_its_station()
    {
        var store = new FakeStore();
        store.Held[ItemId.Cloth] = 10;
        store.Held[ItemId.Rope] = 2;
        Assert.Equal(0, Recipes.Craft(store, Find(ItemId.BeltPouch), 1, Station.Hands));
        Assert.Equal(10, store.Count(ItemId.Cloth));
        Assert.Equal(1, Recipes.Craft(store, Find(ItemId.BeltPouch), 1, Station.Hands | Station.Workbench));
    }

    [Fact]
    public void Craft_with_too_little_takes_nothing()
    {
        var store = new FakeStore();
        store.Held[ItemId.Cloth] = 1;
        store.Held[ItemId.Plastic] = 1;   // duct tape needs 2
        Assert.Equal(0, Recipes.Craft(store, Find(ItemId.DuctTape), 1, Station.Hands));
        Assert.Equal(1, store.Count(ItemId.Cloth));
        Assert.Equal(1, store.Count(ItemId.Plastic));
    }

    [Fact]
    public void A_full_pack_drops_the_output_instead_of_losing_it()
    {
        var store = new FakeStore { Room = 1 };
        store.Held[ItemId.Tyre] = 1;
        var strip = Recipes.All.First(r => r.Salvage && r.In[0].Id == ItemId.Tyre);
        Assert.Equal(1, Recipes.Craft(store, strip, 1, Station.Workbench));
        Assert.Equal(0, store.Count(ItemId.Tyre));
        Assert.Equal(3, store.Count(ItemId.Rubber) + store.Dropped[ItemId.Rubber]);
    }

    [Fact]
    public void Salvage_gives_every_output()
    {
        var store = new FakeStore();
        store.Held[ItemId.CarBattery] = 2;
        var strip = Recipes.All.First(r => r.Salvage && r.In[0].Id == ItemId.CarBattery);
        Assert.Equal(2, Recipes.Craft(store, strip, 5, Station.Workbench));
        Assert.Equal(8, store.Count(ItemId.ScrapMetal));
        Assert.Equal(4, store.Count(ItemId.CopperWire));
        Assert.Equal(2, store.Count(ItemId.Plastic));
    }

    [Fact]
    public void Max_times_is_limited_by_the_scarcest_input()
    {
        var store = new FakeStore();
        store.Held[ItemId.Cloth] = 100;
        store.Held[ItemId.Rope] = 3;
        store.Held[ItemId.DuctTape] = 9;
        Assert.Equal(1, Recipes.MaxTimes(store, Find(ItemId.Backpack)));   // 2 rope each
    }
}
