using Godot;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>
/// <c>--invcheck</c>: the inventory's cursor operations, run on scratch inventories that never
/// touch the save file. Prints each case and returns non-zero on the first wrong count — the
/// operations are arithmetic, and a click that duplicates or loses items would otherwise only be
/// noticed after it had happened to someone's pack.
/// </summary>
public static class InventoryCheck
{
    public static bool Requested => CmdArgs.Has("--invcheck");

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

        // ---- clothes (#251) ----
        int top = Inventory.SlotOf(Avatar.WearSlot.Top), bottom = Inventory.SlotOf(Avatar.WearSlot.Bottom);
        int headSlot = Inventory.SlotOf(Avatar.WearSlot.Head);

        Case("clothes go on in their own slot, and swap", inv =>
        {
            inv.Put(0, new(ItemId.WhiteTee, 1));
            inv.Put(1, new(ItemId.BandTee, 1));
            Expect(inv.Wear(0) && inv[top].Id == ItemId.WhiteTee && inv[0].IsEmpty, "Use puts the tee on");
            Expect(inv.Wear(1) && inv[top].Id == ItemId.BandTee && inv[1].Id == ItemId.WhiteTee, "another tee swaps with it");
            Expect(inv.Outfit[Avatar.WearSlot.Top]?.Item == ItemId.BandTee, "the outfit shows the band tee");
            inv.Put(2, new(bar, 3));
            Expect(!inv.Wear(2), "a snack is not worn");
        });

        Case("a body slot takes only its own", inv =>
        {
            string? refused = null;
            inv.Refused += why => refused = why;
            inv.Put(0, new(ItemId.Jeans, 1));
            inv.PrimaryClick(0);
            inv.PrimaryClick(top);
            Expect(inv[top].IsEmpty && inv.Carried.Id == ItemId.Jeans && refused != null, "jeans refused on the top slot");
            inv.PrimaryClick(bottom);
            Expect(inv[bottom].Id == ItemId.Jeans && inv.Carried.IsEmpty, "and put on in the bottom one");
            inv.Put(1, new(ItemId.Cheese, 1));
            inv.Move(1, bottom);
            Expect(inv[bottom].Id == ItemId.Jeans && inv[1].Id == ItemId.Cheese, "a drag of cheese onto the jeans does nothing");
            inv.PrimaryClick(bottom);
            Expect(inv.Carried.Id == ItemId.Jeans && inv[bottom].IsEmpty, "a click takes them off");
        });

        Case("a dress takes the bottom slot", inv =>
        {
            string? refused = null;
            inv.Refused += why => refused = why;
            inv.Put(bottom, new(ItemId.TartanSkirt, 1));
            inv.Put(0, new(ItemId.LolitaDress, 1));
            Expect(inv.Wear(0) && inv[top].Id == ItemId.LolitaDress && inv[bottom].IsEmpty
                   && Total(inv, ItemId.TartanSkirt) == 1, "the skirt goes back in the pack");
            int skirt = Enumerable.Range(0, inv.Capacity).First(i => inv[i].Id == ItemId.TartanSkirt);
            Expect(!inv.Wear(skirt) && refused != null && inv[bottom].IsEmpty, "and cannot go on over the dress");
        });

        Case("shift-click wears and takes off; worn clothes are saved", inv =>
        {
            inv.Put(Inventory.HotbarSize, new(ItemId.CatEarsPink, 1));
            inv.QuickMove(Inventory.HotbarSize);
            Expect(inv[headSlot].Id == ItemId.CatEarsPink && inv.Worn == ItemId.CatEarsPink, "on the head");
            inv.Put(Inventory.HotbarSize, new(ItemId.BeeStockings, 1));
            inv.QuickMove(Inventory.HotbarSize);
            var back = Inventory.FromJson(inv.ToJson(), persist: false);
            Expect(back != null && back.Outfit == inv.Outfit && back.WornIn(Avatar.WearSlot.Legs).Id == ItemId.BeeStockings, "round trip");
            inv.QuickMove(headSlot);
            Expect(inv[headSlot].IsEmpty && Total(inv, ItemId.CatEarsPink) == 1, "shift-click takes the ears off into the pack");
        });

        Case("a hat worn before body slots moves onto the head", _ =>
        {
            var old = Inventory.FromJson("{\"Worn\":\"WitchHat\",\"Selected\":0,\"Cash\":0,\"Slots\":[{\"Slot\":4,\"Item\":\"WitchHat\",\"Count\":1}]}",
                persist: false);
            Expect(old != null && old.Worn == ItemId.WitchHat && old[4].IsEmpty, "out of the pack onto the head");
            var misplaced = Inventory.FromJson($"{{\"Selected\":0,\"Cash\":0,\"Slots\":[{{\"Slot\":{top},\"Item\":\"Jeans\",\"Count\":1}}]}}",
                persist: false);
            Expect(misplaced != null && misplaced[top].IsEmpty && Total(misplaced, ItemId.Jeans) == 1, "jeans saved in the top slot go back in the pack");
        });

        // ---- crafting (#271): the rules that need real item data (values, categories, stacks) ----
        Case("recipes: real items, nothing shop-only or worn made, salvage worth less than the part", _ =>
        {
            foreach (var r in Crafting.Recipes.All)
            {
                foreach (var o in Crafting.Recipes.Outputs(r))
                {
                    var def = ItemDefs.Get(o.Id);
                    Expect(def != null && def.Category != ItemCategory.Clothing && def.Category != ItemCategory.Cosmetic,
                        $"{r.Key}: makes {o.Id}, a real, non-clothing item");
                }
                Expect(r.In.All(i => ItemDefs.Get(i.Id) != null), $"{r.Key}: every ingredient exists");
                if (!r.Salvage) continue;
                float part = ItemDefs.Get(r.In[0].Id)!.Value;
                float back = Crafting.Recipes.Outputs(r).Sum(o => o.Count * ItemDefs.Get(o.Id)!.Value);
                Expect(back < part, $"{r.Key}: gives back {back:0.#} CHF of a {part:0.#} CHF part");
            }
        });

        Case("crafting on a real inventory takes from the pack end, never a photo", inv =>
        {
            inv.Put(0, new(ItemId.Cloth, 3));
            inv.Put(Inventory.HotbarSize + 4, new(ItemId.Cloth, 4));
            inv.Put(1, new(ItemId.Photo, 1, "abc"));
            Expect(inv.CountPlain(ItemId.Cloth) == 7 && inv.CountPlain(ItemId.Photo) == 0, "counts plain stacks only");
            var store = new ScratchStore(inv);
            var bandage = Crafting.Recipes.All.First(r => r.Out == ItemId.Bandage);
            int made = Crafting.Recipes.Craft(store, bandage, 2, Crafting.Station.Hands);
            Expect(made == 2 && inv.CountPlain(ItemId.Bandage) == 4, $"two batches, four bandages ({made}, {inv.CountPlain(ItemId.Bandage)})");
            Expect(inv[0].Count == 3 && inv[Inventory.HotbarSize + 4].IsEmpty, "the pack's cloth went first, the hotbar's stayed");
            Expect(inv[1].Id == ItemId.Photo, "the photo is untouched");
        });

        Case("crafting into a full pack drops the rest, loses nothing", inv =>
        {
            for (int i = 0; i < inv.Capacity; i++) inv.Put(i, new(ItemId.Binoculars, 1));
            inv.Put(3, new(ItemId.Tyre, 2));   // one stays: the slot never frees up
            var store = new ScratchStore(inv);
            var strip = Crafting.Recipes.All.First(r => r.Salvage && r.In[0].Id == ItemId.Tyre);
            Crafting.Recipes.Craft(store, strip, 1, Crafting.Station.Workbench);
            Expect(store.Dropped == 3 && inv.CountPlain(ItemId.Tyre) == 1, $"3 rubber kept or dropped ({inv.CountPlain(ItemId.Rubber)} + {store.Dropped})");
        });

        GD.Print(_failures == 0 ? "[invcheck] RESULT: ok": $"[invcheck] RESULT: FAILED ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static int Total(Inventory inv, ItemId id) =>
        Enumerable.Range(0, Inventory.Size).Where(i => inv[i].Id == id).Sum(i => inv[i].Count) + (inv.Carried.Id == id ? inv.Carried.Count : 0);

    /// <summary><see cref="Crafting.InventoryStore"/> without an <see cref="ItemController"/>: what does not fit is counted as dropped.</summary>
    private sealed class ScratchStore(Inventory inv) : Crafting.IItemStore
    {
        public int Dropped;
        public int Count(ItemId id) => inv.CountPlain(id);
        public void Take(ItemId id, int count) => inv.TakePlain(id, count);
        public void Give(ItemId id, int count) => Dropped += inv.Add(id, count);
    }

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
