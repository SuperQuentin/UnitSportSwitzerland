using System.Text;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The tracked quest on the HUD (#1223 §5.2): on the right edge below the invite cards, its title, the
/// current step with its count, and how far its target is. The texts are set only when they change, the
/// distance 4 times a second, rounded (<c>perf-no-per-frame-allocations</c>). It fades out while a page
/// is open, in a Battle Royale round, and at speed (driving fast, flying), and shows nothing when no
/// quest is tracked.
/// </summary>
public partial class QuestTracker : CanvasLayer
{
    private const float Width = 300f;
    /// <summary>Faster than this (m/s) the player is under pressure: the tracker steps aside.</summary>
    private const float BusySpeed = 22f;

    private readonly Func<FootPlayerView?> _me;
    private AdventureService? _service;
    private PanelContainer _panel = null!;
    private Label _title = null!, _step = null!, _far = null!;
    private readonly StringBuilder _sb = new();
    private string _farShown = "";
    private double _farIn;
    private bool _has;

    /// <summary>What the tracker needs of the local body: where it is and how fast it goes.</summary>
    public readonly record struct FootPlayerView(GlobalPos At, float Speed);

    public QuestTracker(Func<FootPlayerView?> me) => _me = me;
    public QuestTracker() : this(() => null) { }

    /// <summary>The lines shown (checks): empty when nothing is tracked.</summary>
    public string Shown => _has ? $"{_title.Text} | {_step.Text} | {_far.Text}" : "";

    public override void _Ready()
    {
        Name = "QuestTracker";
        Layer = 5;
        var root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.6f, 10, 10));
        // the right edge, below the invite cards' band (InviteToasts at a third of the way down)
        _panel.AnchorLeft = _panel.AnchorRight = 1f;
        _panel.AnchorTop = _panel.AnchorBottom = 0.64f;
        _panel.OffsetLeft = -Width - 16f;
        _panel.OffsetRight = -16f;
        root.AddChild(_panel);
        var col = UiKit.VBox(2);
        col.MouseFilter = Control.MouseFilterEnum.Ignore;
        _panel.AddChild(col);
        _title = UiKit.Text("", UiTheme.FontBody, UiTheme.Amber, bold: true);
        _title.ClipText = true;
        _step = UiKit.Text("", UiTheme.FontSmall, UiTheme.Text, wrap: true);
        _far = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextDim);
        col.AddChild(_title);
        col.AddChild(_step);
        col.AddChild(_far);
        Bind(AdventureService.Instance);
    }

    public override void _ExitTree() => Bind(null);

    private void Bind(AdventureService? service)
    {
        if (_service != null) _service.SectionChanged -= OnSection;
        _service = service;
        if (_service != null) _service.SectionChanged += OnSection;
        Refresh();
    }

    private void OnSection(string id)
    {
        if (id == QuestsProgress.Section.Id) Refresh();
    }

    /// <summary>The title and the step line, on a change of the quests section.</summary>
    private void Refresh()
    {
        _has = false;
        if (_service == null) return;
        var q = _service.Mine.Get(QuestsProgress.Section);
        if (q.Tracked is not { } id || !q.Active.TryGetValue(id, out var state) || _service.Quests.Resolve(id) is not { } def
            || _service.Quests.Current(id, state) is not { } step)
            return;
        _has = true;
        _title.Text = def.Title;
        _step.Text = step.Count > 1 ? $"{step.Line}  {state.Count}/{step.Count}" : step.Line;
        _farIn = 0;
    }

    public override void _Process(double delta)
    {
        var me = _me();
        bool busy = Permissions.InMatch || GameShell.Instance?.MenuOpen == true || me is { Speed: > BusySpeed };
        float want = _has && !busy ? 1f : 0f;
        var m = _panel.Modulate;
        if (!Mathf.IsEqualApprox(m.A, want))
            _panel.Modulate = new Color(1, 1, 1, Mathf.MoveToward(m.A, want, (float)delta * (want > 0 ? 5f : 3f)));
        if (!_has || (_farIn -= delta) > 0) return;
        _farIn = 0.25;
        _sb.Clear();
        if (_service?.TrackedTarget is { } t && me is { } at)
        {
            double d = Math.Sqrt((at.At.E - t.E) * (at.At.E - t.E) + (at.At.N - t.N) * (at.At.N - t.N));
            if (d <= t.RadiusM) _sb.Append("Here");
            else if (d < 1000) _sb.Append((int)(Math.Round(d / 10) * 10)).Append(" m");
            else _sb.Append((d / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" km");
        }
        if (_sb.Equals(_farShown.AsSpan())) return;
        _farShown = _sb.ToString();
        _far.Text = _farShown;
    }
}
