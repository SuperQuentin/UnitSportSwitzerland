using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Interiors;

/// <summary>
/// A hall's projector panel (#1298): what its screen shows. E (pad interact, VR grip) at the screen's half of
/// the booth's control panel opens it (<see cref="CinemaBooths.TryUse"/>); the lights stay on the other half.
/// Screen off, the cat slides, or a video: a link pasted or typed (a YouTube, Twitch or Vimeo page, or a
/// video's own URL: mp4, webm, an HLS .m3u8), played for the whole hall from now on the shared clock. The links
/// this player played before are one press away (kept in <c>user://cinema_links.txt</c>): a pad or a VR laser
/// picks one, Paste takes the clipboard, so nobody has to type a link without a keyboard.
///
/// <para>
/// Nothing here plays or keeps state: it asks <see cref="CinemaBooths"/> (the server decides, every client plays)
/// and shows what this client's decoder says (loading, the reason a link cannot play). Mouse, keyboard (Tab,
/// Enter), pad (D-pad, A, B) and the VR laser on the UI panel. Closes on E away from the link box, Esc / B, a
/// click outside, or walking away from the panel.
/// </para>
/// </summary>
public partial class CinemaPanelUi : CanvasLayer
{
    /// <summary>This wide, this far above the bottom of the screen: low, so the hall's screen stays in view above it (as the radio's player).</summary>
    private const float Width = 620, Gutter = 16, Lift = 96;
    private const int MaxRecent = 6;
    private const string RecentFile = "user://cinema_links.txt";

    public static CinemaPanelUi? Instance { get; private set; }

    private PanelContainer _panel = null!;
    private Label _title = null!, _now = null!, _status = null!;
    private Button _off = null!, _slides = null!, _stop = null!, _play = null!;
    private LineEdit _link = null!;
    /// <summary>Where focus lands on opening: a pad or a VR laser starts on a button, never in the text box.</summary>
    private Button _paste = null!;
    /// <summary>The hall's video through the server for everyone (#1298 phase 2): for a link some players cannot reach.</summary>
    private CheckBox _relay = null!;
    private VBoxContainer _recent = null!;
    private UnitSport.Player.FootPlayer? _me;
    private string _plan = "";
    private int _hall = -1;
    private double _since;
    private string _shown = "\0";

    public bool IsOpen => _panel != null && _panel.Visible;

    /// <summary>Opens the panel of hall <paramref name="hall"/> of cinema <paramref name="plan"/>, making it the first time.</summary>
    public static void Open(UnitSport.Player.FootPlayer? me, string plan, int hall)
    {
        if (Instance == null || !IsInstanceValid(Instance))
        {
            Instance = new CinemaPanelUi { Name = "CinemaPanelUi" };
            ((Node?)me ?? CinemaBooths.Instance!).GetTree().Root.AddChild(Instance);
        }
        Instance._me = me;
        Instance.OpenFor(plan, hall);
    }

    public override void _Ready()
    {
        Layer = 12;
        _panel = new PanelContainer { Visible = false, Theme = UiTheme.Get() };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.95f, 12, 18));
        AddChild(_panel);
        var box = UiKit.VBox(10);
        _panel.AddChild(box);

        var header = UiKit.HBox(10);
        box.AddChild(header);
        var titles = UiKit.VBox(0);
        titles.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(titles);
        _title = UiKit.Text("Projector", UiTheme.FontHeading, UiTheme.Text, bold: true);
        titles.AddChild(_title);
        _now = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _now.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        titles.AddChild(_now);
        var close = UiKit.IconButton(Icons.Close, InputHints.Format("Close ({menu})", InputDevice.KeyboardMouse));
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        close.Pressed += Close;
        header.AddChild(close);

        // what the screen shows: off, the slides, or the video below
        var shows = UiKit.HBox(10);
        box.AddChild(shows);
        _off = UiKit.Button("Screen off");
        _off.Pressed += () => Ask(screen: false, link: "");
        shows.AddChild(_off);
        _slides = UiKit.Button("Cat slides");
        _slides.Pressed += () => Ask(screen: true, link: "");
        shows.AddChild(_slides);
        _stop = UiKit.Button("Stop the video");
        _stop.Pressed += () => Ask(screen: true, link: "");
        shows.AddChild(_stop);

        box.AddChild(UiKit.Line());
        box.AddChild(UiKit.Section("Play a video"));
        var row = UiKit.HBox(10);
        box.AddChild(row);
        _link = new LineEdit
        {
            PlaceholderText = "A YouTube or Twitch link, or a video's URL",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MaxLength = CinemaVideo.MaxLink,
        };
        _link.TextSubmitted += _ => PlayTyped();
        row.AddChild(_link);
        var paste = _paste = UiKit.Button("Paste");
        paste.TooltipText = "The link on your clipboard";
        paste.Pressed += () =>
        {
            _link.Text = DisplayServer.ClipboardGet().Trim();
            _link.CaretColumn = _link.Text.Length;
        };
        row.AddChild(paste);
        _play = UiKit.Button("Play", primary: true);
        _play.Pressed += PlayTyped;
        row.AddChild(_play);
        _relay = new CheckBox
        {
            Text = "Through the server",
            FocusMode = Control.FocusModeEnum.All,
            TooltipText = "The server fetches the video and sends it to everyone in the hall: for a link some players cannot reach. Smaller picture.",
        };
        _relay.Toggled += on =>
        {
            if (CinemaBooths.Instance is not { } booths) return;
            var (playing, _) = booths.VideoOf(_plan, _hall);
            if (playing.Length > 0 && booths.RelayOf(_plan, _hall) != on) booths.AskVideo(_plan, _hall, playing, on);
        };
        box.AddChild(_relay);
        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.Visible = false;
        box.AddChild(_status);

        _recent = UiKit.VBox(4);
        box.AddChild(_recent);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private void OpenFor(string plan, int hall)
    {
        _plan = plan;
        _hall = hall;
        _title.Text = $"Hall {hall + 1} projector";
        _link.Text = "";
        _shown = "\0";
        Rebuild();
        Refresh();
        _panel.Visible = true;
        MouseCapture.Mode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Callable.From(() => { Fit(); _paste.GrabFocus(); }).CallDeferred();
        if (!CinemaVideoStream.CanDecode) Status = "This device cannot play videos: the others in the hall see it, you see the slides.";
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        _link.ReleaseFocus();
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    private void Fit()
    {
        var screen = GetViewport().GetVisibleRect().Size;
        float w = Mathf.Min(Width, screen.X - 2 * Gutter), h = _panel.GetCombinedMinimumSize().Y;
        // on a short screen it would reach the middle: down to the bottom gutter instead
        float lift = screen.Y - Lift - h >= screen.Y / 2 ? Lift : Gutter;
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _panel.OffsetLeft = -w / 2;
        _panel.OffsetRight = w / 2;
        _panel.OffsetTop = -lift - h;
        _panel.OffsetBottom = -lift;
    }

    /// <summary>The line under the link: hidden while it has nothing to say, so the panel keeps no gap.</summary>
    private string Status
    {
        get => _status.Text;
        set
        {
            if (_status.Text == value) return;
            _status.Text = value;
            _status.Visible = value.Length > 0;
            Callable.From(Fit).CallDeferred();
        }
    }

    // ---- asking ----------------------------------------------------------------------------------

    private void PlayTyped()
    {
        string link = _link.Text.Trim();
        if (!CinemaVideo.Allowed(link)) { Status = "That is not a web link (https://…)."; return; }
        Remember(link);
        Ask(screen: true, link);
        _link.Text = "";
        Rebuild();
    }

    private void Ask(bool screen, string link)
    {
        if (CinemaBooths.Instance is not { } booths) return;
        var (lights, _) = booths.StateOf(_plan, _hall);
        booths.Ask(_plan, _hall, CinemaBooths.Pack(lights, screen));
        booths.AskVideo(_plan, _hall, screen ? link : "", _relay.ButtonPressed);
        Status = link.Length > 0 ? "Starting…" : "";
    }

    // ---- what plays ------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        // a shot's panel has no player (null): it stays until closed
        if (CinemaBooths.Instance == null || _me != null && (!IsInstanceValid(_me) || InteriorManager.Instance?.Current?.Key != _plan
            || CinemaBooths.At(_me) is not { } at || at.Plan != _plan || at.Hall != _hall))
        {
            Close();
            return;
        }
        _since += delta;
        if (_since < 0.2) return;
        _since = 0;
        Refresh();
    }

    /// <summary>The header and the buttons from the hall's state and this client's decoder, on change only.</summary>
    private void Refresh()
    {
        if (CinemaBooths.Instance is not { } booths) return;
        var (_, screen) = booths.StateOf(_plan, _hall);
        var (link, _) = booths.VideoOf(_plan, _hall);
        var stream = booths.StreamOf(_plan, _hall);
        if (link.Length > 0) _relay.SetPressedNoSignal(booths.RelayOf(_plan, _hall));
        string now = !screen ? "The screen is off"
            : link.Length == 0 ? "Showing the cat slides"
            : $"Playing {CinemaVideo.Title(link)}" + (stream is CinemaRelayFeed ? "  ·  through the server" : "")
              + (stream is { Status.Length: > 0 } s ? $"  ·  {s.Status}" : "");
        if (now == _shown) return;
        _shown = now;
        _now.Text = now;
        _off.Disabled = !screen;
        _off.TooltipText = screen ? "Dark screen, no sound" : "The screen is off already";
        _slides.Visible = link.Length == 0;
        _slides.Disabled = screen;
        _slides.TooltipText = screen ? "The slides are showing already" : "The cat slides, with the hall's film music";
        _stop.Visible = link.Length > 0;
        if (stream is { Failed: true }) Status = stream.Status;
        else if (_status.Text == "Starting…" && link.Length > 0) Status = "";
    }

    // ---- the links played before -----------------------------------------------------------------

    private static List<string> Recent()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(RecentFile)) return new();
            using var f = Godot.FileAccess.Open(RecentFile, Godot.FileAccess.ModeFlags.Read);
            return f.GetAsText().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(CinemaVideo.Allowed).Take(MaxRecent).ToList();
        }
        catch (Exception) { return new(); }
    }

    private static void Remember(string link)
    {
        var all = Recent();
        all.Remove(link);
        all.Insert(0, link);
        try
        {
            using var f = Godot.FileAccess.Open(RecentFile, Godot.FileAccess.ModeFlags.Write);
            f?.StoreString(string.Join('\n', all.Take(MaxRecent)));
        }
        catch (Exception) { /* a read-only profile: no history */ }
    }

    private void Rebuild()
    {
        foreach (var c in _recent.GetChildren()) c.QueueFree();
        var links = Recent();
        if (links.Count == 0) return;
        _recent.AddChild(UiKit.Text("Played before", UiTheme.FontSmall, UiTheme.TextDim));
        foreach (string link in links)
        {
            var b = UiKit.Button(CinemaVideo.Title(link));
            b.TooltipText = link;
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            b.Pressed += () => { Remember(link); Ask(screen: true, link); Rebuild(); };
            _recent.AddChild(b);
        }
        Callable.From(Fit).CallDeferred();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        bool typing = GetViewport().GuiGetFocusOwner() is LineEdit;
        bool outside = e is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } click
                       && !_panel.GetGlobalRect().HasPoint(click.Position);
        bool openingKey = e is InputEventKey && e.IsActionPressed(PlayerInput.InteractMount) && !typing;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu) || outside || openingKey)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
