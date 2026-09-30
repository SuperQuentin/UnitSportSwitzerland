using Godot;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// <c>--lootchancecheck</c>: <see cref="LootTables.Chance"/> (exact arithmetic) against a
/// Monte-Carlo run of <see cref="LootTables.Roll"/> for a spread of kinds, containers, items,
/// abundances and a running occasion, plus <see cref="LootTables.BuildingChance"/> on a synthetic
/// building. Passes when every pair is within 2 percentage points. Prints the table.
/// </summary>
public static class LootChanceCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--lootchancecheck") >= 0;

    private const int Samples = 200_000;
    private const double Tolerance = 0.02;

    public static int Run()
    {
        bool ok = true;
        void Row(string label, double analytic, double sampled)
        {
            bool pass = Math.Abs(analytic - sampled) <= Tolerance;
            ok &= pass;
            GD.Print($"[lootchance] {(pass ? "ok  " : "FAIL")} {label,-46}{analytic * 100,8:F2}% {sampled * 100,8:F2}%  {(analytic - sampled) * 100,+6:F2}pp");
        }

        GD.Print($"[lootchance] {"case",-51}{"analytic",8} {"sampled",8}");
        var cases = new (BuildingKind Kind, FurnitureType Type, float Abundance, ItemId Item)[]
        {
            (BuildingKind.House, FurnitureType.Fridge, 1f, ItemId.Bread),
            (BuildingKind.House, FurnitureType.Fridge, 0.5f, ItemId.Apple),
            (BuildingKind.Apartment, FurnitureType.Fridge, 1f, ItemId.Bread),          // RollFactor 0.8: random rounding
            (BuildingKind.House, FurnitureType.Wardrobe, 1f, ItemId.Cloth),
            (BuildingKind.House, FurnitureType.Nightstand, 0.6f, ItemId.Electronics),
            (BuildingKind.Commercial, FurnitureType.ShopCounter, 1f, ItemId.Chocolate),
            (BuildingKind.Industrial, FurnitureType.Crate, 1f, ItemId.ScrapMetal),
            (BuildingKind.Industrial, FurnitureType.Workbench, 0.7f, ItemId.CopperWire), // in two pools
            (BuildingKind.Garage, FurnitureType.Car, 1f, ItemId.Tyre),
            (BuildingKind.Agricultural, FurnitureType.HayBale, 1f, ItemId.Firewood),
            (BuildingKind.Civic, FurnitureType.Shelf, 0.8f, ItemId.Bandage),
            (BuildingKind.UnderConstruction, FurnitureType.Crate, 1f, ItemId.Stone),
            (BuildingKind.Sacral, FurnitureType.Fridge, 1f, ItemId.Bread),             // 0: a church stocks no food
            (BuildingKind.House, FurnitureType.Desk, 0.5f, ItemId.SmartBinoculars),
            (BuildingKind.House, FurnitureType.Shelf, 1f, ItemId.SmartBinoculars),
            (BuildingKind.Commercial, FurnitureType.ShopCounter, 1f, ItemId.Francs),
            (BuildingKind.House, FurnitureType.Nightstand, 0.6f, ItemId.Francs),
            (BuildingKind.Garage, FurnitureType.Car, 1f, ItemId.Francs),
            (BuildingKind.Sacral, FurnitureType.Altar, 1f, ItemId.Francs),
        };
        foreach (var c in cases)
            Row($"{c.Kind} {c.Type} ab{c.Abundance:F1} {c.Item}",
                LootTables.Chance(c.Kind, c.Type, c.Abundance, c.Item), Sample(c.Kind, c.Type, c.Abundance, c.Item));

        // a running occasion adds one more roll, drawn after the container is found non-empty
        var saved = LootTables.Seasonal;
        LootTables.Seasonal = _ => (0.3f, new[] { ItemId.Candy, ItemId.Bread });
        foreach (var item in new[] { ItemId.Candy, ItemId.Bread })
            Row($"seasonal House Fridge ab0.8 {item}",
                LootTables.Chance(BuildingKind.House, FurnitureType.Fridge, 0.8f, item),
                Sample(BuildingKind.House, FurnitureType.Fridge, 0.8f, item));
        LootTables.Seasonal = saved;

        // a whole building: 1 - prod(1 - p_i) against the contents of its containers, restock by restock
        var layout = new InteriorLayout { Key = "0_0_0", Kind = BuildingKind.House, Floors = { new FloorPlan(), new FloorPlan() } };
        foreach (var t in new[] { FurnitureType.Fridge, FurnitureType.Wardrobe, FurnitureType.Wardrobe, FurnitureType.Nightstand,
                     FurnitureType.Nightstand, FurnitureType.Desk, FurnitureType.Shelf, FurnitureType.Counter, FurnitureType.Bed, FurnitureType.Sofa })
            layout.Furniture.Add(new FurniturePlan { Type = t });
        foreach (var item in new[] { ItemId.Bread, ItemId.Bandage, ItemId.Electronics, ItemId.Francs, ItemId.SmartBinoculars })
        {
            int hit = 0;
            const int epochs = 100_000;
            for (int e = 0; e < epochs; e++)
            {
                bool any = false;
                for (int f = 0; f < layout.Furniture.Count && !any; f++)
                    any = LootTables.ContentsOf(layout, f, e).Any(s => s.Id == item);
                if (any) hit++;
            }
            var odds = LootTables.BuildingChance(layout, item);
            Row($"building (8 lootable, 2 floors) {item}", odds.Any, (double)hit / epochs);
            GD.Print($"[lootchance]      best container: {(odds.Best is { } b ? $"{LootTables.Describe(b.Type)} x{b.Count} {b.Each * 100:F1}% each, {b.Group * 100:F1}% any" : "none")}");
        }

        // the list a player picks from: every pooled item, francs included, all with a name
        var targets = LootTables.Targets();
        bool listOk = targets.Contains(ItemId.Francs) && targets.Contains(ItemId.Bread) && targets.Contains(ItemId.SmartBinoculars)
                      && targets.All(id => ItemDefs.Get(id) != null);
        ok &= listOk;
        GD.Print($"[lootchance] {(listOk ? "ok  " : "FAIL")} {targets.Count} selectable targets");
        GD.Print(ok ? "[lootchance] RESULT pass" : "[lootchance] RESULT FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>Scatters consecutive seeds: System.Random's early draws are correlated across neighbouring seeds.</summary>
    private static int Mix(int i)
    {
        uint x = (uint)i * 0x9E3779B9u;
        x ^= x >> 16; x *= 0x85EBCA6Bu; x ^= x >> 13; x *= 0xC2B2AE35u; x ^= x >> 16;
        return (int)(x & 0x7FFFFFFF);
    }

    private static double Sample(BuildingKind kind, FurnitureType type, float abundance, ItemId item)
    {
        int hit = 0;
        for (int i = 0; i < Samples; i++)
            if (LootTables.Roll(kind, type, new Random(Mix(i)), abundance).Any(s => s.Id == item)) hit++;
        return (double)hit / Samples;
    }
}
