using Godot;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>
/// Everything the inventory puts on screen: the hotbar, the full inventory panel (K / Back),
/// the radial quick wheel (hold X / D-pad left), the optic overlays and the GPS readout.
///
/// <para>
/// <b>Hotbar</b>: the six slots at the bottom are what can be in your hand. 1–6, the mouse
/// wheel, or D-pad right pick one. <b>Quick wheel</b>: the same six slots laid out round the
/// centre of the screen, chosen by pushing the mouse or right stick toward one and letting go —
/// the way to swap on a pad without cycling past everything. <b>Inventory</b>: the hotbar plus
/// an 18-slot pack. Click (or A) picks a stack up, click another slot to put it there — same
/// items merge, different ones swap. Right-click or "Use" eats and drinks from any slot.
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
    private Label _crosshair = null!;

    private Control _panel = null!;
    private readonly SlotButton[] _panelSlots = new SlotButton[Inventory.Size];
    private Label _infoName = null!, _infoBlurb = null!;
    private Button _useButton = null!, _handButton = null!;
    private int _picked = -1;
    private int _inspect;

    private WheelView _wheel = null!;
    private Vector2 _wheelAim;

    /// <summary>A local player is on screen at all (not the fly camera, not a replay).</summary>
    public bool PlayerPresent { get; set; }

    /// <summary>Items can be used right now (on foot). The hotbar is hidden otherwise.</summary>
    public bool ItemsActive { get; set; }

    /// <summary>Which optic overlay to draw, if Aim is held with one in hand.</summary>
    public ItemUse? Scope { get; set; }

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
        Refresh();
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

        // the shotgun's bead: a plain centred cross
        _crosshair = new Label
        {
            Text = "+", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _crosshair.AddThemeFontSizeOverride("font_size", 28);
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

        left.AddChild(Caption("Hotbar — in reach (1–6)"));
        left.AddChild(SlotGrid(0, Inventory.HotbarSize));
        left.AddChild(Caption("Backpack"));
        left.AddChild(SlotGrid(Inventory.HotbarSize, Inventory.BackpackSize));

        var hint = Caption("Click / (A) to pick up, again to place — same items stack, others swap.\n"
                           + "Right-click uses. K / (Back) closes. Hold X / D-pad ← for the quick wheel.");
        hint.AutowrapMode = TextServer.AutowrapMode.Off;
        left.AddChild(hint);

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0) };
        right.AddThemeConstantOverride("separation", 8);
        columns.AddChild(right);

        _infoName = new Label();
        _infoName.AddThemeFontSizeOverride("font_size", 18);
        right.AddChild(_infoName);

        _infoBlurb = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(220, 0) };
        _infoBlurb.AddThemeFontSizeOverride("font_size", 13);
        _infoBlurb.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.8f));
        right.AddChild(_infoBlurb);

        _useButton = new Button { Text = "Use" };
        _useButton.Pressed += () => _items.UseSlot(null, _inspect);
        right.AddChild(_useButton);

        _handButton = new Button { Text = "Take in hand" };
        _handButton.Pressed += TakeInHand;
        right.AddChild(_handButton);
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
            };
            button.Pressed += () => PickOrPlace(slot);
            button.FocusEntered += () => Inspect(slot);
            button.MouseEntered += () => Inspect(slot);
            button.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
                {
                    _items.UseSlot(null, slot);
                    button.AcceptEvent();
                }
            };
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
            _panelSlots[i].Display(Inv[i], i == Inv.Selected, i == _picked);
        Inspect(_inspect);
        _wheel.QueueRedraw();

        // the name of what just came into the hand, briefly
        if (Inv.HeldId != _lastHeld)
        {
            _lastHeld = Inv.HeldId;
            _heldName.Text = ItemDefs.Get(Inv.HeldId)?.Name ?? "Empty hand";
            _heldNameTimer = 1.8f;
        }
    }

    private void Inspect(int slot)
    {
        _inspect = slot;
        var stack = Inv[slot];
        var def = stack.IsEmpty ? null : ItemDefs.Get(stack.Id);
        _infoName.Text = def == null ? "Empty slot" : def.MaxStack > 1 ? $"{def.Name}  ×{stack.Count}" : def.Name;
        _infoName.AddThemeColorOverride("font_color", def?.Tint.Lightened(0.35f) ?? new Color(0.6f, 0.6f, 0.6f));
        _infoBlurb.Text = def?.Blurb ?? (slot < Inventory.HotbarSize ? "Hotbar slot — whatever is here can be in your hand." : "Backpack slot.");
        _useButton.Disabled = def?.Use != ItemUse.Consume;
        _handButton.Disabled = def == null || slot == Inv.Selected;
    }

    private void PickOrPlace(int slot)
    {
        if (_picked < 0)
        {
            if (Inv[slot].IsEmpty) return;
            _picked = slot;
            Refresh();
            return;
        }
        int from = _picked;
        _picked = -1;
        Inv.Move(from, slot);   // raises Changed -> Refresh
        Refresh();
    }

    /// <summary>A backpack item is swapped into the selected hotbar slot; a hotbar item just becomes the selection.</summary>
    private void TakeInHand()
    {
        if (_inspect < Inventory.HotbarSize) Inv.Select(_inspect);
        else Inv.Move(_inspect, Inv.Selected);
        Inspect(_inspect);
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
        _picked = -1;
        _panel.Visible = true;
        Refresh();
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        PlayerInput.FocusFirst(_panel);
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        _picked = -1;
        UiFocus.Set(this, false);
        Input.MouseMode = Input.MouseModeEnum.Captured;
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
    // input
    // ------------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (IsOpen)
        {
            if (!e.IsPressed() || e.IsEcho()) return;
            if (e.IsActionPressed("ui_cancel") && _picked >= 0)
            {
                _picked = -1;          // B first puts the held stack down, then closes
                Refresh();
            }
            else if (e.IsActionPressed(PlayerInput.Inventory) || e.IsActionPressed(PlayerInput.Menu)
                     || e.IsActionPressed("ui_cancel"))
                Close();
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
        _readoutPanel.Visible = Readout != null && !IsOpen;
        if (Readout != null) _readout.Text = Readout;

        _binoculars.Visible = Scope == ItemUse.Optic;
        _viewfinder.Visible = Scope == ItemUse.Photo;
        _crosshair.Visible = Scope == ItemUse.Shoot;
        if (_binoculars.Visible && _binoculars.Material is ShaderMaterial sm)
            sm.SetShaderParameter("aspect", _root.Size.X / Mathf.Max(1f, _root.Size.Y));

        _heldNameTimer -= dt;
        _heldName.Visible = ItemsActive && !IsOpen;
        _heldName.Modulate = new Color(1, 1, 1, Mathf.Clamp(_heldNameTimer / 0.4f, 0f, 1f));
        _toastTimer -= dt;
        _toast.Visible = PlayerPresent;
        _toast.Modulate = new Color(1, 1, 1, Mathf.Clamp(_toastTimer / 0.5f, 0f, 1f));
        if (_flash.Color.A > 0) _flash.Color = new Color(1, 1, 1, Mathf.MoveToward(_flash.Color.A, 0f, dt * 3f));

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
void fragment() {
    vec2 p = (UV - 0.5) * vec2(aspect, 1.0);
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

    public override void _Draw() => SlotDrawing.DrawSlot(this, new Rect2(Vector2.Zero, Size), _stack, KeyHint,
        _selected, _picked, IsHovered() || HasFocus());
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
            var inner = r.Grow(-r.Size.X * 0.18f);
            c.DrawRect(inner, def.Tint);
            c.DrawRect(inner, def.Tint.Lightened(0.4f), false, 1.5f);
            int glyphSize = (int)(r.Size.Y * 0.30f);
            c.DrawString(font, new Vector2(inner.Position.X, inner.GetCenter().Y + glyphSize * 0.36f), def.Glyph,
                HorizontalAlignment.Center, inner.Size.X, glyphSize, Colors.White);
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

/// <summary>A camera's viewfinder: corner brackets, a centre mark and the rule-of-thirds grid.</summary>
public partial class ViewfinderView : Control
{
    public override void _Draw()
    {
        var s = Size;
        var line = new Color(1, 1, 1, 0.85f);
        var faint = new Color(1, 1, 1, 0.18f);
        float inset = s.Y * 0.08f, arm = s.Y * 0.07f;

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

        var c = s * 0.5f;
        DrawLine(c - new Vector2(10, 0), c + new Vector2(10, 0), line, 1.5f);
        DrawLine(c - new Vector2(0, 10), c + new Vector2(0, 10), line, 1.5f);
        DrawString(ThemeDB.FallbackFont, new Vector2(inset, s.Y - inset + 22), "Use to shoot",
            HorizontalAlignment.Left, -1, 14, line);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }
}
