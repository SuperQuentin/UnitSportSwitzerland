using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.Fahnen;

/// <summary>
/// Fahnenschwingen, the player's side (#1269 part 5, <c>docs/notes/occasions/festival-fahnenschwingen.md</c>). At the flag ground of a running Älplerfest
/// (day 3 on) the interact key asks the server for a routine; the server answers with its start on the shared clock and a seed, both players
/// who asked in the same slot swing the same routine. A drum keeps 96 BPM; each figure is drawn with the mouse or the right stick
/// (<see cref="GestureRing"/>) and must be done by its last beat; the Hochwurf is held with the use key, let go on a beat and caught on the beat
/// it lands. The client records what it did and hands it in at the end; the server grades it again with the same rules and pays.
/// The flag is a rigid <see cref="FahnenFlag"/> moved by <see cref="FlagMotion"/>, the cloth is a shader. The pad route is the right stick and
/// the use button, so the VR controllers (<c>XrPad</c>) play it too; swinging the flag by hand is a later target.
/// </summary>
public partial class FahnenGame : CanvasLayer
{
    public enum Phase { Idle, Asking, Playing, Handing, Result }

    public const string NodeName = "FahnenGame";
    private const double WindowLate = 0.3, StickDeadzone = 0.45, MouseMinMove = 5.0, ResultSeconds = 6, HandInTimeout = 10, AskTimeout = 6;

    public static FahnenGame? Instance { get; private set; }

    /// <summary>Whether a routine is on: the look, the use key and the items belong to the flag (<see cref="FootPlayer.LookTaken"/>).</summary>
    public static bool Swinging => Instance is { _phase: Phase.Asking or Phase.Playing or Phase.Handing };

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private readonly AudioStreamPlayer _sfx = new() { Name = "Sfx", Bus = "Master" };
    private readonly Random _rng = new();
    private Phase _phase;
    private FahnenRoutine _routine = new(0, []);
    private double _start, _phaseAt;
    private int _figure, _lastBeat = int.MinValue;
    private readonly List<FigureInput> _inputs = new();
    private readonly byte[] _path = new byte[GestureRing.MaxSamples];
    private int _pathLen, _lastMoveMs;
    private double _sampleAt;
    private Vector2 _mouse;
    private bool _wasHeld;
    private int _pressMs, _releaseMs, _catchMs;
    private bool _pressed, _released, _caught;
    private FahnenFlag? _flag;
    private FootPlayer? _flagOwner;
    private Result _result;

    /// <summary>The look and use key, replaced by a check.</summary>
    internal Func<Vector2> Stick = () => Input.GetVector(PlayerInput.LookLeft, PlayerInput.LookRight, PlayerInput.LookUp, PlayerInput.LookDown);
    internal Func<bool> UseHeld = () => PlayerInput.Held(PlayerInput.UseItem);

    /// <summary>The grade of the last figure, for the HUD and the checks.</summary>
    internal FahnenGrade LastGrade { get; private set; }
    internal int GradedFigures { get; private set; }
    internal Phase Current => _phase;
    internal FahnenRoutine Routine => _routine;
    internal bool FlagShown => _flag != null && GodotObject.IsInstanceValid(_flag);
    internal (int Score, int Batzen, bool Synced, int Hits, int Figures, bool Dropped) LastResult => (_result.Score, _result.Batzen, _result.Synced, _result.Hits, _result.Figures, _result.Dropped);

    private record struct Result(int Score, int Batzen, bool Synced, int Hits, int Figures, bool Dropped);

    public FahnenGame(Func<FootPlayer?> local)
    {
        _local = local;
        Name = NodeName;
        Layer = 7;
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore, Game = this, Visible = false };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
        AddChild(_sfx);
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (FestivalService.Instance is { } svc)
        {
            svc.FahneStarted -= OnStarted;
            svc.FahneScored -= OnScored;
        }
        Release();
        if (Instance == this) Instance = null;
    }

    public override void _Ready() { }

    private bool _hooked;

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        svc.FahneStarted += OnStarted;
        svc.FahneScored += OnScored;
    }

    // ---- the interaction ---------------------------------------------------------------------------

    /// <summary>Whether <paramref name="p"/> stands on the open flag ground (the same test the server makes).</summary>
    public static bool AtGround(FootPlayer p)
    {
        if (Instance == null || FestivalService.Instance?.FahneGroundNow() is not { } g) return false;
        var at = p.Global;
        double de = at.E - g.E, dn = at.N - g.N;
        return Math.Sqrt(de * de + dn * dn) <= FestivalCatalog.FahneGroundRadius;
    }

    /// <summary>"[E] Swing the flag" on the ground; null elsewhere or while swinging.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { _phase: Phase.Idle or Phase.Result } game || !AtGround(p) || game._local() != p) return null;
        var g = FestivalService.Instance!.FahneGroundNow()!.Value;
        var origin = p.Origin;
        var outline = origin == null ? default
            : Outline.Box(null, origin.ToWorld(g.E, g.N, p.Global.Alt) + new Vector3(-0.8f, 0f, -0.8f), origin.ToWorld(g.E, g.N, p.Global.Alt) + new Vector3(0.8f, 2.4f, 0.8f));
        return new Interaction(game.PromptText(), outline);
    }

    private (InputDevice, string)? _promptKey;
    private string _prompt = "";

    private string PromptText()
    {
        var key = (PlayerInput.HintDevice, "");
        if (_promptKey == key) return _prompt;
        _promptKey = key;
        return _prompt = InputHints.Prompt(PlayerInput.InteractMount, "Swing the flag");
    }

    /// <summary>E / Y / a VR grip at the flag ground: asks for a routine.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { _phase: Phase.Idle or Phase.Result } game || !AtGround(p) || game._local() != p) return false;
        game.Ask();
        return true;
    }

    internal void Ask()
    {
        if (FestivalService.Instance is not { } svc) return;
        Hook();
        Release();
        _phase = Phase.Asking;
        _phaseAt = GameClock.Now;
        _view.Visible = true;
        svc.AskFahneStart();
    }

    private void OnStarted(double start, int seed)
    {
        if (_phase != Phase.Asking) return;
        _routine = FahnenRules.Plan(seed);
        _start = start;
        _figure = 0;
        _lastBeat = int.MinValue;
        _inputs.Clear();
        ResetFigure();
        GradedFigures = 0;
        _phase = Phase.Playing;
        _phaseAt = GameClock.Now;
        Raise();
    }

    private void OnScored(int score, int batzen, bool synced, int hits, int figures, bool dropped)
    {
        if (_phase != Phase.Handing) return;
        _result = new Result(score, batzen, synced, hits, figures, dropped);
        _view.Describe(_result);
        Release();
        _phase = Phase.Result;
        _phaseAt = GameClock.Now;
        Play(FahnenSounds.Catch);
    }

    // ---- the flag in the hands -----------------------------------------------------------------------

    private void Raise()
    {
        if (_local() is not { } p) return;
        _flagOwner = p;
        _flag = new FahnenFlag { Name = "FahnenFlag" };
        p.AddChild(_flag);
        p.LookTaken = true;
    }

    private void Release()
    {
        if (_flag != null && GodotObject.IsInstanceValid(_flag)) _flag.QueueFree();
        _flag = null;
        if (_flagOwner != null && GodotObject.IsInstanceValid(_flagOwner)) _flagOwner.LookTaken = false;
        _flagOwner = null;
    }

    // ---- the routine ---------------------------------------------------------------------------------

    private void ResetFigure()
    {
        _pathLen = 0;
        _lastMoveMs = 0;
        _mouse = Vector2.Zero;
        _pressed = _released = _caught = false;
        _pressMs = _releaseMs = _catchMs = 0;
        _sampleAt = 0;
    }

    public override void _Input(InputEvent e)
    {
        if (_phase == Phase.Playing && e is InputEventMouseMotion m && PlayerInput.IsLookMotion(m)) _mouse += m.Relative;
    }

    public override void _Process(double delta)
    {
        if (_phase == Phase.Idle) return;
        double now = GameClock.Now;
        switch (_phase)
        {
            case Phase.Asking:
                if (now - _phaseAt > AskTimeout) End("The flag swingers did not answer.");
                break;
            case Phase.Playing:
                Step();
                break;
            case Phase.Handing:
                if (now - _phaseAt > HandInTimeout) End("The score did not come back.");
                break;
            case Phase.Result:
                if (now - _phaseAt > ResultSeconds) End(null);
                break;
        }
        if (_view.Visible) _view.QueueRedraw();
    }

    private void End(string? why)
    {
        Release();
        _phase = Phase.Idle;
        _view.Visible = false;
        if (why != null) GD.Print($"[fahne] {why}");
    }

    /// <summary>Seconds into the routine on the shared clock (negative before the count-in's first beat).</summary>
    internal double Clock => Net.ClockSync.ServerNow - _start;

    private void Step()
    {
        double t = Clock;
        int beat = (int)Math.Floor(t / FahnenRules.SecondsPerBeat);
        if (beat != _lastBeat && t >= 0)
        {
            _lastBeat = beat;
            Play(FahnenSounds.Drum);
        }
        if (_figure >= _routine.Figures.Length) { HandIn(); return; }

        var slot = _routine.Figures[_figure];
        double begin = FahnenRules.Seconds(slot.StartBeat), end = FahnenRules.Seconds(slot.StartBeat + slot.Beats);
        if (t >= end + WindowLate)
        {
            if (!Finish(slot)) { HandIn(); return; }
            return;
        }
        if (t >= begin - WindowLate) Capture(slot, t);
        PoseFlag(slot, t, begin, end);
    }

    private void Capture(FigureSlot slot, double t)
    {
        int ms = (int)Math.Round(t * 1000);
        if (slot.Kind == FahnenFigure.Hochwurf)
        {
            bool held = UseHeld();
            if (held && !_wasHeld)
            {
                if (!_pressed) { _pressed = true; _pressMs = ms; }
                else if (_released && !_caught && ms - _releaseMs > 250) { _caught = true; _catchMs = ms; Play(FahnenSounds.Catch); }
            }
            else if (!held && _wasHeld && _pressed && !_released)
            {
                _released = true;
                _releaseMs = ms;
                Play(FahnenSounds.Whoosh);
            }
            _wasHeld = held;
            return;
        }
        // a gesture: 30 samples a second, one sector each while the stick or the mouse is moving
        if (t < _sampleAt) return;
        _sampleAt = t + 1.0 / GestureRing.SampleHz;
        var v = Stick();
        Vector2 dir;
        if (v.Length() >= StickDeadzone) dir = v;
        else if (_mouse.Length() >= MouseMinMove) dir = _mouse;
        else { _mouse = Vector2.Zero; return; }
        _mouse = Vector2.Zero;
        if (_pathLen < _path.Length)
        {
            _path[_pathLen++] = GestureRing.SectorOf(dir.X, dir.Y);
            _lastMoveMs = ms;
        }
    }

    /// <summary>Closes the figure's window: grades it as the server will and moves on. False when the flag was dropped (the routine is over).</summary>
    private bool Finish(FigureSlot slot)
    {
        FigureInput input;
        FahnenGrade grade;
        if (slot.Kind == FahnenFigure.Hochwurf)
        {
            input = new FigureInput(_pressed ? _pressMs : 0, _released ? _releaseMs : 0, _caught ? _catchMs : 0, []);
            grade = FahnenRules.GradeThrow(slot, input).Grade;
        }
        else
        {
            input = new FigureInput(_lastMoveMs, 0, 0, _path[.._pathLen]);
            grade = FahnenRules.GradeGesture(slot, input);
            if (grade >= FahnenGrade.Good) Play(FahnenSounds.Snap);
            else if (grade == FahnenGrade.Off) Play(FahnenSounds.Whoosh);
        }
        _inputs.Add(input);
        LastGrade = grade;
        GradedFigures++;
        _view.Graded(grade, GameClock.Now);
        ResetFigure();
        _figure++;
        return slot.Kind != FahnenFigure.Hochwurf || grade != FahnenGrade.Miss;
    }

    private void HandIn()
    {
        if (_phase != Phase.Playing) return;
        _phase = Phase.Handing;
        _phaseAt = GameClock.Now;
        var (stamps, path) = FahnenRules.ToWire(_inputs);
        FestivalService.Instance?.AskFahneRoutine(stamps, path);
        if (_flag != null) _flag.Swing = 0f;
    }

    private void PoseFlag(FigureSlot slot, double t, double begin, double end)
    {
        if (_flag == null || !GodotObject.IsInstanceValid(_flag)) return;
        FlagPose pose;
        if (slot.Kind == FahnenFigure.Hochwurf)
        {
            double held = _pressed ? ((_released ? _releaseMs : t * 1000) - _pressMs) / 1000.0 / FahnenRules.SecondsPerBeat : 0;
            int flight = _released ? (int)Math.Round((_releaseMs / 1000.0 - begin) / FahnenRules.SecondsPerBeat) + 1 : 0;
            double since = _released ? (t - _releaseMs / 1000.0) / FahnenRules.SecondsPerBeat : -1;
            pose = FlagMotion.Throw(held, since, Math.Max(2, flight));
        }
        else if (t < begin) pose = FlagMotion.Rest;
        else pose = FlagMotion.Of(slot.Kind, (t - begin) / (end - begin));
        // in front of the swinger's right shoulder, a hand's reach out: the player faces -Z
        _flag.Position = new Vector3(0.45f + (float)pose.X, 0.95f + (float)pose.Y, -0.55f);
        _flag.Rotation = new Vector3(0, 0, -(float)pose.Roll);
        _flag.Swing = (float)pose.Swing;
    }

    private void Play(SfxBank bank)
    {
        var (stream, pitch, db) = bank.Pick(_rng);
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.VolumeDb = db - 8f;
        _sfx.Play();
    }

    // ---- the screen ----------------------------------------------------------------------------------

    private static readonly string[] Names = ["Ufzug", "Mühlirad", "Achter", "Lufthieb", "Unterschwung", "Hochwurf"];
    private static readonly string[] Words = ["Raise: up", "Mill wheel: a full circle", "Figure eight", "Sweep: left or right", "Under swing: down", "High throw"];

    private sealed partial class View : Control
    {
        public FahnenGame Game = null!;
        private FahnenGrade _grade;
        private double _gradeAt = double.NegativeInfinity;
        private string _resultLine = "", _resultSub = "", _hint = "";
        private int _hintFigure = -1;
        private InputDevice _hintDevice;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Green = new(0.45f, 0.95f, 0.5f);
        private static readonly Color Red = new(0.9f, 0.25f, 0.2f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public void Graded(FahnenGrade g, double at) { _grade = g; _gradeAt = at; }

        public void Describe(Result r)
        {
            _resultLine = r.Dropped ? $"Dropped! {r.Score} points" : $"{r.Score} points";
            _resultSub = $"{r.Hits}/{r.Figures} figures on the beat"
                + (r.Synced ? "  -  in step x" + FahnenRules.SyncBonus.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "")
                + $"  -  +{r.Batzen} Batzen";
        }

        public override void _Draw()
        {
            var g = Game;
            // bottom-centre, clear of the hotbar and the middle of the screen (ui/ux-guidelines)
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.80f);
            switch (g._phase)
            {
                case Phase.Asking:
                    Text("Getting in line...", c, UiTheme.FontHeading, UiTheme.Text);
                    return;
                case Phase.Handing:
                    Text("The judges are counting...", c, UiTheme.FontHeading, UiTheme.Text);
                    return;
                case Phase.Result:
                    DrawPanel(c);
                    Text(_resultLine, c + new Vector2(0, -6), UiTheme.FontTitle, Gold, bold: true);
                    Text(_resultSub, c + new Vector2(0, 30), UiTheme.FontBody, UiTheme.Text);
                    return;
            }
            DrawRoutine(c);
        }

        private void DrawPanel(Vector2 c) =>
            DrawRect(new Rect2(c.X - 300, c.Y - 56, 600, 116), new Color(0, 0, 0, 0.45f), true);

        private void DrawRoutine(Vector2 c)
        {
            var g = Game;
            double t = g.Clock, spb = FahnenRules.SecondsPerBeat;
            double beats = t / spb;
            float phase = (float)(beats - Math.Floor(beats));
            const float target = 24f;
            if (g._figure >= g._routine.Figures.Length) return;
            var slot = g._routine.Figures[g._figure];
            double begin = FahnenRules.Seconds(slot.StartBeat), end = FahnenRules.Seconds(slot.StartBeat + slot.Beats);
            bool counting = t < begin;
            float flash = Mathf.Clamp(1f - phase / 0.15f, 0f, 1f);
            DrawCircle(c, target, new Color(UiTheme.Text, 0.15f + 0.5f * flash));
            DrawArc(c, target, 0, Mathf.Tau, 40, UiTheme.Text, 3f, true);
            if (t >= 0) DrawArc(c, target + 64f * (1f - phase), 0, Mathf.Tau, 48, new Color(UiTheme.Text, 0.35f + 0.6f * phase), 2.5f, true);

            // the figure to draw and how long it has: a bar that runs out on the last beat
            Refresh(slot, g._figure);
            Text(Names[(int)slot.Kind], c + new Vector2(0, -target - 54f), UiTheme.FontHeading, counting ? UiTheme.TextDim : UiTheme.Text, bold: true);
            Text(_hint, c + new Vector2(0, -target - 28f), UiTheme.FontSmall, UiTheme.TextDim);
            DrawGlyph(slot.Kind, c + new Vector2(-120f, 0), 26f);
            float w = 200f, frac = counting ? 0f : (float)Math.Clamp((t - begin) / (end - begin), 0, 1);
            var bar = new Rect2(c.X + 50f, c.Y - 5f, w, 10f);
            DrawRect(bar, new Color(0, 0, 0, 0.5f), true);
            DrawRect(new Rect2(bar.Position, new Vector2(w * frac, 10f)), frac > 0.85f ? Gold : UiTheme.Text, true);
            DrawRect(new Rect2(bar.End.X - 3f, bar.Position.Y - 3f, 3f, 16f), Gold, true);
            Text($"{g._figure + 1}/{g._routine.Figures.Length}", c + new Vector2(0, target + 34f), UiTheme.FontSmall, UiTheme.TextDim);

            float age = (float)(GameClock.Now - _gradeAt);
            if (age < 0.7f)
            {
                var (word, colour) = _grade switch
                {
                    FahnenGrade.Perfect => ("PERFECT", Gold),
                    FahnenGrade.Good => ("GOOD", Green),
                    FahnenGrade.Off => ("OFF BEAT", UiTheme.Text),
                    _ => ("MISS", Red),
                };
                Text(word, c + new Vector2(0, -target - 90f - age * 30f), UiTheme.FontHeading, new Color(colour, 1f - age / 0.7f), bold: true);
            }
        }

        /// <summary>The words under the figure's name: cached per figure and device, rebuilt only when either changes.</summary>
        private void Refresh(FigureSlot slot, int index)
        {
            var device = PlayerInput.HintDevice;
            if (index == _hintFigure && device == _hintDevice) return;
            _hintFigure = index;
            _hintDevice = device;
            string pad = device is InputDevice.Gamepad or InputDevice.VR ? "the right stick" : "the mouse";
            _hint = slot.Kind == FahnenFigure.Hochwurf
                ? InputHints.Format("Hold {use_item}, let go on a beat, catch with {use_item}")
                : Words[(int)slot.Kind] + " - draw it with " + pad;
        }

        /// <summary>A small picture of the stroke to draw; polylines made on the spot from a handful of points (a few per frame while swinging).</summary>
        private void DrawGlyph(FahnenFigure kind, Vector2 at, float r)
        {
            var colour = new Color(UiTheme.Text, 0.9f);
            switch (kind)
            {
                case FahnenFigure.Ufzug: Arrow(at + new Vector2(0, r), at + new Vector2(0, -r), colour); break;
                case FahnenFigure.Unterschwung: Arrow(at + new Vector2(0, -r), at + new Vector2(0, r), colour); break;
                case FahnenFigure.Lufthieb: Arrow(at + new Vector2(-r, 0), at + new Vector2(r, 0), colour); break;
                case FahnenFigure.Muehlirad:
                    DrawArc(at, r, -Mathf.Pi * 0.5f, Mathf.Pi * 1.4f, 32, colour, 3f, true);
                    Arrow(at + new Vector2(r * 0.9f, -r * 0.5f), at + new Vector2(r * 0.4f, -r * 0.95f), colour);
                    break;
                case FahnenFigure.Achter:
                    DrawArc(at + new Vector2(-r * 0.5f, 0), r * 0.5f, 0, Mathf.Tau, 20, colour, 3f, true);
                    DrawArc(at + new Vector2(r * 0.5f, 0), r * 0.5f, 0, Mathf.Tau, 20, colour, 3f, true);
                    break;
                default:
                    DrawArc(at, r * 0.8f, Mathf.Pi, Mathf.Tau, 20, colour, 3f, true);
                    Arrow(at + new Vector2(0, r * 0.5f), at + new Vector2(0, -r), colour);
                    break;
            }
        }

        private void Arrow(Vector2 from, Vector2 to, Color colour)
        {
            DrawLine(from, to, colour, 3f, true);
            var back = (from - to).Normalized() * 9f;
            DrawLine(to, to + back.Rotated(0.5f), colour, 3f, true);
            DrawLine(to, to + back.Rotated(-0.5f), colour, 3f, true);
        }

        private void Text(string s, Vector2 at, int size, Color colour, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(300f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, 600f, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, 600f, size, colour);
        }
    }
}
