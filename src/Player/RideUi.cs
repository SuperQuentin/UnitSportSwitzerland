using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// The "what am I travelling as" picker, opened with R (pad: Y with nothing to interact with).
///
/// <para>
/// A menu rather than a cycle key, for two reasons: the list is meant to grow, and a refusal
/// needs somewhere to be explained. On a server, the vehicles in it (anything left in the world
/// when you get out) are an admin's to spawn â€” see <see cref="Permissions"/>; the rows stay
/// listed, greyed, so the reason is visible rather than the vehicles simply missing. You cannot get on a bike while airborne or step off skis at
/// 70 km/h, and a key that silently does nothing in those moments reads as a broken key â€” so the
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
    /// <summary>The car preset (#40): put on the car being driven, and on any car picked from here.</summary>
    private OptionButton _setup = null!;
    /// <summary>Entries reachable by number key: the mounts, not the car list.</summary>
    private int _shortcuts;
    /// <summary>The folded rosters (cars, motorbikes), on the number keys after the mounts.</summary>
    private readonly List<(Button Button, ScrollContainer List)> _folds = new();

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

        // The cars and the motorbikes are rosters, not a line each: one button folds a scrolling
        // list open, so the mounts above stay on screen and in reach of the number keys.
        Fold(rows, number, "Cars", CarCatalog.All.Select(c => (c.Kind, c.Label, c.Blurb)));
        SetupRow(rows);
        Fold(rows, number + 1, "Motorbikes", MotorbikeCatalog.All.Select(b => (b.Kind, b.Label, b.Blurb)));
        Fold(rows, number + 2, "Trucks and buses", HeavyCatalog.All.Select(h => (h.Kind, h.Label,
            h.Blurb + (h.Look.Operator.Length > 0 ? $" ({h.Look.Operator} colours)" : ""))));
        // trailers are not mounts: each row couples one behind the truck being driven, or leaves it
        // in the world ahead to back onto (RideKind.Trailer + its index, decoded in Choose)
        Fold(rows, number + 3, "Trailers", TrailerCatalog.All.Select((t, i) => ((RideKind)(TrailerRow + i), t.Label,
            t.Blurb + (t.Operator.Length > 0 ? $" ({t.Operator} colours)" : ""))));
        LoadRow(rows);

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
            "1-9 to pick, {ride_menu} / Esc closes. Bikes, cars, motorbikes, helicopter and plane are left where you get off "
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

    private void Fold(Container rows, int number, string name, IEnumerable<(RideKind Kind, string Label, string Blurb)> items)
    {
        var list = items.ToList();
        string Title(bool open) => $"{number}.  {name}  ({list.Count})  {(open ? "â–¾" : "â–¸")}";
        var button = new Button { Text = Title(false), CustomMinimumSize = new Vector2(0, 32), Alignment = HorizontalAlignment.Left };
        rows.AddChild(button);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 380), Visible = false };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        var into = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        into.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(into);
        rows.AddChild(scroll);
        foreach (var (kind, label, blurb) in list) Entry(into, 0, kind, label, blurb, vehicle: true);
        button.Pressed += () =>
        {
            bool open = !scroll.Visible;
            // one list at a time, in the mounts' place, or the panel outgrows a 648 px screen
            foreach (var (b, l) in _folds) l.Visible = false;
            scroll.Visible = open;
            for (int i = 0; i < _shortcuts; i++) _entries[i].Button.GetParent<Control>().Visible = !open;
            foreach (var (b, l) in _folds) b.Text = b == button ? Title(open) : b.Text.Replace("â–¾", "â–¸");
            _lockNote.Visible = !Permissions.CanSpawnVehicles;
            if (open) PlayerInput.FocusFirst(scroll);
        };
        _folds.Add((button, scroll));
    }

    /// <summary>
    /// The car preset chooser, data-driven from <see cref="CarSetups.All"/>: the one piece of UI
    /// for #40, meant to be replaced by the garage (#56), which calls
    /// <see cref="FootPlayer.SetCarSetup"/> the same way.
    /// </summary>
    private void SetupRow(Container rows)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "Car preset" });
        _setup = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var s in CarSetups.All) _setup.AddItem(s.Name, s.Id);
        row.AddChild(_setup);
        rows.AddChild(row);
        var blurb = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = CarSetups.All[0].Blurb };
        blurb.AddThemeFontSizeOverride("font_size", 12);
        blurb.AddThemeColorOverride("font_color", new Color(0.55f, 0.59f, 0.65f));
        rows.AddChild(blurb);
        _setup.ItemSelected += i =>
        {
            var setup = CarSetups.For(_setup.GetItemId((int)i));
            blurb.Text = setup.Blurb;
            // in a car: on it now, if it is standing still
            if (ActivePlayer?.Invoke() is { Vehicle: Car } player)
                _status.Text = player.SetCarSetup(setup.Id) ? $"{setup.Name} fitted." : "Stop the car first.";
        };
    }

    /// <summary>Picker rows for trailers carry this plus the trailer's index as their kind; never a real mount.</summary>
    private const int TrailerRow = 1000;
    private OptionButton _load = null!;
    private static readonly (string Name, float Load)[] Loads = { ("Empty", 0f), ("Half", 0.5f), ("Full", 1f) };

    /// <summary>How full the next truck, bus or trailer comes: cargo, or passengers.</summary>
    private void LoadRow(Container rows)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "Load (trucks, buses, trailers)" });
        _load = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var (name, _) in Loads) _load.AddItem(name);
        _load.Select(1);
        row.AddChild(_load);
        rows.AddChild(row);
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
            _status.Text = InputHints.Format("Nothing to mount â€” press {toggle_mode} to drop out of the fly camera first.");
            return;
        }

        // the server refuses to park one anyway; saying so here beats a vehicle that vanishes
        if (_entries.Any(e => e.Kind == kind && e.Vehicle) && !Permissions.CanSpawnVehicles)
        {
            _status.Text = "Only an admin can spawn vehicles on this server.";
            return;
        }

        float load = Loads[Mathf.Clamp(_load.Selected, 0, Loads.Length - 1)].Load;
        if ((int)kind >= TrailerRow)
        {
            if (player.SpawnTrailer((int)kind - TrailerRow, load)) { Close(); return; }
            _status.Text = player.Vehicle is Truck t
                ? (t.Trailer != null ? "Uncouple the trailer you have first." : "That trailer does not fit this vehicle, or it is moving.")
                : "Get off first: a trailer is left 14 m ahead of you.";
            return;
        }
        player.NextLoad = load;
        if (player.SetRide(kind))
        {
            if (CarCatalog.IsCar(kind)) player.SetCarSetup(_setup.GetSelectedId());
            Close();
            return;
        }

        // The refusal is the interesting case, so name the actual reason rather than "no".
        _status.Text = player.IsSliding
            ? "Not mid-slide."
            : player.IsOnFloor()
                ? "Too fast â€” slow down first."
                : "Not in the air.";
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (Permissions.RidesLocked) return;   // in a Battle Royale match you ride what you find
        // marks what you are already on, so the panel answers "what am I riding" too, and greys
        // the vehicles for a non-admin on a server
        Relabel();
        // the chooser shows what is on the car being driven
        if (ActivePlayer?.Invoke() is { Vehicle: Car } driver) _setup.Select(_setup.GetItemIndex(driver.CarSetupId));

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
        // keyboard as on a QWERTY one â€” the same reason the movement keys are read physically.
        int index = (int)key.PhysicalKeycode - (int)Key.Key1;
        if (index >= _shortcuts && index < _shortcuts + _folds.Count)
        {
            _folds[index - _shortcuts].Button.EmitSignal(BaseButton.SignalName.Pressed);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (index < 0 || index >= _shortcuts) return;

        Choose(_entries[index].Kind);
        GetViewport().SetInputAsHandled();
    }
}
