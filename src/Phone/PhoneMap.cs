using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Phone;

/// <summary>
/// The phone's Map app as a real map (#1826): a large see-through map over the 3D view, about three quarters of it, the world
/// visible behind. It is the adventure map's own canvas (<see cref="AdventureMap.MapCanvasView"/>: relief, canton borders,
/// places, the player's arrow, the shared <see cref="IMapOverlay"/> layers) embedded here, not a second map. Standing still its
/// fills (background, relief, fog) are <see cref="RestAlpha"/> opaque; while the player moves they ease to
/// <see cref="MovingAlpha"/> and come back when the player stops; borders, labels and markers stay crisp. A search drawer (the
/// old place list: Recent, Near you, the chips, a query) opens over its left side; a pick centres on the place and shows its card
/// with Travel, which goes behind <see cref="Teleporter.Curtain"/> through the controller. With the adventure off it has no fog
/// and draws the place search's places. The controller drives it (<see cref="Show"/>, <see cref="PanBy"/>, <see cref="Zoom"/>,
/// <see cref="Accept"/>...); the mouse reaches the canvas directly (a screen-space layer over the 3D view). Docs:
/// <c>docs/notes/ui/phone.md</c>.
/// </summary>
public sealed partial class PhoneMap : Node, IMapHost
{
    /// <summary>The fills' opacity standing still, and while the player moves (the owner's numbers, #1826).</summary>
    public const float RestAlpha = 0.6f, MovingAlpha = 0.2f;
    /// <summary>The fade to <see cref="MovingAlpha"/> when the player starts to move, and back to rest when they stop.</summary>
    public const float ToMovingSeconds = 0.2f, ToRestSeconds = 0.35f;
    /// <summary>Metres a second above which the player moves, below which they stand (with a short delay so a step is not a flicker).</summary>
    private const float MovingSpeed = 0.5f, StandingSpeed = 0.2f, StandingDelay = 0.25f;
    /// <summary>The share of the view the map covers: left, top, right, bottom (anchors); the phone stays in the lower right corner.</summary>
    private static readonly Vector4 Area = new(0.02f, 0.035f, 0.775f, 0.765f);
    private const int PlaceZoom = 32, BarHeight = 36, StripHeight = 30;
    private const float GlideSeconds = 0.3f, MarkerRefresh = 0.2f, PeerScan = 1f;

    private readonly Func<FootPlayer?> _player;
    private readonly Func<PlaceSearchUi?> _places;
    private readonly Func<ChatManager?> _chat;

    private CanvasLayer _layer = null!;
    private Control _root = null!, _panel = null!;
    private AdventureMap.MapCanvasView _canvas = null!;
    private PanelContainer _drawer = null!, _card = null!;
    private LineEdit _query = null!;
    private VBoxContainer _rows = null!;
    private ScrollContainer _scroll = null!;
    private Label _hint = null!, _cardTitle = null!, _cardDetail = null!, _drawerNote = null!;
    private Button _travel = null!;
    private readonly List<(Button Chip, PlaceKind? Kind, string Name)> _chips = new();
    private readonly List<Button> _rowButtons = new();
    private readonly List<PhoneScreens.MapRow> _data = new();
    private PlaceKind? _kind;

    private bool _shown, _drawerOpen, _pad, _moving;
    private float _still, _sinceMarkers, _sincePeers, _lastYaw;
    private Vector3 _lastAt;
    private string _hintShown = "", _cardDistance = "";
    private double _cardDistanceAt;
    private Tween? _fade, _glide;
    private Poi? _poi;
    private Place? _place;
    private readonly List<FootPlayer> _peerBodies = new();
    private readonly List<string> _peerNames = new();

    /// <summary>The Travel button was pressed on a card: where, and the name for the curtain.</summary>
    public event Action<GlobalPos, string>? TravelAsked;
    /// <summary>The back chevron on the map's bar was clicked.</summary>
    public event Action? BackPressed;
    /// <summary>What is selected changed: the line the phone's glass says (empty: nothing).</summary>
    public event Action<string>? NoteChanged;

    public PhoneMap(Func<FootPlayer?> player, Func<PlaceSearchUi?> places, Func<ChatManager?> chat)
    {
        _player = player;
        _places = places;
        _chat = chat;
        Name = "PhoneMap";
    }

    public PhoneMap() : this(() => null, () => null, () => null) { }

    /// <summary>The map is up (opening or open).</summary>
    public bool IsUp => _shown;

    /// <summary>Checks: places to show and list instead of the index's (a check's world has none).</summary>
    internal PlaceIndex? TestIndex { get; set; }

    /// <summary>The places nearest <paramref name="at"/>, closest first (checks).</summary>
    internal List<Place> NearestPlaces(GlobalPos at) =>
        CurrentIndex?.Places.OrderBy(p => at.HorizontalDistanceTo(PhoneScreens.GlobalOf(p))).Take(5).ToList() ?? new List<Place>();

    private PlaceIndex? CurrentIndex => TestIndex ?? _places()?.Index;
    /// <summary>The fills' opacity now (the tween's current value), for the checks.</summary>
    public float FillAlpha => _canvas.FillAlpha;
    /// <summary>The player is moving as far as the fade is concerned.</summary>
    public bool Moving => _moving;
    /// <summary>The embedded canvas (checks and the phone's pan).</summary>
    public AdventureMap.MapCanvasView Canvas => _canvas;
    /// <summary>The map's panel in canvas units (checks).</summary>
    public Rect2 PanelRect => _panel.GetGlobalRect();
    public bool DrawerOpen => _drawerOpen;
    public bool CardShown => _card.Visible;
    public string SearchText => _query.Text;
    public int RowCount => _rowButtons.Count;
    public bool Typing => _shown && _query.HasFocus();
    public string CardTitle => _cardTitle.Text;

    // ---------------------------------------------------------------------------------------- IMapHost

    PlacesService? IMapHost.Places => AdventureLog.Adventuring ? PlacesService.Instance : null;
    IReadOnlyList<Place>? IMapHost.Index => CurrentIndex?.Places;
    public Poi? Selected => _poi;
    public Place? SelectedPlace => _place;

    public void Select(Poi poi, bool centre)
    {
        _poi = poi;
        _place = null;
        string title = PlaceCard.TitleOf(poi);
        ShowCard(title, PlaceCard.KindLine(poi), poi.Pos, () => Travel(poi.Pos, title));
        if (centre) GlideTo(poi.Pos);
        _canvas.Redraw();
    }

    public void Select(Place place, bool centre)
    {
        _place = place;
        _poi = null;
        var at = PhoneScreens.GlobalOf(place);
        ShowCard(place.Name, PhoneScreens.DetailOf(place), at, () =>
        {
            PhoneScreens.NoteTravel(place);
            Travel(at, place.Name);
        });
        if (centre) GlideTo(at);
        _canvas.Redraw();
    }

    // ---------------------------------------------------------------------------------------- building

    public override void _Ready()
    {
        _layer = new CanvasLayer { Name = "PhoneMapLayer", Layer = 6 };
        AddChild(_layer);
        _root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Get() };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(_root);

        _panel = new Control { Name = "Panel", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _panel.AnchorLeft = Area.X; _panel.AnchorTop = Area.Y; _panel.AnchorRight = Area.Z; _panel.AnchorBottom = Area.W;
        _root.AddChild(_panel);

        _canvas = new AdventureMap.MapCanvasView(this)
        {
            Name = "Canvas", Driven = true, MeOverride = Me,
            FocusMode = Control.FocusModeEnum.None,
        };
        _canvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        // between the bars, not under them: the title bar above and the keys' strip below are part of the layout
        _canvas.OffsetLeft = 2; _canvas.OffsetRight = -2; _canvas.OffsetTop = BarHeight; _canvas.OffsetBottom = -StripHeight;
        _canvas.Boosted = true;
        _panel.AddChild(_canvas);
        _canvas.AddExtra(new PeersOverlay(this));
        _canvas.FillAlpha = RestAlpha;

        // the thin glass border
        var border = new Panel { Name = "Border", MouseFilter = Control.MouseFilterEnum.Ignore };
        border.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var frame = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = new Color(1, 1, 1, 0.30f), AntiAliasing = true };
        frame.SetBorderWidthAll(1);
        frame.SetCornerRadiusAll(4);
        border.AddThemeStyleboxOverride("panel", frame);
        _panel.AddChild(border);

        BuildBar();
        BuildDrawer();
        BuildCard();

        var strip = new Panel { Name = "KeysStrip", MouseFilter = Control.MouseFilterEnum.Ignore };
        strip.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        strip.OffsetTop = -StripHeight; strip.OffsetLeft = 2; strip.OffsetRight = -2; strip.OffsetBottom = -2;
        strip.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.03f, 0.04f, 0.06f, 0.62f), 3, 0, 0));
        _panel.AddChild(strip);
        _hint = new Label { Name = "Keys", MouseFilter = Control.MouseFilterEnum.Ignore };
        _hint.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        _hint.AddThemeColorOverride("font_color", UiTheme.Text);
        _hint.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        _hint.AddThemeConstantOverride("outline_size", 4);
        _hint.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _hint.OffsetLeft = 12; _hint.OffsetTop = -26; _hint.OffsetBottom = -4;
        _panel.AddChild(_hint);
        var legend = new Legend { Name = "Legend", MouseFilter = Control.MouseFilterEnum.Ignore };
        legend.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        legend.OffsetLeft = -250; legend.OffsetTop = -StripHeight; legend.OffsetRight = -8; legend.OffsetBottom = -2;
        legend.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.AddChild(legend);
    }

    public override void _ExitTree()
    {
        _fade?.Kill();
        _glide?.Kill();
    }

    private void BuildBar()
    {
        var bar = new PanelContainer { Name = "Bar", MouseFilter = Control.MouseFilterEnum.Stop };
        bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        bar.OffsetBottom = BarHeight;
        bar.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.04f, 0.05f, 0.07f, 0.80f), 4, 8, 3));
        var row = UiKit.HBox(6);
        bar.AddChild(row);
        var back = Tool("‹", "Back (Esc)", () => BackPressed?.Invoke(), 30);
        back.FocusMode = Control.FocusModeEnum.None;
        row.AddChild(back);
        row.AddChild(UiKit.Text("Map", UiTheme.FontBody, UiTheme.Amber, bold: true));
        row.AddChild(UiKit.Spacer(expand: true));
        var search = Tool("Search  /", "Search places", () => ToggleDrawer(typing: true));
        var centre = Tool("Me  Home", "Centre on you", Recentre);
        var out_ = Tool("−", "Zoom out", () => Zoom(-1), 30);
        var in_ = Tool("+", "Zoom in", () => Zoom(+1), 30);
        foreach (var b in new[] { search, centre, out_, in_ }) { b.FocusMode = Control.FocusModeEnum.None; row.AddChild(b); }
        _panel.AddChild(bar);
    }

    private static Button Tool(string text, string tip, Action pressed, int width = 0)
    {
        var b = UiKit.Button(text, minWidth: width);
        b.CustomMinimumSize = new Vector2(width, 26);
        b.TooltipText = tip;
        b.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        b.ButtonDown += () => UiMotion.Press(b);
        b.Pressed += pressed;
        return b;
    }

    private void BuildDrawer()
    {
        _drawer = new PanelContainer { Name = "Drawer", Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _drawer.AnchorRight = 0.38f;
        _drawer.AnchorBottom = 1f;
        _drawer.OffsetTop = BarHeight + 2; _drawer.OffsetBottom = -2; _drawer.OffsetLeft = 2;
        _drawer.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.047f, 0.067f, 0.106f, 0.96f), 4, 8, 8, new Color(1, 1, 1, 0.11f), 1));
        var col = UiKit.VBox(6);
        _drawer.AddChild(col);
        _query = new LineEdit { PlaceholderText = "Search a place…", ClearButtonEnabled = true };
        _query.TextChanged += _ => RefillRows();
        _query.TextSubmitted += _ => { if (_rowButtons.Count > 0) _rowButtons[0].EmitSignal(BaseButton.SignalName.Pressed); };
        col.AddChild(_query);
        var chips = UiKit.HBox(4);
        foreach (var (name, kind) in new (string, PlaceKind?)[] { ("All", null), ("Towns", PlaceKind.Town), ("Summits", PlaceKind.Summit), ("Passes", PlaceKind.Pass) })
        {
            var chip = Tool(name, $"Show {name.ToLowerInvariant()}", () => { _kind = kind; RefillRows(); });
            chip.FocusMode = Control.FocusModeEnum.None;
            _chips.Add((chip, kind, name));
            chips.AddChild(chip);
        }
        col.AddChild(chips);
        _drawerNote = UiKit.Text("", UiTheme.FontSmall, UiTheme.Warn);
        _drawerNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(_drawerNote);
        _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rows = UiKit.VBox(3);
        _rows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scroll.AddChild(_rows);
        col.AddChild(_scroll);
        _panel.AddChild(_drawer);
    }

    private void BuildCard()
    {
        _card = new PanelContainer { Name = "Card", Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _card.AnchorLeft = 0.40f; _card.AnchorRight = 1f; _card.AnchorTop = 1f; _card.AnchorBottom = 1f;
        _card.OffsetLeft = 4; _card.OffsetRight = -10; _card.OffsetTop = -36; _card.OffsetBottom = -32;
        _card.GrowVertical = Control.GrowDirection.Begin;
        _card.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.055f, 0.075f, 0.118f, 0.96f), 8, 12, 8, new Color(1, 1, 1, 0.11f), 1));
        var row = UiKit.HBox(10);
        _card.AddChild(row);
        var text = UiKit.VBox(2);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _cardTitle = UiKit.Text("", UiTheme.FontHeading - 4, UiTheme.Text, bold: true);
        _cardDetail = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        text.AddChild(_cardTitle);
        text.AddChild(_cardDetail);
        row.AddChild(text);
        _travel = UiKit.Button("Travel", primary: true, minWidth: 96);
        _travel.ButtonDown += () => UiMotion.Press(_travel);
        _travel.Pressed += () => _travelAction?.Invoke();
        _travel.FocusMode = Control.FocusModeEnum.None;
        row.AddChild(_travel);
        var close = Tool("✕", "Close the card", () => ClearSelection(), 34);
        close.FocusMode = Control.FocusModeEnum.None;
        row.AddChild(close);
        _panel.AddChild(_card);
    }

    // ---------------------------------------------------------------------------------------- showing

    /// <summary>The big map comes up over the view: springs in, centred on the player, fills at rest.</summary>
    public void Show()
    {
        if (_shown) return;
        _shown = true;
        if (_player() is { } away) away.PhoneMapAway = true;
        UiMotion.Open(_panel);
        _panel.Visible = true;
        _pointer = true;
        SetFill(RestAlpha, instant: true);
        _moving = false;
        _still = 0;
        _lastYaw = float.NaN;
        UpdateHint();
        if (_player() is { } p) _canvas.CentreOn(p.Global, PlaceZoom);
        _canvas.Redraw();
        ScanPeers();
    }

    /// <summary>The big map goes away: shrinks and fades (0.1 s), the drawer and card closed for the next time.</summary>
    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        if (_player() is { } back) back.PhoneMapAway = false;
        _glide?.Kill();
        if (_query.HasFocus()) _query.ReleaseFocus();
        UiMotion.Close(_panel, () => { if (!_shown) _panel.Visible = false; });
        _drawerOpen = false;
        _drawer.Visible = false;
        ClearSelection(silent: true);
    }

    private bool _pointer = true;

    /// <summary>
    /// False while the right button is held and the mouse turns the view: the map and its parts do not see the pointer, so the
    /// captured motion reaches the body (and the cursor that comes back is the map's again).
    /// </summary>
    public bool PointerEnabled
    {
        get => _pointer;
        set
        {
            if (_pointer == value) return;
            _pointer = value;
            _panel.MouseBehaviorRecursive = value ? Control.MouseBehaviorRecursiveEnum.Enabled : Control.MouseBehaviorRecursiveEnum.Disabled;
        }
    }

    // ---------------------------------------------------------------------------------------- the player

    private (GlobalPos At, float Yaw)? Me()
    {
        if (_player() is not { } p) return null;
        // the world's own true-north heading when it has one (the adventure's Me), else the body's
        var yaw = AdventureMap.Me?.Invoke()?.Yaw ?? p.GlobalRotation.Y;
        return (p.Global, yaw);
    }

    /// <summary>Centres on the player at least at the place zoom.</summary>
    public void Recentre()
    {
        if (_player() is not { } p) return;
        GlideTo(p.Global, keepZoom: false);
    }

    public void Zoom(int steps) => _canvas.ZoomStep(steps);

    public void PanBy(Vector2 dir, float dt)
    {
        if (dir != Vector2.Zero) _glide?.Kill();
        _canvas.PanBy(dir, dt);
    }

    /// <summary>The map eases to a point (0.3 s), then steps the zoom in to the place zoom if it was further out.</summary>
    private void GlideTo(GlobalPos at, bool keepZoom = true)
    {
        _glide?.Kill();
        var view = _canvas.View;
        var (lat0, lon0) = view.Centre;
        int zoom0 = view.Zoom;
        view.FocusOn(at, keepZoom ? Math.Max(view.PxPerKm, PlaceZoom) : PlaceZoom);
        var (lat1, lon1) = view.Centre;
        int zoom1 = view.Zoom;
        view.Zoom = zoom0;
        view.SetCentre(lat0, lon0);
        if (UiMotion.Reduced || !IsInsideTree() || !_shown)
        {
            view.Zoom = zoom1;
            view.SetCentre(lat1, lon1);
            _canvas.Redraw();
            return;
        }
        _glide = CreateTween().SetIgnoreTimeScale(true);
        _glide.TweenMethod(Callable.From<float>(t =>
        {
            view.SetCentre(Mathf.Lerp(lat0, lat1, t), Mathf.Lerp(lon0, lon1, t));
            _canvas.Redraw();
        }), 0f, 1f, GlideSeconds).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        _glide.TweenCallback(Callable.From(() =>
        {
            view.Zoom = zoom1;
            view.SetCentre(lat1, lon1);
            _canvas.Redraw();
        }));
    }

    public override void _Process(double delta)
    {
        if (!_shown) return;
        float dt = (float)delta;
        if (_player() is not { } p) return;

        // standing or moving: the fills ease, never step (#1826)
        float speed = p.GroundSpeed;
        if (speed > MovingSpeed) { _still = 0; SetMoving(true); }
        else if (speed < StandingSpeed)
        {
            _still += dt;
            if (_moving && _still >= StandingDelay) SetMoving(false);
        }

        // the arrow and the markers follow the body: a redraw of the top layer when it moved or turned, at most five times a second
        _sinceMarkers += dt;
        _sincePeers += dt;
        if (_sincePeers >= PeerScan) ScanPeers();
        float yaw = p.GlobalRotation.Y;
        if (_sinceMarkers >= MarkerRefresh && (float.IsNaN(_lastYaw) || Mathf.Abs(Mathf.AngleDifference(yaw, _lastYaw)) > 0.02f || p.GlobalPosition.DistanceSquaredTo(_lastAt) > 1f || _peerBodies.Count > 0))
        {
            _sinceMarkers = 0;
            _lastYaw = yaw;
            _lastAt = p.GlobalPosition;
            _canvas.RedrawMarkers();
        }
        if (_pad != InputHints.Pad) UpdateHint();
        if (_card.Visible && RealClock.Now - _cardDistanceAt > 1.0) RefreshDistance();
    }

    private void SetMoving(bool moving)
    {
        if (_moving == moving) return;
        _moving = moving;
        SetFill(moving ? MovingAlpha : RestAlpha, instant: false);
    }

    private void SetFill(float alpha, bool instant)
    {
        _fade?.Kill();
        if (instant || !IsInsideTree())
        {
            _canvas.FillAlpha = alpha;
            return;
        }
        float from = _canvas.FillAlpha;
        float seconds = (alpha < from ? ToMovingSeconds : ToRestSeconds) * (UiMotion.Reduced ? 0.5f : 1f);
        _fade = CreateTween().SetIgnoreTimeScale(true);
        _fade.TweenMethod(Callable.From<float>(v => _canvas.FillAlpha = v), from, alpha, seconds).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    // ---------------------------------------------------------------------------------------- the card

    private void ShowCard(string title, string detail, GlobalPos at, Action travel)
    {
        _cardTitle.Text = title;
        _cardAt = at;
        _cardBase = detail;
        _cardDistance = "";
        _cardDistanceAt = 0;
        RefreshDistance();
        _travel.Disabled = !Permissions.CanTeleport;
        _travel.TooltipText = Permissions.CanTeleport ? "Go there behind the phone's screen" : "This server does not allow travelling";
        _travelAction = travel;
        bool wasShown = _card.Visible;
        _card.Visible = true;
        if (!wasShown) UiMotion.Open(_card);
        else UiMotion.Pop(_card, 1.04f);
        NoteChanged?.Invoke($"{title}\n{_cardDistance}");
    }

    private GlobalPos _cardAt;
    private string _cardBase = "";
    private Action? _travelAction;

    private void RefreshDistance()
    {
        _cardDistanceAt = RealClock.Now;
        if (_player() is not { } p) return;
        string d = PhoneScreens.Metres(p.Global.HorizontalDistanceTo(_cardAt));
        if (d == _cardDistance) return;
        _cardDistance = d;
        _cardDetail.Text = $"{_cardBase}  ·  {d} from you";
    }

    private void Travel(GlobalPos at, string name) => TravelAsked?.Invoke(at, name);

    /// <summary>Drops the selection and its card.</summary>
    private void ClearSelection(bool silent = false)
    {
        if (_poi == null && _place == null && !_card.Visible) return;
        _poi = null;
        _place = null;
        if (_card.Visible)
        {
            if (silent) _card.Visible = false;
            else UiMotion.Close(_card, () => { if (_poi == null && _place == null) _card.Visible = false; });
        }
        _canvas.Redraw();
        if (!silent) NoteChanged?.Invoke("");
    }

    // ---------------------------------------------------------------------------------------- the drawer

    public void ToggleDrawer(bool typing)
    {
        if (_drawerOpen) CloseDrawer();
        else OpenDrawer(typing);
    }

    /// <summary>The place list slides in over the map's left side; <paramref name="typing"/>: the search box has the keys, else the first row does (the pad).</summary>
    public void OpenDrawer(bool typing)
    {
        if (!_drawerOpen)
        {
            _drawerOpen = true;
            _query.Clear();
            UiMotion.Open(_drawer);
            _drawer.Visible = true;
            RefillRows();
        }
        if (typing) _query.GrabFocus();
        else if (_rowButtons.Count > 0) _rowButtons[0].GrabFocus();
    }

    public void CloseDrawer()
    {
        if (!_drawerOpen) return;
        _drawerOpen = false;
        if (_query.HasFocus()) _query.ReleaseFocus();
        UiMotion.Close(_drawer, () => { if (!_drawerOpen) _drawer.Visible = false; });
    }

    /// <summary>The search box has the keys ("/" on the keyboard).</summary>
    public void FocusSearch() => OpenDrawer(typing: true);

    /// <summary>One step back: closes the drawer, then the card. False when there is nothing of the map's to close (the phone goes home).</summary>
    public bool StepBack()
    {
        if (_drawerOpen) { CloseDrawer(); return true; }
        if (_card.Visible) { ClearSelection(); return true; }
        return false;
    }

    private void RefillRows()
    {
        foreach (var chip in _chips) chip.Chip.Text = _kind == chip.Kind ? $"● {chip.Name}" : chip.Name;
        foreach (var child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }
        _rowButtons.Clear();
        _drawerNote.Text = Permissions.CanTeleport ? "" : "This server does not allow travelling: pick a place to see it, ask a player in Contacts to bring you.";
        var places = CurrentIndex;
        if (places == null)
        {
            _drawerNote.Text = "No places here.";
            return;
        }
        var here = _player() is { } p ? p.Global : (GlobalPos?)null;
        PhoneScreens.BuildRows(places, _query.Text, _kind, here, _data);
        if (_query.Text.Trim().Length == 0)
        {
            var me = Tool("Where I am", "Centre on you", () => { Recentre(); CloseDrawer(); }, 0);
            me.Alignment = HorizontalAlignment.Left;
            Row(me);
        }
        var added = new List<Control>(_data.Count);
        foreach (var row in _data)
        {
            if (row.Place is not { } place)
            {
                var heading = UiKit.Section(row.Heading ?? "");
                _rows.AddChild(heading);
                added.Add(heading);
                continue;
            }
            var b = Tool(place.Name + row.Distance, "Centre the map on it", () => PickPlace(place), 0);
            b.Text = PhoneScreens.RowText(place, row.Distance);
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            Row(b);
            added.Add(b);
        }
        // the rows arrive one after the other, the first dozen only (a long list is not an animation)
        UiMotion.Stagger(added.Take(12));
    }

    private void Row(Button b)
    {
        b.FocusMode = Control.FocusModeEnum.All;
        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        b.CustomMinimumSize = new Vector2(0, 28);
        _rows.AddChild(b);
        _rowButtons.Add(b);
    }

    private void PickPlace(Place place)
    {
        Select(place, centre: true);
        CloseDrawer();
    }

    /// <summary>Up (-1) or Down (1) in the drawer: from the search box to the first row and back (the pad's and the arrows' way down a list).</summary>
    public void MoveFocus(int way)
    {
        if (!_drawerOpen) return;
        var owner = GetViewport().GuiGetFocusOwner();
        int at = owner is Button b ? _rowButtons.IndexOf(b) : -1;
        int next = owner == _query ? (way > 0 ? 0 : -1) : at + way;
        if (next < 0) { _query.GrabFocus(); return; }
        if (next >= _rowButtons.Count) next = _rowButtons.Count - 1;
        if (next < 0) return;
        _rowButtons[next].GrabFocus();
        _scroll.EnsureControlVisible(_rowButtons[next]);
    }

    // ---------------------------------------------------------------------------------------- the keys

    /// <summary>Enter, A: a row of the drawer that has the focus, else the card's Travel, else what is nearest the centre cross.</summary>
    public void Accept()
    {
        if (GetViewport().GuiGetFocusOwner() is Button { Disabled: false } b && _drawer.IsAncestorOf(b)) { b.EmitSignal(BaseButton.SignalName.Pressed); return; }
        if (_card.Visible && !_travel.Disabled) { UiMotion.Press(_travel); _travel.EmitSignal(BaseButton.SignalName.Pressed); return; }
        _canvas.PickAtCentre();
    }

    private void UpdateHint()
    {
        _pad = InputHints.Pad;
        string text = _pad
            ? "D-pad  move      LB RB  zoom      A  select, then travel      X  places      B  back"
            : "Drag  move      Wheel  zoom      Click  select      /  search      Home  centre      Esc  back";
        if (text != _hintShown) _hint.Text = _hintShown = text;
    }

    /// <summary>The three kinds of place as the map draws them: a disc, a triangle, a diamond.</summary>
    private sealed partial class Legend : Control
    {
        public override void _Draw()
        {
            var font = UiTheme.Font;
            float x = 6;
            foreach (var (kind, name) in new[] { (PlaceKind.Town, "town"), (PlaceKind.Summit, "summit"), (PlaceKind.Pass, "pass") })
            {
                AdventureMap.MapCanvasView.DrawPlaceMarker(this, kind, new Vector2(x + 6, 14), 4f, false);
                DrawString(font, new Vector2(x + 16, 19), name, HorizontalAlignment.Left, -1, 12, new Color(0.95f, 0.96f, 0.98f));
                x += 16 + font.GetStringSize(name, HorizontalAlignment.Left, -1, 12).X + 14;
            }
        }
    }

    // ---------------------------------------------------------------------------------------- nearby players

    /// <summary>The bodies of the other players this client has (it only has those near), found once a second, not on every redraw.</summary>
    private void ScanPeers()
    {
        _sincePeers = 0;
        _peerBodies.Clear();
        _peerNames.Clear();
        if (!IsInsideTree()) return;
        var names = _chat()?.TagNames;
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
        {
            if (node is not FootPlayer other || other.IsMultiplayerAuthority() || other.Puppet) continue;
            string name = names != null && names.TryGetValue(other.GetMultiplayerAuthority(), out var n) ? n : other.BotName;
            _peerBodies.Add(other);
            _peerNames.Add(name);
        }
    }

    /// <summary>Players near this client as dots with their names (the phone's friends layer), drawn after the shared overlays.</summary>
    private sealed class PeersOverlay : IMapOverlay
    {
        private readonly PhoneMap _map;
        public PeersOverlay(PhoneMap map) => _map = map;

        public void Draw(EarthMapView view, CanvasItem on)
        {
            var font = UiTheme.Font;
            for (int i = 0; i < _map._peerBodies.Count; i++)
            {
                var body = _map._peerBodies[i];
                if (!GodotObject.IsInstanceValid(body) || !body.IsInsideTree()) continue;
                var at = view.ToScreen(body.Global);
                if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || at.X < -20 || at.Y < -20 || at.X > view.Size.X + 20 || at.Y > view.Size.Y + 20) continue;
                on.DrawCircle(at, 7, new Color(0, 0, 0, 0.55f));
                on.DrawCircle(at, 5, new Color(0.36f, 0.66f, 1f));
                string name = _map._peerNames[i];
                if (name.Length == 0) continue;
                on.DrawString(font, at + new Vector2(10, 5), name, HorizontalAlignment.Left, -1, 12, new Color(0, 0, 0, 0.8f));
                on.DrawString(font, at + new Vector2(9, 4), name, HorizontalAlignment.Left, -1, 12, new Color(0.80f, 0.90f, 1f));
            }
        }

        public bool Click(Vector2 lv95, MouseButton button) => false;
    }
}
