using Godot;

namespace UnitSport.Items;

/// <summary>
/// <c>--invcheck</c>: the inventory's cursor operations, run on scratch inventories that never
/// touch the save file. Prints each case and returns non-zero on the first wrong count — the
/// operations are arithmetic, and a click that duplicates or loses items would otherwise only be
/// noticed after it had happened to someone's pack.
/// </summary>
public static class InventoryCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--invcheck") >= 0;

    private static int _failures;

    public static int Run()
    {
        var bar = ItemId.EnergyBar;         // stacks to 10
        var flag = ItemId.SwissFlag;        // stacks to 5
        var bin = ItemId.Binoculars;        // does not stack

        Case("pick up and put down", inv =>
        {
            inv.Put(0, new(bar, 7));
            inv.PrimaryClick(0);
            Expect(inv.Carried == new ItemStack(bar, 7) && inv[0].IsEmpty, "whole stack on the cursor");
            inv.PrimaryClick(10);
            Expect(inv[10] == new ItemStack(bar, 7) && inv.Carried.IsEmpty, "put down on an empty slot");
        });

        Case("merge, remainder stays carried", inv =>
        {
            inv.Put(0, new(bar, 7));
            inv.Put(1, new(bar, 6));
            inv.PrimaryClick(0);
            inv.PrimaryClick(1);
            Expect(inv[1].Count == 10 && inv.Carried.Count == 3, $"10 in the slot, 3 left ({inv[1].Count}, {inv.Carried.Count})");
        });

        Case("swap different items", inv =>
        {
            inv.Put(0, new(bar, 4));
            inv.Put(1, new(bin, 1));
            inv.PrimaryClick(0);
            inv.PrimaryClick(1);
            Expect(inv[1] == new ItemStack(bar, 4) && inv.Carried == new ItemStack(bin, 1), "binoculars now carried");
        });

        Case("right click: half, then one each", inv =>
        {
            inv.Put(0, new(bar, 7));
            inv.SecondaryClick(0);
            Expect(inv.Carried.Count == 4 && inv[0].Count == 3, "takes the larger half");
            inv.SecondaryClick(5);
            inv.SecondaryClick(6);
            Expect(inv[5].Count == 1 && inv[6].Count == 1 && inv.Carried.Count == 2, "one in each");
        });

        Case("shift-click across", inv =>
        {
            inv.Put(0, new(flag, 4));
            inv.Put(Inventory.HotbarSize + 3, new(flag, 3));
            inv.QuickMove(0);
            Expect(inv[Inventory.HotbarSize + 3].Count == 5 && inv[Inventory.HotbarSize].Count == 2 && inv[0].IsEmpty,
                "tops up the pack stack, the rest in the first free pack slot");
            inv.QuickMove(Inventory.HotbarSize);
            Expect(inv[0] == new ItemStack(flag, 2), "and back to the hotbar");
        });

        Case("double-click gathers", inv =>
        {
            inv.Put(0, new(bar, 2));
            inv.Put(3, new(bar, 5));
            inv.Put(9, new(bar, 1));
            inv.PrimaryClick(0);
            inv.Collect();
            Expect(inv.Carried.Count == 8 && inv[3].IsEmpty && inv[9].IsEmpty, "everything onto the cursor");
        });

        Case("drag spreads evenly, and redoes cleanly", inv =>
        {
            inv.Put(0, new(bar, 9));
            inv.PrimaryClick(0);
            var snap = inv.Snapshot();
            var slots = new List<int> { 6, 7 };
            inv.Distribute(slots, oneEach: false);
            inv.Restore(snap);
            slots.Add(8);
            inv.Distribute(slots, oneEach: false);
            Expect(inv[6].Count == 3 && inv[7].Count == 3 && inv[8].Count == 3 && inv.Carried.IsEmpty,
                "9 over three slots is three each, not counted twice");
        });

        Case("number key swaps with the hotbar", inv =>
        {
            inv.Put(12, new(flag, 2));
            inv.Put(2, new(bin, 1));
            inv.SwapWithHotbar(12, 2);
            Expect(inv[2] == new ItemStack(flag, 2) && inv[12] == new ItemStack(bin, 1), "swapped");
        });

        Case("bin and back", inv =>
        {
            inv.Put(0, new(flag, 3));
            inv.PrimaryClick(0);
            inv.Trash();
            Expect(inv.Carried.IsEmpty && inv.Trashed.Count == 3, "thrown away");
            inv.Untrash();
            Expect(inv.Carried.Count == 3 && inv.Trashed.IsEmpty, "taken back");
        });

        Case("closing returns the cursor stack", inv =>
        {
            inv.Put(0, new(bar, 5));
            inv.PrimaryClick(0);
            inv.ReturnCarried();
            Expect(inv.Carried.IsEmpty && Total(inv, bar) == 5, "nothing lost");
        });

        Case("francs are cash, not a slot", inv =>
        {
            Expect(inv.Add(ItemId.Francs, 45) == 0 && inv.Cash == 45, "counted");
            Expect(Enumerable.Range(0, Inventory.Size).All(i => inv[i].IsEmpty), "no slot used");
            Expect(inv.TakeCash(45) && inv.Cash == 0 && !inv.TakeCash(1), "claimed away, and no overdraft");
        });

        // ---- per-instance data (photos: ItemStack.Data) ----
        var photo = ItemId.Photo;           // does not stack; Data = which print

        Case("photos with different data never merge", inv =>
        {
            inv.Put(0, new(photo, 1, "aaaaaaaaaaaaaaaa"));
            inv.Put(1, new(photo, 1, "bbbbbbbbbbbbbbbb"));
            inv.PrimaryClick(0);
            inv.PrimaryClick(1);
            Expect(inv[1].Data == "aaaaaaaaaaaaaaaa" && inv.Carried.Data == "bbbbbbbbbbbbbbbb" && inv.Carried.Count == 1,
                "they swap, each keeps its print");
            inv.ReturnCarried();
            Expect(Enumerable.Range(0, Inventory.Size).Count(i => inv[i].Id == photo) == 2 && inv.Carried.IsEmpty,
                "returned to a slot of its own, data kept");
        });

        Case("data keeps stackable items apart", inv =>
        {
            inv.Put(0, new(bar, 3, "x"));
            inv.Put(1, new(bar, 3));
            inv.PrimaryClick(0);
            inv.PrimaryClick(1);
            Expect(inv[1] == new ItemStack(bar, 3, "x") && inv.Carried == new ItemStack(bar, 3), "swapped, not merged");
            inv.SecondaryClick(2);
            Expect(inv[2] == new ItemStack(bar, 1) && inv.Carried.Count == 2, "one put down keeps (no) data");
            inv.SecondaryClick(1);
            Expect(inv[1] == new ItemStack(bar, 2) && inv.Carried == new ItemStack(bar, 3, "x"),
                "right click on a different-data stack swaps");
            var snap = inv.Snapshot();
            inv.Distribute(new List<int> { 1, 5, 6 }, oneEach: true);
            Expect(inv[1] == new ItemStack(bar, 2) && inv[5] == new ItemStack(bar, 1, "x") && inv[6] == new ItemStack(bar, 1, "x"),
                "a spread skips the other stack and keeps the data");
            inv.Restore(snap);
            inv.Put(9, new(bar, 1, "x"));
            inv.Collect();
            Expect(inv.Carried == new ItemStack(bar, 4, "x") && inv[9].IsEmpty && inv[1].Count == 2 && inv[2].Count == 1,
                "double-click gathers only the same data");
        });

        Case("add, room and shift-click respect data", inv =>
        {
            Expect(inv.Add(new ItemStack(photo, 1, "cccccccccccccccc")) == 0 && inv[0] == new ItemStack(photo, 1, "cccccccccccccccc"),
                "added with its print");
            inv.Put(1, new(bar, 9, "x"));
            Expect(inv.Room(bar, "x") == 1 + (inv.Capacity - 2) * 10 && inv.Room(bar) == (inv.Capacity - 2) * 10,
                $"room counts only same-data stacks ({inv.Room(bar, "x")}, {inv.Room(bar)})");
            inv.Add(bar, 1);
            Expect(inv[1].Count == 9 && inv[2] == new ItemStack(bar, 1), "a plain bar does not top up the 'x' stack");
            inv.Put(Inventory.HotbarSize, new(bar, 5));
            inv.QuickMove(1);
            Expect(inv[Inventory.HotbarSize].Count == 5 && inv[Inventory.HotbarSize + 1] == new ItemStack(bar, 9, "x"),
                "shift-click does not merge into a different-data stack");
            inv.Put(4, new(bar, 2, "x"));
            inv.Move(4, Inventory.HotbarSize + 1);
            Expect(inv[Inventory.HotbarSize + 1].Count == 10 && inv[4] == new ItemStack(bar, 1, "x"), "move merges the same data");
        });

        Case("save and load keep data; old saves load", inv =>
        {
            inv.Put(0, new(photo, 1, "dddddddddddddddd"));
            inv.Put(1, new(bar, 4));
            var json = inv.ToJson();
            Expect(json.Contains("dddddddddddddddd") && json.Split("\"Data\"").Length == 2, "only the photo writes data");
            var back = Inventory.FromJson(json, persist: false);
            Expect(back != null && back[0] == new ItemStack(photo, 1, "dddddddddddddddd") && back[1] == new ItemStack(bar, 4),
                "round trip");
            var old = Inventory.FromJson("{\"Worn\":\"\",\"Selected\":2,\"Cash\":5,\"Slots\":[{\"Slot\":3,\"Item\":\"EnergyBar\",\"Count\":7}]}",
                persist: false);
            Expect(old != null && old[3] == new ItemStack(bar, 7) && old[3].Data == null && old.Cash == 5, "a save without data");
        });

        // ---- bags (#208) ----
        Case("a bag adds pack rows", inv =>
        {
            Expect(inv.Capacity == Inventory.HotbarSize + Inventory.BasePack && inv.Bag.IsEmpty, $"base pack ({inv.Capacity})");
            inv.Put(Inventory.HotbarSize, new(ItemId.Backpack, 1));
            inv.QuickMove(Inventory.HotbarSize);
            Expect(inv.Bag.Id == ItemId.Backpack && inv[Inventory.HotbarSize].IsEmpty
                   && inv.Capacity == Inventory.HotbarSize + Inventory.BasePack + 27, $"shift-click wears it ({inv.Capacity})");
            int n = 0;
            while (inv.Add(new ItemStack(photo, 1, $"p{n:D15}")) == 0) n++;
            Expect(n == inv.Capacity, $"every slot of the bigger pack takes an item ({n})");
        });

        Case("taking a bag off moves its stacks in", inv =>
        {
            inv.Put(Inventory.BagSlot, new(ItemId.HikingPack, 1));
            inv.Put(60, new(bar, 6));
            inv.Put(3, new(bar, 2));
            inv.PrimaryClick(Inventory.BagSlot);
            Expect(inv.Carried.Id == ItemId.HikingPack && inv.Bag.IsEmpty && inv[60].IsEmpty && Total(inv, bar) == 8
                   && inv[3].Count == 8, "the bar from slot 61 tops up the stack within the pack");
            inv.PrimaryClick(Inventory.BagSlot);
            Expect(inv.Bag.Id == ItemId.HikingPack && inv.Carried.IsEmpty, "click puts it back on");
        });

        Case("a full pack refuses to shrink", inv =>
        {
            string? refused = null;
            inv.Refused += why => refused = why;
            inv.Put(Inventory.BagSlot, new(ItemId.HikingPack, 1));
            for (int i = 0; i < inv.Capacity; i++) inv.Put(i, new(photo, 1, $"q{i:D15}"));
            inv.PrimaryClick(Inventory.BagSlot);
            Expect(refused != null && inv.Bag.Id == ItemId.HikingPack && inv.Carried.IsEmpty && inv[62].Data == "q000000000000062",
                "nothing moves, and it says why");
            inv.Put(0, new(ItemId.BeltPouch, 1));
            refused = null;
            Expect(!inv.WearBag(0) && refused != null && inv.Bag.Id == ItemId.HikingPack, "nor swaps for a smaller bag");
        });

        Case("only a bag goes in the bag slot", inv =>
        {
            inv.Put(0, new(bar, 3));
            inv.PrimaryClick(0);
            inv.PrimaryClick(Inventory.BagSlot);
            Expect(inv.Bag.IsEmpty && inv.Carried == new ItemStack(bar, 3), "refused");
            inv.ReturnCarried();
            inv.Put(1, new(ItemId.Handbag, 1));
            inv.Put(Inventory.BagSlot, new(ItemId.BeltPouch, 1));
            Expect(inv.WearBag(1) && inv.Bag.Id == ItemId.Handbag && inv[1].Id == ItemId.BeltPouch, "Use swaps the worn bag");
        });

        Case("the bag is saved; a carried bag returns to the bag slot", inv =>
        {
            inv.Put(Inventory.BagSlot, new(ItemId.Backpack, 1));
            inv.Put(50, new(bar, 4));
            var back = Inventory.FromJson(inv.ToJson(), persist: false);
            Expect(back != null && back.Bag.Id == ItemId.Backpack && back[50] == new ItemStack(bar, 4), "round trip");
            inv.PrimaryClick(Inventory.BagSlot);
            Expect(inv.ReturnCarried().IsEmpty && inv.Bag.Id == ItemId.Backpack, "returned to the bag slot, not a pack slot");
            Expect(inv.TakeCarried(false).IsEmpty, "nothing left on the cursor");
            inv.Put(0, new(bar, 5));
            inv.PrimaryClick(0);
            Expect(inv.TakeCarried(true) == new ItemStack(bar, 1) && inv.Carried.Count == 4, "drop one off the cursor");
        });

        GD.Print(_failures == 0 ? "[invcheck] RESULT: ok": $"[invcheck] RESULT: FAILED ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static int Total(Inventory inv, ItemId id) =>
        Enumerable.Range(0, Inventory.Size).Where(i => inv[i].Id == id).Sum(i => inv[i].Count) + (inv.Carried.Id == id ? inv.Carried.Count : 0);

    private static void Case(string name, Action<Inventory> body)
    {
        GD.Print($"[invcheck] {name}");
        body(new Inventory { Persist = false });
    }

    private static void Expect(bool ok, string what)
    {
        GD.Print($"[invcheck]   {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
