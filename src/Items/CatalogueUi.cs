using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Items;

/// <summary>
/// The item catalogue (#262): every item in the game on one panel, by category, searchable, a click
/// away from the pack. Minecraft's creative tab, for testing and for running a server.
///
/// <para>
/// Offered offline, where you own the world, and to an admin on a server (<see cref="Allowed"/>).
/// It decides nothing itself: every button types a chat command (<c>/spawn</c>, <c>/money</c>,
/// <c>/bank</c>, <c>/clear</c>) through <see cref="ItemController.RunCommand"/>, so online the server checks the rights
/// exactly as it does for a typed one, and a client that forged its way into this panel gains nothing.
/// </para>
///
/// <para>
/// Click gives one (100 CHF of francs), right click ten, shift-click a full stack. On a pad: A, X and
/// Y on the focused item. Opened from the inventory panel's Catalogue button or <c>/catalogue</c>.
/// </para>
/// </summary>
public partial class CatalogueUi : CanvasLayer
{
    private const int TilePx = 56;
    private const int Columns = 9;
    private const int Gap = 6;

    private readonly ItemController _items;

    /// <summary>The panel may be offered: alone, or as a server's admin. The server checks again.</summary>
    public static bool Allowed => !Permissions.Online || Permissions.IsAdmin;

    public bool IsOpen => _panel.Visible;

    private Control _panel = null!;
    private LineEdit _search = null!;
    private readonly List<(ItemDef Def, SlotButton Tile)> _tiles = new();
    private ItemCategory? _category;
    private ItemDef? _inspect;
    private TextureRect _icon = null!;
    private Label _name = null!, _kind = null!, _blurb = null!, _value = null!, _status = null!, _count = null!;
    private Button _giveOne = null!, _giveSome = null!, _giveStack = null!, _clear = null!;
    private Label _cash = null!, _account = null!;
    private double _clearArmedUntil;

    public CatalogueUi(ItemController items) => _items = items;

    public CatalogueUi() : this(null!) { }

    /// <summary>What a click, a right click and a shift-click give: francs are counted, not stacked.</summary>
    private static (int One, int Some, int Stack) Amounts(ItemDef def) => def.Id == ItemId.Francs
        ? (100, 1_000, 10_000)
        : (1, 10, Math.Max(def.MaxStack, 1));

    /// <summary>What the catalogue lists: everything but a photo, which is nothing without its print.</summary>
    private static IEnumerable<ItemDef> Listed() =>
        ItemDefs.All.Where(d => d.Id is not (ItemId.None or ItemId.Photo));

    public override void _Ready()
    {
        // over the inventory (12), under the ride picker (30)
        Layer = 13;
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);

        var panel = new PanelContainer { Visible = false, Theme = UiTheme.Get() };
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.92f, 12, 22));
        centre.AddChild(panel);
        _panel = panel;

        var columns = UiKit.HBox(22);
        panel.AddChild(columns);
        columns.AddChild(BuildList());
        columns.AddChild(BuildSide());

        Permissions.Changed += OnPermissionsChanged;
        Filter();
    }

    public override void _ExitTree() => Permissions.Changed -= OnPermissionsChanged;

    private void OnPermissionsChanged()
    {
        if (IsOpen && !Allowed) Close();
    }

    // ------------------------------------------------------------------------------------
    // construction
    // ------------------------------------------------------------------------------------

    private Control BuildList()
    {
        var left = UiKit.VBox(10);

        var header = UiKit.HBox(12);
        header.AddChild(UiKit.Text("Item catalogue", UiTheme.FontHeading, UiTheme.Text, bold: true));
        header.AddChild(UiKit.Spacer(expand: true));
        _count = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _count.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        header.AddChild(_count);
        left.AddChild(header);

        _search = new LineEdit { PlaceholderText = "Search items…", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(0, 36) };
        _search.TextChanged += _ => Filter();
        // Enter in the search gives one of the first match: type "bread", Enter
        _search.TextSubmitted += _ =>
        {
            if (_tiles.FirstOrDefault(t => t.Tile.Visible) is { Def: { } first }) Give(first, Amounts(first).One);
        };
        left.AddChild(_search);

        // category tabs: All, then each category that has something in it
        var tabs = UiKit.HBox(4);
        var group = new ButtonGroup();
        tabs.AddChild(Tab("All", null, group, pressed: true));
        foreach (var category in Enum.GetValues<ItemCategory>())
            if (Listed().Any(d => d.Category == category)) tabs.AddChild(Tab(category.ToString(), category, group));
        left.AddChild(tabs);

        var grid = new GridContainer { Columns = Columns };
        grid.AddThemeConstantOverride("h_separation", Gap);
        grid.AddThemeConstantOverride("v_separation", Gap);
        foreach (var def in Listed().OrderBy(d => d.Category).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var tile = new SlotButton
            {
                CustomMinimumSize = new Vector2(TilePx, TilePx),
                TooltipText = def.Name,
                FocusMode = Control.FocusModeEnum.All,
                // a kind of item, not a stack: only francs say how much a click gives
                ShowCount = def.Id == ItemId.Francs,
            };
            var (one, some, stack) = Amounts(def);
            // a stack of one shows no count: the tile shows what a plain click gives
            tile.Display(new ItemStack(def.Id, one), false, false);
            // the mouse is read here, before the button sees it (shift is the click's own, not the
            // keyboard's now); Pressed is left to ui_accept, the pad's A and the keyboard's Enter
            tile.Pressed += () => Give(def, one);
            tile.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true } mb
                    || mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
                Give(def, mb.ButtonIndex == MouseButton.Right ? some : mb.ShiftPressed ? stack : one);
                tile.GrabFocus();
                tile.AcceptEvent();
            };
            tile.MouseEntered += () => Inspect(def);
            tile.FocusEntered += () => Inspect(def);
            _tiles.Add((def, tile));
            grid.AddChild(tile);
        }

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(Columns * TilePx + (Columns - 1) * Gap + 12, 5 * TilePx + 4 * Gap + 4),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(grid);
        left.AddChild(scroll);

        left.AddChild(UiKit.Text("Click: one   ·   Right click: ten   ·   Shift-click: a full stack   ·   Enter in the search: the first match",
            UiTheme.FontTiny, UiTheme.TextFaint));
        return left;
    }

    private Button Tab(string text, ItemCategory? category, ButtonGroup group, bool pressed = false)
    {
        var b = UiKit.Button(text);
        b.ToggleMode = true;
        b.ButtonGroup = group;
        b.ButtonPressed = pressed;
        b.CustomMinimumSize = new Vector2(0, 30);
        b.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        b.Toggled += on =>
        {
            if (!on) return;
            _category = category;
            Filter();
        };
        return b;
    }

    private Control BuildSide()
    {
        var right = UiKit.VBox(12);
        right.CustomMinimumSize = new Vector2(280, 0);

        // the item under the pointer, and how much of it to take
        var info = UiKit.VBox(6);
        var titleRow = UiKit.HBox(12);
        _icon = new TextureRect
        {
            CustomMinimumSize = new Vector2(48, 48),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        titleRow.AddChild(_icon);
        var names = UiKit.VBox(0);
        names.Alignment = BoxContainer.AlignmentMode.Center;
        names.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _name = UiKit.Text("", UiTheme.FontBody + 2, UiTheme.Text, bold: true);
        _kind = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextDim);
        names.AddChild(_name);
        names.AddChild(_kind);
        titleRow.AddChild(names);
        info.AddChild(titleRow);
        _blurb = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, wrap: true);
        _blurb.CustomMinimumSize = new Vector2(248, 72);
        info.AddChild(_blurb);
        _value = UiKit.Text("", UiTheme.FontTiny, new Color(UiTheme.Amber, 0.75f));
        info.AddChild(_value);

        var give = UiKit.HBox(6);
        _giveOne = SideButton(give, "", () => GiveInspected(a => a.One));
        _giveSome = SideButton(give, "", () => GiveInspected(a => a.Some));
        _giveStack = SideButton(give, "", () => GiveInspected(a => a.Stack), primary: true);
        info.AddChild(give);
        right.AddChild(UiKit.Card(info, 0.55f, 14));

        // money: straight into the pocket, or into the account
        var money = UiKit.VBox(6);
        money.AddChild(UiKit.Section("Money"));
        _cash = UiKit.Text("", UiTheme.FontBody, UiTheme.Text);
        _account = UiKit.Text("", UiTheme.FontSmall, UiTheme.Good);
        money.AddChild(_cash);
        var cashRow = UiKit.HBox(6);
        foreach (int n in new[] { 100, 1_000, 10_000 })
            SideButton(cashRow, $"+{n:N0}", () => Command($"/money {n}", $"+{n:N0} CHF cash"));
        money.AddChild(cashRow);
        money.AddChild(_account);
        var bankRow = UiKit.HBox(6);
        foreach (int n in new[] { 1_000, 10_000, 100_000 })
            SideButton(bankRow, $"+{n:N0}", () => Command($"/bank add {n}", $"+{n:N0} CHF in the account"));
        money.AddChild(bankRow);
        right.AddChild(UiKit.Card(money, 0.55f, 14));

        // the pack: emptied in one go (cash aside), after a second click
        var pack = UiKit.VBox(6);
        pack.AddChild(UiKit.Section("Inventory"));
        var packRow = UiKit.HBox(6);
        _clear = SideButton(packRow, "Clear inventory", ClearPressed);
        pack.AddChild(packRow);
        right.AddChild(UiKit.Card(pack, 0.55f, 14));

        right.AddChild(UiKit.Spacer(expand: true));
        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, wrap: true);
        right.AddChild(_status);
        var close = UiKit.Button("Close");
        close.Pressed += Close;
        right.AddChild(close);
        return right;
    }

    private static Button SideButton(Container into, string text, Action pressed, bool primary = false)
    {
        var b = UiKit.Button(text, primary);
        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        b.Pressed += pressed;
        into.AddChild(b);
        return b;
    }

    // ------------------------------------------------------------------------------------
    // state
    // ------------------------------------------------------------------------------------

    private void Filter()
    {
        string q = _search.Text.Trim();
        int shown = 0;
        foreach (var (def, tile) in _tiles)
        {
            tile.Visible = (_category is null || def.Category == _category)
                && (q.Length == 0 || def.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || def.Id.ToString().Contains(q.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (tile.Visible) shown++;
        }
        _count.Text = $"{shown} of {_tiles.Count} items";
        if (_inspect is null || !_tiles.Any(t => t.Def == _inspect && t.Tile.Visible))
            Inspect(_tiles.FirstOrDefault(t => t.Tile.Visible).Def);
    }

    private void Inspect(ItemDef? def)
    {
        _inspect = def;
        _icon.Texture = def == null ? null : ItemIcons.Get(def.Id);
        _name.Text = def?.Name ?? "Nothing matches";
        _kind.Text = def == null ? "" : $"{def.Category} · {(def.MaxStack > 1 ? $"stacks to {def.MaxStack}" : "does not stack")} · {def.Id}";
        _blurb.Text = def == null ? "Try another search or category." : InputHints.Format(def.Blurb);
        _value.Text = def is { Value: > 0 } ? $"Worth about {def.Value:0.#} CHF" : "";
        var (one, some, stack) = def == null ? (1, 10, 1) : Amounts(def);
        _giveOne.Text = $"+{one:N0}";
        _giveSome.Text = $"+{some:N0}";
        _giveStack.Text = def?.Id == ItemId.Francs ? $"+{stack:N0}" : $"Stack ({stack})";
        _giveSome.Visible = some != one && some != stack;
        _giveStack.Visible = stack != one || def?.Id == ItemId.Francs;
        foreach (var b in new[] { _giveOne, _giveSome, _giveStack }) b.Disabled = def == null;
    }

    private void GiveInspected(Func<(int One, int Some, int Stack), int> amount)
    {
        if (_inspect is { } def) Give(def, amount(Amounts(def)));
    }

    private void Give(ItemDef def, int count) =>
        Command($"/spawn {def.Id} {count}", $"+{count:N0} {def.Name}");

    private void Command(string command, string said)
    {
        if (_items.RunCommand is not { } run)
        {
            _status.Text = "Not connected to the chat: nothing to send it through.";
            return;
        }
        run(command);
        _status.Text = said;
    }

    private void ClearPressed()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now > _clearArmedUntil)
        {
            _clearArmedUntil = now + 3;
            _clear.Text = "Click again to empty it";
            return;
        }
        _clearArmedUntil = 0;
        _clear.Text = "Clear inventory";
        Command("/clear", "Inventory cleared (cash kept).");
    }

    // ------------------------------------------------------------------------------------
    // open / close / input
    // ------------------------------------------------------------------------------------

    public void Open()
    {
        if (IsOpen || !Allowed) return;
        if (_items.Ui.IsOpen) _items.Ui.Close();
        _panel.Visible = true;
        _status.Text = Permissions.Online ? "Online: the server checks each request." : "";
        UiFocus.Set(this, true);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Bank.Instance?.Refresh();
        if (PlayerInput.LastDevice == InputDevice.Gamepad) PlayerInput.FocusFirst(_panel);
        else _search.CallDeferred(Control.MethodName.GrabFocus);
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        _search.ReleaseFocus();
        UiFocus.Set(this, false);
        if (!_items.Ui.IsOpen) MouseCapture.Capture();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Inventory) || e.IsActionPressed(PlayerInput.Menu))
            Close();
        else if (e is InputEventJoypadButton pad && GetViewport().GuiGetFocusOwner() is SlotButton focused
                 && _tiles.FirstOrDefault(t => t.Tile == focused).Def is { } def)
        {
            // A is ui_accept, which presses the tile; X gives ten, Y a stack
            if (pad.ButtonIndex == JoyButton.X) Give(def, Amounts(def).Some);
            else if (pad.ButtonIndex == JoyButton.Y) Give(def, Amounts(def).Stack);
            else return;
        }
        else return;
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        // the body went away (a mode change, a replay): nobody to give to
        if (!_items.Ui.PlayerPresent)
        {
            Close();
            return;
        }
        if (_clearArmedUntil > 0 && Time.GetTicksMsec() / 1000.0 > _clearArmedUntil)
        {
            _clearArmedUntil = 0;
            _clear.Text = "Clear inventory";
        }
        _cash.Text = $"Cash: {_items.Inventory.Cash:N0} CHF";
        _account.Text = Bank.Instance is { } bank ? $"Account: {bank.Balance:N0} CHF" : "No bank";
    }
}
