using Godot;
using UnitSport.Core;
using UnitSport.Map;
using UnitSport.Terrain.Format;

namespace UnitSport.Ui;

/// <summary>
/// The map of Switzerland, and the things it is for (#515, #799): choosing where to start, choosing
/// where a hosted server's players start, and seeing and downloading terrain. One screen, because
/// they are the same question asked again — "is there real ground where I am going?" is answered by
/// looking at the map you would download it on.
///
/// <para>
/// It is built for someone who has never seen it (#799). There is always one marker, put down by a
/// click, a search result or a popular place, and everything else is said about the marker in plain
/// words. <b>Picking</b> (<see cref="Role.Landing"/>, <see cref="Role.ServerSpawn"/>) is search, click,
/// confirm; the terrain under the marker is one line and one button, and the draw tools wait under
/// "Advanced". <b>Downloading</b> (<see cref="Role.Library"/>) reads as three numbered steps: find a
/// place, choose an area round the marker (or draw one), download.
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
    /// <summary>Which of the screen's jobs this instance is doing.</summary>
    public enum Role
    {
        /// <summary>Reached from the title screen: look at what you have, pick more, download it.</summary>
        Library,

        /// <summary>Shown as a solo world starts: where do you want to start?</summary>
        Landing,

        /// <summary>From Host a game (#799): where does everyone start on the server you are about to run?</summary>
        ServerSpawn,
    }

    /// <summary>Places offered as one-click starts, by name; any the country map does not know are left out.</summary>
    private static readonly string[] Popular =
        ["Riddes", "Zermatt", "Bern", "Zürich", "Luzern", "Interlaken", "Lugano", "Lausanne", "St. Moritz", "Genève"];

    /// <summary>The radius the one-click download takes round the marker, km.</summary>
    private const int QuickKm = 8;

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
    private VBoxContainer? _summary;
    private VBoxContainer? _marker;
    private Button _primary = null!;
    private Button? _download;
    private Control? _advanced;
    private Button? _advancedToggle;
    private string _advancedText = "";
    private readonly Button?[] _toolButtons = new Button?[4];

    private Layers _layers = Layers.Terrain | Layers.Roads | Layers.Places;
    private PanelContainer? _panel;
    private VBoxContainer? _jobRow;
    private Label _jobLine = null!;
    private Label _jobDetail = null!;
    private ProgressBar _jobBar = null!;
    private Button _jobStop = null!;
    private double _nextJobPollAt;

    private bool Picking => _role != Role.Library;

    public static MapScreen Create() => new(Role.Library, null) { Name = "Map" };

    /// <summary>The landing role: the marker starts at <paramref name="start"/> and
    /// <paramref name="confirm"/> is called with where the player chose to start.</summary>
    public static MapScreen CreateLanding((double E, double N) start, Action<(double E, double N)> confirm) =>
        new(Role.Landing, confirm) { Name = "Landing", StartMarker = start };

    /// <summary>Host a game (#799): where every player on the hosted server starts.</summary>
    public static MapScreen CreateServerSpawn((double E, double N) start, Action<(double E, double N)> confirm) =>
        new(Role.ServerSpawn, confirm) { Name = "ServerSpawn", StartMarker = start };

    private (double E, double N)? StartMarker { get; init; }

    private MapScreen(Role role, Action<(double E, double N)>? confirmLanding)
    {
        _role = role;
        _confirmLanding = confirmLanding;
    }

    // ---- the place under a point, for this screen and the Host dialog ---------------------------

    private static CountryData? _sharedCountry;
    private static bool _sharedTried;

    /// <summary>The embedded country map, read once (it never changes); null if the build's copy is broken.</summary>
    private static CountryData? SharedCountry()
    {
        if (_sharedTried) return _sharedCountry;
        _sharedTried = true;
        try { _sharedCountry = CountryData.LoadEmbedded(); }
        catch (Exception e) { GD.PushWarning($"[map] no country map: {e.Message}"); }
        return _sharedCountry;
    }

    /// <summary>"Zermatt, VS" or "4 km from Zermatt, VS": the nearest town to a point, for a line a person reads.</summary>
    public static string PlaceNear(double e, double n) =>
        SharedCountry() is { } country ? Describe(country, e, n) : $"{e:F0} / {n:F0}";

    private static string Describe(CountryData country, double e, double n)
    {
        Town? best = null;
        double bestD = double.MaxValue;
        foreach (var town in country.Towns)
        {
            if (town.Kind != PlaceKind.Town) continue;
            double d = (town.E - e) * (town.E - e) + (town.N - n) * (town.N - n);
            if (d < bestD) { bestD = d; best = town; }
        }
        if (best == null) return $"{e:F0} / {n:F0}";
        double km = Math.Sqrt(bestD) / 1000.0;
        string where = string.IsNullOrEmpty(best.Canton) ? best.Name : $"{best.Name}, {best.Canton}";
        return km < 1.5 ? where : $"{km:F0} km from {where}";
    }

    // ---- building ---------------------------------------------------------------------------------

    public override void _Ready()
    {
        // The country map is embedded in MapCore, so this cannot fail for want of a data folder;
        // a corrupt one still can, and the screen has to say so rather than take the title down.
        if (SharedCountry() is not { } country)
        {
            ShowBroken("The country map embedded in the game could not be read.");
            return;
        }
        _country = country;

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

    private (string Title, string Subtitle, string Confirm) Words => _role switch
    {
        Role.Landing => ("Where do you want to start?",
            "Click the map, search for a place, or pick a popular one. Then press Start here.", "Start here"),
        Role.ServerSpawn => ("Where do players start?",
            "Everyone who joins your game starts here. Click the map or search for a place.", "Players start here"),
        _ => ("Download Swiss terrain",
            "Real terrain makes the world look like the real Switzerland. Pick a place, choose an area, download it.",
            "Download selection"),
    };

    private void Build()
    {
        // Sized to the window like the travel menu and the controls screen (#210), because a map
        // wants every pixel it can get rather than a fixed panel.
        var (body, header) = Framed(Words.Title, Words.Subtitle, PanelSize());
        // the glass panel Framed built, so a window resize can follow (ControlsHelp does the same)
        _panel = body.GetParent()?.GetParent() as PanelContainer;
        GetViewport().SizeChanged += FitToWindow;

        var legend = UiKit.HBox(14);
        legend.AddChild(Chip(new Color(0.16f, 0.80f, 0.80f), "real terrain, ready"));
        legend.AddChild(Chip(new Color(0.24f, 0.45f, 0.92f), "downloaded"));
        legend.AddChild(Chip(new Color(1.00f, 0.75f, 0.12f), "selected"));
        header.AddChild(legend);

        var columns = UiKit.HBox(16);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(columns);

        // ---- the map, with its one marker: where you start, or the middle of the area to download
        _map = new MapCanvas(_country, _selection, _local)
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _map.SelectionChanged += RefreshSummary;
        _map.LandingMoved += (_, _) => RefreshMarker();
        var start = StartMarker ?? (_local.Built.Count > 0 ? Middle(Centre(_local.Built))
            : (SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N));
        _map.SetLanding(start.E, start.N);
        _map.FocusOn(start.E, start.N, 8);
        var mapFrame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        mapFrame.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.03f, 0.04f, 0.055f), 10, 2, 2,
            UiTheme.Hairline, 1));
        mapFrame.AddChild(_map);
        columns.AddChild(mapFrame);

        // ---- the side panel, top to bottom in the order a person does things
        var (scroll, side) = UiKit.ScrollPage(8);
        scroll.CustomMinimumSize = new Vector2(340, 0);
        scroll.SizeFlagsHorizontal = SizeFlags.Fill;
        columns.AddChild(scroll);

        BuildSearch(side, "1. Find a place");
        if (Picking)
        {
            BuildMarker(side, "2. " + (_role == Role.ServerSpawn ? "Players start" : "You start"));
            var advanced = BuildAdvanced(side, "Advanced: download more terrain");
            BuildArea(advanced, "Choose an area");
            BuildSummary(advanced, "This selection", withButton: true);
            BuildLayers(advanced);
        }
        else
        {
            BuildMarker(side, null);
            BuildArea(side, "2. Choose an area");
            BuildSummary(side, "3. Download", withButton: false);
            BuildLayers(BuildAdvanced(side, "Advanced: what to build"));
        }
        SetTool(MapCanvas.Tool.Move);

        // ---- the footer
        var footer = UiKit.HBox(10);
        body.AddChild(footer);
        var hint = UiKit.Text(InputHints.Format(
            "Click to put the marker down  ·  drag to move the map  ·  wheel or {map_zoom_in}/{map_zoom_out} to zoom"
            + "  ·  {map_search} search"),
            UiTheme.FontTiny, UiTheme.TextFaint);
        hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(hint);

        var back = UiKit.Button("Back", minWidth: 96);
        back.Pressed += () => Shell.Back();
        footer.AddChild(back);

        _primary = UiKit.Button(Words.Confirm, primary: true, minWidth: 200);
        _primary.Pressed += OnPrimary;
        footer.AddChild(_primary);

        RefreshMarker();
        RefreshSummary();
    }

    /// <summary>A section folded away behind one button: what most people never need to see.</summary>
    private VBoxContainer BuildAdvanced(Container into, string label)
    {
        _advancedText = label;
        into.AddChild(UiKit.Spacer(6));
        _advancedToggle = UiKit.Button(label + "  ▸");
        _advancedToggle.Pressed += () => ShowAdvanced(_advanced is { Visible: false });
        into.AddChild(_advancedToggle);
        var advanced = UiKit.VBox(8);
        advanced.Visible = false;
        _advanced = advanced;
        into.AddChild(advanced);
        return advanced;
    }

    private void ShowAdvanced(bool show)
    {
        if (_advanced == null || _advancedToggle == null) return;
        _advanced.Visible = show;
        _advancedToggle.Text = _advancedText + (show ? "  ▾" : "  ▸");
        // closing it puts the map back to moving: a hidden drawing tool would select on every drag
        if (!show) SetTool(MapCanvas.Tool.Move);
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

    private void BuildSearch(Container into, string heading)
    {
        into.AddChild(UiKit.Section(heading));
        _search = new LineEdit { PlaceholderText = "A town or a mountain: Zermatt, Bern…", ClearButtonEnabled = true };
        _search.TextChanged += _ => RefreshResults();
        _search.TextSubmitted += _ =>
        {
            if (_results.GetChildCount() > 0 && _results.GetChild(0) is Button first) first.EmitSignal(BaseButton.SignalName.Pressed);
        };
        into.AddChild(_search);
        _results = UiKit.VBox(2);
        into.AddChild(_results);

        into.AddChild(UiKit.Text("Popular places", UiTheme.FontTiny, UiTheme.TextFaint));
        var popular = new HFlowContainer();
        popular.AddThemeConstantOverride("h_separation", 6);
        popular.AddThemeConstantOverride("v_separation", 6);
        foreach (string name in Popular)
        {
            if (_country.Search(name, 1).FirstOrDefault() is not { } town) continue;
            var chip = UiKit.Button(town.Name);
            chip.Pressed += () => GoTo(town);
            popular.AddChild(chip);
        }
        into.AddChild(popular);
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
            row.Pressed += () => GoTo(town);
            _results.AddChild(row);
        }
    }

    /// <summary>A search result or a popular place: the marker goes there and the map follows.</summary>
    private void GoTo(Town town)
    {
        _map.SetLanding(town.E, town.N);
        _map.FocusOn(town.E, town.N, 12);
        RefreshMarker();
        _map.GrabFocus();
    }

    /// <summary>What is under the marker, in words, and the one fix for "no real terrain here".</summary>
    private void BuildMarker(Container into, string? heading)
    {
        if (heading != null) into.AddChild(UiKit.Section(heading));
        _marker = UiKit.VBox(6);
        into.AddChild(_marker);
        if (!Picking) return;

        _download = UiKit.Button($"Download real terrain here");
        _download.TooltipText = $"Downloads and builds everything within {QuickKm} km of the marker, in the background";
        _download.Pressed += () =>
        {
            if (_map.Landing is not { } at) return;
            _selection.Clear();
            _selection.AddCircle(at.E, at.N, QuickKm);
            StartDownload();
        };
        into.AddChild(_download);
        BuildJob(into);
    }

    private void RefreshMarker()
    {
        if (_marker == null || _map.Landing is not { } at) return;
        foreach (var child in _marker.GetChildren()) child.QueueFree();

        _marker.AddChild(UiKit.Text(Describe(_country, at.E, at.N), UiTheme.FontBody, UiTheme.Text, bold: true));

        var tile = TileId.FromLv95(at.E, at.N);
        int near = 0;
        for (int de = -4; de <= 4; de++)
            for (int dn = -4; dn <= 4; dn++)
                if (_local.Built.Contains(new TileId(tile.E + de, tile.N + dn))) near++;
        bool covered = _country.Covered(tile);

        var row = UiKit.HBox(8);
        row.AddChild(UiKit.StatusDot(near > 0 ? UiTheme.Good : UiTheme.Warn));
        string text = !covered ? "Outside Switzerland: the ground here is made up."
            : near > 0 ? "Real Swiss terrain is ready here."
            : Picking ? "No real terrain downloaded here yet. You can still play: the ground will be an approximation."
            : "No real terrain here yet.";
        var line = UiKit.Text(text, UiTheme.FontSmall, near > 0 ? UiTheme.TextDim : UiTheme.Warn, wrap: true);
        line.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(line);
        _marker.AddChild(row);

        if (_download != null)
        {
            _download.Visible = covered && near == 0;
            var quick = new Selection(_country);
            quick.AddCircle(at.E, at.N, QuickKm);
            var (bytes, _, seconds) = Estimate(quick);
            _download.Text = $"Download real terrain here  ·  {Bytes(bytes)}, ~{Duration(seconds)}";
        }
    }

    private void BuildArea(Container into, string heading)
    {
        into.AddChild(UiKit.Section(heading));
        into.AddChild(UiKit.Text("Around the marker:", UiTheme.FontSmall, UiTheme.TextDim));
        var around = UiKit.HBox(6);
        into.AddChild(around);
        foreach (var (km, label) in new[] { (3, "Small"), (8, "Medium"), (20, "Large") })
        {
            var button = UiKit.Button($"{label}  {km} km");
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.TooltipText = $"Select everything within {km} km of the marker";
            button.Pressed += () => SelectAroundMarker(km);
            around.AddChild(button);
        }

        into.AddChild(UiKit.Text("Or draw it on the map:", UiTheme.FontSmall, UiTheme.TextDim));
        var row = UiKit.HBox(6);
        into.AddChild(row);
        var names = new[] { "Move", "Rectangle", "Brush", "Erase" };
        var tips = new[]
        {
            "Drag moves the map, a click puts the marker down",
            "Drag a rectangle to select it",
            "Paint tiles to select them",
            "Paint tiles to unselect them",
        };
        for (int i = 0; i < names.Length; i++)
        {
            var tool = (MapCanvas.Tool)i;
            var button = UiKit.Button(names[i]);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.TooltipText = tips[i];
            button.Pressed += () => SetTool(tool);
            _toolButtons[i] = button;
            row.AddChild(button);
        }

        var cantonRow = UiKit.HBox(6);
        into.AddChild(cantonRow);
        var cantons = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cantons.AddItem("Or a whole canton…");
        foreach (var canton in _country.Cantons) cantons.AddItem($"{canton.Name} ({canton.Code})");
        cantons.ItemSelected += index =>
        {
            if (index <= 0) return;
            _selection.AddCanton(_country.Cantons[(int)index - 1]);
            RefreshSummary();
            cantons.Selected = 0;
        };
        cantonRow.AddChild(cantons);
        var clear = UiKit.Button("Clear");
        clear.TooltipText = "Unselect everything";
        clear.Pressed += ClearSelection;
        cantonRow.AddChild(clear);
    }

    private void SetTool(MapCanvas.Tool tool)
    {
        _map.CurrentTool = tool;
        for (int i = 0; i < _toolButtons.Length; i++)
            _toolButtons[i]?.AddThemeColorOverride("font_color", (MapCanvas.Tool)i == tool ? UiTheme.Amber : UiTheme.TextDim);
    }

    private void BuildSummary(Container into, string heading, bool withButton)
    {
        into.AddChild(UiKit.Section(heading));
        _summary = UiKit.VBox(3);
        into.AddChild(_summary);
        if (withButton)
        {
            // picking: the footer's button confirms the marker, so the selection gets its own
            var go = UiKit.Button("Download the selection");
            go.Pressed += StartDownload;
            into.AddChild(go);
        }
        else BuildJob(into);
    }

    private void BuildJob(Container into)
    {
        _jobRow = UiKit.VBox(4);
        into.AddChild(_jobRow);
        _jobLine = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, wrap: true);
        _jobRow.AddChild(_jobLine);
        _jobBar = new ProgressBar { MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6) };
        _jobRow.AddChild(_jobBar);
        _jobDetail = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint, wrap: true);
        _jobRow.AddChild(_jobDetail);
        _jobStop = UiKit.Button("Stop the download");
        _jobStop.Pressed += () =>
        {
            Terrain.DownloadJob.CancelCurrent();
            _map.Working.Clear();
        };
        _jobRow.AddChild(_jobStop);
        _jobRow.Visible = false;
    }

    /// <summary>
    /// Follows the background job. Polled rather than signalled: the job runs on a worker and
    /// publishes an immutable snapshot, so this reads one value a few times a second and touches
    /// no lock the worker is holding.
    /// </summary>
    public override void _Process(double delta)
    {
        if (_jobRow == null) return;
        // wall clock: the download threads do not speed up or slow down with the world
        if (Core.RealClock.Now < _nextJobPollAt) return;
        _nextJobPollAt = Core.RealClock.Now + 0.2;

        if (Terrain.DownloadJob.CurrentProgress is not { } progress)
        {
            _jobRow.Visible = false;
            return;
        }

        _jobRow.Visible = true;
        _jobLine.Text = progress.Error != null ? $"Download stopped: {progress.Error}"
            : progress.Finished ? $"Downloaded {progress.Tiles:N0} km². It is in the world from the next time one loads."
            : progress.Running ? $"Downloading: {progress.StepTitle}  ({progress.StepIndex + 1}/{progress.StepCount})"
            : "Download stopped.";
        _jobLine.AddThemeColorOverride("font_color",
            progress.Error != null ? UiTheme.Bad : progress.Finished ? UiTheme.Good : UiTheme.Amber);
        _jobBar.Visible = progress.Running;
        _jobBar.Value = progress.Overall;
        _jobDetail.Text = progress.Running ? progress.Latest + (Picking ? "  ·  You can start now: it carries on." : "") : "";
        _jobStop.Visible = progress.Running;

        if (!progress.Running && _map.Working.Count > 0)
        {
            // the job is over: stop tinting its tiles and pick up whatever it actually wrote
            _map.Working.Clear();
            _local = LocalState.Scan(_paths);
            _map.Rescan(_local);
            RefreshSummary();
            RefreshMarker();
        }
    }

    /// <summary>
    /// What a selection would cost, from the same <see cref="Planner"/> the terminal wizard prints as
    /// a table. Cheap: the plan is arithmetic over the selection, with no file reading.
    /// </summary>
    private (long Download, long Disk, double Seconds) Estimate(Selection selection)
    {
        if (selection.Count == 0) return (0, 0, 0);
        var steps = Planner.Build(new SetupContext
        {
            Paths = _paths, Country = _country, Local = _local, Selection = selection, Layers = _layers,
            Stats = _stats, State = _state,
        });
        var run = steps.Where(s => s.Skip == null).ToList();
        return (run.Sum(s => s.DownloadBytes), run.Sum(s => s.DiskBytes), run.Sum(s => s.Seconds));
    }

    /// <summary>The selection's size and cost, recomputed on every change.</summary>
    private void RefreshSummary()
    {
        if (_summary == null) return;
        foreach (var child in _summary.GetChildren()) child.QueueFree();

        int selected = _selection.Count;
        int already = _selection.Tiles.Count(t => _local.Built.Contains(t));
        Row("Selected", selected == 0 ? "nothing yet" : $"{selected:N0} km²");
        if (selected > 0 && already > 0) Row("Already ready", $"{already:N0} km²");
        Row("On this computer", $"{_local.Built.Count:N0} km² ready");

        if (!Picking) _primary.Disabled = selected == 0;
        if (selected == 0) return;

        var (download, disk, seconds) = Estimate(_selection);
        Row("To download", Bytes(download));
        Row("Disk space", Bytes(disk));
        Row("Time, roughly", Duration(seconds));
    }

    private void Row(string name, string value)
    {
        var row = UiKit.HBox(8);
        var label = UiKit.Text(name, UiTheme.FontSmall, UiTheme.TextDim);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        row.AddChild(UiKit.Text(value, UiTheme.FontSmall, UiTheme.Text));
        _summary!.AddChild(row);
    }

    private void BuildLayers(Container into)
    {
        into.AddChild(UiKit.Section("What to build"));
        AddLayer(into, Layers.Terrain, "Terrain", "The ground itself. Everything else stands on it.", locked: true);
        AddLayer(into, Layers.Roads, "Roads, rail, water, trees", "One nationwide file, fetched once.");
        AddLayer(into, Layers.Places, "Place names", "The in-game place search.");
        AddLayer(into, Layers.Cadastre, "Building use and storeys", "What each building is, and how tall.");
        AddLayer(into, Layers.Buildings, "Buildings", "The real buildings, roofs and all.");
        AddLayer(into, Layers.Routes, "Cycle routes", "Signed Veloland and Mountainbikeland routes.");
    }

    private void AddLayer(Container into, Layers layer, string name, string hint, bool locked = false)
    {
        var toggle = UiKit.ToggleRow(into, name, _layers.HasFlag(layer), on =>
        {
            _layers = on ? _layers | layer : _layers & ~layer;
            _state.Layers = _layers;
            RefreshSummary();
            RefreshMarker();
        }, hint);
        // Terrain is what every other layer is draped on: shown, and not switchable off.
        if (locked) toggle.Disabled = true;
    }

    // ---- what the screen can be asked to do (the panel's buttons, and --mapcheck) ---------------

    /// <summary>The tiles picked so far.</summary>
    public Selection Picked => _selection;

    /// <summary>The country map this screen was built on.</summary>
    public CountryData Country => _country;

    /// <summary>The map itself, for the view's centre and scale.</summary>
    public MapCanvas Canvas => _map;

    /// <summary>Searches for a place and puts the marker on the best match, as typing and pressing Enter does.</summary>
    public bool SearchAndJump(string query)
    {
        var town = _country.Search(query, 1).FirstOrDefault();
        if (town == null) return false;
        _search.Text = query;
        GoTo(town);
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

    /// <summary>Selects everything within <paramref name="km"/> of the marker, as the Small/Medium/Large buttons do.</summary>
    public void SelectAroundMarker(int km)
    {
        if (_map.Landing is not { } at) return;
        _selection.AddCircle(at.E, at.N, km);
        RefreshSummary();
    }

    /// <summary>Moves the marker, as clicking the map does.</summary>
    public void MoveLanding(double e, double n)
    {
        _map.SetLanding(e, n);
        RefreshMarker();
    }

    /// <summary>Confirms the marker, as the footer's button does.</summary>
    public void ConfirmLanding() => OnPrimary();

    public void ClearSelection()
    {
        _selection.Clear();
        RefreshSummary();
    }

    /// <summary>The Advanced section is open.</summary>
    public bool AdvancedOpen => _advanced is { Visible: true };

    /// <summary>The line under the marker heading: where it is, in words.</summary>
    public string MarkerText => _marker?.GetChildCount() > 0 && _marker.GetChild(0) is Label l ? l.Text : "";

    // ---- actions -------------------------------------------------------------------------------

    private void OnPrimary()
    {
        if (Picking)
        {
            // a download started here keeps running behind the loading screen and the world
            if (_map.Landing is { } landing) _confirmLanding?.Invoke(landing);
            return;
        }
        StartDownload();
    }

    private void StartDownload()
    {
        if (_selection.Count == 0) return;
        if (Terrain.DownloadJob.Current is { Now.Running: true })
        {
            Modal.Inform(this, "Already downloading",
                "One download is running. Two writing the same folders would fight over the shared "
                + "record of what has been fetched. Stop it first, or wait for it to finish.");
            return;
        }

        // Saved as well as started: the terminal wizard's --resume picks up exactly this selection.
        _state.Tiles = _selection.Tiles.OrderBy(t => t.E).ThenBy(t => t.N).Select(SetupState.Key).ToList();
        _state.Layers = _layers;
        _state.Save(_paths);

        Terrain.DownloadJob.Start(_paths, _country, _local, _selection, _layers, _stats, _state);
        foreach (var t in _selection.Tiles) _map.Working.Add(t);
        RefreshSummary();
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
            // the draw tools live in the folded section when picking: open it to reach them
            if (Picking && !AdvancedOpen) ShowAdvanced(true);
            SetTool((MapCanvas.Tool)(((int)_map.CurrentTool + 1) % _toolButtons.Length));
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

    private static (double E, double N) Middle(TileId t) => ((t.E + 0.5) * 1000.0, (t.N + 0.5) * 1000.0);

    private static string Bytes(long b) => b <= 0 ? "—"
        : b >= 1_000_000_000 ? $"{b / 1e9:F1} GB"
        : b >= 1_000_000 ? $"{b / 1e6:F0} MB"
        : $"{b / 1e3:F0} KB";

    private static string Duration(double seconds) => seconds < 90 ? $"{seconds:F0} s"
        : seconds < 5400 ? $"{seconds / 60:F0} min"
        : $"{seconds / 3600:F1} h";
}
