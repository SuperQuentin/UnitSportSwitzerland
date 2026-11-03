using System;
using System.Collections.Generic;
using UnitSport.Items;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>An item and how many, as a chest gives them.</summary>
public readonly record struct LootLine(ItemId Item, int Count);

/// <summary>What one opening rolled: francs (to the bank account), the items, and whether a cosmetic was among them (the pity's reset).</summary>
public sealed record ChestRoll(int Francs, List<LootLine> Items, bool Cosmetic);

/// <summary>
/// A chest's contents (#1227, rewards plan 2), in the shape of <c>LootTables</c>: <see cref="Roll"/> and its
/// closed form <see cref="Chance"/> read the same candidates and the same numbers, so they cannot drift
/// (the loot note's rule: change <see cref="Roll"/>, change <see cref="Chance"/>). The odds are published:
/// the reveal card's "Odds" shows <see cref="Chance"/> per item for the tier and the player's pity now.
/// No stats: items are the game's own; the cosmetics are the Alpine set; duplicates become Bergkristall.
/// </summary>
public static class ChestLoot
{
    /// <summary>The materials a chest may hold (<c>ItemUse.Material</c>): what the workbench and the build tool use.</summary>
    public static readonly ItemId[] Materials =
    {
        ItemId.ScrapMetal, ItemId.Plastic, ItemId.WoodPlanks, ItemId.Cloth, ItemId.Glass, ItemId.CopperWire,
        ItemId.Screws, ItemId.Rubber, ItemId.DuctTape, ItemId.Rope, ItemId.Stone, ItemId.RockSalt, ItemId.Coal, ItemId.Electronics,
    };

    /// <summary>A Common chest's snack, 60 % of the time.</summary>
    public static readonly ItemId[] Consumables =
    {
        ItemId.Bread, ItemId.Cheese, ItemId.Chocolate, ItemId.Apple, ItemId.MineralWater, ItemId.EnergyBar, ItemId.CannedFood,
    };

    /// <summary>The Alpine set (<c>Avatar.Garments</c>): chests only.</summary>
    public static readonly ItemId[] Cosmetics =
    {
        ItemId.Sennenkaeppi, ItemId.EdelweissShirt, ItemId.GuideHat, ItemId.GuideBreeches, ItemId.CrossBandana, ItemId.AlpineSocks, ItemId.AlpineGloves,
    };

    /// <summary>The adventure gadgets (rewards plan 8).</summary>
    public static readonly ItemId[] Gadgets = { ItemId.TreasureDetector, ItemId.BottledGust, ItemId.TravelFlag };

    /// <summary>What a cosmetic already owned becomes ("already yours: 5 Bergkristall").</summary>
    public const int DuplicateCrystals = 5;

    /// <summary>Precious chests guarantee a cosmetic on the opening after this many without one.</summary>
    public const int PreciousPity = 7;

    private readonly record struct Table(int FrancsMin, int FrancsMax, int KindsMin, int KindsMax, int CountMin, int CountMax,
        double Consumable, double CrystalChance, int CrystalMin, int CrystalMax, double Gadget);

    // francs, material kinds and counts, the snack, Bergkristall (chance, min, max), the gadget
    private static Table Of(ChestTier t) => t switch
    {
        ChestTier.Common => new(20, 60, 1, 2, 1, 3, 0.6, 0.10, 1, 1, 0),
        ChestTier.Fine => new(60, 150, 2, 2, 2, 4, 0, 1, 1, 1, 0.06),
        ChestTier.Precious => new(150, 400, 2, 3, 2, 4, 0, 1, 2, 4, 0.25),
        _ => new(400, 1000, 3, 3, 3, 5, 0, 1, 5, 8, 0.50),
    };

    /// <summary>The chance of a cosmetic in this opening: none in a Common, Fine 4 % + 2 points per miss, Precious 15 % and sure on the 8th, Luxurious always.</summary>
    public static double CosmeticChance(ChestTier tier, int pity) => tier switch
    {
        ChestTier.Common => 0,
        ChestTier.Fine => Math.Min(1, 0.04 + 0.02 * Math.Max(0, pity)),
        ChestTier.Precious => pity >= PreciousPity ? 1 : 0.15,
        _ => 1,
    };

    /// <summary>
    /// Rolls one opening. <paramref name="pity"/> is the player's openings of this tier without a cosmetic
    /// (<see cref="ChestsProgress.Pity"/>). Every draw comes from <paramref name="rng"/> in a fixed order.
    /// </summary>
    public static ChestRoll Roll(ChestTier tier, Random rng, int pity)
    {
        var t = Of(tier);
        var items = new List<LootLine>();
        int francs = rng.Next(t.FrancsMin, t.FrancsMax + 1);
        // materials: k distinct kinds (a partial shuffle), each a few
        int kinds = rng.Next(t.KindsMin, t.KindsMax + 1);
        var pool = (ItemId[])Materials.Clone();
        for (int i = 0; i < kinds; i++)
        {
            int j = i + rng.Next(pool.Length - i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
            items.Add(new LootLine(pool[i], rng.Next(t.CountMin, t.CountMax + 1)));
        }
        if (t.Consumable > 0 && rng.NextDouble() < t.Consumable)
            items.Add(new LootLine(Consumables[rng.Next(Consumables.Length)], 1));
        if (t.CrystalChance >= 1 || rng.NextDouble() < t.CrystalChance)
            items.Add(new LootLine(ItemId.Bergkristall, rng.Next(t.CrystalMin, t.CrystalMax + 1)));
        bool cosmetic = false;
        double pc = CosmeticChance(tier, pity);
        if (pc > 0 && rng.NextDouble() < pc)
        {
            items.Add(new LootLine(Cosmetics[rng.Next(Cosmetics.Length)], 1));
            cosmetic = true;
        }
        if (t.Gadget > 0 && rng.NextDouble() < t.Gadget)
            items.Add(new LootLine(Gadgets[rng.Next(Gadgets.Length)], 1));
        return new ChestRoll(francs, items, cosmetic);
    }

    /// <summary>
    /// The exact chance an opening of <paramref name="tier"/> holds <paramref name="item"/> (any count) at
    /// <paramref name="pity"/>, before duplicates turn to crystal: the closed form of <see cref="Roll"/>.
    /// <see cref="ItemId.Francs"/> is always there.
    /// </summary>
    public static double Chance(ChestTier tier, ItemId item, int pity)
    {
        var t = Of(tier);
        if (item == ItemId.Francs) return 1;
        if (item == ItemId.Bergkristall) return t.CrystalChance;
        if (Array.IndexOf(Materials, item) >= 0) return (t.KindsMin + t.KindsMax) / 2.0 / Materials.Length;
        if (Array.IndexOf(Consumables, item) >= 0) return t.Consumable / Consumables.Length;
        if (Array.IndexOf(Cosmetics, item) >= 0) return CosmeticChance(tier, pity) / Cosmetics.Length;
        if (Array.IndexOf(Gadgets, item) >= 0) return t.Gadget / Gadgets.Length;
        return 0;
    }

    /// <summary>Every item a tier can hold, in the card's order (francs, materials, snacks, crystal, cosmetics, gadgets).</summary>
    public static IEnumerable<ItemId> Candidates(ChestTier tier)
    {
        var t = Of(tier);
        yield return ItemId.Francs;
        foreach (var m in Materials) yield return m;
        if (t.Consumable > 0) foreach (var c in Consumables) yield return c;
        yield return ItemId.Bergkristall;
        if (CosmeticChance(tier, 0) > 0) foreach (var c in Cosmetics) yield return c;
        if (t.Gadget > 0) foreach (var g in Gadgets) yield return g;
    }

    /// <summary>The francs a tier gives, for the odds card.</summary>
    public static (int Min, int Max) Francs(ChestTier tier) => (Of(tier).FrancsMin, Of(tier).FrancsMax);

    /// <summary>
    /// Settles a roll against what the player already owns: a cosmetic it has had becomes
    /// <see cref="DuplicateCrystals"/> Bergkristall (and says so); a new one is added to
    /// <paramref name="owned"/>. Returns the lines to grant and the card's note ("" when nothing changed).
    /// </summary>
    public static (List<LootLine> Grant, string Note) Settle(ChestRoll roll, ISet<string> owned)
    {
        var grant = new List<LootLine>(roll.Items.Count);
        string note = "";
        foreach (var line in roll.Items)
        {
            if (Array.IndexOf(Cosmetics, line.Item) >= 0 && !owned.Add(line.Item.ToString()))
            {
                grant.Add(new LootLine(ItemId.Bergkristall, DuplicateCrystals));
                note = $"already yours: {DuplicateCrystals} Bergkristall";
                continue;
            }
            grant.Add(line);
        }
        return (grant, note);
    }

    /// <summary>The pity after an opening: back to 0 with a cosmetic, one more without (tiers that can give one).</summary>
    public static int PityAfter(ChestTier tier, int pity, bool gotCosmetic) =>
        tier is ChestTier.Fine or ChestTier.Precious ? gotCosmetic ? 0 : pity + 1 : 0;
}

/// <summary>
/// The <c>chests</c> section (#1227): the chests this player opened (Luxurious ones by edition,
/// <see cref="RewardRules.OpenId"/>), the pity per tier, the cosmetics ever granted (the inventory is local and
/// could be dropped, so duplicates are judged here), and tallies for the Collection tab.
/// </summary>
public sealed class ChestsProgress
{
    public static readonly SectionKey<ChestsProgress> Section = new("chests", Version: 1);

    public HashSet<string> Opened { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Openings without a cosmetic, per <see cref="ChestTier"/>.</summary>
    public int[] Pity { get; set; } = new int[4];
    /// <summary>Cosmetics ever granted, by <see cref="ItemId"/> name.</summary>
    public HashSet<string> Owned { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Chests opened per tier.</summary>
    public int[] ByTier { get; set; } = new int[4];
    /// <summary>Chests opened per canton (two-letter code, "" outside every canton).</summary>
    public Dictionary<string, int> ByCanton { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The <see cref="RewardPlacer.Version"/> the ids were made by (a generator change moves ids).</summary>
    public int PlacementVersion { get; set; }

    /// <summary>All openings so far: part of each roll's seed.</summary>
    public int Count => ByTier[0] + ByTier[1] + ByTier[2] + ByTier[3];

    public int PityOf(ChestTier tier) => Pity.Length > (int)tier ? Pity[(int)tier] : 0;
}
