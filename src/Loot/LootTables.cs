using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// What can be found in a piece of furniture, as pure data plus one roll function — no Godot, so
/// <see cref="LootProbe"/> can run thousands of rolls and print what the tables really give.
///
/// <para>
/// Three layers multiply together. An item's <b>tier</b> (common 60 / uncommon 28 / rare 10 /
/// very rare 2) weighs it within its pool. A <b>container</b> (fridge, workbench, car…) chooses
/// which pools it draws from, how often it is empty and how many rolls it gets. The
/// <b>building kind</b> scales whole categories: a factory has more scrap and fewer sandwiches, a
/// church has only the offering box.
/// </para>
/// </summary>
public static class LootTables
{
    private enum Tier { Common, Uncommon, Rare, VeryRare }

    private static float TierWeight(Tier t) => t switch
    {
        Tier.Common => 60f,
        Tier.Uncommon => 28f,
        Tier.Rare => 10f,
        _ => 2f,
    };

    private static readonly Dictionary<ItemId, Tier> Tiers = new()
    {
        [ItemId.Bread] = Tier.Common, [ItemId.CannedFood] = Tier.Common, [ItemId.Apple] = Tier.Common,
        [ItemId.Cheese] = Tier.Uncommon, [ItemId.Chocolate] = Tier.Uncommon, [ItemId.EnergyBar] = Tier.Uncommon,
        [ItemId.WaterBottle] = Tier.Common, [ItemId.MineralWater] = Tier.Uncommon,
        [ItemId.Bandage] = Tier.Uncommon, [ItemId.FirstAidKit] = Tier.Rare,
        [ItemId.ScrapMetal] = Tier.Common, [ItemId.Plastic] = Tier.Common, [ItemId.WoodPlanks] = Tier.Common,
        [ItemId.Cloth] = Tier.Common, [ItemId.Screws] = Tier.Common,
        [ItemId.Glass] = Tier.Uncommon, [ItemId.CopperWire] = Tier.Uncommon, [ItemId.Rubber] = Tier.Uncommon,
        [ItemId.DuctTape] = Tier.Uncommon, [ItemId.Rope] = Tier.Uncommon,
        [ItemId.Stone] = Tier.Common, [ItemId.Firewood] = Tier.Common,
        [ItemId.SandBag] = Tier.Uncommon, [ItemId.RockSalt] = Tier.Uncommon, [ItemId.Coal] = Tier.Rare,
        [ItemId.Electronics] = Tier.Uncommon,
        [ItemId.BikeChain] = Tier.Rare, [ItemId.Tyre] = Tier.Rare, [ItemId.CarBattery] = Tier.Rare,
        [ItemId.FuelCan] = Tier.Rare, [ItemId.EnginePart] = Tier.VeryRare,
        [ItemId.SmartBinoculars] = Tier.VeryRare,
        // only in the locked containers (gun lockers, safes): category Gear, so no category pool picks them up
        [ItemId.Shotgun] = Tier.Rare, [ItemId.Shells] = Tier.Common,
    };

    // ---- pools ------------------------------------------------------------------------------

    private static ItemId[] Of(ItemCategory c) =>
        Tiers.Keys.Where(id => ItemDefs.Get(id)?.Category == c).ToArray();

    private static readonly ItemId[] Food = Of(ItemCategory.Food);
    private static readonly ItemId[] Water = Of(ItemCategory.Water);
    private static readonly ItemId[] Medical = Of(ItemCategory.Medical);
    private static readonly ItemId[] Scrap = Of(ItemCategory.Scrap);
    private static readonly ItemId[] Minerals = Of(ItemCategory.Mineral);
    private static readonly ItemId[] Parts = Of(ItemCategory.Part);
    private static readonly ItemId[] VehicleParts = { ItemId.FuelCan, ItemId.CarBattery, ItemId.Tyre, ItemId.EnginePart };
    private static readonly ItemId[] KitchenScrap = { ItemId.Plastic, ItemId.Glass };
    private static readonly ItemId[] Cloth = { ItemId.Cloth };
    private static readonly ItemId[] Sweets = { ItemId.Chocolate };
    private static readonly ItemId[] Gadgets = { ItemId.Electronics };
    private static readonly ItemId[] Wire = { ItemId.CopperWire };
    private static readonly ItemId[] Hardware = { ItemId.Screws, ItemId.DuctTape, ItemId.CopperWire, ItemId.Rope };
    private static readonly ItemId[] Optics = { ItemId.SmartBinoculars };
    private static readonly ItemId[] Tins = { ItemId.CannedFood };
    private static readonly ItemId[] BarnStuff = { ItemId.Rope, ItemId.Firewood, ItemId.Apple };
    private static readonly ItemId[] Fuel = { ItemId.Firewood, ItemId.Coal };
    private static readonly ItemId[] Guns = { ItemId.Shotgun };
    private static readonly ItemId[] Ammo = { ItemId.Shells };
    private static readonly ItemId[] Pantry = { ItemId.CannedFood, ItemId.WaterBottle, ItemId.MineralWater };

    private readonly record struct Pool(ItemId[] Items, float Weight);

    private sealed record Container(float Empty, int MinRolls, int MaxRolls, Pool[] Pools,
        float FrancsChance = 0, int FrancsMin = 0, int FrancsMax = 0);

    private static Pool P(ItemId[] items, float weight) => new(items, weight);

    private static readonly Dictionary<FurnitureType, Container> Containers = new()
    {
        [FurnitureType.Fridge] = new(0.25f, 1, 3, new[] { P(Food, 60), P(Water, 40) }),
        [FurnitureType.Counter] = new(0.40f, 0, 2, new[] { P(Food, 50), P(Water, 35), P(KitchenScrap, 15) }, 0.20f, 1, 5),
        [FurnitureType.Stove] = new(0.40f, 0, 2, new[] { P(Food, 60), P(Water, 20), P(KitchenScrap, 20) }, 0.20f, 1, 5),
        [FurnitureType.Shelf] = new(0.35f, 1, 2, new[] { P(Food, 25), P(Water, 10), P(Scrap, 35), P(Medical, 10), P(Minerals, 15), P(Parts, 5), P(Optics, 1) }, 0.10f, 1, 5),
        [FurnitureType.Wardrobe] = new(0.45f, 1, 1, new[] { P(Cloth, 60), P(Medical, 20), P(Scrap, 20) }, 0.25f, 5, 40),
        [FurnitureType.Nightstand] = new(0.45f, 0, 1, new[] { P(Medical, 40), P(Sweets, 30), P(Gadgets, 30) }, 0.40f, 2, 20),
        [FurnitureType.Desk] = new(0.40f, 1, 2, new[] { P(Gadgets, 40), P(Wire, 30), P(Scrap, 30), P(Optics, 1.5f) }, 0.35f, 5, 50),
        [FurnitureType.Crate] = new(0.20f, 2, 4, new[] { P(Scrap, 45), P(Minerals, 30), P(Tins, 15), P(Parts, 10) }),
        [FurnitureType.Rack] = new(0.20f, 2, 4, new[] { P(Scrap, 45), P(Minerals, 30), P(Tins, 15), P(Parts, 10) }),
        [FurnitureType.Workbench] = new(0.15f, 2, 4, new[] { P(Hardware, 50), P(Parts, 25), P(Scrap, 25) }),
        [FurnitureType.Car] = new(0.30f, 1, 2, new[] { P(VehicleParts, 100) }, 0.15f, 1, 10),
        [FurnitureType.HayBale] = new(0.60f, 0, 1, new[] { P(BarnStuff, 100) }),
        [FurnitureType.ShopCounter] = new(0.10f, 1, 3, new[] { P(Food, 50), P(Water, 30), P(Sweets, 20) }, 0.90f, 20, 150),
        [FurnitureType.Altar] = new(0.50f, 0, 0, Array.Empty<Pool>(), 0.70f, 1, 15),
        // locked (IsLocked): cracked with the dial first, and stocked whatever the building's budget
        [FurnitureType.GunLocker] = new(0.15f, 1, 2, new[] { P(Guns, 40), P(Ammo, 60) }),
        [FurnitureType.Safe] = new(0.10f, 1, 2, new[] { P(Gadgets, 35), P(Ammo, 20), P(Medical, 15), P(Optics, 8), P(Guns, 6) }, 0.90f, 50, 400),
    };

    /// <summary>
    /// The same furniture holds different things in different rooms: a shelf in a garage is
    /// tools and parts, in a living room sweets and gadgets, in a cellar the emergency supplies
    /// every Swiss household keeps. The room comes from the plan (<see cref="InteriorLayout.RoomOf"/>),
    /// so this needs nothing stored; a type/room pair not listed falls back to <see cref="Containers"/>.
    /// </summary>
    private static readonly Dictionary<(FurnitureType, RoomType), Container> RoomContainers = new()
    {
        [(FurnitureType.Shelf, RoomType.Garage)] = new(0.25f, 1, 3, new[] { P(Hardware, 40), P(Parts, 25), P(Scrap, 30), P(Minerals, 5) }),
        [(FurnitureType.Shelf, RoomType.Workshop)] = new(0.25f, 1, 3, new[] { P(Hardware, 40), P(Parts, 25), P(Scrap, 30), P(Minerals, 5) }),
        [(FurnitureType.Shelf, RoomType.Storage)] = new(0.30f, 1, 3, new[] { P(Pantry, 40), P(Scrap, 20), P(Hardware, 10), P(Medical, 10), P(Minerals, 15), P(Parts, 5), P(Optics, 1) }),
        [(FurnitureType.Shelf, RoomType.Living)] = new(0.40f, 1, 2, new[] { P(Sweets, 25), P(Gadgets, 30), P(Medical, 15), P(Cloth, 10), P(Food, 10), P(Fuel, 12), P(Parts, 4), P(Optics, 1) }, 0.15f, 1, 10),   // logs and coal for the stove
        [(FurnitureType.Shelf, RoomType.Dining)] = new(0.40f, 1, 2, new[] { P(Food, 35), P(Water, 25), P(Sweets, 20), P(KitchenScrap, 20) }, 0.10f, 1, 5),
        [(FurnitureType.Shelf, RoomType.Office)] = new(0.35f, 1, 2, new[] { P(Gadgets, 45), P(Wire, 20), P(Scrap, 25), P(Optics, 2) }, 0.15f, 5, 25),
        // a hall shelf: shoes and coats, the winter road salt and firewood by the door, a bike part
        [(FurnitureType.Shelf, RoomType.Hall)] = new(0.50f, 1, 1, new[] { P(Cloth, 25), P(Medical, 15), P(Sweets, 10), P(Minerals, 35), P(Parts, 15) }, 0.20f, 1, 5),
        [(FurnitureType.Shelf, RoomType.Lobby)] = new(0.50f, 1, 1, new[] { P(Cloth, 25), P(Medical, 15), P(Sweets, 10), P(Minerals, 35), P(Parts, 15) }, 0.20f, 1, 5),
        [(FurnitureType.Desk, RoomType.Bedroom)] = new(0.45f, 1, 1, new[] { P(Sweets, 40), P(Gadgets, 40), P(Cloth, 20) }, 0.30f, 2, 20),
        [(FurnitureType.Desk, RoomType.Classroom)] = new(0.55f, 0, 1, new[] { P(Sweets, 35), P(Gadgets, 25), P(Scrap, 40) }, 0.10f, 1, 5),
        [(FurnitureType.Crate, RoomType.Storage)] = new(0.25f, 1, 3, new[] { P(Pantry, 35), P(Scrap, 25), P(Minerals, 30), P(Parts, 10) }),
        [(FurnitureType.Crate, RoomType.Barn)] = new(0.30f, 1, 3, new[] { P(BarnStuff, 55), P(Minerals, 30), P(Tins, 15) }),
        [(FurnitureType.Workbench, RoomType.Garage)] = new(0.15f, 2, 4, new[] { P(Hardware, 40), P(Parts, 25), P(VehicleParts, 15), P(Scrap, 20) }),
    };

    public static bool IsLootable(FurnitureType type) => Containers.ContainsKey(type);

    /// <summary>Opened with the dial (<see cref="LockPickUi"/>) before anything in it can be searched.</summary>
    public static bool IsLocked(FurnitureType type) => type is FurnitureType.GunLocker or FurnitureType.Safe;

    private static Container? ContainerFor(FurnitureType type, RoomType? room) =>
        room is { } r && RoomContainers.TryGetValue((type, r), out var rc) ? rc
        : Containers.TryGetValue(type, out var c) ? c : null;

    // ---- building kinds -----------------------------------------------------------------------

    /// <summary>How strongly a kind of building stocks a category; 0 removes it.</summary>
    private static float KindFactor(BuildingKind kind, ItemCategory c) => kind switch
    {
        BuildingKind.Commercial => c == ItemCategory.Food ? 1.5f : 1f,
        BuildingKind.Industrial => c switch
        {
            ItemCategory.Scrap or ItemCategory.Part => 2f,
            ItemCategory.Food => 0.3f,
            _ => 1f,
        },
        BuildingKind.Agricultural => c switch
        {
            ItemCategory.Food => 1.3f,
            ItemCategory.Mineral => 2f,
            _ => 1f,
        },
        BuildingKind.Annex or BuildingKind.Garage => c switch
        {
            ItemCategory.Food => 0f,
            ItemCategory.Water => 0.5f,
            ItemCategory.Scrap or ItemCategory.Mineral or ItemCategory.Part => 1.5f,
            _ => 1f,
        },
        BuildingKind.Civic => c == ItemCategory.Medical ? 3f : 1f,
        BuildingKind.Sacral => 0f,
        BuildingKind.UnderConstruction => c switch
        {
            ItemCategory.Mineral => 3f,
            ItemCategory.Scrap => 2f,
            _ => 0f,
        },
        _ => 1f,
    };

    private static float RollFactor(BuildingKind kind) => kind == BuildingKind.Apartment ? 0.8f : 1f;

    private static float FrancsFactor(BuildingKind kind) => kind switch
    {
        BuildingKind.Commercial => 1.5f,
        BuildingKind.UnderConstruction or BuildingKind.Annex or BuildingKind.Garage => 0.3f,
        _ => 1f,
    };

    // ---- rolling ------------------------------------------------------------------------------

    /// <summary>
    /// The contents of one piece of furniture. Deterministic in <paramref name="rng"/>, so the
    /// server can always recompute what a container holds instead of storing it. Identical items
    /// are merged into one stack, so a container never lists "Bread 1, Bread 2".
    /// </summary>
    public static List<ItemStack> Roll(BuildingKind kind, FurnitureType type, Random rng, float abundance = 1f, RoomType? room = null)
    {
        var result = new List<ItemStack>();
        if (ContainerFor(type, room) is not { } c) return result;
        if (rng.NextDouble() >= (1 - c.Empty) * abundance) return result;

        var candidates = Candidates(kind, c);

        var found = new Dictionary<ItemId, int>();
        float total = candidates.Sum(x => x.W);
        if (total > 0)
        {
            float rolls = rng.Next(c.MinRolls, c.MaxRolls + 1) * RollFactor(kind);
            int n = (int)rolls + (rng.NextDouble() < rolls - (int)rolls ? 1 : 0);
            for (int i = 0; i < n; i++)
            {
                double pick = rng.NextDouble() * total;
                var id = candidates[^1].Id;
                foreach (var (cid, w) in candidates)
                {
                    pick -= w;
                    if (pick < 0) { id = cid; break; }
                }
                found[id] = found.GetValueOrDefault(id) + Quantity(id, rng);
            }
        }

        if (c.FrancsChance > 0 && rng.NextDouble() < c.FrancsChance * FrancsFactor(kind))
        {
            float scale = kind == BuildingKind.Commercial ? 1.5f : 1f;
            found[ItemId.Francs] = (int)MathF.Round(rng.Next(c.FrancsMin, c.FrancsMax + 1) * scale);
        }

        // A running occasion's treats (#18): one extra roll, drawn last, so with no occasion the
        // stream — and every container's contents — is exactly what it always was, and a take
        // mask saved before the occasion still lines up with the stacks before this one.
        if (Seasonal?.Invoke(type) is { Items.Length: > 0 } treats && rng.NextDouble() < treats.Chance)
        {
            var id = treats.Items[rng.Next(treats.Items.Length)];
            found[id] = found.GetValueOrDefault(id) + rng.Next(1, 4);
        }

        foreach (var (id, count) in found)
            result.Add(new ItemStack(id, Math.Min(count, ItemDefs.Get(id)!.MaxStack)));
        return result;
    }

    /// <summary>
    /// Every item a container can give with its weight, the kind applied (an item in two pools
    /// appears twice). One place for <see cref="Roll"/> and <see cref="Chance"/>, so they cannot drift.
    /// </summary>
    private static List<(ItemId Id, float W)> Candidates(BuildingKind kind, Container c)
    {
        var candidates = new List<(ItemId Id, float W)>();
        foreach (var pool in c.Pools)
        {
            float tierSum = pool.Items.Sum(id => TierWeight(Tiers[id]));
            foreach (var id in pool.Items)
            {
                var cat = ItemDefs.Get(id)!.Category;
                float w = pool.Weight * TierWeight(Tiers[id]) / tierSum * KindFactor(kind, cat);
                if (w > 0) candidates.Add((id, w));
            }
        }
        return candidates;
    }

    // ---- exact chances (smart binoculars) -------------------------------------------------------

    /// <summary>Every item some container's pools can give, plus francs: what a target can be.</summary>
    public static IReadOnlyList<ItemId> Targets() => _targets ??= Containers.Values.Concat(RoomContainers.Values)
        .SelectMany(c => c.Pools.SelectMany(p => p.Items)).Append(ItemId.Francs)
        .Distinct().OrderBy(id => ItemDefs.Get(id)!.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    private static ItemId[]? _targets;

    /// <summary>
    /// The exact probability that one container of this type, in a building of this kind, holds at
    /// least one of <paramref name="item"/> when restocked: a pure function of the tables, the
    /// same arithmetic as <see cref="Roll"/> worked out instead of sampled. Never depends on a
    /// building's actual rolled contents or on what was taken, so it reveals nothing.
    /// <paramref name="now"/> is reserved: the occasions' treats follow the occasions running now.
    /// </summary>
    public static double Chance(BuildingKind kind, FurnitureType type, float abundance, ItemId item, DateTime? now = null, RoomType? room = null)
    {
        if (item == ItemId.Francs) return ChanceFrancs(kind, type, abundance, room);
        if (ContainerFor(type, room) is not { } c) return 0;

        // everything below happens only in a container that was not empty, so work conditionally
        double any = 0;
        var cand = Candidates(kind, c);
        double total = cand.Sum(x => (double)x.W);
        double pk = total > 0 ? cand.Where(x => x.Id == item).Sum(x => (double)x.W) / total : 0;
        if (pk > 0)
        {
            // each of MinRolls..MaxRolls equally likely; a fractional count rounds up with that probability
            int span = c.MaxRolls - c.MinRolls + 1;
            for (int r = c.MinRolls; r <= c.MaxRolls; r++)
            {
                float rolls = r * RollFactor(kind);
                int lo = (int)rolls;
                double up = rolls - lo;
                any += ((1 - up) * (1 - Math.Pow(1 - pk, lo)) + up * (1 - Math.Pow(1 - pk, lo + 1))) / span;
            }
        }

        double seasonal = 0;   // the occasions' extra roll, drawn after the empty check, like Roll
        if (Seasonal?.Invoke(type) is { Items.Length: > 0 } treats)
            seasonal = Math.Clamp(treats.Chance, 0f, 1f) * treats.Items.Count(i => i == item) / treats.Items.Length;
        return NonEmpty(c, abundance) * (1 - (1 - any) * (1 - seasonal));
    }

    /// <summary>Probability a container gives francs (they are cash, not an item: <see cref="Roll"/> stacks them as <see cref="ItemId.Francs"/>).</summary>
    public static double ChanceFrancs(BuildingKind kind, FurnitureType type, float abundance, RoomType? room = null) =>
        ContainerFor(type, room) is { } c && c.FrancsChance > 0
            ? NonEmpty(c, abundance) * Math.Min(1.0, c.FrancsChance * FrancsFactor(kind))
            : 0;

    private static double NonEmpty(Container c, float abundance) => Math.Clamp((1 - c.Empty) * (double)abundance, 0, 1);

    /// <summary>One line of a building's chance breakdown: a furniture type, how many, and the chance per piece.</summary>
    /// <summary><see cref="Each"/> is the mean per piece (pieces in different rooms differ); <see cref="Group"/> = at least one of the <see cref="Count"/> pieces yields it.</summary>
    public readonly record struct ContainerChance(FurnitureType Type, int Count, double Each, double Group);

    /// <summary>The building's chance of the item in at least one container, and its container types best first (by the chance that some piece of the type has it).</summary>
    public readonly record struct BuildingOdds(double Any, IReadOnlyList<ContainerChance> Containers)
    {
        public ContainerChance? Best => Containers.Count > 0 ? Containers[0] : null;
    }

    /// <summary>P(at least one container in the building yields <paramref name="item"/>) = 1 − Π(1 − pᵢ), with the per-type breakdown.</summary>
    public static BuildingOdds BuildingChance(InteriorLayout layout, ItemId item, DateTime? now = null)
    {
        double none = 1;
        var groups = new Dictionary<FurnitureType, (int Count, double Sum, double None)>();
        for (int i = 0; i < layout.Furniture.Count; i++)
        {
            var f = layout.Furniture[i];
            if (!IsLootable(f.Type)) continue;
            double p = Chance(layout.Kind, f.Type, AbundanceFor(layout, f.Type), item, now, layout.RoomOf(f)?.Type);
            none *= 1 - p;
            var g = groups.GetValueOrDefault(f.Type, (0, 0, 1));
            groups[f.Type] = (g.Count + 1, g.Sum + p, g.None * (1 - p));
        }
        var list = groups
            .Select(kv => new ContainerChance(kv.Key, kv.Value.Count, kv.Value.Sum / kv.Value.Count, 1 - kv.Value.None))
            .Where(x => x.Each > 0).OrderByDescending(x => x.Group).ToList();
        return new BuildingOdds(1 - none, list);
    }

    /// <summary>
    /// What a scanner shows for one building: every item it can give with the chance that at
    /// least one container has it, best first, francs included. Pure arithmetic, like <see cref="BuildingChance"/>.
    /// </summary>
    public static List<(ItemId Item, double Chance)> BuildingTable(InteriorLayout layout, DateTime? now = null)
    {
        var rows = new List<(ItemId, double)>();
        foreach (var item in Targets())
        {
            double any = BuildingChance(layout, item, now).Any;
            if (any > 0.0005) rows.Add((item, any));
        }
        rows.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return rows;
    }

    /// <summary>
    /// The running occasions' extra roll for a kind of furniture — a chance and what it draws from
    /// — or null. Set by <see cref="Occasions.OccasionManager"/>; stays null (no extra roll) with
    /// no occasion running, which keeps this class free of Godot for <see cref="LootProbe"/>.
    /// </summary>
    public static Func<FurnitureType, (float Chance, ItemId[] Items)?>? Seasonal { get; set; }

    private static int Quantity(ItemId id, Random rng)
    {
        if (id == ItemId.Shells) return rng.Next(4, 13);   // a box, not a single shell
        int q = Tiers[id] switch
        {
            Tier.Common => rng.Next(1, 4),
            Tier.Uncommon => rng.Next(1, 3),
            _ => 1,
        };
        return q;
    }

    /// <summary>
    /// How many containers' worth of loot a building holds, whatever its furniture count. A house
    /// has six wardrobes and six nightstands, a school a desk in every classroom: stocking each
    /// piece fully would make a school worth fifty houses. The budget grows with floors, so a
    /// block of flats is still richer than a cottage.
    /// </summary>
    public static float Abundance(InteriorLayout layout)
    {
        int lootable = layout.Furniture.Count(f => IsLootable(f.Type) && !IsLocked(f.Type));
        if (lootable == 0) return 1f;
        float budget = (6f + 3f * Math.Max(1, layout.Floors.Count)) * BudgetFactor(layout.Kind);
        return Math.Min(1f, budget / lootable);
    }

    /// <summary>A shop or a works keeps more stock than a home; a shed or a garage less.</summary>
    private static float BudgetFactor(BuildingKind kind) => kind switch
    {
        BuildingKind.Commercial => 1.3f,
        BuildingKind.Industrial => 1.2f,
        BuildingKind.Annex or BuildingKind.Garage => 0.7f,
        _ => 1f,
    };

    /// <summary>A locked container is always fully stocked: cracking it is the price, and it does not count against the building's budget.</summary>
    public static float AbundanceFor(InteriorLayout layout, FurnitureType type) => IsLocked(type) ? 1f : Abundance(layout);

    /// <summary>
    /// The dial combination of a locked container this restock period: 3 numbers (gun locker) or
    /// 4 (safe) on a 0–99 dial, each at least 15 from the one before. Derived like its contents,
    /// so the server checks a claimed combination without storing one.
    /// </summary>
    public static int[] Combination(string buildingKey, int furnitureIndex, long epoch, FurnitureType type)
    {
        var rng = new Random(InteriorGenerator.StableHash($"{buildingKey}|{furnitureIndex}|{epoch}|lock"));
        var combo = new int[type == FurnitureType.Safe ? 4 : 3];
        for (int i = 0; i < combo.Length; i++)
        {
            int v;
            do v = rng.Next(0, 100);
            while (i > 0 && DialDistance(v, combo[i - 1]) < 15);
            combo[i] = v;
        }
        return combo;
    }

    /// <summary>Distance between two dial numbers, the short way round.</summary>
    public static float DialDistance(float a, float b)
    {
        float d = Math.Abs(a - b) % 100f;
        return Math.Min(d, 100f - d);
    }

    // ---- restocking -------------------------------------------------------------------------

    public const long RestockSeconds = 24 * 3600;

    /// <summary>Added to every epoch, for testing a restock without waiting a day (<c>--lootepoch +N</c>).</summary>
    public static long EpochOffset { get; set; }

    /// <summary>
    /// Which restock period a building is in. Offset per building by its own hash, so a whole
    /// town does not refill at the same instant and a player cannot wait at midnight for it.
    /// </summary>
    public static long Epoch(string buildingKey, long unixSeconds)
    {
        long stagger = (uint)InteriorGenerator.StableHash(buildingKey) % RestockSeconds;
        return (unixSeconds + stagger) / RestockSeconds + EpochOffset;
    }

    /// <summary>The seed for one piece of furniture in one restock period.</summary>
    public static Random RngFor(string buildingKey, int furnitureIndex, long epoch) =>
        new(InteriorGenerator.StableHash($"{buildingKey}|{furnitureIndex}|{epoch}"));

    /// <summary>What a piece of furniture holds this period, before anyone has taken anything.</summary>
    public static List<ItemStack> ContentsOf(InteriorLayout layout, int furnitureIndex, long epoch)
    {
        if (furnitureIndex < 0 || furnitureIndex >= layout.Furniture.Count) return new();
        var f = layout.Furniture[furnitureIndex];
        return Roll(layout.Kind, f.Type, RngFor(layout.Key, furnitureIndex, epoch), AbundanceFor(layout, f.Type), layout.RoomOf(f)?.Type);
    }

    /// <summary>A name for the prompt: "Search the fridge".</summary>
    public static string Describe(FurnitureType t) => t switch
    {
        FurnitureType.GunLocker => "gun locker",
        FurnitureType.ShopCounter => "shop counter",
        FurnitureType.HayBale => "hay bale",
        FurnitureType.Car => "car",
        FurnitureType.Altar => "offering box",
        _ => t.ToString().ToLowerInvariant(),
    };
}
