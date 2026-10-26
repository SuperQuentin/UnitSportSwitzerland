using System.Text;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Ui;

/// <summary>
/// The Journal's Quests tab (#1223 §5.1): on the left the quests grouped Running / Available / Done, on the
/// right the selected one's story, its steps (done ticked, the current one bright with its count, later
/// ones hidden unless revealed), its rewards and the buttons: Track (primary), Start, Hand over, and
/// Abandon apart, confirmed. The header: the title worn, how many done, and a warning when this server
/// keeps no progress. Rebuilt only when what it shows changes (a shape string, checked on
/// <see cref="AdventureService.SectionChanged"/> and once a second for the distance and the pack).
/// </summary>
public partial class QuestsPage : VBoxContainer
{
    private AdventureService? _service;
    private Label _header = null!, _status = null!;
    private VBoxContainer _list = null!, _detail = null!;
    private string? _selected;
    private string _shape = "";
    private double _tick;
    private readonly StringBuilder _sb = new();

    public override void _Ready()
    {
        _service = AdventureService.Instance;
        AddThemeConstantOverride("separation", 8);
        _header = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _header.ClipText = true;
        AddChild(_header);

        var split = UiKit.HBox(16);
        split.SizeFlagsVertical = SizeFlags.ExpandFill;
        AddChild(split);
        var (listScroll, list) = UiKit.ScrollPage(4);
        listScroll.CustomMinimumSize = new Vector2(320, 0);
        listScroll.SizeFlagsHorizontal = SizeFlags.Fill;
        _list = list;
        split.AddChild(listScroll);
        var (detailScroll, detail) = UiKit.ScrollPage(8);
        detailScroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _detail = detail;
        split.AddChild(UiKit.Card(detailScroll, 0.4f, 16));
        ((Control)detailScroll.GetParent()).SizeFlagsHorizontal = SizeFlags.ExpandFill;

        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _status.ClipText = true;
        AddChild(_status);

        if (_service != null)
        {
            _service.SectionChanged += OnSection;
            _service.UnsavedChanged += Soon;
            _service.Refused += OnRefused;
            _service.Noted += OnNoted;
        }
        Rebuild(force: true);
    }

    public override void _ExitTree()
    {
        if (_service == null) return;
        _service.SectionChanged -= OnSection;
        _service.UnsavedChanged -= Soon;
        _service.Refused -= OnRefused;
        _service.Noted -= OnNoted;
    }

    private void OnSection(string id) { if (id == QuestsProgress.Section.Id) Soon(); }
    private void Soon() => _tick = 0;

    private void OnRefused(string why)
    {
        _status.Text = why;
        _status.AddThemeColorOverride("font_color", UiTheme.Warn);
    }

    private void OnNoted(string questId, QuestChangeKind what, int step, int count)
    {
        _status.Text = what switch
        {
            QuestChangeKind.Started => $"Started: {Title(questId)}.",
            QuestChangeKind.Completed => $"Complete: {Title(questId)}.",
            QuestChangeKind.Abandoned => $"Abandoned: {Title(questId)}.",
            _ => _status.Text,
        };
        _status.AddThemeColorOverride("font_color", UiTheme.TextDim);
        Soon();
    }

    private string Title(string id) => _service?.Quests.Resolve(id)?.Title ?? id;

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || (_tick -= delta) > 0) return;
        _tick = 1.0;
        Rebuild(force: false);
    }

    // ---- what is shown -------------------------------------------------------------------------------

    private QuestsProgress Quests => _service?.Mine.Get(QuestsProgress.Section) ?? new QuestsProgress();

    private List<QuestDef> Running()
    {
        var list = new List<QuestDef>();
        if (_service == null) return list;
        foreach (var id in Quests.Active.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (_service.Quests.Resolve(id) is { } def) list.Add(def);
        return list;
    }

    private List<QuestDef> Available() => _service?.Quests.Available(Quests).OrderBy(q => q.Canton ?? "").ThenBy(q => q.Title).ToList() ?? new();

    private List<QuestDef> Done()
    {
        var list = new List<QuestDef>();
        if (_service == null) return list;
        foreach (var (id, _) in Quests.Done.OrderByDescending(kv => kv.Value))
            if (_service.Quests.Resolve(id) is { } def && !Quests.Active.ContainsKey(id)) list.Add(def);
        return list;
    }

    private void Rebuild(bool force)
    {
        var q = Quests;
        var running = Running();
        var available = Available();
        var done = Done();
        _selected ??= q.Tracked ?? running.FirstOrDefault()?.Id ?? available.FirstOrDefault()?.Id;
        // what the page shows, as a string: rebuilt only when it changes
        _sb.Clear();
        _sb.Append(_selected).Append('|').Append(q.Tracked).Append('|').Append(q.Title).Append('|').Append(_service?.Unsaved);
        foreach (var (id, s) in q.Active) _sb.Append('|').Append(id).Append(':').Append(s.Step).Append('/').Append(s.Count);
        foreach (var d in available) _sb.Append("|a:").Append(d.Id);
        foreach (var d in done) _sb.Append("|d:").Append(d.Id);
        _sb.Append('|').Append(HandOverState(out _));
        if (!force && _sb.Equals(_shape.AsSpan())) return;
        _shape = _sb.ToString();

        int doneCount = q.Done.Count;
        _header.Text = (q.Title is { } worn ? $"\"{worn}\" · " : "") + $"{doneCount} quest{(doneCount == 1 ? "" : "s")} done"
            + (_service?.Unsaved == true ? " · Progress not saved on this server: it ends with this session" : "");
        _header.AddThemeColorOverride("font_color", _service?.Unsaved == true ? UiTheme.Warn : UiTheme.TextDim);

        foreach (var c in _list.GetChildren()) c.QueueFree();
        foreach (var c in _detail.GetChildren()) c.QueueFree();
        if (running.Count + available.Count + done.Count == 0)
        {
            _list.AddChild(UiKit.Text("No quests yet.", UiTheme.FontBody, UiTheme.TextDim));
            _detail.AddChild(UiKit.Text("New quests arrive with the Swiss Atlas, travel points and chests: places to find, things to open, trials to fly.",
                UiTheme.FontBody, UiTheme.TextDim, wrap: true));
            return;
        }
        Group("Running", running, q);
        Group("Available", available, q);
        Group("Done", done, q);
        if (Find(_selected) is { } selected) Detail(selected, q);
    }

    private QuestDef? Find(string? id) => id == null ? null : _service?.Quests.Resolve(id);

    private void Group(string title, List<QuestDef> quests, QuestsProgress q)
    {
        if (quests.Count == 0) return;
        _list.AddChild(UiKit.Section(title));
        foreach (var def in quests)
        {
            bool selected = def.Id == _selected;
            string mark = q.Tracked == def.Id ? "◆ " : "";
            string where = def.Canton is { } c ? $"   {c}" : "";
            var row = new Button
            {
                Text = mark + def.Title + where, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.All,
                CustomMinimumSize = new Vector2(0, 40), ClipText = true, TooltipText = def.Blurb,
            };
            row.AddThemeStyleboxOverride("normal", UiTheme.Flat(selected ? new Color(UiTheme.Amber, 0.16f) : new Color(0, 0, 0, 0), 6, 10, 4));
            row.AddThemeStyleboxOverride("hover", UiTheme.Flat(new Color(UiTheme.Amber, 0.10f), 6, 10, 4));
            row.AddThemeStyleboxOverride("pressed", UiTheme.Flat(new Color(UiTheme.Amber, 0.22f), 6, 10, 4));
            if (selected) row.AddThemeColorOverride("font_color", UiTheme.Amber);
            string id = def.Id;
            row.Pressed += () =>
            {
                _selected = id;
                Rebuild(force: true);
            };
            _list.AddChild(row);
        }
    }

    private void Detail(QuestDef def, QuestsProgress q)
    {
        var title = UiKit.Text(def.Title, UiTheme.FontHeading, UiTheme.Text, bold: true);
        _detail.AddChild(title);
        if (def.Canton != null) _detail.AddChild(UiKit.Text($"Canton {def.Canton}", UiTheme.FontSmall, UiTheme.TextDim));
        _detail.AddChild(UiKit.Text(def.Blurb, UiTheme.FontBody, UiTheme.Text, wrap: true));

        _detail.AddChild(UiKit.Section("Steps"));
        bool running = q.Active.TryGetValue(def.Id, out var state);
        bool finished = !running && q.Done.ContainsKey(def.Id);
        for (int i = 0; i < def.Steps.Length; i++)
        {
            var step = def.Steps[i];
            bool past = finished || running && i < state!.Step;
            bool current = running && i == state!.Step;
            if (!past && !current && !step.Reveal && i > 0)
            {
                _detail.AddChild(UiKit.Text("…", UiTheme.FontBody, UiTheme.TextFaint));
                break;
            }
            string count = current && step.Count > 1 ? $"  {state!.Count}/{step.Count}" : "";
            // a tick, an arrow or a dot: never the colour alone
            string glyph = past ? "✓ " : current ? "▶ " : "· ";
            _detail.AddChild(UiKit.Text(glyph + step.Line + count, UiTheme.FontBody,
                past ? UiTheme.TextDim : current ? UiTheme.Amber : UiTheme.Text, bold: current, wrap: true));
        }

        if (def.Rewards.Length > 0)
        {
            _detail.AddChild(UiKit.Section("Rewards"));
            _detail.AddChild(UiKit.Text(string.Join(" · ", def.Rewards.Select(r => r.Describe())), UiTheme.FontBody, UiTheme.Text, wrap: true));
        }

        var buttons = UiKit.HBox(10);
        _detail.AddChild(UiKit.Spacer(8));
        _detail.AddChild(buttons);
        string id = def.Id;
        if (running)
        {
            bool tracked = q.Tracked == id;
            var track = UiKit.Button(tracked ? "Tracked" : "Track", primary: !tracked, minWidth: 120);
            track.Disabled = tracked;
            track.TooltipText = tracked ? "Shown on the screen's right edge" : "Show this quest on the screen's right edge";
            track.Pressed += () => _service?.AskTrack(id);
            buttons.AddChild(track);
            if (_service != null && state!.Step < def.Steps.Length && def.Steps[state.Step] is DeliverStep)
            {
                string? why = HandOverState(out string label);
                var hand = UiKit.Button(label, minWidth: 140);
                hand.Disabled = why != null;
                hand.TooltipText = why ?? "Give the items to finish this step";
                int step = state.Step;
                hand.Pressed += () => _service?.AskDeliver(id, step);
                buttons.AddChild(hand);
                if (why != null) _detail.AddChild(UiKit.Text(why, UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
            }
            buttons.AddChild(UiKit.Spacer(expand: true));
            // apart from the common buttons, in the warning colour, confirmed (ux-guidelines 8)
            var abandon = UiKit.Button("Abandon", minWidth: 110);
            abandon.AddThemeColorOverride("font_color", UiTheme.Bad);
            abandon.Pressed += () => Modal.Confirm(this, $"Abandon \"{def.Title}\"?",
                "Its progress is lost; you can start it again from the beginning.", "Abandon", () => _service?.AskAbandon(id), danger: true, cancelText: "Keep it");
            buttons.AddChild(abandon);
        }
        else if (!finished || def.Repeatable)
        {
            string? why = _service?.Quests.CanStart(q, def);
            var start = UiKit.Button("Start", primary: why == null, minWidth: 120);
            start.Disabled = why != null;
            start.TooltipText = why ?? "Start this quest and track it";
            start.Pressed += () => _service?.AskStart(id);
            buttons.AddChild(start);
            if (why != null) _detail.AddChild(UiKit.Text(why, UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        }
        else _detail.AddChild(UiKit.Text("Done.", UiTheme.FontBody, UiTheme.Good));
    }

    /// <summary>Why the selected quest's hand-over cannot happen now (null: it can); <paramref name="label"/> the button's text.</summary>
    private string? HandOverState(out string label)
    {
        label = "Hand over";
        if (_service == null || _selected == null || !Quests.Active.TryGetValue(_selected, out var state)
            || _service.Quests.Current(_selected, state) is not DeliverStep d) return "-";
        label = $"Hand over {d.Item}";
        if ((_service.Pack?.CountPlain(d.Item) ?? 0) < 1) return $"You have no {d.Item} in your pack.";
        if (_service.Quests.Locate(d.At) is { } at && _service.PositionOf?.Invoke(AdventureService.LocalPeer) is { } me)
        {
            double far = Math.Sqrt((me.E - at.E) * (me.E - at.E) + (me.N - at.N) * (me.N - at.N));
            if (far > at.RadiusM) return $"Hand it over at {(d.At is AreaTarget a ? a.Name : d.At.Id)} ({Math.Round(far - at.RadiusM):0} m away).";
        }
        return null;
    }
}
