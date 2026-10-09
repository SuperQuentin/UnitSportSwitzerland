using UnitSport.Items;

// Plain C#, no Godot: linked into the unit tests.

namespace UnitSport.Farming;

/// <summary>One crate on a farm stand: what it holds and how many.</summary>
public sealed class StandSlot
{
    public ItemId Item { get; set; }
    public int Count { get; set; }
    /// <summary>Expected sales not yet whole (the fraction carried to the next tick).</summary>
    public double Due { get; set; }
}

/// <summary>
/// A farm stand's state as the server keeps and saves it (<c>FarmStands</c>, keyed by its
/// <c>PlacedObject</c> id): the crates, the honesty box, where it stands (road and town nearby).
/// </summary>
public sealed class StandState
{
    public long Id { get; set; }
    public string Owner { get; set; } = "";
    public List<StandSlot> Slots { get; set; } = new();
    /// <summary>Francs in the honesty box, waiting for the owner.</summary>
    public int Cash { get; set; }
    /// <summary>Metres to the nearest road (the server measures it when the stand is set up); NaN not known yet.</summary>
    public float RoadM { get; set; } = float.NaN;
    /// <summary>Buildings within <see cref="FarmStandRules.TownRadius"/> m when set up: how many people live near.</summary>
    public int Houses { get; set; }
    /// <summary>Environment time of the last sales tick.</summary>
    public double LastTick { get; set; }
    /// <summary>Francs taken in all (the stand's tally, for the panel).</summary>
    public int Takings { get; set; }

    public int Stocked => Slots.Sum(s => s.Count);
}

/// <summary>One sale to a passer-by: which crate, how many, for how much in all.</summary>
public readonly record struct StandSale(int Slot, ItemId Item, int Count, int Total);

/// <summary>
/// The farm stand's rules (#494, a Hofladen with an honesty box): what may be put on it, how fast
/// passers-by buy (a road close by, a village round it, cooked dishes sell faster), its price
/// (<see cref="FarmPrices.Stand"/>, between the counter and a delivered load).
/// </summary>
public static class FarmStandRules
{
    public const int Crates = 6;
    public const int PerCrate = 40;
    /// <summary>A stand must stand this close to a road (server check when it is set up).</summary>
    public const float RoadMax = 60f;
    /// <summary>Full footfall from the road this close.</summary>
    public const float RoadNear = 15f;
    /// <summary>A second stand may not stand this close to another.</summary>
    public const float Spacing = 20f;
    /// <summary>Houses counted round a stand for the town factor.</summary>
    public const float TownRadius = 300f;
    /// <summary>Sales a day of the world (environment time) of one crate by a road in open country.</summary>
    public const double BasePerDay = 3;
    /// <summary>At most this much environment time is caught up at once (a stand left a month sells no more than this).</summary>
    public const double CatchUpMax = 10 * 24 * 3600;

    /// <summary>What a stand sells: the harvests and what is made of them (flour .. raclette), not seeds, fertiliser or hay.</summary>
    public static bool Stockable(ItemId item) =>
        item != ItemId.HayBale && (FarmTables.IsHarvest(item) || item is >= ItemId.Flour and <= ItemId.Raclette);

    /// <summary>A cooked dish (rösti, soup, raclette...): sells twice as fast.</summary>
    public static bool Cooked(ItemId item) => item is >= ItemId.BakedPotato and <= ItemId.Raclette;

    /// <summary>The road's factor: 1 within <see cref="RoadNear"/>, falling to 0.4 at <see cref="RoadMax"/>, 0 past it.</summary>
    public static double RoadFactor(float roadM)
    {
        if (float.IsNaN(roadM)) return 0.4;
        if (roadM <= RoadNear) return 1.0;
        if (roadM > RoadMax) return 0.0;
        return 1.0 - 0.6 * (roadM - RoadNear) / (RoadMax - RoadNear);
    }

    /// <summary>The town's factor: 1 in open country, up to 3 with 40 houses or more round it.</summary>
    public static double TownFactor(int houses) => 1.0 + Math.Min(Math.Max(houses, 0), 40) / 20.0;

    /// <summary>Expected sales an hour of one crate of <paramref name="item"/> at this stand.</summary>
    public static double PerDay(StandState s, ItemId item) =>
        BasePerDay * RoadFactor(s.RoadM) * TownFactor(s.Houses) * (Cooked(item) ? 2.0 : 1.0);

    /// <summary>
    /// Puts <paramref name="count"/> of an item on the stand: into its crate, else an empty one, at
    /// most <see cref="PerCrate"/> a crate. Returns how many went on (0: not stockable or full).
    /// </summary>
    public static int Stock(StandState s, ItemId item, int count)
    {
        if (count <= 0 || !Stockable(item)) return 0;
        var slot = s.Slots.FirstOrDefault(x => x.Item == item && x.Count > 0) ?? s.Slots.FirstOrDefault(x => x.Count == 0);
        if (slot == null)
        {
            if (s.Slots.Count >= Crates) return 0;
            slot = new StandSlot();
            s.Slots.Add(slot);
        }
        if (slot.Count == 0) { slot.Item = item; slot.Due = 0; }
        int put = Math.Min(count, PerCrate - slot.Count);
        slot.Count += put;
        return put;
    }

    /// <summary>Takes up to <paramref name="count"/> out of a crate (the owner unstocking, or a buyer). Returns how many.</summary>
    public static int Take(StandState s, int slot, int count)
    {
        if (slot < 0 || slot >= s.Slots.Count || count <= 0) return 0;
        var c = s.Slots[slot];
        int n = Math.Min(count, c.Count);
        c.Count -= n;
        if (c.Count == 0) c.Due = 0;
        return n;
    }

    /// <summary>
    /// The passers-by from <see cref="StandState.LastTick"/> to <paramref name="now"/>: each crate's
    /// expected sales (<see cref="PerDay"/>) add up and every whole one is sold, paid into the box
    /// at <paramref name="price"/>. Deterministic (no dice), so a check can count on it.
    /// </summary>
    public static List<StandSale> Advance(StandState s, double now, Func<ItemId, int> price)
    {
        var sales = new List<StandSale>();
        double dt = Math.Min(now - s.LastTick, CatchUpMax);
        s.LastTick = now;
        if (dt <= 0) return sales;
        for (int i = 0; i < s.Slots.Count; i++)
        {
            var c = s.Slots[i];
            if (c.Count <= 0) continue;
            c.Due += PerDay(s, c.Item) * dt / (24 * 3600.0);
            int n = Math.Min(c.Count, (int)Math.Floor(c.Due));
            if (n <= 0) continue;
            c.Due -= n;
            c.Count -= n;
            if (c.Count == 0) c.Due = 0;
            int total = n * Math.Max(0, price(c.Item));
            s.Cash += total;
            s.Takings += total;
            sales.Add(new StandSale(i, c.Item, n, total));
        }
        return sales;
    }
}
