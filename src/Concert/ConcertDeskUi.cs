using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Concert;

/// <summary>
/// The light desk's screen (#885), opened at the front-of-house console of a concert: E (pad Y; in VR
/// Y, or reach out and grip it). It shows the show (artist, song, section, beat, who runs it, an
/// external feed) and runs it like a simple console: host this set or a new one, release it; hold a
/// section or follow the song, GO, tap the tempo, the master fader, blackout; fire the effects; the
/// operator's show input (Art-Net, sACN, OSC) and the patch for the controller. Every button is the
/// <c>/concert</c> command a player could type, so the server decides who may, online or off.
/// A CanvasLayer, so VR shows it on its panel (<c>XR/XrUi</c>); the pad moves through its buttons.
/// </summary>
public partial class ConcertDeskUi : CanvasLayer
{
    public static ConcertDeskUi? Instance { get; private set; }

    /// <summary>How near the console a player must stand to open it, and walks away from it.</summary>
    public const float Reach = 4.5f, WalkAway = 7f;

    private readonly Func<FootPlayer?> _player;
    private readonly Func<ChatManager?> _chat;
    private PanelContainer _panel = null!;
    private Label _title = null!, _status = null!, _input = null!, _hint = null!;
    private HSlider _master = null!;
    private Button _first = null!;
    private ConcertRig? _rig;
    private string? _stadium;
    private readonly List<double> _taps = new();
    private double _statusAt;

    public bool IsOpen => _panel.Visible;

    private ConcertDeskUi(Func<FootPlayer?> player, Func<ChatManager?> chat)
    {
        _player = player;
        _chat = chat;
        Name = "ConcertDesk";
        Layer = 12;
    }

    public static ConcertDeskUi Create(Func<FootPlayer?> player, Func<ChatManager?> chat) => Instance = new ConcertDeskUi(player, chat);

    public override void _ExitTree()
    {
        if (IsOpen) UiFocus.Set(this, false);
        if (Instance == this) Instance = null;
    }

    // ---- where the console is ------------------------------------------------------------------------

    /// <summary>The concert whose console a player stands at, if any.</summary>
    public static ConcertRig? At(FootPlayer p)
    {
        foreach (var rig in ConcertRig.Live.Values)
            if (GodotObject.IsInstanceValid(rig) && rig.IsInsideTree() && rig.Plan is { } plan
                && rig.ToGlobal(plan.Desk).DistanceTo(p.GlobalPosition) < Reach)
                return rig;
        return null;
    }

    /// <summary>E at the console: open its screen (or close it). False when not at one.</summary>
    public static bool TryOpen(FootPlayer p)
    {
        if (Instance is not { } ui || At(p) is not { } rig) return false;
        if (ui.IsOpen) ui.Close();
        else ui.Open(rig);
        return true;
    }

    // ---- the panel -----------------------------------------------------------------------------------

    public override void _Ready()
    {
        _panel = new PanelContainer { Visible = false, Theme = UiTheme.Get() };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.95f, 12, 18));
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical = Control.GrowDirection.Both;
        AddChild(_panel);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(col);

        _title = Text(col, "", UiTheme.FontHeading, UiTheme.Text);
        _status = Text(col, "", UiTheme.FontSmall, UiTheme.TextDim);

        Section(col, "The set");
        var set = Row(col);
        _first = Btn(set, "Host this set", () => Run($"/concert host {_rig?.Event.Seed}"));
        Btn(set, "New random set", () => Run("/concert host"));
        Btn(set, "Release", () => Run("/concert release"));

        Section(col, "Cues");
        var cues = Row(col);
        Btn(cues, "GO", () => Run("/concert cue go"));
        foreach (var s in new[] { "Calm", "Groove", "Chorus", "Peak" })
            Btn(cues, s, () => Run($"/concert cue {s.ToLowerInvariant()}"));
        Btn(cues, "Follow song", () => Run("/concert cue release"));
        Btn(cues, "Blackout", () => Run("/concert cue blackout"));

        var tempo = Row(col);
        Btn(tempo, "Tap tempo", Tap);
        Text(tempo, "Master", UiTheme.FontSmall, UiTheme.TextDim);
        _master = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = 1, CustomMinimumSize = new Vector2(220, 0), FocusMode = Control.FocusModeEnum.All };
        _master.DragEnded += changed => { if (changed) Run(FormattableString.Invariant($"/concert cue master {_master.Value:F2}")); };
        tempo.AddChild(_master);

        Section(col, "Cameras");
        var cams = Row(col);
        Btn(cams, "Auto", () => Run("/concert cue camera auto"));
        for (int i = 0; i < ConcertCameras.Names.Length; i++)
        {
            int n = i + 1;
            Btn(cams, n.ToString(CultureInfo.InvariantCulture), () => Run($"/concert cue camera {n}")).TooltipText = ConcertCameras.Names[i];
        }
        Btn(cams, "Program view", () => Run("/concert view"));
        var crew = Row(col);
        Text(crew, "Operate", UiTheme.FontSmall, UiTheme.TextDim);
        var pick = new OptionButton { FocusMode = Control.FocusModeEnum.All };
        for (int i = 0; i < ConcertCameras.Names.Length; i++) pick.AddItem($"{i + 1} {ConcertCameras.Names[i]}", i);
        crew.AddChild(pick);
        Btn(crew, "Take", () => { Close(); Run($"/concert camera take {pick.Selected + 1}"); });
        Btn(crew, "Leave", () => Run("/concert camera leave"));
        Btn(cams, "NDI out", () => Run("/concert ndi"));
        Btn(cams, "Beat debug", () => Run("/concert debug"));

        Section(col, "Effects");
        var fx = Row(col);
        foreach (var (label, cmd) in new[] { ("Flames", "flames"), ("CO2", "co2"), ("Confetti", "confetti"), ("Gerbs", "gerbs"), ("Pyro", "pyro") })
            Btn(fx, label, () => Run($"/concert cue {cmd}"));

        Section(col, "Show input (this machine)");
        var input = Row(col);
        Btn(input, "Art-Net", () => Toggle("artnet", ConcertService.Instance?.Input?.ArtNetOn == true));
        Btn(input, "sACN", () => Toggle("sacn", ConcertService.Instance?.Input?.SacnOn == true));
        Btn(input, "OSC", () => Toggle("osc", ConcertService.Instance?.Input?.OscOn == true));
        Btn(input, "Export patch", () => Run("/concert patch"));
        _input = Text(col, "", UiTheme.FontSmall, UiTheme.TextDim);

        _hint = Text(col, "", UiTheme.FontTiny, UiTheme.TextFaint);
    }

    private static Label Text(Container to, string text, int size, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        to.AddChild(l);
        return l;
    }

    private static void Section(Container to, string title) => Text(to, title, UiTheme.FontSmall, UiTheme.Amber);

    private static HBoxContainer Row(Container to)
    {
        var r = new HBoxContainer();
        r.AddThemeConstantOverride("separation", 6);
        to.AddChild(r);
        return r;
    }

    private static Button Btn(Container to, string text, Action pressed)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.All };
        b.Pressed += pressed;
        to.AddChild(b);
        return b;
    }

    private void Run(string command) => _chat()?.Send(command);

    private void Toggle(string what, bool on) => Run($"/concert input {what} {(on ? "off" : "on")}");

    /// <summary>Tap tempo: the mean of the last taps' intervals, sent once there are four.</summary>
    private void Tap()
    {
        double now = RealClock.Now;
        if (_taps.Count > 0 && now - _taps[^1] > 2.0) _taps.Clear();
        _taps.Add(now);
        if (_taps.Count > 8) _taps.RemoveAt(0);
        if (_taps.Count < 4) return;
        double mean = (_taps[^1] - _taps[0]) / (_taps.Count - 1);
        Run(FormattableString.Invariant($"/concert cue bpm {60.0 / mean:F0}"));
    }

    public void Open(ConcertRig rig)
    {
        _rig = rig;
        _stadium = rig.Event.Stadium.Name;
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Refresh();
        Callable.From(() => _first.GrabFocus()).CallDeferred();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        // hosting a set builds the stadium's rig again: the screen follows the new one
        if (_rig != null && (!IsInstanceValid(_rig) || !_rig.IsInsideTree()) && _stadium != null
            && ConcertRig.Live.TryGetValue(_stadium, out var again) && IsInstanceValid(again) && again.IsInsideTree())
            _rig = again;
        if (_rig == null || !IsInstanceValid(_rig) || _player() is not { } p || _rig.ToGlobal(_rig.Plan!.Desk).DistanceTo(p.GlobalPosition) > WalkAway)
        {
            Close();
            return;
        }
        // the status twice a second: text only on change, not per frame
        if (RealClock.Now - _statusAt > 0.5) Refresh();
    }

    private void Refresh()
    {
        _statusAt = RealClock.Now;
        if (_rig == null) return;
        var e = _rig.Event;
        var svc = ConcertService.Instance;
        int stadium = Array.IndexOf(Terrain.Stadiums.StadiumCatalog.All, e.Stadium);
        long op = svc?.OperatorOf(stadium) ?? 0;
        string who = op == 0 ? "tonight's show (nobody hosts it)" : svc!.IsOperator(stadium) ? "you run it" : $"run by player {op}";
        var song = _rig.Music.Song;
        string title = $"{e.Artist} — {e.Tour}";
        if (_title.Text != title) _title.Text = title;
        string music = song != null ? FormattableString.Invariant($"♪ {song.Title} · {song.Bpm:F0} bpm") : "no music: made-up songs";
        string status = FormattableString.Invariant($"{e.Genre}, {e.Layout}, set {e.Seed} · {who}\n{music} · {_rig.Desk.Section} · beat {_rig.Desk.Beat:F0}{(_rig.ExternalLive ? " · EXTERNAL DMX LIVE" : "")}");
        if (_status.Text != status) _status.Text = status;
        var input = svc?.Input;
        string inp = input == null ? "Off. Turn a protocol on, then point Chataigne or TouchDesigner at this machine."
            : $"Art-Net {(input.ArtNetOn ? $"on ({input.ArtNetPackets})" : "off")} · sACN {(input.SacnOn ? $"on ({input.SacnPackets})" : "off")} · OSC {(input.OscOn ? $"on ({input.OscPackets})" : "off")}";
        if (_input.Text != inp) _input.Text = inp;
        string hint = InputHints.Format("{ui_accept} press · {ui_cancel} close");
        if (_hint.Text != hint) _hint.Text = hint;
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (!IsOpen) return;
        if (ev.IsActionPressed("ui_cancel") || ev.IsActionPressed(PlayerInput.Menu))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
