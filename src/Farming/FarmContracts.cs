using UnitSport.Core;
using UnitSport.Items;

// Plain C#, no Godot: linked into the unit tests.

namespace UnitSport.Farming;

/// <summary>An order a co-op posts for the farm week: so many of a harvest within so many farm days, at a multiplier.</summary>
public readonly record struct FarmOrder(int Index, ItemId Item, int Count, double Multiplier, int Days);

/// <summary>
/// An accepted order, kept per player by the server (<c>FarmSales</c>, <c>user://farm/contracts.json</c>).
/// Deliveries of <see cref="Item"/> to <see cref="Coop"/> (its counter or a machine) are paid as usual and
/// count here; complete, the server pays <see cref="FarmContracts.Bonus"/>; past <see cref="Deadline"/> it lapses.
/// </summary>
public sealed class FarmContract
{
    public string Coop { get; set; } = "";
    public long Week { get; set; }
    public int Index { get; set; }
    public ItemId Item { get; set; }
    public int Count { get; set; }
    public int Delivered { get; set; }
    public double Multiplier { get; set; }
    /// <summary>The full value of one when accepted (the base of the bonus).</summary>
    public float Unit { get; set; }
    /// <summary>Environment seconds (<c>World.WorldClock.EnvNow</c>).</summary>
    public double Deadline { get; set; }

    public int Missing => Math.Max(0, Count - Delivered);
}

/// <summary>What a delivery did to a player's contracts.</summary>
public readonly record struct ContractStep(int Counted, List<FarmContract> Completed);

/// <summary>
/// Delivery contracts (#494): each co-op posts <see cref="PerWeek"/> orders a farm week, the same on
/// every peer (keyed by the co-op's building and the week, like its wish list); a player takes up
/// to <see cref="MaxActive"/>. The rules are pure and unit-tested; <c>FarmSales</c> stores and pays.
/// </summary>
public static class FarmContracts
{
    public const int PerWeek = 3;
    public const int MaxActive = 3;
    /// <summary>An order is worth about this much at full value (CHF), so the bonus is worth the drive.</summary>
    public const double ValueMin = 500, ValueMax = 1400;

    /// <summary>The co-op's orders this farm week (distinct harvests), from its key and the week.</summary>
    public static FarmOrder[] Orders(string coop, long week, Func<ItemId, float> value)
    {
        var result = new FarmOrder[PerWeek];
        var used = new HashSet<int>();
        int n = FarmPrices.Harvests.Length;
        for (int i = 0; i < PerWeek; i++)
        {
            int pick = (int)(Fnv.Unit($"{coop}|{week}|order{i}") * n) % n;
            while (!used.Add(pick)) pick = (pick + 1) % n;
            var item = FarmPrices.Harvests[pick];
            double target = ValueMin + (ValueMax - ValueMin) * Fnv.Unit($"{coop}|{week}|order{i}|size");
            float v = Math.Max(1f, value(item));
            int count = (int)Math.Max(5, Math.Round(target / v / 5) * 5);
            count = Math.Min(count, 400);
            double mult = 1.3 + 0.1 * Math.Floor(Fnv.Unit($"{coop}|{week}|order{i}|mult") * 6);   // 1.3 .. 1.8
            int days = 2 + (int)(Fnv.Unit($"{coop}|{week}|order{i}|days") * 3);                    // 2 .. 4
            result[i] = new FarmOrder(i, item, count, Math.Round(mult, 1), days);
        }
        return result;
    }

    /// <summary>What completing it pays on top of the deliveries: (multiplier − 1) × count × the value when accepted.</summary>
    public static int Bonus(FarmContract c) => (int)Math.Floor((c.Multiplier - 1) * c.Count * c.Unit);

    /// <summary>The same for an order not taken yet.</summary>
    public static int Bonus(FarmOrder o, float unit) => (int)Math.Floor((o.Multiplier - 1) * o.Count * unit);

    /// <summary>Whether the player holds this co-op's order of this week already.</summary>
    public static bool Holds(IEnumerable<FarmContract> mine, string coop, long week, int index) =>
        mine.Any(c => c.Coop == coop && c.Week == week && c.Index == index);

    /// <summary>
    /// Takes an order: null and the contract added, or why not (too many, taken already, another
    /// week's). The deadline is <see cref="FarmOrder.Days"/> farm days from <paramref name="now"/>.
    /// </summary>
    public static string? Accept(List<FarmContract> mine, string coop, long week, FarmOrder order, float unit, double now)
    {
        if (week != FarmCalendar.Week(now)) return "That order is gone: a new week has begun.";
        if (Holds(mine, coop, week, order.Index)) return "You already took that order.";
        if (mine.Count >= MaxActive) return $"You hold {MaxActive} contracts already.";
        mine.Add(new FarmContract
        {
            Coop = coop, Week = week, Index = order.Index, Item = order.Item, Count = order.Count,
            Multiplier = order.Multiplier, Unit = unit, Deadline = now + order.Days * (double)FarmCalendar.DaySeconds,
        });
        return null;
    }

    /// <summary>
    /// <paramref name="count"/> of <paramref name="item"/> delivered to <paramref name="coop"/> at
    /// <paramref name="now"/>: fills that co-op's contracts for it, oldest first; the ones complete
    /// leave the list and come back in <see cref="ContractStep.Completed"/> (to be paid). Lapsed ones are not filled.
    /// </summary>
    public static ContractStep Deliver(List<FarmContract> mine, string coop, ItemId item, int count, double now)
    {
        var done = new List<FarmContract>();
        int counted = 0;
        foreach (var c in mine.Where(c => c.Coop == coop && c.Item == item && c.Deadline > now).OrderBy(c => c.Deadline).ToList())
        {
            if (count <= 0) break;
            int take = Math.Min(count, c.Missing);
            c.Delivered += take;
            count -= take;
            counted += take;
            if (c.Missing == 0)
            {
                done.Add(c);
                mine.Remove(c);
            }
        }
        return new ContractStep(counted, done);
    }

    /// <summary>Removes and returns the contracts past their deadline.</summary>
    public static List<FarmContract> Expire(List<FarmContract> mine, double now)
    {
        var gone = mine.Where(c => c.Deadline <= now).ToList();
        foreach (var c in gone) mine.Remove(c);
        return gone;
    }
}
