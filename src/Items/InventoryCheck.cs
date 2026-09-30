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

        GD.Print(_failures == 0 ? "[invcheck] RESULT: ok" : $"[invcheck] RESULT: FAILED ({_failures})");
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
