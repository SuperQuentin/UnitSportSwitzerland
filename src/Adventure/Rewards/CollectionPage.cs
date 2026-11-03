using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Items;
using UnitSport.Ui;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The Journal's <b>Collection</b> tab (#1227, rewards plan "Persistence"): on the left six headings (Chests,
/// Edelweiss and cairns, Trials, Photos, Fauna, Odds), each with its count; on the right the one picked. Chests by
/// tier and canton; edelweiss held and offered and the stamina they bought; cairns per canton; each trial's medal,
/// best and the server's board; the photo spots taken; the animals photographed (ibex and chamois greyed); the
/// published odds of every tier at this player's pity. Built when the Journal opens and on its sections' change,
/// never per frame.
/// </summary>
public partial class CollectionPage : Control
{
    public const string TabId = "collection";

    public static void Register() =>
        AdventureLog.RegisterTab(TabId, "Collection", 30, () => new CollectionPage(), () => AdventureLog.Adventuring && RewardService.Instance != null);

    private static string _section = "chests";
    private VBoxContainer _left = null!, _right = null!;

    /// <summary>The section shown (checks).</summary>
    public string Section => _section;

    private static readonly string[] Sections = { "chests", "edelweiss", "trials", "photos", "fauna", "odds" };

    public override void _Ready()
    {
        var row = UiKit.HBox(16);
        row.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(row);
        _left = UiKit.VBox(6);
        _left.CustomMinimumSize = new Vector2(260, 0);
        row.AddChild(_left);
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        row.AddChild(scroll);
        _right = UiKit.VBox(8);
        _right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_right);
        if (AdventureService.Instance is { } svc) svc.SectionChanged += OnSection;
        if (RewardService.Instance is { } r) r.BoardChanged += OnBoard;
        Build();
    }

    public override void _ExitTree()
    {
        if (AdventureService.Instance is { } svc) svc.SectionChanged -= OnSection;
        if (RewardService.Instance is { } r) r.BoardChanged -= OnBoard;
    }

    private void OnSection(string id)
    {
        if (id is "chests" or "collect" or "trials" or "photos") Build();
    }

    private void OnBoard(string id)
    {
        if (_section == "trials") Build();
    }

    private static PlayerProgress? Mine => AdventureService.Instance?.Mine;

    private void Build()
    {
        foreach (var c in _left.GetChildren()) c.QueueFree();
        foreach (var c in _right.GetChildren()) c.QueueFree();
        var mine = Mine;
        if (mine == null) return;
        var chests = mine.Get(ChestsProgress.Section);
        var collect = mine.Get(CollectProgress.Section);
        var trials = mine.Get(TrialsProgress.Section);
        var photos = mine.Get(PhotosProgress.Section);
        foreach (string s in Sections)
        {
            string label = s switch
            {
                "chests" => $"Chests  {chests.Count}",
                "edelweiss" => $"Edelweiss and cairns  {collect.Picked}",
                "trials" => $"Trials  {trials.Best.Count}",
                "photos" => $"Photo spots  {photos.Taken.Count}",
                "fauna" => $"Fauna  {collect.Fauna.Count}",
                _ => "Odds",
            };
            var b = UiKit.Button(label);
            b.Name = "Section_" + s;
            b.Alignment = HorizontalAlignment.Left;
            if (s == _section) b.AddThemeColorOverride("font_color", UiTheme.Amber);
            string pick = s;
            b.Pressed += () => { _section = pick; Build(); };
            _left.AddChild(b);
        }
        switch (_section)
        {
            case "chests": ShowChests(chests); break;
            case "edelweiss": ShowCollect(collect); break;
            case "trials": ShowTrials(trials); break;
            case "photos": ShowPhotos(photos); break;
            case "fauna": ShowFauna(collect); break;
            default: ShowOdds(chests); break;
        }
    }

    private void Line(string text, Color? color = null, int size = UiTheme.FontBody) =>
        _right.AddChild(UiKit.Text(text, size, color ?? UiTheme.Text, wrap: true));

    private void ShowChests(ChestsProgress c)
    {
        _right.AddChild(UiKit.Section("Chests opened"));
        for (int t = 0; t < 4; t++)
        {
            var row = UiKit.HBox(8);
            var dot = UiKit.Text("■", UiTheme.FontBody, RewardMeshes.TierColor((ChestTier)t));
            row.AddChild(dot);
            row.AddChild(UiKit.Text($"{RewardMeshes.TierName((ChestTier)t)}: {c.ByTier[t]}", UiTheme.FontBody, UiTheme.Text));
            _right.AddChild(row);
        }
        Line("Luxurious chests come back every 1 August.", UiTheme.TextDim, UiTheme.FontSmall);
        if (c.ByCanton.Count > 0)
        {
            _right.AddChild(UiKit.Section("By canton"));
            Line(string.Join("   ", c.ByCanton.Where(kv => kv.Key.Length > 0).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        }
        if (c.Owned.Count > 0)
        {
            _right.AddChild(UiKit.Section("The Alpine set"));
            foreach (var id in ChestLoot.Cosmetics)
                Line($"{(c.Owned.Contains(id.ToString()) ? "✓" : "·")} {ItemDefs.Get(id)?.Name ?? id.ToString()}", c.Owned.Contains(id.ToString()) ? UiTheme.Text : UiTheme.TextFaint);
        }
    }

    private void ShowCollect(CollectProgress c)
    {
        _right.AddChild(UiKit.Section("Edelweiss"));
        Line($"Held: {c.Held}   Offered: {c.Offered}   Picked: {c.Picked}");
        Line($"Stamina bought: {Offering.StaminaSteps(c)} of {Offering.MaxSteps} steps (five edelweiss a step, at a summit cross or a chapel).", UiTheme.TextDim, UiTheme.FontSmall);
        _right.AddChild(UiKit.Section("Cairns"));
        if (c.Cairns.Count == 0) Line("No stone added yet: hold gather at a cairn (Steinmann) on a pass, a peak or by a hut.", UiTheme.TextDim, UiTheme.FontSmall);
        foreach (var (canton, n) in c.Cairns.OrderBy(kv => kv.Key)) Line($"{canton}: {n} of {Cairns.PerCanton}{(n >= Cairns.PerCanton ? "  ✓" : "")}");
    }

    private void ShowTrials(TrialsProgress t)
    {
        _right.AddChild(UiKit.Section("Trials"));
        if (t.Best.Count == 0) Line("No trial run yet: start one at a waymarked post by a chapel, a hut, a tower, a castle or a peak.", UiTheme.TextDim, UiTheme.FontSmall);
        var cat = PoiCatalogueHost.Current;
        foreach (var (id, best) in t.Best.OrderBy(kv => kv.Key))
        {
            string name = Trials.Resolve(id, cat)?.Name ?? id;
            Line($"{name}: {Trials.Clock(best.Seconds)}, {Trials.MedalName(best.Medal)} ({best.Runs} run{(best.Runs == 1 ? "" : "s")})");
            if (RewardService.Instance?.Boards.GetValueOrDefault(id) is { Count: > 0 } rows)
                for (int i = 0; i < rows.Count && i < 5; i++) Line($"   #{i + 1} {rows[i].Name}  {Trials.Clock(rows[i].Seconds)}", UiTheme.TextDim, UiTheme.FontSmall);
            else RewardService.Instance?.AskBoard(id);
        }
    }

    private void ShowPhotos(PhotosProgress p)
    {
        _right.AddChild(UiKit.Section($"Photo spots  {p.Taken.Count(id => PhotoSpots.Curated.Any(s => s.Id == id))} of {PhotoSpots.Curated.Length} famous views"));
        foreach (var s in PhotoSpots.Curated)
            Line($"{(p.Taken.Contains(s.Id) ? "✓" : "·")} {s.Name}", p.Taken.Contains(s.Id) ? UiTheme.Text : UiTheme.TextDim, UiTheme.FontSmall);
        int views = p.Taken.Count(id => PhotoSpots.Curated.All(s => s.Id != id));
        if (views > 0) Line($"And {views} view{(views == 1 ? "" : "s")} from viewpoints.", UiTheme.TextDim, UiTheme.FontSmall);
    }

    private void ShowFauna(CollectProgress c)
    {
        _right.AddChild(UiKit.Section("Fauna photographed"));
        if (c.Fauna.Count == 0) Line("Photograph a cow, a bird or a marmot near the middle of the frame, within 40 m.", UiTheme.TextDim, UiTheme.FontSmall);
        foreach (var (name, n) in c.Fauna.OrderBy(kv => kv.Key)) Line($"{name}  ×{n}");
        foreach (var name in Fauna.NotYet) Line($"{name}: not yet in Switzerland's game", UiTheme.TextFaint, UiTheme.FontSmall);
    }

    private static string Inv(FormattableString f) => FormattableString.Invariant(f);

    private void ShowOdds(ChestsProgress c)
    {
        _right.AddChild(UiKit.Section("Published odds, at your luck now"));
        Line("Every chest's contents are rolled for you alone when you open it. A cosmetic you have had becomes 5 Bergkristall.", UiTheme.TextDim, UiTheme.FontSmall);
        for (int t = 0; t < 4; t++)
        {
            var tier = (ChestTier)t;
            int pity = c.PityOf(tier);
            var (fmin, fmax) = ChestLoot.Francs(tier);
            _right.AddChild(UiKit.Text(RewardMeshes.TierName(tier), UiTheme.FontBody, RewardMeshes.TierColor(tier), bold: true));
            var parts = new List<string> { $"CHF {fmin}-{fmax} always" };
            double mat = ChestLoot.Materials.Sum(m => ChestLoot.Chance(tier, m, pity));
            parts.Add(Inv($"materials {mat:0.#} kinds on average"));
            parts.Add(Inv($"Bergkristall {ChestLoot.Chance(tier, ItemId.Bergkristall, pity):P0}"));
            double cos = ChestLoot.CosmeticChance(tier, pity);
            if (cos > 0) parts.Add(Inv($"a cosmetic {cos:P0}{(pity > 0 ? $" ({pity} without one)" : "")}"));
            double gad = ChestLoot.Gadgets.Sum(g => ChestLoot.Chance(tier, g, pity));
            if (gad > 0) parts.Add(Inv($"a gadget {gad:P0}"));
            Line(string.Join(" · ", parts), UiTheme.TextDim, UiTheme.FontSmall);
        }
    }
}

/// <summary>
/// The Journal's <b>Fauna</b> tab (#1227, rewards plan 7), beside Birds and Fish: the animals this player has
/// photographed (a cow, a bird or a marmot near the frame's middle, within 40 m), with ibex and chamois greyed until
/// a wildlife system exists. The same <c>collect.Fauna</c> data as the Collection tab.
/// </summary>
public partial class FaunaPage : Control
{
    public const string TabId = "fauna";

    public static void Register() =>
        AdventureLog.RegisterTab(TabId, "Fauna", 65, () => new FaunaPage(), () => AdventureLog.Adventuring && RewardService.Instance != null);

    private VBoxContainer _list = null!;

    public override void _Ready()
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(scroll);
        _list = UiKit.VBox(6);
        _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_list);
        if (AdventureService.Instance is { } svc) svc.SectionChanged += OnSection;
        Build();
    }

    public override void _ExitTree()
    {
        if (AdventureService.Instance is { } svc) svc.SectionChanged -= OnSection;
    }

    private void OnSection(string id)
    {
        if (id == CollectProgress.Section.Id) Build();
    }

    private void Build()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        var fauna = AdventureService.Instance?.Mine.Get(CollectProgress.Section).Fauna ?? new Dictionary<string, int>();
        _list.AddChild(UiKit.Section("Photographed"));
        if (fauna.Count == 0)
            _list.AddChild(UiKit.Text("Nothing yet: photograph a cow, a bird or a marmot near the middle of the frame, within 40 m.", UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        foreach (var (name, n) in fauna.OrderBy(kv => kv.Key))
            _list.AddChild(UiKit.Text($"✓ {name}  ×{n}", UiTheme.FontBody, UiTheme.Text));
        _list.AddChild(UiKit.Section("Not yet in Switzerland's game"));
        foreach (var name in Fauna.NotYet) _list.AddChild(UiKit.Text($"· {name}", UiTheme.FontBody, UiTheme.TextFaint));
    }
}
