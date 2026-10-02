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
            new("Car radio: stations and CDs (passengers too)", PlayerInput.RadioPanel),
            new("Car: fold the soft top", PlayerInput.RoofToggle),
            new("Get out", PlayerInput.InteractMount),
        }),
        ("Passengers (online)", new Row[]
        {
            new("Get into a seat of a car someone drives / get out", PlayerInput.InteractMount),
            new("In a bus: walk in by a door, E at a seat sits, E again stands up, E at the wheel drives", PlayerInput.InteractMount),
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

    private ScrollContainer _scroll = null!;
    private Label _footer = null!;

    public override void _Ready()
    {
        Layer = 42;   // over the menus, so it can be opened from them

        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);

        // the menus' glass, themed on this panel's own root (docs/notes/ui/style-guide.md)
        _panel = new PanelContainer { Visible = false, Theme = Ui.UiTheme.Get() };
        _panel.AddThemeStyleboxOverride("panel", Ui.UiTheme.GlassPanel(0.92f, 12, 20));
        centre.AddChild(_panel);

        var rows = Ui.UiKit.VBox(10);
        _panel.AddChild(rows);

        var head = Ui.UiKit.HBox(14);
        head.AddChild(Ui.UiKit.Text("Controls", Ui.UiTheme.FontHeading, Ui.UiTheme.Text, bold: true));
        head.AddChild(Ui.UiKit.Spacer(expand: true));
        head.AddChild(Chip("Keyboard", KeyColor, true));
        head.AddChild(Chip("Pad", PadColor, true));
        rows.AddChild(head);

        // the groups flow into as many columns as the window has room for, and scroll past its height
        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        rows.AddChild(_scroll);
        _columns = new GridContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _columns.AddThemeConstantOverride("h_separation", 12);
        _scroll.AddChild(Ui.UiKit.Margin(_columns, 0, 0, 10, 0));

        _footer = Ui.UiKit.Text("", Ui.UiTheme.FontTiny, Ui.UiTheme.TextFaint, wrap: true);
        rows.AddChild(_footer);

        PlayerInput.DeviceChanged += Rebuild;
        GetViewport().SizeChanged += Rebuild;
        Rebuild();
        // "--controls" opens it from boot, for screenshotting it
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--controls") >= 0) Callable.From(Open).CallDeferred();
    }

    public override void _ExitTree()
    {
        PlayerInput.DeviceChanged -= Rebuild;
        GetViewport().SizeChanged -= Rebuild;
    }

    private static readonly Color KeyColor = new(1f, 0.84f, 0.42f);
    private static readonly Color PadColor = new(0.6f, 0.8f, 1f);
    private const float MinGroupWidth = 320;

    private void Rebuild()
    {
        if (_columns == null) return;
        var view = GetViewport().GetVisibleRect().Size;
        var size = new Vector2(Mathf.Min(view.X - 48, 1600), Mathf.Min(view.Y - 48, 900));
        _panel.CustomMinimumSize = size;
        _panel.Size = size;

        foreach (var child in _columns.GetChildren()) child.QueueFree();
        float inner = size.X - 40 - 10;   // panel margins, the scrollbar's gutter
        int count = Mathf.Clamp((int)((inner + 12) / (MinGroupWidth + 12)), 1, 4);
        float width = (inner - 12 * (count - 1)) / count;
        _columns.Columns = count;
        // greedy balance: each group goes to the shortest column, in order of the list
        var stacks = new VBoxContainer[count];
        var heights = new int[count];
        for (int i = 0; i < count; i++)
        {
            stacks[i] = Ui.UiKit.VBox(12);
            stacks[i].CustomMinimumSize = new Vector2(width, 0);
            stacks[i].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _columns.AddChild(stacks[i]);
        }
        foreach (var (title, rows) in Groups)
        {
            int k = Array.IndexOf(heights, heights.Min());
            stacks[k].AddChild(Group(title, rows));
            heights[k] += rows.Length + 3;
        }
        _footer.Text = $"{InputHints.Label(PlayerInput.Help, InputDevice.KeyboardMouse)} or Esc closes. "
            + "Keys are shown as printed on your keyboard. The hints at the bottom right change with what you are doing.";
    }

    private static Control Group(string title, Row[] rows)
    {
        var box = Ui.UiKit.VBox(4);
        box.AddChild(Ui.UiKit.Section(title));
        foreach (var row in rows)
        {
            string keys = row.Keys != null ? InputHints.Format(row.Keys, InputDevice.KeyboardMouse)
                : row.Action != null ? InputHints.Label(row.Action, InputDevice.KeyboardMouse) : "—";
            string pad = row.Pad ?? (row.Action != null ? PadOrDash(row.Action) : "—");
            var line = Ui.UiKit.HBox(6);
            var what = Ui.UiKit.Text(row.What, Ui.UiTheme.FontSmall, Ui.UiTheme.Text, wrap: true);
            what.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            line.AddChild(what);
            line.AddChild(Chip(keys, KeyColor));
            line.AddChild(Chip(pad, PadColor));
            box.AddChild(line);
        }
        return Ui.UiKit.Card(box, 0.5f, 12);
    }

    /// <summary>A key cap: the binding on a rounded tile, tinted keyboard-amber or pad-blue; a dash is left bare.</summary>
    private static Control Chip(string text, Color color, bool legend = false)
    {
        bool none = text == "—";
        var chip = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(legend ? 0 : 70, 0),
        };
        chip.AddThemeStyleboxOverride("panel", none
            ? new StyleBoxEmpty()
            : Ui.UiTheme.Flat(new Color(color, 0.12f), 6, 7, 2, new Color(color, 0.35f), 1));
        var label = Ui.UiKit.Text(text, Ui.UiTheme.FontTiny + 1, none ? Ui.UiTheme.TextFaint : color, bold: !none,
            align: HorizontalAlignment.Center);
        chip.AddChild(label);
        return chip;
    }

    /// <summary>The pad binding, or a dash where there is only a keyboard one (the label would repeat the key).</summary>
    private static string PadOrDash(string action)
    {
        string pad = InputHints.Label(action, InputDevice.Gamepad);
        return pad == InputHints.Label(action, InputDevice.KeyboardMouse) ? "—" : pad;
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
