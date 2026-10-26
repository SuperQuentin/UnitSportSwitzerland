using Godot;
using UnitSport.Adventure;
using UnitSport.Audio;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// "Quest started", "Step done: …" and "Quest complete" with its rewards, top centre for 3 s, with a
/// short chime (#1223 §5.4). From the server's <c>QuestNote</c> (offline the same call). One at a time:
/// a new one replaces the one showing. Nothing on screen otherwise.
/// </summary>
public partial class QuestToasts : CanvasLayer
{
    private const double ShowSeconds = 3.0;

    private static AudioStreamWav? _chime, _fanfare;

    /// <summary>A step done: one soft bell.</summary>
    public static AudioStreamWav Chime => _chime ??= Bells(0.6f, (0f, 987.8f));

    /// <summary>A quest complete: three rising bells, a major arpeggio.</summary>
    public static AudioStreamWav Fanfare => _fanfare ??= Bells(1.1f, (0f, 783.99f), (0.12f, 987.77f), (0.24f, 1174.66f));

    private static AudioStreamWav Bells(float seconds, params (float Start, float Hz)[] notes) => Dsp.OneShot(seconds, 1223, (_, n) =>
    {
        var s = new float[n];
        foreach (var (start, hz) in notes)
            for (int i = (int)(start * Dsp.Rate); i < n; i++)
            {
                float t = i / (float)Dsp.Rate - start;
                float env = Mathf.Min(1f, t * 300f) * Mathf.Exp(-t * 6f);
                s[i] += (Mathf.Sin(Mathf.Tau * hz * t) + 0.3f * Mathf.Sin(Mathf.Tau * hz * 2.76f * t) * Mathf.Exp(-t * 14f)) * env * 0.32f;
            }
        return s;
    });

    private AdventureService? _service;
    private PanelContainer _panel = null!;
    private Label _what = null!, _detail = null!;
    private AudioStreamPlayer _player = null!;
    private double _until;

    /// <summary>The last toast's two lines (checks).</summary>
    public string Last { get; private set; } = "";

    public override void _Ready()
    {
        Name = "QuestToasts";
        Layer = 9;
        var root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        var top = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        top.AnchorRight = 1f;
        top.OffsetTop = 72f;
        top.OffsetBottom = 160f;
        root.AddChild(top);
        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 12));
        top.AddChild(_panel);
        var col = UiKit.VBox(2);
        col.MouseFilter = Control.MouseFilterEnum.Ignore;
        _panel.AddChild(col);
        _what = UiKit.Text("", UiTheme.FontBody, UiTheme.Amber, bold: true, align: HorizontalAlignment.Center);
        _detail = UiKit.Text("", UiTheme.FontSmall, UiTheme.Text, align: HorizontalAlignment.Center);
        col.AddChild(_what);
        col.AddChild(_detail);
        _player = new AudioStreamPlayer { Name = "Chime", Bus = SfxBus.Name, VolumeDb = -6f };
        AddChild(_player);
        _service = AdventureService.Instance;
        if (_service != null) _service.Noted += OnNoted;
    }

    public override void _ExitTree()
    {
        if (_service != null) _service.Noted -= OnNoted;
    }

    private void OnNoted(string questId, QuestChangeKind what, int step, int count)
    {
        if (_service?.Quests.Resolve(questId) is not { } def) return;
        (string head, string line, AudioStreamWav? sound) = what switch
        {
            QuestChangeKind.Started => ("Quest started", def.Title, Chime),
            QuestChangeKind.StepDone => ($"Step done: {def.Steps[Math.Max(0, step - 1)].Line}",
                step < def.Steps.Length ? "Next: " + def.Steps[step].Line : def.Title, Chime),
            QuestChangeKind.Completed => ($"Quest complete: {def.Title}", string.Join(" · ", def.Rewards.Select(r => r.Describe())), Fanfare),
            _ => ("", "", null),
        };
        if (sound == null) return;
        _what.Text = head;
        _detail.Text = line;
        _detail.Visible = line.Length > 0;
        Last = $"{head} | {line}";
        _until = GameClock.Now + ShowSeconds;
        _player.Stream = sound;
        _player.Play();
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        bool show = GameClock.Now < _until;
        var m = _panel.Modulate;
        float want = show ? 1f : 0f;
        if (Mathf.IsEqualApprox(m.A, want))
        {
            if (!show) SetProcess(false);
            return;
        }
        // in over 0.2 s, out over about a second (style-guide 6)
        _panel.Modulate = new Color(1, 1, 1, Mathf.MoveToward(m.A, want, (float)delta * (show ? 5f : 1f)));
    }
}
