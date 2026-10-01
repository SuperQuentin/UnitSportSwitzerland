using Godot;

namespace UnitSport.Core;

/// <summary>
/// Every control in the game on one screen, opened with F1 (or the main menu's Controls button).
///
/// <para>
/// Built from the live <see cref="InputMap"/> through <see cref="InputHints"/>, with a keyboard
/// and a pad column side by side, so it is the actual layout and not a copy of it that can go
/// stale — the keyboard column even prints the letters of the player's own layout. It only reads,
/// so it does not take the input: you can keep playing with it open.
/// </para>
/// </summary>
public partial class ControlsHelp : CanvasLayer
{
    private PanelContainer _panel = null!;
    private GridContainer _columns = null!;

    public bool IsOpen => _panel.Visible;

    public static ControlsHelp Create() => new() { Name = "ControlsHelp" };

    /// <summary>A row: what it does, and either an action or fixed labels (keyboard, pad) for things that are not one.</summary>
    private readonly record struct Row(string What, string? Action = null, string? Keys = null, string? Pad = null);

    /// <summary>The four movement keys, as printed on this keyboard (Z Q S D on AZERTY).</summary>
    private const string Wasd = "{move_forward} {move_left} {move_back} {move_right}";

    private static readonly (string Title, Row[] Rows)[] Groups =
    {
        ("On foot", new Row[]
        {
            new("Move", Keys: Wasd, Pad: "Left stick"),
            new("Look", Keys: "Mouse", Pad: "Right stick"),
            new("Jump / wall jump / mantle", PlayerInput.Jump),
            new("Run", PlayerInput.Sprint),
            new("Slide (while running)", PlayerInput.CrouchSlide),
            new("Interact: get in or out, search, door", PlayerInput.InteractMount),
            new("Travel menu: mounts and vehicles", PlayerInput.RideMenu, Pad: "Y (nothing near)"),
            new("First / third person (driving: chase, cockpit, cockpit without your body)", PlayerInput.CameraToggle),
            new("Base jump: jump again while falling", PlayerInput.Jump),
        }),
        ("Items", new Row[]
        {
            new("Use the item in hand", PlayerInput.UseItem),
            new("Aim (binoculars, camera, shotgun); with anything else, a throw", PlayerInput.AimItem),
            new("Throw: hold Aim, hold Use to wind up, let go", Keys: "{aim_item} + {use_item}", Pad: "LB + RB"),
            new("Drop the item in hand (Ctrl: the whole stack)", PlayerInput.DropItem),
            new("Pick up what you point at", PlayerInput.InteractMount),
            new("Pick a hotbar slot", Keys: "1–6 / Wheel", Pad: "D-pad →"),
            new("Quick wheel (hold)", PlayerInput.QuickWheel),
            new("Inventory", PlayerInput.Inventory),
            new("Gather stone, water, wood (hold)", PlayerInput.Gather),
            new("Bird journal", PlayerInput.BirdJournal),
        }),
        ("Riding and driving", new Row[]
        {
            new("Throttle / pedal", PlayerInput.Throttle),
            new("Brake / reverse", PlayerInput.Brake),
            new("Steer", Keys: "{move_left} {move_right}", Pad: "Left stick"),
            new("Tuck / sprint effort", PlayerInput.TuckBoost),
            new("Hop / handbrake", PlayerInput.Jump),
            new("Trick in the air (hold + stick)", PlayerInput.Trick),
            new("Boost", PlayerInput.Boost),
            new("Look behind", PlayerInput.LookBehind),
            new("Engine on / off", PlayerInput.EngineToggle),
            new("Car: headlights / pop-ups", PlayerInput.LightsToggle),
            new("Car radio: next station", PlayerInput.RadioNext),
            new("Car radio: previous station", PlayerInput.RadioPrev),
            new("Car: fold the soft top", PlayerInput.RoofToggle),
            new("Get out", PlayerInput.InteractMount),
        }),
        ("Passengers (online)", new Row[]
        {
            new("Get into a seat of a vehicle someone drives / get out", PlayerInput.InteractMount),
            new("Take the wheel, when nobody holds it", PlayerInput.TakeWheel),
            new("Look round from your seat / chase view", PlayerInput.CameraToggle),
        }),
        ("Trucks and buses", new Row[]
        {
            new("Couple / uncouple a trailer", PlayerInput.Couple),
            new("Shift up / splitter high", PlayerInput.ShiftUp),
            new("Shift down / splitter low", PlayerInput.ShiftDown),
            new("Clutch (hold)", PlayerInput.Clutch),
            new("H-pattern gates, reverse, neutral", Keys: "1–6, ` , 0", Pad: "—"),
            new("Retarder stalk more / less", Keys: "{retarder_up} / {retarder_down}", Pad: "—"),
            new("Parking brake (hold)", PlayerInput.Jump),
            new("Bus: doors", PlayerInput.CarDoor),
            new("Bus: kneel", PlayerInput.Kneel),
            new("Bus: destination display", PlayerInput.Destination),
        }),
        ("Flying", new Row[]
        {
            new("Pitch and roll", Keys: Wasd, Pad: "Left stick"),
            new("Helicopter up / down", Keys: "{jump} / {crouch_slide}", Pad: "RT / LT"),
            new("Plane throttle up / down", Keys: "{sprint} / {crouch_slide}", Pad: "RT / LT"),
            new("Guns (armed aircraft)", PlayerInput.Fire),
        }),
        ("Fly camera", new Row[]
        {
            new("Fly camera / walk", PlayerInput.ToggleMode),
            new("Up", PlayerInput.FlyUp),
            new("Down", PlayerInput.FlyDown),
            new("Fast", PlayerInput.FlyBoost),
        }),
        ("Game", new Row[]
        {
            new("Map: search a place and go", PlayerInput.Teleport),
            new("Menu", PlayerInput.Menu),
            new("Chat / command", Keys: "Enter or /", Pad: "—"),
            new("This screen", PlayerInput.Help),
            new("Performance overlay / log", Keys: "F3 / F4", Pad: "—"),
        }),
    };

    public override void _Ready()
    {
        Layer = 42;   // over the menus, so it can be opened from them

        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);

        _panel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.95f),
            ContentMarginLeft = 22, ContentMarginRight = 22, ContentMarginTop = 14, ContentMarginBottom = 14,
        };
        style.SetCornerRadiusAll(6);
        _panel.AddThemeStyleboxOverride("panel", style);
        centre.AddChild(_panel);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rows);

        var title = new Label { Text = "Controls" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        rows.AddChild(title);

        _columns = new GridContainer { Columns = 3 };
        _columns.AddThemeConstantOverride("h_separation", 22);
        _columns.AddThemeConstantOverride("v_separation", 10);
        rows.AddChild(_columns);

        var foot = new Label();
        foot.AddThemeFontSizeOverride("font_size", 12);
        foot.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(foot);
        _footer = foot;

        PlayerInput.DeviceChanged += Rebuild;
        Rebuild();
    }

    private Label _footer = null!;

    public override void _ExitTree() => PlayerInput.DeviceChanged -= Rebuild;

    private void Rebuild()
    {
        foreach (var child in _columns.GetChildren()) child.QueueFree();
        foreach (var (title, rows) in Groups) _columns.AddChild(Group(title, rows));
        _footer.Text = $"{InputHints.Label(PlayerInput.Help, InputDevice.KeyboardMouse)} or Esc closes. "
            + "Keys are shown as printed on your keyboard. The hints at the bottom right change with what you are doing.";
    }

    private static Control Group(string title, Row[] rows)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 1);
        var head = new Label { Text = title };
        head.AddThemeFontSizeOverride("font_size", 15);
        head.AddThemeColorOverride("font_color", new Color(0.98f, 0.84f, 0.38f));
        box.AddChild(head);

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 1);
        foreach (var row in rows)
        {
            string keys = row.Keys != null ? InputHints.Format(row.Keys, InputDevice.KeyboardMouse) : (row.Action != null ? InputHints.Label(row.Action, InputDevice.KeyboardMouse) : "—");
            string pad = row.Pad ?? (row.Action != null ? PadOrDash(row.Action) : "—");
            grid.AddChild(Cell(row.What, new Color(0.82f, 0.85f, 0.9f), 168));
            grid.AddChild(Cell(keys, new Color(1f, 0.86f, 0.45f), 78));
            grid.AddChild(Cell(pad, new Color(0.62f, 0.8f, 1f), 70));
        }
        box.AddChild(grid);
        return box;
    }

    /// <summary>The pad binding, or a dash where there is only a keyboard one (the label would repeat the key).</summary>
    private static string PadOrDash(string action)
    {
        string pad = InputHints.Label(action, InputDevice.Gamepad);
        return pad == InputHints.Label(action, InputDevice.KeyboardMouse) ? "—" : pad;
    }

    private static Label Cell(string text, Color color, float width)
    {
        var label = new Label { Text = text, CustomMinimumSize = new Vector2(width, 0) };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        Rebuild();
        _panel.Visible = true;
    }

    public void Close() => _panel.Visible = false;

    // _Input, not _UnhandledInput: it opens over the main menu, which would otherwise take the
    // Esc meant to close this and act on it itself
    public override void _Input(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed(PlayerInput.Help))
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
        else if (IsOpen && (e.IsActionPressed(PlayerInput.Menu) || e.IsActionPressed("ui_cancel")))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
