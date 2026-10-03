using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Player;

/// <summary>
/// The "what am I travelling as" picker, opened with R (pad: Y with nothing to interact with).
///
/// <para>
/// A menu rather than a cycle key, for two reasons: the list is meant to grow, and a refusal
/// needs somewhere to be explained. On a server, the vehicles in it (anything left in the world
/// when you get out) are an admin's to spawn — see <see cref="Permissions"/>; the cards stay
/// listed, greyed, so the reason is visible rather than the vehicles simply missing. You cannot get
/// on a bike while airborne or step off skis at 70 km/h, and a key that silently does nothing in
/// those moments reads as a broken key — so the panel says why and stays open.
/// </para>
///
/// <para>
/// <b>Layout</b> (#210): the menus' glass panel, sized to the window. Tabs (Mounts, Cars,
/// Motorbikes, Trucks and buses, Trailers) over a scrolling grid of cards, each with a thumbnail
/// rendered from the real model (<see cref="RideThumbs"/>); beside it a live <see cref="RideStage"/>
/// showing the selected card: its doors swing open and its lamps come on. A click selects (amber
/// border) and hovering never changes the selection, so the car preset and the load beside it can
/// be set before pressing Ride (or double-clicking the card; on a pad, focus selects and A rides).
/// Under the stage: the name, the blurb, the car preset or the load, and why a choice was refused.
/// </para>
///
/// <para>
/// <b>Folders</b> (#410): a tab can fold its cards into folders — the motorbikes by brand, then by
/// model when a model comes in several variants (Honda › Africa Twin › 28 bikes). A folder card opens
/// on a click (or A / Ride once selected); a Back card, Backspace or Esc / (B) go up a level.
/// </para>
///
/// <para>
/// It registers with <see cref="UiFocus"/> while open. That is not about text: <see cref="FootPlayer"/>
/// reads physical keys every frame, so without it the 1–9 shortcuts would arrive at the same
/// time as W and you would ride away while choosing.
/// </para>
/// </summary>
public partial class RideUi : CanvasLayer
{
    private const float CardW = 168, CardH = 162, ThumbW = 152, ThumbH = 93, Gap = 10;
    /// <summary>Picker cards for trailers carry this plus the trailer's index as their kind; never a real mount.</summary>
    private const int TrailerRow = 1000;
    private static readonly (string Name, float Load)[] Loads = { ("Empty", 0f), ("Half", 0.5f), ("Full", 1f) };

    private sealed class Card
    {
        public required RideKind Kind;
        public required string Label, Blurb, ThumbKey;
        public required bool Vehicle;
        public required Func<Node3D?> Build;
        public Button Button = null!;
        public TextureRect Thumb = null!;
        public Label Badge = null!;
        public StyleBox Normal = null!, Hot = null!, Picked = null!;
        public Label Name = null!;
        /// <summary>A folder card: what it opens. Its <see cref="Kind"/> means nothing.</summary>
        public Folder? Opens;
        /// <summary>The tab's "up a level" card.</summary>
        public bool IsBack;
        /// <summary>The folder this card is shown in.</summary>
        public Folder In = null!;
    }

    private sealed class Folder
    {
        public required string Name;
        public Folder? Parent;
        /// <summary>The cards shown inside, in order: rides and folder cards.</summary>
        public readonly List<Card> Items = new();
        /// <summary>Every ride under it, at any depth.</summary>
        public readonly List<Card> Rides = new();
        /// <summary>This folder's own card in its parent (null for a tab's root).</summary>
        public Card? Card;
    }

    private sealed class Tab
    {
        public required string Name;
        /// <summary>Every ride on the tab, whatever folder it is in.</summary>
        public required List<Card> Cards;
        public Folder Root = null!, Level = null!;
        /// <summary>The folder cards and the Back card.</summary>
        public readonly List<Card> Extra = new();
        public Card Back = null!;
        public Button Button = null!;
        public GridContainer Grid = null!;
        public ScrollContainer Scroll = null!;
    }

    private PanelContainer _panel = null!;
    private readonly List<Tab> _tabs = new();
    private int _tab;
    private Label _lockNote = null!, _hint = null!, _current = null!;
    private Label _name = null!, _blurb = null!, _status = null!;
    private Control _setupBox = null!, _loadBox = null!;
    /// <summary>The car preset (#40): put on the car being driven, and on any car picked from here.</summary>
    private OptionButton _setup = null!;
    private Label _setupBlurb = null!;
    private OptionButton _load = null!;
    private Button _go = null!;

    private RideThumbs _thumbs = null!;
    private RideStage _stage = null!;
    private TextureRect _stageView = null!;
    private Card? _shown;      // on the stage
    private Card? _selected;   // clicked or focused: on the stage, doors and lamps open, what Ride takes
    private float _juiceDelay;
    /// <summary>The turn a pointed-at vehicle swings to: nose and the driver's open door towards you.</summary>
    private const float PresentYaw = 0.35f;
    private float _spin;
    /// <summary>Seconds left of swinging the selected vehicle to face you; then the slow turntable.</summary>
    private float _present;
    private bool _dragging;

    /// <summary>Resolved per press, never captured: in multiplayer the player node is respawned.</summary>
    public Func<FootPlayer?>? ActivePlayer { get; set; }

    public bool IsOpen => _panel.Visible;

    public static RideUi Create() => new() { Name = "RideUi" };

    public override void _Ready()
    {
        Layer = 30;   // under the main menu, over the world

        _thumbs = new RideThumbs { Name = "Thumbs" };
        AddChild(_thumbs);
        _stage = RideStage.Create(new Vector2I(560, 340), live: true);
        AddChild(_stage);

        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);

        // themed on the panel's own root, never the Window (docs/notes/ui/style-guide.md)
        _panel = new PanelContainer { Visible = false, Theme = UiTheme.Get() };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.9f, 12, 20));
        centre.AddChild(_panel);

        var rows = UiKit.VBox(10);
        _panel.AddChild(rows);

        // ---- header: title, tabs, what you are on now ----
        var head = UiKit.HBox(16);
        head.AddChild(UiKit.Text("Travel as", UiTheme.FontHeading, UiTheme.Text, bold: true));
        var tabs = UiKit.HBox(4);
        tabs.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(tabs);
        _current = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _current.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        head.AddChild(_current);
        rows.AddChild(head);

        _lockNote = UiKit.Text("", UiTheme.FontSmall, UiTheme.Warn, wrap: true);
        _lockNote.Visible = false;
        rows.AddChild(_lockNote);

        // ---- body: cards on the left, the stage and the details on the right ----
        var body = UiKit.HBox(18);
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        rows.AddChild(body);

        var pages = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddChild(pages);

        var side = UiKit.VBox(8);
        side.CustomMinimumSize = new Vector2(320, 0);
        body.AddChild(side);
        BuildSide(side);
        BuildTabs(tabs, pages);

        _hint = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint, wrap: true);
        rows.AddChild(_hint);

        GetViewport().SizeChanged += Fit;
        Permissions.Changed += Relabel;
        PlayerInput.DeviceChanged += Relabel;
        Fit();
        SelectTab(0);
        Relabel();
    }

    public override void _ExitTree()
    {
        GetViewport().SizeChanged -= Fit;
        Permissions.Changed -= Relabel;
        PlayerInput.DeviceChanged -= Relabel;
    }

    // ------------------------------------------------------------------------------------
    // construction
    // ------------------------------------------------------------------------------------

    private static Card NewCard(RideKind kind, string label, string blurb, bool vehicle, string key, Func<Node3D?> build) =>
        new() { Kind = kind, Label = label, Blurb = blurb, Vehicle = vehicle, ThumbKey = key, Build = build };

    private void BuildTabs(HBoxContainer bar, Control pages)
    {
        var mounts = new List<Card>
        {
            NewCard(RideKind.OnFoot, "On foot",
                "{move_forward}{move_left}{move_back}{move_right} walk, {sprint} run, {jump} jump, {crouch_slide} slide, jump at a wall to kick off",
                false, "OnFoot", () => new MeshInstance3D
                {
                    Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(0)),
                    MaterialOverride = HumanMeshBuilder.FigureMaterial(),
                }),
        };
        foreach (var ride in Rideable.All)
        {
            var kind = ride.Kind;
            mounts.Add(NewCard(kind, ride.Label, ride.Blurb, ride.IsVehicle, $"{kind}|{ride.Label}",
                () => Rideable.Create(kind)?.BuildParkedVisual(0)));
        }
        AddTab(bar, pages, "Mounts", mounts);

        AddTab(bar, pages, "Cars", CarCatalog.All.Select(c => NewCard(c.Kind, c.Label, c.Blurb, true,
            $"{c.Kind}|{c}", () => Rideable.Create(c.Kind)?.BuildParkedVisual(0))).ToList());
        // brand folders, then a model folder where a model comes in several variants (#410)
        var family = MotorbikeCatalog.All.Where(b => b.Family != "").GroupBy(b => (b.Brand, b.Family)).ToDictionary(g => g.Key, g => g.Count());
        AddTab(bar, pages, "Motorbikes", MotorbikeCatalog.All.Select(b => NewCard(b.Kind, b.Label, b.Blurb, true,
            $"{b.Kind}|{b}", () => Rideable.Create(b.Kind)?.BuildParkedVisual(0))).ToList(),
            card => MotorbikeCatalog.For(card.Kind) is { } b && b.Brand != ""
                ? family.GetValueOrDefault((b.Brand, b.Family)) > 1 ? new[] { b.Brand, b.Family } : new[] { b.Brand }
                : Array.Empty<string>());
        AddTab(bar, pages, "Trucks and buses", HeavyCatalog.All.Select(h => NewCard(h.Kind, h.Label,
            h.Blurb + (h.Look.Operator.Length > 0 ? $" ({h.Look.Operator} colours)" : ""), true,
            $"{h.Kind}|{h}", () => HeavyRig.Create(h, 0, 0.5f))).ToList());
        // the boats (#302): picked on the water (or by it: it starts afloat at the surface)
        AddTab(bar, pages, "Boats", BoatCatalog.All.Select((b, i) => NewCard((RideKind)(BoatCatalog.First + i), b.Name,
            Rideable.Create((RideKind)(BoatCatalog.First + i))!.Blurb, true,
            $"{BoatCatalog.First + i}|{b.Name}", () => Rideable.Create((RideKind)(BoatCatalog.First + i))?.BuildParkedVisual(0))).ToList());
        // trailers are not mounts: each card couples one behind the truck being driven, or leaves it
        // in the world ahead to back onto (RideKind.Trailer + its index, decoded in Choose)
        AddTab(bar, pages, "Trailers", TrailerCatalog.All.Select((t, i) => NewCard((RideKind)(TrailerRow + i), t.Label,
            t.Blurb + (t.Operator.Length > 0 ? $" ({t.Operator} colours)" : ""), true,
            $"Trailer{i}|{t}", () => HeavyRig.CreateTrailer(t, 0, 0.5f))).ToList());
    }

    /// <param name="path">The folders a card goes in, outermost first; null or empty for none.</param>
    private void AddTab(HBoxContainer bar, Control pages, string name, List<Card> cards, Func<Card, string[]>? path = null)
    {
        var tab = new Tab { Name = name, Cards = cards };
        tab.Root = tab.Level = new Folder { Name = name };
        int index = _tabs.Count;
        tab.Button = TabButton($"{name}  {cards.Count}");
        tab.Button.Pressed += () => SelectTab(index);
        bar.AddChild(tab.Button);

        tab.Scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            Visible = false,
        };
        tab.Scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        pages.AddChild(tab.Scroll);
        tab.Grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        tab.Grid.AddThemeConstantOverride("h_separation", (int)Gap);
        tab.Grid.AddThemeConstantOverride("v_separation", (int)Gap);
        tab.Scroll.AddChild(UiKit.Margin(tab.Grid, 4, 4, 10, 4));   // room for the focus outline, the hover pop and the scrollbar
        tab.Back = new Card { Kind = RideKind.OnFoot, Label = "‹ Back", Blurb = "", Vehicle = false, ThumbKey = "", Build = () => null, IsBack = true, In = tab.Root };
        BuildCard(tab.Back);
        tab.Grid.AddChild(tab.Back.Button);
        tab.Extra.Add(tab.Back);
        // depth first, so each level's cards keep their order among the grid's children; a level is
        // shown by hiding everything else (ShowLevel)
        foreach (var card in cards)
        {
            var folder = tab.Root;
            folder.Rides.Add(card);
            foreach (var step in path?.Invoke(card) ?? Array.Empty<string>())
            {
                var inner = folder.Items.FirstOrDefault(c => c.Opens?.Name == step)?.Opens;
                if (inner == null)
                {
                    inner = new Folder { Name = step, Parent = folder };
                    // the folder wears its first ride's thumbnail
                    inner.Card = new Card { Kind = card.Kind, Label = step, Blurb = "", Vehicle = card.Vehicle, ThumbKey = card.ThumbKey, Build = card.Build, Opens = inner, In = folder };
                    folder.Items.Add(inner.Card);
                    BuildCard(inner.Card);
                    tab.Grid.AddChild(inner.Card.Button);
                    tab.Extra.Add(inner.Card);
                }
                folder = inner;
                folder.Rides.Add(card);
            }
            card.In = folder;
            folder.Items.Add(card);
            BuildCard(card);
            tab.Grid.AddChild(card.Button);
        }
        foreach (var c in tab.Extra)
            if (c.Opens is { } f)
            {
                int folders = f.Items.Count(i => i.Opens != null);
                c.Blurb = folders > 0 && folders == f.Items.Count
                    ? $"{f.Name}: {folders} models, {f.Rides.Count} in all. Open it to choose."
                    : $"{f.Name}: {f.Rides.Count} to choose from. Open it to see them.";
            }
        _tabs.Add(tab);
        ShowLevel(tab, tab.Root);
    }

    /// <summary>Shows one folder's cards on the tab (and Back, below the root); hides the rest.</summary>
    private static void ShowLevel(Tab tab, Folder folder)
    {
        tab.Level = folder;
        foreach (var c in tab.Cards) c.Button.Visible = c.In == folder;
        foreach (var c in tab.Extra) c.Button.Visible = c.IsBack ? folder.Parent != null : c.In == folder;
        tab.Back.Name.Text = folder.Parent == null ? "‹ Back" : $"‹ Back to {folder.Parent.Name}";
        tab.Back.Blurb = folder.Parent == null ? "" : $"Up to {folder.Parent.Name}.";
    }

    /// <summary>The cards on show, in order, without Back.</summary>
    private static IEnumerable<Card> Shown(Tab tab) => tab.Level.Items;

    /// <summary>A click on a folder or Back, A on a pad, Ride, a number key: open, go up, or ride.</summary>
    private void Activate(Card card)
    {
        var tab = _tabs[_tab];
        if (card.IsBack) { Up(); return; }
        if (card.Opens is { } folder)
        {
            ShowLevel(tab, folder);
            var first = folder.Items.FirstOrDefault(c => c.Opens == null) ?? folder.Items.FirstOrDefault();
            if (first != null) Select(first);
            if (PlayerInput.LastDevice == InputDevice.Gamepad) PlayerInput.FocusFirst(tab.Scroll);
            tab.Scroll.ScrollVertical = 0;
            return;
        }
        Select(card);
        Choose(card.Kind);
    }

    /// <summary>Up a level; false at a tab's root.</summary>
    private bool Up()
    {
        var tab = _tabs[_tab];
        if (tab.Level.Parent is not { } parent) return false;
        var from = tab.Level.Card;
        ShowLevel(tab, parent);
        if (from != null)
        {
            Select(from);
            if (PlayerInput.LastDevice == InputDevice.Gamepad) from.Button.GrabFocus();
        }
        return true;
    }

    /// <summary>Opens the folder a ride is in and selects it.</summary>
    private void Reveal(Tab tab, Card card)
    {
        ShowLevel(tab, card.In);
        Select(card);
    }

    private static Button TabButton(string text)
    {
        var b = new Button { Text = text, ToggleMode = true, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 32) };
        b.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        b.AddThemeStyleboxOverride("normal", UiTheme.Flat(new Color(1, 1, 1, 0), 8, 12, 6));
        b.AddThemeStyleboxOverride("hover", UiTheme.Flat(new Color(1, 1, 1, 0.06f), 8, 12, 6));
        b.AddThemeStyleboxOverride("pressed", UiTheme.Flat(new Color(UiTheme.Amber, 0.16f), 8, 12, 6));
        b.AddThemeStyleboxOverride("hover_pressed", UiTheme.Flat(new Color(UiTheme.Amber, 0.22f), 8, 12, 6));
        b.AddThemeColorOverride("font_color", UiTheme.TextDim);
        b.AddThemeColorOverride("font_hover_color", UiTheme.Text);
        b.AddThemeColorOverride("font_pressed_color", UiTheme.Amber);
        b.AddThemeColorOverride("font_hover_pressed_color", UiTheme.Amber);
        return b;
    }

    private void BuildCard(Card card)
    {
        var b = new Button
        {
            CustomMinimumSize = new Vector2(CardW, CardH),
            FocusMode = Control.FocusModeEnum.All,
            PivotOffset = new Vector2(CardW, CardH) * 0.5f,
        };
        var normal = UiTheme.Flat(new Color(0.10f, 0.115f, 0.14f, 0.7f), 10, 8, 8, new Color(1, 1, 1, 0.07f), 1);
        var hot = UiTheme.Flat(new Color(0.14f, 0.16f, 0.19f, 0.9f), 10, 8, 8, new Color(1, 1, 1, 0.3f), 1);
        var picked = UiTheme.Flat(new Color(0.17f, 0.16f, 0.13f, 0.95f), 10, 8, 8, UiTheme.Amber, 3);
        card.Normal = normal; card.Hot = hot; card.Picked = picked;
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hot);
        b.AddThemeStyleboxOverride("pressed", picked);
        b.AddThemeStyleboxOverride("hover_pressed", picked);
        b.AddThemeStyleboxOverride("focus", UiTheme.Flat(new Color(0, 0, 0, 0), 10, 8, 8, new Color(UiTheme.Amber, 0.5f), 1));
        b.AddThemeStyleboxOverride("disabled", normal);
        // the mouse never "presses" a card (that would ride on the first click); it selects through
        // GuiInput below. Enter / pad A still press it, and that rides the focused (= selected) card.
        b.ButtonMask = 0;

        var inside = UiKit.VBox(3);
        inside.MouseFilter = Control.MouseFilterEnum.Ignore;
        inside.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        inside.OffsetLeft = 8; inside.OffsetRight = -8; inside.OffsetTop = 8; inside.OffsetBottom = -6;
        b.AddChild(inside);

        card.Thumb = new TextureRect
        {
            CustomMinimumSize = new Vector2(ThumbW, ThumbH),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
        };
        inside.AddChild(card.Thumb);
        // two lines for the long names (motorbike generations), then an ellipsis
        var name = UiKit.Text(card.Label, UiTheme.FontSmall, UiTheme.Text, bold: true, wrap: true);
        name.MaxLinesVisible = 2;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.CustomMinimumSize = new Vector2(ThumbW, 0);
        inside.AddChild(name);
        card.Name = name;
        card.Badge = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint);
        inside.AddChild(card.Badge);

        b.Pressed += () => Activate(card);
        b.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb) return;
            // a folder (or Back) opens on one click: there is nothing to set before going in
            if (card.Opens != null || card.IsBack) { Activate(card); return; }
            Select(card);
            if (mb.DoubleClick) Choose(card.Kind);
        };
        b.FocusEntered += () => { if (!card.IsBack) Select(card); };
        // hover only pops the card; it never changes the selection or the stage
        b.MouseEntered += () => b.CreateTween().TweenProperty(b, "scale", Vector2.One * 1.04f, 0.12f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        b.MouseExited += () => b.CreateTween().TweenProperty(b, "scale", Vector2.One, 0.15f);
        card.Button = b;
    }

    private void BuildSide(VBoxContainer side)
    {
        // the stage: a live render of the card under the pointer
        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.07f, 0.08f, 0.10f, 1f), 10, 0, 0, new Color(1, 1, 1, 0.08f), 1));
        _stageView = new TextureRect
        {
            Texture = _stage.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            CustomMinimumSize = new Vector2(320, 194),
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = "Drag to turn it",
        };
        // dragging on the stage turns the vehicle round, like a showroom turntable
        _stageView.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) { _dragging = mb.Pressed; _present = 0; }
            else if (e is InputEventMouseMotion mm && _dragging) _stage.Yaw += mm.Relative.X * 0.012f;
        };
        frame.AddChild(_stageView);
        side.AddChild(frame);

        _name = UiKit.Text("", UiTheme.FontBody + 3, UiTheme.Text, bold: true, wrap: true);
        side.AddChild(_name);
        _blurb = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, wrap: true);
        _blurb.CustomMinimumSize = new Vector2(320, 0);
        side.AddChild(_blurb);

        // the car preset chooser, data-driven from CarSetups.All (#40); the garage (#56) calls
        // FootPlayer.SetCarSetup the same way
        var setup = UiKit.VBox(4);
        var setupRow = UiKit.HBox(10);
        setupRow.AddChild(UiKit.Text("Car preset", UiTheme.FontSmall, UiTheme.TextDim));
        _setup = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var s in CarSetups.All) _setup.AddItem(s.Name, s.Id);
        setupRow.AddChild(_setup);
        setup.AddChild(setupRow);
        _setupBlurb = UiKit.Text(CarSetups.All[0].Blurb, UiTheme.FontTiny, UiTheme.TextFaint, wrap: true);
        setup.AddChild(_setupBlurb);
        _setup.ItemSelected += i =>
        {
            var preset = CarSetups.For(_setup.GetItemId((int)i));
            _setupBlurb.Text = preset.Blurb;
            // in a car: on it now, if it is standing still
            if (ActivePlayer?.Invoke() is { Vehicle: Car } player)
                _status.Text = player.SetCarSetup(preset.Id) ? $"{preset.Name} fitted." : "Stop the car first.";
        };
        _setupBox = setup;
        side.AddChild(setup);

        // how full the next truck, bus or trailer comes: cargo, or passengers
        var load = UiKit.HBox(10);
        load.AddChild(UiKit.Text("Load", UiTheme.FontSmall, UiTheme.TextDim));
        _load = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var (name, _) in Loads) _load.AddItem(name);
        _load.Select(1);
        load.AddChild(_load);
        _loadBox = load;
        side.AddChild(load);

        side.AddChild(UiKit.Spacer(expand: true));
        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.Bad, wrap: true);
        side.AddChild(_status);
        _go = UiKit.Button("Ride", primary: true);
        _go.Pressed += () => { if (_selected != null) Activate(_selected); };
        side.AddChild(_go);
    }

    /// <summary>The panel fills most of the window, whatever its size, and the card grid reflows to it.</summary>
    private void Fit()
    {
        if (_panel == null || _stageView == null) return;
        var view = GetViewport().GetVisibleRect().Size;
        var size = new Vector2(Mathf.Min(view.X - 48, 1240), Mathf.Min(view.Y - 48, 780));
        _panel.CustomMinimumSize = size;
        _panel.Size = size;
        // the side column narrows on a small window; the grid takes the rest
        float side = Mathf.Clamp(size.X * 0.3f, 250, 360);
        _stageView.CustomMinimumSize = new Vector2(side, Mathf.Min(side * 0.6f, size.Y * 0.36f));
        _blurb.CustomMinimumSize = new Vector2(side, 0);
        float gridWidth = size.X - 40 - 18 - side - 14 - 16;   // margins, gap, the scrollbar
        int columns = Mathf.Max(1, (int)((gridWidth + Gap) / (CardW + Gap)));
        foreach (var tab in _tabs) tab.Grid.Columns = columns;
    }

    // ------------------------------------------------------------------------------------
    // state
    // ------------------------------------------------------------------------------------

    private void SelectTab(int index)
    {
        _tab = ((index % _tabs.Count) + _tabs.Count) % _tabs.Count;
        for (int i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].Scroll.Visible = i == _tab;
            _tabs[i].Button.SetPressedNoSignal(i == _tab);
        }
        var tab = _tabs[_tab];
        _setupBox.Visible = tab.Name == "Cars";
        _loadBox.Visible = tab.Name is "Trucks and buses" or "Trailers";
        _status.Text = "";
        if (!IsOpen) return;   // the stage and the thumbnails are drawn only while the menu is up
        RequestThumbs(tab);
        if (Shown(tab).FirstOrDefault() is { } first) Select(first);
        // a pad drives the cards by focus; a mouse by pointing, so focus would point at the first card unasked
        if (PlayerInput.LastDevice == InputDevice.Gamepad) PlayerInput.FocusFirst(tab.Scroll);
    }

    /// <summary>This tab's thumbnails, ahead of every other tab's; then the rest of the roster, in the background.</summary>
    private void RequestThumbs(Tab first)
    {
        foreach (var card in Shown(first).Reverse()) RequestThumb(card, first: true);
        foreach (var card in first.Cards) RequestThumb(card, first: false);
        foreach (var tab in _tabs)
            if (tab != first)
                foreach (var card in tab.Cards.Concat(tab.Extra.Where(c => !c.IsBack))) RequestThumb(card, first: false);
    }

    private void RequestThumb(Card card, bool first)
    {
        if (card.Thumb.Texture != null) return;
        _thumbs.Request(card.ThumbKey, card.Build, tex =>
        {
            if (card.Thumb.Texture != null) return;
            card.Thumb.Texture = tex;
            // fades in where it lands, so a roster filling up reads as arriving, not popping
            card.Thumb.CreateTween().TweenProperty(card.Thumb, "modulate:a", 1f, 0.25f);
        }, first);
    }

    /// <summary>Everything that names a key or depends on being an admin, redone when either changes.</summary>
    private void Relabel()
    {
        if (_hint == null) return;
        bool pad = PlayerInput.LastDevice == InputDevice.Gamepad;
        _hint.Text = InputHints.Format(pad
            ? "LB / RB switch tabs · (A) ride or open a folder · (B) up a folder, else closes · {ride_menu} closes. Vehicles stay where you get off ({interact_mount}); {interact_mount} next to one gets back in."
            : "Tab / Shift+Tab or click a tab · click a card to select it, then Ride (or double-click, or 1–9); a folder opens on a click, Backspace goes back · drag the preview to turn it · {ride_menu} / Esc closes. "
              + "Vehicles stay where you get off ({interact_mount}); {interact_mount} next to one gets back in.");

        bool locked = !Permissions.CanSpawnVehicles;
        _lockNote.Text = InputHints.Format(
            "Vehicles are spawned by an admin on this server. Walk up to one left in the world and press {interact_mount} to get in.");
        _lockNote.Visible = locked;

        var current = ActivePlayer?.Invoke()?.Ride ?? RideKind.OnFoot;
        string currentName = "On foot";
        foreach (var tab in _tabs)
            foreach (var card in tab.Cards)
            {
                bool here = card.Kind == current;
                if (here) currentName = card.Label;
                card.Button.Disabled = card.Vehicle && locked;
                card.Button.Modulate = card.Button.Disabled ? new Color(1, 1, 1, 0.45f) : Colors.White;
                card.Badge.Text = here ? "● Riding now" : card.Vehicle && locked ? "Admin only" : "";
                card.Badge.AddThemeColorOverride("font_color", here ? UiTheme.Amber : UiTheme.TextFaint);
            }
        foreach (var tab in _tabs)
            foreach (var card in tab.Extra)
                if (card.Opens is { } folder)
                {
                    bool here = folder.Rides.Any(c => c.Kind == current);
                    card.Button.Disabled = locked && folder.Rides.All(c => c.Vehicle);
                    card.Button.Modulate = card.Button.Disabled ? new Color(1, 1, 1, 0.45f) : Colors.White;
                    card.Badge.Text = here ? $"● Riding one · {folder.Rides.Count} ›" : $"{folder.Rides.Count} ›";
                    card.Badge.AddThemeColorOverride("font_color", here ? UiTheme.Amber : UiTheme.TextFaint);
                }
        _current.Text = $"Now: {currentName}";
        if (_shown != null) ShowText(_shown);
    }

    // ------------------------------------------------------------------------------------
    // the stage
    // ------------------------------------------------------------------------------------

    /// <summary>A card is clicked (or focused on a pad): amber border, on the stage, doors and lamps open.</summary>
    private void Select(Card card)
    {
        if (_selected == card) return;
        if (_selected != null) Mark(_selected, false);
        _selected = card;
        Mark(card, true);
        _status.Text = "";
        Show(card);
        _stage.Juice = 0;
        _juiceDelay = 0.12f;   // a beat for the eye to land before the doors swing
        _present = 2.5f;
    }

    private static void Mark(Card card, bool picked)
    {
        card.Button.AddThemeStyleboxOverride("normal", picked ? card.Picked : card.Normal);
        card.Button.AddThemeStyleboxOverride("hover", picked ? card.Picked : card.Hot);
        card.Button.AddThemeStyleboxOverride("disabled", picked ? card.Picked : card.Normal);
    }

    private void Show(Card card)
    {
        _shown = card;
        _stage.Show(card.Build());
        // starts at the thumbnail's angle and turns slowly from there
        _stage.Yaw = RideStage.ThumbYaw;
        _spin = 0;
        // a little drop onto the stage
        _stageView.PivotOffset = _stageView.Size * 0.5f;
        _stageView.Scale = Vector2.One * 0.94f;
        _stageView.CreateTween().TweenProperty(_stageView, "scale", Vector2.One, 0.25f)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        ShowText(card);
    }

    private void ShowText(Card card)
    {
        _name.Text = card.Label;
        _blurb.Text = InputHints.Format(card.Blurb);
        _go.Disabled = card.Button.Disabled;
        _go.Text = card.Opens != null ? "Open" : _tabs[_tab].Name == "Trailers" ? "Couple / leave ahead" : "Ride";
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        float dt = (float)delta;
        if (_selected != null && _juiceDelay > 0 && (_juiceDelay -= dt) <= 0) _stage.Juice = 1;
        if (_dragging) return;
        if (_selected != null && (_present -= dt) > 0)
        {
            // just selected: it swings round to show its face, the doors and the lamps
            float target = _stage.Yaw + Mathf.AngleDifference(_stage.Yaw, PresentYaw);
            _stage.Yaw = Mathf.Lerp(_stage.Yaw, target, MathX.Damp(4f, dt));
            _spin = 0;
        }
        else
        {
            // otherwise a slow turntable, eased in so it starts from where it stopped
            _spin = Mathf.MoveToward(_spin, 0.22f, dt * 0.15f);
            _stage.Yaw += _spin * dt;
        }
    }

    // ------------------------------------------------------------------------------------
    // choosing
    // ------------------------------------------------------------------------------------

    private void Choose(RideKind kind)
    {
        var player = ActivePlayer?.Invoke();
        if (player == null)
        {
            _status.Text = InputHints.Format("Nothing to mount — press {toggle_mode} to drop out of the fly camera first.");
            return;
        }

        bool vehicle = _tabs.Any(t => t.Cards.Any(c => c.Kind == kind && c.Vehicle));
        // the server refuses to park one anyway; saying so here beats a vehicle that vanishes
        if (vehicle && !Permissions.CanSpawnVehicles)
        {
            _status.Text = "Only an admin can spawn vehicles on this server.";
            return;
        }

        float load = Loads[Mathf.Clamp(_load.Selected, 0, Loads.Length - 1)].Load;
        if ((int)kind >= TrailerRow)
        {
            if (player.SpawnTrailer((int)kind - TrailerRow, load)) { Close(); return; }
            _status.Text = player.Vehicle is Truck t
                ? (t.Trailer != null ? "Uncouple the trailer you have first." : "That trailer does not fit this vehicle, or it is moving.")
                : "Get off first: a trailer is left 14 m ahead of you.";
            return;
        }
        if (kind == player.Ride)
        {
            _status.Text = "You are already on it.";
            return;
        }
        player.NextLoad = load;
        if (player.SetRide(kind))
        {
            if (CarCatalog.IsCar(kind)) player.SetCarSetup(_setup.GetSelectedId());
            Close();
            return;
        }

        // The refusal is the interesting case, so name the actual reason rather than "no".
        _status.Text = player.IsSliding
            ? "Not mid-slide."
            : player.IsOnFloor()
                ? "Too fast — slow down first."
                : "Not in the air.";
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (Permissions.RidesLocked) return;   // in a Battle Royale match you ride what you find
        // marks what you are already on, so the panel answers "what am I riding" too, and greys
        // the vehicles for a non-admin on a server
        Relabel();
        // the chooser shows what is on the car being driven
        if (ActivePlayer?.Invoke() is { Vehicle: Car } driver) _setup.Select(_setup.GetItemIndex(driver.CarSetupId));

        Fit();
        _panel.Visible = true;
        _stage.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        // open on the tab of what you are riding; a controller player drives the cards with the D-pad and A from there
        var current = ActivePlayer?.Invoke()?.Ride ?? RideKind.OnFoot;
        int tab = _tabs.FindIndex(t => t.Cards.Any(c => c.Kind == current));
        SelectTab(tab >= 0 ? tab : _tab);
        if (tab >= 0) Reveal(_tabs[tab], _tabs[tab].Cards.First(c => c.Kind == current));
        ApplyShotArgs();
    }

    /// <summary>"--ridemenu &lt;tab&gt; &lt;card&gt;" (0-based): opens on that tab with that card pointed at, for screenshots.</summary>
    private void ApplyShotArgs()
    {
        if (CmdArgs.Int("--ridemenu") is not int tab) return;
        SelectTab(tab);
        if (CmdArgs.Int("--ridemenu", 2) is int card && card < _tabs[_tab].Cards.Count)
            Reveal(_tabs[_tab], _tabs[_tab].Cards[card]);
    }

    public void Close()
    {
        _panel.Visible = false;
        _stage.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _stage.Juice = 0;
        if (_selected != null) Mark(_selected, false);
        _selected = null;
        _dragging = false;
        UiFocus.Set(this, false);
        Core.MouseCapture.Capture();
    }

    // _Input, ahead of the inventory's Tab: while this is open, Tab switches tabs here
    public override void _Input(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        int step = e switch
        {
            InputEventKey { PhysicalKeycode: Key.Tab, ShiftPressed: true } => -1,
            InputEventKey { PhysicalKeycode: Key.Tab } => 1,
            InputEventJoypadButton { ButtonIndex: JoyButton.LeftShoulder } => -1,
            InputEventJoypadButton { ButtonIndex: JoyButton.RightShoulder } => 1,
            _ => 0,
        };
        if (step == 0) return;
        SelectTab(_tab + step);
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // _UnhandledInput rather than _UnhandledKeyInput: a pad button is not a key event, and
        // Y / B / Start have to close this the way E and Esc do.
        if (!IsOpen || !@event.IsPressed() || @event.IsEcho()) return;

        // Esc / (B) and Backspace go up a folder first; from a tab's root Esc closes
        bool back = @event.IsActionPressed("ui_cancel") || @event is InputEventKey { PhysicalKeycode: Key.Backspace };
        if (back && Up())
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event.IsActionPressed(PlayerInput.RideMenu) || @event.IsActionPressed(PlayerInput.InteractMount)
            || @event.IsActionPressed(PlayerInput.Menu) || @event.IsActionPressed("ui_cancel"))
        {
            Close();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey key) return;

        // Key.Key1 is the physical "1", so the shortcuts land in the same place on an AZERTY
        // keyboard as on a QWERTY one — the same reason the movement keys are read physically.
        int index = (int)key.PhysicalKeycode - (int)Key.Key1;
        var cards = Shown(_tabs[_tab]).ToList();
        if (index < 0 || index >= Math.Min(9, cards.Count)) return;
        Activate(cards[index]);
        GetViewport().SetInputAsHandled();
    }
}
