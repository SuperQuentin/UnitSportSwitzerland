using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// Invites to the local player as cards on the right edge (#840): who, what, the keys to answer and a
/// timer bar, newest on top, three at most. A soft two-note chime when one arrives. Nothing is on
/// screen without an invite. Answering is the quick menu's (F2 / Shift+F2), the Start menu's first
/// entry, the Activities screen's Invites tab, or <c>/accept</c>. Docs: <c>docs/notes/net/invites.md</c>.
/// </summary>
public partial class InviteToasts : CanvasLayer
{
    private const int MaxShown = 3;
    private const float Width = 340f;

    public static InviteToasts? Instance { get; private set; }

    private static AudioStreamWav? _chime;

    /// <summary>An invite arrived: two soft rising notes, a fourth apart.</summary>
    public static AudioStreamWav Chime => _chime ??= Dsp.OneShot(0.55f, 840, (_, n) =>
    {
        var s = new float[n];
        foreach (var (start, hz) in new[] { (0f, 880f), (0.13f, 1174.7f) })
            for (int i = (int)(start * Dsp.Rate); i < n; i++)
            {
                float t = i / (float)Dsp.Rate - start;
                float env = Mathf.Min(1f, t * 400f) * Mathf.Exp(-t * 9f);
                s[i] += (Mathf.Sin(Mathf.Tau * hz * t) + 0.25f * Mathf.Sin(Mathf.Tau * hz * 2f * t)) * env * 0.4f;
            }
        return s;
    });

    private sealed class Card
    {
        public InviteView View = null!;
        public PanelContainer Panel = null!;
        public ColorRect Bar = null!;
        public Label Left = null!;
        public int ShownSeconds = -1;
    }

    private VBoxContainer _column = null!;
    private readonly List<Card> _cards = new();
    private InviteService? _bound;
    private AudioStreamPlayer _player = null!;
    private PanelContainer? _flash;
    private double _flashUntil;
    private bool _dirty;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        Bind(null);
    }

    public override void _Ready()
    {
        Name = "InviteToasts";
        Layer = 9;
        var root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        // the right edge, a third of the way down: clear of the minimap and the kill feed in a match
        _column = UiKit.VBox(8);
        _column.MouseFilter = Control.MouseFilterEnum.Ignore;
        _column.AnchorLeft = _column.AnchorRight = 1f;
        _column.AnchorTop = _column.AnchorBottom = 0.34f;
        _column.OffsetLeft = -Width - 16f;
        _column.OffsetRight = -16f;
        root.AddChild(_column);
        _player = new AudioStreamPlayer { Name = "Chime", Bus = SfxBus.Name, VolumeDb = -6f };
        AddChild(_player);
    }

    private void Bind(InviteService? service)
    {
        if (_bound == service) return;
        if (_bound != null)
        {
            _bound.Changed -= MarkDirty;
            _bound.Arrived -= OnArrived;
        }
        _bound = service;
        if (_bound != null)
        {
            _bound.Changed += MarkDirty;
            _bound.Arrived += OnArrived;
        }
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    private void OnArrived(InviteView _)
    {
        _player.Stream = Chime;
        _player.Play();
    }

    /// <summary>A one-line note in the same place for a few seconds (the quick menu's "Vehicles: on foot only").</summary>
    public void Flash(string text)
    {
        _flash?.QueueFree();
        _flash = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _flash.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 10));
        _flash.AddChild(UiKit.Text(text, UiTheme.FontSmall, UiTheme.TextDim));
        _column.AddChild(_flash);
        _column.MoveChild(_flash, 0);
        _flashUntil = GameClock.Now + 2.5;
    }

    public override void _Process(double delta)
    {
        Bind(InviteService.Client);
        if (_flash != null && GameClock.Now > _flashUntil)
        {
            _flash.QueueFree();
            _flash = null;
        }
        if (_dirty) Rebuild();
        foreach (var c in _cards)
        {
            double left = c.View.Left;
            float frac = c.View.Seconds > 0 ? (float)(left / c.View.Seconds) : 0f;
            // a layout pass only when the bar has moved a pixel (perf-no-per-frame-allocations)
            float width = Mathf.Round((Width - 20f) * Mathf.Clamp(frac, 0f, 1f));
            if (width != c.Bar.CustomMinimumSize.X) c.Bar.CustomMinimumSize = new Vector2(width, 3f);
            int secs = Mathf.CeilToInt((float)left);
            if (secs == c.ShownSeconds) continue;
            c.ShownSeconds = secs;
            c.Left.Text = $"{secs} s";
        }
    }

    private void Rebuild()
    {
        _dirty = false;
        foreach (var c in _cards) c.Panel.QueueFree();
        _cards.Clear();
        if (_bound == null) return;
        int i = 0;
        foreach (var view in _bound.Incoming)
        {
            if (i++ >= MaxShown) break;
            var card = Make(view, newest: i == 1);
            _cards.Add(card);
            _column.AddChild(card.Panel);
        }
        int more = _bound.IncomingCount - MaxShown;
        if (more > 0 && _cards.Count > 0)
            ((VBoxContainer)_cards[^1].Panel.GetChild(0)).AddChild(UiKit.Text($"+{more} more in the quick menu's Invites", UiTheme.FontTiny, UiTheme.TextFaint));
    }

    private static Card Make(InviteView view, bool newest)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(Width, 0) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 10));
        var box = UiKit.VBox(3);
        box.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.AddChild(box);

        var top = UiKit.HBox(8);
        var title = UiKit.Text(view.Title, UiTheme.FontBody, newest ? UiTheme.Text : UiTheme.TextDim, bold: true);
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(title);
        var left = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint);
        top.AddChild(left);
        box.AddChild(top);
        if (view.Detail.Length > 0) box.AddChild(UiKit.Text(view.Detail, UiTheme.FontSmall, UiTheme.TextDim));
        // only the newest answers to the keys
        if (newest) box.AddChild(UiKit.Text(Keys(), UiTheme.FontTiny, UiTheme.Amber));
        var bar = new ColorRect { Color = new Color(UiTheme.Amber, newest ? 0.85f : 0.4f), CustomMinimumSize = new Vector2(Width - 20f, 3f), MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(bar);
        return new Card { View = view, Panel = panel, Bar = bar, Left = left };
    }

    /// <summary>How to answer, on this device.</summary>
    private static string Keys() => InputHints.Pad
        ? InputHints.Format("{menu} › Accept  ·  hold {quick_menu} › Invites")
        : $"{InputHints.Label(PlayerInput.QuickMenu)} accept  ·  Shift+{InputHints.Label(PlayerInput.QuickMenu)} decline";
}
