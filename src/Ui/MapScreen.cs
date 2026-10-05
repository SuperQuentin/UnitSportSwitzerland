using Godot;
using UnitSport.Core;
using UnitSport.Map;
using UnitSport.Terrain.Format;

namespace UnitSport.Ui;

/// <summary>
/// The map of Switzerland, and the two things it is for (#515): seeing and choosing what terrain to
/// download, and choosing where to land. One screen rather than two, because they are the same
/// question asked twice — "is there real ground where I am going?" is answered by looking at the map
/// you would have downloaded it on.
///
/// <para>
/// <b>Library</b> (from the title screen) selects and downloads. <b>Landing</b> (when a world starts
/// or a server is joined) adds a marker you confirm, and keeps the selecting and downloading, so a
/// landing zone with no terrain can be fixed where it is noticed instead of being reported after the
/// loading screen.
/// </para>
///
/// <para>
/// The work behind it is <c>tools/MapCore</c>, the same library the terminal region wizard
/// (<c>tools/MapSetup</c>) runs on: the country map, the selection, the scan of what is on disk, and
/// the plan with its download, disk and time estimates.
/// </para>
/// </summary>
public partial class MapScreen : Screen
{
    /// <summary>Which of the screen's two jobs this instance is doing.</summary>
    public enum Role
    {
        /// <summary>Reached from the title screen: look at what you have, pick more, download it.</summary>
        Library,

        /// <summary>Shown as a world starts: the same, plus the landing marker and a Land here button.</summary>
        Landing,
    }

    private readonly Role _role;
    private readonly Action<(double E, double N)>? _confirmLanding;

    private CountryData _country = null!;
    private Selection _selection = null!;
    private LocalState _local = null!;
    private Paths _paths = null!;
    private Stats _stats = null!;
    private SetupState _state = null!;

    private MapCanvas _map = null!;
    private LineEdit _search = null!;
    private VBoxContainer _results = null!;
    private VBoxContainer _summary = null!;
    private Label _hint = null!;
    private Button _primary = null!;
    private readonly Button[] _toolButtons = new Button[3];

    private Layers _layers = Layers.Terrain | Layers.Roads | Layers.Places;
    private PanelContainer? _panel;

    public static MapScreen Create() => new(Role.Library, null) { Name = "Map" };

    /// <summary>The landing role: the marker starts at <paramref name="start"/> and
    /// <paramref name="confirm"/> is called with where the player chose to land.</summary>
    public static MapScreen CreateLanding((double E, double N) start, Action<(double E, double N)> confirm) =>
        new(Role.Landing, confirm) { Name = "Landing", StartLanding = start };

    private (double E, double N)? StartLanding { get; init; }

    private MapScreen(Role role, Action<(double E, double N)>? confirmLanding)
    {
        _role = role;
        _confirmLanding = confirmLanding;
    }

    public override void _Ready()
    {
        // The country map is embedded in MapCore, so this cannot fail for want of a data folder;
        // a corrupt one still can, and the screen has to say so rather than take the title down.
        try
        {
            _country = CountryData.LoadEmbedded();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[map] no country map: {e.Message}");
            ShowBroken(e.Message);
            return;
        }

        _paths = Paths.ForGame(TerrainPaths.FindDataDir(), TerrainPaths.FindChunkDir());
        _selection = new Selection(_country);
        _local = LocalState.Scan(_paths);
        _stats = Stats.Load(_paths);
        _state = SetupState.Load(_paths);
        _layers = _state.Layers;

        Build();
    }

    private void ShowBroken(string message)
    {
        var (body, _) = Framed("Map", "the country map could not be read", new Vector2(640, 260));
        body.AddChild(UiKit.Text(message, UiTheme.FontSmall, UiTheme.Bad, wrap: true));
        body.AddChild(UiKit.Text("The map is embedded in the game, so this is a broken build rather than "
                                 + "missing data. The terminal wizard can rebuild it with --bake.",
            UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
    }

    private void Build()
    {
        // Sized to the window like the travel menu and the controls screen (#210), because a map
        // wants every pixel it can get rather than a fixed panel.
        var (body, header) = Framed(_role == Role.Landing ? "Where do you want to land?" : "Map of Switzerland",
            _role == Role.Landing
                ? "Shift-click to move the marker. Pick more terrain to download while you are here."
                : "What you have, and what to download next",
            PanelSize());
        // the glass panel Framed built, so a window resize can follow (ControlsHelp does the same)
        _panel = body.GetParent()?.GetParent() as PanelContainer;
        GetViewport().SizeChanged += FitToWindow;

        var legend = UiKit.HBox(14);
        legend.AddChild(Chip(new Color(0.16f, 0.80f, 0.80f), "built"));
        legend.AddChild(Chip(new Color(0.24f, 0.45f, 0.92f), "downloaded"));
        legend.AddChild(Chip(new Color(1.00f, 0.75f, 0.12f), "selected"));
        header.AddChild(legend);

        var columns = UiKit.HBox(16);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(columns);

        // ---- the map
        _map = new MapCanvas(_country, _selection, _local)
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _map.SelectionChanged += RefreshSummary;
        if (StartLanding is { } start)
        {
            _map.SetLanding(start.E, start.N);
            _map.FocusOn(start.E, start.N, 8);
            _map.LandingMoved += (_, _) => RefreshSummary();
        }
        else if (_local.Built.Count > 0)
        {
            // open on what the player already has, not on an arbitrary centre
            var centre = Centre(_local.Built);
            _map.FocusOn((centre.E + 0.5) * 1000.0, (centre.N + 0.5) * 1000.0, 8);
        }
        var mapFrame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        mapFrame.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.03f, 0.04f, 0.055f), 10, 2, 2,
            UiTheme.Hairline, 1));
        mapFrame.AddChild(_map);
        columns.AddChild(mapFrame);

        // ---- the side panel
        var (scroll, side) = UiKit.ScrollPage(8);
        scroll.CustomMinimumSize = new Vector2(330, 0);
        scroll.SizeFlagsHorizontal = SizeFlags.Fill;
        columns.AddChild(scroll);

        BuildSearch(side);
        BuildTools(side);
        BuildSummary(side);
        BuildLayers(side);
        BuildRequirements(side);

        // ---- the footer
        var footer = UiKit.HBox(10);
        body.AddChild(footer);
        _hint = UiKit.Text(InputHints.Format(
            "Drag to draw  ·  right-drag or {ui_left}/{ui_right} to pan  ·  wheel or {map_zoom_in}/{map_zoom_out} to zoom"
            + "  ·  {map_tool} tool  ·  {map_search} search"),
            UiTheme.FontTiny, UiTheme.TextFaint);
        _hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(_hint);

        var clear = UiKit.Button("Clear selection");
        clear.Pressed += () => { _selection.Clear(); RefreshSummary(); };
        footer.AddChild(clear);

        _primary = UiKit.Button(_role == Role.Landing ? "Land here" : "Download selection", primary: true, minWidth: 190);
        _primary.Pressed += OnPrimary;
        footer.AddChild(_primary);

        RefreshSummary();
    }

    /// <summary>
    /// Nearly the whole window: a map wants every pixel it can get. Taken from the viewport rather
    /// than the window, which has not settled at <c>_Ready</c> when a resolution was asked for on
    /// the command line, and followed on resize.
    /// </summary>
    private Vector2 PanelSize()
    {
        var view = GetViewport().GetVisibleRect().Size;
        return new Vector2(Mathf.Max(720, view.X - 56), Mathf.Max(480, view.Y - 52));
    }

    private void FitToWindow()
    {
        if (_panel == null) return;
        var size = PanelSize();
        _panel.CustomMinimumSize = size;
        _panel.Size = size;
    }

    private static Control Chip(Color colour, string label)
    {
        var row = UiKit.HBox(5);
        row.AddChild(UiKit.StatusDot(colour));
        row.AddChild(UiKit.Text(label, UiTheme.FontTiny, UiTheme.TextDim));
        return row;
    }

    // ---- the side panel ------------------------------------------------------------------------

    private void BuildSearch(Container into)
    {
        into.AddChild(UiKit.Section("Find a place"));
        _search = new LineEdit { PlaceholderText = "Zermatt, Bern, Matterhorn…" };
        _search.TextChanged += _ => RefreshResults();
        _search.TextSubmitted += _ =>
        {
            if (_results.GetChildCount() > 0 && _results.GetChild(0) is Button first) first.EmitSignal(BaseButton.SignalName.Pressed);
        };
        into.AddChild(_search);
        _results = UiKit.VBox(2);
        into.AddChild(_results);
    }

    private void RefreshResults()
    {
        foreach (var child in _results.GetChildren()) child.QueueFree();
        if (_search.Text.Length < 2) return;

        foreach (var town in _country.Search(_search.Text, 7))
        {
            int elevation = _country.MaxElevation(town.Tile);
            var row = UiKit.MenuButton($"{town.Name}  ·  {town.Canton}"
                                       + (town.Kind == PlaceKind.Town ? "" : elevation > 0 ? $"  ·  {elevation} m" : ""), 15);
            row.Pressed += () =>
            {
                _map.FocusOn(town.E, town.N, 12);
                _map.GrabFocus();
            };
            _results.AddChild(row);
        }
    }

    private void BuildTools(Container into)
    {
        into.AddChild(UiKit.Section("Draw"));
        var row = UiKit.HBox(6);
        into.AddChild(row);
        var names = new[] { "Rectangle", "Brush", "Erase" };
        for (int i = 0; i < names.Length; i++)
        {
            var tool = (MapCanvas.Tool)i;
            var button = UiKit.Button(names[i]);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Pressed += () => SetTool(tool);
            _toolButtons[i] = button;
            row.AddChild(button);
        }
        SetTool(MapCanvas.Tool.Rectangle);

        var around = UiKit.HBox(6);
        into.AddChild(around);
        foreach (int km in new[] { 3, 8, 20 })
        {
            var button = UiKit.Button($"+{km} km here");
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.TooltipText = $"Select everything within {km} km of the tile under the pointer";
            button.Pressed += () =>
            {
                if (_map.Hovered is not { } t) return;
                _selection.AddCircle((t.E + 0.5) * 1000.0, (t.N + 0.5) * 1000.0, km);
                RefreshSummary();
            };
            around.AddChild(button);
        }

        var cantonRow = UiKit.HBox(6);
        into.AddChild(cantonRow);
        var cantons = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cantons.AddItem("Whole canton…");
        foreach (var canton in _country.Cantons) cantons.AddItem($"{canton.Name} ({canton.Code})");
        cantons.ItemSelected += index =>
        {
            if (index <= 0) return;
            _selection.AddCanton(_country.Cantons[(int)index - 1]);
            RefreshSummary();
            cantons.Selected = 0;
        };
        cantonRow.AddChild(cantons);
    }

    private void SetTool(MapCanvas.Tool tool)
    {
        _map.CurrentTool = tool;
        for (int i = 0; i < _toolButtons.Length; i++)
            _toolButtons[i].AddThemeColorOverride("font_color", (MapCanvas.Tool)i == tool ? UiTheme.Amber : UiTheme.TextDim);
    }

    private void BuildSummary(Container into)
    {
        into.AddChild(UiKit.Section("This selection"));
        _summary = UiKit.VBox(3);
        into.AddChild(_summary);
    }

    /// <summary>
    /// The estimate, from the same <see cref="Planner"/> the terminal wizard prints as a table: the
    /// steps that would actually run, and what they would cost. Recomputed on every selection
    /// change, which is cheap — the plan is arithmetic over the selection, with no file reading.
    /// </summary>
    private void RefreshSummary()
    {
        foreach (var child in _summary.GetChildren()) child.QueueFree();

        int selected = _selection.Count;
        int already = _selection.Tiles.Count(t => _local.Built.Contains(t));
        Row("Selected", selected == 0 ? "nothing yet" : $"{selected:N0} km²");
        if (selected > 0 && already > 0) Row("Already built", $"{already:N0} km²");
        Row("On this machine", $"{_local.Built.Count:N0} km² built, {_local.Downloaded.Count:N0} downloaded");

        if (_role == Role.Landing) RowLandingWarning();

        if (selected == 0)
        {
            _primary.Disabled = _role != Role.Landing;
            return;
        }
        _primary.Disabled = false;

        var steps = Planner.Build(new SetupContext
        {
            Paths = _paths, Country = _country, Local = _local, Selection = _selection, Layers = _layers,
            Stats = _stats, State = _state,
            // In the game there is no Python and no GDAL: the C# downloader and the in-process
            // preprocessor replace them (#515 phases 1 and 2). The two GDAL layers say so instead.
            Python = null, Gdal = false,
        });

        long download = steps.Where(s => s.Skip == null).Sum(s => s.DownloadBytes);
        long disk = steps.Where(s => s.Skip == null).Sum(s => s.DiskBytes);
        double seconds = steps.Where(s => s.Skip == null).Sum(s => s.Seconds);
        Row("To download", Bytes(download));
        Row("Disk needed", Bytes(disk));
        Row("Estimated time", Duration(seconds));
    }

    /// <summary>
    /// The landing role's whole point: say plainly when there is no real ground where the player is
    /// about to arrive, and make the fix one click away rather than a thing to go and find.
    /// </summary>
    private void RowLandingWarning()
    {
        if (_map.Landing is not { } landing) return;
        var tile = TileId.FromLv95(landing.E, landing.N);
        int near = 0;
        for (int de = -4; de <= 4; de++)
            for (int dn = -4; dn <= 4; dn++)
                if (_local.Built.Contains(new TileId(tile.E + de, tile.N + dn))) near++;

        if (near > 0)
        {
            Row("Landing zone", $"{near} km² of real terrain within 4 km");
            return;
        }

        var warning = UiKit.Text(
            "No real terrain within 4 km of the marker — you will fly over generated ground. "
            + "Select the area and download it, or move the marker to terrain you already have.",
            UiTheme.FontSmall, UiTheme.Warn, wrap: true);
        _summary.AddChild(warning);

        var fix = UiKit.Button("Select 8 km around the marker");
        fix.Pressed += () =>
        {
            _selection.AddCircle(landing.E, landing.N, 8);
            RefreshSummary();
        };
        _summary.AddChild(fix);
    }

    private void Row(string name, string value)
    {
        var row = UiKit.HBox(8);
        var label = UiKit.Text(name, UiTheme.FontSmall, UiTheme.TextDim);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        row.AddChild(UiKit.Text(value, UiTheme.FontSmall, UiTheme.Text));
        _summary.AddChild(row);
    }

    private void BuildLayers(Container into)
    {
        into.AddChild(UiKit.Section("What to build"));
        AddLayer(into, Layers.Terrain, "Terrain", "The ground itself. Everything else stands on it.", locked: true);
        AddLayer(into, Layers.Roads, "Roads, rail, water, trees", "swissTLM3D: one nationwide file, fetched once.");
        AddLayer(into, Layers.Places, "Place names", "The in-game place search.");
        AddLayer(into, Layers.Cadastre, "Building use and storeys", "The GWR register.");
        AddLayer(into, Layers.Buildings, "Buildings", "Needs GDAL — see below.", needsGdal: true);
        AddLayer(into, Layers.Routes, "Cycle routes", "Needs GDAL — see below.", needsGdal: true);
    }

    private void AddLayer(Container into, Layers layer, string name, string hint, bool locked = false, bool needsGdal = false)
    {
        var toggle = UiKit.ToggleRow(into, name, _layers.HasFlag(layer), on =>
        {
            _layers = on ? _layers | layer : _layers & ~layer;
            _state.Layers = _layers;
            RefreshSummary();
        }, hint);
        // Terrain is what every other layer is draped on, and GDAL is not in a release build:
        // disable rather than hide, so the reason is visible instead of mysterious.
        if (locked || needsGdal) toggle.Disabled = true;
        if (needsGdal) _layers &= ~layer;
    }

    /// <summary>
    /// What the machine can and cannot do, named plainly. Downloading and building need nothing
    /// installed (#515 phases 1 and 2 removed Python and the .NET SDK); buildings and cycle routes
    /// are read from Esri FileGDB, which still needs GDAL until that reader is ported to C#.
    /// </summary>
    private void BuildRequirements(Container into)
    {
        into.AddChild(UiKit.Section("Requirements"));
        Requirement(into, true, "Downloading", "Built in — nothing to install.");
        Requirement(into, true, "Building tiles", "Built in — nothing to install.");
        Requirement(into, false, "Buildings and cycle routes",
            "These two come as Esri FileGDB, which needs GDAL's Python bindings. Install Python 3.10+ "
            + "and GDAL, then run the terminal wizard once for those layers: "
            + "dotnet run --project tools/MapSetup. Everything else works without it.");
    }

    private static void Requirement(Container into, bool ok, string name, string detail)
    {
        var row = UiKit.HBox(8);
        row.AddChild(UiKit.StatusDot(ok ? UiTheme.Good : UiTheme.Warn));
        var text = UiKit.VBox(1);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.AddChild(UiKit.Text(name, UiTheme.FontSmall, UiTheme.Text));
        text.AddChild(UiKit.Text(detail, UiTheme.FontTiny, UiTheme.TextFaint, wrap: true));
        row.AddChild(text);
        into.AddChild(row);
    }

    // ---- what the screen can be asked to do (the panel's buttons, and --mapcheck) ---------------

    /// <summary>The tiles picked so far.</summary>
    public Selection Picked => _selection;

    /// <summary>The country map this screen was built on.</summary>
    public CountryData Country => _country;

    /// <summary>The map itself, for the view's centre and scale.</summary>
    public MapCanvas Canvas => _map;

    /// <summary>Searches for a place and jumps to the best match, as typing and pressing Enter does.</summary>
    public bool SearchAndJump(string query)
    {
        var town = _country.Search(query, 1).FirstOrDefault();
        if (town == null) return false;
        _search.Text = query;
        _map.FocusOn(town.E, town.N, 12);
        return true;
    }

    public void SelectRect(TileId a, TileId b)
    {
        _selection.AddRect(a, b);
        RefreshSummary();
    }

    public void Paint(TileId t, bool erase = false)
    {
        if (erase) _selection.Remove(t); else _selection.Add(t);
        RefreshSummary();
    }

    /// <summary>Moves the landing marker, as shift-clicking the map does.</summary>
    public void MoveLanding(double e, double n)
    {
        _map.SetLanding(e, n);
        RefreshSummary();
    }

    /// <summary>Confirms the landing, as the "Land here" button does.</summary>
    public void ConfirmLanding() => OnPrimary();

    public void ClearSelection()
    {
        _selection.Clear();
        RefreshSummary();
    }

    // ---- actions -------------------------------------------------------------------------------

    private void OnPrimary()
    {
        if (_role == Role.Landing)
        {
            // Phase 4 starts the background download here when something is selected; until then
            // the landing choice is what this button is for.
            if (_map.Landing is { } landing) _confirmLanding?.Invoke(landing);
            return;
        }

        // Phase 4 hands the plan to the background DownloadJob. Saving the selection is already
        // useful on its own: the terminal wizard's --resume picks up exactly this.
        _state.Tiles = _selection.Tiles.OrderBy(t => t.E).ThenBy(t => t.N).Select(SetupState.Key).ToList();
        _state.Layers = _layers;
        _state.Save(_paths);
        Modal.Inform(this, "Not downloading yet",
            $"{_selection.Count:N0} tiles and the layers you chose are saved. Running the download from "
            + "here arrives with the next phase of #515; until then the terminal wizard picks this "
            + "selection up with --resume.");
    }

    public override void OnShown()
    {
        if (_map != null) _map.CallDeferred(Control.MethodName.GrabFocus);
        else base.OnShown();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_map == null) return;
        if (e.IsActionPressed(PlayerInput.MapTool))
        {
            SetTool((MapCanvas.Tool)(((int)_map.CurrentTool + 1) % 3));
            AcceptEvent();
        }
        else if (e.IsActionPressed(PlayerInput.MapSearch))
        {
            _search.GrabFocus();
            _search.SelectAll();
            AcceptEvent();
        }
    }

    /// <summary>The tile nearest the centre of a set — where to look when the screen opens.</summary>
    private static TileId Centre(IReadOnlyCollection<TileId> tiles)
    {
        double ce = tiles.Average(t => (double)t.E), cn = tiles.Average(t => (double)t.N);
        return tiles.MinBy(t => (t.E - ce) * (t.E - ce) + (t.N - cn) * (t.N - cn));
    }

    private static string Bytes(long b) => b <= 0 ? "—"
        : b >= 1_000_000_000 ? $"{b / 1e9:F1} GB"
        : b >= 1_000_000 ? $"{b / 1e6:F0} MB"
        : $"{b / 1e3:F0} KB";

    private static string Duration(double seconds) => seconds < 90 ? $"{seconds:F0} s"
        : seconds < 5400 ? $"{seconds / 60:F0} min"
        : $"{seconds / 3600:F1} h";
}
