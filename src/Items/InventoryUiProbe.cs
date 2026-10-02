using Godot;

namespace UnitSport.Items;

/// <summary>
/// <c>--invuicheck</c> (with <c>--ride foot,&lt;s&gt;</c>, which puts a player on foot): opens the
/// inventory and drives it with synthetic mouse events at the real slot rectangles — drag and drop,
/// click-carry-click, a spread across three slots, shift-click, the bin. <see cref="InventoryCheck"/>
/// checks the arithmetic; this checks that the panel's hit-testing and press/release handling reach
/// it. Runs on a scratch inventory (<see cref="Inventory.Persist"/> off), never the player's own.
/// </summary>
public partial class InventoryUiProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--invuicheck") >= 0;

    private readonly ItemController _items;
    private int _failures;

    public InventoryUiProbe(ItemController items) => _items = items;
    public InventoryUiProbe() : this(null!) { }

    private Inventory Inv => _items.Inventory;

    public override async void _Ready()
    {
        // wait for a player on foot, which is what makes the items usable at all
        for (int i = 0; i < 600 && _items.UsablePlayer == null; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_items.UsablePlayer == null)
        {
            GD.Print("[invui] RESULT: FAILED — no player on foot");
            GetTree().Quit(1);
            return;
        }

        _items.Ui.Open();
        await Frames(3);

        // 1: drag and drop — press on hotbar slot 5, release on a pack slot
        var bars = Inv[4];
        await Press(4, MouseButton.Left);
        await Move(10);
        await Release(10, MouseButton.Left);
        Expect(Inv[10] == bars && Inv[4].IsEmpty && Inv.Carried.IsEmpty, $"drag and drop slot 5 -> pack ({Inv[10]})");

        // 2: click to carry, click to put down
        await Click(10, MouseButton.Left);
        Expect(Inv.Carried == bars, "click picks up");
        await Click(4, MouseButton.Left);
        Expect(Inv[4] == bars && Inv.Carried.IsEmpty, "click puts down");

        // 3: spread: carry the 4 energy bars in the pack, drag over three empty slots
        int pack = Inventory.HotbarSize;          // starter kit: 4 energy bars there
        int count = Inv[pack].Count;
        await Click(pack, MouseButton.Left);
        await Press(15, MouseButton.Left);
        await Move(16);
        await Move(17);
        await Release(17, MouseButton.Left);
        int spread = Inv[15].Count + Inv[16].Count + Inv[17].Count + Inv.Carried.Count;
        Expect(spread == count && Inv[15].Count == count / 3 && Inv[16].Count == count / 3,
            $"left drag spreads {count} evenly ({Inv[15].Count}/{Inv[16].Count}/{Inv[17].Count}, carried {Inv.Carried.Count})");
        Inv.ReturnCarried();

        // 4: shift-click sends a hotbar stack to the pack
        var gps = Inv[2];
        await Click(2, MouseButton.Left, shift: true);
        Expect(Inv[2].IsEmpty && Enumerable.Range(pack, Inv.PackSize).Any(i => Inv[i] == gps), "shift-click to the pack");

        // 5: right click takes half
        await Click(15, MouseButton.Right);
        Expect(!Inv.Carried.IsEmpty, $"right click takes half ({Inv.Carried.Count})");
        Inv.ReturnCarried();

        // 6: a click outside the panel with a stack on the cursor drops it on the ground (#208, #206)
        int dropSlot = Enumerable.Range(0, Inv.Capacity).First(i => !Inv[i].IsEmpty && Inv[i].Id != ItemId.Radio);
        var dropped = Inv[dropSlot];
        int before = DroppedItems.Instance?.GetChildCount() ?? -1;
        await Click(dropSlot, MouseButton.Left);
        var outside = new Vector2(6, 6);
        Push(new InputEventMouseMotion { Position = outside, GlobalPosition = outside });
        await Frames(2);
        Push(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = outside, GlobalPosition = outside });
        await Frames(2);
        Push(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = outside, GlobalPosition = outside });
        await Frames(20);
        int after = DroppedItems.Instance?.GetChildCount() ?? -1;
        Expect(Inv.Carried.IsEmpty && Inv[dropSlot].IsEmpty && after > before && before >= 0,
            $"click outside drops {dropped} on the ground (dropped items {before} -> {after})");

        await Catalogue();

        _items.Ui.Close();
        GD.Print(_failures == 0 ? "[invui] RESULT: ok" : $"[invui] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// 7 (#262): the inventory's catalogue button opens the catalogue; real clicks on a tile give
    /// one, right click ten, shift-click a stack; a screenshot lands in test_output/ when windowed.
    /// Offline, so the clicks run /spawn on this machine.
    /// </summary>
    private async Task Catalogue()
    {
        var open = _items.Ui.FindChildren("*", "Button", true, false).OfType<Button>()
            .FirstOrDefault(b => b.Text == "Item catalogue");
        Expect(open is { Visible: true }, "the inventory offers the catalogue offline");
        open?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        var cat = _items.Catalogue;
        Expect(cat.IsOpen && !_items.Ui.IsOpen, "the catalogue opens over a closed inventory");

        var search = cat.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().First();
        search.Text = "bread";
        search.EmitSignal(LineEdit.SignalName.TextChanged, "bread");
        await Frames(3);
        var tile = cat.FindChildren("*", "", true, false).OfType<SlotButton>().First(b => b.TooltipText == "Bread");
        Expect(tile.IsVisibleInTree(), "search finds the bread");

        int Bread() => Enumerable.Range(0, Inv.Capacity).Where(i => Inv[i].Id == ItemId.Bread).Sum(i => Inv[i].Count);
        int had = Bread();
        var at = tile.GetGlobalRect().GetCenter();
        async Task ClickTile(MouseButton button, bool shift = false)
        {
            Push(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await Frames(2);
            Push(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = at, GlobalPosition = at, ShiftPressed = shift });
            await Frames(2);
            Push(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = at, GlobalPosition = at, ShiftPressed = shift });
            await Frames(4);
        }
        await ClickTile(MouseButton.Left);
        Expect(Bread() == had + 1, $"a click gives one bread ({had} -> {Bread()})");
        await ClickTile(MouseButton.Right);
        Expect(Bread() == had + 11, $"a right click gives ten ({Bread()})");
        int stack = ItemDefs.Get(ItemId.Bread)!.MaxStack;
        await ClickTile(MouseButton.Left, shift: true);
        Expect(Bread() == had + 11 + stack, $"a shift-click gives a stack of {stack} ({Bread()})");

        search.Text = "";
        search.EmitSignal(LineEdit.SignalName.TextChanged, "");
        await Frames(4);
        if (DisplayServer.GetName() != "headless")
        {
            string png = ProjectSettings.GlobalizePath("res://test_output/catalogue.png");
            DirAccess.MakeDirRecursiveAbsolute(png.GetBaseDir());
            GetViewport().GetTexture().GetImage().SavePng(png);
            GD.Print($"[invui] screenshot {png}");
        }
        cat.Close();
        await Frames(2);
        Expect(!cat.IsOpen, "the catalogue closes");
    }

    private SlotButton SlotButtonOf(int slot) =>
        _items.Ui.FindChildren("*", "", true, false).OfType<SlotButton>()
            .First(b => b.Slot == slot && b.FocusMode != Control.FocusModeEnum.None);

    private Vector2 At(int slot) => SlotButtonOf(slot).GetGlobalRect().GetCenter();

    private async Task Move(int slot)
    {
        Push(new InputEventMouseMotion { Position = At(slot), GlobalPosition = At(slot) });
        await Frames(2);
    }

    private async Task Press(int slot, MouseButton button, bool shift = false)
    {
        await Move(slot);
        Push(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = At(slot), GlobalPosition = At(slot), ShiftPressed = shift });
        await Frames(2);
    }

    private async Task Release(int slot, MouseButton button)
    {
        Push(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = At(slot), GlobalPosition = At(slot) });
        await Frames(2);
    }

    private async Task Click(int slot, MouseButton button, bool shift = false)
    {
        await Press(slot, button, shift);
        await Release(slot, button);
        await Frames(12);   // past the double-click window's reach in frames, if not in time
    }

    private void Push(InputEvent e) => GetViewport().PushInput(e, true);

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"[invui] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
