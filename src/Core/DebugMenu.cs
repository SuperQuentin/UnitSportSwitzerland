using System.Globalization;
using Godot;
using UnitSport.Terrain;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// The debug menu (#339): F9 or <c>/debug</c>. Overlays (tile boundaries and labels, the world
/// origin), terrain layers on and off, view modes (wireframe, clay, vertex colours, overdraw),
/// frozen streaming, a live readout of where the camera is, and the time scale.
///
/// <para>
/// Offered alone, and to an admin on a server (<see cref="Allowed"/>): hiding buildings or the
/// ground, or the wireframe view, would be a wallhack in a match. Losing the right turns every
/// tool off. Everything here is this screen's alone; nothing is sent anywhere.
/// </para>
///
/// <para>
/// The panel is a mouse tool: its controls never take keyboard focus, so the player keeps
/// walking or flying with it open and Space never flips a switch. The tools stay on when it
/// closes; leaving the world puts everything back. <c>--debugview a,b,...</c> turns tools on
/// from boot (see <see cref="ApplyArgs"/>), for screenshots.
/// </para>
/// </summary>
public partial class DebugMenu : CanvasLayer
{
    private const double ReadoutSeconds = 0.25;

    /// <summary>The menu may be offered: alone, or as a server's admin.</summary>
    public static bool Allowed => !Permissions.Online || Permissions.IsAdmin;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly Func<Node3D?> _nearTrees;
    private readonly Action<string> _say;
    private DebugOverlay _overlay = null!;

    private Control _panel = null!;
    private Label _readout = null!;
    private OptionButton _viewPicker = null!, _timePicker = null!, _perfPicker = null!;
    private readonly Dictionary<string, CheckBox> _toggles = new();
    private readonly Dictionary<string, bool> _defaults = new();
    private double _sinceReadout = ReadoutSeconds;
    private Input.MouseModeEnum _mouseBefore;

    private TileLayers _hiddenLayers;
    private bool _hideHorizon, _hideReal, _hideGenerated;
    private DebugViewMode _view;
    private static readonly double[] TimeScales = [0.1, 0.25, 0.5, 1, 2, 4];
    private bool _timeScaled;

    public bool IsOpen => _panel.Visible;

    // for --debugcheck (DebugMenuCheck), which clicks them like a player would
    internal CheckBox SwitchBox(string key) => _toggles[key];
    internal OptionButton ViewPicker => _viewPicker;
    internal DebugViewMode View => _view;
    internal DebugOverlay Overlay => _overlay;

    public DebugMenu(ChunkManager chunks, WorldOrigin origin, Func<Node3D?> nearTrees, Action<string> say)
    {
        _chunks = chunks;
        _origin = origin;
        _nearTrees = nearTrees;
        _say = say;
        Name = "DebugMenu";
    }

    public DebugMenu() : this(null!, null!, () => null, _ => { }) { }

    public override void _Ready()
    {
        // over the chat (15), under the place search (20) and the menus
        Layer = 16;
        _overlay = new DebugOverlay(_chunks, _origin);
        // the overlay draws in the world: it belongs under the world, not this canvas layer
        GetParent().CallDeferred(Node.MethodName.AddChild, _overlay);

        var panel = new PanelContainer { Visible = false, Theme = UiTheme.Get() };
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 10));
        panel.AnchorTop = 0;
        panel.AnchorBottom = 1;
        panel.OffsetLeft = 16;
        panel.OffsetTop = 16;
        panel.OffsetRight = 16 + 340;
        panel.OffsetBottom = -16;
        AddChild(panel);
        _panel = panel;

        var outer = UiKit.VBox(6);
        panel.AddChild(outer);
        var header = UiKit.HBox(8);
        header.AddChild(UiTheme.Title("Debug", 18));
        header.AddChild(UiKit.Spacer(expand: true));
        var hint = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint);
        hint.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        hint.Text = $"{InputHints.Label(PlayerInput.DebugMenu, InputDevice.KeyboardMouse)} or Esc closes";
        header.AddChild(hint);
        outer.AddChild(header);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        outer.AddChild(scroll);
        var rows = UiKit.VBox(2);
        rows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(rows);

        rows.AddChild(UiKit.Section("Overlays"));
        var overlays = Grid(rows);
        Switch(overlays, "tiles", "Tile boundaries", false, on => _overlay.Tiles = on,
            "Each loaded tile outlined on its ground, coloured by the stride it is drawn at: green full\n"
            + "resolution, through yellow, to red coarse; grey not built yet. Generated tiles are dashed.");
        Switch(overlays, "labels", "Tile labels", false, on => _overlay.Labels = on,
            "Tile id, stride and real or generated over the tiles around the camera.");
        Switch(overlays, "origin", "World origin", false, on => _overlay.WorldOrigin = on,
            "The floating origin's mast (seen through the ground), its east (red) and north (blue)\n"
            + "axes, and the circle past which the camera moves it.");

        rows.AddChild(UiKit.Spacer(6));
        rows.AddChild(UiKit.Section("Terrain"));
        var terrain = Grid(rows);
        LayerSwitch(terrain, "ground", "Ground", TileLayers.Ground);
        LayerSwitch(terrain, "roads", "Roads", TileLayers.Roads);
        LayerSwitch(terrain, "buildings", "Buildings", TileLayers.Buildings);
        LayerSwitch(terrain, "trees", "Trees", TileLayers.Trees);
        LayerSwitch(terrain, "water", "Water", TileLayers.Water);
        Switch(terrain, "horizon", "Far horizon", true, on => { _hideHorizon = !on; ApplyHidden(); },
            "The decimated ring of the whole country beyond the streamed tiles.");
        Switch(terrain, "real", "Real tiles", true, on => { _hideReal = !on; ApplyHidden(); },
            "Tiles built from swisstopo data.");
        Switch(terrain, "generated", "Generated tiles", true, on => { _hideGenerated = !on; ApplyHidden(); },
            "Tiles the generator made where there is no real data.");
        Switch(terrain, "fill", "Generated fill", GameSettings.Current.GeneratedFill, on =>
        {
            GameSettings.Current.GeneratedFill = on;
            GameSettings.Current.Commit();
        }, "The saved setting (also in Settings): off unloads every generated tile and stops making them.");

        rows.AddChild(UiKit.Spacer(6));
        rows.AddChild(UiKit.Section("View"));
        _viewPicker = Picker(rows, ["Normal", "Wireframe", "Clay (no textures)", "Vertex colours", "Overdraw"],
            0, i => SetView((DebugViewMode)i));
        _viewPicker.TooltipText =
            "Wireframe draws every triangle as lines: the full-resolution ground near the camera is tens of\n"
            + "millions of them (a few fps). Hide the ground to look at the rest. Switching to it rebuilds the tiles.\n"
            + "Clay and vertex colours replace every world shader: no patterns, photos, fog, wind or PS1 snap.";
        var pickers = UiKit.HBox(6);
        rows.AddChild(pickers);
        _timePicker = Picker(pickers, Array.ConvertAll(TimeScales, t => $"Time x{t.ToString(CultureInfo.InvariantCulture)}"),
            Array.IndexOf(TimeScales, 1.0), i => SetTimeScale(TimeScales[i]));
        _perfPicker = Picker(pickers, ["Perf: off", "Perf: FPS", "Perf: detailed"],
            (int)GameSettings.Current.PerfOverlay, i =>
            {
                GameSettings.Current.PerfOverlay = (PerfOverlayMode)i;
                GameSettings.Current.Commit();
            });

        rows.AddChild(UiKit.Spacer(6));
        rows.AddChild(UiKit.Section("Streaming"));
        Switch(rows, "freeze", "Freeze tile streaming", false, on => _chunks.FreezeRings = on,
            "What is loaded stays loaded at the stride it has: fly away and look at it from outside.");
        var buttons = UiKit.HBox(6);
        buttons.AddChild(ActionButton("Rebuild tiles", _chunks.RebuildVisuals));
        buttons.AddChild(ActionButton("Copy position", CopyPosition));
        rows.AddChild(buttons);

        rows.AddChild(UiKit.Spacer(6));
        rows.AddChild(UiKit.Section("Camera"));
        _readout = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextDim);
        _readout.AddThemeFontOverride("font", new SystemFont { FontNames = ["Consolas", "Courier New", "monospace"] });
        rows.AddChild(_readout);

        Permissions.Changed += OnPermissionsChanged;
        ApplyArgs();
    }

    public override void _ExitTree()
    {
        Permissions.Changed -= OnPermissionsChanged;
        if (IsOpen) Close();
        // what outlives this world: the root viewport, the kit's materials, the clock
        if (_view != DebugViewMode.Normal) DebugView.Apply(GetViewport(), null, DebugViewMode.Normal, _view);
        if (_timeScaled) Engine.TimeScale = 1;
    }

    private void OnPermissionsChanged()
    {
        if (Allowed) return;
        Close();
        ResetAll();
    }

    // ------------------------------------------------------------------------------------
    // construction
    // ------------------------------------------------------------------------------------

    /// <summary>Two columns of switches: the panel stays short enough to leave the chat its corner.</summary>
    private static GridContainer Grid(Container into)
    {
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 0);
        into.AddChild(grid);
        return grid;
    }

    private static readonly StyleBoxFlat Row = UiTheme.Flat(new Color(0, 0, 0, 0), 6, 4, 1);
    private static readonly StyleBoxFlat RowHover = UiTheme.Flat(new Color(1, 1, 1, 0.05f), 6, 4, 1);

    private CheckBox Switch(Container into, string key, string text, bool on, Action<bool> set, string? tip = null)
    {
        // a mouse tool: never the keyboard's focus, or Space (jump) would flip the last one clicked
        var box = new CheckBox
        {
            Text = text, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None, TooltipText = tip ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        box.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        box.AddThemeStyleboxOverride("normal", Row);
        box.AddThemeStyleboxOverride("pressed", Row);
        box.AddThemeStyleboxOverride("hover", RowHover);
        box.AddThemeStyleboxOverride("hover_pressed", RowHover);
        // the theme's focus outline has menu-sized margins, which a button counts in its height
        box.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        box.Toggled += pressed => set(pressed);
        into.AddChild(box);
        _toggles[key] = box;
        _defaults[key] = on;
        return box;
    }

    private void LayerSwitch(Container into, string key, string text, TileLayers layer) =>
        Switch(into, key, text, true, on =>
        {
            _hiddenLayers = on ? _hiddenLayers & ~layer : _hiddenLayers | layer;
            ApplyHidden();
        });

    private static OptionButton Picker(Container into, string[] items, int selected, Action<int> set)
    {
        var picker = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        picker.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        foreach (string item in items) picker.AddItem(item);
        picker.Selected = selected;
        picker.ItemSelected += i => set((int)i);
        into.AddChild(picker);
        return picker;
    }

    private static Button ActionButton(string text, Action pressed)
    {
        var b = UiKit.Button(text);
        b.FocusMode = Control.FocusModeEnum.None;
        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        b.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        b.Pressed += pressed;
        return b;
    }

    // ------------------------------------------------------------------------------------
    // tools
    // ------------------------------------------------------------------------------------

    private void ApplyHidden()
    {
        _chunks.SetDebugHidden(_hiddenLayers, _hideReal, _hideGenerated);
        if (_chunks.Horizon != null) _chunks.Horizon.Visible = !_hideHorizon;
        // the high-detail styles' trees near the camera are one node of their own
        if (_nearTrees() is { } near) near.Visible = (_hiddenLayers & TileLayers.Trees) == 0;
    }

    private void SetView(DebugViewMode mode)
    {
        if (mode == _view) return;
        DebugView.Apply(GetViewport(), _chunks, mode, _view);
        _view = mode;
    }

    private void SetTimeScale(double scale)
    {
        // online the server keeps the clock: a client running slow would only fall out of step
        if (Permissions.Online && scale != 1)
        {
            _say("Time scale is offline only.");
            _timePicker.Selected = Array.IndexOf(TimeScales, 1.0);
            return;
        }
        Engine.TimeScale = scale;
        _timeScaled = scale != 1;
    }

    private void CopyPosition()
    {
        if (GetViewport().GetCamera3D() is not { } cam) return;
        var (e, n) = _origin.ToLv95(cam.GlobalPosition);
        string text = string.Create(CultureInfo.InvariantCulture, $"--at {e:F0},{n:F0}");
        DisplayServer.ClipboardSet(text);
        _say($"Copied {text}");
    }

    /// <summary>Every tool back as it starts, and the world drawn as the style has it (the fill is a setting: kept).</summary>
    private void ResetAll()
    {
        foreach (var (key, box) in _toggles)
            if (key != "fill") box.SetPressedNoSignal(_defaults[key]);
        _overlay.Tiles = _overlay.Labels = _overlay.WorldOrigin = false;
        _hiddenLayers = TileLayers.None;
        _hideHorizon = _hideReal = _hideGenerated = false;
        ApplyHidden();
        _chunks.FreezeRings = false;
        SetView(DebugViewMode.Normal);
        _viewPicker.Selected = 0;
        if (_timeScaled) Engine.TimeScale = 1;
        _timeScaled = false;
        _timePicker.Selected = Array.IndexOf(TimeScales, 1.0);
    }

    /// <summary>
    /// <c>--debugview</c>: a comma list of tools to turn on at boot. <c>open</c> shows the panel;
    /// <c>tiles</c>, <c>labels</c>, <c>origin</c>, <c>freeze</c> switch those on; <c>no&lt;layer&gt;</c>
    /// hides one (<c>noground</c>, <c>noroads</c>, <c>nobuildings</c>, <c>notrees</c>, <c>nowater</c>,
    /// <c>nohorizon</c>, <c>noreal</c>, <c>nogenerated</c>); <c>wireframe</c>, <c>clay</c>,
    /// <c>colours</c>, <c>overdraw</c> pick the view.
    /// </summary>
    private void ApplyArgs()
    {
        if (CmdArgs.Value("--debugview") is not { } list) return;
        foreach (string raw in list.Split(','))
        {
            string tool = raw.Trim().ToLowerInvariant();
            if (tool == "open") CallDeferred(MethodName.Open);
            else if (_toggles.TryGetValue(tool, out var on)) on.ButtonPressed = true;
            else if (tool.StartsWith("no") && _toggles.TryGetValue(tool[2..], out var off)) off.ButtonPressed = false;
            else if (Array.IndexOf(ViewNames, tool) is var view and > 0)
            {
                _viewPicker.Selected = view;
                SetView((DebugViewMode)view);
            }
            else GD.PushWarning($"[debug] --debugview: unknown tool '{tool}'");
        }
    }

    private static readonly string[] ViewNames = ["normal", "wireframe", "clay", "colours", "overdraw"];

    // ------------------------------------------------------------------------------------
    // open / close / input
    // ------------------------------------------------------------------------------------

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (IsOpen) return;
        if (!Allowed)
        {
            _say("The debug menu is for admins on a server.");
            return;
        }
        _panel.Visible = true;
        _sinceReadout = ReadoutSeconds;
        // the two settings here may have changed in the Settings screen since
        _toggles["fill"].SetPressedNoSignal(GameSettings.Current.GeneratedFill);
        _perfPicker.Selected = (int)GameSettings.Current.PerfOverlay;
        _mouseBefore = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        if (_mouseBefore == Input.MouseModeEnum.Captured) MouseCapture.Capture();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed(PlayerInput.DebugMenu))
        {
            if (UiFocus.TextEntryActive) return;
            Toggle();
        }
        else if (IsOpen && (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu))) Close();
        else return;
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        _sinceReadout += delta;
        if (_sinceReadout < ReadoutSeconds) return;
        _sinceReadout = 0;
        string text = Readout();
        if (_readout.Text != text) _readout.Text = text;
    }

    /// <summary>Where the camera is, what is under it, and the state of the loader and origin.</summary>
    private string Readout()
    {
        if (GetViewport().GetCamera3D() is not { } cam) return "no camera";
        var ci = CultureInfo.InvariantCulture;
        var pos = cam.GlobalPosition;
        var (e, n) = _origin.ToLv95(pos);
        var tile = _origin.TileAt(pos);
        int stride = _chunks.StrideAt(tile);
        string ground = _chunks.TryGetHeight(pos, out float h)
            ? string.Format(ci, "{0:F1} m, {1:F0} m below", h, pos.Y - h) : "not loaded";
        string cover = _chunks.TryGetCover(pos, out var c) ? c.ToString() : "-";
        var shifter = OriginShifter.Instance;
        return string.Format(ci,
            "LV95   {0:F0} / {1:F0}  alt {2:F0} m\ntile   {3}  {4}  {5}\nground {6}  {7}\n"
            + "origin {8:F0} / {9:F0}  shifts {10}\ntiles  {11} loaded{12}",
            e, n, pos.Y, tile, stride < 0 ? "not built" : $"stride {stride}",
            _chunks.IsGenerated(tile) ? "generated" : "real", ground, cover,
            _origin.E, _origin.N, shifter?.ShiftCount ?? 0,
            _chunks.ActiveChunkCount, _chunks.FreezeRings ? "  FROZEN" : "");
    }
}
