using System.Text.Json;
using Godot;

namespace UnitSport.Items;

/// <summary>
/// One slot's contents. An empty slot is <c>default</c> (None, 0). <see cref="Data"/> is per-instance
/// data (a photo's id): two stacks only ever merge when both the item and the data are the same.
/// </summary>
public readonly record struct ItemStack(ItemId Id, int Count, string? Data = null)
{
    public bool IsEmpty => Id == ItemId.None || Count <= 0;
    public static readonly ItemStack Empty = default;

    /// <summary>Same item and same per-instance data: the two may share a stack.</summary>
    public bool SameKind(ItemStack other) => Id == other.Id && Data == other.Data;
}

/// <summary>
/// The player's items: a hotbar of <see cref="HotbarSize"/> slots, the first of the array, and a
/// backpack behind it, plus the <see cref="Cash"/> in their pocket. Pure data — no nodes — so the
/// UI, the item behaviours and the save file all read one thing, and <see cref="Changed"/> is the
/// only way anything learns it moved.
///
/// <para>
/// <b>Handled the Minecraft way.</b> A stack taken out of a slot is <see cref="Carried"/> — on the
/// cursor, in no slot — until it is put down; <see cref="PrimaryClick"/>, <see cref="SecondaryClick"/>,
/// <see cref="QuickMove"/>, <see cref="Collect"/>, <see cref="SwapWithHotbar"/> and
/// <see cref="Distribute"/> are the whole vocabulary, and the panel is only a way of calling them.
/// The carried stack is saved with the rest, so quitting mid-move never loses it.
/// </para>
///
/// <para>
/// <b>Money is not an item.</b> Swiss francs picked up go to <see cref="Cash"/>, a counter, rather
/// than a slot a 30 CHF find would fill: money is counted, not stacked. Cash is what you carry, and
/// is lost when you are knocked out; claiming it moves it to the account the server keeps
/// (<see cref="Bank"/>).
/// </para>
///
/// <para>
/// Local to this machine and saved to <c>user://inventory.json</c>. It is never sent: nothing in
/// the game can take an item from another player, so a server-side inventory would be authority
/// over something nobody contests. What is in the hand is replicated separately, as
/// <see cref="Player.FootPlayer.HeldItemId"/>, because that one other players can see.
/// </para>
/// </summary>
public sealed class Inventory
{
    public const int HotbarSize = 6;
    public const int BackpackSize = 18;
    public const int Size = HotbarSize + BackpackSize;

    private const string File = "user://inventory.json";

    private ItemStack[] _slots = new ItemStack[Size];
    private int _batch;
    private bool _dirty;

    /// <summary>Which hotbar slot is in the hand, 0..<see cref="HotbarSize"/>-1.</summary>
    public int Selected { get; private set; }

    /// <summary>The stack on the cursor while the inventory is open. Empty the rest of the time.</summary>
    public ItemStack Carried { get; private set; }

    /// <summary>The last stack thrown away, which one click on the bin gets back. Not saved.</summary>
    public ItemStack Trashed { get; private set; }

    /// <summary>Swiss francs in your pocket, not yet claimed to your account.</summary>
    public int Cash { get; private set; }

    public event Action? Changed;

    /// <summary>Write every change to <c>user://inventory.json</c>. Off for the check's scratch inventories.</summary>
    public bool Persist { get; init; } = true;

    public ItemStack this[int slot] => _slots[slot];

    public ItemStack Held => _slots[Selected];

    public ItemId HeldId => Held.IsEmpty ? ItemId.None : Held.Id;

    public static bool IsHotbar(int slot) => slot < HotbarSize;

    public void Select(int hotbarSlot)
    {
        hotbarSlot = ((hotbarSlot % HotbarSize) + HotbarSize) % HotbarSize;
        if (hotbarSlot == Selected) return;
        Selected = hotbarSlot;
        Notify();
    }

    /// <summary>Next/previous hotbar slot that holds something, skipping empties so a scroll always lands on an item.</summary>
    public void Cycle(int direction)
    {
        for (int step = 1; step <= HotbarSize; step++)
        {
            int slot = ((Selected + direction * step) % HotbarSize + HotbarSize) % HotbarSize;
            if (!_slots[slot].IsEmpty || step == HotbarSize)
            {
                Select(slot);
                return;
            }
        }
    }

    private static int MaxStack(ItemId id) => ItemDefs.Get(id)?.MaxStack ?? 1;

    /// <summary>
    /// Adds items, topping up existing stacks first, then the hotbar, then the backpack.
    /// Francs go to <see cref="Cash"/>. Returns how many did not fit.
    /// </summary>
    public int Add(ItemId id, int count) => Add(new ItemStack(id, count));

    /// <summary>
    /// Adds a stack, its <see cref="ItemStack.Data"/> kept: only stacks with the same data are topped
    /// up. Returns how many did not fit.
    /// </summary>
    public int Add(ItemStack stack)
    {
        var (id, count, data) = (stack.Id, stack.Count, stack.Data);
        if (count <= 0) return count;
        if (id == ItemId.Francs)
        {
            Cash += count;
            Notify();
            return 0;
        }
        if (ItemDefs.Get(id) is not { } def) return count;

        for (int i = 0; i < Size && count > 0; i++)
            if (_slots[i].Id == id && _slots[i].Data == data && _slots[i].Count < def.MaxStack)
            {
                int take = Math.Min(count, def.MaxStack - _slots[i].Count);
                _slots[i] = _slots[i] with { Count = _slots[i].Count + take };
                count -= take;
            }

        for (int i = 0; i < Size && count > 0; i++)
            if (_slots[i].IsEmpty)
            {
                int take = Math.Min(count, def.MaxStack);
                _slots[i] = new ItemStack(id, take, data);
                count -= take;
            }

        Notify();
        return count;
    }

    /// <summary>How many of <paramref name="id"/> would fit right now, without adding any.</summary>
    public int Room(ItemId id) => Room(id, null);

    /// <summary>Room for <paramref name="id"/> carrying <paramref name="data"/> (a photo needs a free slot).</summary>
    public int Room(ItemId id, string? data)
    {
        if (id == ItemId.Francs) return int.MaxValue;
        if (ItemDefs.Get(id) is not { } def) return 0;
        int room = 0;
        for (int i = 0; i < Size; i++)
            if (_slots[i].IsEmpty) room += def.MaxStack;
            else if (_slots[i].Id == id && _slots[i].Data == data) room += Math.Max(0, def.MaxStack - _slots[i].Count);
        return room;
    }

    /// <summary>Rewrites a stack's per-instance data (a radio's CD). False if the slot is empty.</summary>
    public bool SetData(int slot, string? data)
    {
        if (_slots[slot].IsEmpty) return false;
        _slots[slot] = _slots[slot] with { Data = data };
        Notify();
        return true;
    }

    /// <summary>Takes one from a slot (eating, planting). False if it was empty.</summary>
    public bool TakeOne(int slot)
    {
        if (_slots[slot].IsEmpty) return false;
        _slots[slot] = Less(_slots[slot], 1);
        Notify();
        return true;
    }

    /// <summary>
    /// Moves slot <paramref name="from"/> onto <paramref name="to"/>: merges when they are the
    /// same item and there is room, swaps otherwise. What the pad's "take in hand" does.
    /// </summary>
    public void Move(int from, int to)
    {
        if (from == to) return;
        var a = _slots[from];
        var b = _slots[to];

        if (!a.IsEmpty && a.SameKind(b) && b.Count < MaxStack(a.Id))
        {
            int take = Math.Min(a.Count, MaxStack(a.Id) - b.Count);
            _slots[to] = b with { Count = b.Count + take };
            _slots[from] = Less(a, take);
        }
        else
        {
            _slots[from] = b;
            _slots[to] = a;
        }
        Notify();
    }

    // ------------------------------------------------------------------------------------
    // the cursor: Minecraft's inventory, as operations
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Left click. Empty hand: pick the whole stack up. Carrying: put it all down on an empty slot,
    /// top up a stack of the same item (the rest stays on the cursor), or swap with a different one.
    /// </summary>
    public void PrimaryClick(int slot)
    {
        var s = _slots[slot];
        if (Carried.IsEmpty)
        {
            if (s.IsEmpty) return;
            Carried = s;
            _slots[slot] = ItemStack.Empty;
        }
        else if (s.IsEmpty)
        {
            int put = Math.Min(Carried.Count, MaxStack(Carried.Id));
            _slots[slot] = Carried with { Count = put };
            Carried = Less(Carried, put);
        }
        else if (s.SameKind(Carried))
        {
            int put = Math.Min(Carried.Count, MaxStack(s.Id) - s.Count);
            if (put <= 0) return;
            _slots[slot] = s with { Count = s.Count + put };
            Carried = Less(Carried, put);
        }
        else
        {
            _slots[slot] = Carried;
            Carried = s;
        }
        Notify();
    }

    /// <summary>
    /// Right click. Empty hand: pick up half, rounded up. Carrying: put exactly one down on an empty
    /// slot or a stack of the same item with room; swap with anything else.
    /// </summary>
    public void SecondaryClick(int slot)
    {
        var s = _slots[slot];
        if (Carried.IsEmpty)
        {
            if (s.IsEmpty) return;
            int half = (s.Count + 1) / 2;
            Carried = s with { Count = half };
            _slots[slot] = Less(s, half);
        }
        else if (s.IsEmpty || (s.SameKind(Carried) && s.Count < MaxStack(s.Id)))
        {
            _slots[slot] = Carried with { Count = s.IsEmpty ? 1 : s.Count + 1 };
            Carried = Less(Carried, 1);
        }
        else if (!s.SameKind(Carried))
        {
            _slots[slot] = Carried;
            Carried = s;
        }
        else return;
        Notify();
    }

    /// <summary>
    /// Shift-click: sends the stack across to the other section — hotbar to backpack or back —
    /// topping up stacks of the same item first. Whatever finds no room stays where it was.
    /// </summary>
    public void QuickMove(int slot)
    {
        var s = _slots[slot];
        if (s.IsEmpty) return;
        (int from, int to) = IsHotbar(slot) ? (HotbarSize, Size) : (0, HotbarSize);
        int left = s.Count, max = MaxStack(s.Id);

        for (int i = from; i < to && left > 0; i++)
            if (_slots[i].SameKind(s) && _slots[i].Count < max)
            {
                int put = Math.Min(left, max - _slots[i].Count);
                _slots[i] = _slots[i] with { Count = _slots[i].Count + put };
                left -= put;
            }
        for (int i = from; i < to && left > 0; i++)
            if (_slots[i].IsEmpty)
            {
                _slots[i] = s with { Count = left };
                left = 0;
            }

        if (left == s.Count) return;
        _slots[slot] = s with { Count = left };
        if (left <= 0) _slots[slot] = ItemStack.Empty;
        Notify();
    }

    /// <summary>
    /// Double-click with a stack on the cursor: pulls every other stack of the same item onto it,
    /// up to a full stack — the smallest first, so a full stack elsewhere is not broken for a few.
    /// </summary>
    public void Collect()
    {
        if (Carried.IsEmpty) return;
        int max = MaxStack(Carried.Id);
        var order = Enumerable.Range(0, Size)
            .Where(i => _slots[i].SameKind(Carried) && !_slots[i].IsEmpty)
            .OrderBy(i => _slots[i].Count);
        foreach (int i in order)
        {
            int take = Math.Min(_slots[i].Count, max - Carried.Count);
            if (take <= 0) break;
            Carried = Carried with { Count = Carried.Count + take };
            _slots[i] = Less(_slots[i], take);
        }
        Notify();
    }

    /// <summary>A number key over a slot: swaps that slot with hotbar slot <paramref name="hotbar"/>.</summary>
    public void SwapWithHotbar(int slot, int hotbar)
    {
        if (slot == hotbar || hotbar < 0 || hotbar >= HotbarSize) return;
        (_slots[slot], _slots[hotbar]) = (_slots[hotbar], _slots[slot]);
        Notify();
    }

    /// <summary>
    /// A drag across several slots with a stack on the cursor. <paramref name="oneEach"/> (right
    /// button) lays one item in each; otherwise (left) the stack is split evenly and the remainder
    /// stays on the cursor. Slots holding something else are skipped. The caller restores the
    /// <see cref="Snapshot"/> taken when the drag began before each call, so moving back over a slot
    /// never counts it twice.
    /// </summary>
    public void Distribute(IReadOnlyList<int> slots, bool oneEach)
    {
        if (Carried.IsEmpty) return;
        var carried = Carried;
        int max = MaxStack(carried.Id);
        var usable = slots.Where(i => _slots[i].IsEmpty || (_slots[i].SameKind(carried) && _slots[i].Count < max)).ToList();
        if (usable.Count == 0) return;

        int share = oneEach ? 1 : Math.Max(1, Carried.Count / usable.Count);
        foreach (int i in usable)
        {
            if (Carried.IsEmpty) break;
            int have = _slots[i].IsEmpty ? 0 : _slots[i].Count;
            int put = Math.Min(Math.Min(share, Carried.Count), max - have);
            if (put <= 0) continue;
            _slots[i] = carried with { Count = have + put };
            Carried = Less(Carried, put);
        }
        Notify();
    }

    /// <summary>The cursor stack into the bin, replacing whatever was thrown there before.</summary>
    public void Trash()
    {
        if (Carried.IsEmpty) return;
        Trashed = Carried;
        Carried = ItemStack.Empty;
        Notify();
    }

    /// <summary>A click on the bin with an empty hand: the last thing thrown away comes back.</summary>
    public void Untrash()
    {
        if (!Carried.IsEmpty || Trashed.IsEmpty) return;
        Carried = Trashed;
        Trashed = ItemStack.Empty;
        Notify();
    }

    /// <summary>
    /// Puts the cursor stack back into the slots: on closing the panel, where Minecraft would throw
    /// it on the ground. It always fits, having come out of them; if somehow it does not, the rest
    /// goes to the bin rather than vanishing.
    /// </summary>
    public void ReturnCarried()
    {
        if (Carried.IsEmpty) return;
        var c = Carried;
        Carried = ItemStack.Empty;
        using (Batch())
        {
            int left = Add(c);
            if (left > 0) Trashed = c with { Count = left };
            _dirty = true;
        }
    }

    /// <summary>Everything the cursor operations touch, for undoing a drag in progress.</summary>
    public (ItemStack[] Slots, ItemStack Carried) Snapshot() => ((ItemStack[])_slots.Clone(), Carried);

    public void Restore((ItemStack[] Slots, ItemStack Carried) snap)
    {
        _slots = (ItemStack[])snap.Slots.Clone();
        Carried = snap.Carried;
        Notify();
    }

    private static ItemStack Less(ItemStack s, int n) =>
        s.Count - n > 0 ? s with { Count = s.Count - n } : ItemStack.Empty;

    // ------------------------------------------------------------------------------------
    // money
    // ------------------------------------------------------------------------------------

    /// <summary>Spends or removes cash. False, and nothing taken, if there is not that much.</summary>
    public bool TakeCash(int amount)
    {
        if (amount < 0 || amount > Cash) return false;
        Cash -= amount;
        Notify();
        return true;
    }

    /// <summary>
    /// Groups several changes into one <see cref="Changed"/> and one save. A drag repaints on every
    /// slot it crosses; without this each would write the file.
    /// </summary>
    public IDisposable Batch() => new BatchScope(this);

    private sealed class BatchScope : IDisposable
    {
        private readonly Inventory _inv;
        private bool _done;

        public BatchScope(Inventory inv)
        {
            _inv = inv;
            inv._batch++;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--_inv._batch == 0 && _inv._dirty) _inv.Notify();
        }
    }

    private void Notify()
    {
        if (_batch > 0)
        {
            _dirty = true;
            return;
        }
        _dirty = false;
        Changed?.Invoke();
        Save();
    }

    // ---- worn -----------------------------------------------------------------------------------

    /// <summary>The hat being worn (an <see cref="ItemUse.Wear"/> item still in the pack), or None.</summary>
    public ItemId Worn => _worn != ItemId.None && Contains(_worn) ? _worn : ItemId.None;

    private ItemId _worn;

    public void SetWorn(ItemId id)
    {
        if (id == _worn) return;
        _worn = id;
        Notify();
    }

    public bool Contains(ItemId id)
    {
        if (Carried.Id == id && !Carried.IsEmpty) return true;
        for (int i = 0; i < Size; i++)
            if (_slots[i].Id == id && !_slots[i].IsEmpty) return true;
        return false;
    }

    // ------------------------------------------------------------------------------------
    // persistence
    // ------------------------------------------------------------------------------------

    private sealed class SaveData
    {
        public string Worn { get; set; } = "";
        public int Selected { get; set; }
        public int Cash { get; set; }
        public List<SavedSlot> Slots { get; set; } = new();
        public SavedSlot? Carried { get; set; }
    }

    private sealed class SavedSlot
    {
        public int Slot { get; set; }
        public string Item { get; set; } = "";
        public int Count { get; set; }
        /// <summary>Per-instance data (a photo id); absent in saves made before it existed.</summary>
        public string? Data { get; set; }
    }

    /// <summary>The saved inventory, or a fresh starter kit when there is none (or it is unreadable).</summary>
    public static Inventory Load()
    {
        try
        {
            using var f = Godot.FileAccess.FileExists(File)
                ? Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read)
                : null;
            if (f != null && FromJson(f.GetAsText()) is { } loaded) return loaded;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[inventory] could not read {File}: {e.Message}; starting fresh");
        }

        var inv = new Inventory();
        inv.GiveStarterKit();
        return inv;
    }

    /// <summary>
    /// An inventory from the save format, or null. Saves from before <see cref="ItemStack.Data"/>
    /// existed have no "Data" and load as plain stacks.
    /// </summary>
    internal static Inventory? FromJson(string json, bool persist = true)
    {
        var data = JsonSerializer.Deserialize<SaveData>(json);
        if (data == null) return null;
        var inv = new Inventory { Persist = persist };
        inv.Cash = Math.Max(0, data.Cash);
        // saved by name, so a renumbered enum cannot turn binoculars into a flag
        foreach (var s in data.Slots)
            if (s.Slot >= 0 && s.Slot < Size && Parse(s) is { } stack)
            {
                // francs saved in a slot before money had its own counter
                if (stack.Id == ItemId.Francs) inv.Cash += stack.Count;
                else inv._slots[s.Slot] = stack;
            }
        inv.Selected = Math.Clamp(data.Selected, 0, HotbarSize - 1);
        if (Enum.TryParse<ItemId>(data.Worn, out var worn)) inv._worn = worn;
        // quit with something on the cursor: back into the slots
        if (data.Carried is { } c && Parse(c) is { } carried)
        {
            inv.Carried = carried;
            inv._batch++;           // no save from inside Load
            inv.ReturnCarried();
            inv._batch--;
        }
        return inv;
    }

    /// <summary>A starter kit that is never saved, for the checks.</summary>
    public static Inventory Scratch()
    {
        var inv = new Inventory { Persist = false };
        inv.GiveStarterKit();
        return inv;
    }

    private static ItemStack? Parse(SavedSlot s) =>
        Enum.TryParse<ItemId>(s.Item, out var id) && ItemDefs.Get(id) is { } def && s.Count > 0
            ? new ItemStack(id, id == ItemId.Francs ? s.Count : Math.Min(s.Count, def.MaxStack),
                string.IsNullOrEmpty(s.Data) ? null : s.Data)
            : null;

    /// <summary>Puts a stack straight into a slot, for the check to set up a position.</summary>
    internal void Put(int slot, ItemStack stack) => _slots[slot] = stack;

    /// <summary>What a new player sets out with: the hotbar filled, spares in the pack.</summary>
    private void GiveStarterKit()
    {
        _slots[0] = new ItemStack(ItemId.Binoculars, 1);
        _slots[1] = new ItemStack(ItemId.Camera, 1);
        _slots[2] = new ItemStack(ItemId.Gps, 1);
        _slots[3] = new ItemStack(ItemId.SwissFlag, 3);
        _slots[4] = new ItemStack(ItemId.EnergyBar, 4);
        _slots[5] = new ItemStack(ItemId.WaterBottle, 2);
        _slots[HotbarSize] = new ItemStack(ItemId.EnergyBar, 4);
        _slots[HotbarSize + 1] = new ItemStack(ItemId.SwissFlag, 2);
        Selected = 5;   // start with a hand holding something harmless
        Save();
    }

    private void Save()
    {
        if (!Persist) return;
        using var f = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(ToJson());
    }

    /// <summary>The save format: stacks by item name, their data only when there is some.</summary>
    internal string ToJson()
    {
        var data = new SaveData { Selected = Selected, Cash = Cash, Worn = _worn == ItemId.None ? "" : _worn.ToString() };
        for (int i = 0; i < Size; i++)
            if (!_slots[i].IsEmpty)
                data.Slots.Add(new SavedSlot { Slot = i, Item = _slots[i].Id.ToString(), Count = _slots[i].Count, Data = _slots[i].Data });
        if (!Carried.IsEmpty)
            data.Carried = new SavedSlot { Slot = -1, Item = Carried.Id.ToString(), Count = Carried.Count, Data = Carried.Data };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }
}
