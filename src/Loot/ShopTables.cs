using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md). Item values come
// in as a function (the game passes ItemDefs' Value), so the tables never need the item rows.

/// <summary>What a shop sells (#273). Stored in plans as a number: <b>append only</b>.</summary>
public enum ShopType : byte
{
    None = 0, Grocery = 1, Hardware = 2, Kiosk = 3, Pharmacy = 4, Sport = 5, Boutique = 6,
    Electronics = 7, GunShop = 8, Garage = 9,
    /// <summary>A PAUSA vending machine: a piece of furniture, never a building's type.</summary>
    Vending = 10,
    /// <summary>The farm co-op (#494): seeds, the hoe and fertiliser; buys produce; only on a rural tile.</summary>
    FarmCoop = 11,
}

/// <summary>How a line may be paid.</summary>
public enum Payment : byte { Cash, CashOrCard, Card }

/// <summary>How the buyer chose to pay: cash from the pocket, or the card (the server-kept bank account).</summary>
public enum PayWith : byte { Cash = 0, Card = 1 }

/// <summary>One line of a shop's catalogue: carried with probability <see cref="Chance"/> each restock, then <see cref="Min"/>..<see cref="Max"/> of it.</summary>
public readonly record struct ShopLine(ItemId Id, float Chance, int Min, int Max);

/// <summary>One line of what a counter or a machine holds this restock period, before anything was sold.</summary>
public readonly record struct StockLine(int Slot, ItemId Id, int Stock, int Price, Payment Pay);

/// <summary>
/// Shops and PAUSA vending machines as pure tables, the way <see cref="LootTables"/> is for loot.
/// <b>Stock is computed, never stored</b>: <see cref="Stock"/> is seeded by building key, furniture
/// index and restock epoch, so the server only remembers how many of each line were <i>sold</i>
/// (<see cref="ShopLedger"/>). <see cref="Chance"/> is the closed form of the stock roll (what the
/// smart binoculars show), unit-tested against sampling.
/// </summary>
public static class ShopTables
{
    /// <summary>From this price on, a line outside a kiosk, a grocery or a machine may go on the card.</summary>
    public const int CardFrom = 20;
    /// <summary>A shop pays this share of an item's value for it.</summary>
    public const double SellShare = 0.35;
    /// <summary>A tile with fewer buildings than this is countryside: only there is a gun shop.</summary>
    public const int RuralBuildings = 250;

    public const int VendingRows = 4, VendingCols = 6, VendingSlots = VendingRows * VendingCols;
    public const int VendingMin = 3, VendingMax = 8;
    /// <summary>A bought item stays caught on the spiral this often (a hit on the machine frees it).</summary>
    public const double StuckChance = 0.04;
    /// <summary>A hit that frees a stuck item drops one more now and then.</summary>
    public const double BonusChance = 0.15;
    /// <summary>A machine is "Hors service" for a whole restock period this often.</summary>
    public const double OutOfOrderChance = 0.06;

    // ---- which shop -----------------------------------------------------------------------------

    private static readonly (ShopType Type, int Weight)[] Weights =
    {
        (ShopType.Grocery, 35), (ShopType.Hardware, 15), (ShopType.Kiosk, 15), (ShopType.Pharmacy, 10),
        (ShopType.Sport, 8), (ShopType.Boutique, 8), (ShopType.Electronics, 6), (ShopType.GunShop, 3), (ShopType.FarmCoop, 8),
    };

    public static bool IsRural(int tileBuildings) => tileBuildings < RuralBuildings;

    /// <summary>Shops that only the countryside has: the gun shop and the farm co-op (#494).</summary>
    public static bool RuralOnly(ShopType type) => type is ShopType.GunShop or ShopType.FarmCoop;

    /// <summary>A big agricultural building is a farm co-op this often (rural tiles only, #494).</summary>
    public const double CoopShare = 0.12;
    /// <summary>The smallest agricultural building (plan area, m²) that can be a farm co-op.</summary>
    public const float CoopMinArea = 120f;

    /// <summary>
    /// A building's shop, from nothing but what every peer knows about it: every shop or office
    /// building that is not a bank is a shop (its ground floor has a counter), its type drawn from
    /// <see cref="Weights"/> by the key's hash (a gun shop only in the countryside); about one garage
    /// or shed in four big enough to hold a car and a bench is a mechanic's.
    /// </summary>
    public static ShopType TypeFor(string key, BuildingKind kind, float area, bool bank, bool rural)
    {
        if (kind == BuildingKind.Commercial && !bank)
        {
            int total = Weights.Where(w => rural || !RuralOnly(w.Type)).Sum(w => w.Weight);
            double pick = Fnv.Unit(key + "|shop") * total;
            foreach (var (type, weight) in Weights)
            {
                if (!rural && RuralOnly(type)) continue;
                pick -= weight;
                if (pick < 0) return type;
            }
            return ShopType.Grocery;
        }
        if (rural && kind == BuildingKind.Agricultural && area >= CoopMinArea && Fnv.Unit(key + "|coop") < CoopShare)
            return ShopType.FarmCoop;
        if (kind is BuildingKind.Garage or BuildingKind.Annex && area >= 30f && Fnv.Unit(key + "|garage") < 0.25)
            return ShopType.Garage;
        return ShopType.None;
    }

    /// <summary>The chance <see cref="TypeFor"/> gives a shop or office building this type.</summary>
    public static double TypeShare(ShopType type, bool rural)
    {
        int total = Weights.Where(w => rural || !RuralOnly(w.Type)).Sum(w => w.Weight);
        var row = Weights.FirstOrDefault(w => w.Type == type);
        return row.Type != type || (!rural && RuralOnly(type)) ? 0 : row.Weight / (double)total;
    }

    // ---- what each sells ------------------------------------------------------------------------

    /// <summary>Clothes with a finish that moves (<c>Avatar.Garments</c>, IsSpecial): rare at a boutique. <c>--shopcheck</c> keeps this in step.</summary>
    public static readonly ItemId[] SpecialClothes =
    {
        ItemId.RainbowCatEars, ItemId.NeonHeadset, ItemId.DiscoShades, ItemId.HoloMask, ItemId.RainbowTee,
        ItemId.DiscoTop, ItemId.GalaxyHoodie, ItemId.LavaTee, ItemId.GalaxyDress, ItemId.GlitchTee,
        ItemId.HoloSkirt, ItemId.RainbowStockings, ItemId.DiscoPlatforms, ItemId.NeonGloves,
    };

    /// <summary>Every other look in the wardrobe (ids 65-149).</summary>
    public static readonly ItemId[] PlainClothes = Enumerable.Range((int)ItemId.CatEarsBlack, ItemId.NeonGloves - ItemId.CatEarsBlack + 1)
        .Select(i => (ItemId)i).Where(id => !SpecialClothes.Contains(id)).ToArray();

    /// <summary>What only a PAUSA machine sells.</summary>
    public static readonly ItemId[] VendingOnly = { ItemId.IceTea, ItemId.Crisps, ItemId.GummyBears, ItemId.IsotonicDrink };

    /// <summary>Bought, never made (#270): the camera, the shotgun and its shells, the hiking pack, the Swiss army knife and the machine's snacks.</summary>
    public static readonly ItemId[] ShopOnly =
    {
        ItemId.Camera, ItemId.Shotgun, ItemId.Shells, ItemId.HikingPack, ItemId.SwissArmyKnife,
        ItemId.IceTea, ItemId.Crisps, ItemId.GummyBears, ItemId.IsotonicDrink, ItemId.Fertiliser,
    };

    private static ShopLine L(ItemId id, float chance, int min, int max) => new(id, chance, min, max);

    private static readonly Dictionary<ShopType, ShopLine[]> Catalogues = new()
    {
        [ShopType.Grocery] = new[]
        {
            L(ItemId.Bread, 0.95f, 4, 12), L(ItemId.CannedFood, 0.9f, 4, 15), L(ItemId.Apple, 0.85f, 6, 20),
            L(ItemId.Cheese, 0.8f, 2, 8), L(ItemId.Chocolate, 0.9f, 4, 12), L(ItemId.MineralWater, 0.9f, 4, 12),
            L(ItemId.WaterBottle, 0.85f, 3, 10), L(ItemId.EnergyBar, 0.6f, 2, 8), L(ItemId.Bandage, 0.3f, 1, 4),
            L(ItemId.RockSalt, 0.35f, 1, 4), L(ItemId.Firewood, 0.3f, 5, 15),
            L(ItemId.Potato, 0.6f, 4, 12), L(ItemId.Carrot, 0.5f, 4, 12), L(ItemId.Flour, 0.5f, 3, 10),
        },
        [ShopType.Kiosk] = new[]
        {
            L(ItemId.Chocolate, 0.95f, 3, 10), L(ItemId.EnergyBar, 0.8f, 2, 8), L(ItemId.MineralWater, 0.85f, 3, 8),
            L(ItemId.WaterBottle, 0.7f, 2, 6), L(ItemId.Bread, 0.4f, 1, 4), L(ItemId.Apple, 0.4f, 2, 6),
            L(ItemId.DuctTape, 0.25f, 1, 2),
        },
        [ShopType.Pharmacy] = new[]
        {
            L(ItemId.Bandage, 0.95f, 4, 12), L(ItemId.FirstAidKit, 0.8f, 1, 4), L(ItemId.WaterBottle, 0.6f, 2, 6),
            L(ItemId.EnergyBar, 0.5f, 2, 6), L(ItemId.MineralWater, 0.5f, 2, 6),
        },
        [ShopType.Hardware] = new[]
        {
            L(ItemId.Screws, 0.95f, 20, 60), L(ItemId.DuctTape, 0.9f, 2, 8), L(ItemId.Rope, 0.85f, 2, 6),
            L(ItemId.CopperWire, 0.75f, 3, 10), L(ItemId.WoodPlanks, 0.85f, 5, 20), L(ItemId.Glass, 0.5f, 2, 8),
            L(ItemId.Plastic, 0.5f, 5, 15), L(ItemId.Rubber, 0.4f, 2, 6), L(ItemId.FuelCan, 0.45f, 1, 3),
            L(ItemId.Firewood, 0.6f, 10, 25), L(ItemId.RockSalt, 0.6f, 2, 6), L(ItemId.SandBag, 0.5f, 2, 8),
            L(ItemId.Coal, 0.35f, 3, 10), L(ItemId.SwissArmyKnife, 0.5f, 1, 3),
        },
        [ShopType.Sport] = new[]
        {
            L(ItemId.BeltPouch, 0.8f, 1, 4), L(ItemId.Backpack, 0.7f, 1, 3), L(ItemId.HikingPack, 0.55f, 1, 2),
            L(ItemId.EnergyBar, 0.9f, 4, 12), L(ItemId.WaterBottle, 0.85f, 3, 8), L(ItemId.Binoculars, 0.4f, 1, 2),
            L(ItemId.SwissArmyKnife, 0.7f, 1, 3), L(ItemId.Rope, 0.5f, 1, 4),
            L(ItemId.JoggingShorts, 0.5f, 1, 3), L(ItemId.WhiteSneakers, 0.4f, 1, 2), L(ItemId.KneeSocks, 0.4f, 1, 3),
        },
        [ShopType.Boutique] = PlainClothes.Select(id => L(id, 0.10f, 1, 2))
            .Concat(SpecialClothes.Select(id => L(id, 0.025f, 1, 1)))
            .Append(L(ItemId.Handbag, 0.5f, 1, 2)).Append(L(ItemId.BeltPouch, 0.4f, 1, 2)).ToArray(),
        [ShopType.Electronics] = new[]
        {
            L(ItemId.Camera, 0.75f, 1, 3), L(ItemId.Gps, 0.6f, 1, 2), L(ItemId.Radio, 0.6f, 1, 2),
            L(ItemId.Electronics, 0.9f, 3, 10), L(ItemId.CopperWire, 0.7f, 3, 10), L(ItemId.CarBattery, 0.3f, 1, 2),
        },
        [ShopType.GunShop] = new[]
        {
            L(ItemId.Shotgun, 0.9f, 1, 3), L(ItemId.Shells, 1.0f, 50, 150), L(ItemId.Binoculars, 0.6f, 1, 2),
            L(ItemId.SwissArmyKnife, 0.6f, 1, 3), L(ItemId.Backpack, 0.3f, 1, 2),
        },
        [ShopType.FarmCoop] = new[]
        {
            L(ItemId.WheatSeed, 0.9f, 3, 12), L(ItemId.BarleySeed, 0.8f, 3, 10), L(ItemId.MaizeSeed, 0.8f, 3, 10),
            L(ItemId.SeedPotato, 0.85f, 3, 12), L(ItemId.RapeSeed, 0.6f, 2, 8), L(ItemId.SunflowerSeed, 0.6f, 2, 8),
            L(ItemId.SugarBeetSeed, 0.6f, 3, 10), L(ItemId.VegetableSeeds, 0.85f, 3, 12), L(ItemId.PeaSeed, 0.6f, 2, 8),
            L(ItemId.Hoe, 0.85f, 1, 3), L(ItemId.Fertiliser, 0.9f, 5, 20),
            L(ItemId.Rope, 0.4f, 1, 4), L(ItemId.FuelCan, 0.35f, 1, 3),
        },
        [ShopType.Garage] = new[]
        {
            L(ItemId.Tyre, 0.85f, 2, 8), L(ItemId.CarBattery, 0.7f, 1, 4), L(ItemId.FuelCan, 0.9f, 2, 6),
            L(ItemId.EnginePart, 0.5f, 1, 3), L(ItemId.BikeChain, 0.6f, 1, 4), L(ItemId.Rubber, 0.5f, 2, 8),
            L(ItemId.Screws, 0.6f, 10, 40),
        },
    };

    /// <summary>What a PAUSA machine's spirals are filled from: each slot is one item, the exclusives most often.</summary>
    public static readonly (ItemId Id, float Weight)[] VendingTable =
    {
        (ItemId.IceTea, 16), (ItemId.Crisps, 16), (ItemId.GummyBears, 14), (ItemId.IsotonicDrink, 12),
        (ItemId.Chocolate, 14), (ItemId.EnergyBar, 10), (ItemId.MineralWater, 10), (ItemId.WaterBottle, 8),
    };

    /// <summary>A shop type's catalogue, in slot order; a machine's possible items (one line each, chance per slot).</summary>
    public static IReadOnlyList<ShopLine> Catalogue(ShopType type)
    {
        if (type == ShopType.Vending)
        {
            float total = VendingTable.Sum(v => v.Weight);
            return VendingTable.Select(v => L(v.Id, v.Weight / total, VendingMin, VendingMax)).ToArray();
        }
        return Catalogues.TryGetValue(type, out var lines) ? lines : Array.Empty<ShopLine>();
    }

    /// <summary>Every item some shop or machine can sell.</summary>
    public static IEnumerable<ItemId> Sold() =>
        Enum.GetValues<ShopType>().SelectMany(t => Catalogue(t).Select(l => l.Id)).Distinct();

    // ---- buying back ----------------------------------------------------------------------------

    /// <summary>The categories a shop buys from players. A machine buys nothing.</summary>
    public static bool Buys(ShopType type, ItemCategory category) => type switch
    {
        ShopType.Grocery or ShopType.Kiosk => category is ItemCategory.Food or ItemCategory.Water,
        ShopType.Pharmacy => category == ItemCategory.Medical,
        ShopType.Hardware => category is ItemCategory.Scrap or ItemCategory.Mineral,
        ShopType.Sport or ShopType.GunShop or ShopType.Electronics => category == ItemCategory.Gear,
        ShopType.Boutique => category is ItemCategory.Clothing or ItemCategory.Cosmetic,
        ShopType.Garage => category == ItemCategory.Part,
        ShopType.FarmCoop => category == ItemCategory.Produce,
        _ => false,
    };

    /// <summary>What a shop pays for one: 35 % of the value, rounded down; 0 means it is not worth buying.</summary>
    public static int SellPrice(float value) => value <= 0 ? 0 : (int)Math.Floor(value * SellShare);

    /// <summary>
    /// What a farm co-op pays for a delivered load (#494, <c>Farming.FarmMarket.Deliver</c>): the full
    /// producer price (<see cref="ItemDef.Value"/>) of every unit, rounded down for the load. A load is a
    /// weighed delivery to a buyer, not a shop-counter sale at <see cref="SellShare"/>; only what the
    /// co-op buys (<see cref="Buys"/>) is taken, 0 otherwise.
    /// </summary>
    public static long DeliveryPrice(ItemCategory category, float value, int count) =>
        count <= 0 || value <= 0 || !Buys(ShopType.FarmCoop, category) ? 0 : (long)Math.Floor(value * (double)count);

    // ---- prices and payment ---------------------------------------------------------------------

    /// <summary>A shop's (or one machine's) mark-up over the value: 1.1 to 1.4, fixed by its key.</summary>
    public static double Markup(string key) => 1.1 + 0.3 * Fnv.Unit(key + "|markup");

    /// <summary>The price of one, in whole francs, never under 1.</summary>
    public static int Price(float value, double markup) => Math.Max(1, (int)Math.Round(value * markup, MidpointRounding.AwayFromZero));

    /// <summary>
    /// How a line may be paid: a machine, a kiosk, a grocery or anything under <see cref="CardFrom"/>
    /// takes cash only; from there on the other shops take the card too; the shotgun goes on the
    /// card only (a paper trail).
    /// </summary>
    public static Payment PaymentFor(ShopType type, ItemId id, int price)
    {
        if (id == ItemId.Shotgun) return Payment.Card;
        if (type is ShopType.Vending or ShopType.Kiosk or ShopType.Grocery || price < CardFrom) return Payment.Cash;
        return Payment.CashOrCard;
    }

    public static bool Accepts(Payment pay, PayWith with) => pay switch
    {
        Payment.Cash => with == PayWith.Cash,
        Payment.Card => with == PayWith.Card,
        _ => true,
    };

    // ---- the stock ------------------------------------------------------------------------------

    /// <summary>The seed of one counter or machine in one restock period.</summary>
    public static Random RngFor(string key, int furniture, long epoch) => new(Fnv.Hash($"{key}|{furniture}|{epoch}|shop"));

    /// <summary>The mark-up key: a shop prices its whole counter alike, each machine on its own.</summary>
    public static string PriceKey(string key, int furniture, ShopType type) => type == ShopType.Vending ? $"{key}|{furniture}" : key;

    /// <summary>
    /// What a counter or a machine holds this period. A shop: one line per catalogue line, slot =
    /// line, stock 0 when not carried (every line draws the same numbers either way, so slots never
    /// shift). The shotgun is never in stock out of the hunting season. A machine: 24 slots
    /// (A1..D6), each one item, 3-8 of it.
    /// </summary>
    public static List<StockLine> Stock(string key, int furniture, long epoch, ShopType type, Func<ItemId, float> value, bool huntingSeason)
    {
        var rng = RngFor(key, furniture, epoch);
        double markup = Markup(PriceKey(key, furniture, type));
        var result = new List<StockLine>();
        if (type == ShopType.Vending)
        {
            float total = VendingTable.Sum(v => v.Weight);
            for (int slot = 0; slot < VendingSlots; slot++)
            {
                double pick = rng.NextDouble() * total;
                var id = VendingTable[^1].Id;
                foreach (var (vid, w) in VendingTable)
                {
                    pick -= w;
                    if (pick < 0) { id = vid; break; }
                }
                int count = rng.Next(VendingMin, VendingMax + 1);
                int price = Price(value(id), markup);
                result.Add(new StockLine(slot, id, count, price, PaymentFor(type, id, price)));
            }
            return result;
        }
        var lines = Catalogue(type);
        for (int slot = 0; slot < lines.Count; slot++)
        {
            var line = lines[slot];
            bool carried = rng.NextDouble() < line.Chance;
            int count = rng.Next(line.Min, line.Max + 1);
            if (!carried || (line.Id == ItemId.Shotgun && !huntingSeason)) count = 0;
            int price = Price(value(line.Id), markup);
            result.Add(new StockLine(slot, line.Id, count, price, PaymentFor(type, line.Id, price)));
        }
        return result;
    }

    /// <summary>A machine that is "Hors service" for the whole restock period: it takes no coins.</summary>
    public static bool OutOfOrder(string key, int furniture, long epoch) =>
        Fnv.Unit($"{key}|{furniture}|{epoch}|hs") < OutOfOrderChance;

    /// <summary>
    /// The exact probability that one counter of this type (or one machine) has at least one
    /// <paramref name="item"/> when restocked: the closed form of <see cref="Stock"/>, for the smart
    /// binoculars. A machine that is out of order sells nothing, so it counts as empty.
    /// </summary>
    public static double Chance(ShopType type, ItemId item, bool huntingSeason)
    {
        if (type == ShopType.Vending)
        {
            float total = VendingTable.Sum(v => v.Weight);
            double p = VendingTable.Where(v => v.Id == item).Sum(v => (double)v.Weight) / total;
            return (1 - OutOfOrderChance) * (1 - Math.Pow(1 - p, VendingSlots));
        }
        if (item == ItemId.Shotgun && !huntingSeason) return 0;
        double none = 1;
        foreach (var line in Catalogue(type))
            if (line.Id == item && line.Max > 0) none *= 1 - Math.Clamp(line.Chance, 0f, 1f);
        return 1 - none;
    }

    // ---- names ----------------------------------------------------------------------------------

    /// <summary>"A1".."D6": row letter, column number, as typed on the keypad.</summary>
    public static string SlotName(int slot) => $"{(char)('A' + slot / VendingCols)}{slot % VendingCols + 1}";

    /// <summary>The slot a keypad code names, or -1.</summary>
    public static int ParseSlot(string code)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length != 2) return -1;
        int row = code[0] - 'A', col = code[1] - '1';
        return row is >= 0 and < VendingRows && col is >= 0 and < VendingCols ? row * VendingCols + col : -1;
    }

    /// <summary>The name on the door sign and the panel's title.</summary>
    public static string Name(ShopType type) => type switch
    {
        ShopType.Grocery => "Grocery",
        ShopType.Hardware => "Hardware",
        ShopType.Kiosk => "Kiosk",
        ShopType.Pharmacy => "Pharmacy",
        ShopType.Sport => "Sport",
        ShopType.Boutique => "Boutique",
        ShopType.Electronics => "Electronics",
        ShopType.GunShop => "Gun shop",
        ShopType.Garage => "Garage",
        ShopType.FarmCoop => "Farm co-op",
        ShopType.Vending => "PAUSA",
        _ => "",
    };
}
