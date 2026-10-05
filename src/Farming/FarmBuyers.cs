using UnitSport.Items;

// Plain C#, no Godot: linked into the unit tests.

namespace UnitSport.Farming;

/// <summary>
/// A bulk buyer at a real place (#494): a sugar factory, a grain mill or an oil mill that pays more
/// than the co-op for one kind of goods, so it is worth driving the tractor and tipper there.
/// <see cref="E"/>/<see cref="N"/> are LV95 (EPSG:2056), converted from the OpenStreetMap feature
/// named in <see cref="Source"/> (swisstopo's approximate WGS84 -> LV95 formulas, about 1 m).
/// </summary>
/// <param name="Key">Stable id: the contracts and the logs name it.</param>
/// <param name="Reach">How far from the point a load or a sack counts as delivered, m (the yard).</param>
/// <param name="Premium">The factor over the full value (the co-op pays 1).</param>
public sealed record FarmBuyer(string Key, string Name, string Place, double E, double N, float Reach,
    ItemId[] Goods, double Premium, string Source)
{
    public bool Buys(ItemId item) => Array.IndexOf(Goods, item) >= 0;

    /// <summary>"sugar beet", "wheat, barley or maize": what it takes, for prompts.</summary>
    public string GoodsText(Func<ItemId, string> name) => Goods.Length switch
    {
        1 => name(Goods[0]).ToLowerInvariant(),
        _ => string.Join(", ", Goods[..^1].Select(g => name(g).ToLowerInvariant())) + " or " + name(Goods[^1]).ToLowerInvariant(),
    };
}

/// <summary>
/// The specialty buyers placed from data (docs/notes/farming/selling.md lists the sources and the
/// sites left out). Only sites whose position the OSM extract in <c>ressources/data/osm</c> names
/// are here; none is invented.
/// </summary>
public static class FarmBuyers
{
    public static readonly FarmBuyer[] All =
    {
        // Schweizer Zucker AG: the two Swiss sugar factories. Beet only, by the load or the sack.
        new("zucker-aarberg", "Schweizer Zucker AG", "Aarberg BE", 2587711.0, 1209764.5, 150f,
            new[] { ItemId.SugarBeet }, 1.3, "OSM relation 17792349 \"Zuckerfabrik Aarberg\" (centroid 7.276937 E, 47.038800 N)"),
        new("zucker-frauenfeld", "Schweizer Zucker AG", "Frauenfeld TG", 2707961.5, 1268240.9, 150f,
            new[] { ItemId.SugarBeet }, 1.3, "OSM way 243688664 \"Rübenlager\", operator Zuckerfabrik Frauenfeld (centroid 8.873056 E, 47.556006 N)"),
        // grain mills
        new("swissmill-zuerich", "Swissmill", "Zürich", 2682140.2, 1249358.5, 60f,
            new[] { ItemId.Wheat, ItemId.Barley, ItemId.Maize }, 1.15, "OSM way 554219427 \"Swissmill\", industrial=grinding_mill, operator Coop (centroid 8.526553 E, 47.389933 N)"),
        new("emmental-muehle", "Emmental-Mühle AG", "Schüpbach BE", 2622853.6, 1197438.4, 60f,
            new[] { ItemId.Wheat, ItemId.Barley }, 1.1, "OSM way 245720805 \"Emmental-Mühle AG\", industrial=grinding_mill (centroid 7.738710 E, 46.927650 N)"),
        // oil mills
        new("sabo-horn", "Oleificio SABO", "Horn TG", 2751595.8, 1262754.9, 80f,
            new[] { ItemId.Rapeseed, ItemId.SunflowerSeeds }, 1.2, "OSM node 8107445017 \"Oleificio Sabo\" (9.450720 E, 47.498059 N)"),
        new("moulin-severy", "Moulin et Huilerie de Sévery", "Sévery VD", 2524164.2, 1157924.2, 60f,
            new[] { ItemId.Rapeseed, ItemId.SunflowerSeeds }, 1.15, "OSM way 296573291, product=oil (centroid 6.449477 E, 46.568282 N)"),
    };

    /// <summary>The buyer whose yard holds an LV95 point (the nearest if two did), or null.</summary>
    public static FarmBuyer? At(double e, double n, float slack = 0f)
    {
        FarmBuyer? best = null;
        double bestD = double.MaxValue;
        foreach (var b in All)
        {
            double d = Math.Sqrt((b.E - e) * (b.E - e) + (b.N - n) * (b.N - n));
            if (d <= b.Reach + slack && d < bestD) { best = b; bestD = d; }
        }
        return best;
    }

    public static FarmBuyer? ByKey(string key) => All.FirstOrDefault(b => b.Key == key);
}
