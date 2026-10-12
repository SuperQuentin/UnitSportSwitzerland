using Godot;
using UnitSport.Crafting;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// <c>--shopcheck</c> (#273): what the unit tests cannot see, because it needs the game's own rows.
/// Every catalogue item is a real item with a value and a price above what a shop pays for it (no
/// buy-low-sell-high loop at one counter); the boutique's rare clothes are exactly the wardrobe's
/// finishes; the machine's four snacks and the Swiss army knife are found nowhere and made nowhere;
/// the shotgun goes on the card. Then synthetic buildings through the real generator: every shop
/// gets its counter, a garage shop too, and PAUSA machines stand in about the share of lobbies the
/// tables say. Builds no world; prints a RESULT line and exits 0 or 1.
/// </summary>
public static class ShopCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--shopcheck") >= 0;

    public static int Run()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[shopcheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        foreach (var type in Enum.GetValues<ShopType>().Where(t => t != ShopType.None))
            foreach (var line in ShopTables.Catalogue(type))
            {
                var def = ItemDefs.Get(line.Id);
                if (def == null || def.Value <= 0) { Expect(false, $"{type} sells {line.Id}, which has no value"); continue; }
                int dearest = ShopTables.Price(def.Value, 1.1);
                if (ShopTables.SellPrice(def.Value) >= dearest) Expect(false, $"{line.Id}: sold back for {ShopTables.SellPrice(def.Value)}, bought from {dearest}");
            }
        Expect(failures == 0, "every catalogue line is a real item, worth more bought than sold back");

        var specials = Avatar.Garments.All.Where(g => g.IsSpecial && g.Item <= ItemId.NeonGloves).Select(g => g.Item).OrderBy(i => i).ToArray();
        Expect(specials.SequenceEqual(ShopTables.SpecialClothes.OrderBy(i => i)),
            $"the boutique's rare clothes are the wardrobe's finishes ({specials.Length})");

        var lootable = LootTables.Targets().ToHashSet();
        foreach (var id in ShopTables.VendingOnly.Append(ItemId.SwissArmyKnife).Append(ItemId.Camera))
            Expect(!lootable.Contains(id) && Recipes.NeverCrafted.Contains(id), $"{id} is only bought: in no loot table, never crafted");
        foreach (var id in ShopTables.VendingOnly)
            Expect(ItemDefs.Get(id) is { Use: ItemUse.Consume } def && def.Category is ItemCategory.Food or ItemCategory.Water,
                $"{id} is eaten or drunk");
        var gun = ItemDefs.Get(ItemId.Shotgun)!;
        Expect(ShopTables.PaymentFor(ShopType.GunShop, ItemId.Shotgun, ShopTables.Price(gun.Value, 1.25)) == Payment.Card, "the shotgun goes on the card only");
        var months = Enumerable.Range(1, 12).Where(ShopService.HuntingSeasonIn).ToArray();
        GD.Print($"[shopcheck] hunting season (the shotgun on sale): months {string.Join(",", months)}");
        Expect(months.Length is > 0 and < 12, "the hunting season is part of the year");

        // the generator: synthetic buildings of every kind that sells
        int shops = 0, counters = 0, garages = 0, garageCounters = 0;
        var machines = new Dictionary<BuildingKind, (int Of, int With)>();
        for (int i = 0; i < 400; i++)
            foreach (var kind in new[] { BuildingKind.Commercial, BuildingKind.Civic, BuildingKind.Industrial, BuildingKind.Garage })
            {
                var (w, d) = kind == BuildingKind.Garage ? (7f + i % 5, 8f) : (11f + i % 9, 12f + i % 7);
                var layout = Synthetic(new BuildingKey(2580, 1110, i), kind, w, d, rural: i % 2 == 0);
                if (layout.Shop != ShopType.None)
                {
                    bool counter = layout.Furniture.Any(f => f.Type == FurnitureType.ShopCounter && f.Floor == layout.Below);
                    if (kind == BuildingKind.Garage) { garages++; garageCounters += counter ? 1 : 0; }
                    else { shops++; counters += counter ? 1 : 0; }
                }
                var m = machines.GetValueOrDefault(kind);
                machines[kind] = (m.Of + 1, m.With + (layout.Furniture.Any(f => f.Type == FurnitureType.VendingMachine) ? 1 : 0));
            }
        Expect(shops > 300 && counters == shops, $"every shop has its counter ({counters}/{shops})");
        Expect(garages > 50 && garageCounters >= garages * 0.95, $"a garage shop has a counter too ({garageCounters}/{garages})");
        foreach (var (kind, want) in new[] { (BuildingKind.Civic, 0.35), (BuildingKind.Commercial, 0.20), (BuildingKind.Industrial, 0.15) })
        {
            var (of, with) = machines[kind];
            double share = with / (double)of;
            // placed in the space a room has left over, so a little under the dice's share
            Expect(share > want * 0.6 && share < want + 0.06, $"{kind}: a PAUSA machine in {share:P0} (dice {want:P0})");
        }

        // the farm co-op (#494): a big barn or a village shop on a rural tile, counter and all; never in town
        int coops = 0, coopCounters = 0, townCoops = 0;
        for (int i = 0; i < 400; i++)
            foreach (var rural in new[] { true, false })
            {
                var layout = Synthetic(new BuildingKey(2580, 1110, 1000 + i), BuildingKind.Agricultural, 14f + i % 10, 20f + i % 8, rural);
                if (layout.Shop != ShopType.FarmCoop) continue;
                if (!rural) townCoops++;
                else { coops++; coopCounters += layout.Furniture.Any(f => f.Type == FurnitureType.ShopCounter && f.Floor == layout.Below) ? 1 : 0; }
            }
        Expect(coops > 20 && townCoops == 0, $"big barns are farm co-ops on rural tiles only ({coops} of 400, {townCoops} in town)");
        Expect(coopCounters == coops, $"every farm co-op has its counter ({coopCounters}/{coops})");
        foreach (var def in ItemDefs.All.Where(d => d.Category == ItemCategory.Produce))
            Expect(ShopTables.Buys(ShopType.FarmCoop, def.Category) && ShopTables.SellPrice(def.Value) > 0
                   && ShopTables.DeliveryPrice(def.Category, def.Value, 10) > 10 * ShopTables.SellPrice(def.Value),
                $"{def.Name}: the co-op buys it ({ShopTables.SellPrice(def.Value)} CHF at the counter, {ShopTables.DeliveryPrice(def.Category, def.Value, 10)} for ten delivered)");

        GD.Print($"[shopcheck] RESULT: {(failures == 0 ? "ok" : $"FAILED ({failures})")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>A plain box of a building, its door on the front: enough for the generator.</summary>
    private static InteriorLayout Synthetic(BuildingKey key, BuildingKind kind, float width, float depth, bool rural)
    {
        var door = new DoorSpot(key.Index, new Vector3(0, 0, -depth / 2), new Vector3(0, 0, -1),
            BuildingFootprint.DoorWidthFor(kind), BuildingFootprint.DoorHeightFor(kind)) { Kind = kind };
        var fp = new Footprint(key, kind, Vector2.Zero, new Vector2(1, 0), width, depth, door);
        var b = new Building { Kind = kind, MinY = 0, MaxY = kind == BuildingKind.Garage ? 3.3f : 9f, Floors = (byte)(kind == BuildingKind.Garage ? 1 : 2), Triangles = new float[9] };
        return InteriorGenerator.Generate(fp, b, rural);
    }
}
