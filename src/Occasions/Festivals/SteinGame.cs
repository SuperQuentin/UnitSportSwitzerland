using Godot;
using UnitSport.Adventure;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// The client side of Steinstossen (#1269 part 3): the prompt at the stone, the attempt (lift on a meter, three steps and the push on a
/// 90 BPM drum, <see cref="SteinRules"/>), the HUD beside the body, and what everyone near sees: the stone flying its arc and a stake with
/// the distance where it fell. This node only plays and records presses; the server (<see cref="SteinService"/>) decides the meter,
/// the grades and the distance. Walk, items and jump are taken while an attempt runs (<see cref="FootPlayer.WalkControls"/>,
/// <see cref="FootPlayer.GameBusy"/>, <see cref="FootPlayer.JumpTaken"/>). Controls: E at the stone, hold <c>use_item</c> and
/// let go on the peak, <c>use_item</c> on each beat, <c>jump</c> for another stone, E again to put it down.
/// </summary>
public partial class SteinGame : Node3D, IOriginContainer
{
    public const string NodeName = "SteinGame";

    public static SteinGame? Instance { get; private set; }

    public enum Phase { Idle, Asking, Ready, Lifting, Run, Sent, Done }

    private const int Marks = 6, Flights = 3;
    private const double StoneReach = 2.6, MarkSeconds = 90;

    private readonly Func<FootPlayer?> _me;
    private readonly WorldOrigin _origin;
    private readonly Func<Vector3, float?> _groundAt;
    private readonly View _view = new();
    private readonly Random _rng = new(1269);

    private sealed class Flight
    {
        public MeshInstance3D Node = null!;
        public SteinLanding? Landing;
        public double T, Total;
        public float GroundY;
    }

    private sealed class MarkSlot
    {
        public Node3D Node = null!;
        public Label3D Label = null!;
        public double Until;
    }

    private readonly Flight[] _flights = new Flight[Flights];
    private readonly MarkSlot[] _marks = new MarkSlot[Marks];
    private int _nextMark;
    private AudioStreamPlayer _own = null!;
    private AudioStreamPlayer3D _there = null!;

    // the ground, refreshed twice a second: the running festival that has the stone, where it lies, how high
    private SteinGround? _ground;
    private RunningFestival? _run;
    private float _stoneY;
    private double _nextRefresh;
    private (string Text, InputDevice Device, int Cls, int Left)? _promptKey;
    private string? _prompt;

    // the attempt
    private SteinService? _service;
    private SteinAttempt? _attempt;
    private int _cls;
    private double _askUntil, _doneAt, _readyAt;
    private readonly double[] _stamps = new double[SteinRules.Stamp.Count];
    private readonly bool[] _pressed = new bool[SteinRules.Stamp.Count];
    private readonly bool[] _ticked = new bool[5];
    private double _pitch = SteinRules.BestAngle;
    private bool _useHeld, _jumpHeld, _eHeld, _locked;
    private Func<(Vector3 Wish, bool Run)>? _walkWas;
    private FootPlayer? _lockedBody;

    public Phase State { get; private set; }

    /// <summary>The distance of the put this client made last, m (checks), and how many landings were drawn near it.</summary>
    public SteinLanding? LastMine { get; private set; }
    public int LandingsSeen { get; private set; }
    public int DrumBeats { get; private set; }
    public int StakesShown { get; private set; }

    /// <summary>The attempt open now, the stamps and pitch of the last put sent, and a camera pitch a check fixes (never set in the game).</summary>
    public SteinAttempt? Attempt => _attempt;
    public double[]? LastSent { get; private set; }
    public double LastPitch { get; private set; }
    public double? PitchOverride { get; set; }

    /// <summary>What the HUD is saying now (checks).</summary>
    public string Message => _view.Message;

    public SteinGame(Func<FootPlayer?> me, WorldOrigin origin, Func<Vector3, float?> groundAt)
    {
        Name = NodeName;
        _me = me;
        _origin = origin;
        _groundAt = groundAt;
    }

    public SteinGame() : this(() => null, WorldOrigin.SwissDefault(), _ => null) { }

    public static SteinGame Create(Node world, Func<FootPlayer?> me, WorldOrigin origin, Func<Vector3, float?> groundAt)
    {
        var g = new SteinGame(me, origin, groundAt);
        world.AddChild(g);
        return g;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        Unlock();
        if (_service != null)
        {
            _service.Began -= OnBegan;
            _service.Landed -= OnLanded;
            _service.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        var layer = new CanvasLayer { Name = "SteinHud", Layer = 7 };
        _view.MouseFilter = Control.MouseFilterEnum.Ignore;
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _view.Game = this;
        layer.AddChild(_view);
        AddChild(layer);

        var stone = SteinProps.Stone.Mesh;
        var stoneMaterial = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        stoneMaterial.SetShaderParameter("flicker", 0f);
        FogUniforms.Apply(stoneMaterial);
        for (int i = 0; i < Flights; i++)
        {
            _flights[i] = new Flight { Node = new MeshInstance3D { Name = $"Flight{i}", Mesh = stone, MaterialOverride = stoneMaterial, Visible = false } };
            AddChild(_flights[i].Node);
        }
        var markMesh = SteinProps.Mark.Mesh;
        for (int i = 0; i < Marks; i++)
        {
            var node = new Node3D { Name = $"Mark{i}", Visible = false };
            node.AddChild(new MeshInstance3D { Mesh = markMesh, MaterialOverride = stoneMaterial });
            var label = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.006f, FontSize = 64, OutlineSize = 14,
                NoDepthTest = false, Position = new Vector3(0, 1.45f, 0), Modulate = new Color(1, 0.96f, 0.82f),
            };
            node.AddChild(label);
            AddChild(node);
            _marks[i] = new MarkSlot { Node = node, Label = label };
        }
        _own = new AudioStreamPlayer { Name = "Own", Bus = SfxBus.Player };
        _there = new AudioStreamPlayer3D { Name = "There", Bus = SfxBus.Name, MaxDistance = 80f, UnitSize = 8f };
        AddChild(_own);
        AddChild(_there);
        // touch the banks now, so the first beat does not stall the frame it is heard in
        _ = SteinSounds.Drum; _ = SteinSounds.Accent; _ = SteinSounds.Grunt; _ = SteinSounds.Thud; _ = SteinSounds.Slip;
        SetProcess(true);
    }

    private SteinService? Service
    {
        get
        {
            if (_service == null && SteinService.Instance is { } s)
            {
                _service = s;
                s.Began += OnBegan;
                s.Landed += OnLanded;
                s.Refused += OnRefused;
            }
            return _service;
        }
    }

    // ---- the prompt at the stone -----------------------------------------------------------------

    private void Refresh()
    {
        _nextRefresh = GameClock.Now + 0.5;
        _ground = null;
        _run = null;
        if (Service?.Open() is { } o)
        {
            _run = o.Run;
            _ground = SteinGround.Of(o.Site);
            _stoneY = _groundAt(_origin.ToWorld(_ground.Value.E, _ground.Value.N, 0)) ?? _stoneY;
        }
    }

    /// <summary>Is the body at the stone: within reach on the ground's plane.</summary>
    private bool AtStone(FootPlayer p) =>
        _ground is { } g && g.DistanceTo(p.Global.E, p.Global.N) <= StoneReach;

    private int PutsLeft()
    {
        if (_run == null || FestivalService.Instance is not { } f) return SteinRules.AttemptsPerDay;
        int played = f.Mine.Instances.GetValueOrDefault(_run.Instance)?.Played.GetValueOrDefault(FestivalRules.PlayKey(SteinRules.GameId, _run.Day)) ?? 0;
        return Math.Max(0, SteinRules.AttemptsPerDay - played);
    }

    /// <summary>"[E] Put the stone" at an open festival stone, and its outline; null elsewhere.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } g || g.State != Phase.Idle || g._ground == null || !g.AtStone(p)) return null;
        return new(g.PromptText(), Outline.Box(null, new Transform3D(Basis.Identity, g.StoneWorld() + Vector3.Up * 0.2f), new Vector3(0.6f, 0.45f, 0.6f)));
    }

    /// <summary>E / Y at the stone: lifts it (asks the server for an attempt). False when no stone is at hand.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } g || g.State != Phase.Idle || g._ground == null || !g.AtStone(p) || g.Service is not { } svc) return false;
        if (g.PutsLeft() <= 0)
        {
            g._view.Say("The stone rests until tomorrow.", UiTheme.Warn);
            return true;
        }
        g._cls = Math.Clamp(g._cls, 0, SteinRules.Classes.Length - 1);
        g.State = Phase.Asking;
        g._askUntil = GameClock.Now + 4;
        g._eHeld = true;
        g.Lock(p);
        svc.AskBegin(g._cls);
        return true;
    }

    private Vector3 StoneWorld() => _ground is { } g ? _origin.ToWorld(g.E, g.N, 0) with { Y = _stoneY } : Vector3.Zero;

    private string PromptText()
    {
        int left = PutsLeft();
        var key = (State.ToString(), PlayerInput.HintDevice, _cls, left);
        if (_prompt != null && _promptKey == key) return _prompt;
        _promptKey = key;
        return _prompt = left > 0
            ? InputHints.Prompt(PlayerInput.InteractMount, $"Put the stone ({left} of {SteinRules.AttemptsPerDay} today)")
            : InputHints.Prompt(PlayerInput.InteractMount, "The stone rests until tomorrow");
    }

    // ---- the attempt ---------------------------------------------------------------------------

    private void Lock(FootPlayer p)
    {
        if (_locked) return;
        _locked = true;
        _lockedBody = p;
        _walkWas = p.WalkControls;
        p.WalkControls = static () => (Vector3.Zero, false);
        p.GameBusy = true;
    }

    private void Unlock()
    {
        if (!_locked) return;
        _locked = false;
        if (_lockedBody != null && IsInstanceValid(_lockedBody))
        {
            _lockedBody.WalkControls = _walkWas;
            _lockedBody.GameBusy = false;
            _lockedBody.JumpTaken = false;
        }
        _lockedBody = null;
        _walkWas = null;
    }

    private void End(string? why = null, Color? colour = null)
    {
        Unlock();
        State = Phase.Idle;
        _attempt = null;
        _view.Meter = 0;
        if (why != null) _view.Say(why, colour ?? UiTheme.Warn);
    }

    private void OnBegan(SteinAttempt a)
    {
        if (State is not (Phase.Asking or Phase.Ready)) return;
        _attempt = a;
        _cls = a.Class;
        State = Phase.Ready;
        _readyAt = GameClock.Now;
        Array.Clear(_pressed);
        Array.Clear(_ticked);
        _view.Result = "";
        _view.Reset(_cls);
    }

    private void OnRefused(string why)
    {
        if (State is Phase.Asking or Phase.Ready or Phase.Sent) End(why);
        else _view.Say(why, UiTheme.Warn);
    }

    private bool IsMine(long thrower) =>
        thrower == (Multiplayer.MultiplayerPeer != null ? Multiplayer.GetUniqueId() : AdventureService.LocalPeer);

    private void OnLanded(SteinLanding l)
    {
        LandingsSeen++;
        bool mine = IsMine(l.Thrower);
        if (mine)
        {
            LastMine = l;
            if (l.Foul != null)
            {
                Play(SteinSounds.Slip);
                _view.Result = "";
                State = Phase.Done;
                _doneAt = GameClock.Now;
                _view.Say(l.Foul, UiTheme.Bad);
                return;
            }
            _view.Result = $"{l.Metres:F2} m";
            _view.Detail = l.Batzen > 0 ? $"+{l.Batzen} Batzen   {l.Points} points" : $"{l.Points} points";
            _view.ResultColour = l.Metres >= SteinRules.Class(l.Class).Par * 0.9 ? UiTheme.Good : UiTheme.Text;
            State = Phase.Done;
            _doneAt = GameClock.Now;
        }
        if (l.Foul != null) return;
        // the stone flies from its place along the throw
        foreach (var f in _flights)
        {
            if (f.Landing != null) continue;
            f.Landing = l;
            f.T = 0;
            f.Total = SteinRules.FlightTime(l.Speed, l.Angle);
            f.GroundY = _stoneY;
            f.Node.Visible = true;
            return;
        }
        Land(l);   // every flight busy: only its stake
    }

    // ---- every frame ---------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        double wall = GameClock.Now;
        if (wall >= _nextRefresh) Refresh();
        StepFlights(delta);
        StepMarks(wall);
        var p = _me();
        if (State == Phase.Idle) return;
        if (p == null || !IsInstanceValid(p)) { End(); return; }
        p.JumpTaken = true;
        bool use = PlayerInput.Held(PlayerInput.UseItem), jump = PlayerInput.Held(PlayerInput.Jump), e = PlayerInput.Held(PlayerInput.InteractMount);
        bool usePressed = use && !_useHeld, jumpPressed = jump && !_jumpHeld, ePressed = e && !_eHeld;
        (_useHeld, _jumpHeld, _eHeld) = (use, jump, e);
        double now = ClockSync.ServerNow;

        switch (State)
        {
            case Phase.Asking:
                if (wall > _askUntil) End("The stone is not answering.");
                break;
            case Phase.Ready:
                if (ePressed || wall - _readyAt > 90 || !AtStone(p)) { End("You put the stone down."); break; }
                if (jumpPressed)
                {
                    _cls = (_cls + 1) % SteinRules.Classes.Length;
                    State = Phase.Asking;
                    _askUntil = wall + 4;
                    Service?.AskBegin(_cls);
                }
                else if (usePressed)
                {
                    _stamps[SteinRules.Stamp.Down] = now;
                    State = Phase.Lifting;
                    Play(SteinSounds.Grunt);
                }
                break;
            case Phase.Lifting:
                if (_attempt == null) { End(); break; }
                _view.Meter = (float)SteinRules.Meter(_attempt.Seed, _attempt.Class, now - _stamps[SteinRules.Stamp.Down]);
                if (!use)
                {
                    if (now - _stamps[SteinRules.Stamp.Down] < 0.15) { State = Phase.Ready; break; }   // a tap, not a lift
                    _stamps[SteinRules.Stamp.Up] = now;
                    State = Phase.Run;
                    _view.Meter = 0;
                    _view.Beat = 0;
                }
                else if (now - _stamps[SteinRules.Stamp.Down] > 20) End("You could not hold it any longer.");
                break;
            case Phase.Run:
                RunUp(p, now, usePressed);
                break;
            case Phase.Sent:
                if (wall - _doneAt > 6) End("The judges lost your put.");
                break;
            case Phase.Done:
                if (wall - _doneAt > 4.5) End();
                break;
        }
    }

    private void RunUp(FootPlayer p, double now, bool usePressed)
    {
        double up = _stamps[SteinRules.Stamp.Up], spb = SteinRules.SecondsPerBeat, late = AudioServer.GetOutputLatency();
        for (int n = 1; n <= 4; n++)
            if (!_ticked[n] && now >= SteinRules.BeatAt(up, n) - late)
            {
                _ticked[n] = true;
                DrumBeats++;
                Play(n == 4 ? SteinSounds.Accent : SteinSounds.Drum);
                _view.Beat = n;
            }
        if (usePressed)
        {
            int n = (int)Math.Round((now - up) / spb);
            if (n is >= 1 and <= 4 && !_pressed[SteinRules.Stamp.Step1 + n - 1])
            {
                _pressed[SteinRules.Stamp.Step1 + n - 1] = true;
                _stamps[SteinRules.Stamp.Step1 + n - 1] = now;
                _view.Grade(SteinRules.Grade(now, SteinRules.BeatAt(up, n)));
                if (n == 4) _pitch = PitchOverride ?? CameraPitch();
            }
        }
        if (now < SteinRules.BeatAt(up, 4) + PogoBeatWindow + 0.1) return;
        // the push is over: a beat nobody pressed on is a press a fifth of a second late (a miss), then the stamps keep their order
        for (int n = 1; n <= 4; n++)
        {
            int i = SteinRules.Stamp.Step1 + n - 1;
            if (!_pressed[i]) _stamps[i] = SteinRules.BeatAt(up, n) + 0.2;
            if (_stamps[i] <= _stamps[i - 1] + 0.1) _stamps[i] = _stamps[i - 1] + 0.1;
        }
        if (!_pressed[SteinRules.Stamp.Push]) _pitch = PitchOverride ?? CameraPitch();
        LastSent = (double[])_stamps.Clone();
        LastPitch = _pitch;
        State = Phase.Sent;
        _doneAt = GameClock.Now;
        Service?.AskThrow(_attempt!.Id, (double[])_stamps.Clone(), _pitch);
    }

    private static readonly double PogoBeatWindow = Concert.PogoBeat.Window(SteinRules.SecondsPerBeat);

    /// <summary>The camera's pitch above the horizon, degrees: the throw's angle.</summary>
    public double CameraPitch()
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return SteinRules.BestAngle;
        return Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-cam.GlobalBasis.Z.Y, -1f, 1f)));
    }

    private void Play(SfxBank bank)
    {
        var (stream, pitch, db) = bank.Pick(_rng);
        _own.Stream = stream;
        _own.PitchScale = pitch;
        _own.VolumeDb = db;
        _own.Play();
    }

    // ---- what everyone sees -----------------------------------------------------------------------

    private void StepFlights(double delta)
    {
        foreach (var f in _flights)
        {
            if (f.Landing is not { } l) continue;
            f.T += delta;
            var g = new SteinGround(l.E, l.N, Math.Cos(l.Yaw), Math.Sin(l.Yaw));
            var (along, up) = SteinRules.Position(l.Speed, l.Angle, Math.Min(f.T, f.Total));
            var (e, n) = g.Along(along);
            f.Node.GlobalPosition = _origin.ToWorld(e, n, 0) with { Y = f.GroundY + (float)up };
            f.Node.Rotation = new Vector3((float)f.T * 3f, l.Yaw, (float)f.T * 2f);
            if (f.T < f.Total) continue;
            f.Node.Visible = false;
            f.Landing = null;
            Land(l);
        }
    }

    /// <summary>The stone is down: a thud, a puff-free stake with the distance, for this peer's ears and eyes.</summary>
    private void Land(SteinLanding l)
    {
        var g = new SteinGround(l.E, l.N, Math.Cos(l.Yaw), Math.Sin(l.Yaw));
        var (e, n) = g.Along(l.Metres);
        var world = _origin.ToWorld(e, n, 0);
        world.Y = _groundAt(world) ?? _stoneY;
        var slot = _marks[_nextMark];
        _nextMark = (_nextMark + 1) % Marks;
        slot.Node.GlobalPosition = world;
        slot.Node.Rotation = new Vector3(0, l.Yaw, 0);
        slot.Label.Text = $"{(l.Name.Length > 0 ? l.Name + "  " : "")}{l.Metres:F2} m";
        slot.Node.Visible = true;
        slot.Until = GameClock.Now + MarkSeconds;
        StakesShown++;
        var (stream, pitch, db) = SteinSounds.Thud.Pick(_rng);
        _there.GlobalPosition = world;
        _there.Stream = stream;
        _there.PitchScale = pitch;
        _there.VolumeDb = db;
        _there.Play();
    }

    private void StepMarks(double wall)
    {
        foreach (var m in _marks)
            if (m.Node.Visible && wall > m.Until) m.Node.Visible = false;
    }

    // ---- the HUD -------------------------------------------------------------------------------

    /// <summary>
    /// Beside the body, as the concert pogo's ring is, the centre clear. Redrawn only while an attempt runs; every string is formatted when
    /// it changes (a state, a device, a beat), never per frame.
    /// </summary>
    private sealed partial class View : Control
    {
        public SteinGame Game = null!;
        public float Meter;
        public int Beat;
        public string Result = "", Detail = "";
        public Color ResultColour = UiTheme.Text;
        public string Message = "";

        private string _title = "", _hint = "", _grade = "", _say = "";
        private Phase _shown = (Phase)(-1);
        private InputDevice _shownDevice = (InputDevice)(-1);
        private double _gradeAt = double.NegativeInfinity, _sayAt = double.NegativeInfinity;
        private Color _gradeColour = UiTheme.Text, _sayColour = UiTheme.Text;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public void Reset(int cls)
        {
            _title = SteinRules.Class(cls).Title;
            _shown = (Phase)(-1);
        }

        public void Grade(StepGrade g)
        {
            _gradeAt = GameClock.Now;
            (_grade, _gradeColour) = g switch
            {
                StepGrade.Perfect => ("PERFECT", Gold),
                StepGrade.Good => ("GOOD", UiTheme.Good),
                _ => ("MISS", UiTheme.Bad),
            };
        }

        public void Say(string text, Color colour)
        {
            _say = text;
            _sayColour = colour;
            _sayAt = GameClock.Now;
            Message = text;
        }

        public override void _Process(double delta)
        {
            bool live = Game.State != Phase.Idle || GameClock.Now - _sayAt < 3;
            if (!live)
            {
                if (Visible) Visible = false;
                return;
            }
            Visible = true;
            if (Game.State != _shown || PlayerInput.HintDevice != _shownDevice)
            {
                _shown = Game.State;
                _shownDevice = PlayerInput.HintDevice;
                _hint = InputHints.Format(_shown switch
                {
                    Phase.Ready => "Hold {use_item} to lift, let go on the peak\n{jump} another stone   {interact_mount} put it down",
                    Phase.Lifting => "Let go of {use_item} on the peak",
                    Phase.Run => "{use_item} on every beat; look up to 40 degrees for the push",
                    Phase.Asking => "Lifting the stone...",
                    Phase.Sent => "The judges measure...",
                    _ => "",
                });
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            var c = new Vector2(Size.X * 0.5f + Mathf.Min(260f, Size.X * 0.22f), Size.Y * 0.56f);
            float age = (float)(GameClock.Now - _sayAt);
            if (Game.State == Phase.Idle)
            {
                if (age < 3) Text(_say, c, UiTheme.FontBody, new Color(_sayColour, Mathf.Clamp(3f - age, 0f, 1f)), bold: true);
                return;
            }
            Text(_title, c + new Vector2(0, -70f), UiTheme.FontBody, UiTheme.Text, bold: true);
            switch (Game.State)
            {
                case Phase.Lifting:
                    DrawMeter(c + new Vector2(0, -30f));
                    break;
                case Phase.Run:
                    DrawBeats(c);
                    break;
                case Phase.Done:
                    Text(Result, c + new Vector2(0, -6f), UiTheme.FontTitle, ResultColour, bold: true);
                    Text(Detail, c + new Vector2(0, 30f), UiTheme.FontBody, Gold, bold: true);
                    if (age < 3) Text(_say, c + new Vector2(0, 60f), UiTheme.FontBody, new Color(_sayColour, Mathf.Clamp(3f - age, 0f, 1f)), bold: true);
                    break;
            }
            float hintY = Game.State == Phase.Done ? 90f : 108f;
            foreach (var (line, k) in _hint.Split('\n').Select((l, k) => (l, k)))
                Text(line, c + new Vector2(0, hintY + k * 18f), UiTheme.FontSmall, UiTheme.TextDim);
        }

        private void DrawMeter(Vector2 top)
        {
            const float h = 180f, w = 26f;
            var box = new Rect2(top.X - w / 2, top.Y, w, h);
            DrawRect(box, new Color(UiTheme.Glass, 0.9f));
            // the green band is the last fifth: let go while the marker is in it
            DrawRect(new Rect2(box.Position.X, box.Position.Y, w, h * 0.12f), new Color(UiTheme.Good, 0.65f));
            float y = box.End.Y - h * Meter;
            DrawRect(new Rect2(box.Position.X - 6f, y - 2f, w + 12f, 4f), Meter >= 0.95f ? Gold : UiTheme.Text);
            DrawRect(box, new Color(UiTheme.Text, 0.5f), false, 1.5f);
        }

        private void DrawBeats(Vector2 c)
        {
            // four lamps: three steps and the push; the one on the beat lights
            for (int i = 1; i <= 4; i++)
            {
                var p = c + new Vector2((i - 2.5f) * 44f, -20f);
                bool on = Beat >= i;
                DrawCircle(p, i == 4 ? 17f : 13f, new Color(i == 4 ? Gold : UiTheme.Text, on ? 0.95f : 0.2f));
                DrawArc(p, i == 4 ? 17f : 13f, 0, Mathf.Tau, 24, UiTheme.Text, 2f, true);
            }
            float age = (float)(GameClock.Now - _gradeAt);
            if (age < 0.6f) Text(_grade, c + new Vector2(0, -64f - age * 24f), UiTheme.FontHeading, new Color(_gradeColour, 1f - age * 1.6f), bold: true);
        }

        private void Text(string s, Vector2 at, int size, Color colour, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(240f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, 480f, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, 480f, size, colour);
        }
    }
}
