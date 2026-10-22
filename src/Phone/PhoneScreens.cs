using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;
using UnitSport.Ui;
using UnitSport.BattleRoyale;
using UnitSport.World;

namespace UnitSport.Phone;

/// <summary>
/// What the phone's two screens show (#889), each a <see cref="SubViewport"/> drawn on the 3D
/// phone: the cover screen (clock, the newest call, one quick action at a time, the punch-hole
/// camera) and the inner screen, its shape the open phone's (home, Map, Contacts, Camera,
/// Travelling). The inner screen takes the input the controller pushes into it, the mouse mapped
/// onto it through the 3D screen (<c>FootPlayer.PhoneScreenPoint</c>).
/// </summary>
public partial class PhoneScreens : Node
{
    public enum Page { Home, Map, Contacts, Camera, Activities, Photos, Photo, Travel, Training }

    /// <summary>One quick action on the cover: its label and, when it is a call, who and what.</summary>
    public readonly record struct Quick(string Label, string? Detail = null, Color? Tint = null);

    private const int CoverW = 330;
    private static readonly int CoverH = Mathf.RoundToInt(CoverW * PhoneModel.CoverSize.Y / PhoneModel.CoverSize.X);
    private static readonly Color Ink = new(0.97f, 0.97f, 0.98f), InkDim = new(0.90f, 0.92f, 0.95f, 0.85f);
    /// <summary>The camera app's zoom steps, as a phone offers them (× the main lens).</summary>
    private static readonly float[] Zooms = { 1f, 2f, 5f };
    /// <summary>The main lens's vertical field of view at 1×.</summary>
    public const float LensFov = 58f;

    public SubViewport Cover { get; private set; } = null!;
    public SubViewport Inner { get; private set; } = null!;
    /// <summary>The camera app's view through the back lens; drawn only while the app is open.</summary>
    public SubViewport Finder { get; private set; } = null!;
    /// <summary>The back lens, placed every frame by the controller.</summary>
    public Camera3D Lens { get; private set; } = null!;
    public Page Shown { get; private set; } = Page.Home;
    public float Zoom => Zooms[_zoom];
    public float LensFovNow => Mathf.RadToDeg(2f * Mathf.Atan(Mathf.Tan(Mathf.DegToRad(LensFov) * 0.5f) / Zoom));

    /// <summary>A place picked in the Map app.</summary>
    public event Action<Place>? TravelPicked;
    /// <summary>A command picked in Contacts (a chat command, as the Activities screen sends them).</summary>
    public event Action<string>? CommandPicked;
    /// <summary>An incoming call answered (true) or declined (false) in Contacts.</summary>
    public event Action<int, bool>? CallAnswered;
    /// <summary>The Menu app: the full Activities screen opens over the phone.</summary>
    public event Action? ActivitiesPicked;
    /// <summary>The camera app's shutter.</summary>
    public event Action? ShutterPressed;
    /// <summary>The camera app's turn: the phone turned round, the main camera filming you, the cover showing you (a selfie).</summary>
    public event Action? SelfiePressed;
    /// <summary>A photo set as the lock screen (true) or the home screen (false).</summary>
    public event Action<string, bool>? WallpaperPicked;
    /// <summary>A page changed (the controller hands the mouse over for the camera).</summary>
    public event Action<Page>? PageShown;

    private readonly Func<PlaceSearchUi?> _places;
    private readonly Func<ChatManager?> _chat;

    // cover
    private Label _coverClock = null!, _coverDate = null!, _coverCall = null!, _coverAction = null!, _coverDots = null!;
    private StyleBoxFlat _coverCardBox = null!;
    private PanelContainer _coverCard = null!;
    private TextureRect _coverWall = null!, _innerWall = null!, _coverFinder = null!;
    private ColorRect _coverShade = null!, _innerShade = null!;
    private Control _coverSelfie = null!;
    private Control _photos = null!, _photo = null!;
    private GridContainer _grid = null!;
    private TextureRect _photoView = null!;
    private Label _photoCaption = null!, _photosEmpty = null!;
    private string? _photoShown;
    private Control _coverContent = null!;
    private Panel _coverRing = null!, _innerRing = null!;
    private StyleBoxFlat _coverRingBox = null!, _innerRingBox = null!;

    /// <summary>The inner screen's layout scale (laid out for 800 units high, 640 in VR; drawn at its pixels).</summary>
    public float InnerScale => _innerRoot.Scale.Y;
    private GridContainer _homeGrid = null!;
    private readonly List<(Label Label, string Accept, string Back)> _hints = new();
    private bool _vr;
    private Vector2 _fitWindow;
    // inner
    private Control _innerRoot = null!;
    private Label _statusClock = null!;
    private Control _home = null!, _map = null!, _contacts = null!, _camera = null!, _activities = null!, _travel = null!;
    private VBoxContainer _actBr = null!, _actRaces = null!;
    private Label _actBrTitle = null!, _actBrSub = null!;
    private string _actShown = "";
    // the Training app (#901)
    private Control _training = null!;
    private Label _trCourse = null!, _trStatus = null!, _trTrainer = null!;
    private VBoxContainer _trActions = null!, _trRecent = null!, _trDevices = null!;
    private string _trShown = "";
    private double _actRefreshAt;
    private RaceManager? _races;
    private Label _homeClock = null!, _homeDate = null!, _travelTitle = null!, _travelDots = null!, _travelNote = null!, _zoomLabel = null!;
    private LineEdit _query = null!;
    private VBoxContainer _results = null!, _people = null!, _calls = null!;
    private Label _mapHint = null!;
    private TextureRect _lastShot = null!;
    private ColorRect _flash = null!;
    private readonly List<Button> _homeTiles = new();
    private string _clockShown = "";
    private int _zoom;
    private readonly StringBuilder _sb = new();

    public PhoneScreens(Func<PlaceSearchUi?> places, Func<ChatManager?> chat)
    {
        _places = places;
        _chat = chat;
        Name = "PhoneScreens";
    }

    public PhoneScreens() : this(() => null, () => null) { }

    public override void _Ready()
    {
        Cover = new SubViewport
        {
            Name = "Cover", Size = new Vector2I(CoverW, CoverH), TransparentBg = false, Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, GuiDisableInput = true,
        };
        AddChild(Cover);
        // the back lens: shares the world it is in (no World3D of its own), as the Polaroid's capture does
        Finder = new SubViewport
        {
            Name = "Finder", Size = new Vector2I(640, 460), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            HandleInputLocally = false, GuiDisableInput = true,
        };
        Lens = new Camera3D { Name = "Lens", Fov = LensFov, Near = 0.03f, Far = 4000f };
        Finder.AddChild(Lens);
        AddChild(Finder);
        BuildCover();
        Inner = new SubViewport
        {
            Name = "Inner", Size = new Vector2I(1120, 802), TransparentBg = false, Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, HandleInputLocally = true,
        };
        AddChild(Inner);
        BuildInner();
        ShowPage(Page.Home);
    }

    /// <summary>Draws the screens only while the phone is out; the lens only while the camera app is.</summary>
    public void SetActive(bool on)
    {
        var mode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (Cover.RenderTargetUpdateMode != mode) Cover.RenderTargetUpdateMode = mode;
        if (Inner.RenderTargetUpdateMode != mode) Inner.RenderTargetUpdateMode = mode;
        var lens = on && Shown == Page.Camera ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (Finder.RenderTargetUpdateMode != lens) Finder.RenderTargetUpdateMode = lens;
    }

    /// <summary>
    /// Sizes the inner screen for a window <paramref name="window"/> pixels high (the window's real
    /// pixels, not the stretched canvas's units): about as many pixels as it covers there, so the
    /// open phone's text is sharp.
    /// </summary>
    public void Fit(Vector2 window)
    {
        if (window.Y <= 0) return;
        _fitWindow = window;
        int h = Mathf.Clamp(Mathf.RoundToInt(window.Y * 0.8f / 8f) * 8, 560, 1600);
        var size = new Vector2I(Mathf.RoundToInt(h * PhoneModel.InnerW / PhoneModel.InnerH), h);
        // laid out for a screen 800 units high, drawn at the pixels it covers; in VR for 640, so every
        // target is a quarter bigger at arm's length (#920)
        float k = h / (_vr ? VrUnits : 800f);
        if (Inner.Size == size && Mathf.IsEqualApprox(_innerRoot.Scale.Y, k)) return;
        Inner.Size = size;
        _innerRoot.Scale = new Vector2(k, k);
        _innerRoot.Size = (Vector2)size / k;
        Finder.Size = size * 3 / 5;
    }

    private const float VrUnits = 640f;

    /// <summary>The home screen's apps per row (6; 3 in VR), for the checks.</summary>
    internal int HomeColumns => _homeGrid.Columns;

    /// <summary>
    /// The VR layout (#920): seen at arm's length the screen is about 23° wide, so it is laid out a
    /// quarter bigger, the home screen's six apps in two rows of three (each about 1.6× the desktop's
    /// size), and the footers name the fingertip and B.
    /// </summary>
    public void SetVr(bool vr)
    {
        if (vr == _vr) return;
        _vr = vr;
        _homeGrid.Columns = vr ? 3 : 6;
        foreach (var tile in _homeTiles)
        {
            tile.CustomMinimumSize = vr ? new Vector2(196, 196) : new Vector2(152, 152);
            tile.AddThemeFontSizeOverride("font_size", vr ? 30 : 24);
        }
        _homeClock.AddThemeFontSizeOverride("font_size", vr ? 72 : 120);
        _grid.Columns = vr ? 4 : 6;
        foreach (var (label, accept, back) in _hints) label.Text = HintText(accept, back);
        if (_fitWindow.Y > 0) Fit(_fitWindow);
    }

    // ---------------------------------------------------------------- cover

    private void BuildCover()
    {
        // sized by hand, not anchored to the viewport: anchored, it took the window's content scale (2x)
        var root = new Control { Theme = UiTheme.Get(), Size = new Vector2(CoverW, CoverH) };
        Cover.AddChild(root);
        root.AddChild(_coverWall = WallpaperRect(Wallpaper.Cover));
        root.AddChild(_coverShade = Shade());
        // the front camera's punch hole, top right
        var hole = new Panel { Position = new Vector2(CoverW - 46, 16), Size = new Vector2(22, 22), MouseFilter = Control.MouseFilterEnum.Ignore };
        var dot = new StyleBoxFlat { BgColor = new Color(0.01f, 0.01f, 0.015f) };
        dot.SetCornerRadiusAll(11);
        hole.AddThemeStyleboxOverride("panel", dot);
        root.AddChild(hole);

        var col = UiKit.VBox(8);
        col.Position = new Vector2(18, 44);
        col.Size = new Vector2(CoverW - 36, CoverH - 44 - 26);
        root.AddChild(col);
        _coverContent = col;
        _coverDate = Big("", 22, InkDim);
        col.AddChild(_coverDate);
        _coverClock = Big("12:00", 96);
        col.AddChild(_coverClock);
        col.AddChild(UiKit.Spacer(expand: true));

        _coverCardBox = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.35f), CornerDetail = 8 };
        _coverCardBox.SetCornerRadiusAll(28);
        _coverCardBox.SetContentMarginAll(14);
        var cardPanel = _coverCard = new PanelContainer();
        cardPanel.AddThemeStyleboxOverride("panel", _coverCardBox);
        var card = UiKit.VBox(4);
        _coverCall = Big("", 20, InkDim);
        _coverCall.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        card.AddChild(_coverCall);
        _coverAction = Big("", 38);
        _coverAction.HorizontalAlignment = HorizontalAlignment.Center;
        card.AddChild(_coverAction);
        _coverDots = Big("", 22, InkDim);
        _coverDots.HorizontalAlignment = HorizontalAlignment.Center;
        card.AddChild(_coverDots);
        cardPanel.AddChild(card);
        col.AddChild(cardPanel);

        // a selfie: the main camera's view on the cover, which faces you with it once the open phone is turned round
        _coverSelfie = new Control { Size = new Vector2(CoverW, CoverH), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _coverFinder = new TextureRect
        {
            Texture = Finder.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, Size = new Vector2(CoverW, CoverH),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _coverSelfie.AddChild(_coverFinder);
        var shutter = new Panel { Position = new Vector2(CoverW / 2f - 34, CoverH - 96), Size = new Vector2(68, 68), MouseFilter = Control.MouseFilterEnum.Ignore };
        var ring = new StyleBoxFlat { BgColor = Ink, BorderColor = new Color(1, 1, 1, 0.45f), CornerDetail = 12 };
        ring.SetCornerRadiusAll(34);
        ring.SetBorderWidthAll(6);
        shutter.AddThemeStyleboxOverride("panel", ring);
        _coverSelfie.AddChild(shutter);
        var tip = Big("Selfie", 22, Ink);
        tip.Position = new Vector2(18, 16);
        _coverSelfie.AddChild(tip);
        root.AddChild(_coverSelfie);
        (_coverRing, _coverRingBox) = Ring(28f);
        root.AddChild(_coverRing);
    }

    /// <summary>The VR fingertip's ring (#920): where it would press, filled while it touches.</summary>
    private static (Panel, StyleBoxFlat) Ring(float size)
    {
        var box = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.12f), BorderColor = new Color(1, 1, 1, 0.85f), CornerDetail = 12 };
        box.SetCornerRadiusAll((int)(size / 2));
        box.SetBorderWidthAll(3);
        var ring = new Panel { Size = new Vector2(size, size), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        ring.AddThemeStyleboxOverride("panel", box);
        return (ring, box);
    }

    /// <summary>The fingertip's ring on the screen it is over (null hides both); filled while touching.</summary>
    public void ShowRing(Vector2? uv, bool cover, bool down)
    {
        _coverRing.Visible = uv != null && cover;
        _innerRing.Visible = uv != null && !cover;
        if (uv is not { } at) return;
        var (ring, box, size) = cover ? (_coverRing, _coverRingBox, new Vector2(CoverW, CoverH)) : (_innerRing, _innerRingBox, _innerRoot.Size);
        ring.Position = at * size - ring.Size * 0.5f;
        var fill = new Color(1, 1, 1, down ? 0.45f : 0.12f);
        if (box.BgColor != fill) box.BgColor = fill;
    }

    /// <summary>A selfie: the cover shows the main camera's view (the controller turns the phone and sizes the lens).</summary>
    public void SetSelfie(bool on)
    {
        _coverSelfie.Visible = on;
        _coverContent.Visible = !on;
    }

    /// <summary>The middle of the quick-action card on the cover, 0..1: where a keyboard-led finger rests.</summary>
    public Vector2 CoverCardUv() => _coverCard.GetGlobalRect().GetCenter() / new Vector2(CoverW, CoverH);

    /// <summary>The middle of what has the keys on the inner screen, 0..1; null when nothing has.</summary>
    public Vector2? FocusUv() => Inner.GuiGetFocusOwner() is { } c && c.IsVisibleInTree()
        ? c.GetGlobalRect().GetCenter() / (Vector2)Inner.Size : null;

    /// <summary>The two wallpapers: a photo each, or null for the drawn gradient.</summary>
    public void SetWallpapers(Texture2D? lockScreen, Texture2D? home)
    {
        _coverWall.Texture = lockScreen ?? Wallpaper.Cover;
        _coverWall.StretchMode = lockScreen != null ? TextureRect.StretchModeEnum.KeepAspectCovered : TextureRect.StretchModeEnum.Scale;
        _coverShade.Visible = lockScreen != null;
        _innerWall.Texture = home ?? Wallpaper.Inner;
        _innerWall.StretchMode = home != null ? TextureRect.StretchModeEnum.KeepAspectCovered : TextureRect.StretchModeEnum.Scale;
        _innerShade.Visible = home != null;
    }

    /// <summary>A light shade over a photo wallpaper, so the clock and the labels read on any picture.</summary>
    private static ColorRect Shade()
    {
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.22f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return shade;
    }

    /// <summary>The cover's picture: the clock, and the quick action <paramref name="index"/> of <paramref name="quick"/>.</summary>
    public void ShowCover(IReadOnlyList<Quick> quick, int index)
    {
        var q = quick[Mathf.Clamp(index, 0, quick.Count - 1)];
        SetText(_coverAction, $"‹  {q.Label}  ›");
        SetText(_coverCall, q.Detail ?? "");
        _coverCall.Visible = q.Detail != null;
        var tint = q.Tint ?? new Color(0, 0, 0, 0.35f);
        if (_coverCardBox.BgColor != tint) _coverCardBox.BgColor = tint;
        _sb.Clear();
        for (int i = 0; i < quick.Count; i++) _sb.Append(i == index ? '●' : '○').Append(' ');
        SetText(_coverDots, _sb.ToString().TrimEnd());
    }

    // ---------------------------------------------------------------- inner

    private void BuildInner()
    {
        _innerRoot = new Control { Theme = UiTheme.Get(), Size = Inner.Size };
        Inner.AddChild(_innerRoot);
        _innerRoot.AddChild(_innerWall = WallpaperRect(Wallpaper.Inner));
        _innerRoot.AddChild(_innerShade = Shade());

        // the camera's page lies under the status bar, edge to edge
        _camera = BuildCamera();
        _innerRoot.AddChild(_camera);

        // a status bar along the top, as on any phone
        var status = UiKit.HBox(12);
        status.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        status.OffsetLeft = 40; status.OffsetRight = -40; status.OffsetTop = 12; status.OffsetBottom = 46;
        _statusClock = Big("12:00", 22);
        status.AddChild(_statusClock);
        status.AddChild(UiKit.Spacer(expand: true));
        status.AddChild(Big("Swisscom 5G   ▮▮▮▯   87 %", 20, InkDim));
        _innerRoot.AddChild(status);

        _home = Page_(BuildHome());
        _map = Page_(BuildMap());
        _contacts = Page_(BuildContacts());
        _activities = Page_(BuildActivities());
        _training = Page_(BuildTraining());
        _photos = Page_(BuildPhotos());
        _photo = Page_(BuildPhoto());
        (_innerRing, _innerRingBox) = Ring(40f);
        _innerRoot.AddChild(_innerRing);
        _travel = Page_(BuildTravel());
    }

    private Control Page_(Control content)
    {
        var margin = UiKit.Margin(content, 56, 56, 56, 32);
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _innerRoot.AddChild(margin);
        return margin;
    }

    private Control BuildHome()
    {
        var col = UiKit.VBox(10);
        _homeClock = Big("12:00", 120);
        _homeClock.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_homeClock);
        _homeDate = Big("", 30, InkDim);
        _homeDate.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_homeDate);
        col.AddChild(UiKit.Spacer(expand: true));
        var row = _homeGrid = new GridContainer { Columns = 6, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        row.AddThemeConstantOverride("h_separation", 16);
        row.AddThemeConstantOverride("v_separation", 16);
        row.AddChild(Tile("Map", "Find a place, go there", new Color(0.18f, 0.55f, 0.36f), () => ShowPage(Page.Map)));
        row.AddChild(Tile("Contacts", "Call people: go to them, bring them, team up", new Color(0.20f, 0.42f, 0.80f), () => ShowPage(Page.Contacts)));
        row.AddChild(Tile("Camera", "Take photos and selfies into your album", new Color(0.22f, 0.24f, 0.28f), () => ShowPage(Page.Camera)));
        row.AddChild(Tile("Photos", "Your album; set a photo as a wallpaper", new Color(0.86f, 0.30f, 0.45f), () => ShowPage(Page.Photos)));
        row.AddChild(Tile("Activities", "Join a Battle Royale or a race", new Color(0.82f, 0.38f, 0.16f), () => ShowPage(Page.Activities)));
        row.AddChild(Tile("Menu", "The full Activities menu: set up a race, a Battle Royale", new Color(0.50f, 0.30f, 0.62f), () => ActivitiesPicked?.Invoke()));
        row.AddChild(Tile("Training", "Ride a GPX course on your home trainer, the road's grade on the trainer", new Color(0.10f, 0.55f, 0.58f), () => ShowPage(Page.Training)));
        col.AddChild(row);
        col.AddChild(UiKit.Spacer(expand: true));
        col.AddChild(Hint("open", "put the phone away"));
        return col;
    }

    private Button Tile(string name, string tip, Color colour, Action pressed)
    {
        var b = new Button { Text = name, CustomMinimumSize = new Vector2(152, 152), TooltipText = tip, FocusMode = Control.FocusModeEnum.All };
        b.AddThemeFontSizeOverride("font_size", 24);
        foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color" }) b.AddThemeColorOverride(c, Ink);
        foreach (var (state, c, border) in new[] { ("normal", colour, 0), ("hover", colour.Lightened(0.15f), 0),
                     ("pressed", colour.Darkened(0.2f), 0), ("focus", colour.Lightened(0.1f), 6) })
        {
            var box = new StyleBoxFlat { BgColor = c, CornerDetail = 10, BorderColor = Ink };
            box.SetCornerRadiusAll(36);
            box.SetBorderWidthAll(border);
            if (state == "focus") box.DrawCenter = false;
            b.AddThemeStyleboxOverride(state, box);
        }
        b.Pressed += pressed;
        _homeTiles.Add(b);
        return b;
    }

    private Control BuildMap()
    {
        var col = UiKit.VBox(12);
        col.AddChild(Title("Map"));
        _query = new LineEdit { PlaceholderText = "Search a town, a summit, a pass", CustomMinimumSize = new Vector2(0, 64) };
        _query.AddThemeFontSizeOverride("font_size", 30);
        _query.TextChanged += _ => FillResults();
        _query.TextSubmitted += _ => { if (_results.GetChildCount() > 0 && _results.GetChild(0) is Button b) b.EmitSignal(BaseButton.SignalName.Pressed); };
        col.AddChild(_query);
        _mapHint = Big("", 22, InkDim);
        col.AddChild(_mapHint);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _results = UiKit.VBox(6);
        _results.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_results);
        col.AddChild(scroll);
        col.AddChild(Hint("travel there", "back"));
        return col;
    }

    private void FillResults()
    {
        foreach (var child in _results.GetChildren()) child.QueueFree();
        var places = _places();
        if (places == null)
        {
            SetText(_mapHint, "No map here.");
            return;
        }
        string q = _query.Text.Trim();
        var shown = q.Length == 0
            ? places.All.Where(p => p.Kind == PlaceKind.Town).OrderByDescending(p => p.Buildings).Take(10).ToList()
            : places.Search(q, 10);
        SetText(_mapHint, !Permissions.CanTeleport ? "This server does not allow travelling: ask a player in Contacts to bring you."
            : q.Length == 0 ? "Popular places" : shown.Count == 0 ? "Nothing by that name" : "");
        foreach (var place in shown)
        {
            var b = Row(PlaceSearchUi.Describe(place));
            var p = place;
            b.Pressed += () => TravelPicked?.Invoke(p);
            b.Disabled = !Permissions.CanTeleport;
            _results.AddChild(b);
        }
    }

    private Control BuildContacts()
    {
        var col = UiKit.VBox(12);
        col.AddChild(Title("Contacts"));
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = UiKit.VBox(10);
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _calls = UiKit.VBox(6);
        list.AddChild(_calls);
        _people = UiKit.VBox(6);
        list.AddChild(_people);
        scroll.AddChild(list);
        col.AddChild(scroll);
        col.AddChild(Hint("call", "back"));
        return col;
    }

    /// <summary>Lists the incoming calls (invites) and everyone online, each with what you can ask them.</summary>
    public void FillContacts()
    {
        foreach (var child in _calls.GetChildren()) child.QueueFree();
        foreach (var child in _people.GetChildren()) child.QueueFree();
        if (InviteService.Client is { } invites && invites.Incoming.Any())
        {
            _calls.AddChild(Big("Calls", 26, InkDim));
            foreach (var call in invites.Incoming)
            {
                var row = UiKit.HBox(10);
                var who = Big($"{call.PeerName}: {call.Title}", 28);
                who.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                who.ClipText = true;
                row.AddChild(who);
                int id = call.Id;
                row.AddChild(Small("Answer", new Color(0.20f, 0.62f, 0.32f), () => { CallAnswered?.Invoke(id, true); FillContacts(); }));
                row.AddChild(Small("Decline", new Color(0.70f, 0.22f, 0.20f), () => { CallAnswered?.Invoke(id, false); FillContacts(); }));
                _calls.AddChild(row);
            }
        }
        _people.AddChild(Big("People", 26, InkDim));
        var chat = _chat();
        if (!Permissions.Online || chat == null)
        {
            _people.AddChild(Big("Nobody to call: play online to reach other players.", 26, InkDim));
            return;
        }
        string me = GameShell.Instance?.PlayerName ?? "";
        var names = chat.PlayerNames.Where(n => !n.Equals(me, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0) _people.AddChild(Big("Nobody else is online.", 26, InkDim));
        foreach (var name in names)
        {
            var row = UiKit.HBox(10);
            var who = Big(name, 30);
            who.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(who);
            string n = name;
            row.AddChild(Small("Go to", new Color(0.20f, 0.42f, 0.80f), () => CommandPicked?.Invoke($"/tpa {n}")));
            row.AddChild(Small("Bring here", new Color(0.20f, 0.42f, 0.80f), () => CommandPicked?.Invoke($"/tphere {n}")));
            row.AddChild(Small("Party", new Color(0.45f, 0.30f, 0.70f), () => CommandPicked?.Invoke($"/party invite {n}")));
            row.AddChild(Small("Fight", new Color(0.70f, 0.22f, 0.20f), () => CommandPicked?.Invoke($"/fight {n}")));
            _people.AddChild(row);
        }
    }

    /// <summary>The Activities app: the Battle Royale lobby and the races on the server, joined from the phone.</summary>
    private Control BuildActivities()
    {
        var col = UiKit.VBox(10);
        col.AddChild(Title("Activities"));
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = UiKit.VBox(12);
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        list.AddChild(Big("Battle Royale", 26, InkDim));
        _actBrTitle = Big("", 30);
        list.AddChild(_actBrTitle);
        _actBrSub = Big("", 22, InkDim);
        _actBrSub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        list.AddChild(_actBrSub);
        _actBr = UiKit.VBox(6);
        list.AddChild(_actBr);
        list.AddChild(UiKit.Spacer(6));
        list.AddChild(Big("Races", 26, InkDim));
        _actRaces = UiKit.VBox(6);
        list.AddChild(_actRaces);
        scroll.AddChild(list);
        col.AddChild(scroll);
        col.AddChild(Hint("join", "back"));
        return col;
    }

    /// <summary>The Activities app's lists, rebuilt only when what they show changes; the race list asked for once a second.</summary>
    private void RefreshActivities(bool force)
    {
        if (!Permissions.Online)
        {
            if (_actShown == "offline" && !force) return;
            _actShown = "offline";
            SetText(_actBrTitle, "Not in this game");
            SetText(_actBrSub, "Battle Royale and races are played online: join a server to see them here.");
            Clear(_actBr); Clear(_actRaces);
            return;
        }
        _races ??= GameShell.Instance?.World?.GetNodeOrNull<RaceManager>(RaceManager.NodeName);
        if (RealClock.Now >= _actRefreshAt)
        {
            _actRefreshAt = RealClock.Now + 1.0;
            _races?.RequestRows();
        }
        var br = BrManager.Instance;
        var s = br?.State ?? new BrState();
        var mine = s.Find(br?.Me ?? 0);
        var rows = _races?.Rows ?? [];
        string shape = $"{s.Phase}|{s.Entrants.Count}|{mine != null}|{mine?.Alive}|{s.AreaName}|"
            + string.Join(",", rows.Select(r => $"{r.Id}:{r.Entrants}:{r.Running}:{r.Mine}:{r.Joinable}"));
        if (shape == _actShown && !force) return;
        _actShown = shape;

        (string title, string sub) = s.Phase switch
        {
            BrPhase.Lobby => ($"Lobby open · {s.AreaName}", $"{s.Entrants.Count} joined. It starts once {BrManager.MinPlayers} are in."),
            BrPhase.Countdown => ($"Starting · {s.AreaName}", $"{s.Entrants.Count} in the match."),
            BrPhase.Playing => ($"Match on · {s.AreaName}", $"{s.AliveCount} of {s.Entrants.Count} alive" + (mine != null ? mine.Alive ? ", you among them." : $", you placed #{mine.Place}." : ".")),
            _ => ("No match open", "An admin opens a lobby from the Menu app; you are invited when it opens."),
        };
        SetText(_actBrTitle, title);
        SetText(_actBrSub, sub);
        Clear(_actBr);
        if (s.Phase is BrPhase.Lobby or BrPhase.Countdown)
            _actBr.AddChild(Small(mine == null ? "Join the match" : "Leave the lobby", mine == null ? new Color(0.82f, 0.38f, 0.16f) : new Color(0.45f, 0.45f, 0.5f),
                () => CommandPicked?.Invoke(mine == null ? "/br join" : "/br leave")));

        Clear(_actRaces);
        foreach (var r in rows)
        {
            var row = UiKit.HBox(10);
            string state = r.Running ? "racing" : $"{Mathf.CeilToInt(r.SecondsLeft)} s to join";
            var what = Big($"{r.Host}'s {(r.Km > 0 ? $"{r.Km:0.0} km " : "")}{r.What} · {r.Entrants} in · {state}", 24);
            what.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            what.ClipText = true;
            row.AddChild(what);
            int id = r.Id;
            if (r.Mine) row.AddChild(Small("Leave", new Color(0.45f, 0.45f, 0.5f), () => CommandPicked?.Invoke("/race leave")));
            else if (r.Joinable) row.AddChild(Small("Join", new Color(0.18f, 0.55f, 0.36f), () => CommandPicked?.Invoke($"/race join {id}")));
            _actRaces.AddChild(row);
        }
        if (rows.Count == 0) _actRaces.AddChild(Big("No race right now.", 24, InkDim));
        _actRaces.AddChild(Small("Start a 2 km race here", new Color(0.20f, 0.42f, 0.80f), () => CommandPicked?.Invoke("/race start 2000 open")));
        if (Inner.GuiGetFocusOwner() == null || !_activities.IsAncestorOf(Inner.GuiGetFocusOwner())) Callable.From(() => FocusFirst(_activities)).CallDeferred();
    }

    private static void Clear(Node n)
    {
        foreach (var c in n.GetChildren()) { n.RemoveChild(c); c.QueueFree(); }
    }

    /// <summary>The list a swipe at <paramref name="at"/> (the inner screen's pixels) scrolls, or null.</summary>
    public ScrollContainer? ScrollAt(Vector2 at)
    {
        foreach (var page in new[] { _map, _contacts, _activities, _photos, _training })
        {
            if (!page.Visible) continue;
            var found = FindScroll(page, at);
            if (found != null) return found;
        }
        return null;
    }

    private static ScrollContainer? FindScroll(Node under, Vector2 at)
    {
        foreach (var child in under.GetChildren())
        {
            if (child is ScrollContainer { Visible: true } sc && sc.GetGlobalRect().HasPoint(at)) return sc;
            if (FindScroll(child, at) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>
    /// The Training app (#901): a GPX course to ride on the home trainer, the trainer itself, and Start.
    /// The page talks to <see cref="Trainer.TrainingSession"/> and <see cref="Trainer.TrainerLink"/>
    /// directly: both are the local client's, nothing here goes over the network.
    /// </summary>
    private Control BuildTraining()
    {
        var col = UiKit.VBox(10);
        col.AddChild(Title("Training"));
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = UiKit.VBox(12);
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        list.AddChild(Big("Course", 26, InkDim));
        _trCourse = Big("", 30);
        _trCourse.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        list.AddChild(_trCourse);
        _trStatus = Big("", 22, InkDim);
        _trStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        list.AddChild(_trStatus);
        _trActions = UiKit.VBox(6);
        list.AddChild(_trActions);
        _trRecent = UiKit.VBox(6);
        list.AddChild(_trRecent);
        list.AddChild(UiKit.Spacer(6));
        list.AddChild(Big("Trainer", 26, InkDim));
        _trTrainer = Big("", 24);
        _trTrainer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        list.AddChild(_trTrainer);
        _trDevices = UiKit.VBox(6);
        list.AddChild(_trDevices);
        scroll.AddChild(list);
        col.AddChild(scroll);
        col.AddChild(Hint("choose", "back"));
        return col;
    }

    /// <summary>The Training app's lists, rebuilt only when what they show changes; the labels are set as they go.</summary>
    private void RefreshTraining(bool force)
    {
        var session = Trainer.TrainingSession.Instance;
        var link = Trainer.TrainerLink.Instance;
        if (session == null || link == null)
        {
            SetText(_trCourse, "Not in this game");
            SetText(_trStatus, "Trainings are ridden in a world: start or join one first.");
            return;
        }
        var phase = session.State;
        SetText(_trCourse, session.CourseName is { } name
            ? FormattableString.Invariant($"{name} · {session.CourseKm:0.0} km · {session.CourseAscent:0} m up")
            : "No course");
        SetText(_trStatus, session.Status);
        string devices = string.Join(", ", link.Devices.Values.Select(d => $"{d.Name} {d.State}"));
        string cadence = link.Cadence is { } c ? FormattableString.Invariant($" · {c:0} rpm") : "";
        string heart = link.HeartRate is { } hr ? $" · {hr} bpm" : "";
        SetText(_trTrainer, !link.Available ? "No Bluetooth on this build (Windows only): the keys pedal."
            : link.PowerLive ? $"{devices}\n{link.Watts} W{cadence}{heart} · difficulty {link.Difficulty}%"
            : link.Devices.Count > 0 ? $"{devices}\nNo power yet: pedal. Difficulty {link.Difficulty}%"
            : $"No trainer: the keys pedal. Difficulty {link.Difficulty}%");

        string shape = $"{phase}|{session.CanStart}|{string.Join(",", link.Prefs.Recent)}|{link.Scanning}|{link.Difficulty}|{link.Prefs.Devices.Count}|"
            + string.Join(",", link.Found.Select(f => f.Id)) + "|" + string.Join(",", link.Devices.Keys);
        if (shape == _trShown && !force) return;
        _trShown = shape;

        var go = new Color(0.10f, 0.55f, 0.58f);
        var grey = new Color(0.45f, 0.45f, 0.5f);
        Clear(_trActions);
        var row = UiKit.HBox(10);
        bool riding = phase is Trainer.TrainingSession.Phase.Travelling or Trainer.TrainingSession.Phase.Mounting or Trainer.TrainingSession.Phase.Riding;
        if (riding) row.AddChild(Small("Stop training", new Color(0.75f, 0.25f, 0.22f), () => session.Stop()));
        else if (session.CanStart) row.AddChild(Small("Start training", go, session.Start));
        row.AddChild(Small("Import GPX...", new Color(0.20f, 0.42f, 0.80f), session.Browse));
        _trActions.AddChild(row);

        Clear(_trRecent);
        foreach (var path in link.Prefs.Recent.Where(p => p != session.CoursePath).Take(4))
        {
            var b = Row("Ride again: " + System.IO.Path.GetFileNameWithoutExtension(path));
            b.Pressed += () => session.Load(path);
            _trRecent.AddChild(b);
        }

        Clear(_trDevices);
        if (link.Available)
        {
            var tools = UiKit.HBox(10);
            tools.AddChild(Small(link.Scanning ? "Searching..." : "Search", go, () => link.Scan()));
            if (link.Prefs.Devices.Count > 0) tools.AddChild(Small("Reconnect", grey, link.Reconnect));
            tools.AddChild(Small("Easier", grey, () => link.Difficulty -= 10));
            tools.AddChild(Small("Harder", grey, () => link.Difficulty += 10));
            _trDevices.AddChild(tools);
            foreach (var d in link.Found.Where(f => !link.Devices.ContainsKey(f.Id)))
            {
                var found = d;
                var b = Row(Trainer.TrainerLink.Describe(found));
                b.Pressed += () => link.Connect(found);
                _trDevices.AddChild(b);
            }
        }
        if (Inner.GuiGetFocusOwner() == null || !_training.IsAncestorOf(Inner.GuiGetFocusOwner())) Callable.From(() => FocusFirst(_training)).CallDeferred();
    }


    /// <summary>The Photos app: the album's shots, newest first, as a grid.</summary>
    private Control BuildPhotos()
    {
        var col = UiKit.VBox(10);
        col.AddChild(Title("Photos"));
        _photosEmpty = Big("No photos yet: take some with the Camera app.", 26, InkDim);
        col.AddChild(_photosEmpty);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 10);
        _grid.AddThemeConstantOverride("v_separation", 10);
        scroll.AddChild(_grid);
        col.AddChild(scroll);
        col.AddChild(Hint("open", "back"));
        return col;
    }

    private void FillPhotos()
    {
        Clear(_grid);
        var ids = Items.PhotoStore.Archive().Take(60).ToList();
        _photosEmpty.Visible = ids.Count == 0;
        foreach (var id in ids)
        {
            var b = new Button { CustomMinimumSize = new Vector2(150, 150), FocusMode = Control.FocusModeEnum.All, TooltipText = Items.PhotoStore.ShortCaption(id) };
            var focus = new StyleBoxFlat { DrawCenter = false, BorderColor = Ink, CornerDetail = 6 };
            focus.SetCornerRadiusAll(10);
            focus.SetBorderWidthAll(5);
            b.AddThemeStyleboxOverride("focus", focus);
            foreach (var state in new[] { "normal", "hover", "pressed" }) b.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
            var pic = new TextureRect
            {
                Texture = PhoneWallpaper.SquareOf(id), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            pic.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            pic.OffsetLeft = pic.OffsetTop = 4; pic.OffsetRight = pic.OffsetBottom = -4;
            b.AddChild(pic);
            string shot = id;
            b.Pressed += () => ShowPhoto(shot);
            _grid.AddChild(b);
        }
    }

    /// <summary>One photo, big, with what it can be.</summary>
    private Control BuildPhoto()
    {
        var col = UiKit.VBox(10);
        _photoView = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        col.AddChild(_photoView);
        _photoCaption = Big("", 22, InkDim);
        _photoCaption.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_photoCaption);
        var row = UiKit.HBox(14);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        row.AddChild(Small("Set as lock screen", new Color(0.20f, 0.42f, 0.80f), () => { if (_photoShown != null) WallpaperPicked?.Invoke(_photoShown, true); }));
        row.AddChild(Small("Set as home screen", new Color(0.18f, 0.55f, 0.36f), () => { if (_photoShown != null) WallpaperPicked?.Invoke(_photoShown, false); }));
        foreach (var b in row.GetChildren().OfType<Button>()) b.CustomMinimumSize = new Vector2(260, 54);
        col.AddChild(row);
        col.AddChild(Hint("choose", "back to the album"));
        return col;
    }

    public void ShowPhoto(string id)
    {
        _photoShown = id;
        _photoView.Texture = PhoneWallpaper.FullOf(id);
        SetText(_photoCaption, Items.PhotoStore.ShortCaption(id));
        ShowPage(Page.Photo);
    }

    /// <summary>The camera app: the back lens edge to edge, the shutter on the right, zoom above it, the last shot below.</summary>
    private Control BuildCamera()
    {
        var page = new Control { Name = "CameraPage", MouseFilter = Control.MouseFilterEnum.Ignore };
        page.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var finder = new TextureRect
        {
            Texture = Finder.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        finder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        page.AddChild(finder);

        var side = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        side.AddThemeConstantOverride("separation", 26);
        side.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        side.OffsetLeft = -150; side.OffsetRight = -30;
        page.AddChild(side);
        _zoomLabel = Big("1×", 30);
        _zoomLabel.HorizontalAlignment = HorizontalAlignment.Center;
        side.AddChild(_zoomLabel);
        var shutter = new Button { CustomMinimumSize = new Vector2(120, 120), FocusMode = Control.FocusModeEnum.None, TooltipText = "Take a photo" };
        foreach (var (state, c) in new[] { ("normal", Ink), ("hover", Ink), ("pressed", new Color(0.8f, 0.8f, 0.82f)), ("focus", Ink) })
        {
            var ring = new StyleBoxFlat { BgColor = c, BorderColor = new Color(1, 1, 1, 0.45f), CornerDetail = 16 };
            ring.SetCornerRadiusAll(60);
            ring.SetBorderWidthAll(8);
            shutter.AddThemeStyleboxOverride(state, ring);
        }
        shutter.Pressed += () => ShutterPressed?.Invoke();
        side.AddChild(shutter);
        _lastShot = new TextureRect
        {
            CustomMinimumSize = new Vector2(84, 100), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        side.AddChild(_lastShot);
        var turn = Small("Selfie", new Color(0.30f, 0.32f, 0.38f), () => SelfiePressed?.Invoke());
        turn.CustomMinimumSize = new Vector2(120, 50);
        turn.FocusMode = Control.FocusModeEnum.None;
        side.AddChild(turn);

        var hint = Big(InputHints.Pad ? "L stick  walk      RT  shoot      LB RB  zoom      Y  selfie      B  back" : "WASD  walk      Click / Enter  shoot      Wheel  zoom      F  selfie      Esc  back", 22, Ink);
        hint.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        hint.OffsetTop = -54; hint.OffsetBottom = -20; hint.OffsetLeft = 40;
        page.AddChild(hint);

        _flash = new ColorRect { Color = new Color(1, 1, 1, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        page.AddChild(_flash);
        return page;
    }

    /// <summary>Steps the zoom by <paramref name="step"/> (1×, 2×, 5×).</summary>
    public void StepZoom(int step, bool wrap = false)
    {
        _zoom = wrap ? (_zoom + step + Zooms.Length) % Zooms.Length : Mathf.Clamp(_zoom + step, 0, Zooms.Length - 1);
        SetText(_zoomLabel, $"{Zoom:0.#}×");
    }

    /// <summary>The shutter went: the screen flashes white, and the shot lands in the corner.</summary>
    public void Shot(Texture2D? print)
    {
        _flash.Color = new Color(1, 1, 1, 0.9f);
        if (print != null) _lastShot.Texture = print;
    }

    private Control BuildTravel()
    {
        var col = UiKit.VBox(16);
        col.AddChild(UiKit.Spacer(expand: true));
        _travelTitle = Big("", 48);
        _travelTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _travelTitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(_travelTitle);
        _travelDots = Big("", 60, InkDim);
        _travelDots.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_travelDots);
        _travelNote = Big("", 30, InkDim);
        _travelNote.HorizontalAlignment = HorizontalAlignment.Center;
        _travelNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(_travelNote);
        col.AddChild(UiKit.Spacer(expand: true));
        return col;
    }

    /// <summary>The travelling page: where to, and dots that go round while the ground there streams in.</summary>
    public void ShowTravel(string where, string note = "")
    {
        SetText(_travelTitle, $"Travelling to {where}");
        SetText(_travelNote, note);
        ShowPage(Page.Travel);
    }

    public void ShowPage(Page page)
    {
        Shown = page;
        _home.Visible = page == Page.Home;
        _map.Visible = page == Page.Map;
        _contacts.Visible = page == Page.Contacts;
        _camera.Visible = page == Page.Camera;
        _activities.Visible = page == Page.Activities;
        _photos.Visible = page == Page.Photos;
        _photo.Visible = page == Page.Photo;
        _travel.Visible = page == Page.Travel;
        _training.Visible = page == Page.Training;
        switch (page)
        {
            case Page.Home:
                if (_homeTiles.Count > 0) Callable.From(() => _homeTiles[0].GrabFocus()).CallDeferred();
                break;
            case Page.Map:
                _query.Clear();
                FillResults();
                Callable.From(() => _query.GrabFocus()).CallDeferred();
                break;
            case Page.Contacts:
                _chat()?.RequestPlayerNames();
                FillContacts();
                Callable.From(() => FocusFirst(_contacts)).CallDeferred();
                break;
            case Page.Camera:
                Inner.GuiReleaseFocus();
                break;
            case Page.Photos:
                FillPhotos();
                Callable.From(() => FocusFirst(_photos)).CallDeferred();
                break;
            case Page.Photo:
                Callable.From(() => FocusFirst(_photo)).CallDeferred();
                break;
            case Page.Training:
                _trShown = "";
                RefreshTraining(force: true);
                break;
            case Page.Activities:
                _actShown = "";
                _actRefreshAt = 0;
                RefreshActivities(force: true);
                break;
        }
        PageShown?.Invoke(page);
    }

    /// <summary>The time on both screens, the travelling dots and the shutter's flash, set only when they change.</summary>
    public void Tick(string clock, string date, float dt)
    {
        if (clock != _clockShown)
        {
            _clockShown = clock;
            _coverClock.Text = clock;
            _statusClock.Text = clock;
            _homeClock.Text = clock;
            _coverDate.Text = date;
            _homeDate.Text = date;
        }
        if (Shown == Page.Activities) RefreshActivities(force: false);
        if (Shown == Page.Training) RefreshTraining(force: false);
        if (Shown == Page.Travel) SetText(_travelDots, ((int)(RealClock.Now * 3) % 3) switch { 0 => "●  ○  ○", 1 => "○  ●  ○", _ => "○  ○  ●" });
        if (_flash.Color.A > 0f) _flash.Color = new Color(1, 1, 1, Mathf.MoveToward(_flash.Color.A, 0f, dt / 0.35f));
    }

    // ---------------------------------------------------------------- bits

    private static TextureRect WallpaperRect(Texture2D texture)
    {
        var wall = new TextureRect
        {
            Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
        };
        wall.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return wall;
    }

    private static Label Big(string text, int size, Color? colour = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", colour ?? Ink);
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.45f));
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        return l;
    }

    private static Label Title(string text) => Big(text, 54);

    /// <summary>The page's two keys, named for the device in hand (a menu's accept and back, not the game's actions).</summary>
    private Label Hint(string accept, string back)
    {
        var l = Big(HintText(accept, back), 22, InkDim);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        _hints.Add((l, accept, back));
        return l;
    }

    private string HintText(string accept, string back) =>
        _vr ? $"tap  {accept}      swipe right or B  {back}"
        : InputHints.Pad ? $"A  {accept}      B  {back}" : $"Enter  {accept}      Esc  {back}";

    private static Button Row(string text)
    {
        var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 58), FocusMode = Control.FocusModeEnum.All };
        b.AddThemeFontSizeOverride("font_size", 28);
        return b;
    }

    private static Button Small(string text, Color colour, Action pressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(150, 54), FocusMode = Control.FocusModeEnum.All };
        b.AddThemeFontSizeOverride("font_size", 24);
        b.AddThemeColorOverride("font_color", Ink);
        foreach (var (state, c) in new[] { ("normal", colour), ("hover", colour.Lightened(0.15f)), ("pressed", colour.Darkened(0.2f)) })
        {
            var box = new StyleBoxFlat { BgColor = c, CornerDetail = 6 };
            box.SetCornerRadiusAll(27);
            b.AddThemeStyleboxOverride(state, box);
        }
        var focus = new StyleBoxFlat { DrawCenter = false, BorderColor = Ink, CornerDetail = 6 };
        focus.SetCornerRadiusAll(27);
        focus.SetBorderWidthAll(4);
        b.AddThemeStyleboxOverride("focus", focus);
        b.Pressed += pressed;
        return b;
    }

    private static void FocusFirst(Node under)
    {
        foreach (var child in under.GetChildren())
        {
            if (child is Button { Visible: true, Disabled: false } b) { b.GrabFocus(); return; }
            FocusFirst(child);
            if (under.GetViewport()?.GuiGetFocusOwner() is { } owner && under.IsAncestorOf(owner)) return;
        }
    }

    private static void SetText(Label l, string text)
    {
        if (l.Text != text) l.Text = text;
    }
}
