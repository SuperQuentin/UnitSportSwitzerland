using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The arrival curtain of a trip (#1226, navigation plan 5.3): the kind's chime (SBB gong, PostAuto horn, ship's
/// bell, ropeway bell), 0.8 s to black, the jump, a card "Arriving at Zermatt · 1 608 m" for 1.2 s, then the world
/// fades back. With the phone out its own screen hides the jump (<see cref="Teleporter.Curtain"/>), as today. Built
/// once; its text is set once per trip. Without a curtain in the tree (a bare probe) the jump goes at once.
/// </summary>
public partial class TravelCurtain : CanvasLayer
{
    private const float FadeOut = 0.8f, Hold = 1.2f, FadeIn = 0.6f;

    public static TravelCurtain? Instance { get; private set; }

    private ColorRect _black = null!;
    private Label _head = null!, _line = null!;
    private AudioStreamPlayer _chime = null!;
    private Action? _jump;
    private double _t;
    private bool _running;

    /// <summary>Trips the curtain played (checks).</summary>
    public int Played { get; private set; }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Name = "TravelCurtain";
        Layer = 60;
        _black = new ColorRect { Color = new Color(0.02f, 0.03f, 0.06f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_black);
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Get() };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _black.AddChild(centre);
        var col = UiKit.VBox(6);
        col.MouseFilter = Control.MouseFilterEnum.Ignore;
        centre.AddChild(col);
        _head = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, bold: true, align: HorizontalAlignment.Center);
        _line = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true, align: HorizontalAlignment.Center);
        col.AddChild(_head);
        col.AddChild(_line);
        _chime = new AudioStreamPlayer { Name = "Chime", Bus = SfxBus.Name, VolumeDb = -6f };
        AddChild(_chime);
        Visible = false;
        SetProcess(false);
    }

    /// <summary>Plays a trip's curtain and runs <paramref name="jump"/> under the black.</summary>
    public static void Play(Poi to, Action jump)
    {
        if (Instance is not { } c || XR.XrSession.Active)
        {
            // the headset has no flat overlay: the chime only, and the jump
            if (Instance is { } vr) vr.Chime(to);
            jump();
            return;
        }
        c.Start(to, jump);
    }

    private void Chime(Poi to)
    {
        _chime.Stream = NavSounds.ChimeOf(TravelNetwork.KindOf(to.Kind));
        _chime.Play();
    }

    private void Start(Poi to, Action jump)
    {
        // a trip asked during another one: the earlier jump goes now
        _jump?.Invoke();
        _jump = jump;
        _head.Text = TravelNetwork.LineName(TravelNetwork.KindOf(to.Kind)) is { Length: > 0 } line ? line.ToUpperInvariant() : "";
        _line.Text = to.Elevation > 0 && to.Kind != PoiKind.BusStop
            ? $"Arriving at {to.Name} · {AtlasFacts.Num(to.Elevation)} m" : $"Arriving at {to.Name}";
        _head.Modulate = _line.Modulate = new Color(1, 1, 1, 0);
        Chime(to);
        _t = 0;
        _running = true;
        Visible = true;
        Played++;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (!_running) return;
        _t += delta;
        float a;
        if (_t < FadeOut) a = (float)(_t / FadeOut);
        else if (_t < FadeOut + Hold)
        {
            a = 1f;
            if (_jump is { } jump)
            {
                _jump = null;
                jump();
            }
        }
        else a = 1f - Mathf.Clamp((float)((_t - FadeOut - Hold) / FadeIn), 0f, 1f);
        _black.Color = _black.Color with { A = a };
        float text = _t < FadeOut ? 0f : _t < FadeOut + Hold ? Mathf.Clamp((float)((_t - FadeOut) / 0.25), 0f, 1f) : a;
        _head.Modulate = _line.Modulate = new Color(1, 1, 1, text);
        if (_t >= FadeOut + Hold + FadeIn)
        {
            _running = false;
            Visible = false;
            SetProcess(false);
        }
    }

    /// <summary>The curtain is up (checks).</summary>
    public bool Running => _running;
}
