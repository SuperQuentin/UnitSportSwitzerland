using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Type-to-search teleport panel. Only places with terrain are listed, so every result
/// takes you somewhere you can actually stand.
/// </summary>
public partial class PlaceSearchUi : CanvasLayer
{
    private PlaceIndex _index = new();
    private Teleporter _teleporter = null!;

    private PanelContainer _panel = null!;
    private LineEdit _query = null!;
    private ItemList _results = null!;
    private Label _hint = null!;
    private List<Place> _shown = new();

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

        string path = System.IO.Path.Combine(TerrainPaths.FindChunkDir(), PlaceIndex.FileName);
        if (System.IO.File.Exists(path))
            _index = PlaceIndex.FromJson(System.IO.File.ReadAllText(path));
        else
            GD.PushWarning($"[places] {PlaceIndex.FileName} not found — run the preprocessor " +
                           "with --places --gwr <gwr data.sqlite>");

        _panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0, AnchorBottom = 0,
            OffsetLeft = -230, OffsetRight = 230, OffsetTop = 70, OffsetBottom = 400,
            Visible = false,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 0.94f),
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        style.SetCornerRadiusAll(4);
        _panel.AddThemeStyleboxOverride("panel", style);
        AddChild(_panel);

        var rows = new VBoxContainer();
        _panel.AddChild(rows);

        var title = new Label { Text = $"Teleport — {_index.Places.Count} places with terrain" };
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        rows.AddChild(title);

        _query = new LineEdit { PlaceholderText = "type a town name…" };
        _query.TextChanged += _ => Refresh();
        _query.TextSubmitted += _ => Go(0);
        rows.AddChild(_query);

        _results = new ItemList { CustomMinimumSize = new Vector2(0, 250), AllowReselect = true };
        _results.ItemActivated += i => Go((int)i);
        _results.ItemSelected += i => Go((int)i);
        rows.AddChild(_results);

        _hint = new Label { Text = "Enter to jump   Esc to close" };
        _hint.AddThemeColorOverride("font_color", new Color(0.65f, 0.68f, 0.72f));
        rows.AddChild(_hint);

        Refresh();

        // "--goto <town>" jumps there once the world is up, which beats looking up an LV95
        // easting and northing to pass to --at.
        if (ParseGotoArg() is { } wanted) Callable.From(() => GoToNamed(wanted)).CallDeferred();
    }

    /// <summary>Reads an optional "--goto &lt;town&gt;" from the command line.</summary>
    public static string? ParseGotoArg()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--goto")
                return args[i + 1];
        return null;
    }

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
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _query.Clear();
            Refresh();
            _query.GrabFocus();
        }
        else
        {
            _query.ReleaseFocus();
        }
    }

    private void Refresh()
    {
        _shown = _index.Search(_query.Text);
        _results.Clear();
        foreach (var p in _shown)
            _results.AddItem(string.IsNullOrEmpty(p.Canton)
                ? $"{p.Name}   ({p.Buildings} buildings)"
                : $"{p.Name}, {p.Canton}   ({p.Buildings} buildings)");
    }

    private void Go(int index)
    {
        if (index < 0 || index >= _shown.Count) return;
        var place = _shown[index];

        _teleporter.TeleportTo(place.E, place.N, place.Name);
        SetOpen(false);
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
}
