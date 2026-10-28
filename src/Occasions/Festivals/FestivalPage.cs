using System.Globalization;
using Godot;
using UnitSport.Adventure;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// The Journal's <b>Festival</b> tab (#1269 section 1.3): for each festival that runs, where it is held, which day it is, the
/// day table (what is open, what opens tomorrow, the closing show), the party score with its milestones, and this
/// player's Batzen with what they are worth in francs; below, the festivals that are over (a card for what was converted).
/// Shown while a festival runs or the player has a festival record. Built when the Journal opens and when the service or the
/// section changes, never per frame. Read-only: there is nothing to press yet (the stall comes with a later part), so a pad
/// scrolls it with the stick and the D-pad like any page.
/// </summary>
public partial class FestivalPage : Control
{
    public const string TabId = "festival";

    public static void Register() =>
        AdventureLog.RegisterTab(TabId, "Festival", 35, () => new FestivalPage(),
            () => AdventureLog.Adventuring && FestivalService.Instance is { } s && (s.Running.Count > 0 || s.Mine.Instances.Count > 0));

    private VBoxContainer _rows = null!;
    private ScrollContainer _scroll = null!;

    /// <summary>The lines the page shows, in order (checks).</summary>
    public List<string> Lines { get; } = new();

    public override void _Ready()
    {
        (_scroll, _rows) = UiKit.ScrollPage(10);
        _scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        _scroll.FocusMode = FocusModeEnum.All;
        AddChild(_scroll);
        if (FestivalService.Instance is { } f)
        {
            f.Changed += Build;
            f.BoardChanged += OnBoard;
            f.Converted += OnConverted;
        }
        if (AdventureService.Instance is { } a) a.SectionChanged += OnSection;
        Build();
        _scroll.CallDeferred(Control.MethodName.GrabFocus);
    }

    public override void _ExitTree()
    {
        if (FestivalService.Instance is { } f)
        {
            f.Changed -= Build;
            f.BoardChanged -= OnBoard;
            f.Converted -= OnConverted;
        }
        if (AdventureService.Instance is { } a) a.SectionChanged -= OnSection;
    }

    private void OnBoard(string _) => Build();
    private void OnConverted(string _, int __, int ___) => Build();

    private void OnSection(string id)
    {
        if (id == FestivalBook.Section.Id) Build();
    }

    private void Build()
    {
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        Lines.Clear();
        if (FestivalService.Instance is not { } svc) return;
        var book = svc.Mine;
        foreach (var run in svc.Running) AddRunning(svc, book, run);

        var past = book.Instances.Where(i => svc.Running.All(r => r.Instance != i.Key)).OrderByDescending(i => i.Key, StringComparer.Ordinal).ToList();
        if (past.Count > 0)
        {
            Add(UiKit.Section("Past festivals"));
            foreach (var (instance, inst) in past) AddPast(instance, inst);
        }
        if (svc.Running.Count == 0 && past.Count == 0) Line("No festival now.", UiTheme.TextDim);
    }

    private void AddRunning(FestivalService svc, FestivalBook book, RunningFestival run)
    {
        var def = FestivalCatalog.Get(run.Id);
        var card = UiKit.VBox(6);
        Line(run.Title, UiTheme.FontHeading, UiTheme.Text, card, bold: true);
        string where = run.Site is { } s ? s.Text : "Not held in this region.";
        Line(where, UiTheme.TextDim, card);
        Line(FormattableString.Invariant($"Day {run.Day} of {run.Length} · until {Date(run.End)}"), UiTheme.Amber, card);
        if (def == null)
        {
            Add(UiKit.Card(card));
            return;
        }

        card.AddChild(UiKit.Section("Days"));
        Lines.Add("-- Days");
        foreach (var row in def.Days)
        {
            string games = string.Join(", ", row.Opens.Select(id => def.Game(id)?.Title ?? id));
            bool open = row.From <= run.Day;
            string state = open ? "open" : row.From == run.Day + 1 ? "tomorrow" : $"opens day {row.From}";
            Line($"Day {row.From} · {games} · {state}", open ? UiTheme.Good : UiTheme.TextDim, card);
        }
        if (def.Show != ClosingShow.None)
            Line($"Day {run.Length} evening · closing show: {Show(def.Show)}", run.Day >= run.Length ? UiTheme.Good : UiTheme.TextDim, card);

        card.AddChild(UiKit.Section("Party score"));
        Lines.Add("-- Party score");
        var view = svc.BoardOf(run.Id);
        int points = view?.Points ?? 0;
        if (def.Milestones.Length > 0)
        {
            var bar = new ProgressBar
            {
                MinValue = 0, MaxValue = def.Milestones[^1].Points, Value = Math.Min(points, def.Milestones[^1].Points),
                ShowPercentage = false, CustomMinimumSize = new Vector2(0, 10), MouseFilter = MouseFilterEnum.Ignore,
            };
            card.AddChild(bar);
        }
        Line(view == null || points == 0 ? "Play a game to start your crew's score." : FormattableString.Invariant($"{points} points"), UiTheme.Text, card);
        for (int i = 0; i < def.Milestones.Length; i++)
        {
            var m = def.Milestones[i];
            bool taken = view?.Taken.Contains(i) == true;
            Line(FormattableString.Invariant($"{m.Title} · {m.Points} points · {m.Batzen} Batzen to each who played · {(taken ? "reached" : "to reach")}"),
                taken ? UiTheme.Good : UiTheme.TextDim, card);
        }
        if (view is { Members.Length: > 0 })
            Line(string.Join(" · ", view.Members.Select(m => $"{m.Name} {m.Points}")), UiTheme.TextDim, card);

        card.AddChild(UiKit.Section("Your Batzen"));
        Lines.Add("-- Your Batzen");
        var inst = book.Instances.GetValueOrDefault(run.Instance) ?? new FestivalInstance();
        Line(FormattableString.Invariant($"{inst.Batzen} Batzen held · worth CHF {inst.Batzen} once the festival ends"), UiTheme.Text, card);
        Line(FormattableString.Invariant($"Earned from games: {inst.Earned} of {def.TokenCap}"), UiTheme.TextDim, card);
        foreach (var g in def.Games)
            if (inst.Best.TryGetValue(g.Id, out int best))
                Line(FormattableString.Invariant($"{g.Title}: best {best}"), UiTheme.TextDim, card);
        Add(UiKit.Card(card));
    }

    private void AddPast(string instance, FestivalInstance inst)
    {
        string title = FestivalCatalog.Get(inst.Festival)?.Title ?? inst.Festival;
        int year = FestivalCalendar.YearOf(instance);
        var card = UiKit.VBox(4);
        Line(FormattableString.Invariant($"{title} {year}"), UiTheme.FontBody, UiTheme.Text, card, bold: true);
        Line(inst.Converted
                ? FormattableString.Invariant($"Over. {inst.ConvertedChf} Batzen were paid into your account as CHF {inst.ConvertedChf}.")
                : FormattableString.Invariant($"{inst.Batzen} Batzen kept: they are paid into your account as francs once the festival is over."),
            inst.Converted ? UiTheme.Good : UiTheme.TextDim, card);
        Add(UiKit.Card(card));
    }

    private static string Date(DateOnly d) => d.ToString("d MMM", CultureInfo.InvariantCulture);

    private static string Show(ClosingShow show) => show switch
    {
        ClosingShow.Fireworks => "fireworks",
        ClosingShow.Concert => "a concert",
        ClosingShow.Both => "a concert and fireworks",
        ClosingShow.Bonfires => "bonfires",
        _ => "none",
    };

    private void Add(Control c) => _rows.AddChild(c);

    private void Line(string text, Color color, Container? into = null) => Line(text, UiTheme.FontBody, color, into);

    private void Line(string text, int size, Color color, Container? into = null, bool bold = false)
    {
        Lines.Add(text);
        var label = UiKit.Text(text, size, color, bold, wrap: true);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        (into ?? _rows).AddChild(label);
    }
}
