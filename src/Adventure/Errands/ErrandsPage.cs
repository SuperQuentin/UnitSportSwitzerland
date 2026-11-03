using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Ui;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// The Journal's <b>Errands</b> tab (#1267 part 8): today's cards in the player's own cell (taken, open, done) with Track to the
/// giver and "2 of 3 taken today", the running cards with Share with party, the cards friends shared that can be joined, and the
/// reputation in each canton. Rebuilt when the <c>errands</c> or <c>quests</c> sections sync or a card is shared, never per frame.
/// </summary>
public partial class ErrandsPage : Control
{
    public const string TabId = "errands";

    /// <summary>Where the local player is (LV95), set by the client world; null away from a body.</summary>
    public static Func<GlobalPos?>? Where { get; set; }

    public static void Register() =>
        AdventureLog.RegisterTab(TabId, "Errands", 15, () => new ErrandsPage(), () => AdventureLog.Adventuring && ErrandService.Instance != null);

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
        if (ErrandService.Instance is { } errands) errands.CardShared += OnShared;
        Build();
    }

    public override void _ExitTree()
    {
        if (AdventureService.Instance is { } svc) svc.SectionChanged -= OnSection;
        if (ErrandService.Instance is { } errands) errands.CardShared -= OnShared;
    }

    private void OnSection(string id)
    {
        if (id == ErrandsProgress.Section.Id || id == QuestsProgress.Section.Id) Build();
    }

    private void OnShared(string a, long b, string c, string d) => Build();

    private void Build()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        if (AdventureService.Instance is not { } adventure || ErrandService.Instance is not { } svc) return;
        var mine = adventure.Mine;
        var errands = mine.Get(ErrandsProgress.Section);
        var quests = mine.Get(QuestsProgress.Section);
        int taken = errands.Day == ErrandService.Today ? errands.Taken.Count : 0;

        _list.AddChild(UiKit.Section("Today"));
        _list.AddChild(UiKit.Text($"{taken} of {ErrandsProgress.DailyCards} cards taken today. Cards turn over at midnight, Swiss time, and are the same for everyone in the same place.",
            UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        if (Where?.Invoke() is { } at)
        {
            var (ce, cn) = Postkarte.CellOf(at.E, at.N);
            var cards = svc.CardsIn(ce, cn);
            if (cards.Count == 0) _list.AddChild(UiKit.Text("No givers in this part of the country. Look for a post office, an alp dairy, a hut or a station.", UiTheme.FontSmall, UiTheme.TextFaint, wrap: true));
            foreach (var card in cards) _list.AddChild(CardRow(card, ErrandService.StateOf(mine, card.Id)));
        }
        else _list.AddChild(UiKit.Text("Step out of your vehicle to see the cards around you.", UiTheme.FontSmall, UiTheme.TextFaint));
        // near the friends: the cards of every party member's cell (a card is still taken at its giver; "go to where Lea is" is a plan)
        if (Net.Parties.Client is { InParty: true } party && Navigation.AdventureCompass.PeerAt != null)
        {
            long self = Multiplayer.GetUniqueId();
            var seen = new HashSet<(int, int)>();
            if (Where?.Invoke() is { } here) seen.Add(Postkarte.CellOf(here.E, here.N));
            foreach (var (peer, name) in party.Members)
            {
                if (peer == self || Navigation.AdventureCompass.PeerAt(peer) is not { } far) continue;
                var cell = Postkarte.CellOf(far.E, far.N);
                if (!seen.Add(cell)) continue;
                var theirs = svc.CardsIn(cell.E, cell.N);
                if (theirs.Count == 0) continue;
                _list.AddChild(UiKit.Section($"Near {name}"));
                foreach (var card in theirs) _list.AddChild(CardRow(card, ErrandService.StateOf(mine, card.Id)));
            }
        }

        _list.AddChild(UiKit.Section("Running"));
        int running = 0;
        foreach (string id in quests.Active.Keys.Where(Postkarte.IsCard).OrderBy(k => k, StringComparer.Ordinal))
        {
            running++;
            var offer = svc.Source.Find(id);
            var row = UiKit.HBox(8);
            var label = UiKit.Text($"{offer?.Quest.Title ?? id}", UiTheme.FontSmall, UiTheme.Text, wrap: true);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            var share = UiKit.Button("Share with party");
            share.Pressed += () => svc.AskShare(id);
            row.AddChild(share);
            _list.AddChild(row);
        }
        if (running == 0) _list.AddChild(UiKit.Text("No card running.", UiTheme.FontSmall, UiTheme.TextFaint));
        foreach (var (card, from, name, title) in svc.SharedCards.ToList())
        {
            if (quests.Active.ContainsKey(card) || quests.Done.ContainsKey(card)) continue;
            var row = UiKit.HBox(8);
            var label = UiKit.Text($"Shared by {name}: {title}", UiTheme.FontSmall, UiTheme.Amber, wrap: true);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            var join = UiKit.Button("Join", primary: true);
            join.Pressed += () => svc.AskJoin(card, from);
            row.AddChild(join);
            _list.AddChild(row);
        }

        _list.AddChild(UiKit.Section("Reputation"));
        var reps = errands.Rep.Where(r => r.Value > 0).OrderByDescending(r => r.Value).ToList();
        if (reps.Count == 0) _list.AddChild(UiKit.Text("Do errands for a canton's givers and the canton gets to know you.", UiTheme.FontSmall, UiTheme.TextFaint, wrap: true));
        foreach (var (canton, points) in reps)
        {
            int level = Reputation.Level(points);
            string next = Reputation.NextAt(points) is { } n ? $" ({points}/{n})" : $" ({points})";
            _list.AddChild(UiKit.Text($"{Places.AtlasChapters.NameOf(canton)}: {Reputation.Name(level)}{next}", UiTheme.FontSmall, level >= 3 ? UiTheme.Amber : UiTheme.Text));
        }
        _list.AddChild(UiKit.Text($"{errands.Done} cards done, {errands.Together} of them with friends.", UiTheme.FontSmall, UiTheme.TextDim));
    }

    private Control CardRow(Offer card, ErrandCardState state)
    {
        var box = UiKit.VBox(2);
        var top = UiKit.HBox(8);
        var title = UiKit.Text(card.Quest.Title, UiTheme.FontSmall, state == ErrandCardState.Open ? UiTheme.Amber : UiTheme.TextDim, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(title);
        if (state != ErrandCardState.Open) top.AddChild(UiKit.Text(ErrandLines.StateText(state), UiTheme.FontSmall, UiTheme.TextDim));
        var giver = card.Giver;
        var track = UiKit.Button("Track");
        track.Pressed += () => AdventureService.Instance?.TrackPlace(giver.Name, giver.E, giver.N, 12);
        top.AddChild(track);
        box.AddChild(top);
        box.AddChild(UiKit.Text($"{giver.Name} · {ErrandLines.Reward(card)}", UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        return UiKit.Card(box, 0.4f);
    }
}
