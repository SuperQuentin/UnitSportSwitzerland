using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Format;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md). Item values come in
// as numbers (the game passes ItemDefs' Value), like ShopTables.

namespace UnitSport.Farming;

/// <summary>
/// The farm economy's clock (#494, docs/notes/farming/selling.md): a <b>farm day</b> is the default
/// day length (24 min of server time, <c>GameSettings.DayLengthMinutes</c>) whatever the sky clock
/// does (it may be stopped), and a <b>farm week</b> is seven of them. Co-op wish lists and orders
/// change with the week; contract deadlines count farm days. The season is the calendar month the
/// fields show (<see cref="FarmRules.MonthFromArgs"/>).
/// </summary>
public static class FarmCalendar
{
    public const int DaySeconds = 24 * 60;
    public const int WeekSeconds = 7 * DaySeconds;

    /// <summary>The farm week a server Unix time falls in.</summary>
    public static long Week(double unix) => (long)Math.Floor(unix / WeekSeconds);

    /// <summary>When a farm week ends (server Unix seconds).</summary>
    public static double WeekEnds(long week) => (week + 1) * (double)WeekSeconds;

    /// <summary>"2 d 5 h" in farm days and farm hours (a farm hour is a minute of server time).</summary>
    public static string Left(double seconds)
    {
        if (seconds <= 0) return "due";
        int hours = (int)Math.Ceiling(seconds / (DaySeconds / 24.0));
        int d = hours / 24, h = hours % 24;
        return d > 0 ? $"{d} d {h} h" : $"{h} h";
    }
}

/// <summary>One crop a co-op wants this week, and how much more it pays for it (1.3 = +30 %).</summary>
public readonly record struct FarmWish(ItemId Item, double Bonus);

/// <summary>
/// Every farm-produce price in one place (#494): the co-op's counter, a delivered load (co-op or
/// specialty buyer), the farm stand. A price is the item's value (the producer price of one) times
/// <see cref="Season"/> (harvest glut, stored goods dear in spring) times the co-op's
/// <see cref="Wishes"/> bonus for the week, times a buyer's premium. <c>Loot.ShopTables</c>
/// (<c>DeliveryPrice</c>, <c>CounterPrice</c>) and the server's handlers all call these, so the
/// panel, the prompt and the payment agree.
/// </summary>
public static class FarmPrices
{
    /// <summary>The counter pays this share of the value (<c>ShopTables.SellShare</c>).</summary>
    public const double CounterShare = 0.35;
    /// <summary>A farm stand asks this share of the value: between the counter and a delivered load.</summary>
    public const double StandShare = 0.7;
    /// <summary>A crop at harvest time: everyone sells, the price sags.</summary>
    public const double GlutFactor = 0.8;
    /// <summary>A stored crop in spring (March-May), before the new harvest: dearer.</summary>
    public const double SpringFactor = 1.2;
    /// <summary>A wished-for crop's bonus, from .. to (steps of 0.05).</summary>
    public const double WishMin = 1.3, WishMax = 1.6;

    /// <summary>The harvests a co-op may wish for or order, in a fixed order (the seed's index).</summary>
    public static readonly ItemId[] Harvests =
    {
        ItemId.Wheat, ItemId.Barley, ItemId.Maize, ItemId.Potato, ItemId.Rapeseed,
        ItemId.SunflowerSeeds, ItemId.SugarBeet, ItemId.Carrot, ItemId.HayBale, ItemId.Peas,
    };

    /// <summary>The crop a harvest comes from (hay: meadow), None for anything else.</summary>
    public static CropKind CropOf(ItemId item) => item switch
    {
        ItemId.Wheat => CropKind.Wheat,
        ItemId.Barley => CropKind.Barley,
        ItemId.Maize => CropKind.Maize,
        ItemId.Potato => CropKind.Potato,
        ItemId.Rapeseed => CropKind.Rapeseed,
        ItemId.SunflowerSeeds => CropKind.Sunflower,
        ItemId.SugarBeet => CropKind.SugarBeet,
        ItemId.Carrot => CropKind.Vegetables,
        ItemId.Peas => CropKind.Legumes,
        ItemId.HayBale => CropKind.Meadow,
        _ => CropKind.None,
    };

    /// <summary>Whether a harvest keeps over winter (all but carrots, sold fresh): dearer in spring.</summary>
    public static bool Stored(ItemId item) => item != ItemId.Carrot && CropOf(item) != CropKind.None;

    /// <summary>Whether <paramref name="month"/> is when this harvest comes in (hay: June to August).</summary>
    public static bool HarvestMonth(ItemId item, int month)
    {
        var crop = CropOf(item);
        if (crop == CropKind.None) return false;
        if (item == ItemId.HayBale) return month is >= 6 and <= 8;
        return FarmTables.NaturalStage(crop, month) == FieldStage.Ripe;
    }

    /// <summary>The season's factor on a harvest's price: <see cref="GlutFactor"/> in its harvest months, <see cref="SpringFactor"/> in spring if it keeps, else 1. Products: 1.</summary>
    public static double Season(ItemId item, int month)
    {
        if (HarvestMonth(item, month)) return GlutFactor;
        if (month is >= 3 and <= 5 && Stored(item)) return SpringFactor;
        return 1.0;
    }

    /// <summary>What the season says, for the panel ("harvest glut", "spring: stores low"), or "".</summary>
    public static string SeasonNote(ItemId item, int month) =>
        HarvestMonth(item, month) ? "harvest glut" : month is >= 3 and <= 5 && Stored(item) ? "spring" : "";

    /// <summary>
    /// The 1-2 harvests a co-op pays a bonus for this farm week, each <see cref="WishMin"/>..<see cref="WishMax"/>:
    /// keyed by the co-op's building and the week, like the shop mark-up, so every peer works out the same.
    /// </summary>
    public static FarmWish[] Wishes(string coop, long week)
    {
        int count = Fnv.Unit($"{coop}|{week}|wishes") < 0.5 ? 1 : 2;
        var result = new FarmWish[count];
        int first = (int)(Fnv.Unit($"{coop}|{week}|wish0") * Harvests.Length) % Harvests.Length;
        int second = (first + 1 + (int)(Fnv.Unit($"{coop}|{week}|wish1") * (Harvests.Length - 1))) % Harvests.Length;
        for (int i = 0; i < count; i++)
        {
            double steps = Math.Round((WishMax - WishMin) / 0.05);
            double bonus = WishMin + 0.05 * Math.Floor(Fnv.Unit($"{coop}|{week}|bonus{i}") * (steps + 1));
            result[i] = new FarmWish(Harvests[i == 0 ? first : second], Math.Round(Math.Min(bonus, WishMax), 2));
        }
        return result;
    }

    /// <summary>The co-op's bonus for an item this week (1 when it is not wanted, or no co-op).</summary>
    public static double WishBonus(string? coop, long week, ItemId item)
    {
        if (string.IsNullOrEmpty(coop)) return 1.0;
        foreach (var w in Wishes(coop, week)) if (w.Item == item) return w.Bonus;
        return 1.0;
    }

    /// <summary>Season × the co-op's wish (× nothing else): the factor on a harvest's value, 1 for anything else.</summary>
    public static double Factor(ItemId item, int month, string? coop, long week) =>
        CropOf(item) == CropKind.None ? 1.0 : Season(item, month) * WishBonus(coop, week, item);

    /// <summary>
    /// A delivered load (a machine's tank, a tipper, or sacks at a specialty buyer's weighbridge):
    /// the full value per unit × <see cref="Factor"/> × <paramref name="premium"/> (a specialty buyer's), floored for the load.
    /// </summary>
    public static long Delivery(float value, ItemId item, int count, int month, string? coop, long week, double premium = 1.0) =>
        count <= 0 || value <= 0 ? 0 : (long)Math.Floor(value * Factor(item, month, coop, week) * premium * count);

    /// <summary>What a co-op's counter pays for one: <see cref="CounterShare"/> of the value × <see cref="Factor"/>, floored.</summary>
    public static int Counter(float value, ItemId item, int month, string? coop, long week) =>
        value <= 0 ? 0 : (int)Math.Floor(value * CounterShare * Factor(item, month, coop, week) + 1e-9);

    /// <summary>A farm stand's price for one: <see cref="StandShare"/> of the value × the season, at least 1 franc.</summary>
    public static int Stand(float value, ItemId item, int month) =>
        value <= 0 ? 0 : Math.Max(1, (int)Math.Round(value * StandShare * Season(item, month), MidpointRounding.AwayFromZero));
}
