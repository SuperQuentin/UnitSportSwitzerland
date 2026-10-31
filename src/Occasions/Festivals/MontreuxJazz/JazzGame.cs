using Godot;
using UnitSport.Concert;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.MontreuxJazz;

/// <summary>
/// Montreux Jazz, the player's side (#1394, <c>docs/notes/occasions/festival-montreuxjazz.md</c>). Two things, on keys that exist: the interact key (E / the pad's
/// Y / a VR grip, which <c>XrPad</c> replays) at the stage-front rail marks "I am listening" (a song heard from the audience pays when it ends: a toast, top-right);
/// and when the set is over and the band has left, jump (Space / the pad's A / the VR pad's A, as the pogo's) on the beat claps it back: a ring closes onto a target
/// on each beat at the bottom centre, and the crowd's meter ("7 of 12") says how close the band is to returning. The server judges every clap from the shared clock;
/// this screen only stamps the press. While the call is on and the player is in the audience, jump is the clap, not a jump (<see cref="FootPlayer.JumpTaken"/>).
/// </summary>
public partial class JazzGame : CanvasLayer
{
    public const string NodeName = "JazzGame";

    public static JazzGame? Instance { get; private set; }

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private FestivalService? _svc;
    private bool _hooked, _held, _atRail, _inZone, _calling;
    private string _message = "";
    private double _messageAt = double.NegativeInfinity;
    private string _prompt = "";
    private (InputDevice, bool) _promptKey = ((InputDevice)(-1), false);
    private ConcertEvent? _event;
    private (string, int) _eventKey = ("", -1);
    private double _gradeAt = double.NegativeInfinity;
    private PogoGrade _grade;
    private int _gradeBeat = int.MinValue;
    private double _callBeat;
    private string _hint = "";
    private double _hintAt = double.NegativeInfinity;

    /// <summary>For the checks: at the rail, in the audience, and whether the call is on for this player; the last line and the last grade.</summary>
    internal bool AtRail => _atRail;

    internal bool InZone => _inZone;

    internal bool Calling => _calling;

    internal string Message => _message;

    internal PogoGrade LastGrade => _grade;

    public JazzGame(Func<FootPlayer?> local)
    {
        _local = local;
        Name = NodeName;
        Layer = 7;
        // after the pogo's screen, which lets go of the jump press every frame it is not dancing
        ProcessPriority = 10;
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore, Game = this };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (_svc != null)
        {
            _svc.MontreuxTold -= OnTold;
            _svc.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        _svc = svc;
        svc.MontreuxTold += OnTold;
        svc.Refused += OnRefused;
    }

    // ---- what the service says ---------------------------------------------------------------------------

    private void OnTold(JazzEvent what, int a, int b, string text)
    {
        if (what == JazzEvent.Clap)
        {
            _grade = (PogoGrade)a;
            _gradeAt = GameClock.Now;
            _gradeBeat = (int)Math.Round(_callBeat);
        }
        if (text.Length > 0) Say(text);
        else _view.QueueRedraw();
    }

    private void OnRefused(string why)
    {
        // only this festival's own refusals reach the screen: said at the rail or in the audience
        if (_svc?.MontreuxNow() == null || !(_atRail || _inZone)) return;
        Say(why);
    }

    private void Say(string text)
    {
        _message = text;
        _messageAt = GameClock.Now;
        _view.QueueRedraw();
    }

    // ---- what is near, and the clap ------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        Hook();
        bool rail = false, zone = false, calling = false;
        double callBeat = 0;
        var p = _local();
        if (_svc?.MontreuxNow() is { } m && p != null)
        {
            var g = p.Global;
            rail = MontreuxJazzRules.AtRail(m.Site, g.E, g.N);
            zone = MontreuxJazzRules.InZone(m.Site, g.E, g.N);
            var e = EventOf(m.Run);
            double h = _svc.Hour();
            if (zone && h >= e.StartHour && h < e.EndHour)
            {
                double now = Net.ClockSync.ServerNow;
                var mom = MontreuxJazzRules.Moment(e, now);
                if (mom.Part == JazzPart.Call)
                {
                    calling = true;
                    callBeat = JazzSet.BeatOf(now % 86400.0, e.Bpm) - JazzSet.CallStart(mom.Set, e.SetSongs);
                }
            }
        }
        _atRail = rail;
        _inZone = zone;
        _calling = calling;
        _callBeat = callBeat;
        if (p != null)
        {
            // the press is the clap while the call is on: not also a jump
            bool down = calling && PlayerInput.Held(PlayerInput.Jump);
            if (calling) p.JumpTaken = true;
            else if (_wasCalling) p.JumpTaken = false;
            if (down && !_held) _svc?.AskMontreux(FestivalService.MontreuxClap, Net.ClockSync.ServerNow);
            _held = down;
        }
        _wasCalling = calling;
        bool shown = calling || (zone && _svc?.MontreuxListening == true) || rail || GameClock.Now - _messageAt < 4;
        if (calling || zone) _view.QueueRedraw();
        if (_view.Visible != shown) _view.Visible = shown;
    }

    private bool _wasCalling;

    private ConcertEvent EventOf(RunningFestival run)
    {
        var key = (run.Instance, run.Day);
        if (_event == null || key != _eventKey)
        {
            _eventKey = key;
            _event = FestivalService.MontreuxEvent(run);
        }
        return _event;
    }

    // ---- the interaction -------------------------------------------------------------------------------------

    /// <summary>The prompt of what E does here: mark yourself as listening. Null away from the rail.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc?.MontreuxNow() == null || !game._atRail) return null;
        var device = PlayerInput.HintDevice;
        bool listening = game._svc.MontreuxListening;
        var key = (device, listening);
        if (key != game._promptKey)
        {
            game._promptKey = key;
            game._prompt = InputHints.Prompt(PlayerInput.InteractMount, listening ? "Listening: stay for the song" : "Listen to the band");
        }
        return new Interaction(game._prompt, default);
    }

    /// <summary>E / Y / a VR grip: marks the player as listening. False away from the rail.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.MontreuxNow() == null || !game._atRail) return false;
        svc.AskMontreux(FestivalService.MontreuxMark);
        return true;
    }

    // ---- the screen ------------------------------------------------------------------------------------------

    private sealed partial class View : Control
    {
        public JazzGame Game = null!;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Amber = new(1f, 0.7f, 0.25f);
        private static readonly Color Green = new(0.45f, 0.95f, 0.5f);
        private static readonly Color Red = new(0.9f, 0.25f, 0.2f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public override void _Draw()
        {
            var g = Game;
            if (g._svc?.MontreuxNow() is not { } m) return;
            double now = GameClock.Now;
            // the toast: top-right, a 3 s fade (ui/ux-guidelines), never over the stage
            double age = now - g._messageAt;
            if (g._message.Length > 0 && age < 4)
            {
                float a = age < 3 ? 1f : (float)(4 - age);
                var at = new Vector2(Size.X - 24f, 96f);
                DrawRect(new Rect2(at.X - 520, at.Y - 28, 520, 44), new Color(0, 0, 0, 0.45f * a), true);
                Text(g._message, at + new Vector2(-260, 0), 500f, UiTheme.FontBody, new Color(Amber, a));
            }
            if (g._calling) DrawRing();
            else if (g._inZone && g._svc.MontreuxListening)
                Text("Listening: stay near the stage", new Vector2(Size.X * 0.5f, Size.Y * 0.80f), 600f, UiTheme.FontSmall, new Color(UiTheme.TextDim, 0.9f));
        }

        /// <summary>The clap ring at the bottom centre: closes onto its target on each beat, the meter above it, the grade of the last clap.</summary>
        private void DrawRing()
        {
            var g = Game;
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.70f);
            double beats = g._callBeat;
            float phase = (float)(beats - Math.Floor(beats));
            double ring = beats - JazzSet.CallLeadBeats;
            bool live = ring >= -0.5 && ring < JazzSet.CallBeats - 0.5;
            const float target = 22f;
            float flash = Mathf.Clamp(1f - phase / 0.15f, 0f, 1f);
            var colour = live ? Gold : UiTheme.TextDim;
            DrawCircle(c, target, new Color(colour, 0.18f + (live ? 0.5f * flash : 0f)));
            DrawArc(c, target, 0, Mathf.Tau, 40, colour, 3f, true);
            if (live) DrawArc(c, target + 70f * (1f - phase), 0, Mathf.Tau, 48, new Color(UiTheme.Text, 0.35f + 0.6f * phase), 2.5f, true);
            // the eight beats as dots, the ones gone by filled
            for (int i = 0; i < JazzSet.CallBeats; i++)
            {
                var d = c + new Vector2((i - (JazzSet.CallBeats - 1) / 2f) * 18f, target + 26f);
                bool done = ring >= i + 0.5;
                DrawCircle(d, 5f, new Color(done ? Gold : UiTheme.TextDim, done ? 0.95f : 0.4f));
            }
            double age = GameClock.Now - g._gradeAt;
            if (age < 0.5 && g._grade != PogoGrade.None)
            {
                var (s, col) = g._grade switch { PogoGrade.Perfect => ("PERFECT", Gold), PogoGrade.Good => ("GOOD", Green), _ => ("MISS", Red) };
                Text(s, c + new Vector2(0, -target - 18f - (float)age * 30f), 400f, UiTheme.FontHeading, new Color(col, 1f - (float)age * 2f), true);
            }
            Text($"Call the band back: {MontreuxJazzRules.MeterText(g._svc!.MontreuxMeter)}", c + new Vector2(0, -target - 62f), 700f, UiTheme.FontBody, Gold, true);
            if (GameClock.Now - g._hintAt > 1.0)
            {
                g._hintAt = GameClock.Now;
                g._hint = InputHints.Format("{jump} on every beat");
            }
            Text(g._hint, c + new Vector2(0, target + 64f), 400f, UiTheme.FontSmall, UiTheme.TextDim);
        }

        private void Text(string s, Vector2 at, float width, int size, Color colour, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(width / 2f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, width, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, width, size, colour);
        }
    }
}
