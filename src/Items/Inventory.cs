using System.Text.Json;
using Godot;

namespace UnitSport.Items;

/// <summary>One slot's contents. An empty slot is <c>default</c> (None, 0).</summary>
public readonly record struct ItemStack(ItemId Id, int Count)
{
    public bool IsEmpty => Id == ItemId.None || Count <= 0;
    public static readonly ItemStack Empty = default;
}

/// <summary>
/// The player's items: a hotbar of <see cref="HotbarSize"/> slots, the first of the array, and a
/// backpack behind it. Pure data — no nodes — so the UI, the item behaviours and the save file
/// all read one thing, and <see cref="Changed"/> is the only way anything learns it moved.
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

    private readonly ItemStack[] _slots = new ItemStack[Size];

    /// <summary>Which hotbar slot is in the hand, 0..<see cref="HotbarSize"/>-1.</summary>
    public int Selected { get; private set; }

    public event Action? Changed;

    public ItemStack this[int slot] => _slots[slot];

    public ItemStack Held => _slots[Selected];

    public ItemId HeldId => Held.IsEmpty ? ItemId.None : Held.Id;

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

    /// <summary>
    /// Adds items, topping up existing stacks first, then the hotbar, then the backpack.
    /// Returns how many did not fit.
    /// </summary>
    public int Add(ItemId id, int count)
    {
        var def = ItemDefs.Get(id);
        if (def == null || count <= 0) return count;

        for (int i = 0; i < Size && count > 0; i++)
            if (_slots[i].Id == id && _slots[i].Count < def.MaxStack)
            {
                int take = Math.Min(count, def.MaxStack - _slots[i].Count);
                _slots[i] = _slots[i] with { Count = _slots[i].Count + take };
                count -= take;
            }

        for (int i = 0; i < Size && count > 0; i++)
            if (_slots[i].IsEmpty)
            {
                int take = Math.Min(count, def.MaxStack);
                _slots[i] = new ItemStack(id, take);
                count -= take;
            }

        Notify();
        return count;
    }

    /// <summary>How many of <paramref name="id"/> would fit right now, without adding any.</summary>
    public int Room(ItemId id)
    {
        if (ItemDefs.Get(id) is not { } def) return 0;
        int room = 0;
        for (int i = 0; i < Size; i++)
            if (_slots[i].IsEmpty) room += def.MaxStack;
            else if (_slots[i].Id == id) room += Math.Max(0, def.MaxStack - _slots[i].Count);
        return room;
    }

    /// <summary>Takes one from a slot (eating, planting). False if it was empty.</summary>
    public bool TakeOne(int slot)
    {
        if (_slots[slot].IsEmpty) return false;
        int left = _slots[slot].Count - 1;
        _slots[slot] = left > 0 ? _slots[slot] with { Count = left } : ItemStack.Empty;
        Notify();
        return true;
    }

    /// <summary>
    /// Moves slot <paramref name="from"/> onto <paramref name="to"/>: merges when they are the
    /// same item and there is room, swaps otherwise. What every drag and pick-and-place does.
    /// </summary>
    public void Move(int from, int to)
    {
        if (from == to) return;
        var a = _slots[from];
        var b = _slots[to];

        if (!a.IsEmpty && a.Id == b.Id && ItemDefs.Get(a.Id) is { } def && b.Count < def.MaxStack)
        {
            int take = Math.Min(a.Count, def.MaxStack - b.Count);
            _slots[to] = b with { Count = b.Count + take };
            int left = a.Count - take;
            _slots[from] = left > 0 ? a with { Count = left } : ItemStack.Empty;
        }
        else
        {
            _slots[from] = b;
            _slots[to] = a;
        }
        Notify();
    }

    private void Notify()
    {
        Changed?.Invoke();
        Save();
    }

    // ------------------------------------------------------------------------------------
    // persistence
    // ------------------------------------------------------------------------------------

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
        for (int i = 0; i < Size; i++)
            if (_slots[i].Id == id && !_slots[i].IsEmpty) return true;
        return false;
    }

    private sealed class SaveData
    {
        public string Worn { get; set; } = "";
        public int Selected { get; set; }
        public List<SavedSlot> Slots { get; set; } = new();
    }

    private sealed class SavedSlot
    {
        public int Slot { get; set; }
        public string Item { get; set; } = "";
        public int Count { get; set; }
    }

    /// <summary>The saved inventory, or a fresh starter kit when there is none (or it is unreadable).</summary>
    public static Inventory Load()
    {
        var inv = new Inventory();
        try
        {
            using var f = Godot.FileAccess.FileExists(File)
                ? Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read)
                : null;
            if (f != null)
            {
                var data = JsonSerializer.Deserialize<SaveData>(f.GetAsText());
                if (data != null)
                {
                    // saved by name, so a renumbered enum cannot turn binoculars into a flag
                    foreach (var s in data.Slots)
                        if (s.Slot >= 0 && s.Slot < Size && Enum.TryParse<ItemId>(s.Item, out var id)
                            && ItemDefs.Get(id) is { } def && s.Count > 0)
                            inv._slots[s.Slot] = new ItemStack(id, Math.Min(s.Count, def.MaxStack));
                    inv.Selected = Math.Clamp(data.Selected, 0, HotbarSize - 1);
                    if (Enum.TryParse<ItemId>(data.Worn, out var worn)) inv._worn = worn;
                    return inv;
                }
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[inventory] could not read {File}: {e.Message}; starting fresh");
        }

        inv.GiveStarterKit();
        return inv;
    }

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
        var data = new SaveData { Selected = Selected, Worn = _worn == ItemId.None ? "" : _worn.ToString() };
        for (int i = 0; i < Size; i++)
            if (!_slots[i].IsEmpty)
                data.Slots.Add(new SavedSlot { Slot = i, Item = _slots[i].Id.ToString(), Count = _slots[i].Count });

        using var f = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
