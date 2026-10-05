using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Vehicles;

/// <summary>
/// The garage's tuning menu, NFS style and on the spot: the car stays where it stopped, in front
/// of the garage, and the chase camera walks round it (<see cref="FootPlayer.ShowroomYaw"/>) while
/// every part is tried on live. Free. The parts go on the car being driven
/// (<see cref="FootPlayer.SetTuning"/>) and so travel with it — parked, re-entered, seen by others.
///
/// <para>
/// Opened with T / D-pad down in a stopped car where <see cref="GarageNear"/> says there is a
/// garage (or anywhere with <c>--tuning</c>). Same pattern as <see cref="RideUi"/>: mouse visible,
/// registered with <see cref="UiFocus"/> so the car does not drive off, keys consumed here first.
/// </para>
/// </summary>
public partial class GarageUi : CanvasLayer
{
    /// <summary>
    /// Is there a garage to tune at, here? Plugged in by whoever knows where garages are; null
    /// until then, when only <c>--tuning</c> opens the menu (anywhere).
    /// </summary>
    public static Func<Vector3, bool>? GarageNear { get; set; }

    /// <summary><c>--tuning</c>: the menu opens anywhere, for testing and screenshots.</summary>
    public static bool Anywhere { get; } = CmdArgs.Has("--tuning");

    /// <summary>Resolved per press, never captured: in multiplayer the player node is respawned.</summary>
    public Func<FootPlayer?>? ActivePlayer { get; set; }

    public bool IsOpen => _panel.Visible;

    private PanelContainer _panel = null!;
    private VBoxContainer _slots = null!, _options = null!;
    private Label _prompt = null!, _optionsTitle = null!, _hint = null!;
    private FootPlayer? _player;
    private CarTuning _atOpen;
    private TuneSlot _slot;
    private float _yaw;

    private static readonly string[] SlotNames =
    {
        "Tyres", "Rear wing", "Front bumper", "Rear bumper", "Side skirts", "Bonnet scoop", "Bonnet",
        "Paint", "Two-tone lower", "Rim colour", "Rim size", "Ride height", "Window tint", "Underglow", "Doors",
    };

    public static GarageUi Create() => new() { Name = "GarageUi" };

    /// <summary>Whether this player can tune right now: in a car, stopped, at a garage.</summary>
    public static bool CanTune(FootPlayer player) =>
        CarCatalog.For(player.Ride) != null && player.GroundSpeed < 1f
        && (Anywhere || GarageNear?.Invoke(player.GlobalPosition) == true);

    public override void _Ready()
    {
        Layer = 30;   // under the main menu, over the world

        _prompt = UiTheme.Prompt(-210);
        AddChild(_prompt);

        // on the left, so the car stays in view on the right
        _panel = new PanelContainer { Visible = false, Position = new Vector2(24, 24), CustomMinimumSize = new Vector2(500, 0) };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.05f, 0.06f, 0.08f, 0.9f), 6, 18, 14));
        AddChild(_panel);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rows);

        rows.AddChild(UiTheme.Title("Garage"));
        rows.AddChild(new HSeparator());

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 14);
        rows.AddChild(columns);

        _slots = new VBoxContainer { CustomMinimumSize = new Vector2(250, 0) };
        _slots.AddThemeConstantOverride("separation", 2);
        columns.AddChild(Scroll(_slots));

        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _optionsTitle = new Label();
        _optionsTitle.AddThemeColorOverride("font_color", new Color(0.7f, 0.74f, 0.8f));
        right.AddChild(_optionsTitle);
        _options = new VBoxContainer();
        _options.AddThemeConstantOverride("separation", 2);
        right.AddChild(Scroll(_options));
        columns.AddChild(right);

        for (int i = 0; i < CarTuning.SlotCount; i++)
        {
            var slot = (TuneSlot)i;
            var button = new Button { Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 28) };
            button.Pressed += () => ShowSlot(slot);
            // a pad walks the list with the D-pad: the options follow the focus
            button.FocusEntered += () => ShowSlot(slot);
            _slots.AddChild(button);
        }

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        foreach (var (text, act) in new (string, Action)[] { ("Done", Close), ("Revert", Revert), ("All stock", () => Set(default)) })
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(110, 32) };
            b.Pressed += act;
            actions.AddChild(b);
        }
        rows.AddChild(actions);

        var hint = _hint = new Label();
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(hint);
    }

    private static ScrollContainer Scroll(Control content)
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 430), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(content);
        return scroll;
    }

    /// <summary>T / D-pad down: opens the menu if this player can tune here. False otherwise.</summary>
    public bool TryOpen()
    {
        if (IsOpen || ActivePlayer?.Invoke() is not { } player || !CanTune(player)) return false;
        Open(player);
        return true;
    }

    /// <summary>Opens the menu on this player's car, wherever it is (the entry point for a garage trigger).</summary>
    public void Open(FootPlayer player)
    {
        _player = player;
        _atOpen = player.Tuning;
        _yaw = 2.4f;   // three-quarter front: the view a garage shows a car in
        player.ShowroomYaw = _yaw;
        _panel.Visible = true;
        _prompt.Visible = false;
        // named for the device in hand as the panel opens (#435)
        _hint.Text = (InputHints.Pad ? InputHints.Format("Free. {look_right}: walk round the car. {ui_cancel} / {tune}: done.")
                : InputHints.Format("Free. Right-drag: walk round the car. {menu} / {tune}: done."))
            + " The parts stay on this car; a new car comes stock.";
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Refresh();
        ShowSlot(TuneSlot.Tyres);
        PlayerInput.FocusFirst(_slots);
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        if (_player != null && IsInstanceValid(_player)) _player.ShowroomYaw = null;
        _player = null;
        UiFocus.Set(this, false);
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void Revert() => Set(_atOpen);

    private void Set(CarTuning tuning)
    {
        _player?.SetTuning(tuning);
        Refresh();
        ShowSlot(_slot);
    }

    private void Refresh()
    {
        var t = _player?.Tuning ?? default;
        for (int i = 0; i < CarTuning.SlotCount; i++)
            ((Button)_slots.GetChild(i)).Text = $"{SlotNames[i]}:  {CarTuning.Options[i][t[(TuneSlot)i]]}";
    }

    private void ShowSlot(TuneSlot slot)
    {
        if (_player == null) return;
        _slot = slot;
        _optionsTitle.Text = SlotNames[(int)slot];
        foreach (var child in _options.GetChildren()) child.QueueFree();
        var options = CarTuning.Options[(int)slot];
        int current = _player.Tuning[slot];
        for (int v = 0; v < options.Length; v++)
        {
            int value = v;
            var b = new Button
            {
                Text = (v == current ? "▶ " : "   ") + options[v],
                Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 28),
            };
            b.Pressed += () =>
            {
                Set(_player!.Tuning.With(slot, value));
                // keep a pad's focus where it was: the list was just rebuilt under it
                if (_options.GetChildCount() > 0) CallDeferred(MethodName.FocusOption, value);
            };
            _options.AddChild(b);
        }
    }

    private void FocusOption(int index)
    {
        // the rebuilt list: the old buttons are queued for deletion and still counted first
        var live = _options.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).ToList();
        if (index < live.Count) live[index].GrabFocus();
    }

    public override void _Process(double delta)
    {
        if (!IsOpen)
        {
            var p = ActivePlayer?.Invoke();
            bool can = p != null && CanTune(p);
            _prompt.Visible = can;
            if (can) _prompt.Text = InputHints.Prompt(PlayerInput.Tune, "Tuning");
            return;
        }
        // the car went (a respawn, a wreck, a reconnect): nothing left to tune
        if (_player == null || !IsInstanceValid(_player) || CarCatalog.For(_player.Ride) == null) { Close(); return; }
        float stick = PlayerInput.Strength(PlayerInput.LookRight) - PlayerInput.Strength(PlayerInput.LookLeft);
        if (Mathf.Abs(stick) > 0.2f) _yaw -= stick * 2f * (float)delta;
        _player.ShowroomYaw = _yaw;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsOpen) return;
        if (@event is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Right) != 0)
        {
            _yaw -= motion.Relative.X * 0.008f;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!@event.IsPressed() || @event.IsEcho()) return;
        if (@event.IsActionPressed(PlayerInput.Menu) || @event.IsActionPressed("ui_cancel") || @event.IsActionPressed(PlayerInput.Tune))
            Close();
        // everything else is the menu's while it is open: E must not get out, T not drop to the fly camera
        GetViewport().SetInputAsHandled();
    }
}
