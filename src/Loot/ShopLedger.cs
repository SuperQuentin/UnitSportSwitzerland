namespace UnitSport.Loot;

// Plain C#, no Godot: linked into the unit tests. The server's half of a shop (#273), with the disk
// and the bank handed in, so the rules are tested without a server.

/// <summary>What became of a purchase.</summary>
public enum BuyOutcome : byte
{
    Sold = 0,
    /// <summary>Nothing left in that slot (or the slot never had any).</summary>
    SoldOut = 1,
    /// <summary>The panel was opened in an earlier restock period: the answer carries the new stock.</summary>
    Restocked = 2,
    /// <summary>The line does not take that payment (cash under 20 CHF, the shotgun on the card only).</summary>
    WrongPayment = 3,
    /// <summary>The card was declined: the account does not cover it.</summary>
    Declined = 4,
    /// <summary>The machine is "Hors service" this period.</summary>
    OutOfOrder = 5,
    /// <summary>Sold, but it caught on the spiral: a hit on the machine frees it.</summary>
    Stuck = 6,
    /// <summary>Not a slot, not a count, not inside the shop.</summary>
    Refused = 7,
}

/// <summary>The answer to a purchase: what happened, how many, what they cost, and the slot's sold count after.</summary>
public readonly record struct BuyResult(BuyOutcome Outcome, int Count, int Total, int SoldAfter);

/// <summary>
/// The only thing a shop stores: how many of each slot were sold this restock period, per counter or
/// machine, in one file per tile (<c>user://shops/E_N.json</c>, as <c>[epoch, plan version, sold...]</c>).
/// A record from another epoch is a restocked shop; from another plan version (furniture
/// renumbered) it is ignored, like the loot masks. Stuck items are kept in memory only.
/// </summary>
public sealed class ShopLedger
{
    /// <summary>One tile's records: building index → furniture index → [epoch, plan version, sold per slot…].</summary>
    public sealed class TileState
    {
        public Dictionary<string, Dictionary<string, long[]>> Buildings { get; set; } = new();
    }

    private readonly Func<string, TileState?> _load;
    private readonly Action<string, TileState> _save;
    private readonly int _version;
    private readonly Dictionary<string, TileState> _tiles = new();
    private readonly Dictionary<(string Key, int Furniture), (long Epoch, List<int> Slots)> _stuck = new();

    /// <param name="load">Reads a tile's file by name ("E_N"), null when there is none.</param>
    /// <param name="save">Writes it (the game: <c>JsonStore.SaveAsync</c>).</param>
    /// <param name="planVersion"><c>InteriorLayout.CurrentVersion</c>: records of other plans are ignored.</param>
    public ShopLedger(Func<string, TileState?> load, Action<string, TileState> save, int planVersion)
    {
        _load = load;
        _save = save;
        _version = planVersion;
    }

    /// <summary>"E_N_I" → ("E_N", "I"); any other key is its own tile with building "0".</summary>
    public static (string Tile, string Building) Split(string key)
    {
        int at = key.LastIndexOf('_');
        return at > 0 ? (key[..at], key[(at + 1)..]) : (key, "0");
    }

    private TileState Tile(string name)
    {
        if (_tiles.TryGetValue(name, out var t)) return t;
        return _tiles[name] = _load(name) ?? new TileState();
    }

    /// <summary>How many of each of <paramref name="slots"/> slots were sold this period (zeros after a restock).</summary>
    public int[] Sold(string key, int furniture, long epoch, int slots)
    {
        var sold = new int[slots];
        var (tile, building) = Split(key);
        if (Tile(tile).Buildings.TryGetValue(building, out var b) && b.TryGetValue(furniture.ToString(), out var e)
            && e.Length >= 2 && e[0] == epoch && e[1] == _version)
            for (int i = 0; i < slots && i + 2 < e.Length; i++) sold[i] = (int)e[i + 2];
        return sold;
    }

    /// <summary>What is left of each line: its stock less what was sold.</summary>
    public static int[] Left(IReadOnlyList<StockLine> stock, int[] sold) =>
        stock.Select((l, i) => Math.Max(0, l.Stock - (i < sold.Length ? sold[i] : 0))).ToArray();

    private void Record(string key, int furniture, long epoch, int[] sold)
    {
        var (tile, building) = Split(key);
        var t = Tile(tile);
        if (!t.Buildings.TryGetValue(building, out var b)) t.Buildings[building] = b = new();
        b[furniture.ToString()] = new long[] { epoch, _version }.Concat(sold.Select(s => (long)s)).ToArray();
        _save(tile, t);
    }

    /// <summary>
    /// Sells up to <paramref name="count"/> of a slot (fewer when fewer are left). The buyer's epoch
    /// must be this one; the line must take the payment; on the card, <paramref name="charge"/>
    /// debits the account and may refuse. For a machine <paramref name="stuck"/> says the item
    /// caught (one item: machines sell one at a time); it is still sold, and kept for <see cref="Bump"/>.
    /// Cash cannot be checked here: the pocket is the client's, which pays when this answers.
    /// </summary>
    public BuyResult Buy(string key, int furniture, long epoch, long now, IReadOnlyList<StockLine> stock, int slot, int count,
        PayWith with, Func<int, bool> charge, bool outOfOrder = false, bool stuck = false)
    {
        if (epoch != now) return new(BuyOutcome.Restocked, 0, 0, 0);
        if (outOfOrder) return new(BuyOutcome.OutOfOrder, 0, 0, 0);
        if (slot < 0 || slot >= stock.Count || count <= 0) return new(BuyOutcome.Refused, 0, 0, 0);
        var sold = Sold(key, furniture, now, stock.Count);
        var line = stock[slot];
        int left = Math.Max(0, line.Stock - sold[slot]);
        if (left == 0) return new(BuyOutcome.SoldOut, 0, 0, sold[slot]);
        if (!ShopTables.Accepts(line.Pay, with)) return new(BuyOutcome.WrongPayment, 0, 0, sold[slot]);
        int n = Math.Min(count, left);
        int total = n * line.Price;
        if (with == PayWith.Card && !charge(total)) return new(BuyOutcome.Declined, 0, 0, sold[slot]);
        sold[slot] += n;
        Record(key, furniture, now, sold);
        if (stuck)
        {
            if (!_stuck.TryGetValue((key, furniture), out var s) || s.Epoch != now) _stuck[(key, furniture)] = s = (now, new List<int>());
            s.Slots.Add(slot);
            return new(BuyOutcome.Stuck, n, total, sold[slot]);
        }
        return new(BuyOutcome.Sold, n, total, sold[slot]);
    }

    /// <summary>Whether a machine has something caught on a spiral this period.</summary>
    public bool HasStuck(string key, int furniture, long now) =>
        _stuck.TryGetValue((key, furniture), out var s) && s.Epoch == now && s.Slots.Count > 0;

    /// <summary>
    /// A hit on the machine: whatever was caught drops (the slots, one item each), and with
    /// <paramref name="bonus"/> one more of the first of them comes along if the spiral still holds
    /// one (sold like any other, for nothing). Empty when nothing was stuck.
    /// </summary>
    public List<int> Bump(string key, int furniture, long now, IReadOnlyList<StockLine> stock, bool bonus)
    {
        var freed = new List<int>();
        if (!_stuck.Remove((key, furniture), out var s) || s.Epoch != now) return freed;
        freed.AddRange(s.Slots);
        int first = freed[0];
        var sold = Sold(key, furniture, now, stock.Count);
        if (bonus && first >= 0 && first < stock.Count && stock[first].Stock - sold[first] > 0)
        {
            sold[first]++;
            Record(key, furniture, now, sold);
            freed.Add(first);
        }
        return freed;
    }
}
