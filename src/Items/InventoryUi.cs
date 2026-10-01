using Godot;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>
/// Everything the inventory puts on screen: the hotbar and cash counter, the full inventory panel
/// (I / Tab / Back), the radial quick wheel (hold X / D-pad left), the optic overlays and the GPS
/// readout.
///
/// <para>
/// <b>Hotbar</b>: the six slots at the bottom are what can be in your hand. 1–6, the mouse
/// wheel, or D-pad right pick one. <b>Quick wheel</b>: the same six slots laid out round the
/// centre of the screen, chosen by pushing the mouse or right stick toward one and letting go —
/// the way to swap on a pad without cycling past everything.
/// </para>
///
/// <para>
/// <b>The panel works like Minecraft's.</b> A stack taken out of a slot rides on the cursor
/// (<see cref="Inventory.Carried"/>) until it is put down: left click picks up / puts down / swaps,
/// right click takes half or puts down one, shift-click sends a stack across between hotbar and
/// pack, a double-click gathers every stack of that item onto the cursor, and dragging with a stack
/// on the cursor spreads it over the slots crossed (evenly with the left button, one each with the
/// right). A plain press-drag-release from one slot to another is also a drag and drop. A number
/// key over a slot swaps it with that hotbar slot. On a pad: A, X and Y on the focused slot.
/// </para>
///
/// <para>
/// Mouse handling is done here in <see cref="_Input"/>, by hit-testing the slots, rather than by
/// each slot button: Godot keeps sending the events of a press to the control that took it, so a
/// slot never hears the mouse arrive during a drag that started on another. The slot buttons still
/// take focus, which is what a pad drives.
/// </para>
///
/// <para>
/// The panel and the wheel both register with <see cref="UiFocus"/>, which is what stops the
/// player walking or looking while they are open — <see cref="PlayerInput"/> reads neutral and
/// <c>FootPlayer</c> ignores mouse motion. The wheel reads the raw stick itself for that reason.
/// </para>
/// </summary>
public partial class InventoryUi : CanvasLayer
{
    private const int SlotPx = 50;
    private const int PanelSlotPx = 58;
    private const int TrashSlot = -2;
    private const double DoubleClickSeconds = 0.35;

    private readonly ItemController _items;
    private Inventory Inv => _items.Inventory;

    private Control _root = null!;
    private HBoxContainer _hotbar = null!;
    private readonly SlotButton[] _hotbarSlots = new SlotButton[Inventory.HotbarSize];
    private Label _heldName = null!;
    private float _heldNameTimer;
    private ItemId _lastHeld = (ItemId)(-1);
    private Label _toast = null!;
    private float _toastTimer;
    private PanelContainer _readoutPanel = null!;
    private Label _readout = null!;
    private ColorRect _flash = null!;
    private ColorRect _binoculars = null!;
    private ViewfinderView _viewfinder = null!;
    private Control _crosshair = null!;
    private Label _cashHud = null!;

    private Control _panel = null!;
    private readonly SlotButton[] _panelSlots = new SlotButton[Inventory.Size];
    private SlotButton _trash = null!;
    private Label _infoName = null!, _infoBlurb = null!, _infoValue = null!;
    private Button _useButton = null!, _handButton = null!, _claimButton = null!;
    private Label _cashLine = null!, _accountLine = null!, _controlsHint = null!, _hotbarCaption = null!;
    private int _inspect;
    private CarriedView _carried = null!;
    private PanelContainer _tooltip = null!;
    private Label _tooltipText = null!;

    // mouse state while the panel is open
    private Vector2 _cursor;
    private int _hover = -1;
    private MouseButton _paintButton = MouseButton.None;
    private (ItemStack[] Slots, ItemStack Carried) _paintSnapshot;
    private readonly List<int> _paintSlots = new();
    private int _pickedOnPress = -1;
    private int _lastClickSlot = -1;
    private double _lastClickTime;

    private WheelView _wheel = null!;
    private Vector2 _wheelAim;

    /// <summary>A local player is on screen at all (not the fly camera, not a replay).</summary>
    public bool PlayerPresent { get; set; }

    /// <summary>Items can be used right now (on foot). The hotbar is hidden otherwise.</summary>
    public bool ItemsActive { get; set; }

    /// <summary>Which optic overlay to draw, if Aim is held with one in hand.</summary>
    public ItemUse? Scope { get; set; }

    /// <summary>Breathing drift of the binocular overlay, in screen fractions.</summary>
    public Vector2 OpticSway { get; set; }

    /// <summary>The camera's 35 mm-equivalent focal length, shown in the viewfinder.</summary>
    public float PhotoFocalMm { get => _viewfinder.FocalMm; set => _viewfinder.FocalMm = value; }

    /// <summary>Text for the GPS panel, or null to hide it.</summary>
    public string? Readout { get; set; }

    public bool IsOpen => _panel.Visible;
    public bool WheelOpen => _wheel.Visible;

    public InventoryUi(ItemController items) => _items = items;

    public InventoryUi() : this(null!) { }

    public override void _Ready()
    {
        // over the feel HUD (4) and the lens (5) and the HUD (10), under the ride picker (30)
        Layer = 12;

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        BuildOverlays();
        BuildHud();
        BuildWheel();
        BuildPanel();

        // the cursor stack and its tooltip sit over everything else in the panel
        _carried = new CarriedView(this) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _carried.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_carried);
        BuildTooltip();

        PlayerInput.DeviceChanged += OnDeviceChanged;
        if (Bank.Instance is { } bank) bank.BalanceChanged += OnBalanceChanged;
        Refresh();
    }

    public override void _ExitTree()
    {
        PlayerInput.DeviceChanged -= OnDeviceChanged;
        if (Bank.Instance is { } bank) bank.BalanceChanged -= OnBalanceChanged;
    }

    // ------------------------------------------------------------------------------------
    // construction
    // ------------------------------------------------------------------------------------

    private void BuildOverlays()
    {
        _binoculars = new ColorRect
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            Material = new ShaderMaterial { Shader = new Shader { Code = BinocularShader } },
        };
        _binoculars.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_binoculars);

        _viewfinder = new ViewfinderView { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _viewfinder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_viewfinder);

        // the shotgun's bead: a small open ring at the screen centre, where the front bead sits
        _crosshair = new BeadReticle { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _crosshair.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_crosshair);

        _flash = new ColorRect
        {
            Color = new Color(1, 1, 1, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_flash);
    }

    private void BuildHud()
    {
        int width = Inventory.HotbarSize * SlotPx + (Inventory.HotbarSize - 1) * 6;
        _hotbar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hotbar.AddThemeConstantOverride("separation", 6);
        _hotbar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _hotbar.OffsetLeft = -width / 2f;
        _hotbar.OffsetRight = width / 2f;
        _hotbar.OffsetTop = -12 - SlotPx;
        _hotbar.OffsetBottom = -12;
        _root.AddChild(_hotbar);

        for (int i = 0; i < Inventory.HotbarSize; i++)
        {
            var slot = new SlotButton
            {
                Slot = i,
                KeyHint = (i + 1).ToString(),
                CustomMinimumSize = new Vector2(SlotPx, SlotPx),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                FocusMode = Control.FocusModeEnum.None,
            };
            _hotbarSlots[i] = slot;
            _hotbar.AddChild(slot);
        }

        // cash in the pocket, just right of the hotbar, where the eye already is
        _cashHud = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        _cashHud.AddThemeFontSizeOverride("font_size", 15);
        _cashHud.AddThemeColorOverride("font_color", new Color(0.98f, 0.84f, 0.38f));
        _cashHud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _cashHud.AddThemeConstantOverride("outline_size", 5);
        _cashHud.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _cashHud.OffsetLeft = width / 2f + 14;
        _cashHud.OffsetRight = width / 2f + 220;
        _cashHud.OffsetTop = -12 - SlotPx;
        _cashHud.OffsetBottom = -12;
        _root.AddChild(_cashHud);

        _heldName = CentredLabel(16, -12 - SlotPx - 28);
        _toast = CentredLabel(15, -12 - SlotPx - 56);
        _toast.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.7f));

        _readoutPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _readoutPanel.AddThemeStyleboxOverride("panel", PanelStyle(0.75f, 8));
        _readoutPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _readoutPanel.OffsetLeft = 18;
        _readoutPanel.OffsetTop = -96;
        _readoutPanel.OffsetBottom = -40;
        _readoutPanel.GrowVertical = Control.GrowDirection.Begin;
        _root.AddChild(_readoutPanel);

        _readout = new Label();
        _readout.AddThemeFontSizeOverride("font_size", 15);
        _readout.AddThemeColorOverride("font_color", new Color(0.7f, 1f, 0.75f));
        _readoutPanel.AddChild(_readout);
    }

    private Label CentredLabel(int size, float bottom)
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        label.AddThemeConstantOverride("outline_size", 5);
        label.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        label.OffsetLeft = -300;
        label.OffsetRight = 300;
        label.OffsetBottom = bottom;
        label.OffsetTop = bottom - 24;
        _root.AddChild(label);
        return label;
    }

    private void BuildWheel()
    {
        _wheel = new WheelView(this) { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _wheel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_wheel);
    }

    private void BuildPanel()
    {
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(centre);

        var panel = new PanelContainer { Visible = false };
        panel.AddThemeStyleboxOverride("panel", PanelStyle(0.94f, 20));
        centre.AddChild(panel);
        _panel = panel;

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 20);
        panel.AddChild(columns);

        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 8);
        columns.AddChild(left);

        var title = new Label { Text = "Inventory" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        left.AddChild(title);

        _hotbarCaption = Caption("");
        left.AddChild(_hotbarCaption);
        left.AddChild(SlotGrid(0, Inventory.HotbarSize));
        left.AddChild(Caption("Backpack"));
        left.AddChild(SlotGrid(Inventory.HotbarSize, Inventory.BackpackSize));

        _controlsHint = Caption("");
        _controlsHint.AutowrapMode = TextServer.AutowrapMode.Off;
        left.AddChild(_controlsHint);

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(230, 0) };
        right.AddThemeConstantOverride("separation", 8);
        columns.AddChild(right);

        _infoName = new Label();
        _infoName.AddThemeFontSizeOverride("font_size", 18);
        right.AddChild(_infoName);

        _infoBlurb = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(230, 0) };
        _infoBlurb.AddThemeFontSizeOverride("font_size", 13);
        _infoBlurb.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.8f));
        right.AddChild(_infoBlurb);

        _infoValue = new Label();
        _infoValue.AddThemeFontSizeOverride("font_size", 12);
        _infoValue.AddThemeColorOverride("font_color", new Color(0.62f, 0.58f, 0.44f));
        right.AddChild(_infoValue);

        _useButton = new Button { Text = "Use" };
        _useButton.Pressed += () => _items.UseSlot(null, _inspect);
        right.AddChild(_useButton);

        _handButton = new Button { Text = "Take in hand" };
        _handButton.Pressed += TakeInHand;
        right.AddChild(_handButton);

        // the Polaroids: every photo in the pack and every one taken here (PhotoUi)
        var albumButton = new Button { Text = "Photo album" };
        albumButton.Pressed += () => _items.PhotoUi.OpenAlbum();
        right.AddChild(albumButton);

        // the bin: drop a stack on it to throw it away; click it empty-handed to get it back
        var binRow = new HBoxContainer();
        binRow.AddThemeConstantOverride("separation", 10);
        _trash = new SlotButton
        {
            Slot = TrashSlot, KeyHint = "", IsTrash = true,
            CustomMinimumSize = new Vector2(PanelSlotPx, PanelSlotPx),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _trash.Pressed += () => ClickTrash();
        binRow.AddChild(_trash);
        var binText = Caption("Bin: drop a stack here to throw it away.\nClick it again to take the last one back.");
        binText.VerticalAlignment = VerticalAlignment.Center;
        binRow.AddChild(binText);
        right.AddChild(binRow);

        right.AddChild(new HSeparator());

        // money: what is in your pocket, what is safe, and the button between the two
        var money = new Label { Text = "Money" };
        money.AddThemeFontSizeOverride("font_size", 16);
        money.AddThemeColorOverride("font_color", new Color(0.98f, 0.84f, 0.38f));
        right.AddChild(money);
        _cashLine = new Label();
        _cashLine.AddThemeFontSizeOverride("font_size", 14);
        right.AddChild(_cashLine);
        _accountLine = new Label();
        _accountLine.AddThemeFontSizeOverride("font_size", 14);
        _accountLine.AddThemeColorOverride("font_color", new Color(0.7f, 0.86f, 0.72f));
        right.AddChild(_accountLine);
        _claimButton = new Button();
        _claimButton.Pressed += () => Bank.Instance?.ClaimAll();
        right.AddChild(_claimButton);
        var moneyHint = Caption("Cash you carry is lost if you are knocked out.\nClaimed money is safe in your account.");
        right.AddChild(moneyHint);
    }

    private void BuildTooltip()
    {
        _tooltip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _tooltip.AddThemeStyleboxOverride("panel", PanelStyle(0.96f, 8));
        _tooltipText = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(220, 0) };
        _tooltipText.AddThemeFontSizeOverride("font_size", 13);
        _tooltip.AddChild(_tooltipText);
        _root.AddChild(_tooltip);
    }

    private static Label Caption(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        return label;
    }

    private GridContainer SlotGrid(int first, int count)
    {
        var grid = new GridContainer { Columns = Inventory.HotbarSize };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        for (int i = first; i < first + count; i++)
        {
            int slot = i;
            var button = new SlotButton
            {
                Slot = slot,
                KeyHint = slot < Inventory.HotbarSize ? (slot + 1).ToString() : "",
                CustomMinimumSize = new Vector2(PanelSlotPx, PanelSlotPx),
                // the mouse is hit-tested by the panel (see the class notes); focus is for the pad
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            // ui_accept on a focused slot: the pad's A, the keyboard's Enter
            button.Pressed += () => { Inv.PrimaryClick(slot); Inspect(slot); };
            button.FocusEntered += () => Inspect(slot);
            _panelSlots[slot] = button;
            grid.AddChild(button);
        }
        return grid;
    }

    private static StyleBoxFlat PanelStyle(float alpha, int margin)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, alpha),
            ContentMarginLeft = margin, ContentMarginRight = margin,
            ContentMarginTop = margin * 0.8f, ContentMarginBottom = margin * 0.8f,
        };
        style.SetCornerRadiusAll(6);
        return style;
    }

    // ------------------------------------------------------------------------------------
    // state
    // ------------------------------------------------------------------------------------

    /// <summary>Redraws every slot from the inventory. Called on every inventory change.</summary>
    public void Refresh()
    {
        if (_panel == null) return;
        for (int i = 0; i < Inventory.HotbarSize; i++)
            _hotbarSlots[i].Display(Inv[i], i == Inv.Selected, false);
        for (int i = 0; i < Inventory.Size; i++)
        {
            _panelSlots[i].Hot = i == _hover;
            _panelSlots[i].Display(Inv[i], i == Inv.Selected, _paintSlots.Contains(i));
        }
        _trash.Hot = _hover == TrashSlot;
        _trash.Display(Inv.Trashed, false, false);
        Inspect(_inspect);
        _wheel.QueueRedraw();
        _carried.QueueRedraw();
        RefreshMoney();

        // the name of what just came into the hand, briefly
        if (Inv.HeldId != _lastHeld)
        {
            _lastHeld = Inv.HeldId;
            _heldName.Text = ItemDefs.Get(Inv.HeldId)?.Name ?? "Empty hand";
            _heldNameTimer = 1.8f;
        }
    }

    private void RefreshMoney()
    {
        long balance = Bank.Instance?.Balance ?? 0;
        bool pending = Bank.Instance?.Pending == true;
        _cashLine.Text = $"Cash on you:  {Chf(Inv.Cash)}";
        _accountLine.Text = $"Account:  {Chf(balance)}";
        _claimButton.Text = pending ? "Claiming…" : Inv.Cash > 0 ? $"Claim {Chf(Inv.Cash)} to account" : "No cash to claim";
        _claimButton.Disabled = pending || Inv.Cash <= 0 || Bank.Instance == null;
        _cashHud.Text = Inv.Cash > 0 ? $"{Chf(Inv.Cash)}\n{InputHints.Tag(PlayerInput.Inventory)} claim" : "";
    }

    private static string Chf(long amount) =>
        amount.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", "'") + " CHF";

    /// <summary>The key reference under the slots, for the device in hand.</summary>
    private void OnDeviceChanged()
    {
        _hotbarCaption.Text = $"Hotbar — in reach ({InputHints.Label(PlayerInput.NextItem)} / 1–6 to pick)";
        _controlsHint.Text = PlayerInput.LastDevice == InputDevice.Gamepad
            ? "(A) pick up / put down   (X) take half / put one   (Y) send across\n"
              + $"(B) put back, then close   {InputHints.Label(PlayerInput.Inventory)} close"
            : "LMB pick up / put down   RMB take half / put one   Shift+LMB send across\n"
              + "Drag to move or to spread a stack   Double-click gather   1–6 over a slot: swap into hotbar\n"
              + $"{InputHints.Label(PlayerInput.Inventory)} / Esc close   {InputHints.Label(PlayerInput.QuickWheel)} (hold) quick wheel";
        _useButton.Text = PlayerInput.LastDevice == InputDevice.Gamepad ? "Use" : "Use  [MMB]";
        RefreshMoney();
    }

    private void OnBalanceChanged(long deposited)
    {
        if (deposited > 0) Toast($"+{Chf(deposited)} claimed — account {Chf(Bank.Instance?.Balance ?? 0)}");
        RefreshMoney();
    }

    private void Inspect(int slot)
    {
        if (slot < 0) return;
        _inspect = slot;
        var stack = Inv[slot];
        var def = stack.IsEmpty ? null : ItemDefs.Get(stack.Id);
        _infoName.Text = def == null ? "Empty slot" : def.MaxStack > 1 ? $"{def.Name}  ×{stack.Count}" : def.Name;
        _infoName.AddThemeColorOverride("font_color", def?.Tint.Lightened(0.35f) ?? new Color(0.6f, 0.6f, 0.6f));
        _infoBlurb.Text = def != null ? InputHints.Format(def.Blurb)
            : slot < Inventory.HotbarSize ? "Hotbar slot — whatever is here can be in your hand." : "Backpack slot.";
        _infoValue.Text = def is { Value: > 0 } ? $"Worth about {def.Value * stack.Count:0.#} CHF" : "";
        _useButton.Disabled = def?.Use is not (ItemUse.Consume or ItemUse.Wear or ItemUse.Print);
        _handButton.Disabled = def == null || slot == Inv.Selected;
    }

    /// <summary>A backpack item is swapped into the selected hotbar slot; a hotbar item just becomes the selection.</summary>
    private void TakeInHand()
    {
        if (_inspect < Inventory.HotbarSize) Inv.Select(_inspect);
        else Inv.Move(_inspect, Inv.Selected);
        Inspect(_inspect);
    }

    private void ClickTrash()
    {
        if (!Inv.Carried.IsEmpty) Inv.Trash();
        else Inv.Untrash();
    }

    public void Toast(string text)
    {
        _toast.Text = text;
        _toastTimer = 2.6f;
    }

    public void Flash() => _flash.Color = new Color(1, 1, 1, 0.85f);

    public void Open()
    {
        if (IsOpen) return;
        CloseWheel(false);
        EndPaint(commit: false);
        _panel.Visible = true;
        OnDeviceChanged();
        Refresh();
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        PlayerInput.FocusFirst(_panel);
    }

    public void Close()
    {
        if (!IsOpen) return;
        EndPaint(commit: true);
        _panel.Visible = false;
        _tooltip.Visible = false;
        _hover = -1;
        // Minecraft throws a stack held on closing to the ground; here it goes back in the pack
        Inv.ReturnCarried();
        UiFocus.Set(this, false);
        Core.MouseCapture.Capture();
        Refresh();
    }

    private void OpenWheel()
    {
        _wheelAim = Vector2.Zero;
        _wheel.Highlight = -1;
        _wheel.Visible = true;
        UiFocus.Set(_wheel, true);
    }

    private void CloseWheel(bool choose)
    {
        if (!_wheel.Visible) return;
        _wheel.Visible = false;
        UiFocus.Set(_wheel, false);
        if (choose && _wheel.Highlight >= 0) Inv.Select(_wheel.Highlight);
    }

    // ------------------------------------------------------------------------------------
    // the panel's mouse
    // ------------------------------------------------------------------------------------

    /// <summary>The slot under a point, <see cref="TrashSlot"/> for the bin, or -1.</summary>
    private int SlotAt(Vector2 point)
    {
        for (int i = 0; i < Inventory.Size; i++)
            if (_panelSlots[i].GetGlobalRect().HasPoint(point)) return i;
        return _trash.GetGlobalRect().HasPoint(point) ? TrashSlot : -1;
    }

    /// <summary>Where the carried stack is drawn: at the pointer, or on the focused slot with a pad.</summary>
    public Vector2 CarriedAt =>
        PlayerInput.LastDevice == InputDevice.Gamepad && GetViewport().GuiGetFocusOwner() is SlotButton focused
            ? focused.GetGlobalRect().GetCenter() + new Vector2(10, 10)
            : _cursor;

    public ItemStack CarriedStack => IsOpen ? Inv.Carried : ItemStack.Empty;

    private bool HandlePanelMouse(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion m:
            {
                _cursor = m.Position;
                int hover = SlotAt(m.Position);
                if (hover != _hover)
                {
                    _hover = hover;
                    if (hover >= 0) Inspect(hover);
                    if (_paintButton != MouseButton.None && hover >= 0 && !_paintSlots.Contains(hover)) AddPaint(hover);
                    Refresh();
                }
                _carried.QueueRedraw();
                return false;   // motion is never consumed: the buttons need their hover
            }

            case InputEventMouseButton { Pressed: true } b when b.ButtonIndex is MouseButton.Left or MouseButton.Right or MouseButton.Middle:
            {
                int slot = SlotAt(b.Position);
                if (slot == -1) return false;
                if (slot == TrashSlot)
                {
                    if (b.ButtonIndex == MouseButton.Left) ClickTrash();
                    return true;
                }
                if (b.ButtonIndex == MouseButton.Middle)
                {
                    _items.UseSlot(null, slot);
                    return true;
                }

                bool left = b.ButtonIndex == MouseButton.Left;
                double now = Time.GetTicksMsec() / 1000.0;
                bool doubleClick = left && slot == _lastClickSlot && now - _lastClickTime < DoubleClickSeconds;
                _lastClickSlot = slot;
                _lastClickTime = now;

                if (left && b.ShiftPressed && Inv.Carried.IsEmpty) Inv.QuickMove(slot);
                else if (doubleClick && !Inv.Carried.IsEmpty)
                {
                    EndPaint(commit: false);
                    Inv.Collect();
                }
                else if (Inv.Carried.IsEmpty)
                {
                    if (left) Inv.PrimaryClick(slot);
                    else Inv.SecondaryClick(slot);
                    // released over another slot, this becomes a drag and drop
                    _pickedOnPress = Inv.Carried.IsEmpty ? -1 : slot;
                }
                else
                {
                    // a stack on the cursor: what this press does is decided on release, since a
                    // drag across slots spreads it where a click would put it all in one
                    _paintButton = b.ButtonIndex;
                    _paintSnapshot = Inv.Snapshot();
                    _paintSlots.Clear();
                    _paintSlots.Add(slot);
                    Refresh();
                }
                Inspect(slot);
                return true;
            }

            case InputEventMouseButton { Pressed: false } b when b.ButtonIndex is MouseButton.Left or MouseButton.Right:
            {
                int slot = SlotAt(b.Position);
                if (_paintButton == b.ButtonIndex)
                {
                    EndPaint(commit: true);
                    return true;
                }
                if (_pickedOnPress >= 0)
                {
                    int from = _pickedOnPress;
                    _pickedOnPress = -1;
                    if (slot == TrashSlot) Inv.Trash();
                    else if (slot >= 0 && slot != from) Inv.PrimaryClick(slot);   // dropped on another slot
                    return slot != -1;
                }
                return false;
            }
        }
        return false;
    }

    private void AddPaint(int slot)
    {
        _paintSlots.Add(slot);
        using (Inv.Batch())
        {
            Inv.Restore(_paintSnapshot);
            Inv.Distribute(_paintSlots, oneEach: _paintButton == MouseButton.Right);
        }
    }

    /// <summary>
    /// Ends a press that started with a stack on the cursor. Over a single slot it was a click;
    /// over several, the spread already applied as the pointer crossed them stays.
    /// </summary>
    private void EndPaint(bool commit)
    {
        if (_paintButton == MouseButton.None) return;
        var button = _paintButton;
        _paintButton = MouseButton.None;
        if (commit && _paintSlots.Count == 1)
        {
            int slot = _paintSlots[0];
            if (button == MouseButton.Left) Inv.PrimaryClick(slot);
            else Inv.SecondaryClick(slot);
        }
        _paintSlots.Clear();
        Refresh();
    }

    private void UpdateTooltip()
    {
        bool show = IsOpen && _hover >= 0 && Inv.Carried.IsEmpty && PlayerInput.LastDevice != InputDevice.Gamepad
                    && !Inv[_hover].IsEmpty && ItemDefs.Get(Inv[_hover].Id) is not null;
        _tooltip.Visible = show;
        if (!show) return;
        var stack = Inv[_hover];
        var def = ItemDefs.Get(stack.Id)!;
        string count = def.MaxStack > 1 ? $"  ×{stack.Count}" : "";
        string worth = def.Value > 0 ? $"\n{def.Value * stack.Count:0.#} CHF" : "";
        string swap = _hover >= Inventory.HotbarSize ? "\n1–6 swap into hotbar · Shift+click to hotbar" : "\nShift+click to backpack";
        _tooltipText.Text = $"{def.Name}{count}\n{InputHints.Format(def.Blurb)}{worth}{swap}";
        _tooltip.ResetSize();
        var size = _tooltip.Size;
        var at = _cursor + new Vector2(18, 18);
        var view = _root.Size;
        if (at.X + size.X > view.X) at.X = _cursor.X - size.X - 12;
        if (at.Y + size.Y > view.Y) at.Y = view.Y - size.Y - 4;
        _tooltip.Position = at;
    }

    // ------------------------------------------------------------------------------------
    // input
    // ------------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (_items.PhotoUi.Blocking) return;   // the album or a photo is over the panel
        if (IsOpen)
        {
            if (!e.IsPressed() || e.IsEcho()) return;
            if (e.IsActionPressed("ui_cancel") && !Inv.Carried.IsEmpty)
                Inv.ReturnCarried();   // B first puts the carried stack back, then closes
            else if (e.IsActionPressed(PlayerInput.Inventory) || e.IsActionPressed(PlayerInput.Menu)
                     || e.IsActionPressed("ui_cancel"))
                Close();
            else if (e is InputEventJoypadButton pad && GetViewport().GuiGetFocusOwner() is SlotButton { Slot: >= 0 } focused)
            {
                // A is ui_accept, which presses the focused slot; X and Y are the other two clicks
                if (pad.ButtonIndex == JoyButton.X) Inv.SecondaryClick(focused.Slot);
                else if (pad.ButtonIndex == JoyButton.Y) Inv.QuickMove(focused.Slot);
                else return;
                Inspect(focused.Slot);
            }
            else return;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (WheelOpen || UiFocus.TextEntryActive || !e.IsPressed() || e.IsEcho()) return;
        if (_items.UsablePlayer == null) return;

        if (e.IsActionPressed(PlayerInput.Inventory))
        {
            Open();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed(PlayerInput.QuickWheel))
        {
            OpenWheel();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Input(InputEvent e)
    {
        if (_items.PhotoUi.Blocking) return;
        if (IsOpen)
        {
            if (HandlePanelMouse(e)) GetViewport().SetInputAsHandled();
            // a number key over a slot swaps it with that hotbar slot, as in Minecraft
            else if (e is InputEventKey { Pressed: true, Echo: false } k && _hover >= 0
                     && (int)k.PhysicalKeycode - (int)Key.Key1 is var n && n >= 0 && n < Inventory.HotbarSize)
            {
                Inv.SwapWithHotbar(_hover, n);
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (!WheelOpen) return;

        switch (e)
        {
            case InputEventMouseMotion m:
                // the pointer stays captured; its motion pushes a virtual stick instead
                _wheelAim = (_wheelAim + m.Relative / 90f).LimitLength(1.2f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                CloseWheel(true);
                break;
            case InputEventKey { Pressed: true } key
                when (int)key.PhysicalKeycode - (int)Key.Key1 is var n && n >= 0 && n < Inventory.HotbarSize:
                _wheel.Highlight = n;
                CloseWheel(true);
                break;
            default:
                if (e.IsActionReleased(PlayerInput.QuickWheel)) CloseWheel(true);
                else if (e.IsActionPressed(PlayerInput.Menu) || e.IsActionPressed("ui_cancel")) CloseWheel(false);
                else return;
                break;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // a mode change or a mount under an open menu takes it away
        if (!ItemsActive)
        {
            Close();
            CloseWheel(false);
        }

        _hotbar.Visible = ItemsActive && !IsOpen && Scope == null;
        _cashHud.Visible = _hotbar.Visible;
        _readoutPanel.Visible = Readout != null && !IsOpen;
        if (Readout != null) _readout.Text = Readout;

        _binoculars.Visible = Scope == ItemUse.Optic;
        _viewfinder.Visible = Scope == ItemUse.Photo;
        _crosshair.Visible = false;   // no reticle for the shotgun: the barrel is the aim
        if (_binoculars.Visible && _binoculars.Material is ShaderMaterial sm)
        {
            sm.SetShaderParameter("aspect", _root.Size.X / Mathf.Max(1f, _root.Size.Y));
            sm.SetShaderParameter("sway", OpticSway);
        }

        _heldNameTimer -= dt;
        _heldName.Visible = ItemsActive && !IsOpen;
        _heldName.Modulate = new Color(1, 1, 1, Mathf.Clamp(_heldNameTimer / 0.4f, 0f, 1f));
        _toastTimer -= dt;
        _toast.Visible = PlayerPresent;
        _toast.Modulate = new Color(1, 1, 1, Mathf.Clamp(_toastTimer / 0.5f, 0f, 1f));
        if (_flash.Color.A > 0) _flash.Color = new Color(1, 1, 1, Mathf.MoveToward(_flash.Color.A, 0f, dt * 3f));

        UpdateTooltip();
        if (IsOpen && PlayerInput.LastDevice == InputDevice.Gamepad) _carried.QueueRedraw();

        if (WheelOpen)
        {
            // the right stick aims directly; it has no captured-pointer drift to accumulate
            var stick = Input.GetVector(PlayerInput.LookLeft, PlayerInput.LookRight,
                PlayerInput.LookUp, PlayerInput.LookDown);
            if (stick.Length() > 0.5f) _wheelAim = stick;
            int before = _wheel.Highlight;
            _wheel.Highlight = _wheelAim.Length() < 0.35f
                ? before
                : Mathf.PosMod((int)Mathf.Round(Mathf.Atan2(_wheelAim.X, -_wheelAim.Y) / (Mathf.Tau / Inventory.HotbarSize)),
                    Inventory.HotbarSize);
            if (_wheel.Highlight != before) _wheel.QueueRedraw();
        }
    }

    public ItemStack WheelSlot(int i) => Inv[i];
    public int SelectedSlot => Inv.Selected;

    // ------------------------------------------------------------------------------------
    // drawing
    // ------------------------------------------------------------------------------------

    /// <summary>Two overlapping circles of view and black round them — the binocular picture everyone recognises.</summary>
    private const string BinocularShader = @"
shader_type canvas_item;
uniform float aspect = 1.777;
uniform vec2 sway = vec2(0.0);
void fragment() {
    vec2 p = (UV - 0.5 - sway) * vec2(aspect, 1.0);
    float r = 0.42;
    float d = min(length(p - vec2(-0.24, 0.0)), length(p - vec2(0.24, 0.0)));
    float a = smoothstep(r - 0.012, r + 0.004, d);
    // a faint dark rim just inside the glass
    a = max(a, smoothstep(r - 0.06, r, d) * 0.35);
    COLOR = vec4(0.0, 0.0, 0.0, a);
}";
}

/// <summary>One inventory slot: an icon swatch with a two-letter glyph, a count and a key hint.</summary>
public partial class SlotButton : Button
{
    public int Slot;
    public string KeyHint = "";
    /// <summary>Under the pointer. Set by the panel, which hit-tests the mouse itself.</summary>
    public bool Hot;
    /// <summary>The bin, drawn with its own mark when empty.</summary>
    public bool IsTrash;
    private ItemStack _stack;
    private bool _selected, _picked;

    public override void _Ready()
    {
        Flat = true;
        AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        MouseEntered += QueueRedraw;
        MouseExited += QueueRedraw;
        FocusEntered += QueueRedraw;
        FocusExited += QueueRedraw;
    }

    public void Display(ItemStack stack, bool selected, bool picked)
    {
        _stack = stack;
        _selected = selected;
        _picked = picked;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        SlotDrawing.DrawSlot(this, r, _stack, KeyHint, _selected, _picked, Hot || IsHovered() || HasFocus());
        if (IsTrash && _stack.IsEmpty)
            DrawString(ThemeDB.FallbackFont, new Vector2(0, r.Size.Y * 0.62f), "BIN",
                HorizontalAlignment.Center, r.Size.X, 13, new Color(0.55f, 0.35f, 0.32f));
    }
}

/// <summary>The stack riding on the cursor while the inventory is open, drawn over everything.</summary>
public partial class CarriedView : Control
{
    private readonly InventoryUi _ui;

    public CarriedView(InventoryUi ui) => _ui = ui;
    public CarriedView() : this(null!) { }

    public override void _Draw()
    {
        var stack = _ui.CarriedStack;
        if (stack.IsEmpty) return;
        const float size = 50f;
        var at = _ui.CarriedAt - new Vector2(size * 0.5f, size * 0.5f);
        SlotDrawing.DrawSlot(this, new Rect2(at, new Vector2(size, size)), stack, "", false, true, false);
    }
}

public static class SlotDrawing
{
    /// <summary>Shared by the slots and the wheel so an item looks the same everywhere it appears.</summary>
    public static void DrawSlot(CanvasItem c, Rect2 r, ItemStack stack, string keyHint,
        bool selected, bool picked, bool hot)
    {
        var font = ThemeDB.FallbackFont;
        c.DrawRect(r, new Color(0.04f, 0.05f, 0.07f, 0.78f));

        if (!stack.IsEmpty && ItemDefs.Get(stack.Id) is { } def)
        {
            var icon = ItemIcons.Get(stack.Id);
            // a photo shows its own print, a thumbnail drawn 1:1
            if (stack.Id == ItemId.Photo && PhotoStore.Thumbnail(stack.Data) is { } thumb)
            {
                var size = thumb.GetSize();
                float k = Mathf.Min(1f, r.Size.Y * 0.86f / size.Y);
                c.DrawTextureRect(thumb, new Rect2((r.GetCenter() - size * k * 0.5f).Round(), size * k), false);
            }
            else if (icon != null)
            {
                if (c.TextureFilter != CanvasItem.TextureFilterEnum.Nearest)
                    c.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
                float avail = Mathf.Min(r.Size.X, r.Size.Y) * 0.8f;
                float px = avail >= ItemIcons.Size ? Mathf.Floor(avail / ItemIcons.Size) : avail / ItemIcons.Size;
                var size = new Vector2(ItemIcons.Size, ItemIcons.Size) * px;
                var at = (r.GetCenter() - size * 0.5f).Round();
                c.DrawTextureRect(icon, new Rect2(at, size), false);
            }
            else
            {
                var inner = r.Grow(-r.Size.X * 0.18f);
                c.DrawRect(inner, def.Tint);
                c.DrawRect(inner, def.Tint.Lightened(0.4f), false, 1.5f);
                int glyphSize = (int)(r.Size.Y * 0.30f);
                c.DrawString(font, new Vector2(inner.Position.X, inner.GetCenter().Y + glyphSize * 0.36f), def.Glyph,
                    HorizontalAlignment.Center, inner.Size.X, glyphSize, Colors.White);
            }
            if (def.MaxStack > 1)
            {
                int countSize = (int)(r.Size.Y * 0.26f);
                c.DrawString(font, new Vector2(r.Position.X, r.End.Y - 3), stack.Count.ToString(),
                    HorizontalAlignment.Right, r.Size.X - 4, countSize, new Color(1f, 1f, 0.85f));
            }
        }

        if (keyHint.Length > 0)
            c.DrawString(font, r.Position + new Vector2(4, 12), keyHint,
                HorizontalAlignment.Left, -1, 11, new Color(0.65f, 0.68f, 0.72f));

        var border = picked ? new Color(0.3f, 0.85f, 1f)
            : selected ? new Color(0.98f, 0.72f, 0.10f)
            : hot ? new Color(0.9f, 0.9f, 0.9f)
            : new Color(0.25f, 0.28f, 0.33f);
        c.DrawRect(r.Grow(-1), border, false, selected || picked ? 3f : 1.5f);
    }
}

/// <summary>The radial quick menu: the six hotbar slots round a circle, slot 1 at the top, clockwise.</summary>
public partial class WheelView : Control
{
    private readonly InventoryUi _ui;
    public int Highlight = -1;

    public WheelView(InventoryUi ui) => _ui = ui;
    public WheelView() : this(null!) { }

    public override void _Draw()
    {
        var centre = Size * 0.5f;
        const float radius = 120f, slot = 60f;
        DrawCircle(centre, radius + slot * 0.8f, new Color(0, 0, 0, 0.45f));

        for (int i = 0; i < Inventory.HotbarSize; i++)
        {
            float a = Mathf.Tau * i / Inventory.HotbarSize;
            var at = centre + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * radius;
            float size = i == Highlight ? slot * 1.2f : slot;
            var rect = new Rect2(at - Vector2.One * size * 0.5f, Vector2.One * size);
            SlotDrawing.DrawSlot(this, rect, _ui.WheelSlot(i), (i + 1).ToString(),
                i == _ui.SelectedSlot, false, i == Highlight);
        }

        int shown = Highlight >= 0 ? Highlight : _ui.SelectedSlot;
        var stack = _ui.WheelSlot(shown);
        string name = stack.IsEmpty ? "Empty hand" : ItemDefs.Get(stack.Id)?.Name ?? "";
        DrawString(ThemeDB.FallbackFont, centre + new Vector2(-100, 6), name,
            HorizontalAlignment.Center, 200, 17, Colors.White);
    }
}

/// <summary>
/// A camera's viewfinder: thirds grid, corner brackets, focal length readout with a zoom scale,
/// an autofocus brace that hunts after every zoom change, and shots / time / battery at the corners.
/// </summary>
/// <summary>The shotgun's aiming dot: a thin dark-edged ring around the centre, small enough to leave the front bead visible.</summary>
public partial class BeadReticle : Control
{
    public override void _Draw()
    {
        var c = Size / 2f;
        DrawArc(c, 5.5f, 0f, Mathf.Tau, 28, new Color(0, 0, 0, 0.55f), 3.5f, true);
        DrawArc(c, 5.5f, 0f, Mathf.Tau, 28, new Color(1f, 0.92f, 0.6f, 0.95f), 1.6f, true);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }
}

public partial class ViewfinderView : Control
{
    public const float Min = 24f, Max = 200f;
    private float _focal = 35f;
    private double _hunt;          // seconds of autofocus hunt left
    private int _shots;
    private double _recount = 99;

    public float FocalMm
    {
        get => _focal;
        set
        {
            if (Mathf.Abs(value - _focal) < 0.01f) return;
            _focal = value;
            _hunt = 0.35;
            QueueRedraw();
        }
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) { _recount = 99; return; }
        _hunt = System.Math.Max(0, _hunt - delta);
        _recount += delta;
        if (_recount > 1.0)
        {
            _recount = 0;
            using var d = DirAccess.Open("user://photos");
            _shots = d?.GetFiles().Length ?? 0;
        }
        QueueRedraw();   // the clock and the hunt animate
    }

    private static string Fmt(float v, string f) => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);

    public override void _Draw()
    {
        var s = Size;
        var font = ThemeDB.FallbackFont;
        var line = new Color(1, 1, 1, 0.85f);
        var faint = new Color(1, 1, 1, 0.18f);
        float inset = s.Y * 0.08f, arm = s.Y * 0.07f;

        // the print keeps the centre square of the frame (PhotoStore.Print): dim what it crops
        float side = Mathf.Min(s.X, s.Y);
        var keep = new Rect2((s - Vector2.One * side) * 0.5f, Vector2.One * side);
        var dim = new Color(0, 0, 0, 0.35f);
        if (s.X > side)
        {
            DrawRect(new Rect2(0, 0, keep.Position.X, s.Y), dim);
            DrawRect(new Rect2(keep.End.X, 0, s.X - keep.End.X, s.Y), dim);
        }
        else if (s.Y > side)
        {
            DrawRect(new Rect2(0, 0, s.X, keep.Position.Y), dim);
            DrawRect(new Rect2(0, keep.End.Y, s.X, s.Y - keep.End.Y), dim);
        }
        DrawRect(keep, new Color(1, 1, 1, 0.35f), false, 1f);

        for (int k = 1; k <= 2; k++)
        {
            DrawLine(new Vector2(s.X * k / 3f, inset), new Vector2(s.X * k / 3f, s.Y - inset), faint, 1f);
            DrawLine(new Vector2(inset, s.Y * k / 3f), new Vector2(s.X - inset, s.Y * k / 3f), faint, 1f);
        }

        foreach (var (x, dx) in new[] { (inset, 1f), (s.X - inset, -1f) })
        foreach (var (y, dy) in new[] { (inset, 1f), (s.Y - inset, -1f) })
        {
            DrawLine(new Vector2(x, y), new Vector2(x + arm * dx, y), line, 2f);
            DrawLine(new Vector2(x, y), new Vector2(x, y + arm * dy), line, 2f);
        }

        // autofocus: the brace starts wide and shrinks onto the subject, green once locked
        var c = s * 0.5f;
        float t = (float)(_hunt / 0.35);
        bool locked = _hunt <= 0;
        float half = 22f + (locked ? 0f : t * 26f * (0.6f + 0.4f * Mathf.Sin((float)Time.GetTicksMsec() * 0.04f)));
        var af = locked ? new Color(0.35f, 1f, 0.35f, 0.95f) : new Color(1f, 0.85f, 0.3f, 0.95f);
        float a = 9f;
        foreach (var (sx, sy) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
        {
            var p = c + new Vector2(sx * half, sy * half);
            DrawLine(p, p + new Vector2(-sx * a, 0), af, 2f);
            DrawLine(p, p + new Vector2(0, -sy * a), af, 2f);
        }
        DrawRect(new Rect2(c - Vector2.One, Vector2.One * 2f), line);

        // focal length and relative zoom, top centre
        DrawString(font, new Vector2(0, inset + 24), Fmt(_focal, "F0") + "mm   x" + Fmt(_focal / 24f, "F1"),
            HorizontalAlignment.Center, s.X, 20, Colors.White);

        // zoom scale down the right side: ticks at the classic stops, a marker at the focal length
        float sx0 = s.X - inset - 40f, top = s.Y * 0.3f, bot = s.Y * 0.7f;
        float Y(float mm) => bot - (bot - top) * Mathf.Log(mm / Min) / Mathf.Log(Max / Min);
        DrawLine(new Vector2(sx0, top), new Vector2(sx0, bot), line, 1.5f);
        foreach (float mm in new[] { 24f, 28f, 35f, 50f, 70f, 85f, 105f, 135f, 200f })
        {
            float y = Y(mm);
            bool major = mm is 24f or 35f or 70f or 135f or 200f;
            DrawLine(new Vector2(sx0, y), new Vector2(sx0 + (major ? 10 : 6), y), line, 1.5f);
            if (major)
                DrawString(font, new Vector2(sx0 + 14, y + 5), Fmt(mm, "F0"), HorizontalAlignment.Left, -1, 12, line);
        }
        float my = Y(Mathf.Clamp(_focal, Min, Max));
        DrawColoredPolygon(new[] { new Vector2(sx0 - 2, my), new Vector2(sx0 - 12, my - 6), new Vector2(sx0 - 12, my + 6) }, af);

        // bottom row: shots taken, time stamp, battery; key hint on top
        float by = s.Y - inset + 22;
        DrawString(font, new Vector2(inset, by), "SHOTS " + _shots, HorizontalAlignment.Left, -1, 14, line);
        DrawString(font, new Vector2(0, by),
            System.DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            HorizontalAlignment.Center, s.X, 14, line);
        DrawString(font, new Vector2(inset, inset - 10), "Use: shoot   Wheel: zoom", HorizontalAlignment.Left, -1, 14, line);
        var bat = new Rect2(s.X - inset - 30, by - 11, 26, 12);
        DrawRect(bat, line, false, 1.5f);
        DrawRect(new Rect2(bat.End.X, bat.Position.Y + 3, 3, 6), line);
        for (int i = 0; i < 3; i++) DrawRect(new Rect2(bat.Position.X + 2 + i * 8, bat.Position.Y + 2, 6, 8), line);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }
}
