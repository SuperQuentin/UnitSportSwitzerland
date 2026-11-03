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
/// picks one, Paste takes the clipboard, so nobody has to type a link without a keyboard. The cinema's own films (the
/// ones a hall shows by itself: the open films and the server's list, #1367) are a drop-down and a Play button; a link
/// playing can be starred (this player's own, <c>user://cinema_starred.txt</c>, at the end of the drop-down) and an admin
/// puts it on the server's list in a small window (its name, whether halls show it by themselves; <c>/cinema pool add</c>
/// and <c>set</c>, the server checks the rights).
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
    private const string StarFile = "user://cinema_starred.txt";
    private const int MaxStarred = 50;
    /// <summary>A starred link's id in the film picker: after the cinema's films.</summary>
    private const int StarredIds = 100000;

    public static CinemaPanelUi? Instance { get; private set; }

    private PanelContainer _panel = null!;
    private Label _title = null!, _now = null!, _status = null!;
    private Button _off = null!, _slides = null!, _stop = null!, _play = null!, _star = null!, _toServer = null!;
    /// <summary>What the star and the server's-list buttons were last set for: the link, starred, its place in the list, admin.</summary>
    private (string Link, bool Starred, int InPool, bool Admin) _starFor = ("\0", false, 0, false);
    /// <summary>An admin's window over the panel: the link playing on the server's list, its name and rotation.</summary>
    private PanelContainer _server = null!;
    private Label _serverTitle = null!;
    private LineEdit _serverName = null!;
    private CheckButton _serverRotation = null!;
    private Button _serverSave = null!, _serverRemove = null!;
    /// <summary>The link the window is for, and its place on the server's list (0: not on it).</summary>
    private string _serverLink = "";
    private int _serverAt;
    private LineEdit _link = null!;
    /// <summary>Where focus lands on opening: a pad or a VR laser starts on a button, never in the text box.</summary>
    private Button _paste = null!;
    /// <summary>The hall's video through the server for everyone (#1298 phase 2): for a link some players cannot reach.</summary>
    private CheckBox _relay = null!;
    private OptionButton _stereo = null!, _fit = null!, _ratio = null!, _films = null!;
    /// <summary>The server's list the film picker was filled from (-1: not yet).</summary>
    private int _filmsFor = -1;
    private CheckBox _glasses = null!;
    private VBoxContainer _recent = null!;
    private UnitSport.Player.FootPlayer? _me;
    private string _plan = "";
    private int _hall = -1;
    private double _since;
    private float _fitFor;
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
        _stop.Pressed += () =>
        {
            if (CinemaBooths.Instance is { } booths && booths.SharingIn(_plan, _hall)) booths.StopSharing();
            else Ask(screen: true, link: "");
        };
        shows.AddChild(_stop);
        // the link playing kept (#1367): starred for this player, or on the server's list for everyone (an admin)
        _star = UiKit.IconButton(Icons.Star, "Star it: it stays in your films list");
        _star.Pressed += ToggleStar;
        shows.AddChild(_star);
        _toServer = UiKit.Button("Server's films…");
        _toServer.Pressed += ToggleServer;
        shows.AddChild(_toServer);

        box.AddChild(UiKit.Line());
        box.AddChild(UiKit.Section("Play a video"));
        // the cinema's own films (#1367): what a phone, with no keyboard or clipboard, can play
        var films = UiKit.HBox(10);
        box.AddChild(films);
        _films = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TooltipText = "The films this cinema shows by itself, and the server's own list. Played for the hall from the start.",
        };
        films.AddChild(_films);
        var playFilm = UiKit.Button("Play the film", primary: true);
        playFilm.Pressed += PlayFilm;
        films.AddChild(playFilm);
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
            if (playing.Length > 0 && booths.RelayOf(_plan, _hall) != on) booths.AskVideo(_plan, _hall, playing, on, booths.StereoOf(_plan, _hall));
        };
        // 2D or a 3D film's layout (#1298 phase 6): guessed from a link's name, the same for a file
        var how = UiKit.HBox(10);
        box.AddChild(how);
        _stereo = new OptionButton { FocusMode = Control.FocusModeEnum.All };
        _stereo.AddItem("2D", (int)CinemaVideo.Stereo.Mono);
        _stereo.AddItem("3D side by side", (int)CinemaVideo.Stereo.SideBySide);
        _stereo.AddItem("3D top and bottom", (int)CinemaVideo.Stereo.TopBottom);
        _stereo.TooltipText = "A 3D film's two pictures, left and right or one above the other. In a headset each eye sees its own; on a screen, red-cyan glasses.";
        _stereo.ItemSelected += _ =>
        {
            if (CinemaBooths.Instance is not { } booths) return;
            var (playing, _) = booths.VideoOf(_plan, _hall);
            var want = (CinemaVideo.Stereo)_stereo.GetSelectedId();
            if (playing.Length > 0 && !CinemaVideo.IsLive(playing) && booths.StereoOf(_plan, _hall) != want)
                booths.AskVideo(_plan, _hall, playing, booths.RelayOf(_plan, _hall), want);
        };
        how.AddChild(_stereo);
        how.AddChild(_relay);
        _glasses = new CheckBox
        {
            Text = "Red-cyan glasses",
            FocusMode = Control.FocusModeEnum.All,
            ButtonPressed = InteriorNode.Anaglyph,
            TooltipText = "Off: the left picture alone, for watching a 3D film without glasses. A headset shows each eye its own.",
            Visible = false,
        };
        _glasses.Toggled += on => InteriorNode.Anaglyph = on;
        how.AddChild(_glasses);
        // the projector's display (#1298): how every video is fitted on this hall's screen; the curtains frame it
        var shown = UiKit.HBox(10);
        box.AddChild(shown);
        shown.AddChild(UiKit.Text("Display", UiTheme.FontSmall, UiTheme.TextDim));
        _fit = new OptionButton { FocusMode = Control.FocusModeEnum.All };
        _fit.AddItem("Contain", (int)CinemaVideo.Fit.Contain);
        _fit.AddItem("Cover", (int)CinemaVideo.Fit.Cover);
        _fit.AddItem("Stretch", (int)CinemaVideo.Fit.Stretch);
        _fit.TooltipText = "Contain: all of the picture, black bars where its shape differs. Cover: it fills the frame, cropped. Stretch: it fills the frame, distorted.";
        _fit.ItemSelected += _ => AskDisplay();
        shown.AddChild(_fit);
        _ratio = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (int i = 0; i < CinemaVideo.Ratios.Length; i++) _ratio.AddItem(CinemaVideo.Ratios[i].Name, i);
        _ratio.TooltipText = "The frame the picture is fitted into, centred on the screen; the curtains close to it.";
        _ratio.ItemSelected += _ => AskDisplay();
        shown.AddChild(_ratio);
        _link.TextChanged += text =>
        {
            if (CinemaVideo.Guess(text.Trim()) is var g && g != CinemaVideo.Stereo.Mono) _stereo.Select((int)g);
        };
        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.Visible = false;
        box.AddChild(_status);

        _recent = UiKit.VBox(4);
        box.AddChild(_recent);

        // sharing from this computer (#1298 phase 3): a desktop only (ffmpeg)
        _shareBox = UiKit.VBox(8);
        _shareBox.Visible = Platform.CanSpawnProcesses;
        box.AddChild(_shareBox);
        _shareBox.AddChild(UiKit.Line());
        _shareBox.AddChild(UiKit.Section("Share from this computer"));
        BuildServerWindow();
        var what = UiKit.HBox(10);
        _shareBox.AddChild(what);
        // a window's title can be long: the picker keeps the panel's width, not its longest item's
        _source = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.All, ClipText = true, FitToLongestItem = false };
        _source.TooltipText = "A monitor, a window, a webcam, or an NDI source on the network (OBS, a vision mixer, a concert's program camera)";
        what.AddChild(_source);
        _quality = new OptionButton { FocusMode = Control.FocusModeEnum.All };
        _quality.AddItem("Low (light)", (int)CinemaVideo.LiveQuality.Low);
        _quality.AddItem("Sharp (text)", (int)CinemaVideo.LiveQuality.High);
        _quality.AddItem("Smooth (motion)", (int)CinemaVideo.LiveQuality.Smooth);
        _quality.Select(1);
        _quality.TooltipText = "Low: 480x200, 15 a second. Sharp: 960x400, 10 a second (text reads from the seats). Smooth: 960x400, 20 a second (twice the data).";
        what.AddChild(_quality);
        var sound = UiKit.HBox(10);
        _shareBox.AddChild(sound);
        _pcSound = new CheckBox { Text = "PC sound", ButtonPressed = true, FocusMode = Control.FocusModeEnum.All,
            TooltipText = "What your computer plays (every app but the game), for a screen or a window" };
        sound.AddChild(_pcSound);
        _mic = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.All, ClipText = true, FitToLongestItem = false };
        _mic.TooltipText = "A microphone mixed in, to talk over it";
        sound.AddChild(_mic);
        _share = UiKit.Button("Share", primary: true);
        _share.Pressed += ToggleShare;
        sound.AddChild(_share);
        // a film on this computer (#1298 phase 4): streamed from here at the quality above, nothing stored on the server
        var file = UiKit.Button("Play a video file…");
        file.TooltipText = "A film on this computer (" + string.Join(" ", CinemaFileSource.Extensions)
            + "), played for the hall from here at the quality above. It stops if you leave.";
        file.Pressed += PickFile;
        file.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _shareBox.AddChild(file);
    }

    private FileDialog? _picker;

    private void PickFile()
    {
        if (_picker == null)
        {
            _picker = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true, Title = "A video to play in the hall",
                Filters = new[] { string.Join(", ", CinemaFileSource.Extensions.Select(e => "*" + e)) + " ; Videos" },
            };
            _picker.FileSelected += path =>
            {
                if (CinemaBooths.Instance is not { } booths) return;
                var (lights, _) = booths.StateOf(_plan, _hall);
                booths.Ask(_plan, _hall, CinemaBooths.Pack(lights, true));
                if (CinemaVideo.Guess(path) is var g && g != CinemaVideo.Stereo.Mono) _stereo.Select((int)g);
                booths.StartFile(_plan, _hall, path, (CinemaVideo.LiveQuality)_quality.GetSelectedId(), (CinemaVideo.Stereo)_stereo.GetSelectedId());
                Status = "Starting the share…";
            };
            AddChild(_picker);
        }
        _picker.PopupCentered(new Vector2I(760, 520));
    }

    private VBoxContainer _shareBox = null!;
    private OptionButton _source = null!, _quality = null!, _mic = null!;
    private CheckBox _pcSound = null!;
    private Button _share = null!;
    private CinemaDevices.Found? _devices;
    private readonly List<CaptureSource> _sources = new();
    private bool _listing;

    /// <summary>What this computer can share, listed on a worker the first time the panel opens (ffmpeg takes a moment).</summary>
    private void ListDevices()
    {
        if (_listing || !Platform.CanSpawnProcesses) return;
        _listing = true;
        _source.Clear();
        _source.AddItem("Looking for screens and cameras…");
        _source.Disabled = true;
        Task.Run(() =>
        {
            var found = CinemaDevices.List();
            Callable.From(() => ShowDevices(found)).CallDeferred();
        });
    }

    private void ShowDevices(CinemaDevices.Found found)
    {
        _listing = false;
        _devices = found;
        _sources.Clear();
        _sources.AddRange(found.Monitors);
        _sources.AddRange(found.Windows);
        _sources.AddRange(found.Cameras);
        if (CinemaVideo.AllowLocal) _sources.Add(new CaptureSource(CaptureKind.Test, "Test pattern", "test"));
        _source.Clear();
        foreach (var s in _sources)
            _source.AddItem(s.Kind switch { CaptureKind.Window => $"Window: {s.Label}", CaptureKind.Webcam => $"Camera: {s.Label}", _ => s.Label });
        _source.Disabled = _sources.Count == 0;
        if (_sources.Count == 0) _source.AddItem("Nothing to share found");
        _pcSound.Visible = found.PcSound;
        _mic.Clear();
        _mic.AddItem("No microphone");
        foreach (string m in found.Mics) _mic.AddItem(m);
        Callable.From(Fit).CallDeferred();
    }

    private void ToggleShare()
    {
        if (CinemaBooths.Instance is not { } booths) return;
        if (booths.SharingIn(_plan, _hall)) { booths.StopSharing(); Refresh(true); return; }
        int i = _source.Selected;
        if (i < 0 || i >= _sources.Count) { Status = "Nothing to share was found on this computer."; return; }
        var (lights, _) = booths.StateOf(_plan, _hall);
        booths.Ask(_plan, _hall, CinemaBooths.Pack(lights, true));
        string? mic = _mic.Selected > 0 && _devices != null ? _devices.Mics[_mic.Selected - 1] : null;
        booths.StartSharing(_plan, _hall, _sources[i], (CinemaVideo.LiveQuality)_quality.GetSelectedId(), _pcSound.Visible && _pcSound.ButtonPressed, mic);
        Status = "Starting the share…";
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
        _filmsFor = -1;
        _link.Text = "";
        _shown = "\0";
        Rebuild();
        Refresh(true);
        if (_devices == null) ListDevices();
        _panel.Visible = true;
        MouseCapture.Mode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Callable.From(() => { Fit(); _paste.GrabFocus(); }).CallDeferred();
        if (!CinemaVideoStream.CanDecode) Status = "This device gets videos through the server: a smaller picture than the others'.";
    }

    public void Close()
    {
        if (!IsOpen) return;
        CloseServer();
        _panel.Visible = false;
        _link.ReleaseFocus();
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    private void Fit()
    {
        var screen = GetViewport().GetVisibleRect().Size;
        float w = Mathf.Min(Width, screen.X - 2 * Gutter), h = _fitFor = _panel.GetCombinedMinimumSize().Y;
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
        if (CinemaVideo.Guess(link) is var guess && guess != CinemaVideo.Stereo.Mono) _stereo.Select((int)guess);
        Remember(link);
        Ask(screen: true, link);
        _link.Text = "";
        Rebuild();
    }

    /// <summary>The picked film from its start for the whole hall: one of the cinema's (2D) or one this player starred.</summary>
    private void PlayFilm()
    {
        if (CinemaBooths.Instance is not { } booths) return;
        int id = _films.GetSelectedId();
        if (id >= StarredIds)
        {
            var starred = Starred();
            if (id - StarredIds >= starred.Count) return;
            string link = starred[id - StarredIds];
            _stereo.Select((int)CinemaVideo.Guess(link));
            Ask(screen: true, link);
            return;
        }
        if (id < 0 || id >= booths.FilmCount) return;
        _stereo.Select((int)CinemaVideo.Stereo.Mono);
        Ask(screen: true, booths.FilmAt(id).Link);
    }

    private void ToggleStar()
    {
        if (CinemaBooths.Instance is not { } booths) return;
        var (link, _) = booths.VideoOf(_plan, _hall);
        if (link.Length == 0) return;
        var all = Starred();
        if (!all.Remove(link)) all.Insert(0, link);
        try
        {
            using var f = Godot.FileAccess.Open(StarFile, Godot.FileAccess.ModeFlags.Write);
            f?.StoreString(string.Join('\n', all.Take(MaxStarred)));
        }
        catch (Exception) { /* a read-only profile: no stars */ }
        _filmsFor = -1;
        Refresh(true);
    }

    /// <summary>The admin's window: a name, the halls' rotation, Save, Remove (once on the list), Cancel.</summary>
    private void BuildServerWindow()
    {
        _server = new PanelContainer { Visible = false, Theme = UiTheme.Get() };
        _server.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(1f, 12, 18));
        AddChild(_server);
        var box = UiKit.VBox(12);
        _server.AddChild(box);
        _serverTitle = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        box.AddChild(_serverTitle);
        _serverName = UiKit.FormField(box, "Name", "", "The video's own title", "as the projector lists it");
        _serverName.MaxLength = 80;
        _serverName.TextSubmitted += _ => SaveServer();
        _serverRotation = new CheckButton
        {
            Text = "In the halls' rotation",
            FocusMode = Control.FocusModeEnum.All,
            TooltipText = "On: halls show it by themselves too. Off: only picked here, at the projector.",
        };
        box.AddChild(_serverRotation);
        var buttons = UiKit.HBox(10);
        box.AddChild(buttons);
        _serverRemove = UiKit.Button("Remove from the list");
        _serverRemove.Pressed += () =>
        {
            if (_serverAt > 0) SendChat($"/cinema pool remove {_serverAt}");
            CloseServer();
        };
        buttons.AddChild(_serverRemove);
        buttons.AddChild(UiKit.Spacer(expand: true));
        var cancel = UiKit.Button("Cancel");
        cancel.Pressed += CloseServer;
        buttons.AddChild(cancel);
        _serverSave = UiKit.Button("Save", primary: true);
        _serverSave.Pressed += SaveServer;
        buttons.AddChild(_serverSave);
    }

    /// <summary>An admin's: the window for the link playing, filled from its place on the server's list.</summary>
    private void ToggleServer()
    {
        if (CinemaBooths.Instance is not { } booths) return;
        var (link, _) = booths.VideoOf(_plan, _hall);
        if (link.Length == 0) return;
        _serverLink = link;
        _serverAt = booths.PoolIndexOf(link);
        var film = _serverAt > 0 ? booths.FilmAt(CinemaVideo.OpenFilms.Length + _serverAt - 1) : default;
        _serverTitle.Text = _serverAt > 0 ? "On the server's films" : "Add to the server's films";
        _serverName.Text = _serverAt > 0 ? film.Title : "";
        _serverRotation.ButtonPressed = _serverAt == 0 || film.Rotation;
        _serverRemove.Visible = _serverAt > 0;
        _serverSave.Text = _serverAt > 0 ? "Save" : "Add";
        _server.Visible = true;
        Callable.From(() =>
        {
            var screen = GetViewport().GetVisibleRect().Size;
            var size = _server.GetCombinedMinimumSize();
            float w = Mathf.Min(Mathf.Max(size.X, 520), screen.X - 2 * Gutter);
            _server.SetAnchorsPreset(Control.LayoutPreset.Center);
            _server.OffsetLeft = -w / 2;
            _server.OffsetRight = w / 2;
            _server.OffsetTop = -size.Y / 2;
            _server.OffsetBottom = size.Y / 2;
            _serverSave.GrabFocus();
        }).CallDeferred();
    }

    /// <summary>The window's choice to the server as the chat command an admin would type (the server checks the rights).</summary>
    private void SaveServer()
    {
        string name = _serverName.Text.Trim().Replace('\n', ' ');
        string rotation = _serverRotation.ButtonPressed ? "rotation" : "norotation";
        SendChat(_serverAt > 0 ? $"/cinema pool set {_serverAt} {rotation} {name}".TrimEnd()
            : $"/cinema pool add {_serverLink} {rotation} {name}".TrimEnd());
        if (_serverAt == 0) Status = "Looking it up on the server: the chat says when it is on the list.";
        CloseServer();
    }

    private void CloseServer()
    {
        if (!_server.Visible) return;
        _server.Visible = false;
        _serverName.ReleaseFocus();
        Callable.From(() => _toServer.GrabFocus()).CallDeferred();
    }

    private void SendChat(string line)
    {
        if (GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat) chat.Send(line);
    }

    /// <summary>The links this player starred, newest first.</summary>
    private static List<string> Starred()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(StarFile)) return new();
            using var f = Godot.FileAccess.Open(StarFile, Godot.FileAccess.ModeFlags.Read);
            return f.GetAsText().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(CinemaVideo.Allowed).Take(MaxStarred).ToList();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>The links starred, read once per opening and on a star (the buttons ask every refresh).</summary>
    private List<string>? _starred;

    /// <summary>The star and the server's-list buttons for the link playing, on change only: a web link that is no open film.</summary>
    private void StarButtons(CinemaBooths booths, string link)
    {
        bool admin = !Net.NetLink.Online(this) || Permissions.IsAdmin;
        _starred ??= Starred();
        var key = (link, link.Length > 0 && _starred.Contains(link), booths.PoolIndexOf(link), admin);
        if (key == _starFor) return;
        _starFor = key;
        bool open = Array.Exists(CinemaVideo.OpenFilms, f => f.Link == link);
        bool keepable = link.Length > 0 && !CinemaVideo.IsLive(link) && !open && CinemaVideo.Allowed(link);
        _star.Visible = keepable && key.Item3 == 0;
        _star.Icon = key.Item2 ? Icons.StarFilled : Icons.Star;
        _star.TooltipText = key.Item2 ? "Starred: in your films list. Press to unstar." : "Star it: it stays in your films list";
        _toServer.Visible = keepable && admin;
        _toServer.TooltipText = key.Item3 > 0 ? "On the server's list: its name, the halls' rotation, or off the list"
            : "On the server's list, for everyone: a name, and whether halls show it by themselves";
    }

    /// <summary>The film picker from the cinema's list, on opening and when the server's list changes; the hall's film selected.</summary>
    private void FillFilms(CinemaBooths booths, string playing)
    {
        if (_filmsFor == booths.PoolVersion) return;
        _filmsFor = booths.PoolVersion;
        _starred = Starred();
        _films.Clear();
        int pick = 0;
        for (int i = 0; i < booths.FilmCount; i++)
        {
            var f = booths.FilmAt(i);
            _films.AddItem($"{f.Title}  ({Math.Max(1, (int)Math.Round(f.Seconds / 60))} min)", i);
            if (f.Link == playing) pick = _films.ItemCount - 1;
        }
        // this player's starred links after them (one on the server's list shows there already)
        for (int j = 0; j < _starred.Count; j++)
        {
            if (booths.FilmTitle(_starred[j]).Length > 0) continue;
            _films.AddItem($"Starred: {CinemaVideo.Title(_starred[j])}", StarredIds + j);
            if (_starred[j] == playing) pick = _films.ItemCount - 1;
        }
        if (_films.ItemCount > 0) _films.Select(pick);
    }

    /// <summary>The hall's display from the two pickers: everyone in the hall sees it.</summary>
    private void AskDisplay()
    {
        if (CinemaBooths.Instance is not { } booths) return;
        int display = CinemaVideo.PackDisplay((CinemaVideo.Fit)_fit.GetSelectedId(), _ratio.GetSelectedId());
        if (display != booths.DisplayOf(_plan, _hall)) booths.AskDisplay(_plan, _hall, display);
    }

    private void Ask(bool screen, string link)
    {
        if (CinemaBooths.Instance is not { } booths) return;
        var (lights, _) = booths.StateOf(_plan, _hall);
        booths.Ask(_plan, _hall, CinemaBooths.Pack(lights, screen));
        booths.AskVideo(_plan, _hall, screen ? link : "", _relay.ButtonPressed, (CinemaVideo.Stereo)_stereo.GetSelectedId());
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
        // a wrapped line knows its height only once laid out: fit again when it changes
        float h = _panel.GetCombinedMinimumSize().Y;
        if (h != _fitFor) Fit();
    }

    /// <summary>The header and the buttons from the hall's state and this client's decoder, on change only.</summary>
    private void Refresh(bool force = false)
    {
        if (CinemaBooths.Instance is not { } booths) return;
        if (force) _shown = "\0";
        var (_, screen) = booths.StateOf(_plan, _hall);
        var (link, _) = booths.VideoOf(_plan, _hall);
        FillFilms(booths, link);
        StarButtons(booths, link);
        var stream = booths.StreamOf(_plan, _hall);
        if (link.Length > 0) _relay.SetPressedNoSignal(booths.RelayOf(_plan, _hall));
        var (fit, ratio) = CinemaVideo.UnpackDisplay(booths.DisplayOf(_plan, _hall));
        if (_fit.GetSelectedId() != (int)fit) _fit.Select((int)fit);
        if (_ratio.GetSelectedId() != ratio) _ratio.Select(ratio);
        var layout = booths.StereoOf(_plan, _hall);
        if (link.Length > 0) _stereo.Select((int)layout);
        // the glasses are this viewer's: offered while a 3D film plays on a flat screen
        _glasses.Visible = layout != CinemaVideo.Stereo.Mono && !XR.XrSession.Active;
        string now = !screen ? "The screen is off"
            : link.Length == 0 ? "Showing the cat slides"
            : (CinemaVideo.IsLive(link) ? $"Live: {CinemaVideo.Title(link)}" : $"Playing {(booths.FilmTitle(link) is { Length: > 0 } film ? film : CinemaVideo.Title(link))}")
              + (booths.StereoOf(_plan, _hall) != CinemaVideo.Stereo.Mono ? "  ·  3D" : "")
              + (stream is CinemaRelayFeed && !CinemaVideo.IsLive(link) ? "  ·  through the server" : "")
              + (stream is { Status.Length: > 0 } s ? $"  ·  {s.Status}" : "");
        bool sharing = booths.SharingIn(_plan, _hall);
        now += sharing ? "  ·  you share it" + (booths.Sharing is { Status.Length: > 0 } c ? $" ({c.Status})" : "") : "";
        if (now == _shown) return;
        _shown = now;
        _now.Text = now;
        _off.Disabled = !screen;
        _off.TooltipText = screen ? "Dark screen, no sound" : "The screen is off already";
        _slides.Visible = link.Length == 0;
        _slides.Disabled = screen;
        _slides.TooltipText = screen ? "The slides are showing already" : "The cat slides, with the hall's film music";
        _stop.Visible = link.Length > 0;
        _stop.Text = CinemaVideo.IsLive(link) ? "Stop the share" : "Stop the video";
        _share.Text = sharing ? "Stop sharing" : "Share";
        _relay.Visible = !CinemaVideo.IsLive(link);
        if (sharing && _status.Text == "Starting the share…") Status = "";
        if (stream is { Failed: true }) Status = stream.Status;
        else if (_status.Text == "Starting…" && link.Length > 0) Status = "";
        if (booths.Sharing is { Failed: true } failed && sharing) Status = failed.Status;
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
                       && !_panel.GetGlobalRect().HasPoint(click.Position)
                       && !(_server.Visible && _server.GetGlobalRect().HasPoint(click.Position));
        if (_server.Visible && (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)))
        {
            CloseServer();
            GetViewport().SetInputAsHandled();
            return;
        }
        bool openingKey = e is InputEventKey && e.IsActionPressed(PlayerInput.InteractMount) && !typing;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu) || outside || openingKey)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
