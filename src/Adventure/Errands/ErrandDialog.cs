using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// The dialogue with an errand giver (#1267 part 3, <c>ui/style-guide</c>, <c>ui/ux-guidelines</c>): bottom centre, a HUD glass panel
/// while open. The giver's line and name on top, then at most three choices, the primary one first: the card (a postcard with its
/// stamp, route and reward; "&lt;" and "&gt;" switch between two) or, at a hand-over, "Hand over ...", then "What's on around here?"
/// (the cell's other cards, each with Track) and the farewell in the giver's language. Keyboard: 1-3, Enter, Esc, Left / Right
/// between cards; pad: D-pad and A, B leaves, LB / RB between cards; touch and VR: the buttons. Rebuilt on a change only; the body
/// standing 4 m away closes it.
/// </summary>
public sealed partial class ErrandDialog : CanvasLayer
{
    public static ErrandDialog? Instance { get; private set; }

    private const float LeaveM = 4;

    private readonly Func<FootPlayer?> _me;
    private PanelContainer _root = null!;
    private VBoxContainer _col = null!;
    private Label _status = null!;
    private Giver? _giver;
    private int _index;
    private bool _info, _focused;
    private double _nextCheck;
    private readonly List<Button> _choices = new();
    private string _statusText = "";

    public ErrandDialog(Func<FootPlayer?> me)
    {
        _me = me;
        Name = "ErrandDialog";
        Layer = 9;
    }

    public ErrandDialog() : this(() => null) { }

    /// <summary>The dialogue is open (checks).</summary>
    public bool IsOpen => _root.Visible;
    public Giver? Giver => _giver;
    /// <summary>The choices' texts now (checks, screenshots).</summary>
    public IEnumerable<string> Choices => _choices.Select(b => b.Text);
    public string Status => _status.Text;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (_focused) UiFocus.Set(this, false);
        if (AdventureService.Instance is { } svc)
        {
            svc.SectionChanged -= OnSection;
            svc.Refused -= OnRefused;
        }
    }

    public override void _Ready()
    {
        _root = new PanelContainer { Theme = UiTheme.Get(), Visible = false, CustomMinimumSize = new Vector2(540, 0) };
        _root.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.82f, 12, 10));
        _root.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _root.GrowHorizontal = Control.GrowDirection.Both;
        _root.GrowVertical = Control.GrowDirection.Begin;
        _root.OffsetBottom = -96;
        AddChild(_root);
        _col = UiKit.VBox(6);
        _root.AddChild(_col);
        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, wrap: true);
        if (AdventureService.Instance is { } svc)
        {
            svc.SectionChanged += OnSection;
            svc.Refused += OnRefused;
        }
    }

    private void OnSection(string id)
    {
        if (_root.Visible && (id == ErrandsProgress.Section.Id || id == QuestsProgress.Section.Id)) Rebuild();
    }

    private void OnRefused(string why)
    {
        if (!_root.Visible) return;
        _statusText = why;
        _status.Text = why;
    }

    public void Open(Giver giver)
    {
        _giver = giver;
        _index = 0;
        _info = false;
        _statusText = "";
        _root.Visible = true;
        if (!_focused)
        {
            _focused = true;
            UiFocus.Set(this, true);
            MouseCapture.Mode = Input.MouseModeEnum.Visible;
        }
        Rebuild();
        GD.Print($"[errands] talking to {giver.Id}");
    }

    public void Close()
    {
        if (!_root.Visible) return;
        _root.Visible = false;
        _giver = null;
        if (_focused)
        {
            _focused = false;
            UiFocus.Set(this, false);
            MouseCapture.Capture();
        }
    }

    public override void _Process(double delta)
    {
        if (!_root.Visible || GameClock.Now < _nextCheck) return;
        _nextCheck = GameClock.Now + 0.25;
        if (_giver is { } g && _me() is { } me && IsInstanceValid(me))
        {
            var at = me.Global;
            if (Math.Sqrt((at.E - g.E) * (at.E - g.E) + (at.N - g.N) * (at.N - g.N)) > LeaveM) Close();
        }
    }

    public override void _Input(InputEvent e)
    {
        if (!_root.Visible) return;
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            int n = k.Keycode switch { Key.Key1 => 1, Key.Key2 => 2, Key.Key3 => 3, _ => 0 };
            if (n > 0 && n <= _choices.Count) { _choices[n - 1].EmitSignal(BaseButton.SignalName.Pressed); Handled(); }
            else if (k.Keycode == Key.Escape) { Close(); Handled(); }
            else if (k.Keycode == Key.Left) { Step(-1); Handled(); }
            else if (k.Keycode == Key.Right) { Step(1); Handled(); }
        }
        else if (e is InputEventJoypadButton { Pressed: true } j)
        {
            if (j.ButtonIndex == JoyButton.B) { Close(); Handled(); }
            else if (j.ButtonIndex == JoyButton.LeftShoulder) { Step(-1); Handled(); }
            else if (j.ButtonIndex == JoyButton.RightShoulder) { Step(1); Handled(); }
        }
    }

    private void Handled() => GetViewport().SetInputAsHandled();

    private void Step(int by)
    {
        if (_giver == null || ErrandService.Instance is not { } svc) return;
        int count = svc.CardsOf(_giver).Count;
        if (count < 2 || _info) return;
        _index = (_index + by + count) % count;
        Rebuild();
    }

    // ---- building the dialogue -------------------------------------------------------------------------

    private static string ItemName(ItemId id) => ItemDefs.Get(id)?.Name ?? Postkarte.Pretty(id);

    private void Rebuild()
    {
        foreach (var c in _col.GetChildren())
        {
            _col.RemoveChild(c);
            if (c != _status) c.QueueFree();
        }
        _choices.Clear();
        if (_giver is not { } g || ErrandService.Instance is not { } svc || AdventureService.Instance?.Mine is not { } mine) return;
        var errands = mine.Get(ErrandsProgress.Section);
        int level = errands.Level(g.Canton);
        string player = GameSettings.Current.PlayerName;

        var head = UiKit.HBox(10);
        var name = UiKit.Text(GiverNames.For(g.Id, g.Kind, g.Canton), UiTheme.FontBody, UiTheme.Text, bold: true);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(name);
        head.AddChild(UiKit.Text($"{g.Canton} · {Reputation.Name(level)}", UiTheme.FontSmall, UiTheme.TextDim));
        _col.AddChild(head);
        _col.AddChild(UiKit.Text(ErrandLines.Greeting(g, level, player), UiTheme.FontSmall, UiTheme.TextDim, wrap: true));

        var cards = svc.CardsOf(g);
        _index = cards.Count == 0 ? 0 : Math.Clamp(_index, 0, cards.Count - 1);
        if (_info) BuildInfo(g, svc, mine);
        else if (ErrandGivers.HandOver(mine, g) is { } hand)
        {
            var text = UiKit.Text($"{svc.Source.Find(hand.QuestId)?.Quest.Title ?? "Your errand"}: {hand.Left} {ItemName(hand.Deliver.Item)} wanted here.",
                UiTheme.FontSmall, UiTheme.Text, wrap: true);
            _col.AddChild(text);
            Choice($"1  Hand over {hand.Left} {ItemName(hand.Deliver.Item)}", true, () => AdventureService.Instance?.AskDeliver(hand.QuestId, hand.Step));
        }
        else if (cards.Count == 0)
        {
            _col.AddChild(UiKit.Text("No errands today. Come back tomorrow, or ask around.", UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        }
        else
        {
            var card = cards[_index];
            var state = ErrandService.StateOf(mine, card.Id);
            _col.AddChild(CardView(card, cards.Count > 1 ? $"{_index + 1} / {cards.Count}  (Left / Right)" : "", state));
            int taken = errands.Day == ErrandService.Today ? errands.Taken.Count : 0;
            bool open = state == ErrandCardState.Open;
            var take = Choice(open ? $"1  Take the card  ({taken} of {ErrandsProgress.DailyCards} taken today)" : $"1  {ErrandLines.StateText(state)}", open,
                () => svc.AskAccept(g.Id, card.Id));
            take.Disabled = !open;
        }

        Choice(_info ? "2  Back" : "2  What's on around here?", false, () => { _info = !_info; Rebuild(); });
        Choice($"3  {GiverNames.Farewell(g.Canton, g.Id)}", false, Close);
        _status.Text = _statusText;
        _col.AddChild(_status);
        // the primary choice first on the pad
        (_choices.FirstOrDefault(b => !b.Disabled) ?? _choices[0]).GrabFocus();
    }

    private Button Choice(string text, bool primary, Action pressed)
    {
        var b = UiKit.Button(text, primary);
        b.Alignment = HorizontalAlignment.Left;
        b.Pressed += () => pressed();
        _col.AddChild(b);
        _choices.Add(b);
        return b;
    }

    /// <summary>A postcard: its title, route, cargo and reward, and a stamp in the corner once taken.</summary>
    private static Control CardView(Offer card, string pager, ErrandCardState state)
    {
        var box = UiKit.VBox(2);
        var top = UiKit.HBox(8);
        var title = UiKit.Text(card.Quest.Title, UiTheme.FontBody, UiTheme.Amber, bold: true);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(title);
        if (pager.Length > 0) top.AddChild(UiKit.Text(pager, UiTheme.FontSmall, UiTheme.TextDim));
        if (state != ErrandCardState.Open) top.AddChild(UiKit.Text(ErrandLines.StateText(state).ToUpperInvariant(), UiTheme.FontSmall, UiTheme.Bad, bold: true));
        box.AddChild(top);
        box.AddChild(UiKit.Text(card.Quest.Blurb, UiTheme.FontSmall, UiTheme.Text, wrap: true));
        string carry = card.Cargo is { } c && card.CargoCount > 0 ? $"You carry {card.CargoCount} {ItemName(c)}.  " : "";
        box.AddChild(UiKit.Text(carry + ErrandLines.Reward(card), UiTheme.FontSmall, UiTheme.TextDim));
        return UiKit.Card(box, 0.45f);
    }

    private void BuildInfo(Giver g, ErrandService svc, PlayerProgress mine)
    {
        var (ce, cn) = g.Cell;
        var list = UiKit.VBox(3);
        int shown = 0;
        foreach (var card in svc.CardsIn(ce, cn))
        {
            var row = UiKit.HBox(8);
            var label = UiKit.Text($"{card.Quest.Title} — {card.Giver.Name}  ({ErrandLines.Reward(card)})", UiTheme.FontSmall, UiTheme.Text, wrap: true);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            var track = UiKit.Button("Track");
            var giver = card.Giver;
            track.Pressed += () =>
            {
                AdventureService.Instance?.TrackPlace(giver.Name, giver.E, giver.N, 12);
                _statusText = $"Tracking {giver.Name}.";
                _status.Text = _statusText;
            };
            row.AddChild(track);
            list.AddChild(row);
            shown++;
        }
        if (shown == 0) list.AddChild(UiKit.Text("Nothing on today in this part of the country.", UiTheme.FontSmall, UiTheme.TextDim));
        _col.AddChild(UiKit.Card(list, 0.45f));
    }
}
