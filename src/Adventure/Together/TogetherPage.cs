using Godot;
using UnitSport.Ui;

namespace UnitSport.Adventure.Together;

/// <summary>
/// The Journal's <b>Together</b> tab (#1266 part 7): the punch card's ten holes (a stamp is a small Swiss cross, drawn),
/// the cards filled and their titles, this week's minutes toward the week's chest, the distinct friends met, the mentor
/// table, and the pots shared. From the <c>together</c> section, rebuilt when it syncs.
/// </summary>
public partial class TogetherPage : Control
{
    public const string TabId = "together";

    public static void Register() =>
        AdventureLog.RegisterTab(TabId, "Together", 66, () => new TogetherPage(), () => AdventureLog.Adventuring && TogetherService.Instance != null);

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
        if (id == TogetherProgress.Section.Id) Build();
    }

    private void Build()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        var t = AdventureService.Instance?.Mine.Get(TogetherProgress.Section) ?? new TogetherProgress();
        _list.AddChild(UiKit.Section("Punch card"));
        _list.AddChild(new PunchCard { Stamps = t.Stamps, CustomMinimumSize = new Vector2(420, 52) });
        _list.AddChild(UiKit.Text($"{t.Stamps} of {TogetherProgress.StampsPerCard} stamps · {t.Cards} card{(t.Cards == 1 ? "" : "s")} filled. A day with 20 minutes together is a stamp; stamps never expire.",
            UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        var titles = new[] { 1, 3, 5, 10 }.Select(n => $"{(t.Cards >= n ? "✓" : "·")} card {n}: {TogetherProgress.CardTitle(n)}{(n == 10 ? " and the Stammgast hat" : "")}");
        foreach (var line in titles) _list.AddChild(UiKit.Text(line, UiTheme.FontSmall, UiTheme.Text));
        _list.AddChild(UiKit.Section("This week"));
        int minutes = t.WeekSeconds / 60;
        _list.AddChild(UiKit.Text(t.WeekChest ? "This week's chest is yours. Another next Monday." : $"{minutes} of 30 minutes together toward this week's chest.", UiTheme.FontBody, UiTheme.Text));
        _list.AddChild(UiKit.Text($"Friends met: {t.Friends.Count} · fondues shared: {t.Fondues} ({t.Splits} split, {t.BreadLost} breads lost)", UiTheme.FontSmall, UiTheme.TextDim));
        _list.AddChild(UiKit.Section("Mentors"));
        foreach (var m in MentorRules.All)
        {
            int done = t.Done.GetValueOrDefault(m.Id), guided = t.Mentored.GetValueOrDefault(m.Id);
            string state = done >= MentorRules.VeteranTimes ? $"veteran ({done}×): guided {guided}" : done > 0 ? $"done {done}×" : "not yet";
            _list.AddChild(UiKit.Text($"{m.Name}: {state}", UiTheme.FontSmall, done >= MentorRules.VeteranTimes ? UiTheme.Amber : UiTheme.Text));
        }
        _list.AddChild(UiKit.Text("A veteran (3 times) who shows a newcomer their first gets a cosmetic of their own, and so does the newcomer.", UiTheme.FontSmall, UiTheme.TextFaint, wrap: true));
    }

    /// <summary>Ten holes; a stamp is a red disc with a white Swiss cross. Drawn once per build.</summary>
    private sealed partial class PunchCard : Control
    {
        public int Stamps;

        public override void _Draw()
        {
            float r = Mathf.Min(18f, Size.Y * 0.4f);
            for (int i = 0; i < TogetherProgress.StampsPerCard; i++)
            {
                var c = new Vector2(r + 4 + i * (r * 2 + 6), Size.Y / 2);
                if (i < Stamps)
                {
                    DrawCircle(c, r, new Color("c8202a"));
                    float a = r * 0.55f, b = r * 0.18f;
                    DrawRect(new Rect2(c.X - a, c.Y - b, a * 2, b * 2), Colors.White);
                    DrawRect(new Rect2(c.X - b, c.Y - a, b * 2, a * 2), Colors.White);
                }
                else DrawArc(c, r, 0, Mathf.Tau, 24, new Color(1, 1, 1, 0.35f), 2f);
            }
        }
    }
}
