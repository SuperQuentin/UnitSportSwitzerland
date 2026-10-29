using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Occasions.Festivals.Desalpe;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.Chalanda;

/// <summary>
/// The Chalandamarz, the player's side (#1356, <c>docs/notes/occasions/festival-chalandamarz.md</c>): one key, the interact key (E / the pad's Y / a VR grip,
/// which <c>XrPad</c> replays), so no new binding exists. At the Treichel stand E begins the procession; then, walking the village loop, every E rings the
/// Treichel: the press is stamped with the server clock (<see cref="ClockSync.ServerNow"/>) and sent, and the server grades it against the march beat, which
/// is the same moment for everybody (a drum thumps it from the speakers, a ring on the HUD closes on it). The Treichel sounds at once on this client (the
/// Désalpe's bell, the middle one on the beat, the small one near it, a muffled clank off it); the server's word comes back for the score. Nothing is built
/// per frame: the drum is a sound, the pulse is two arcs, strings are cached on the keys that change.
/// </summary>
public partial class ChalandaGame : CanvasLayer
{
    public const string NodeName = "ChalandaGame";

    public static ChalandaGame? Instance { get; private set; }

    private static readonly string[] Compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
    private static readonly string[] GradeText = ["Off the beat", "Good", "Perfect!"];

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private readonly AudioStreamPlayer _sfx = new() { Name = "Sfx", VolumeDb = -4f };
    private readonly AudioStreamPlayer _drum = new() { Name = "Drum", VolumeDb = -10f };
    private readonly Random _rng = new(1356);
    private FestivalService? _svc;
    private bool _hooked, _nearStand, _marching;
    private int _next, _points, _synced;
    private long _nextBeat;
    private string _message = "";
    private double _messageAt = double.NegativeInfinity;
    private int _grade = -1;
    private double _gradeAt = double.NegativeInfinity;
    private string _prompt = "";
    private (InputDevice, bool, bool) _promptKey = ((InputDevice)(-1), false, false);
    private string _runLine = "";
    private (int, int, int, int, int) _runKey = (-1, -1, -1, -1, -1);

    /// <summary>The player stands at the Treichel stand and is not marching.</summary>
    internal bool NearStand => _nearStand;

    /// <summary>The procession runs for this player; the station it heads for next (0 to 7, 7 is the stand), its points and the rings in step.</summary>
    internal bool Marching => _marching;

    internal int NextStation => _next;

    internal int Points => _points;

    internal int SyncedRings => _synced;

    /// <summary>Rings this client pressed, and the grade (0 miss, 1 good, 2 perfect) the server last gave one (-1 before any).</summary>
    internal int Pressed { get; private set; }

    internal int LastGrade => _grade;

    internal string Message => _message;

    /// <summary>Checks: a drum beat has sounded this many times.</summary>
    internal int Beats { get; private set; }

    public ChalandaGame(Func<FootPlayer?> local)
    {
        _local = local;
        Name = NodeName;
        Layer = 7;
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore, Game = this };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
        AddChild(_sfx);
        AddChild(_drum);
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (_svc != null)
        {
            _svc.ChalandaTold -= OnTold;
            _svc.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        _svc = svc;
        svc.ChalandaTold += OnTold;
        svc.Refused += OnRefused;
    }

    // ---- what the service says ----------------------------------------------------------------------

    private void OnTold(FestivalService.ChalandaEvent what, int a, int points, string text)
    {
        switch (what)
        {
            case FestivalService.ChalandaEvent.Started:
                _marching = true;
                _next = 0;
                _points = 0;
                _synced = 0;
                _grade = -1;
                _nextBeat = ChalandaRules.NextBeat(ClockSync.ServerNow);
                break;
            case FestivalService.ChalandaEvent.Rang:
                _grade = a;
                _points = points;
                _gradeAt = GameClock.Now;
                if (text.Length == 0) text = GradeText[Math.Clamp(a, 0, 2)];
                break;
            case FestivalService.ChalandaEvent.Synced:
                _synced = a;
                _points = points;
                Play(ChalandaSounds.InStepChime, -6f);
                break;
            case FestivalService.ChalandaEvent.Station:
                _next = a;
                _points = points;
                break;
            case FestivalService.ChalandaEvent.Done:
            case FestivalService.ChalandaEvent.Lost:
                _marching = false;
                break;
        }
        Say(text);
    }

    private void OnRefused(string why)
    {
        // only the procession's own refusals reach this screen: the ones said at the stand or on the march
        if (_svc?.ChalandaNow() == null || !(_nearStand || _marching)) return;
        Say(why);
    }

    private void Say(string text)
    {
        _message = text;
        _messageAt = GameClock.Now;
        _view.QueueRedraw();
    }

    private void Play(SfxBank bank, float db)
    {
        var (stream, pitch, jitter) = bank.Pick(_rng);
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.VolumeDb = db + jitter;
        _sfx.Play();
    }

    // ---- what is near ---------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        Hook();
        bool near = false;
        if (_svc?.ChalandaNow() is { } m && _local() is { } p)
        {
            var g = p.Global;
            near = !_marching && ChalandaRules.NearStand(m.Site, g.E, g.N);
        }
        else if (_marching) _marching = false;   // the festival ended, or this body is gone
        if (_marching)
        {
            // the march drum: one thump on every beat of the server clock, however the frames fall
            double now = ClockSync.ServerNow;
            if (ChalandaRules.BeatTime(_nextBeat) <= now)
            {
                if (now - ChalandaRules.BeatTime(_nextBeat) < ChalandaRules.SecondsPerBeat)
                {
                    var (stream, pitch, jitter) = ChalandaSounds.MarchDrum.Pick(_rng);
                    _drum.Stream = stream;
                    _drum.PitchScale = pitch;
                    _drum.VolumeDb = -10f + jitter;
                    _drum.Play();
                    Beats++;
                }
                _nextBeat = ChalandaRules.NextBeat(now);
            }
            _view.QueueRedraw();   // the pulse moves every frame
        }
        bool shown = near || _marching || GameClock.Now - _messageAt < 4;
        if (near != _nearStand) _view.QueueRedraw();
        _nearStand = near;
        if (_view.Visible != shown) _view.Visible = shown;
    }

    // ---- the interaction -------------------------------------------------------------------------------

    /// <summary>The prompt of what E does here: ring while marching, begin at the stand, else null.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc?.ChalandaNow() == null || !(game._marching || game._nearStand)) return null;
        var key = (PlayerInput.HintDevice, game._marching, game._nearStand);
        if (key != game._promptKey)
        {
            game._promptKey = key;
            game._prompt = InputHints.Prompt(PlayerInput.InteractMount, game._marching ? "Ring the Treichel on the beat" : "Begin the procession (take a Treichel)");
        }
        return new Interaction(game._prompt, default);
    }

    /// <summary>E / Y / a VR grip: rings the Treichel on the march, or begins the procession at the stand. False when neither is at hand.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.ChalandaNow() == null) return false;
        if (game._marching)
        {
            double stamp = ClockSync.ServerNow;
            game.Pressed++;
            // the Treichel answers at once, the way the beat was met; the server's grade comes back for the score
            switch (ChalandaRules.Grade(stamp))
            {
                case RingGrade.Perfect: game.Play(DesalpeSounds.TreichelMiddle, -4f); break;
                case RingGrade.Good: game.Play(DesalpeSounds.TreichelSmall, -4f); break;
                default: game.Play(ChalandaSounds.MuffledClank, -4f); break;
            }
            svc.AskChalandaRing(stamp);
            return true;
        }
        if (!game._nearStand) return false;
        svc.AskChalandaStart();
        return true;
    }

    // ---- the screen --------------------------------------------------------------------------------------

    /// <summary>The run's line: the points, the rings in step, the next station and its bearing and distance, rebuilt only when a shown value changes.</summary>
    private void RunLine(ResolvedSite site, FootPlayer p)
    {
        var path = ChalandaRules.Path(site);
        var g = p.Global;
        var to = path[Math.Clamp(_next, 0, ChalandaRules.Stations)];
        double dx = to.E - g.E, dn = to.N - g.N;
        int dist = (int)(Math.Sqrt(dx * dx + dn * dn) / 5) * 5;
        int bearing = (int)Math.Round(Math.Atan2(dx, dn) / (Math.PI / 4));   // 0 = north, clockwise
        var key = (_points, _synced, _next, dist, ((bearing % 8) + 8) % 8);
        if (key == _runKey) return;
        _runKey = key;
        string where = _next >= ChalandaRules.Stations ? "the stand" : $"station {_next + 1}/{ChalandaRules.Stations}";
        _runLine = $"{_points} points  -  {_synced} in step  -  {where}: {dist} m {Compass[key.Item5]}";
    }

    private sealed partial class View : Control
    {
        public ChalandaGame Game = null!;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Amber = new(1f, 0.7f, 0.25f);
        private static readonly Color Green = new(0.45f, 0.95f, 0.5f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public override void _Draw()
        {
            var g = Game;
            if (g._svc?.ChalandaNow() is not { } m) return;
            // bottom-centre, clear of the hotbar and the middle of the screen (ui/ux-guidelines)
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.80f);
            bool message = g._message.Length > 0 && GameClock.Now - g._messageAt < 4;
            if (g._marching)
            {
                if (g._local() is { } p) g.RunLine(m.Site, p);
                Panel(c, 92);
                Pulse(c + new Vector2(-330, 0));
                Text("Chalandamarz", c + new Vector2(30, -22), UiTheme.FontHeading, UiTheme.Text, true);
                Text(g._runLine, c + new Vector2(30, 4), UiTheme.FontBody, Gold);
                if (message) Text(g._message, c + new Vector2(30, 28), UiTheme.FontSmall, g._grade == 2 ? Green : Amber);
                return;
            }
            if (g._nearStand)
            {
                Panel(c, 76);
                Text("Treichel stand", c + new Vector2(0, -14), UiTheme.FontHeading, UiTheme.Text, true);
                Text("Walk the village loop with the children and ring on the march beat.", c + new Vector2(0, 12), UiTheme.FontBody, Gold);
                if (message) Text(g._message, c + new Vector2(0, 34), UiTheme.FontSmall, Amber);
                return;
            }
            if (message) Text(g._message, c, UiTheme.FontBody, Amber);
        }

        /// <summary>The beat's pulse: a ring that closes on a fixed one at the beat, then flashes with the last grade.</summary>
        private void Pulse(Vector2 at)
        {
            double now = ClockSync.ServerNow;
            double phase = now / ChalandaRules.SecondsPerBeat;
            float closing = 1f - (float)(phase - Math.Floor(phase));   // 1 right after a beat, 0 at the next
            DrawArc(at, 16f, 0f, Mathf.Tau, 28, new Color(1f, 1f, 1f, 0.9f), 3f);
            DrawArc(at, 16f + 30f * closing, 0f, Mathf.Tau, 36, new Color(Gold, 0.35f + 0.65f * (1f - closing)), 3f);
            float age = (float)(GameClock.Now - Game._gradeAt);
            if (age < 0.4f && Game._grade >= 0)
                DrawCircle(at, 14f * (1f - age / 0.4f), Game._grade == 2 ? Green : Game._grade == 1 ? Gold : new Color(0.9f, 0.3f, 0.3f));
        }

        private void Panel(Vector2 c, float height) =>
            DrawRect(new Rect2(c.X - 380, c.Y - height / 2 - 12, 760, height), new Color(0, 0, 0, 0.45f), true);

        private void Text(string s, Vector2 at, int size, Color colour, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(380f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, 760f, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, 760f, size, colour);
        }
    }
}
