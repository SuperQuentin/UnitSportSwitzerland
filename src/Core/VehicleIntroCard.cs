using Godot;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// The mini tutorial of a ride (#517): the first time the player drives, rides or flies each kind
/// (<see cref="VehicleIntroKind"/>), a card in the top-left corner lists its few essential controls,
/// and ticks each off as it is used. All ticked, the kind is saved in
/// <see cref="GameSettings.VehicleIntrosSeen"/> and never shown again; getting off before that
/// shows it again next time. Only the driver sees it, never a passenger. The rows are
/// <see cref="VehicleIntros"/>; like <see cref="Tutorial"/> it takes no input of its own.
/// </summary>
public partial class VehicleIntroCard : CanvasLayer
{
    /// <summary>Keys already held when the card appears (W from walking up to a car) do not count.</summary>
    private const double Grace = 0.6;
    /// <summary>All ticked: shown green this long, then gone.</summary>
    private const double DoneHold = 1.2;

    private readonly Func<FootPlayer?> _walker;
    private readonly Func<bool> _covered;

    private Rideable? _lastRide;
    private VehicleIntro? _intro;
    private bool[] _ticked = Array.Empty<bool>();
    private double _shownFor, _doneFor = -1;

    private PanelContainer _panel = null!;
    private Label _title = null!, _footer = null!;
    private VBoxContainer _rows = null!;
    private readonly List<(Label Mark, Label Text)> _rowLabels = new();

    /// <summary>A card is up: the first-run tutorial waits under it.</summary>
    public bool Showing => _intro != null;

    /// <param name="walker">The local player while on foot or mounted; null in the fly camera.</param>
    /// <param name="covered">Something owns the screen (a menu, the travel menu, the map): the card hides.</param>
    public VehicleIntroCard(Func<FootPlayer?> walker, Func<bool> covered)
    {
        Name = "VehicleIntro";
        _walker = walker;
        _covered = covered;
    }

    public override void _Ready()
    {
        Layer = 11;   // the prompt bar's, like the tutorial

        _panel = new PanelContainer { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 14));
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _panel.OffsetLeft = 18; _panel.OffsetTop = 18;
        _panel.CustomMinimumSize = new Vector2(400, 0);
        AddChild(_panel);

        var column = UiKit.VBox(6);
        column.MouseFilter = Control.MouseFilterEnum.Ignore;
        _panel.AddChild(column);

        var head = UiKit.HBox(10);
        head.MouseFilter = Control.MouseFilterEnum.Ignore;
        _title = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(_title);
        head.AddChild(UiKit.Text("New ride", UiTheme.FontSmall, UiTheme.Amber));
        column.AddChild(head);

        _rows = UiKit.VBox(4);
        _rows.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AddChild(_rows);

        _footer = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint, wrap: true);
        _footer.CustomMinimumSize = new Vector2(372, 0);
        column.AddChild(_footer);

        PlayerInput.DeviceChanged += Words;
    }

    public override void _ExitTree() => PlayerInput.DeviceChanged -= Words;

    /// <summary>The kind of intro a ride gets, or null (airstairs, a parked trailer: nothing to learn there).</summary>
    public static VehicleIntroKind? KindOf(Rideable ride) => ride switch
    {
        Airliner => VehicleIntroKind.Airliner,
        Helicopter => VehicleIntroKind.Helicopter,
        Player.Plane => VehicleIntroKind.Plane,
        Pigeon => VehicleIntroKind.Pigeon,
        Wingsuit => VehicleIntroKind.Wingsuit,
        Canopy => VehicleIntroKind.Canopy,
        Steamer => VehicleIntroKind.Steamer,
        Boat => VehicleIntroKind.Boat,
        Truck => VehicleIntroKind.Truck,
        Car => VehicleIntroKind.Car,
        Motorbike => VehicleIntroKind.Motorbike,
        Bicycle => VehicleIntroKind.RoadBike,
        Skis => VehicleIntroKind.Skis,
        _ => null,
    };

    public override void _Process(double delta)
    {
        var walker = _walker();
        // the driver only: a passenger's seat (SeatIndex > 0) or a ride somebody else hosts is not theirs to learn
        var ride = walker != null && IsInstanceValid(walker) && walker.SeatIndex == 0 && walker.Host == null
            ? walker.Vehicle : null;

        if (_intro == null)
        {
            if (ride != null && ride != _lastRide && KindOf(ride) is { } kind
                && !VehicleIntros.Seen(GameSettings.Current.VehicleIntrosSeen, kind))
                Show(VehicleIntros.For(kind));
            _lastRide = ride;
            return;
        }

        // got off (or into something else) before trying it all: next time again
        if (ride == null || KindOf(ride) != _intro.Kind)
        {
            Close(seen: false);
            _lastRide = ride;
            return;
        }

        bool covered = _covered();
        _panel.Visible = !covered;
        if (covered) return;

        if (_doneFor >= 0)
        {
            _doneFor += delta;
            if (_doneFor >= DoneHold) Close(seen: true);
            return;
        }

        _shownFor += delta;
        if (_shownFor < Grace || UiFocus.TextEntryActive) return;
        bool all = true;
        for (int i = 0; i < _ticked.Length; i++)
        {
            if (!_ticked[i] && Pressed(_intro.Rows[i].Actions))
            {
                _ticked[i] = true;
                Tick(i);
            }
            all &= _ticked[i];
        }
        if (!all) return;
        _doneFor = 0;
        _title.AddThemeColorOverride("font_color", UiTheme.Good);
        GD.Print($"[intro] {_intro.Kind} done");
    }

    private static bool Pressed(string[] actions)
    {
        foreach (var a in actions)
            if (Input.IsActionPressed(a)) return true;
        return false;
    }

    private void Show(VehicleIntro intro)
    {
        _intro = intro;
        _ticked = new bool[intro.Rows.Length];
        _shownFor = 0;
        _doneFor = -1;
        _title.Text = intro.Title;
        _title.AddThemeColorOverride("font_color", UiTheme.Text);

        foreach (var child in _rows.GetChildren()) child.QueueFree();
        _rowLabels.Clear();
        foreach (var _ in intro.Rows)
        {
            var line = UiKit.HBox(8);
            line.MouseFilter = Control.MouseFilterEnum.Ignore;
            var mark = UiKit.Text("○", UiTheme.FontBody, UiTheme.TextFaint);
            mark.CustomMinimumSize = new Vector2(16, 0);
            var text = UiKit.Text("", UiTheme.FontBody, UiTheme.Text, wrap: true);
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            text.CustomMinimumSize = new Vector2(340, 0);
            line.AddChild(mark);
            line.AddChild(text);
            _rows.AddChild(line);
            _rowLabels.Add((mark, text));
        }
        Words();

        _panel.Visible = !_covered();
        _panel.Modulate = new Color(1, 1, 1, 0);
        _panel.CreateTween().TweenProperty(_panel, "modulate:a", 1f, 0.25f);
        GD.Print($"[intro] {intro.Kind}");
    }

    /// <summary>The rows' words for the device in hand (again when it changes).</summary>
    private void Words()
    {
        if (_intro == null) return;
        bool pad = InputHints.Pad;
        for (int i = 0; i < _rowLabels.Count; i++)
            _rowLabels[i].Text.Text = InputHints.Format(VehicleIntros.Text(_intro.Rows[i], pad));
        _footer.Text = InputHints.Format(_intro.Footer + "  ·  {help} every control");
    }

    private void Tick(int i)
    {
        var (mark, text) = _rowLabels[i];
        mark.Text = "✓";
        mark.AddThemeColorOverride("font_color", UiTheme.Good);
        text.AddThemeColorOverride("font_color", UiTheme.TextDim);
    }

    private void Close(bool seen)
    {
        if (seen && _intro != null && !VehicleIntros.Seen(GameSettings.Current.VehicleIntrosSeen, _intro.Kind))
        {
            GameSettings.Current.VehicleIntrosSeen.Add(_intro.Kind.ToString());
            // only this key: a command-line --rings must not become the saved choice
            GameSettings.SaveOnly(nameof(GameSettings.VehicleIntrosSeen),
                new System.Text.Json.Nodes.JsonArray(GameSettings.Current.VehicleIntrosSeen.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()));
        }
        _intro = null;
        _panel.Visible = false;
    }
}
