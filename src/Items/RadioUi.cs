using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Audio.Live;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Items;

/// <summary>
/// The panel of a radio, the music picker (#211), in two views (#392). The **player**, a small
/// stereo low on the screen that every radio opens on: what plays now (title, elapsed / length
/// bar, previous, play/stop, next, and what happens when it ends), the volume, and for a radio
/// lying in the world, pick it back up. The **library**, one button (or pad Y, or / to search)
/// deeper and centred: a search box over the CDs (the server's shared ones, then this player's
/// own) and, in a car, the live stations; a box to burn a new CD from a link for everyone or for
/// yourself only, with its progress. In the menus' look (<see cref="UiTheme"/>), driven by mouse,
/// keyboard (arrows, Enter) or pad (D-pad, A, B). It closes on the key that opened it, Esc, or a
/// click outside it: the cursor comes back in the middle of the screen, above the player, so the
/// click that meant "close" can no longer start the CD under it (#375).
///
/// <para>
/// Three radios open it: one in the world (E beside it), the one in the hand (Use), and a car's
/// stereo (<see cref="PlayerInput.RadioPanel"/>, at the wheel; a passenger sees what the driver
/// plays but cannot change it). Nothing here plays sound or keeps state of its own: it asks
/// whoever owns the radio's play state — the server for a world radio, the stack's data for the
/// held one, the driver's replicated <see cref="FootPlayer.CarCd"/> / <see cref="FootPlayer.CarRadio"/>
/// for a car — and every speaker follows the shared clock.
/// </para>
///
/// <para>
/// It also runs, open or not, the CD changer of the radios this player owns (held, driven):
/// when one of their CDs ends it puts on what <see cref="RadioQueue"/> says follows.
/// </para>
///
/// <para>
/// While open it registers with <see cref="UiFocus"/>, so typing does not walk the player around
/// (a car coasts, as with chat), and it gives the mouse back; closing re-captures it.
/// </para>
/// </summary>
public partial class RadioUi : CanvasLayer
{
    /// <summary>Farther than this from the radio and the panel closes itself.</summary>
    private const float WalkAway = 4f;
    private const float MaxWidth = 700, MaxHeight = 660, Gutter = 16;
    /// <summary>The player view: this wide, this far above the bottom of the screen (clear of the hotbar).</summary>
    private const float PlayerWidth = 590, PlayerLift = 96;
    /// <summary>The view toggle's two faces; the probes press them by text.</summary>
    public const string LibraryLabel = "Library  ▸", PlayerLabel = "◂  Player";

    private enum Target { World, Held, Car, Church }

    /// <summary>The live panel, for <see cref="FootPlayer.TryInteract"/>.</summary>
    public static RadioUi? Instance { get; private set; }

    private Func<FootPlayer?> _local = () => null;
    private Inventory _inventory = new();
    private Target _target;
    private RadioBody? _radio;
    /// <summary>The church whose radio the panel is on (#370), by plan key.</summary>
    private string _churchPlan = "";
    private int _heldSlot = -1;

    // the mode a held or car radio starts its next CD with, when none plays to carry it
    private static RadioMode _pendingMode = RadioMode.Once;
    private readonly Random _random = new();

    private PanelContainer _panel = null!;
    private Label _title = null!, _subtitle = null!;
    private Label _nowTitle = null!, _nowMeta = null!, _time = null!;
    private ProgressBar _bar = null!;
    private RadioCassette _cassette = null!;
    private Button _prev = null!, _playStop = null!, _next = null!, _mode = null!, _pick = null!;
    private LineEdit _search = null!;
    private Label _count = null!;
    private VBoxContainer _rows = null!;
    private ScrollContainer _scroll = null!;
    private HSlider _volume = null!;
    private Label _volumeValue = null!;
    private LineEdit _link = null!;
    private CheckBox _mine = null!;
    private ProgressBar _burnBar = null!;
    private Label _status = null!, _footer = null!;
    private VBoxContainer _library = null!;
    private Button _view = null!;
    private bool _libraryShown;

    private readonly Dictionary<int, Button> _cdRows = new();
    private readonly Dictionary<int, Button> _stationRows = new();
    private int _shownCd = int.MinValue, _shownStation = int.MinValue;
    private bool _shownLocked;
    private double _statusUntil, _sinceNow;
    private bool _burning;

    public bool IsOpen => _panel != null && _panel.Visible;

    /// <summary>The panel is on the radio in the hand rather than one in the world.</summary>
    public bool Held => IsOpen && _target == Target.Held;

    /// <summary>The panel is on a car's stereo. For the probes.</summary>
    public bool OnCar => IsOpen && _target == Target.Car;

    /// <summary>Into the pack, or on the ground when it is full (<see cref="ItemController.Give"/>).</summary>
    public Func<ItemStack, int>? Give { get; set; }

    public static RadioUi Create(Func<FootPlayer?> local, Inventory inventory) =>
        new() { Name = "RadioUi", _local = local, _inventory = inventory };

    // ---- building ---------------------------------------------------------------------------

    public override void _Ready()
    {
        Instance = this;
        Layer = 12;
        _panel = new PanelContainer { Visible = false, Theme = UiTheme.Get() };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.95f, 12, 18));
        AddChild(_panel);

        var box = UiKit.VBox(10);
        _panel.AddChild(box);

        // header: what radio this is, and a close cross
        var header = UiKit.HBox(10);
        box.AddChild(header);
        var titles = UiKit.VBox(0);
        titles.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(titles);
        _title = UiKit.Text("Radio", UiTheme.FontHeading, UiTheme.Text, bold: true);
        titles.AddChild(_title);
        _subtitle = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        titles.AddChild(_subtitle);
        _pick = UiKit.Button("Pick up");
        _pick.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _pick.Pressed += PickUp;
        header.AddChild(_pick);
        var close = UiKit.IconButton(Icons.Close, InputHints.Format("Close ({menu})", InputDevice.KeyboardMouse));
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        close.Pressed += Close;
        header.AddChild(close);

        box.AddChild(NowPlayingCard());

        // the library: everything to pick from and burn, shown on demand (#392)
        _library = UiKit.VBox(10);
        _library.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // search over the list
        var search = UiKit.HBox(10);
        _library.AddChild(search);
        _search = new LineEdit
        {
            PlaceholderText = "Search CDs   ( / )",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClearButtonEnabled = true,
            MaxLength = 80,
        };
        _search.TextChanged += _ => Rebuild();
        _search.TextSubmitted += _ => PressFirstRow();
        search.AddChild(_search);
        _count = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextFaint);
        _count.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        search.AddChild(_count);

        (_scroll, _rows) = UiKit.ScrollPage(2);
        _scroll.CustomMinimumSize = new Vector2(0, 120);
        _library.AddChild(_scroll);

        // volume: one for every radio this player hears; then the way into (or out of) the library
        var volume = UiKit.HBox(12);
        box.AddChild(volume);
        var volumeLabel = UiKit.Text("Volume", UiTheme.FontSmall, UiTheme.TextDim);
        volumeLabel.CustomMinimumSize = new Vector2(64, 0);
        volumeLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        volume.AddChild(volumeLabel);
        _volume = new HSlider
        {
            MinValue = 0, MaxValue = 1, Step = 0.05, Value = RadioSpeaker.UserVolume,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 22),
            FocusMode = Control.FocusModeEnum.All,
        };
        _volumeValue = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, align: HorizontalAlignment.Right);
        _volume.ValueChanged += v =>
        {
            RadioSpeaker.UserVolume = (float)v;
            _volumeValue.Text = Percent((float)v);
        };
        _volume.DragEnded += _ => RadioSpeaker.SaveVolume();
        volume.AddChild(_volume);
        _volumeValue.CustomMinimumSize = new Vector2(48, 0);
        _volumeValue.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        volume.AddChild(_volumeValue);
        _view = UiKit.Button(LibraryLabel, minWidth: 130);
        _view.TooltipText = "All the CDs and stations, and burning a new CD";
        _view.Pressed += () => ShowLibrary(!_libraryShown);
        volume.AddChild(_view);

        box.AddChild(_library);
        _library.AddChild(UiKit.Line());

        // burning a CD from a link
        var burn = UiKit.HBox(10);
        _library.AddChild(burn);
        _link = new LineEdit
        {
            PlaceholderText = "Paste a YouTube link to burn a CD",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MaxLength = 300,
        };
        _link.TextSubmitted += _ => Burn();
        burn.AddChild(_link);
        _mine = new CheckBox { Text = "Just for me", FocusMode = Control.FocusModeEnum.All, TooltipText = "Burn it on this computer, into your own list: nobody else hears it" };
        _mine.Visible = Platform.CanSpawnProcesses; // a personal burn runs yt-dlp + ffmpeg here (#63)
        burn.AddChild(_mine);
        var burnButton = UiKit.Button("Burn");
        burnButton.Pressed += Burn;
        burn.AddChild(burnButton);
        // a file of the player's own (#736): burnt here for "just for me" or offline, else uploaded
        // and virus-scanned on the server
        var fromFile = UiKit.Button("From a file…");
        fromFile.TooltipText = "Burn a song from your computer (" + string.Join(" ", CdUpload.Extensions)
            + $", up to {CdUpload.MaxBytes / (1024 * 1024)} MB). Online, the server checks it for viruses first.";
        fromFile.Pressed += PickFile;
        burn.AddChild(fromFile);

        var status = UiKit.HBox(10);
        _library.AddChild(status);
        _burnBar = Bar(6);
        _burnBar.CustomMinimumSize = new Vector2(120, 6);
        _burnBar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _burnBar.Visible = false;
        status.AddChild(_burnBar);
        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.ClipText = true;
        _status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        status.AddChild(_status);

        _footer = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint, align: HorizontalAlignment.Center);
        box.AddChild(_footer);

        if (CdLibrary.Instance is { } library)
        {
            library.Changed += OnLibraryChanged;
            library.BurnStatus += OnBurnStatus;
        }
        GetViewport().SizeChanged += Fit;
    }

    /// <summary>
    /// The player (#725): a cassette deck. The tape turning on the left; the title, its style and
    /// big round keys on the right; the progress along the bottom.
    /// </summary>
    private PanelContainer NowPlayingCard()
    {
        var card = UiKit.VBox(8);
        var deck = UiKit.HBox(14);
        card.AddChild(deck);
        _cassette = new RadioCassette();
        deck.AddChild(_cassette);

        var side = UiKit.VBox(4);
        side.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        deck.AddChild(side);
        _nowTitle = UiKit.Text("", UiTheme.FontBody + 3, UiTheme.Text, bold: true);
        _nowTitle.ClipText = true;
        _nowTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        side.AddChild(_nowTitle);
        _nowMeta = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _nowMeta.ClipText = true;
        _nowMeta.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        side.AddChild(_nowMeta);

        var controls = UiKit.HBox(8);
        controls.AddThemeConstantOverride("separation", 8);
        side.AddChild(controls);
        _prev = DeckKey(UiKit.Button("◀◀"), 46, 46);
        _prev.TooltipText = "Previous";
        _prev.Pressed += () => Skip(-1);
        controls.AddChild(_prev);
        _playStop = DeckKey(UiKit.Button("▶  Play", primary: true), 112, 46, UiTheme.Amber);
        _playStop.Pressed += PlayStop;
        controls.AddChild(_playStop);
        _next = DeckKey(UiKit.Button("▶▶"), 46, 46);
        _next.TooltipText = "Next";
        _next.Pressed += () => Skip(1);
        controls.AddChild(_next);
        controls.AddChild(UiKit.Spacer(expand: true));
        _mode = UiKit.Button("");
        _mode.TooltipText = "What happens when the CD ends";
        _mode.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        _mode.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _mode.Pressed += CycleMode;
        controls.AddChild(_mode);

        var progress = UiKit.HBox(10);
        card.AddChild(progress);
        _bar = Bar(4);
        _bar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _bar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        progress.AddChild(_bar);
        _time = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint, align: HorizontalAlignment.Right);
        _time.CustomMinimumSize = new Vector2(70, 0);
        progress.AddChild(_time);
        return UiKit.Card(card, 0.6f, 12);
    }

    /// <summary>A chunky deck key: fully round when square, a pill when wider.</summary>
    private static Button DeckKey(Button b, int w, int h, Color? fill = null)
    {
        b.CustomMinimumSize = new Vector2(w, h);
        int r = h / 2;
        var bg = fill ?? new Color(1, 1, 1, 0.09f);
        b.AddThemeStyleboxOverride("normal", UiTheme.Flat(bg, r, 10, 6));
        b.AddThemeStyleboxOverride("hover", UiTheme.Flat(bg.Lightened(0.15f), r, 10, 6));
        b.AddThemeStyleboxOverride("pressed", UiTheme.Flat(bg.Darkened(0.15f), r, 10, 6));
        b.AddThemeStyleboxOverride("hover_pressed", UiTheme.Flat(bg.Darkened(0.15f), r, 10, 6));
        b.AddThemeStyleboxOverride("focus", UiTheme.Flat(new Color(0, 0, 0, 0), r, 10, 6, Colors.White, 2));
        b.AddThemeStyleboxOverride("disabled", UiTheme.Flat(new Color(bg, bg.A * 0.4f), r, 10, 6));
        b.AddThemeFontSizeOverride("font_size", UiTheme.FontBody + 2);
        return b;
    }

    /// <summary>A thin amber progress bar on a faint track.</summary>
    private static ProgressBar Bar(int height)
    {
        var bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Step = 0, ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, height),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bar.AddThemeStyleboxOverride("background", UiTheme.Flat(new Color(1, 1, 1, 0.08f), height / 2, 0, 0));
        bar.AddThemeStyleboxOverride("fill", UiTheme.Flat(UiTheme.Amber, height / 2, 0, 0));
        return bar;
    }

    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100)} %";

    /// <summary>
    /// The library: centred, never wider or taller than the screen less a gutter. The player: as
    /// tall as its content, low and centred like a car stereo, leaving the middle of the screen
    /// (where the freed cursor appears) to the world.
    /// </summary>
    private void Fit()
    {
        if (_panel == null) return;
        var screen = GetViewport().GetVisibleRect().Size;
        if (_libraryShown)
        {
            float w = Mathf.Min(MaxWidth, screen.X - 2 * Gutter), h = Mathf.Min(MaxHeight, screen.Y - 2 * Gutter);
            _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
            _panel.OffsetLeft = -w / 2;
            _panel.OffsetRight = w / 2;
            _panel.OffsetTop = -h / 2;
            _panel.OffsetBottom = h / 2;
            return;
        }
        float pw = Mathf.Min(PlayerWidth, screen.X - 2 * Gutter);
        float ph = _panel.GetCombinedMinimumSize().Y;
        // on a short screen it would still reach the middle: down to the bottom gutter instead
        float lift = screen.Y - PlayerLift - ph >= screen.Y / 2 ? PlayerLift : Gutter;
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _panel.OffsetLeft = -pw / 2;
        _panel.OffsetRight = pw / 2;
        _panel.OffsetTop = -lift - ph;
        _panel.OffsetBottom = -lift;
    }

    /// <summary>Switches between the player and the library (#392); the library focuses the row that plays.</summary>
    public void ShowLibrary(bool on)
    {
        _libraryShown = on;
        _library.Visible = on;
        _view.Text = on ? PlayerLabel : LibraryLabel;
        Fit();
        if (!IsOpen) return;
        if (on) { Rebuild(); FocusCurrent(); }
        else
        {
            _search.ReleaseFocus();
            _link.ReleaseFocus();
            _playStop.CallDeferred(Control.MethodName.GrabFocus);
        }
    }

    public override void _ExitTree()
    {
        if (CdLibrary.Instance is { } library)
        {
            library.Changed -= OnLibraryChanged;
            library.BurnStatus -= OnBurnStatus;
        }
        if (Instance == this) Instance = null;
        if (IsOpen) UiFocus.Set(this, false);
    }

    // ---- opening and closing ----------------------------------------------------------------

    /// <summary>Opens the panel on a radio lying in the world.</summary>
    public void Open(RadioBody radio)
    {
        _radio = radio;
        _heldSlot = -1;
        OpenPanel(Target.World);
    }

    /// <summary>Opens (or, open already, closes) the panel on the radio in hotbar <paramref name="slot"/>, the one in the hand.</summary>
    public void OpenHeld(int slot)
    {
        if (IsOpen) { Close(); return; }
        _radio = null;
        _heldSlot = slot;
        OpenPanel(Target.Held);
    }

    /// <summary>Opens (or, open on it already, closes) the panel on the stereo of the car the local player sits in.</summary>
    public void OpenCar()
    {
        if (IsOpen) { if (_target == Target.Car) Close(); return; }
        if (_local()?.StereoOwner == null) return;
        _radio = null;
        _heldSlot = -1;
        OpenPanel(Target.Car);
    }

    /// <summary>Opens the panel on the radio by the pastor rat of the church the player is in (#370).</summary>
    public void OpenChurch(string plan)
    {
        if (IsOpen) return;
        _radio = null;
        _heldSlot = -1;
        _churchPlan = plan;
        OpenPanel(Target.Church);
    }

    private void OpenPanel(Target target)
    {
        _target = target;
        _title.Text = target switch { Target.Car => "Car radio", Target.Church => "Church radio", _ => "Radio" };
        _pick.Visible = target == Target.World;
        _search.Text = "";
        _search.PlaceholderText = target == Target.Car ? "Search CDs and stations   ( / )" : "Search CDs   ( / )";
        _volume.SetValueNoSignal(RadioSpeaker.UserVolume);
        _volumeValue.Text = Percent(RadioSpeaker.UserVolume);
        if (!_burning) ShowStatus("", UiTheme.TextDim, 0);
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Rebuild();   // hidden in the player, but Play with nothing on starts its first row
        UpdateNow();
        // always the player first: the list is one step deeper (#392)
        ShowLibrary(false);
        Callable.From(() => { Fit(); CursorAbove(); }).CallDeferred();   // its height is known once laid out
    }

    /// <summary>
    /// The freed cursor appears in the middle of the screen; on a short screen that can be on the
    /// player. Put it above, where a click means "back to the world", never "play the CD under it".
    /// </summary>
    private void CursorAbove()
    {
        if (!IsOpen || _libraryShown || DisplayServer.GetName() == "headless") return;
        var rect = _panel.GetGlobalRect();
        var at = GetViewport().GetMousePosition();
        if (rect.Grow(8).HasPoint(at)) Input.WarpMouse(new Vector2(rect.GetCenter().X, Mathf.Max(8, rect.Position.Y - 60)));
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        _radio = null;
        _heldSlot = -1;
        _churchPlan = "";
        _link.ReleaseFocus();
        _search.ReleaseFocus();
        RadioSpeaker.SaveVolume();
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    // ---- what the radio plays ---------------------------------------------------------------

    /// <summary>The world radio being looked at, or null once it is gone.</summary>
    private RadioBody? Live() => _radio != null && IsInstanceValid(_radio) && _radio.IsInsideTree() ? _radio : null;

    /// <summary>The held radio is still in the hand.</summary>
    private bool HeldLive() => _heldSlot >= 0 && _inventory.Selected == _heldSlot && _inventory[_heldSlot].Id == ItemId.Radio;

    /// <summary>The player whose car stereo the panel is on: the driver (maybe this player), or null once out.</summary>
    private FootPlayer? Stereo() => _local() is { } me && IsInstanceValid(me) ? me.StereoOwner : null;

    /// <summary>This player may change what plays: always, except a passenger in someone else's car.</summary>
    private bool MayChange() => _target != Target.Car || Stereo() is { } owner && owner == _local();

    /// <summary>What plays now: a CD (and since when) or a live station; both 0 when silent.</summary>
    private (int Cd, int Station, double StartedAt, float Length, RadioMode Mode) Now()
    {
        double now = ClockSync.ServerNow;
        switch (_target)
        {
            case Target.World when Live() is { } radio:
                var mode = RadioQueue.Clamp(radio.Mode);
                return radio.NowPlaying is { } w ? (w.CdId, 0, w.StartedAt, w.Length, mode) : (0, 0, 0, 0, mode);
            case Target.Held when HeldLive() && RadioPlay.Decode(_inventory[_heldSlot].Data) is { } h && h.Sounding(now):
                return (h.CdId, 0, h.StartedAt, h.Length, h.Mode);
            case Target.Car when Stereo() is { } owner:
                if (owner.PlayingCarCd is { } c && c.Sounding(now)) return (c.CdId, 0, c.StartedAt, c.Length, c.Mode);
                if (owner.PlayingCarRadio is > 0 and var station) return (0, station, 0, 0, _pendingMode);
                break;
            case Target.Church when Interiors.ChurchRadios.Instance is { } church:
                var cm = church.ModeOf(_churchPlan);
                return church.PlayOf(_churchPlan) is { } c2 ? (c2.CdId, 0, c2.StartedAt, c2.Length, cm) : (0, 0, 0, 0, cm);
        }
        return (0, 0, 0, 0, _target == Target.World ? RadioMode.Once : _pendingMode);
    }

    // ---- actions ----------------------------------------------------------------------------

    private bool Refuse()
    {
        if (MayChange()) return false;
        ShowStatus("Only the driver changes the music.", UiTheme.Warn, 3);
        return true;
    }

    private void PlayCd(int id)
    {
        GD.Print($"[radio] the {_target.ToString().ToLowerInvariant()} radio's panel plays CD {id}");
        if (Refuse() || CdLibrary.Instance?.Find(id) is not { } cd) return;
        var mode = Now().Mode;
        switch (_target)
        {
            case Target.World:
                if (Live() is { } radio && RadioManager.Instance is { } manager) manager.Play(radio, id, cd.Duration);
                break;
            case Target.Held:
                if (HeldLive()) _inventory.SetData(_heldSlot, new RadioPlay(id, ClockSync.ServerNow, cd.Duration, mode).Encode());
                break;
            case Target.Car:
                if (Stereo() is not { } me) return;
                me.CarRadio = 0;
                me.CarCd = new RadioPlay(id, ClockSync.ServerNow, cd.Duration, mode).Encode();
                break;
            case Target.Church:
                Interiors.ChurchRadios.Instance?.Play(_churchPlan, id, cd.Duration);
                break;
        }
    }

    private void Tune(int station)
    {
        if (_target != Target.Car || Refuse() || Stereo() is not { } me) return;
        me.CarCd = "";
        me.CarRadio = station;
    }

    private void StopRadio()
    {
        if (Refuse()) return;
        switch (_target)
        {
            case Target.World:
                if (Live() is { } r) RadioManager.Instance?.Stop(r);
                break;
            case Target.Held:
                if (HeldLive()) _inventory.SetData(_heldSlot, RadioPlay.Decode(_inventory[_heldSlot].Data) is { } held ? RadioPlay.Off(held) : null);   // a tap puts it back on (#725)
                break;
            case Target.Car:
                if (Stereo() is { } me) { me.CarCd = ""; me.CarRadio = 0; }
                break;
            case Target.Church:
                Interiors.ChurchRadios.Instance?.Stop(_churchPlan);
                break;
        }
    }

    /// <summary>Play / Stop: stops what plays, or plays the row in focus (else the first one shown).</summary>
    private void PlayStop()
    {
        var now = Now();
        if (now.Cd != 0 || now.Station != 0) { StopRadio(); return; }
        // the church radio has the chess type beat loaded (#370)
        if (_target == Target.Church && CdLibrary.Instance is { RatBeatId: > 0 and var rat }) { PlayCd(rat); return; }
        // the radio stays on the song it last played (#732), not the first of the list
        if (LastCd() is int last && CdLibrary.Instance?.Find(last) != null) { PlayCd(last); return; }
        if (FocusedRow() is { } focused) { focused.EmitSignal(BaseButton.SignalName.Pressed); return; }
        PressFirstRow();
    }

    /// <summary>The CD this radio last played and still holds (switched off, or run out), or null.</summary>
    private int? LastCd()
    {
        int cd = _target switch
        {
            Target.World => Live()?.CdId ?? 0,
            Target.Held => HeldLive() ? RadioPlay.DecodeAny(_inventory[_heldSlot].Data)?.CdId ?? 0 : 0,
            _ => 0,
        };
        return cd != 0 ? cd : null;
    }

    /// <summary>The previous or next CD (or station, when one is on), round the list.</summary>
    private void Skip(int step)
    {
        var now = Now();
        if (now.Station > 0)
        {
            int station = Stations.Step(now.Station, step);
            if (station == 0) station = Stations.Step(station, step);
            Tune(station);
            return;
        }
        int next = RadioQueue.Step(now.Cd, step, RadioQueue.Order(CdLibrary.Instance, withPersonal: true));
        if (next == 0) { ShowStatus("No CDs yet: paste a link below to burn one.", UiTheme.TextDim, 4); return; }
        PlayCd(next);
    }

    private void CycleMode()
    {
        if (Refuse()) return;
        var mode = RadioQueue.Cycle(Now().Mode);
        _pendingMode = mode;
        switch (_target)
        {
            case Target.World:
                if (Live() is { } radio) RadioManager.Instance?.SetMode(radio, mode);
                break;
            case Target.Held:
                if (HeldLive() && RadioPlay.Decode(_inventory[_heldSlot].Data) is { } held)
                    _inventory.SetData(_heldSlot, (held with { Mode = mode }).Encode());
                break;
            case Target.Car:
                if (Stereo() is { } me && RadioPlay.Decode(me.CarCd) is { } car) me.CarCd = (car with { Mode = mode }).Encode();
                break;
            case Target.Church:
                Interiors.ChurchRadios.Instance?.SetMode(_churchPlan, mode);
                break;
        }
        UpdateNow();
    }

    private void RemoveCd(int id)
    {
        if (id >= 0 || CdLibrary.Instance is not { } library) return;
        if (Now().Cd == id && MayChange()) StopRadio();
        library.RemovePersonal(id);
    }

    private void PickUp()
    {
        if (Live() is not { } radio || RadioManager.Instance is not { } manager) return;
        // what it plays carries on in the hand: the stack keeps the CD, its start and its mode (#168)
        string? playing = radio.CarriedData;
        manager.PickUp(radio, () =>
        {
            if (Give != null) Give(new ItemStack(ItemId.Radio, 1, playing));
            else _inventory.Add(new ItemStack(ItemId.Radio, 1, playing));
            Close();
        });
    }

    private FileDialog? _picker;

    /// <summary>The system's file picker on audio files; the chosen one is burnt (#736).</summary>
    private void PickFile()
    {
        if (_picker == null)
        {
            _picker = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true, Title = "A song to burn",
                Filters = new[] { string.Join(", ", System.Array.ConvertAll(CdUpload.Extensions, e => "*" + e)) + " ; Audio" },
            };
            _picker.FileSelected += path =>
            {
                if (CdLibrary.Instance is not { } library) return;
                _burning = true;
                ShowStatus($"Burning {System.IO.Path.GetFileName(path)}…", UiTheme.TextDim, 0, progress: -1);
                library.BurnFile(path, _mine.ButtonPressed);
            };
            AddChild(_picker);
        }
        _picker.PopupCentered(new Vector2I(760, 520));
    }

    private void Burn()
    {
        string text = _link.Text.Trim();
        if (text.Length == 0 || CdLibrary.Instance is not { } library) return;
        _link.Text = "";
        _burning = true;
        ShowStatus("Sending the link…", UiTheme.TextDim, 0, progress: -1);
        library.RequestBurn(text, _mine.ButtonPressed);
    }

    /// <summary>A burn reports a line per stage; the bar shows which of the three it is at.</summary>
    private void OnBurnStatus(string line)
    {
        // deferred: it may come from a worker
        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            // the steps of a burn, an upload's first (#736): how far along the bar is at each
            float at = line.StartsWith("Uploading") ? 0.05f + 0.3f * UploadShare(line)
                : line.StartsWith("Scanning") ? 0.4f
                : line.StartsWith("Downloading") ? 0.2f
                : line.StartsWith("Analysing") ? 0.6f
                : line.StartsWith("Encoding") ? 0.85f : -1f;
            if (at >= 0f)
            {
                _burning = true;
                ShowStatus($"Burning: {line}", UiTheme.Text, 0, progress: at);
            }
            else if (line.StartsWith("Burnt"))
            {
                _burning = false;
                ShowStatus(line, UiTheme.Good, 8, progress: 1f);
            }
            else
            {
                _burning = false;
                ShowStatus(line, UiTheme.Warn, 10);
            }
        }).CallDeferred();
    }

    /// <summary>"Uploading… 45 %" as 0.45; 0 before the first percentage.</summary>
    private static float UploadShare(string line)
    {
        int pct = line.IndexOf('%');
        if (pct < 0) return 0f;
        int start = line.LastIndexOf(' ', Math.Max(0, pct - 2)) + 1;
        return int.TryParse(line.AsSpan(start, pct - start).Trim(), out int n) ? Mathf.Clamp(n / 100f, 0f, 1f) : 0f;
    }

    /// <summary>A line under the burn box; <paramref name="seconds"/> 0 keeps it, <paramref name="progress"/> &lt; 0 animates the bar, NaN hides it.</summary>
    private void ShowStatus(string text, Color color, double seconds, float progress = float.NaN)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color);
        _statusUntil = seconds > 0 ? Time.GetTicksMsec() / 1000.0 + seconds : 0;
        _burnBar.Visible = !float.IsNaN(progress);
        _burnBar.Indeterminate = progress < 0;
        if (progress >= 0) _burnBar.Value = progress;
    }

    // ---- the list -----------------------------------------------------------------------------

    private void OnLibraryChanged()
    {
        if (IsOpen) Rebuild();
    }

    private void Rebuild()
    {
        if (!IsOpen) return;
        var keep = FocusedKey();
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        _cdRows.Clear();
        _stationRows.Clear();
        string q = _search.Text.Trim();
        bool locked = !MayChange();
        int shown = 0, total = 0;

        if (_target == Target.Car)
        {
            var stations = Stations.All.Where(s => Matches(q, s.Name, "live", "station", "radio")).ToList();
            total += Stations.All.Length;
            if (stations.Count > 0) _rows.AddChild(Heading("Live stations", stations.Count, "the same moment for everyone"));
            foreach (var s in stations)
            {
                int id = s.Id;
                _stationRows[id] = Row(s.Name, "LIVE", locked, () => Tune(id), null);
                shown++;
            }
        }

        if (CdLibrary.Instance is { } library)
        {
            var shared = library.All.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            var mine = library.Personal.Select(kv => kv.Value).OrderBy(cd => cd.Title, StringComparer.OrdinalIgnoreCase).ToList();
            total += shared.Count + mine.Count;
            var sharedShown = shared.Where(cd => Matches(q, cd.Title, cd.Style.ToString())).ToList();
            var mineShown = mine.Where(cd => Matches(q, cd.Title, cd.Style.ToString())).ToList();
            if (sharedShown.Count > 0 || q.Length == 0)
                _rows.AddChild(Heading("Shared CDs", sharedShown.Count, "burnt on the server, everyone near hears them"));
            if (shared.Count == 0 && q.Length == 0)
                _rows.AddChild(UiKit.Margin(UiKit.Text("No CDs yet: paste a link below to burn one.", UiTheme.FontSmall, UiTheme.TextFaint), 10, 2, 0, 6));
            foreach (var cd in sharedShown) { AddCd(cd, locked, removable: false); shown++; }
            if (mineShown.Count > 0)
            {
                _rows.AddChild(Heading("My CDs", mineShown.Count, "on this computer: only you hear them"));
                foreach (var cd in mineShown) { AddCd(cd, locked, removable: true); shown++; }
            }
        }
        if (shown == 0 && q.Length > 0)
            _rows.AddChild(UiKit.Margin(UiKit.Text($"Nothing matches \"{q}\".", UiTheme.FontSmall, UiTheme.TextFaint), 10, 8, 0, 8));
        _count.Text = q.Length > 0 ? $"{shown} of {total}" : $"{total}";
        _shownCd = _shownStation = int.MinValue;
        _shownLocked = locked;
        Highlight();
        if (keep is { } k && (k.Station ? _stationRows : _cdRows).TryGetValue(k.Id, out var again))
            Callable.From(() => { if (IsInstanceValid(again) && again.IsInsideTree()) again.GrabFocus(); }).CallDeferred();
    }

    private static bool Matches(string query, params string[] fields)
    {
        if (query.Length == 0) return true;
        foreach (string word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!fields.Any(f => f.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private void AddCd(CdInfo cd, bool locked, bool removable)
    {
        int id = cd.Id;
        string meta = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cd.Bpm:F0} bpm · {cd.Style} · {Clock(cd.Duration)}");
        _cdRows[id] = Row(cd.Title, meta, locked, () => PlayCd(id), removable ? () => RemoveCd(id) : null);
    }

    private static Control Heading(string title, int count, string hint)
    {
        var line = UiKit.HBox(10);
        line.AddChild(UiKit.Section($"{title} · {count}"));
        var faint = UiKit.Text(hint, UiTheme.FontTiny, UiTheme.TextFaint);
        faint.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        line.AddChild(faint);
        return UiKit.Margin(line, 4, 10, 0, 2);
    }

    private static readonly StyleBoxFlat RowNormal = UiTheme.Flat(new Color(1, 1, 1, 0), 6, 10, 4);
    private static readonly StyleBoxFlat RowHover = UiTheme.Flat(new Color(1, 1, 1, 0.06f), 6, 10, 4);
    private static readonly StyleBoxFlat RowPressed = UiTheme.Flat(new Color(UiTheme.Amber, 0.24f), 6, 10, 4);
    private static readonly StyleBoxFlat RowFocus = UiTheme.Flat(new Color(UiTheme.Amber, 0.16f), 6, 10, 4, UiTheme.AmberDim, 1);

    /// <summary>One entry: the title as a button (Enter / A plays it), its details on the right, an optional bin.</summary>
    private Button Row(string title, string meta, bool locked, Action press, Action? remove)
    {
        var line = UiKit.HBox(6);
        var b = new Button
        {
            Text = title,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            FocusMode = Control.FocusModeEnum.All,
            CustomMinimumSize = new Vector2(0, 32),
            TooltipText = locked ? "Only the driver changes the music" : "Play",
        };
        b.AddThemeStyleboxOverride("normal", RowNormal);
        b.AddThemeStyleboxOverride("hover", RowHover);
        b.AddThemeStyleboxOverride("pressed", RowPressed);
        b.AddThemeStyleboxOverride("hover_pressed", RowPressed);
        b.AddThemeStyleboxOverride("focus", RowFocus);
        b.AddThemeStyleboxOverride("disabled", RowNormal);
        b.AddThemeColorOverride("icon_normal_color", UiTheme.Amber);
        b.AddThemeColorOverride("icon_focus_color", UiTheme.Amber);
        b.AddThemeColorOverride("icon_hover_color", UiTheme.Amber);
        b.AddThemeColorOverride("icon_pressed_color", UiTheme.Amber);
        b.Pressed += press;
        line.AddChild(b);
        var details = UiKit.Text(meta, UiTheme.FontSmall, UiTheme.TextFaint, align: HorizontalAlignment.Right);
        details.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        line.AddChild(details);
        if (remove != null)
        {
            var bin = UiKit.IconButton(Icons.Trash, "Delete this CD of yours", 30);
            bin.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            bin.Pressed += remove;
            line.AddChild(bin);
        }
        else line.AddChild(UiKit.Spacer(w: 30));
        _rows.AddChild(line);
        return b;
    }

    /// <summary>The playing row in amber with a play mark, the others plain; a passenger's rows dimmed.</summary>
    private void Highlight()
    {
        var now = Now();
        bool locked = !MayChange();
        if (locked != _shownLocked) { Rebuild(); return; }
        if (now.Cd == _shownCd && now.Station == _shownStation) return;
        _shownCd = now.Cd;
        _shownStation = now.Station;
        foreach (var (id, b) in _cdRows) Mark(b, id == now.Cd, locked);
        foreach (var (id, b) in _stationRows) Mark(b, id == now.Station, locked);

        static void Mark(Button b, bool on, bool locked)
        {
            var rest = locked ? UiTheme.TextDim : UiTheme.Text;
            b.AddThemeColorOverride("font_color", on ? UiTheme.Amber : rest);
            foreach (string c in new[] { "font_focus_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
                b.AddThemeColorOverride(c, on ? UiTheme.Amber : Colors.White);
            b.Icon = on ? Icons.Play : null;
        }
    }

    private Button? FocusedRow() => _panel.GetViewport().GuiGetFocusOwner() is Button b && (_cdRows.ContainsValue(b) || _stationRows.ContainsValue(b)) ? b : null;

    private (bool Station, int Id)? FocusedKey()
    {
        if (FocusedRow() is not { } b) return null;
        foreach (var (id, row) in _cdRows) if (row == b) return (false, id);
        foreach (var (id, row) in _stationRows) if (row == b) return (true, id);
        return null;
    }

    private Button? FirstRow() =>
        _rows.GetChildren().OfType<HBoxContainer>().Select(h => h.GetChildOrNull<Button>(0)).FirstOrDefault(b => b != null);

    private void PressFirstRow() => FirstRow()?.EmitSignal(BaseButton.SignalName.Pressed);

    /// <summary>Keyboard and pad start on the row that plays, else the first one.</summary>
    private void FocusCurrent()
    {
        var now = Now();
        Button? target = now.Station > 0 ? _stationRows.GetValueOrDefault(now.Station) : _cdRows.GetValueOrDefault(now.Cd);
        target ??= FirstRow();
        if (target == null) { _search.CallDeferred(Control.MethodName.GrabFocus); return; }
        // deferred, and only if the row is still there: a search typed meanwhile rebuilds the list
        Callable.From(() =>
        {
            if (!IsInstanceValid(target) || !target.IsInsideTree()) return;
            target.GrabFocus();
            _scroll.EnsureControlVisible(target);
        }).CallDeferred();
    }

    // ---- every frame ------------------------------------------------------------------------

    private void UpdateNow()
    {
        var now = Now();
        var library = CdLibrary.Instance;
        bool locked = !MayChange();
        if (now.Station > 0)
        {
            _nowTitle.Text = Stations.Name(now.Station);
            _nowMeta.Text = "Live station · relayed by the server";
            _bar.Value = 1;
            _time.Text = "LIVE";
        }
        else if (now.Cd != 0)
        {
            var cd = library?.Find(now.Cd);
            _nowTitle.Text = cd?.Title ?? "Someone's own CD";
            var order = RadioQueue.Order(library, withPersonal: true);
            int index = order.IndexOf(now.Cd);
            _nowMeta.Text = (cd != null ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cd.Bpm:F0} bpm · {cd.Style}") : "only its owner has it: you hear nothing")
                + (index >= 0 ? $" · CD {index + 1} of {order.Count}" : "")
                + (now.Cd < 0 ? " · yours, only you hear it" : "");
            double at = Math.Clamp(ClockSync.ServerNow - now.StartedAt, 0, now.Length);
            _bar.Value = now.Length > 0 ? at / now.Length : 0;
            _time.Text = $"{Clock(at)} / {Clock(now.Length)}";
        }
        else
        {
            _nowTitle.Text = "Nothing playing";
            _nowMeta.Text = locked ? "The driver picks the music."
                : _target == Target.Church && CdLibrary.Instance is { RatBeatId: > 0 } ? "Chess Type Beat is loaded: press Play."
                : _libraryShown ? (_target == Target.Car ? "Pick a station or a CD below." : "Pick a CD below.")
                : "Press Play, or pick a CD in the Library.";
            _bar.Value = 0;
            _time.Text = "";
        }
        _subtitle.Text = _target switch
        {
            Target.World => "On the ground · everyone near hears it",
            Target.Held => "In your hand · it plays as you carry it",
            Target.Church => "By the pastor rat · everyone in the church hears it",
            _ => locked ? "Riding along · only the driver changes the music" : "At the wheel · everyone near the car hears it",
        };
        bool playing = now.Cd != 0 || now.Station != 0;
        _cassette.Playing = playing;
        _cassette.Progress = (float)_bar.Value;
        _cassette.Kick = now.Cd != 0 ? RadioGroove.Of(now.Cd, now.StartedAt, ClockSync.ServerNow).Kick : 0f;
        _playStop.Text = playing ? "■  Stop" : "▶  Play";
        _mode.Text = RadioQueue.Label(now.Mode);
        _mode.Disabled = locked;
        _prev.Disabled = _next.Disabled = _playStop.Disabled = locked;
        string closeKey = _target switch
        {
            Target.Car => ", " + KeyName(PlayerInput.RadioPanel),
            Target.Held => "",   // its Use is a click: the click outside
            _ => ", " + KeyName(PlayerInput.InteractMount),
        };
        // pad Y switches the view (_UnhandledInput reads the button itself)
        string y = InputHints.Button(JoyButton.Y);
        _footer.Text = InputHints.Vr
            ? (_libraryShown ? $"Point and pull to play · {y} player · " : $"Point and pull to press · {y} library · ")
              + InputHints.Format("{ui_cancel} close")
            : InputHints.Pad
            ? InputHints.Format(_libraryShown ? "{ui_up} {ui_down} choose · {ui_accept} play · " : "{ui_up} {ui_down} choose · {ui_accept} press · ")
              + (_libraryShown ? $"{y} player · " : $"{y} library · ") + InputHints.Format("{ui_cancel} close")
            : (_libraryShown ? InputHints.Format("Up / Down choose · Enter play · / search · {menu} close")
                : InputHints.Format("/ library · {menu}") + $"{closeKey} or a click outside close");
        Highlight();
    }

    public override void _Process(double delta)
    {
        Changer();
        if (!IsOpen) return;
        bool gone = _target switch
        {
            Target.Held => !HeldLive(),
            Target.Car => Stereo() == null,
            Target.Church => _local() is not { } me || !IsInstanceValid(me) || !me.Indoors
                || Interiors.InteriorManager.Instance?.Current?.Key != _churchPlan,
            _ => Live() is not { } radio || _local() is { } player && IsInstanceValid(player)
                && player.GlobalPosition.DistanceTo(radio.GlobalPosition) > WalkAway,
        };
        if (gone) { Close(); return; }
        if (_statusUntil > 0 && Time.GetTicksMsec() / 1000.0 > _statusUntil)
        {
            _statusUntil = 0;
            ShowStatus("", UiTheme.TextDim, 0);
        }
        _sinceNow += delta;
        if (_sinceNow >= 0.1)
        {
            _sinceNow = 0;
            UpdateNow();
        }
    }

    /// <summary>
    /// The CD changer of the radios this player owns, open or not: the one in the hand and the
    /// stereo of the car it drives. A CD that ran out is followed by what its mode says; a car's
    /// CD played once is taken out, so a parked car does not carry a finished one.
    /// </summary>
    private void Changer()
    {
        double now = ClockSync.ServerNow;
        // the carried radio, in the hand or on the back (#261): it plays on either way
        int slot = _inventory.RadioSlot();
        if (slot >= 0 && RadioPlay.Decode(_inventory[slot].Data) is { Mode: not RadioMode.Once } held && !held.Sounding(now))
        {
            var next = RadioQueue.Continue(held, now, CdLibrary.Instance, _random);
            _inventory.SetData(slot, next?.Encode());
        }
        if (_local() is { RidingWith: 0 } me && IsInstanceValid(me) && NetLink.Ready(me) && me.IsMultiplayerAuthority()
            && me.PlayingCarCd is { } car && !car.Sounding(now))
        {
            var next = car.Mode == RadioMode.Once ? null : RadioQueue.Continue(car, now, CdLibrary.Instance, _random);
            me.CarCd = next?.Encode() ?? "";
            if (next is { } n) GD.Print($"[radio] car stereo goes on with CD {n.CdId} ({n.Mode})");
        }
    }

    /// <summary>The key of an action as printed on it: "." rather than "Period".</summary>
    private static string KeyName(string action) => InputHints.Label(action) switch
    {
        "Period" => ".",
        "Comma" => ",",
        var label => label,
    };

    private static string Clock(double seconds)
    {
        int s = (int)Math.Max(0, seconds);
        return $"{s / 60}:{s % 60:D2}";
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return;
        if (!IsOpen)
        {
            if (e.IsActionPressed(PlayerInput.RadioPanel) && !UiFocus.TextEntryActive && _local()?.StereoOwner != null)
            {
                OpenCar();
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        // a text box has the keys: E is a letter in the link, Esc there still closes
        bool typing = GetViewport().GuiGetFocusOwner() is LineEdit;
        // pad Y flips between player and library; on the pad it is also interact, so it comes first
        if (e is InputEventJoypadButton { ButtonIndex: JoyButton.Y })
        {
            ShowLibrary(!_libraryShown);
            GetViewport().SetInputAsHandled();
            return;
        }
        // the key that opened it closes it (#392): R the car, Use the one in the hand, E one in the world or church
        bool openingKey = _target switch
        {
            Target.Car => e.IsActionPressed(PlayerInput.RadioPanel),
            Target.Held => e is not InputEventMouseButton && e.IsActionPressed(PlayerInput.UseItem),
            _ => e is InputEventKey && e.IsActionPressed(PlayerInput.InteractMount),
        };
        // a click outside the panel: back to the world (the GUI took every click on the panel)
        bool outside = e is InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } click
                       && !_panel.GetGlobalRect().HasPoint(click.Position);
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu) || outside || openingKey && !typing)
        {
            Close();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventKey { Keycode: Key.Slash } or InputEventKey { Keycode: Key.F, CtrlPressed: true })
        {
            if (!_libraryShown) ShowLibrary(true);
            _search.GrabFocus();
            _search.SelectAll();
            GetViewport().SetInputAsHandled();
        }
    }
}
