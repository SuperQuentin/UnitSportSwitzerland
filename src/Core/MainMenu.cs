using Godot;

namespace UnitSport.Core;

/// <summary>What the client is currently doing. Each mode owns the camera while it runs.</summary>
public enum GameMode
{
    /// <summary>Free fly and on-foot exploration — the default.</summary>
    Explore,

    /// <summary>GPX ghost racing: the playback camera and HUD take over.</summary>
    GpxReplay,

    /// <summary>Connected to a dedicated server, walking as a networked player.</summary>
    Multiplayer,
}

/// <summary>
/// Mode picker, shown at boot and whenever Esc is pressed.
///
/// The menu owns the mouse: opening it releases the pointer so the buttons are clickable,
/// closing it hands capture back to whichever mode is running. Without that the fly camera
/// keeps the cursor and nothing here can be clicked.
/// </summary>
public partial class MainMenu : CanvasLayer
{
    public event Action<GameMode>? ModeChosen;
    public event Action? QuitRequested;

    private PanelContainer _panel = null!;
    private LineEdit _host = null!;
    private Label _status = null!;
    private Button _resume = null!;

    /// <summary>Server address typed into the multiplayer row.</summary>
    public string Host => string.IsNullOrWhiteSpace(_host.Text) ? "127.0.0.1" : _host.Text.Trim();

    public bool IsOpen => _panel.Visible;

    /// <summary>The mode currently running, shown so the menu can offer to resume it.</summary>
    public GameMode? Current { get; private set; }

    public static MainMenu Create() => new() { Name = "MainMenu" };

    public override void _Ready()
    {
        Layer = 40;   // above the teleport search and the playback HUD

        // A CenterContainer over the whole viewport lets the panel size itself to its
        // content, so adding or hiding a mode does not leave dead space.
        var centre = new CenterContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(centre);

        _panel = new PanelContainer { CustomMinimumSize = new Vector2(500, 0), Visible = false };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.95f),
            ContentMarginLeft = 24, ContentMarginRight = 24,
            ContentMarginTop = 20, ContentMarginBottom = 20,
        };
        style.SetCornerRadiusAll(6);
        _panel.AddThemeStyleboxOverride("panel", style);
        centre.AddChild(_panel);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rows);

        var title = new Label { Text = "UnitSport Switzerland" };
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        rows.AddChild(title);

        _status = new Label { Text = "" };
        _status.AddThemeColorOverride("font_color", new Color(0.62f, 0.66f, 0.72f));
        rows.AddChild(_status);

        rows.AddChild(new HSeparator());

        _resume = ModeButton(rows, "Resume", "Back to what you were doing", () => Close());
        _resume.Visible = false;

        ModeButton(rows, "Explore",
            "Fly the terrain, T to drop on foot, Tab to teleport to a town",
            () => Choose(GameMode.Explore));

        ModeButton(rows, "GPX replay",
            "Run a recorded track; load several to race them as ghosts",
            () => Choose(GameMode.GpxReplay));

        ModeButton(rows, "Join a server",
            "Connect to a dedicated server and walk it with others",
            () => Choose(GameMode.Multiplayer));

        var hostRow = new HBoxContainer();
        hostRow.AddChild(new Label { Text = "Server" });
        _host = new LineEdit
        {
            Text = "127.0.0.1",
            PlaceholderText = "host or host:port",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        hostRow.AddChild(_host);
        rows.AddChild(hostRow);

        rows.AddChild(new HSeparator());

        var quit = new Button { Text = "Quit", CustomMinimumSize = new Vector2(0, 30) };
        quit.Pressed += () => QuitRequested?.Invoke();
        rows.AddChild(quit);

        var hint = new Label { Text = "Esc opens this menu at any time" };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(hint);
    }

    /// <summary>A big button with a dimmer explanatory line under it.</summary>
    private static Button ModeButton(Container into, string text, string blurb, Action pressed)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);

        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 34) };
        button.Pressed += pressed;
        box.AddChild(button);

        var label = new Label { Text = blurb, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", new Color(0.55f, 0.59f, 0.65f));
        box.AddChild(label);

        into.AddChild(box);
        // the caller wants the button so it can be hidden; the box follows its visibility
        button.VisibilityChanged += () => box.Visible = button.Visible;
        return button;
    }

    private void Choose(GameMode mode)
    {
        Current = mode;
        Close();
        ModeChosen?.Invoke(mode);
    }

    public void Toggle() => SetOpen(!IsOpen);

    /// <summary>Records the running mode so "Resume" and the status line are accurate.</summary>
    public void NoteMode(GameMode mode) => Current = mode;

    public void Open() => SetOpen(true);
    public void Close() => SetOpen(false);

    private void SetOpen(bool open)
    {
        _panel.Visible = open;
        if (open)
        {
            _status.Text = Current == null
                ? "Pick a mode to begin"
                : $"Currently: {Describe(Current.Value)}";
            _resume.Visible = Current != null;
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else if (Current is GameMode.Explore or GameMode.Multiplayer)
        {
            // hand the pointer back to the fly camera / player controller
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    private static string Describe(GameMode mode) => mode switch
    {
        GameMode.Explore => "exploring",
        GameMode.GpxReplay => "GPX replay",
        GameMode.Multiplayer => "on a server",
        _ => mode.ToString(),
    };

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!IsOpen) return;
        if (@event is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape } && Current != null)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
