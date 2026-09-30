using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// The "what am I travelling as" picker, opened with R (pad: Y with nothing to interact with).
///
/// <para>
/// A menu rather than a cycle key, for two reasons: the list is meant to grow, and a refusal
/// needs somewhere to be explained. On a server, the vehicles in it (anything left in the world
/// when you get out) are an admin's to spawn — see <see cref="Permissions"/>; the rows stay
/// listed, greyed, so the reason is visible rather than the vehicles simply missing. You cannot get on a bike while airborne or step off skis at
/// 70 km/h, and a key that silently does nothing in those moments reads as a broken key — so the
/// panel says why and stays open.
/// </para>
///
/// <para>
/// It registers with <see cref="UiFocus"/> while open. That is not about text: <see cref="FootPlayer"/>
/// reads physical keys every frame, so without it the 1/2/3 shortcuts would arrive at the same
/// time as W and you would ride away while choosing.
/// </para>
/// </summary>
public partial class RideUi : CanvasLayer
{
    private PanelContainer _panel = null!;
    private Label _status = null!;
    private readonly List<(RideKind Kind, Button Button, bool Vehicle)> _entries = new();
    private readonly List<(Label Line, string Blurb)> _blurbs = new();
    private Label _hint = null!, _lockNote = null!;
    /// <summary>Entries reachable by number key: the mounts, not the car list.</summary>
    private int _shortcuts;
    private ScrollContainer _cars = null!;
    private Button _carsButton = null!;

    /// <summary>Resolved per press, never captured: in multiplayer the player node is respawned.</summary>
    public Func<FootPlayer?>? ActivePlayer { get; set; }

    public bool IsOpen => _panel.Visible;

    public static RideUi Create() => new() { Name = "RideUi" };

    public override void _Ready()
    {
        Layer = 30;   // under the main menu, over the world

        var centre = new CenterContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(centre);

        _panel = new PanelContainer { CustomMinimumSize = new Vector2(440, 0), Visible = false };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.94f),
            ContentMarginLeft = 22, ContentMarginRight = 22,
            ContentMarginTop = 18, ContentMarginBottom = 18,
        };
        style.SetCornerRadiusAll(6);
        _panel.AddThemeStyleboxOverride("panel", style);
        centre.AddChild(_panel);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rows);

        var title = new Label { Text = "Travel as" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        rows.AddChild(title);

        _lockNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _lockNote.AddThemeFontSizeOverride("font_size", 12);
        _lockNote.AddThemeColorOverride("font_color", new Color(0.92f, 0.72f, 0.4f));
        rows.AddChild(_lockNote);

        rows.AddChild(new HSeparator());

        Entry(rows, 1, RideKind.OnFoot, "On foot",
            "{move_forward}{move_left}{move_back}{move_right} walk, {sprint} run, {jump} jump, {crouch_slide} slide, jump at a wall to kick off",
            false);

        int number = 2;
        foreach (var ride in Rideable.All)
            Entry(rows, number++, ride.Kind, ride.Label, ride.Blurb, ride.IsVehicle);
        _shortcuts = _entries.Count;

        // The cars are a roster, not a line each: one button folds a scrolling list open, so the
        // mounts above stay on screen and in reach of the number keys.
        var carsButton = new Button { Text = $"{number}.  Cars  ({CarCatalog.All.Count})  ▸", CustomMinimumSize = new Vector2(0, 32) };
        carsButton.Alignment = HorizontalAlignment.Left;
        rows.AddChild(carsButton);
        _cars = new ScrollContainer { CustomMinimumSize = new Vector2(0, 380), Visible = false };
        _cars.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        var carRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        carRows.AddThemeConstantOverride("separation", 6);
        _cars.AddChild(carRows);
        rows.AddChild(_cars);
        foreach (var car in CarCatalog.All)
            Entry(carRows, 0, car.Kind, car.Label, car.Blurb, true);
        carsButton.Pressed += () =>
        {
            _cars.Visible = !_cars.Visible;
            // the list takes the mounts' place, or the panel outgrows a 648 px screen
            for (int i = 0; i < _shortcuts; i++) _entries[i].Button.GetParent<Control>().Visible = !_cars.Visible;
            _lockNote.Visible = !Permissions.CanSpawnVehicles;
            carsButton.Text = $"{number}.  Cars  ({CarCatalog.All.Count})  {(_cars.Visible ? "▾" : "▸")}";
            if (_cars.Visible) PlayerInput.FocusFirst(_cars);
        };
        _carsButton = carsButton;

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeColorOverride("font_color", new Color(0.92f, 0.55f, 0.35f));
        rows.AddChild(_status);

        _hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(_hint);

        Permissions.Changed += Relabel;
        PlayerInput.DeviceChanged += Relabel;
        Relabel();
    }

    public override void _ExitTree()
    {
        Permissions.Changed -= Relabel;
        PlayerInput.DeviceChanged -= Relabel;
    }

    /// <summary>Everything that names a key or depends on being an admin, redone when either changes.</summary>
    private void Relabel()
    {
        if (_hint == null) return;
        _hint.Text = InputHints.Format(
            "1-9 to pick, {ride_menu} / Esc closes. Bikes, cars, helicopter and plane are left where you get off "
            + "({interact_mount}); {interact_mount} next to one gets back in.");
        foreach (var (line, blurb) in _blurbs) line.Text = InputHints.Format(blurb);

        bool locked = !Permissions.CanSpawnVehicles;
        _lockNote.Text = InputHints.Format(
            "Vehicles are spawned by an admin on this server. Walk up to one left in the world and press {interact_mount} to get in.");
        _lockNote.Visible = locked;

        var current = ActivePlayer?.Invoke()?.Ride ?? RideKind.OnFoot;
        foreach (var (kind, button, vehicle) in _entries)
        {
            button.Disabled = kind == current || (vehicle && locked);
            button.TooltipText = vehicle && locked ? "Admin only on this server" : "";
        }
    }

    private void Entry(Container into, int number, RideKind kind, string label, string blurb, bool vehicle)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);

        var button = new Button { Text = number > 0 ? $"{number}.  {label}" : label, CustomMinimumSize = new Vector2(0, 32) };
        button.Alignment = HorizontalAlignment.Left;
        button.Pressed += () => Choose(kind);
        box.AddChild(button);

        var line = new Label { Text = InputHints.Format(blurb), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        line.AddThemeFontSizeOverride("font_size", 12);
        line.AddThemeColorOverride("font_color", new Color(0.55f, 0.59f, 0.65f));
        box.AddChild(line);
        _blurbs.Add((line, blurb));

        into.AddChild(box);
        _entries.Add((kind, button, vehicle));
    }

    private void Choose(RideKind kind)
    {
        var player = ActivePlayer?.Invoke();
        if (player == null)
        {
            _status.Text = InputHints.Format("Nothing to mount — press {toggle_mode} to drop out of the fly camera first.");
            return;
        }

        // the server refuses to park one anyway; saying so here beats a vehicle that vanishes
        if (_entries.Any(e => e.Kind == kind && e.Vehicle) && !Permissions.CanSpawnVehicles)
        {
            _status.Text = "Only an admin can spawn vehicles on this server.";
            return;
        }

        if (player.SetRide(kind))
        {
            Close();
            return;
        }

        // The refusal is the interesting case, so name the actual reason rather than "no".
        _status.Text = player.IsSliding
            ? "Not mid-slide."
            : player.IsOnFloor()
                ? "Too fast — slow down first."
                : "Not in the air.";
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        // marks what you are already on, so the panel answers "what am I riding" too, and greys
        // the vehicles for a non-admin on a server
        Relabel();

        _status.Text = "";
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        // a controller player drives the list with the D-pad and A from here
        PlayerInput.FocusFirst(_panel);
    }

    public void Close()
    {
        _panel.Visible = false;
        UiFocus.Set(this, false);
        Core.MouseCapture.Capture();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // _UnhandledInput rather than _UnhandledKeyInput: a pad button is not a key event, and
        // Y / B / Start have to close this the way E and Esc do.
        if (!IsOpen || !@event.IsPressed() || @event.IsEcho()) return;

        if (@event.IsActionPressed(PlayerInput.RideMenu) || @event.IsActionPressed(PlayerInput.InteractMount)
            || @event.IsActionPressed(PlayerInput.Menu) || @event.IsActionPressed("ui_cancel"))
        {
            Close();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey key) return;

        // Key.Key1 is the physical "1", so the shortcuts land in the same place on an AZERTY
        // keyboard as on a QWERTY one — the same reason the movement keys are read physically.
        int index = (int)key.PhysicalKeycode - (int)Key.Key1;
        if (index == _shortcuts) { _carsButton.EmitSignal(BaseButton.SignalName.Pressed); GetViewport().SetInputAsHandled(); return; }
        if (index < 0 || index >= _shortcuts) return;

        Choose(_entries[index].Kind);
        GetViewport().SetInputAsHandled();
    }
}
