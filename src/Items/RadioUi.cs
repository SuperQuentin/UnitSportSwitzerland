using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Audio.Live;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Items;

/// <summary>
/// The panel of a radio, the music picker (#211): what plays now (title, elapsed / length bar,
/// previous, play/stop, next, and what happens when it ends), a search box over the CDs (the
/// server's shared ones, then this player's own) and, in a car, the live stations; the volume;
/// a box to burn a new CD from a link for everyone or for yourself only, with its progress; and
/// for a radio lying in the world, pick it back up. In the menus' look (<see cref="UiTheme"/>),
/// sized to the screen, driven by mouse, keyboard (arrows, Enter, / to search) or pad (D-pad, A, B).
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
        var close = UiKit.IconButton(Icons.Close, "Close (Esc)");
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        close.Pressed += Close;
        header.AddChild(close);

        box.AddChild(NowPlayingCard());

        // search over the list
        var search = UiKit.HBox(10);
        box.AddChild(search);
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
        box.AddChild(_scroll);

        box.AddChild(UiKit.Line());

        // volume: one for every radio this player hears
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

        // burning a CD from a link
        var burn = UiKit.HBox(10);
        box.AddChild(burn);
        _link = new LineEdit
        {
            PlaceholderText = "Paste a YouTube link to burn a CD",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MaxLength = 300,
        };
        _link.TextSubmitted += _ => Burn();
        burn.AddChild(_link);
        _mine = new CheckBox { Text = "Just for me", FocusMode = Control.FocusModeEnum.All, TooltipText = "Burn it on this computer, into your own list: nobody else hears it" };
        burn.AddChild(_mine);
        var burnButton = UiKit.Button("Burn");
        burnButton.Pressed += Burn;
        burn.AddChild(burnButton);

        var status = UiKit.HBox(10);
        box.AddChild(status);
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

    private PanelContainer NowPlayingCard()
    {
        var now = UiKit.VBox(6);
        now.AddChild(UiKit.Section("Now playing"));
        _nowTitle = UiKit.Text("", UiTheme.FontBody + 2, UiTheme.Text, bold: true);
        _nowTitle.ClipText = true;
        _nowTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        now.AddChild(_nowTitle);
        _nowMeta = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _nowMeta.ClipText = true;
        _nowMeta.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        now.AddChild(_nowMeta);

        var progress = UiKit.HBox(10);
        now.AddChild(progress);
        _bar = Bar(6);
        _bar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _bar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        progress.AddChild(_bar);
        _time = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, align: HorizontalAlignment.Right);
        _time.CustomMinimumSize = new Vector2(86, 0);
        progress.AddChild(_time);

        var controls = UiKit.HBox(8);
        now.AddChild(controls);
        _prev = UiKit.Button("◀◀", minWidth: 52);
        _prev.TooltipText = "Previous";
        _prev.Pressed += () => Skip(-1);
        controls.AddChild(_prev);
        _playStop = UiKit.Button("▶  Play", primary: true, minWidth: 110);
        _playStop.Pressed += PlayStop;
        controls.AddChild(_playStop);
        _next = UiKit.Button("▶▶", minWidth: 52);
        _next.TooltipText = "Next";
        _next.Pressed += () => Skip(1);
        controls.AddChild(_next);
        controls.AddChild(UiKit.Spacer(expand: true));
        _mode = UiKit.Button("", minWidth: 150);
        _mode.TooltipText = "What happens when the CD ends";
        _mode.Pressed += CycleMode;
        controls.AddChild(_mode);
        return UiKit.Card(now, 0.6f, 14);
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

    /// <summary>Centred, never wider or taller than the screen less a gutter.</summary>
    private void Fit()
    {
        if (_panel == null) return;
        var screen = GetViewport().GetVisibleRect().Size;
        float w = Mathf.Min(MaxWidth, screen.X - 2 * Gutter), h = Mathf.Min(MaxHeight, screen.Y - 2 * Gutter);
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.OffsetLeft = -w / 2;
        _panel.OffsetRight = w / 2;
        _panel.OffsetTop = -h / 2;
        _panel.OffsetBottom = h / 2;
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
        Fit();
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Rebuild();
        UpdateNow();
        FocusCurrent();
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
                if (HeldLive()) _inventory.SetData(_heldSlot, null);
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
        if (FocusedRow() is { } focused) { focused.EmitSignal(BaseButton.SignalName.Pressed); return; }
        PressFirstRow();
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
        string? playing = radio.NowPlaying is { } p ? (p with { Mode = RadioQueue.Clamp(radio.Mode) }).Encode() : null;
        manager.PickUp(radio, () =>
        {
            if (Give != null) Give(new ItemStack(ItemId.Radio, 1, playing));
            else _inventory.Add(new ItemStack(ItemId.Radio, 1, playing));
            Close();
        });
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
            int stage = line.StartsWith("Downloading") ? 1 : line.StartsWith("Analysing") ? 2 : line.StartsWith("Encoding") ? 3 : 0;
            if (stage > 0)
            {
                _burning = true;
                ShowStatus($"Burning, step {stage} of 3: {line}", UiTheme.Text, 0, progress: (stage - 0.5f) / 3f);
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
                : _target == Target.Car ? "Pick a station or a CD below."
                : _target == Target.Church && CdLibrary.Instance is { RatBeatId: > 0 } ? "Chess Type Beat is loaded: press Play."
                : "Pick a CD below.";
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
        _playStop.Text = playing ? "■  Stop" : "▶  Play";
        _mode.Text = RadioQueue.Label(now.Mode);
        _mode.Disabled = locked;
        _prev.Disabled = _next.Disabled = _playStop.Disabled = locked;
        _footer.Text = PlayerInput.LastDevice == InputDevice.Gamepad
            ? "D-pad choose · A play · B close"
            : $"Up / Down choose · Enter play · / search · Esc{(_target == Target.Car ? " or " + KeyName(PlayerInput.RadioPanel) : "")} close";
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
        // not E: it is a letter in the link box
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)
            || _target == Target.Car && e.IsActionPressed(PlayerInput.RadioPanel))
        {
            Close();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventKey { Keycode: Key.Slash } or InputEventKey { Keycode: Key.F, CtrlPressed: true })
        {
            _search.GrabFocus();
            _search.SelectAll();
            GetViewport().SetInputAsHandled();
        }
    }
}
