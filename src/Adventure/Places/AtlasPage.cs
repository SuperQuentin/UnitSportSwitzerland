using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Places;

/// <summary>
/// The Swiss Atlas (#1224, places plan 7), the Journal's <b>Atlas</b> tab (J, then Q / E or LB / RB):
/// on the left the six chapters and their cantons on this map, each with a Landmarks bar (tier 1 and 2
/// found / this map's) and a Map bar (fog cells revealed / land cells); on the right the selected canton's
/// stamps: its landmarks first, then the notable places by kind, then the minor ones folded. A stamp opens
/// its card (<see cref="PlaceCard"/>). Built when the Journal opens and again on the <c>places</c>
/// section's change, never per frame. Docs: <c>docs/notes/adventure/atlas.md</c>.
/// </summary>
public partial class AtlasPage : Control
{
    public const string TabId = "atlas";

    /// <summary>Registers the tab (the places service, on clients). Shown while the adventure is on.</summary>
    public static void Register() =>
        AdventureLog.RegisterTab(TabId, "Atlas", 20, () => new AtlasPage(), () => AdventureLog.Adventuring && PlacesService.Instance != null);

    private static string? _canton;
    private static Poi? _pending;

    /// <summary>Opens the Journal on the Atlas at a place's card (the map's "Show in Atlas").</summary>
    public static void Open(Poi poi)
    {
        _pending = poi;
        if (poi.Canton.Length > 0) _canton = poi.Canton;
        if (GameShell.Instance?.Top is AdventureLog log) { log.ShowTab(TabId); return; }
        AdventureLog.Open(TabId);
    }

    private VBoxContainer _left = null!, _right = null!;
    private PlacesService? _places;
    private Dictionary<string, int> _land = new();

    /// <summary>The canton shown and the card open (checks).</summary>
    public string? Canton => _canton;
    public Poi? Card { get; private set; }

    public override void _Ready()
    {
        _places = PlacesService.Instance;
        var row = UiKit.HBox(16);
        row.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(row);
        var leftScroll = new ScrollContainer { CustomMinimumSize = new Vector2(330, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        row.AddChild(leftScroll);
        _left = UiKit.VBox(6);
        _left.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        leftScroll.AddChild(_left);
        var rightScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        row.AddChild(rightScroll);
        _right = UiKit.VBox(10);
        _right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightScroll.AddChild(_right);
        if (AdventureService.Instance is { } svc) svc.SectionChanged += OnSection;
        Build();
        if (_pending is { } poi) { _pending = null; ShowCard(poi); }
    }

    public override void _ExitTree()
    {
        if (AdventureService.Instance is { } svc) svc.SectionChanged -= OnSection;
    }

    private void OnSection(string id)
    {
        if (id != PlacesProgress.Section.Id) return;
        Build();
        if (Card != null) ShowCard(Card);
    }

    private PlacesProgress Mine => AdventureService.Instance?.Mine.Get(PlacesProgress.Section) ?? new PlacesProgress();

    private void Build()
    {
        foreach (var c in _left.GetChildren()) c.QueueFree();
        if (_places is not { } places || places.Catalogue.Count == 0)
        {
            _left.AddChild(UiKit.Text("No places on this map.", UiTheme.FontBody, UiTheme.TextDim));
            return;
        }
        var cat = places.Catalogue;
        var mine = Mine;
        _land = AtlasProgress.RevealedLand(places.MyFog, cat);
        var present = new HashSet<string>(cat.Cantons.Where(c => cat.InCanton(c).Count > 0));
        _canton ??= present.OrderByDescending(c => AtlasProgress.Landmarks(cat, mine, c).Found).FirstOrDefault();
        int stamps = mine.Found.Count;
        _left.AddChild(UiKit.Text($"{stamps} stamp{(stamps == 1 ? "" : "s")} · counted against this map", UiTheme.FontSmall, UiTheme.TextDim));
        Button? first = null;
        foreach (var (chapter, cantons) in AtlasChapters.All)
        {
            var here = cantons.Where(present.Contains).ToList();
            if (here.Count == 0) continue;
            int f = 0, t = 0;
            foreach (string c in here) { var (cf, ct) = AtlasProgress.Landmarks(cat, mine, c); f += cf; t += ct; }
            _left.AddChild(UiKit.Section($"{chapter}  {AtlasProgress.Percent(f, t)} %"));
            foreach (string c in here)
            {
                var b = CantonButton(c, cat, mine);
                first ??= b;
                _left.AddChild(b);
            }
        }
        ShowCanton(_canton);
    }

    private Button CantonButton(string code, PoiCatalogue cat, PlacesProgress mine)
    {
        var (found, total) = AtlasProgress.Landmarks(cat, mine, code);
        int land = cat.Totals(code).LandCells, seen = _land.GetValueOrDefault(code);
        var b = new Button { ToggleMode = false, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 58), Name = "Canton" + code };
        var box = UiKit.VBox(2);
        box.MouseFilter = MouseFilterEnum.Ignore;
        box.SetAnchorsPreset(LayoutPreset.FullRect);
        box.OffsetLeft = 10; box.OffsetRight = -10; box.OffsetTop = 4;
        var name = UiKit.Text($"{code}  {AtlasChapters.NameOf(code)}", UiTheme.FontBody, code == _canton ? UiTheme.Amber : UiTheme.Text, bold: true);
        name.MouseFilter = MouseFilterEnum.Ignore;
        box.AddChild(name);
        box.AddChild(Bar("Landmarks", found, total));
        box.AddChild(Bar("Map", seen, Math.Max(land, seen)));
        b.AddChild(box);
        b.Pressed += () => { _canton = code; Card = null; Build(); };
        return b;
    }

    private static Control Bar(string what, int value, int total)
    {
        var row = UiKit.HBox(6);
        row.MouseFilter = MouseFilterEnum.Ignore;
        var label = UiKit.Text(what, UiTheme.FontSmall, UiTheme.TextDim);
        label.CustomMinimumSize = new Vector2(70, 0);
        label.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(label);
        var bar = new ProgressBar { MinValue = 0, MaxValue = Math.Max(total, 1), Value = value, ShowPercentage = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 8), MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(bar);
        var count = UiKit.Text(total > 0 ? $"{value}/{total}" : "–", UiTheme.FontSmall, UiTheme.TextDim);
        count.CustomMinimumSize = new Vector2(64, 0);
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(count);
        return row;
    }

    private void Clear()
    {
        foreach (var c in _right.GetChildren()) c.QueueFree();
    }

    private void ShowCanton(string? code)
    {
        Clear();
        if (code == null || _places is not { } places) return;
        var cat = places.Catalogue;
        var mine = Mine;
        var fog = places.MyFog;
        _right.AddChild(UiKit.Text($"{AtlasChapters.NameOf(code)} ({code})", UiTheme.FontHeading, UiTheme.Text, bold: true));
        var all = cat.InCanton(code);
        var landmarks = all.Where(cat.IsLandmark).OrderByDescending(p => mine.Has(p)).ThenBy(p => p.Name).ToList();
        if (landmarks.Count > 0)
        {
            _right.AddChild(UiKit.Section("Landmarks"));
            var grid = new HFlowContainer();
            grid.AddThemeConstantOverride("h_separation", 10);
            grid.AddThemeConstantOverride("v_separation", 10);
            foreach (var poi in landmarks) grid.AddChild(StampButton(poi, mine, fog));
            _right.AddChild(grid);
        }
        // the notable places by kind: found ones as stamps, the rest counted (named when sighted)
        foreach (var group in all.Where(p => p.Tier == 2 && !cat.IsLandmark(p)).GroupBy(p => p.Kind).OrderBy(g => g.Key))
        {
            var list = group.ToList();
            int found = list.Count(mine.Has);
            _right.AddChild(UiKit.Section($"{Plural(group.Key)}  {found}/{list.Count}"));
            var grid = new HFlowContainer();
            grid.AddThemeConstantOverride("h_separation", 8);
            grid.AddThemeConstantOverride("v_separation", 8);
            foreach (var poi in list.Where(p => mine.Has(p) || fog.Revealed(p.E, p.N)).OrderByDescending(mine.Has).ThenBy(p => p.Name).Take(60))
                grid.AddChild(StampButton(poi, mine, fog));
            _right.AddChild(grid);
        }
        var minor = all.Where(p => p.Tier >= 3 && !cat.IsLandmark(p) && mine.Has(p)).OrderBy(p => p.Name).ToList();
        int minorTotal = all.Count(p => p.Tier >= 3 && !cat.IsLandmark(p));
        var fold = UiKit.Button($"Minor places: {minor.Count} of {minorTotal} stamped ▸");
        var minorBox = UiKit.VBox(2);
        minorBox.Visible = false;
        foreach (var poi in minor.Take(200))
        {
            var line = UiKit.Button($"{poi.Name} · {AtlasFacts.KindName(poi.Kind)}");
            line.Pressed += () => ShowCard(poi);
            minorBox.AddChild(line);
        }
        fold.Pressed += () => minorBox.Visible = !minorBox.Visible;
        _right.AddChild(fold);
        _right.AddChild(minorBox);
    }

    private Control StampButton(Poi poi, PlacesProgress mine, FogMask fog)
    {
        bool found = mine.Has(poi);
        var b = new Button { FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(104, 112), Flat = true, Name = "Stamp" };
        b.TooltipText = found ? PlaceCard.TitleOf(poi) : "Not found yet";
        var col = UiKit.VBox(2);
        col.MouseFilter = MouseFilterEnum.Ignore;
        col.SetAnchorsPreset(LayoutPreset.FullRect);
        var mark = new PlaceCard.StampMark(poi, mine.StampOf(poi)) { CustomMinimumSize = new Vector2(72, 72), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        col.AddChild(mark);
        // an unfound landmark keeps its name until sighted under revealed fog
        string name = found || fog.Revealed(poi.E, poi.N) ? PlaceCard.TitleOf(poi) : "?";
        var label = UiKit.Text(name, UiTheme.FontSmall, found ? UiTheme.Text : UiTheme.TextFaint, align: HorizontalAlignment.Center);
        label.ClipText = true;
        label.MouseFilter = MouseFilterEnum.Ignore;
        col.AddChild(label);
        b.AddChild(col);
        b.Pressed += () => ShowCard(poi);
        return b;
    }

    private void ShowCard(Poi poi)
    {
        if (_places is not { } places) return;
        Card = poi;
        Clear();
        var back = UiKit.Button($"◂ {AtlasChapters.NameOf(poi.Canton.Length > 0 ? poi.Canton : _canton ?? "")}");
        back.Pressed += () => { Card = null; ShowCanton(_canton); };
        _right.AddChild(back);
        _right.AddChild(PlaceCard.Build(poi, places.Catalogue, Mine, onMap: () =>
        {
            GameShell.Instance?.Back();
            AdventureMap.Open(poi);
        }));
        back.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static string Plural(PoiKind kind) => kind switch
    {
        PoiKind.Peak => "Summits", PoiKind.Pass => "Passes", PoiKind.SummitCross => "Summit crosses", PoiKind.Waterfall => "Waterfalls",
        PoiKind.Lake => "Lakes", PoiKind.LookoutTower => "Lookout towers", PoiKind.Church => "Churches", PoiKind.Chapel => "Chapels",
        PoiKind.Castle => "Castles", PoiKind.Bridge => "Covered bridges", PoiKind.Dam => "Dams", PoiKind.RailStation => "Stations",
        PoiKind.BusStop => "Bus stops", PoiKind.ShipLanding => "Boat landings", PoiKind.CableCarStation => "Cable cars",
        PoiKind.Hut => "Mountain huts", _ => kind.ToString(),
    };
}
