using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>
/// The alphorn and yodel game on this client (#1269): E at the festival ground's row asks the server for a seat; the song's seed and this
/// player's voice come back; the notes scroll towards a line and the player picks the partial (wheel, W/S, the right stick), holds blow
/// (use_item, RT) from a note's start to its end, or taps jump for the yodel voice. Every blow sounds at once and the valley answers it from
/// the real wall (<see cref="EchoFinder"/>, once per blow). When the song is over the blows go to the server, which grades them against the
/// same <see cref="AlphornRules"/> this screen used, scores the harmony with the other voices and pays. VR drives it through the pad route
/// (<c>XrPad</c>): the right stick and the trigger.
/// </summary>
public partial class HornGame : Node
{
    public enum Phase { Idle, Asking, Playing, Sent }

    public static HornGame? Instance { get; private set; }

    private readonly Func<FootPlayer?> _local;
    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<ChunkManager?> _chunks;
    private readonly View _view;
    private readonly CanvasLayer _layer = new() { Layer = 7, Name = "HornHud" };
    private readonly Node3D _echoes = new() { Name = "Echoes" };
    private readonly AudioStreamPlayer _tone = new() { Name = "Tone", Bus = SfxBus.Name };
    private readonly AudioStreamPlayer _tick = new() { Name = "Tick", Bus = SfxBus.Name, VolumeDb = -14f };
    private readonly AudioStreamPlayer3D[] _echoVoices = new AudioStreamPlayer3D[4];
    private int _nextEcho;
    private FestivalService? _svc;

    // the song
    private AlphornChart _chart = AlphornChart.Make(0);
    private HornPart _part;
    private HornNote[] _notes = [];
    private int[] _lanes = [];
    private int _lane;
    private string _festival = "";
    private double _t0, _askedAt, _lastFrame, _sentAt = double.NegativeInfinity, _resultAt = double.NegativeInfinity;
    private readonly List<HornPlay> _plays = new();
    private bool _blowing, _wasBlow, _wasUp, _wasDown, _wasJump, _scripted;
    private double _blowOn;
    private int _blowPartial, _lastTick = -1;
    private double _breath = AlphornRules.BreathBeats;
    private double _lastOff;

    // what the screen says
    private string _message = "", _grade = "", _hint = "", _header = "";
    private string[] _laneLabels = [];
    private string[] _resultLines = [];
    private double _messageAt = double.NegativeInfinity, _gradeAt = double.NegativeInfinity;
    private Color _gradeColour = UiTheme.Text;
    private HornResult? _result;
    private (HornPart, InputDevice) _hintKey = (HornPart.Melody, (InputDevice)(-1));

    public HornGame(Func<FootPlayer?> local, Func<WorldOrigin?> origin, Func<ChunkManager?> chunks)
    {
        _local = local;
        _origin = origin;
        _chunks = chunks;
        Name = "HornGame";
        _view = new View(this) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(_view);
        _layer.Visible = false;
        AddChild(_layer);
        AddChild(_echoes);
        AddChild(_tone);
        AddChild(_tick);
        for (int i = 0; i < _echoVoices.Length; i++)
        {
            _echoVoices[i] = new AudioStreamPlayer3D { Name = $"Echo{i}", Bus = SfxBus.Name, UnitSize = 80f, MaxDistance = 5000f, AttenuationFilterCutoffHz = 3000f };
            _echoes.AddChild(_echoVoices[i]);
        }
    }

    public HornGame() : this(() => null, () => null, () => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (_svc != null)
        {
            _svc.HornSeated -= OnSeat;
            _svc.HornGraded -= OnGraded;
            _svc.Refused -= OnRefused;
        }
    }

    // ---- the state the checks read ------------------------------------------------------------------

    public Phase State { get; private set; }
    public bool Idle => State == Phase.Idle && GameClock.Now - _sentAt > 0.5;
    public AlphornChart Chart => _chart;
    public HornPart Part => _part;
    public IReadOnlyList<HornPlay> Plays => _plays;
    public int[] Lanes => _lanes;
    public int Lane => _lane;
    public HornResult? Result => _result;
    public string Message => _message;
    /// <summary>Echoes that sounded (checks): the last one's delay and gain.</summary>
    public int EchoesHeard { get; private set; }
    public Echo? LastEcho { get; private set; }
    /// <summary>The ground the echo is found on; the world's by default, a check may put its own valley.</summary>
    public Func<double, double, double?>? Ground { get; set; }
    /// <summary>Real input is ignored while a check drives the song with <see cref="Blow"/> and <see cref="SetLane"/>.</summary>
    public bool Scripted { get => _scripted; set => _scripted = value; }
    public double SongTime => GameClock.Now - _t0;
    public double Breath => _breath;

    // ---- asking, being seated -------------------------------------------------------------------------

    /// <summary>E at the row: asks the server for a seat in the song of this time slot.</summary>
    public void Begin()
    {
        if (!Idle || Service() is not { } svc) return;
        if (_local() is not { } p || HornGround.At(p) is not { } run) return;
        _festival = run.Id;
        State = Phase.Asking;
        _askedAt = GameClock.Now;
        _result = null;
        svc.AskHornStart();
    }

    private FestivalService? Service()
    {
        if (_svc != null || FestivalService.Instance is not { } svc) return _svc;
        _svc = svc;
        svc.HornSeated += OnSeat;
        svc.HornGraded += OnGraded;
        svc.Refused += OnRefused;
        return _svc;
    }

    private void OnRefused(string why)
    {
        if (State != Phase.Asking) return;
        State = Phase.Idle;
        Say(why);
    }

    private void OnSeat(HornSeat seat)
    {
        if (State != Phase.Asking) return;
        _chart = AlphornChart.Make(seat.Seed);
        _part = seat.Part;
        _notes = _chart.Of(_part);
        _lanes = _part == HornPart.Yodel ? [] : _chart.Lanes(_part);
        _lane = _lanes.Length == 0 ? 0 : Array.IndexOf(_lanes, _notes[0].Partial);
        _plays.Clear();
        _blowing = _wasBlow = false;
        _breath = AlphornRules.BreathBeats;
        _lastOff = 0;
        _lastTick = -1;
        _lastFrame = 0;
        _header = $"{(_part switch { HornPart.Melody => "Melody", HornPart.Third => "Second voice", HornPart.Drone => "Drone", _ => "Yodel" })}  ·  {_chart.Bpm:F0} BPM";
        _laneLabels = _lanes.Select(l => $"x{l}").ToArray();
        _t0 = GameClock.Now;
        State = Phase.Playing;
        _hintKey = (_part, (InputDevice)(-1));
        Say(seat.Voices > 1 ? $"{seat.Voices} voices in this song" : "");
        GD.Print($"[horn] seat: {_part}, song {seat.Seed}, {_chart.Bpm:F0} BPM, {_notes.Length} notes, {seat.Voices} voice(s)");
    }

    private void OnGraded(HornResult r)
    {
        _result = r;
        _resultAt = GameClock.Now;
        _resultLines =
        [
            $"{r.Points} of {r.Max} points",
            r.Voices > 1 ? $"Harmony x{r.Harmony100 / 100.0:F2} with {r.Voices} voices" : "Alone: no harmony. Sing with others!",
            r.Batzen > 0 ? $"+{r.Batzen} Batzen   +{r.BoardPoints} party points" : "No Batzen this time",
        ];
        State = Phase.Idle;
        GD.Print($"[horn] result: {r.Points}/{r.Max}, harmony x{r.Harmony100 / 100.0:F2} with {r.Voices}, +{r.Batzen} Batzen, +{r.BoardPoints} points");
    }

    // ---- playing --------------------------------------------------------------------------------------

    /// <summary>Picks a lane (the partial) by <paramref name="delta"/> steps.</summary>
    public void SetLane(int delta)
    {
        if (_lanes.Length == 0 || _blowing) return;
        _lane = Math.Clamp(_lane + delta, 0, _lanes.Length - 1);
    }

    /// <summary>Presses or lets go of the blow (the yodel's tap on a press); the real input and a check both come here.</summary>
    public void Blow(bool down)
    {
        if (State != Phase.Playing) return;
        double t = SongTime;
        double spb = _chart.SecondsPerBeat;
        if (_part == HornPart.Yodel)
        {
            if (!down) return;
            int partial = NearestNote(t).Partial;
            _plays.Add(new HornPlay(partial, t, t + spb * 0.5));
            Grade(t, partial);
            Sound(HornSounds.Yodel(partial), partial, t, 0.7);
            return;
        }
        if (down && !_blowing)
        {
            if (_lanes.Length == 0 || _breath < 0.25) return;
            _blowing = true;
            _blowOn = t;
            _blowPartial = _lanes[_lane];
            Grade(t, _blowPartial);
            _tone.Stream = HornSounds.Hold(_blowPartial);
            _tone.VolumeDb = -4f;
            _tone.Play();
            Echo(_blowPartial, t, hold: true);
        }
        else if (!down && _blowing) Release(t);
    }

    private void Release(double t)
    {
        _blowing = false;
        _tone.Stop();
        _plays.Add(new HornPlay(_blowPartial, _blowOn, t));
        _lastOff = t;
        StopEchoes();
    }

    private HornNote NearestNote(double t)
    {
        double beat = t / _chart.SecondsPerBeat;
        HornNote best = _notes[0];
        foreach (var n in _notes)
            if (Math.Abs(n.Start - beat) < Math.Abs(best.Start - beat)) best = n;
        return best;
    }

    /// <summary>What a press at <paramref name="t"/> earns on the nearest note, for the word on the screen (the server grades the whole song again).</summary>
    private void Grade(double t, int partial)
    {
        var n = NearestNote(t);
        double spb = _chart.SecondsPerBeat, off = t - n.Start * spb;
        var g = AlphornRules.GradeOff(off, spb);
        bool wrong = g != HornGrade.Miss && _part != HornPart.Yodel && partial != n.Partial;
        _gradeAt = GameClock.Now;
        (_grade, _gradeColour) = wrong ? ("WRONG NOTE", UiTheme.Bad) : g switch
        {
            HornGrade.Perfect => ("PERFECT", new Color(0.98f, 0.84f, 0.18f)),
            HornGrade.Good => ("GOOD", UiTheme.Good),
            _ => ("MISS", UiTheme.Bad),
        };
    }

    /// <summary>The yodel's sound, and (with the echo) the valley's answer to it.</summary>
    private void Sound(AudioStreamWav stream, int partial, double t, double length)
    {
        _tone.Stream = stream;
        _tone.VolumeDb = -4f;
        _tone.Play();
        Echo(partial, t, hold: false, length);
    }

    // ---- the valley's answer ----------------------------------------------------------------------------

    private (int Slot, double Delay) _echoing = (-1, 0);

    private void Echo(int partial, double t, bool hold, double length = 0)
    {
        if (_local() is not { } p || _origin() is not { } origin) return;
        var g = p.Global;
        var cam = GetViewport().GetCamera3D();
        double bearing = cam == null ? 0 : Math.Atan2(-cam.GlobalTransform.Basis.Z.X, cam.GlobalTransform.Basis.Z.Z);
        var ground = Ground ?? ((e, n) => _chunks() is { } chunks && chunks.TryGetHeight(origin.ToWorld(e, n, 0), out float h) ? h : null);
        if (EchoFinder.Find(ground, g.E, g.N, bearing, g.E, g.N) is not { } echo) { LastEcho = null; return; }
        LastEcho = echo;
        int slot = _nextEcho++ % _echoVoices.Length;
        if (hold) _echoing = (slot, echo.DelaySeconds);
        var voice = _echoVoices[slot];
        double wallAlt = ground(echo.WallE, echo.WallN) ?? g.Alt;
        var at = origin.ToWorld(echo.WallE, echo.WallN, wallAlt + 30);
        var stream = hold ? HornSounds.Hold(partial) : HornSounds.Yodel(partial);
        GetTree().CreateTimer(echo.DelaySeconds).Timeout += () =>
        {
            if (!IsInsideTree()) return;
            voice.Stream = stream;
            voice.GlobalPosition = at;
            voice.VolumeDb = Mathf.LinearToDb((float)echo.Gain) - 4f;
            voice.AttenuationFilterCutoffHz = (float)echo.LowPassHz;
            voice.Play();
            EchoesHeard++;
        };
    }

    private void StopEchoes()
    {
        if (_echoing.Slot < 0) return;
        var voice = _echoVoices[_echoing.Slot];
        GetTree().CreateTimer(_echoing.Delay).Timeout += () => { if (IsInsideTree()) voice.Stop(); };
        _echoing = (-1, 0);
    }

    // ---- every frame ---------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        Service();
        double now = GameClock.Now;
        switch (State)
        {
            case Phase.Asking when now - _askedAt > 6:
                State = Phase.Idle;
                Say("Nobody answered. Try again.");
                break;
            case Phase.Sent when now - _sentAt > 40:
                State = Phase.Idle;
                break;
            case Phase.Playing:
                Play(now);
                break;
        }
        bool show = State != Phase.Idle || now - _messageAt < 4 || now - _resultAt < 9;
        if (_layer.Visible != show) _layer.Visible = show;
        if (show) _view.QueueRedraw();
    }

    private void Play(double now)
    {
        var p = _local();
        if (p == null || !p.IsInsideTree()) return;
        p.JumpTaken = true;   // jump is the yodel's tap while the song is on, never a jump
        double t = now - _t0, spb = _chart.SecondsPerBeat;
        // a count-in tick on each of the four beats before the first note
        int beat = (int)Math.Floor(t / spb);
        if (beat != _lastTick && beat >= 0 && beat < AlphornChart.LeadBeats)
        {
            _lastTick = beat;
            var (stream, pitch, db) = SfxSynth.TickBank.Pick(Random.Shared);
            _tick.Stream = stream;
            _tick.PitchScale = pitch * (beat == 0 ? 1.4f : 1f);
            _tick.VolumeDb = db - 6f;
            _tick.Play();
        }
        var g = p.Global;
        var site = _svc?.RunningOf(_festival)?.Site;
        if (site == null || !HornGround.Near(site, g.E, g.N, 25) || !_scripted && Input.IsActionJustPressed("ui_cancel"))
        {
            Abort(site == null ? "The festival is over." : "You left the row.");
            return;
        }
        if (!_scripted) ReadInput();
        double beats = _lastFrame == 0 ? 0 : (now - _lastFrame) / spb;
        _lastFrame = now;
        if (_blowing)
        {
            _breath = Math.Max(0, _breath - beats);
            if (_breath <= 0) { Release(t); Say("Out of breath"); }
        }
        else _breath = Math.Min(AlphornRules.BreathBeats, _breath + beats * AlphornRules.RefillPerBeat);
        if (t > _chart.Seconds + 0.5) Finish();
    }

    private void ReadInput()
    {
        bool blow = PlayerInput.Held(PlayerInput.UseItem) || PlayerInput.Strength(PlayerInput.TriggerRight) > 0.5f;
        if (blow != _wasBlow) Blow(blow);
        _wasBlow = blow;
        bool jump = PlayerInput.Held(PlayerInput.Jump);
        if (jump && !_wasJump && _part == HornPart.Yodel) Blow(true);
        _wasJump = jump;
        bool up = PlayerInput.Held(PlayerInput.MoveForward) || PlayerInput.Held(PlayerInput.LookUp);
        bool down = PlayerInput.Held(PlayerInput.MoveBack) || PlayerInput.Held(PlayerInput.LookDown);
        if (up && !_wasUp) SetLane(1);
        if (down && !_wasDown) SetLane(-1);
        _wasUp = up;
        _wasDown = down;
    }

    public override void _Input(InputEvent e)
    {
        if (State != Phase.Playing || _scripted || e is not InputEventMouseButton { Pressed: true } m) return;
        if (m.ButtonIndex == MouseButton.WheelUp) SetLane(1);
        else if (m.ButtonIndex == MouseButton.WheelDown) SetLane(-1);
        else return;
        GetViewport().SetInputAsHandled();
    }

    private void Finish()
    {
        if (_blowing) Release(SongTime);
        _tone.Stop();
        State = Phase.Sent;
        _sentAt = GameClock.Now;
        var score = AlphornRules.Grade(_chart, _part, _plays);
        Say($"Song over: {score.Points} of {score.Max}. Waiting for the others...");
        GD.Print($"[horn] song over: {score.Points}/{score.Max} ({_plays.Count} blows)");
        _svc?.AskHornSong([.. _plays]);
    }

    private void Abort(string why)
    {
        if (_blowing) Release(SongTime);
        _tone.Stop();
        StopEchoes();
        State = Phase.Idle;
        _sentAt = GameClock.Now - 1;
        Say(why);
    }

    private void Say(string text)
    {
        _message = text;
        _messageAt = GameClock.Now;
    }

    // ---- the screen ----------------------------------------------------------------------------------------

    private sealed partial class View : Control
    {
        private readonly HornGame _g;
        private static readonly Color Panel = new(0.055f, 0.065f, 0.085f, 0.78f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);
        private static readonly string[] Counts = ["1", "2", "3", "4"];

        public View(HornGame g) => _g = g;

        public override void _Draw()
        {
            var g = _g;
            float w = Mathf.Min(760f, Size.X * 0.7f), h = 210f;
            var box = new Rect2((Size.X - w) / 2f, Size.Y - h - 118f, w, h);   // clear of the hotbar, the centre stays free
            double now = GameClock.Now;
            if (g.State == Phase.Idle)
            {
                if (g._result is { } r && now - g._resultAt < 9) Result(box, r);
                else if (now - g._messageAt < 4) Line(g._message, box.Position + new Vector2(w / 2f, h - 30f), UiTheme.FontBody, UiTheme.Text, w);
                return;
            }
            DrawRect(box, Panel);
            DrawRect(box, UiTheme.Hairline, false, 1f);
            if (g.State == Phase.Asking) { Line("Taking a seat at the row...", box.GetCenter(), UiTheme.FontBody, UiTheme.Text, w); return; }
            if (g.State == Phase.Sent) { Line(g._message, box.GetCenter(), UiTheme.FontBody, UiTheme.Text, w); return; }

            double t = g.SongTime, spb = g._chart.SecondsPerBeat, beatNow = t / spb;
            Line(g._header, box.Position + new Vector2(110f, 18f), UiTheme.FontSmall, UiTheme.TextDim, 200f);
            float top = box.Position.Y + 34f, laneH = 118f / Mathf.Max(1, g._lanes.Length == 0 ? 3 : g._lanes.Length);
            float nowX = box.Position.X + 80f + (w - 100f) * 0.18f, pxBeat = (w - 100f) * 0.82f / 8f;
            // the lanes (the yodel has one: the tap)
            int rows = Mathf.Max(1, g._lanes.Length);
            if (g._part == HornPart.Yodel) laneH = 54f;
            for (int i = 0; i < rows; i++)
            {
                int lane = g._part == HornPart.Yodel ? 0 : rows - 1 - i;   // the highest partial on top
                var row = new Rect2(box.Position.X + 78f, top + i * laneH, w - 94f, laneH - 2f);
                bool sel = g._part != HornPart.Yodel && lane == g._lane;
                DrawRect(row, sel ? new Color(UiTheme.Amber, 0.14f) : new Color(1, 1, 1, 0.04f));
                if (sel) DrawRect(row, UiTheme.Amber, false, 1.5f);
                if (g._part != HornPart.Yodel) Line(g._laneLabels[lane], new Vector2(box.Position.X + 52f, row.Position.Y + laneH * 0.5f + 4f), UiTheme.FontSmall, sel ? UiTheme.Amber : UiTheme.TextDim, 60f);
                foreach (var n in g._notes)
                {
                    if (g._part != HornPart.Yodel && n.Partial != g._lanes[lane]) continue;
                    float x0 = nowX + (float)(n.Start - beatNow) * pxBeat, x1 = x0 + (float)n.Beats * pxBeat;
                    if (x1 < row.Position.X || x0 > row.End.X) continue;
                    x0 = Mathf.Max(x0, row.Position.X);
                    x1 = Mathf.Min(x1, row.End.X);
                    DrawRect(new Rect2(x0, row.Position.Y + 3f, Mathf.Max(6f, x1 - x0), laneH - 8f), new Color(UiTheme.Good, 0.75f));
                }
            }
            // the blow being held
            if (g._blowing && g._lanes.Length > 0)
            {
                int row = rows - 1 - Array.IndexOf(g._lanes, g._blowPartial);
                float x0 = nowX + (float)(g._blowOn - t) / (float)spb * pxBeat;
                DrawRect(new Rect2(Mathf.Max(x0, box.Position.X + 78f), top + row * laneH + 6f, nowX - Mathf.Max(x0, box.Position.X + 78f), laneH - 14f), new Color(UiTheme.Amber, 0.9f));
            }
            DrawLine(new Vector2(nowX, top - 4f), new Vector2(nowX, top + rows * laneH + 2f), UiTheme.Text, 2f);
            // the breath
            var bar = new Rect2(box.End.X - 150f, box.Position.Y + 10f, 130f, 8f);
            DrawRect(bar, new Color(1, 1, 1, 0.12f));
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * (float)(g._breath / AlphornRules.BreathBeats), bar.Size.Y)), g._breath < 1.5 ? UiTheme.Bad : UiTheme.Good);
            Line("breath", bar.Position + new Vector2(-30f, 9f), UiTheme.FontTiny, UiTheme.TextDim, 60f);
            // the count-in and the word on the last press
            if (beatNow < AlphornChart.LeadBeats)
            {
                int b = Mathf.Clamp(AlphornChart.LeadBeats - (int)Mathf.Floor((float)beatNow), 1, AlphornChart.LeadBeats);
                Line(Counts[b - 1], new Vector2(nowX + 70f, top + 40f), UiTheme.FontTitle, new Color(UiTheme.Amber, 0.9f), 80f, bold: true);
            }
            float age = (float)(now - g._gradeAt);
            if (g._grade.Length > 0 && age < 0.6f)
                Line(g._grade, new Vector2(nowX, top - 14f - age * 20f), UiTheme.FontHeading, new Color(g._gradeColour, 1f - age * 1.6f), 220f, bold: true);
            Line(Hint(), new Vector2(box.GetCenter().X, box.End.Y - 14f), UiTheme.FontSmall, UiTheme.TextDim, w - 20f);
        }

        private string Hint()
        {
            var key = (_g._part, PlayerInput.HintDevice);
            if (key != _g._hintKey || _g._hint.Length == 0)
            {
                _g._hintKey = key;
                _g._hint = _g._part == HornPart.Yodel
                    ? InputHints.Format("{jump} on each beat to yodel  ·  {ui_cancel} leaves")
                    : InputHints.Format("Hold {use_item} from a note's start to its end  ·  " + (InputHints.Pad ? "right stick up / down" : "wheel or {move_forward} / {move_back}") + " picks the note  ·  {ui_cancel} leaves");
            }
            return _g._hint;
        }

        private void Result(Rect2 box, HornResult r)
        {
            DrawRect(box, Panel);
            DrawRect(box, UiTheme.Hairline, false, 1f);
            var c = box.GetCenter();
            Line("Song over", c + new Vector2(0, -52f), UiTheme.FontHeading, UiTheme.Text, 400f, bold: true);
            Line(_g._resultLines[0], c + new Vector2(0, -16f), UiTheme.FontBody, UiTheme.Text, 400f);
            Line(_g._resultLines[1], c + new Vector2(0, 10f), UiTheme.FontBody, r.Voices > 1 ? UiTheme.Good : UiTheme.TextDim, 500f);
            Line(_g._resultLines[2], c + new Vector2(0, 40f), UiTheme.FontBody, UiTheme.Amber, 500f);
        }

        private void Line(string s, Vector2 at, int size, Color colour, float width, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(width / 2f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, width, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, width, size, colour);
        }
    }
}
