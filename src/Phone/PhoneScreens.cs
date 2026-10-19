using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Phone;

/// <summary>
/// What the phone's two screens show (#889), each a <see cref="SubViewport"/>: the narrow cover
/// screen (clock, the newest call, one quick action at a time) and the square inner screen (home,
/// Map, Contacts, Travelling). The inner screen's content sits in a centred band of the game
/// window's shape, with wallpaper above and below: the band is what fills the view once the camera
/// is on the screen, so the full-screen picture is this same texture, cropped the same way.
/// </summary>
public partial class PhoneScreens : Node
{
    public enum Page { Home, Map, Contacts, Travel }

    /// <summary>One quick action on the cover: its label and, when it is a call, who and what.</summary>
    public readonly record struct Quick(string Label, string? Detail = null, Color? Tint = null);

    private const int CoverW = 330, CoverH = 700;
    private static readonly Color Ink = new(0.95f, 0.96f, 0.98f), InkDim = new(0.80f, 0.84f, 0.90f, 0.85f);

    public SubViewport Cover { get; private set; } = null!;
    public SubViewport Inner { get; private set; } = null!;
    public Page Shown { get; private set; } = Page.Home;

    /// <summary>A place picked in the Map app.</summary>
    public event Action<Place>? TravelPicked;
    /// <summary>A command picked in Contacts (a chat command, as the Activities screen sends them).</summary>
    public event Action<string>? CommandPicked;
    /// <summary>An incoming call answered (true) or declined (false) in Contacts.</summary>
    public event Action<int, bool>? CallAnswered;
    /// <summary>The Activities app: the existing screen opens over the phone.</summary>
    public event Action? ActivitiesPicked;

    private readonly Func<PlaceSearchUi?> _places;
    private readonly Func<ChatManager?> _chat;

    // cover
    private Label _coverClock = null!, _coverDate = null!, _coverCall = null!, _coverAction = null!, _coverDots = null!;
    private PanelContainer _coverCard = null!;
    private StyleBoxFlat _coverCardBox = null!;
    // inner
    private Control _band = null!, _innerRoot = null!;
    private Label _statusClock = null!;
    private Control _home = null!, _map = null!, _contacts = null!, _travel = null!;
    private Label _homeClock = null!, _homeDate = null!, _travelTitle = null!, _travelDots = null!;
    private LineEdit _query = null!;
    private VBoxContainer _results = null!, _people = null!, _calls = null!;
    private Label _mapHint = null!;
    private readonly List<Button> _homeTiles = new();
    private string _clockShown = "";
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
        BuildCover();
        Inner = new SubViewport
        {
            Name = "Inner", Size = new Vector2I(1024, 1024), TransparentBg = false, Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, HandleInputLocally = true,
        };
        AddChild(Inner);
        BuildInner();
        ShowPage(Page.Home);
    }

    /// <summary>Draws the screens only while the phone is out.</summary>
    public void SetActive(bool on)
    {
        var mode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (Cover.RenderTargetUpdateMode != mode) Cover.RenderTargetUpdateMode = mode;
        if (Inner.RenderTargetUpdateMode != mode) Inner.RenderTargetUpdateMode = mode;
    }

    /// <summary>
    /// Sizes the inner screen for a window of <paramref name="window"/> pixels: as wide as the window
    /// (so the full-screen picture is drawn 1:1), and the band the window's shape.
    /// </summary>
    public void Fit(Vector2 window)
    {
        if (window.X <= 0 || window.Y <= 0) return;
        float aspect = window.X / window.Y;
        int n = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(window.X, window.Y) / 8f) * 8, 768, 2560);
        if (Inner.Size.X != n) Inner.Size = new Vector2I(n, n);
        _innerRoot.Size = new Vector2(n, n);
        var band = new Vector2(n * Mathf.Min(1f, aspect), n / Mathf.Max(1f, aspect));
        _band.Size = band;
        _band.Position = (new Vector2(n, n) - band) * 0.5f;
    }

    // ---------------------------------------------------------------- cover

    private void BuildCover()
    {
        // sized by hand, not anchored to the viewport: anchored, it took the window's content scale (2x)
        var root = new Control { Theme = UiTheme.Get(), Size = new Vector2(CoverW, CoverH) };
        Cover.AddChild(root);
        root.AddChild(WallpaperRect());

        var col = UiKit.VBox(10);
        col.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        col.OffsetLeft = 18; col.OffsetRight = -18; col.OffsetTop = 40; col.OffsetBottom = -34;
        root.AddChild(col);
        _coverClock = Big("12:00", 92);
        col.AddChild(_coverClock);
        _coverDate = Big("", 24, InkDim);
        col.AddChild(_coverDate);
        col.AddChild(UiKit.Spacer(expand: true));

        _coverCardBox = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.35f), CornerDetail = 8 };
        _coverCardBox.SetCornerRadiusAll(28);
        _coverCardBox.SetContentMarginAll(16);
        _coverCard = new PanelContainer();
        _coverCard.AddThemeStyleboxOverride("panel", _coverCardBox);
        var card = UiKit.VBox(6);
        _coverCall = Big("", 22, InkDim);
        _coverCall.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        card.AddChild(_coverCall);
        _coverAction = Big("", 44);
        card.AddChild(_coverAction);
        _coverDots = Big("", 26, InkDim);
        card.AddChild(_coverDots);
        _coverCard.AddChild(card);
        col.AddChild(_coverCard);
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
        var root = _innerRoot = new Control { Theme = UiTheme.Get(), Size = new Vector2(1024, 1024) };
        Inner.AddChild(root);
        root.AddChild(WallpaperRect());
        _band = new Control { Name = "Band" };
        root.AddChild(_band);

        // a status bar along the top of the band, as on any phone
        var status = UiKit.HBox(12);
        status.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        status.OffsetLeft = 40; status.OffsetRight = -40; status.OffsetTop = 14; status.OffsetBottom = 50;
        _statusClock = Big("12:00", 22);
        status.AddChild(_statusClock);
        status.AddChild(UiKit.Spacer(expand: true));
        status.AddChild(Big("Swisscom 5G   ▮▮▮▯   87 %", 20, InkDim));
        _band.AddChild(status);

        _home = Page_(BuildHome());
        _map = Page_(BuildMap());
        _contacts = Page_(BuildContacts());
        _travel = Page_(BuildTravel());
    }

    private Control Page_(Control content)
    {
        var margin = UiKit.Margin(content, 64, 64, 64, 40);
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _band.AddChild(margin);
        return margin;
    }

    private Control BuildHome()
    {
        var col = UiKit.VBox(14);
        _homeClock = Big("12:00", 120);
        _homeClock.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_homeClock);
        _homeDate = Big("", 30, InkDim);
        _homeDate.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_homeDate);
        col.AddChild(UiKit.Spacer(expand: true));
        var row = UiKit.HBox(40);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        row.AddChild(Tile("Map", "Find a place, go there", new Color(0.18f, 0.55f, 0.36f), () => ShowPage(Page.Map)));
        row.AddChild(Tile("Contacts", "Call people: go to them, bring them, team up", new Color(0.20f, 0.42f, 0.80f), () => ShowPage(Page.Contacts)));
        row.AddChild(Tile("Activities", "Battle Royale, races", new Color(0.82f, 0.38f, 0.16f), () => ActivitiesPicked?.Invoke()));
        col.AddChild(row);
        col.AddChild(UiKit.Spacer(expand: true));
        col.AddChild(Hint("open", "put the phone away"));
        return col;
    }

    private Button Tile(string name, string tip, Color colour, Action pressed)
    {
        var b = new Button { Text = name, CustomMinimumSize = new Vector2(250, 250), TooltipText = tip, FocusMode = Control.FocusModeEnum.All };
        b.AddThemeFontSizeOverride("font_size", 34);
        b.AddThemeColorOverride("font_color", Ink);
        b.AddThemeColorOverride("font_hover_color", Ink);
        b.AddThemeColorOverride("font_focus_color", Ink);
        b.AddThemeColorOverride("font_pressed_color", Ink);
        foreach (var (state, c, border) in new[] { ("normal", colour, 0), ("hover", colour.Lightened(0.15f), 0),
                     ("pressed", colour.Darkened(0.2f), 0), ("focus", colour.Lightened(0.1f), 6) })
        {
            var box = new StyleBoxFlat { BgColor = c, CornerDetail = 10, BorderColor = Ink };
            box.SetCornerRadiusAll(56);
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
        col.AddChild(UiKit.Spacer(expand: true));
        return col;
    }

    /// <summary>The travelling page: where to, and dots that go round while the ground there streams in.</summary>
    public void ShowTravel(string where)
    {
        SetText(_travelTitle, $"Travelling to {where}");
        ShowPage(Page.Travel);
    }

    public void ShowPage(Page page)
    {
        Shown = page;
        _home.Visible = page == Page.Home;
        _map.Visible = page == Page.Map;
        _contacts.Visible = page == Page.Contacts;
        _travel.Visible = page == Page.Travel;
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
        }
    }

    /// <summary>The time on both screens and the travelling dots, set only when they change.</summary>
    public void Tick(string clock, string date)
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
        if (Shown == Page.Travel) SetText(_travelDots, ((int)(RealClock.Now * 3) % 3) switch { 0 => "●  ○  ○", 1 => "○  ●  ○", _ => "○  ○  ●" });
    }

    // ---------------------------------------------------------------- bits

    private static TextureRect WallpaperRect()
    {
        var wall = new TextureRect
        {
            Texture = Wallpaper.Inner, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
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
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.35f));
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        return l;
    }

    private static Label Title(string text) => Big(text, 54);

    /// <summary>The page's two keys, named for the device in hand (a menu's accept and back, not the game's actions).</summary>
    private static Label Hint(string accept, string back)
    {
        var l = Big(InputHints.Pad ? $"A  {accept}      B  {back}" : $"Enter  {accept}      Esc  {back}", 22, InkDim);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        return l;
    }

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
