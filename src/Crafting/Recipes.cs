using UnitSport.Items;

namespace UnitSport.Crafting;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>Where a recipe can be made. Flags: you can stand at several at once.</summary>
[Flags]
public enum Station
{
    /// <summary>Anywhere, from the inventory panel.</summary>
    Hands = 1,
    /// <summary>Beside a workbench (workshops, garages, works) or a placed field workbench.</summary>
    Workbench = 2,
    /// <summary>Beside a fire: a burning campfire, or a kitchen stove indoors (#272).</summary>
    Fire = 4,
}

public readonly record struct Ingredient(ItemId Id, int Count);

/// <summary>
/// One recipe: <see cref="In"/> becomes <see cref="Count"/> × <see cref="Out"/> after
/// <see cref="Seconds"/>, at <see cref="Station"/>. <see cref="Salvage"/> recipes take a part apart.
/// </summary>
public sealed record Recipe(ItemId Out, int Count, Station Station, Ingredient[] In, float Seconds, bool Salvage = false)
{
    /// <summary>Salvage gives several things: the first is <see cref="Out"/>, these are the rest.</summary>
    public Ingredient[] Extra { get; init; } = Array.Empty<Ingredient>();

    /// <summary>Stable name for logs and checks: the output, plus the first input for salvage.</summary>
    public string Key => Salvage ? $"salvage:{In[0].Id}" : $"{Out}x{Count}";
}

/// <summary>What crafting needs from an inventory: counts, taking and giving. The game's adapter is <c>InventoryStore</c>.</summary>
public interface IItemStore
{
    /// <summary>How many plain stacks of <paramref name="id"/> there are (no per-instance data).</summary>
    int Count(ItemId id);
    /// <summary>Removes exactly <paramref name="count"/>; only called after <see cref="Count"/> said there are enough.</summary>
    void Take(ItemId id, int count);
    /// <summary>Gives the output. What does not fit is the store's business (the game drops it at your feet).</summary>
    void Give(ItemId id, int count);
}

public static class Recipes
{
    private static Ingredient I(ItemId id, int n = 1) => new(id, n);

    private static Recipe Hands(ItemId output, int count, params Ingredient[] input) =>
        new(output, count, Station.Hands, input, 1.5f);

    private static Recipe Bench(ItemId output, int count, float seconds, params Ingredient[] input) =>
        new(output, count, Station.Workbench, input, seconds);

    private static Recipe Cook(ItemId output, float seconds, params Ingredient[] input) =>
        new(output, 1, Station.Fire, input, seconds);

    /// <summary>The part taken apart is the first ingredient; the outputs are the rest.</summary>
    private static Recipe Strip(ItemId part, params Ingredient[] yields) =>
        new(yields[0].Id, yields[0].Count, Station.Workbench, new[] { I(part) }, 3f, Salvage: true)
        { Extra = yields[1..] };

    /// <summary>
    /// Every recipe, in the order the panel lists them. Built from items that already exist: the
    /// scrap, minerals and parts found in loot (ids 16-36) are what everything is made of.
    /// </summary>
    public static readonly Recipe[] All =
    {
        // ---- by hand ----
        Hands(ItemId.Bandage, 2, I(ItemId.Cloth, 2)),
        Hands(ItemId.Rope, 1, I(ItemId.Cloth, 3)),
        Hands(ItemId.DuctTape, 1, I(ItemId.Cloth), I(ItemId.Plastic, 2)),
        Hands(ItemId.SandBag, 1, I(ItemId.Cloth), I(ItemId.Stone, 3)),
        Hands(ItemId.Campfire, 1, I(ItemId.Firewood, 5), I(ItemId.Stone, 4)),
        Hands(ItemId.Torch, 1, I(ItemId.Firewood), I(ItemId.Cloth), I(ItemId.Coal)),

        // ---- at a workbench ----
        Bench(ItemId.WoodPlanks, 1, 2f, I(ItemId.Firewood, 3)),
        Bench(ItemId.SwissFlag, 1, 2f, I(ItemId.Cloth, 2), I(ItemId.WoodPlanks)),
        Bench(ItemId.FirstAidKit, 1, 3f, I(ItemId.Bandage, 3), I(ItemId.Plastic), I(ItemId.DuctTape)),
        Bench(ItemId.Binoculars, 1, 4f, I(ItemId.Glass, 2), I(ItemId.ScrapMetal, 2), I(ItemId.Rubber)),
        Bench(ItemId.SmartBinoculars, 1, 5f, I(ItemId.Binoculars), I(ItemId.Electronics, 3), I(ItemId.Glass)),
        Bench(ItemId.Gps, 1, 5f, I(ItemId.Electronics, 4), I(ItemId.CopperWire, 2), I(ItemId.Plastic), I(ItemId.Glass)),
        Bench(ItemId.Radio, 1, 5f, I(ItemId.Electronics, 3), I(ItemId.CopperWire, 2), I(ItemId.Plastic, 2), I(ItemId.CarBattery)),
        Bench(ItemId.BeltPouch, 1, 3f, I(ItemId.Cloth, 3), I(ItemId.Rope)),
        Bench(ItemId.Handbag, 1, 3f, I(ItemId.Cloth, 5), I(ItemId.Rope), I(ItemId.Screws, 2)),
        Bench(ItemId.Backpack, 1, 4f, I(ItemId.Cloth, 8), I(ItemId.Rope, 2), I(ItemId.DuctTape)),
        Bench(ItemId.Hammer, 1, 3f, I(ItemId.ScrapMetal, 2), I(ItemId.WoodPlanks)),

        // gadgets (#275): getting up high, and hiding
        Bench(ItemId.Zipline, 1, 4f, I(ItemId.Rope, 3), I(ItemId.ScrapMetal, 2)),
        Bench(ItemId.RopeLadder, 1, 3f, I(ItemId.Rope, 2), I(ItemId.WoodPlanks, 2)),
        Bench(ItemId.Trampoline, 1, 5f, I(ItemId.Tyre, 3), I(ItemId.Rubber, 2), I(ItemId.WoodPlanks, 4)),
        Bench(ItemId.LaunchPad, 1, 6f, I(ItemId.CarBattery), I(ItemId.Electronics, 2), I(ItemId.ScrapMetal, 4), I(ItemId.Cloth)),
        Bench(ItemId.CamoNet, 1, 3f, I(ItemId.Cloth, 4), I(ItemId.Rope, 2)),
        Bench(ItemId.HayHideout, 1, 3f, I(ItemId.Firewood, 6), I(ItemId.Rope, 2), I(ItemId.Cloth, 2)),

        Bench(ItemId.FieldWorkbench, 1, 6f, I(ItemId.WoodPlanks, 6), I(ItemId.Screws, 10), I(ItemId.ScrapMetal, 2)),

        // ---- at a fire: a campfire or a stove (#272) ----
        Cook(ItemId.Fondue, 8f, I(ItemId.Cheese, 2), I(ItemId.Bread), I(ItemId.MineralWater)),
        Cook(ItemId.HotChocolate, 4f, I(ItemId.Chocolate), I(ItemId.MineralWater)),
        Cook(ItemId.ToastedBread, 3f, I(ItemId.Bread)),
        Cook(ItemId.CaramelApple, 4f, I(ItemId.Apple), I(ItemId.Candy, 2)),
        Cook(ItemId.MineralWater, 5f, I(ItemId.WaterBottle)),   // boiled

        // ---- salvage: parts back into materials (always worth less than the part) ----
        Strip(ItemId.Tyre, I(ItemId.Rubber, 3)),
        Strip(ItemId.Electronics, I(ItemId.CopperWire), I(ItemId.Plastic), I(ItemId.Screws, 2)),
        Strip(ItemId.BikeChain, I(ItemId.ScrapMetal, 3), I(ItemId.Screws, 5)),
        Strip(ItemId.CarBattery, I(ItemId.ScrapMetal, 4), I(ItemId.CopperWire, 2), I(ItemId.Plastic)),
        Strip(ItemId.EnginePart, I(ItemId.ScrapMetal, 6), I(ItemId.Screws, 8), I(ItemId.CopperWire)),
    };

    /// <summary>
    /// Never made, only found or bought (#270): guns and ammunition, the camera, the biggest bag,
    /// the seasonal treats (but the caramel apple, cooked at a fire) and hats, the flare gun, the shops' own items (#273). Clothes are excluded by category in the game check.
    /// </summary>
    public static readonly HashSet<ItemId> NeverCrafted = new()
    {
        ItemId.Shotgun, ItemId.Shells, ItemId.Pistol, ItemId.Rifle, ItemId.HuntingRifle, ItemId.Knife,
        ItemId.Ammo9mm, ItemId.Ammo75, ItemId.ArmorVest, ItemId.FlareGun,
        ItemId.Camera, ItemId.HikingPack, ItemId.Francs, ItemId.Photo,
        ItemId.Candy, ItemId.Pumpkin, ItemId.Biberli, ItemId.Mandarin,
        ItemId.Grittibaenz, ItemId.Gluehwein,
        ItemId.WitchHat, ItemId.PumpkinHead, ItemId.SantaHat, ItemId.ReindeerAntlers,
        // sold only (#273): the Swiss army knife, and what only a PAUSA machine holds
        ItemId.SwissArmyKnife, ItemId.IceTea, ItemId.Crisps, ItemId.GummyBears, ItemId.IsotonicDrink,
        // Battle Royale finds (#478)
        ItemId.Alphorn, ItemId.FonduePot, ItemId.SmokeCanister,
    };

    /// <summary>Everything a recipe gives, the main output first.</summary>
    public static IEnumerable<Ingredient> Outputs(Recipe r)
    {
        yield return new Ingredient(r.Out, r.Count);
        foreach (var extra in r.Extra) yield return extra;
    }

    /// <summary>How many times <paramref name="r"/> can be made from what <paramref name="store"/> holds.</summary>
    public static int MaxTimes(IItemStore store, Recipe r)
    {
        int times = int.MaxValue;
        foreach (var need in r.In)
            times = Math.Min(times, store.Count(need.Id) / need.Count);
        return times == int.MaxValue ? 0 : times;
    }

    /// <summary>
    /// Makes <paramref name="r"/> up to <paramref name="times"/> times, at <paramref name="here"/>.
    /// Atomic per batch: the inputs are counted first, then taken, then the outputs given, so a full
    /// pack can never eat the ingredients (the store drops what does not fit). Returns how many were made.
    /// </summary>
    public static int Craft(IItemStore store, Recipe r, int times, Station here)
    {
        if ((r.Station & here) == 0 || times <= 0) return 0;
        times = Math.Min(times, MaxTimes(store, r));
        if (times <= 0) return 0;
        foreach (var need in r.In) store.Take(need.Id, need.Count * times);
        foreach (var made in Outputs(r)) store.Give(made.Id, made.Count * times);
        return times;
    }
}
