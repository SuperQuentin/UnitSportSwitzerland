using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Ui;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The chest's reveal (#1227, rewards plan 2): a card at the left of the screen in the tier's colour, the francs, then
/// the items fanned in one by one over 2.2 s with the tier's jingle; any key or button skips to the end, and it fades
/// 3 s after. The odds are one line away: "Odds: Journal › Collection" (<see cref="CollectionPage"/>). Not a modal:
/// the player keeps moving. Built once; the rows are rebuilt per reveal, never per frame.
/// </summary>
public partial class ChestRevealUi : CanvasLayer
{
    public const double FanSeconds = 2.2, HoldSeconds = 3.0;

    private PanelContainer _panel = null!;
    private Label _title = null!, _francs = null!, _note = null!;
    private HBoxContainer _cards = null!;
    private AudioStreamPlayer _player = null!;
    private readonly List<Control> _fan = new();
    private double _shownAt = -1;
    private int _fanned;
    private readonly Queue<(string Id, List<LootLine> Items, ChestTier Tier, int Francs, string Note)> _queue = new();

    public static ChestRevealUi? Instance { get; private set; }

    /// <summary>The last reveal's title and lines (checks).</summary>
    public string Last { get; private set; } = "";
    public bool Showing => _shownAt >= 0;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (RewardService.Instance is { } r) r.ChestOpened -= OnOpened;
    }

    public override void _Ready()
    {
        Name = "ChestReveal";
        Layer = 9;
        var root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        // the left of the screen, half way down: the top centre is the quest and stamp toasts', the bottom the hotbar's
        var top = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = 24, OffsetRight = 24 + 470, OffsetTop = -150, OffsetBottom = 150 };
        root.AddChild(top);
        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.82f, 12, 16));
        top.AddChild(_panel);
        var box = UiKit.VBox(8);
        box.Alignment = BoxContainer.AlignmentMode.Center;
        _panel.AddChild(box);
        _title = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(_title);
        _francs = UiKit.Text("", UiTheme.FontBody, UiTheme.Amber);
        _francs.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(_francs);
        _cards = UiKit.HBox(8);
        _cards.Alignment = BoxContainer.AlignmentMode.Center;
        box.AddChild(_cards);
        _note = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _note.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(_note);
        _player = new AudioStreamPlayer { Bus = SfxBus.Name, VolumeDb = -6 };
        AddChild(_player);
        if (RewardService.Instance is { } r) r.ChestOpened += OnOpened;
    }

    private void OnOpened(string id, List<LootLine> items, ChestTier tier, int francs, string note)
    {
        _queue.Enqueue((id, items, tier, francs, note));
        if (!Showing) ShowNext();
    }

    private void ShowNext()
    {
        if (!_queue.TryDequeue(out var r)) return;
        string what = r.Id.StartsWith("ch:", StringComparison.Ordinal) ? RewardMeshes.TierName(r.Tier)
            : r.Id.StartsWith("offering:", StringComparison.Ordinal) ? "The mountain thanks you"
            : r.Id.StartsWith("photo:", StringComparison.Ordinal) ? "A view for the album"
            : r.Id.StartsWith("cairns:", StringComparison.Ordinal) ? "Every cairn of the canton"
            : r.Id.StartsWith("trial", StringComparison.Ordinal) ? "Trial reward" : RewardMeshes.TierName(r.Tier);
        _title.Text = what;
        _title.AddThemeColorOverride("font_color", RewardMeshes.TierColor(r.Tier));
        _francs.Text = $"CHF {r.Francs} to your account";
        _note.Text = (r.Note.Length > 0 ? char.ToUpperInvariant(r.Note[0]) + r.Note[1..] + " · " : "") + "Odds: Journal › Collection";
        foreach (var c in _fan) c.QueueFree();
        _fan.Clear();
        foreach (var line in r.Items)
        {
            var card = Card(line);
            card.Modulate = new Color(1, 1, 1, 0);
            _cards.AddChild(card);
            _fan.Add(card);
        }
        Last = $"{what}: CHF {r.Francs}, {string.Join(", ", r.Items.Select(l => $"{l.Count} {ItemDefs.Get(l.Item)?.Name ?? l.Item.ToString()}"))}{(r.Note.Length > 0 ? $" ({r.Note})" : "")}";
        _panel.Visible = true;
        _panel.Modulate = Colors.White;
        _shownAt = GameClock.Now;
        _fanned = 0;
        _player.Stream = RewardSounds.Jingle(r.Tier);
        _player.Play();
    }

    private static Control Card(LootLine line)
    {
        var def = ItemDefs.Get(line.Item);
        var card = new PanelContainer { CustomMinimumSize = new Vector2(84, 96), MouseFilter = Control.MouseFilterEnum.Ignore };
        card.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(1, 1, 1, 0.06f), 8, 6, 6));
        var v = UiKit.VBox(2);
        v.Alignment = BoxContainer.AlignmentMode.Center;
        card.AddChild(v);
        if (ItemIcons.Get(line.Item) is { } icon)
            v.AddChild(new TextureRect { Texture = icon, CustomMinimumSize = new Vector2(40, 40), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Ignore });
        var name = UiKit.Text(def?.Name ?? line.Item.ToString(), UiTheme.FontTiny, UiTheme.Text, wrap: true);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.CustomMinimumSize = new Vector2(76, 0);
        v.AddChild(name);
        var count = UiKit.Text($"× {line.Count}", UiTheme.FontSmall, UiTheme.Amber, bold: true);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(count);
        return card;
    }

    public override void _Process(double delta)
    {
        if (_shownAt < 0) return;
        double t = GameClock.Now - _shownAt;
        // fan the cards in, one after another, over FanSeconds
        int due = _fan.Count == 0 ? 0 : Math.Min(_fan.Count, (int)Math.Ceiling(t / (FanSeconds / _fan.Count)));
        while (_fanned < due)
        {
            var c = _fan[_fanned++];
            c.PivotOffset = c.Size / 2;
            c.Scale = new Vector2(0.6f, 0.6f);
            var tw = c.CreateTween().SetParallel();
            tw.TweenProperty(c, "modulate:a", 1f, 0.18f);
            tw.TweenProperty(c, "scale", Vector2.One, 0.22f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }
        if (t > FanSeconds + HoldSeconds)
        {
            _panel.Modulate = new Color(1, 1, 1, (float)Math.Max(0, 1 - (t - FanSeconds - HoldSeconds) / 0.6));
            if (t > FanSeconds + HoldSeconds + 0.6) Close();
        }
    }

    public override void _Input(InputEvent e)
    {
        if (_shownAt < 0 || !e.IsPressed() || e.IsEcho() || e is InputEventMouseMotion) return;
        if (e is not (InputEventKey or InputEventJoypadButton or InputEventMouseButton)) return;
        double t = GameClock.Now - _shownAt;
        // the first press skips the fan; one after it closes the card (the key still does its own job)
        if (t < FanSeconds) _shownAt = GameClock.Now - FanSeconds;
        else if (t > FanSeconds + 0.3) _shownAt = GameClock.Now - FanSeconds - HoldSeconds;
    }

    private void Close()
    {
        _panel.Visible = false;
        _shownAt = -1;
        ShowNext();
    }
}
