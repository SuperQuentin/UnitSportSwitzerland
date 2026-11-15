using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// Type-to-search teleport panel. Only places with terrain are listed, so every result
/// takes you somewhere you can actually stand.
///
/// <para>
/// By body (#2039): a stepper over the list (‹ Moon ›: LB / RB, Page Up / Page Down, a click or the VR ray) picks the
/// body, the one you are on when it opens. The Earth lists its Swiss towns (<see cref="PlaceIndex"/>); every other body
/// lists its <see cref="BodyPlaces"/> beside a map of it (its equirectangular photo, a place a dot, you a ring): a dot
/// clicked travels there, anywhere else on the map to that latitude and longitude. A pick on another body hands the
/// world over (<see cref="Teleporter"/>'s body jump), offline only.
/// </para>
/// </summary>
public partial class PlaceSearchUi : CanvasLayer
{
    private PlaceIndex _index = new();

    /// <summary>Every place of the index (the Battle Royale map names its towns from it).</summary>
    public IReadOnlyList<Place> All => _index.Places;
    private Teleporter _teleporter = null!;

    private PanelContainer _panel = null!;
    private LineEdit _query = null!;
    private ItemList _results = null!;
    private Label _hint = null!;
    private Label? _title;
    private List<Place> _shown = new();

    /// <summary>The bodies the stepper goes through: the Earth, then those with places, in the Sun's order.</summary>
    private static readonly BodyId[] Bodies = [BodyId.Earth, .. BodyPlaces.Bodies];

    private int _body;
    private List<BodyPlace> _bodyShown = new();
    private BodyMapView _map = null!;
    private Label _mapNote = null!;
    private Control _mapColumn = null!;
    private Button _prev = null!, _next = null!;

    /// <summary>The body the list shows.</summary>
    public BodyId ShownBody => Bodies[_body];

    /// <summary>
    /// The teleport itself lives in <see cref="Teleporter"/> rather than here, because what
    /// gets moved depends on what you are controlling at the time — fly camera, local player,
    /// or a networked player on a server.
    /// </summary>
    public static PlaceSearchUi Create(Teleporter teleporter) => new()
    {
        Name = "PlaceSearchUi",
        _teleporter = teleporter,
    };

    public override void _Ready()
    {
        Layer = 20;   // above the GPX HUD

        LoadIndex();

        _panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0, AnchorBottom = 0,
            OffsetLeft = -240, OffsetRight = 240, OffsetTop = 70,
            Visible = false,
            Theme = UiTheme.Get(),
        };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.94f, 10, 12));
        AddChild(_panel);

        var rows = UiKit.VBox(8);
        _panel.AddChild(rows);

        // the body stepper: ‹ Moon ›, the title between
        var head = UiKit.HBox(6);
        rows.AddChild(head);
        _prev = UiKit.Button("‹", minWidth: 38);
        _prev.TooltipText = "Previous body";
        _prev.Pressed += () => StepBody(-1);
        head.AddChild(_prev);
        _title = UiTheme.Title("", UiTheme.FontBody + 2);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.ClipText = true;
        head.AddChild(_title);
        _next = UiKit.Button("›", minWidth: 38);
        _next.TooltipText = "Next body";
        _next.Pressed += () => StepBody(1);
        head.AddChild(_next);

        var columns = UiKit.HBox(14);
        rows.AddChild(columns);
        var left = UiKit.VBox(8);
        left.CustomMinimumSize = new Vector2(432, 0);
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        columns.AddChild(left);

        _query = new LineEdit { PlaceholderText = "type a town name…" };
        _query.TextChanged += _ => Refresh();
        _query.TextSubmitted += _ => Go(0);
        left.AddChild(_query);

        _results = new ItemList { CustomMinimumSize = new Vector2(0, 250), AllowReselect = true };
        _results.ItemActivated += i => Go((int)i);
        _results.ItemSelected += i => Go((int)i);
        left.AddChild(_results);

        // another body's map, beside the list
        var mapColumn = UiKit.VBox(6);
        _mapColumn = mapColumn;
        _map = new BodyMapView { CustomMinimumSize = new Vector2(BodyMapView.Width, BodyMapView.Width / 2) };
        _map.Picked += i => Go(i);
        _map.PickedAt += GoTo;
        _map.Hovered += name => _mapNote.Text = name ?? MapNote();
        mapColumn.AddChild(_map);
        _mapNote = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _mapNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _mapNote.CustomMinimumSize = new Vector2(BodyMapView.Width, 0);
        mapColumn.AddChild(_mapNote);
        columns.AddChild(mapColumn);

        _hint = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextFaint);
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_hint);
        ShowBody(0);

        Refresh();

        // "--goto <town>" jumps there once the world is up, which beats looking up an LV95
        // easting and northing to pass to --at.
        if (ParseGotoArg() is { } wanted) Callable.From(() => GoToNamed(wanted)).CallDeferred();
    }

    /// <summary>Reads an optional "--goto &lt;town&gt;" from the command line.</summary>
    public static string? ParseGotoArg() => CmdArgs.Value("--goto");

    /// <summary>Places matching a query, best first: the same ranking the search panel uses.</summary>
    public List<Place> Search(string query, int limit = 12) => _index.Search(query, limit);

    /// <summary>The towns as loaded, for <c>/locate</c> offline (#1277).</summary>
    public PlaceIndex Index => _index;

    /// <summary>Jumps to the best match for a name. Returns false when nothing matched.</summary>
    public bool GoToNamed(string query)
    {
        var matches = _index.Search(query, limit: 1);
        if (matches.Count == 0)
        {
            GD.PushWarning($"[places] no town matching '{query}'");
            return false;
        }

        var place = matches[0];
        return _teleporter.TeleportTo(place.E, place.N, place.Name);
    }

    /// <summary>
    /// Reads the town index: local chunk directory first, then the streaming cache.
    ///
    /// <para>
    /// The cache is what makes this work on a client that shipped without terrain — the index
    /// arrives from the server after connecting, which is well after this UI was built, hence
    /// <see cref="ReloadIndex"/>.
    /// </para>
    /// </summary>
    private void LoadIndex()
    {
        foreach (string dir in new[] { TerrainPaths.FindChunkDir(), TerrainPaths.FindCacheDir() })
        {
            string path = System.IO.Path.Combine(dir, PlaceIndex.FileName);
            if (!System.IO.File.Exists(path)) continue;

            try
            {
                _index = PlaceIndex.FromJson(System.IO.File.ReadAllText(path));
                if (_index.Places.Count > 0) return;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[places] {path} could not be read: {e.Message}");
            }
        }

        GD.PushWarning(
            $"[places] no {PlaceIndex.FileName} found. Run the preprocessor with "
            + "--places --gwr <gwr data.sqlite>, or join a server and it will be sent.");
    }

    /// <summary>Re-reads the index after one arrives from the server, and refreshes the list.</summary>
    public void ReloadIndex()
    {
        LoadIndex();
        if (_title is not null) ShowBody(_body);
    }

    public bool IsOpen => _panel.Visible;

    public void Toggle() => SetOpen(!_panel.Visible);

    public void SetOpen(bool open)
    {
        _panel.Visible = open;
        // Movement reads physical keys directly, so the search box has to announce that it
        // owns the keyboard or typing a town name walks the player there the hard way.
        UiFocus.Set(this, open);

        if (open)
        {
            // the fly-cam holds the mouse captured; typing needs it released
            MouseCapture.Mode = Input.MouseModeEnum.Visible;
            _query.Clear();
            // the body you are on (the Earth from a body with no places: a giant, the Sun's space)
            int here = System.Array.IndexOf(Bodies, _teleporter.Body);
            ShowBody(here < 0 ? 0 : here);
            _query.GrabFocus();
        }
        else
        {
            _query.ReleaseFocus();
        }
    }

    private void Refresh()
    {
        _results.Clear();
        if (ShownBody == BodyId.Earth)
        {
            _shown = _index.Search(_query.Text);
            foreach (var p in _shown)
                _results.AddItem(Describe(p));
            return;
        }
        _bodyShown = BodyPlaces.Search(ShownBody, _query.Text);
        foreach (var p in _bodyShown)
            _results.AddItem(Describe(p));
        _map.Show(_bodyShown, ShownBody == _teleporter.Body ? _teleporter.Here()?.Geodetic : null);
    }

    /// <summary>A place on another body: its name and what it is.</summary>
    internal static string Describe(BodyPlace p) => $"{p.Name}   ({p.Kind})";

    /// <summary>Steps the body shown by one, round the list.</summary>
    public void StepBody(int step) => ShowBody((_body + step + Bodies.Length) % Bodies.Length);

    /// <summary>Shows a body's places by its place in the stepper (and its map, off the Earth).</summary>
    public void ShowBody(int index)
    {
        _body = index;
        var body = ShownBody;
        bool earth = body == BodyId.Earth;
        string name = CelestialBody.Of(body).Name;
        int count = earth ? _index.Places.Count : BodyPlaces.For(body).Count;
        string here = body == _teleporter.Body ? " · you are here" : "";
        _title!.Text = earth ? $"Earth — {count} places with terrain{here}" : $"{name} — {count} places{here}";
        _query.PlaceholderText = earth ? "type a town name…" : $"type a place on {name}…";
        _mapColumn.Visible = !earth;
        _panel.OffsetLeft = earth ? -240 : -450;
        _panel.OffsetRight = earth ? 240 : 450;
        _panel.ResetSize();
        if (!earth) _map.SetBody(body);
        _mapNote.Text = MapNote();
        _hint.Text = HintText();
        Refresh();
    }

    /// <summary>Shows a body's places, for the checks and the shots.</summary>
    public void ShowBody(BodyId body)
    {
        int i = System.Array.IndexOf(Bodies, body);
        if (i >= 0) ShowBody(i);
    }

    private string MapNote() => ShownBody == BodyId.Earth ? "" :
        $"Click a dot to go there, or anywhere else on {CelestialBody.Of(ShownBody).Name} to land at that spot.";

    private string HintText()
    {
        if (!Permissions.CanTeleport) return "This server does not allow teleporting";
        string steps = InputHints.Pad
            ? $"{InputHints.Button(JoyButton.LeftShoulder)} {InputHints.Button(JoyButton.RightShoulder)} other bodies"
            : $"{InputHints.Keyboard(Key.Pageup)} {InputHints.Keyboard(Key.Pagedown)} other bodies";
        string close = $"{(InputHints.Pad ? InputHints.Button(JoyButton.B) : InputHints.Keyboard(Key.Escape))} to close";
        if (_teleporter.RefusalFor(ShownBody, _teleporter.ActiveTarget()) is { } why) return $"{why}\n{steps}   {close}";
        return $"{(InputHints.Pad ? InputHints.Button(JoyButton.A) : InputHints.Keyboard(Key.Enter))} to jump   {steps}   {close}";
    }

    /// <summary>
    /// A summit is described by its height and a town by its size, because those are the two
    /// different things that make one worth picking out of a list. Labelling a mountain
    /// "0 buildings" would be true and useless.
    /// </summary>
    internal static string Describe(Place p) => p.Kind switch
    {
        PlaceKind.Summit => $"{p.Name}   ▲ {p.Elevation} m",
        PlaceKind.Pass => $"{p.Name}   ⌒ pass, {p.Elevation} m",
        _ => string.IsNullOrEmpty(p.Canton)
            ? $"{p.Name}   ({p.Buildings} buildings)"
            : $"{p.Name}, {p.Canton}   ({p.Buildings} buildings)",
    };

    private void Go(int index)
    {
        // a server without teleporting (#799): say so where the jump was asked for
        if (!Permissions.CanTeleport)
        {
            _hint.Text = "This server does not allow teleporting. Walk, ride or fly there.";
            return;
        }
        if (ShownBody != BodyId.Earth)
        {
            if (index < 0 || index >= _bodyShown.Count) return;
            var p = _bodyShown[index];
            Travel(p.Body, p.LatDeg, p.LonDeg, p.Name, (p.LandEast, p.LandNorth));
            return;
        }
        if (index < 0 || index >= _shown.Count) return;
        var place = _shown[index];
        // from another body a Swiss place is still a Swiss place: the teleporter hands the world back to the Earth
        if (_teleporter.Body != BodyId.Earth && _teleporter.RefusalFor(BodyId.Earth, _teleporter.ActiveTarget()) is { } why)
        {
            _hint.Text = why;
            return;
        }
        _teleporter.TeleportTo(place.E, place.N, place.Name);
        SetOpen(false);
    }

    /// <summary>A spot clicked on a body's map (no place there): that latitude and longitude.</summary>
    private void GoTo(double lat, double lon)
    {
        if (!Permissions.CanTeleport) return;
        var body = ShownBody;
        Travel(body, lat, lon, System.FormattableString.Invariant($"{CelestialBody.Of(body).Name} {lat:F1}°, {lon:F1}°"), default);
    }

    private void Travel(BodyId body, double lat, double lon, string label, (double, double) land)
    {
        if (_teleporter.RefusalFor(body, _teleporter.ActiveTarget()) is { } why)
        {
            _hint.Text = why;
            return;
        }
        if (_teleporter.TeleportTo(body, lat, lon, label, land)) SetOpen(false);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!_panel.Visible) return;
        if (@event is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape })
        {
            SetOpen(false);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>LB / RB and Page Up / Page Down step the body, whatever has the focus (the field keeps the typing).</summary>
    public override void _Input(InputEvent e)
    {
        if (!_panel.Visible || !e.IsPressed() || e.IsEcho()) return;
        int step = e switch
        {
            InputEventJoypadButton { ButtonIndex: JoyButton.LeftShoulder } => -1,
            InputEventJoypadButton { ButtonIndex: JoyButton.RightShoulder } => 1,
            InputEventKey { PhysicalKeycode: Key.Pageup } => -1,
            InputEventKey { PhysicalKeycode: Key.Pagedown } => 1,
            _ => 0,
        };
        if (step == 0) return;
        StepBody(step);
        GetViewport().SetInputAsHandled();
    }
}
