using UnitSport.Interiors;
using UnitSport.Items;

namespace UnitSport.Loot;

/// <summary>The kinds of thing a Battle Royale match fills (#194). Sent as an int: append only.</summary>
public enum MatchTable
{
    Furniture = 0, GunLocker = 1, Safe = 2, Supply = 3, Military = 4, Airdrop = 5,
    // the outdoor sites (#198)
    Bunker = 6, HighSeat = 7, HayStash = 8, SacBox = 9, Wreck = 10, FishingHut = 11,
}

/// <summary>
/// What a Battle Royale match puts in its containers and crates (#194): weapons with rounds for
/// them, ammunition, bandages and first-aid kits, armour, a little food. Pure and seeded like
/// <see cref="LootTables.Roll"/>, so a building's furniture is recomputed instead of stored.
/// Docs: <c>docs/notes/br/loot.md</c>.
/// </summary>
public static class MatchLoot
{
    private static readonly (ItemId Id, float W)[] Weapons =
        { (ItemId.Pistol, 38), (ItemId.Shotgun, 26), (ItemId.Rifle, 9), (ItemId.HuntingRifle, 3), (ItemId.Knife, 6) };   // #455
    private static readonly (ItemId Id, float W)[] Supplies =
        { (ItemId.Ammo9mm, 24), (ItemId.Shells, 20), (ItemId.Ammo75, 16), (ItemId.Bandage, 28), (ItemId.FirstAidKit, 7),
          (ItemId.ArmorVest, 8), (ItemId.EnergyBar, 8), (ItemId.Chocolate, 5), (ItemId.WaterBottle, 4) };

    /// <summary>The share of furniture that is not empty, and the weight of a weapon against supplies.</summary>
    private const float FurnitureFull = 0.5f, WeaponShare = 0.3f;

    public static MatchTable ForFurniture(FurnitureType type) => type switch
    {
        FurnitureType.GunLocker => MatchTable.GunLocker,
        FurnitureType.Safe => MatchTable.Safe,
        _ => MatchTable.Furniture,
    };

    public static List<ItemStack> Roll(MatchTable table, Random rng)
    {
        var found = new Dictionary<ItemId, int>();
        switch (table)
        {
            case MatchTable.Furniture:
                if (rng.NextDouble() >= FurnitureFull) break;
                for (int i = rng.Next(1, 3); i > 0; i--)
                    if (rng.NextDouble() < WeaponShare) Weapon(found, rng, Pick(Weapons, rng));
                    else Supply(found, rng, Pick(Supplies, rng));
                break;
            case MatchTable.Supply:
                for (int i = rng.Next(1, 3); i > 0; i--)
                    if (rng.NextDouble() < 0.2) Weapon(found, rng, rng.NextDouble() < 0.7 ? ItemId.Pistol : ItemId.Shotgun);
                    else Supply(found, rng, Pick(Supplies, rng));
                Materials(found, rng, metal: false);
                break;
            case MatchTable.Military:
                Materials(found, rng, metal: true);
                Weapon(found, rng, rng.NextDouble() < 0.6 ? ItemId.Rifle : rng.NextDouble() < 0.6 ? ItemId.Shotgun : ItemId.HuntingRifle);
                Supply(found, rng, ItemId.Ammo75);
                if (rng.NextDouble() < 0.5) Supply(found, rng, ItemId.ArmorVest);
                Supply(found, rng, ItemId.Bandage);
                break;
            case MatchTable.GunLocker:
                Weapon(found, rng, rng.NextDouble() < 0.65 ? ItemId.Rifle : ItemId.HuntingRifle);
                Supply(found, rng, ItemId.Ammo75);
                if (rng.NextDouble() < 0.35) Supply(found, rng, ItemId.ArmorVest);
                break;
            case MatchTable.Safe:
                Supply(found, rng, ItemId.ArmorVest);
                Supply(found, rng, ItemId.FirstAidKit);
                if (rng.NextDouble() < 0.5) Weapon(found, rng, ItemId.Pistol);
                break;
            case MatchTable.Bunker:
                Weapon(found, rng, ItemId.HuntingRifle);
                Supply(found, rng, ItemId.ArmorVest);
                Supply(found, rng, ItemId.FirstAidKit);
                if (rng.NextDouble() < 0.5) Weapon(found, rng, ItemId.Rifle);
                break;
            case MatchTable.HighSeat:
                Weapon(found, rng, rng.NextDouble() < 0.45 ? ItemId.HuntingRifle : ItemId.Shotgun);
                if (rng.NextDouble() < 0.5) found[ItemId.Binoculars] = 1;
                break;
            case MatchTable.HayStash:
                Supply(found, rng, ItemId.Bandage);
                Supply(found, rng, rng.NextDouble() < 0.5 ? ItemId.Apple : ItemId.Cheese);
                if (rng.NextDouble() < 0.4) Weapon(found, rng, ItemId.Knife);
                if (rng.NextDouble() < 0.3) Weapon(found, rng, ItemId.Pistol);
                break;
            case MatchTable.SacBox:
                Supply(found, rng, ItemId.FirstAidKit);
                Supply(found, rng, ItemId.Bandage);
                if (rng.NextDouble() < 0.35) found[ItemId.FlareGun] = 1;
                break;
            case MatchTable.Wreck:
                Weapon(found, rng, ItemId.Rifle);
                found[ItemId.Ammo75] = found.GetValueOrDefault(ItemId.Ammo75) + 20;
                Supply(found, rng, ItemId.ArmorVest);
                if (rng.NextDouble() < 0.5) found[ItemId.FlareGun] = 1;
                break;
            case MatchTable.FishingHut:
                Supply(found, rng, Pick(new[] { (ItemId.Ammo9mm, 1f), (ItemId.Shells, 1f), (ItemId.Ammo75, 0.6f) }, rng));
                Supply(found, rng, ItemId.CannedFood);
                if (rng.NextDouble() < 0.25) Weapon(found, rng, ItemId.Shotgun);
                break;
            case MatchTable.Airdrop:
                Weapon(found, rng, rng.NextDouble() < 0.5 ? ItemId.HuntingRifle : ItemId.Rifle);
                found[ItemId.Ammo75] = found.GetValueOrDefault(ItemId.Ammo75) + 30;
                Supply(found, rng, ItemId.ArmorVest);
                Supply(found, rng, ItemId.FirstAidKit);
                break;
        }
        return found.Select(kv => new ItemStack(kv.Key, Math.Min(kv.Value, ItemDefs.Get(kv.Key)!.MaxStack))).ToList();
    }

    private static ItemId Pick((ItemId Id, float W)[] pool, Random rng)
    {
        double r = rng.NextDouble() * pool.Sum(p => p.W);
        foreach (var (id, w) in pool)
            if ((r -= w) < 0) return id;
        return pool[^1].Id;
    }

    /// <summary>A gun comes with rounds for it: no one finds a rifle and nothing to shoot.</summary>
    private static void Weapon(Dictionary<ItemId, int> found, Random rng, ItemId gun)
    {
        found[gun] = 1;
        if (Items.Weapons.Get(gun) is { Melee: false } w) Supply(found, rng, w.Ammo);
    }

    /// <summary>
    /// Building material for the hammer (#276): a supply crate holds planks most of the time, sometimes
    /// stone; an army crate metal or sandbags.
    /// </summary>
    private static void Materials(Dictionary<ItemId, int> found, Random rng, bool metal)
    {
        void Add(ItemId id, int n) => found[id] = found.GetValueOrDefault(id) + n;
        if (metal)
        {
            if (rng.NextDouble() < 0.6) { Add(ItemId.ScrapMetal, rng.Next(8, 13)); Add(ItemId.Screws, rng.Next(4, 7)); }
            else Add(ItemId.SandBag, rng.Next(6, 10));
            return;
        }
        double r = rng.NextDouble();
        if (r < 0.6) Add(ItemId.WoodPlanks, rng.Next(10, 21));
        else if (r < 0.8) Add(ItemId.Stone, rng.Next(12, 25));
    }

    private static void Supply(Dictionary<ItemId, int> found, Random rng, ItemId id)
    {
        int n = id switch
        {
            ItemId.Ammo9mm => rng.Next(12, 25),
            ItemId.Ammo75 => rng.Next(10, 21),
            ItemId.Shells => rng.Next(5, 11),
            ItemId.Bandage => rng.Next(1, 4),
            ItemId.ArmorVest or ItemId.FirstAidKit => 1,
            _ => rng.Next(1, 3),
        };
        found[id] = found.GetValueOrDefault(id) + n;
    }
}
