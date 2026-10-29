using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Occasions.Festivals.Fasnacht;

/// <summary>
/// The Fasnacht, the player's side (#1357, <c>docs/notes/occasions/festival-fasnacht.md</c>). Three things on the interact key (E / the pad's Y / a VR grip through
/// <c>XrPad</c>), so no new binding exists: at the Larven stall E takes the day's mask and Räppli, at Basel's Vorlaarve lantern E joins the Morgestraich (04:00-05:30 of
/// day 1), at the Guggenmusik stage E asks for a seat in the next song. Räppli are thrown with the item's own Use (<c>ItemUse.Confetti</c>). In a song the notes of
/// the player's part scroll towards a line after a four-beat count-in (120-140 BPM) and the player taps <c>use_item</c> (LMB, the pad's trigger, the VR trigger) or jump on each
/// one: the drum's snare and bass, the trumpet's stabs, the sousaphone's oom sound as tapped. When it is over the taps go to the server, which grades them with the
/// same <see cref="GuggenRules"/> this screen used and scores how many voices were together. Nothing is built per frame: strings are cached on the keys that change, the panel
/// redraws only while it is shown.
/// </summary>
public partial class FasnachtGame : CanvasLayer
{
    public const string NodeName = "FasnachtGame";

    public enum Phase { Idle, Asking, Playing, Sent }

    /// <summary>What is at hand.</summary>
    public enum Near { None, Stall, Lantern, Stage }

    public static FasnachtGame? Instance { get; private set; }

    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private readonly AudioStreamPlayer[] _voices = new AudioStreamPlayer[4];
    private readonly AudioStreamPlayer _tick = new() { Name = "Tick", Bus = SfxBus.Name, VolumeDb = -14f };
    private readonly AudioStreamPlayer _fx = new() { Name = "Fx", Bus = SfxBus.Name, VolumeDb = -4f };
    private readonly Random _rng = new(1357);
    private int _nextVoice;
    private FestivalService? _svc;
    private bool _hooked;

    // what is at hand, what is said
    private Near _near;
    private string _message = "";
    private double _messageAt = double.NegativeInfinity;
    private string _prompt = "";
    private (InputDevice, Near) _promptKey = ((InputDevice)(-1), Near.None);
    private bool _wasMorgestraich;

    // the song
    private GuggenChart _chart = GuggenChart.Make(0);
    private GuggenPart _part;
    private GuggenNote[] _notes = [];
    private bool[] _taken = [];
    private readonly List<double> _taps = new();
    private string _festival = "";
    private double _t0, _askedAt, _sentAt = double.NegativeInfinity, _resultAt = double.NegativeInfinity, _gradeAt = double.NegativeInfinity;
    private int _lastTick = -1;
    private bool _wasTap, _scripted;
    private string _header = "", _grade = "", _hint = "";
    private string[] _resultLines = [];
    private Color _gradeColour = UiTheme.Text;
    private GuggenResult? _result;
    private (GuggenPart, InputDevice) _hintKey = (GuggenPart.Drum, (InputDevice)(-1));

    public FasnachtGame(Func<FootPlayer?> local)
    {
        _local = local;
        Name = NodeName;
        Layer = 7;
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore, Game = this };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
        AddChild(_tick);
        AddChild(_fx);
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer { Name = $"Voice{i}", Bus = SfxBus.Name, VolumeDb = -2f };
            AddChild(_voices[i]);
        }
    }

    public FasnachtGame() : this(() => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (_svc != null)
        {
            _svc.FasnachtTold -= OnTold;
            _svc.GuggenSeated -= OnSeat;
            _svc.GuggenGraded -= OnGraded;
            _svc.Refused -= OnRefused;
        }
        if (Instance == this) Instance = null;
    }

    private void Hook()
    {
        if (_hooked || FestivalService.Instance is not { } svc) return;
        _hooked = true;
        _svc = svc;
        svc.FasnachtTold += OnTold;
        svc.GuggenSeated += OnSeat;
        svc.GuggenGraded += OnGraded;
        svc.Refused += OnRefused;
    }

    // ---- what the checks read ---------------------------------------------------------------------------------

    public Phase State { get; private set; }
    internal Near NearTarget => _near;
    internal string Message => _message;
    public GuggenChart Chart => _chart;
    public GuggenPart Part => _part;
    public IReadOnlyList<double> Taps => _taps;
    public GuggenResult? Result => _result;
    public double SongTime => GameClock.Now - _t0;
    public bool Idle => State == Phase.Idle && GameClock.Now - _sentAt > 0.5;

    /// <summary>The Morgestraich's banner has been shown on this machine (checks).</summary>
    public int Morgestraichs { get; private set; }

    /// <summary>Real input is ignored while a check drives the song with <see cref="Tap"/>.</summary>
    public bool Scripted { get => _scripted; set => _scripted = value; }

    // ---- what the service says -----------------------------------------------------------------------------------

    private void OnTold(FestivalService.FasnachtEvent what, int a, string text)
    {
        Say(text);
        var bank = what == FestivalService.FasnachtEvent.Raeppli ? FasnachtSounds.RaeppliToss : null;
        if (bank == null) return;
        var (stream, pitch, jitter) = bank.Pick(_rng);
        _fx.Stream = stream;
        _fx.PitchScale = pitch;
        _fx.VolumeDb = -8f + jitter;
        _fx.Play();
    }

    private void OnRefused(string why)
    {
        if (_svc?.FasnachtNow() == null) return;
        if (State == Phase.Asking) { State = Phase.Idle; Say(why); return; }
        if (_near != Near.None) Say(why);
    }

    private void OnSeat(GuggenSeat seat)
    {
        if (State != Phase.Asking) return;
        _chart = GuggenChart.Make(seat.Seed);
        _part = seat.Part;
        _notes = _chart.Of(_part);
        _taken = new bool[_notes.Length];
        _taps.Clear();
        _lastTick = -1;
        _header = $"{_part switch { GuggenPart.Drum => "Drum", GuggenPart.Trumpet => "Trumpet", _ => "Sousaphone" }}  -  {_chart.Bpm:F0} BPM  -  {seat.Voices} in the band";
        _t0 = GameClock.Now;
        _hintKey = (_part, (InputDevice)(-1));
        State = Phase.Playing;
        GD.Print($"[fasnacht] seat: {_part}, song {seat.Seed}, {_chart.Bpm:F0} BPM, {_notes.Length} notes, {seat.Voices} voice(s)");
    }

    private void OnGraded(GuggenResult r)
    {
        _result = r;
        _resultAt = GameClock.Now;
        string din = (r.Din100 / 100.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _resultLines =
        [
            $"{r.Points} of {r.Max} points",
            r.Voices > 1 ? $"In sync {r.Sync100} %: din x{din} with {r.Voices} voices" : "Alone: no din. Play with others!",
            r.Batzen > 0 ? $"+{r.Batzen} Batzen   +{r.BoardPoints} party points" : "No Batzen this time",
        ];
        State = Phase.Idle;
        _sentAt = GameClock.Now;
        GD.Print($"[fasnacht] result: {r.Points}/{r.Max}, sync {r.Sync100} %, din x{din} with {r.Voices}, +{r.Batzen} Batzen, +{r.BoardPoints} points");
    }

    private void Say(string text)
    {
        _message = text;
        _messageAt = GameClock.Now;
        _view.QueueRedraw();
    }

    // ---- asking, playing ------------------------------------------------------------------------------------------

    /// <summary>E at the stage: asks the server for a seat in the song being opened.</summary>
    public void Begin()
    {
        if (!Idle || _svc == null || _svc.FasnachtNow() is not { } m) return;
        _festival = m.Run.Id;
        State = Phase.Asking;
        _askedAt = GameClock.Now;
        _result = null;
        _svc.AskGuggenStart();
    }

    /// <summary>A tap (the real input and a check both come here): sounds the part's note and grades it for the word on the screen.</summary>
    public void Tap(double? at = null)
    {
        if (State != Phase.Playing) return;
        double t = at ?? SongTime, spb = _chart.SecondsPerBeat;
        _taps.Add(t);
        int best = -1;
        double bestOff = double.MaxValue;
        for (int i = 0; i < _notes.Length; i++)
        {
            double off = Math.Abs(t - _chart.TimeOf(_notes[i]));
            if (!_taken[i] && off < bestOff) { best = i; bestOff = off; }
        }
        var g = best < 0 ? GuggenGrade.Miss : GuggenRules.GradeOff(bestOff, spb);
        if (g != GuggenGrade.Miss) _taken[best] = true;
        _gradeAt = GameClock.Now;
        (_grade, _gradeColour) = g switch
        {
            GuggenGrade.Perfect => ("PERFECT", new Color(0.98f, 0.84f, 0.18f)),
            GuggenGrade.Good => ("GOOD", UiTheme.Good),
            _ => ("OFF", UiTheme.Bad),
        };
        // the note that is due sounds (a miss still sounds: a Gugge plays on, and is not in tune)
        int nearest = 0;
        double nearestOff = double.MaxValue;
        for (int i = 0; i < _notes.Length; i++)
        {
            double off = Math.Abs(t - _chart.TimeOf(_notes[i]));
            if (off < nearestOff) { nearest = i; nearestOff = off; }
        }
        var note = _notes[nearest];
        var (stream, pitch, jitter) = _part switch
        {
            GuggenPart.Drum => (note.Pitch == 1 ? FasnachtSounds.BassDrum : FasnachtSounds.Snare).Pick(_rng),
            GuggenPart.Trumpet => Pitched(FasnachtSounds.Trumpet, note.Pitch),
            _ => Pitched(FasnachtSounds.Sousaphone, note.Pitch + 12),
        };
        var voice = _voices[_nextVoice++ % _voices.Length];
        voice.Stream = stream;
        voice.PitchScale = pitch;
        voice.VolumeDb = -2f + jitter;
        voice.Play();
    }

    private (AudioStreamWav, float, float) Pitched(SfxBank bank, int semitones)
    {
        var (stream, pitch, jitter) = bank.Pick(_rng);
        return (stream, pitch * FasnachtSounds.Pitch(semitones), jitter);
    }

    public override void _Process(double delta)
    {
        Hook();
        double now = GameClock.Now;
        UpdateNear();
        MorgestraichBanner();
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
        bool show = State != Phase.Idle || _near != Near.None || now - _messageAt < 4 || now - _resultAt < 9;
        if (_view.Visible != show) _view.Visible = show;
        if (show && State != Phase.Idle) _view.QueueRedraw();
    }

    private void UpdateNear()
    {
        var near = Near.None;
        if (_svc?.FasnachtNow() is { } m && _local() is { } p)
        {
            var g = p.Global;
            near = FasnachtRules.NearStage(m.Site, g.E, g.N) ? Near.Stage
                : FasnachtRules.NearStall(m.Site, g.E, g.N) ? Near.Stall
                : m.Run.Id == FasnachtRules.BaselId && FasnachtRules.NearLantern(m.Site, g.E, g.N) ? Near.Lantern : Near.None;
        }
        if (near != _near) _view.QueueRedraw();
        _near = near;
    }

    /// <summary>At 04:00 on Basel's day 1, for whoever is at the square: the drum roll and the saying, once.</summary>
    private void MorgestraichBanner()
    {
        bool on = _svc?.FasnachtNow() is { } m && m.Run.Id == FasnachtRules.BaselId
            && FasnachtRules.Morgestraich(m.Run.Id, m.Run.Day, World.DayNight.Instance?.Hour ?? 12);
        if (on == _wasMorgestraich) return;
        _wasMorgestraich = on;
        if (!on) return;
        Morgestraichs++;
        Say(FasnachtRules.Saying + "  The street lights are out: the lanterns are lit.");
        var (stream, pitch, jitter) = FasnachtSounds.MorgestraichRoll.Pick(_rng);
        _fx.Stream = stream;
        _fx.PitchScale = pitch;
        _fx.VolumeDb = -2f + jitter;
        _fx.Play();
    }

    private void Play(double now)
    {
        var p = _local();
        if (p == null || !p.IsInsideTree()) return;
        p.JumpTaken = true;   // jump is a tap while the song is on, never a jump
        double t = now - _t0, spb = _chart.SecondsPerBeat;
        // a count-in tick on each of the four beats before the first note
        int beat = (int)Math.Floor(t / spb);
        if (beat != _lastTick && beat >= 0 && beat < GuggenChart.LeadBeats)
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
        if (site == null || !FasnachtRules.NearStage(site, g.E, g.N, 25) || !_scripted && Input.IsActionJustPressed("ui_cancel"))
        {
            Abort(site == null ? "The Fasnacht is over." : "You left the stage.");
            return;
        }
        if (!_scripted)
        {
            bool tap = PlayerInput.Held(PlayerInput.UseItem) || PlayerInput.Held(PlayerInput.Jump) || PlayerInput.Strength(PlayerInput.TriggerRight) > 0.5f;
            if (tap && !_wasTap) Tap();
            _wasTap = tap;
        }
        if (t > _chart.Seconds + 0.5) Finish();
    }

    private void Finish()
    {
        State = Phase.Sent;
        _sentAt = GameClock.Now;
        var score = GuggenRules.Grade(_chart, _part, _taps);
        Say($"Song over: {score.Points} of {score.Max}. Waiting for the others...");
        GD.Print($"[fasnacht] song over: {score.Points}/{score.Max} ({_taps.Count} taps, {score.Extra} off)");
        _svc?.AskGuggenSong([.. _taps]);
    }

    private void Abort(string why)
    {
        GD.Print($"[fasnacht] song abandoned: {why}");
        State = Phase.Idle;
        _sentAt = GameClock.Now - 1;
        Say(why);
    }

    // ---- the interaction -----------------------------------------------------------------------------------------

    /// <summary>The prompt of what E does here, or null away from the stall, the lantern and the stage (or while a song is on).</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc?.FasnachtNow() == null || game._near == Near.None || !game.Idle) return null;
        var key = (PlayerInput.HintDevice, game._near);
        if (key != game._promptKey)
        {
            game._promptKey = key;
            game._prompt = InputHints.Prompt(PlayerInput.InteractMount, game._near switch
            {
                Near.Stall => "Take a Larve and Räppli (once a day)",
                Near.Lantern => "March with the Vorlaarve lantern (the Morgestraich)",
                _ => "Play with the Guggenmusik (drum, trumpet, sousaphone)",
            });
        }
        return new Interaction(game._prompt, default);
    }

    /// <summary>E / Y / a VR grip: takes the mask, joins the Morgestraich or sits down at the stage. False when none is at hand.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } game || game._local() != p || game._svc is not { } svc || svc.FasnachtNow() == null || game._near == Near.None || !game.Idle) return false;
        switch (game._near)
        {
            case Near.Stall: svc.AskFasnacht(FestivalService.FasnachtStall); break;
            case Near.Lantern: svc.AskFasnacht(FestivalService.FasnachtLantern); break;
            default: game.Begin(); break;
        }
        return true;
    }

    // ---- the screen --------------------------------------------------------------------------------------------------

    private sealed partial class View : Control
    {
        public FasnachtGame Game = null!;

        private static readonly Color Panel = new(0.055f, 0.065f, 0.085f, 0.78f);
        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Amber = new(1f, 0.7f, 0.25f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);
        private static readonly string[] Counts = ["1", "2", "3", "4"];

        public override void _Draw()
        {
            var g = Game;
            if (g._svc?.FasnachtNow() == null) return;
            float w = Mathf.Min(760f, Size.X * 0.7f), h = 170f;
            var box = new Rect2((Size.X - w) / 2f, Size.Y - h - 118f, w, h);   // clear of the hotbar, the centre stays free
            double now = GameClock.Now;
            bool message = g._message.Length > 0 && now - g._messageAt < 4;
            if (g.State == Phase.Idle)
            {
                if (g._result is { } r && now - g._resultAt < 9) { Result(box, r); return; }
                if (g._near != Near.None)
                {
                    var c = new Vector2(Size.X * 0.5f, Size.Y * 0.80f);
                    DrawRect(new Rect2(c.X - 380, c.Y - 50, 760, 76), new Color(0, 0, 0, 0.45f), true);
                    (string title, string line) = g._near switch
                    {
                        Near.Stall => ("Larven stall", "A mask from the stall and a bag of Räppli: put the Larve on from your pack, throw Räppli with Use."),
                        Near.Lantern => ("Vorlaarve lantern", "At four in the morning of the first day the lights go out and the lanterns march."),
                        _ => ("Guggenmusik", "Drum, trumpet and sousaphone. Loud and together: the more voices in step, the more it pays."),
                    };
                    Line(title, c + new Vector2(0, -14), UiTheme.FontHeading, UiTheme.Text, 760f, true);
                    Line(line, c + new Vector2(0, 12), UiTheme.FontBody, Gold, 760f);
                    if (message) Line(g._message, c + new Vector2(0, 34), UiTheme.FontSmall, Amber, 760f);
                }
                else if (message) Line(g._message, new Vector2(Size.X * 0.5f, Size.Y * 0.80f), UiTheme.FontBody, Amber, 760f);
                return;
            }
            DrawRect(box, Panel);
            DrawRect(box, UiTheme.Hairline, false, 1f);
            if (g.State == Phase.Asking) { Line("Taking a seat on the stage...", box.GetCenter(), UiTheme.FontBody, UiTheme.Text, w); return; }
            if (g.State == Phase.Sent) { Line(g._message, box.GetCenter(), UiTheme.FontBody, UiTheme.Text, w); return; }

            double t = g.SongTime, spb = g._chart.SecondsPerBeat, beatNow = t / spb;
            Line(g._header, box.Position + new Vector2(w / 2f, 20f), UiTheme.FontSmall, UiTheme.TextDim, w - 20f);
            var row = new Rect2(box.Position.X + 20f, box.Position.Y + 40f, w - 40f, 70f);
            DrawRect(row, new Color(1, 1, 1, 0.05f));
            float nowX = row.Position.X + row.Size.X * 0.2f, pxBeat = row.Size.X * 0.8f / 6f;
            for (int i = 0; i < g._notes.Length; i++)
            {
                float x = nowX + (float)(g._notes[i].Beat - beatNow) * pxBeat;
                if (x < row.Position.X - 12f || x > row.End.X + 12f) continue;
                var colour = g._taken[i] ? new Color(UiTheme.Good, 0.35f) : g._part == GuggenPart.Drum && g._notes[i].Pitch == 1 ? Gold : new Color(UiTheme.Good, 0.9f);
                float size = g._part == GuggenPart.Drum && g._notes[i].Pitch == 1 ? 22f : 16f;
                DrawRect(new Rect2(x - size / 2f, row.GetCenter().Y - size / 2f, size, size), colour);
            }
            DrawLine(new Vector2(nowX, row.Position.Y - 4f), new Vector2(nowX, row.End.Y + 4f), UiTheme.Text, 2f);
            if (beatNow < GuggenChart.LeadBeats)
            {
                int b = Mathf.Clamp(GuggenChart.LeadBeats - (int)Mathf.Floor((float)beatNow), 1, GuggenChart.LeadBeats);
                Line(Counts[b - 1], new Vector2(nowX + 70f, row.Position.Y + 48f), UiTheme.FontTitle, new Color(UiTheme.Amber, 0.9f), 80f, true);
            }
            float age = (float)(now - g._gradeAt);
            if (g._grade.Length > 0 && age < 0.6f)
                Line(g._grade, new Vector2(box.GetCenter().X, box.Position.Y + 36f - age * 16f), UiTheme.FontHeading, new Color(g._gradeColour, 1f - age * 1.6f), 220f, true);
            Line(Hint(), new Vector2(box.GetCenter().X, box.End.Y - 14f), UiTheme.FontSmall, UiTheme.TextDim, w - 20f);
        }

        private string Hint()
        {
            var g = Game;
            var key = (g._part, PlayerInput.HintDevice);
            if (key != g._hintKey || g._hint.Length == 0)
            {
                g._hintKey = key;
                g._hint = InputHints.Format("Tap {use_item} (or {jump}) on every note, loud and in step  -  {ui_cancel} leaves");
            }
            return g._hint;
        }

        private void Result(Rect2 box, GuggenResult r)
        {
            var g = Game;
            DrawRect(box, Panel);
            DrawRect(box, UiTheme.Hairline, false, 1f);
            var c = box.GetCenter();
            Line("Song over", c + new Vector2(0, -52f), UiTheme.FontHeading, UiTheme.Text, 400f, true);
            Line(g._resultLines[0], c + new Vector2(0, -16f), UiTheme.FontBody, UiTheme.Text, 400f);
            Line(g._resultLines[1], c + new Vector2(0, 10f), UiTheme.FontBody, r.Voices > 1 ? UiTheme.Good : UiTheme.TextDim, 600f);
            Line(g._resultLines[2], c + new Vector2(0, 40f), UiTheme.FontBody, UiTheme.Amber, 500f);
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
