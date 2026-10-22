using System;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Concert;

/// <summary>
/// The concert pogo (#1150): dancing at a concert, a ring closes on each beat of the song as this
/// player hears it (the PA's distance delay and the sound card's latency taken off), and jump on
/// the beat pogos. A hit puts the dancer into the Pogo emote (<see cref="FootPlayer.DanceId"/>,
/// replicated, so every peer sees the jump on the shared beat); two beats let go by drop back to
/// the normal dance. While it runs the jump press is the QTE's, not a jump
/// (<see cref="FootPlayer.JumpTaken"/>). The score is this screen's only: nothing on the wire.
/// </summary>
public partial class PogoQte : CanvasLayer
{
    private readonly Func<FootPlayer?> _local;
    private readonly View _view;
    private readonly PogoBeat _beat = new();
    private static int _pogoEmote = -1;
    private bool _ours, _held;

    public PogoQte(Func<FootPlayer?> local)
    {
        _local = local;
        Layer = 7;
        Name = "PogoQte";
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore, Beat = _beat };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    /// <summary>The Pogo emote's <see cref="FootPlayer.DanceId"/>.</summary>
    public static int PogoDanceId
    {
        get
        {
            if (_pogoEmote < 0)
                for (int i = 0; i < Avatar.HumanMeshBuilder.EmoteCount; i++)
                    if (Avatar.HumanMeshBuilder.EmoteName(i) == "Pogo") { _pogoEmote = i; break; }
            return FootPlayer.EmoteDanceBase + _pogoEmote;
        }
    }

    public override void _Process(double delta)
    {
        var p = _local();
        bool dancing = p != null && p.CanEmote && (p.DanceId == 1 || _ours && p.DanceId == PogoDanceId);
        if (p != null && _ours && p.DanceId != PogoDanceId) _ours = false;   // E, the wheel, a fall: theirs now
        if (!dancing || ConcertRig.MusicNear(p!.GlobalPosition) is not { } m || !Heard(m, out double beats, out double spb))
        {
            if (p != null && _ours && p.DanceId == PogoDanceId) p.DanceId = 0;   // walked out of the show mid-pogo
            if (p != null) p.JumpTaken = false;
            _ours = false;
            _beat.Reset();
            _view.Visible = false;
            return;
        }
        p.JumpTaken = true;
        bool down = PlayerInput.Held(PlayerInput.Jump);
        if (down && !_held && _beat.Press(beats, spb) != PogoGrade.Miss && p.DanceId == 1)
        {
            p.DanceId = PogoDanceId;
            _ours = true;
        }
        _held = down;
        if (_beat.Tick(beats, spb) && _ours)
        {
            p.DanceId = 1;
            _ours = false;
        }
        _view.Visible = true;
        _view.Step(beats);
        _view.QueueRedraw();
    }

    /// <summary>
    /// The beat count where this player's ears are in the song, and the beat's length: the song's
    /// clock less the loudest hang's delay and the output latency.
    /// </summary>
    private static bool Heard(Items.RadioManager.Music m, out double beats, out double secondsPerBeat)
    {
        beats = secondsPerBeat = 0;
        double late = AudioServer.GetOutputLatency();
        if (m.Source.GetParent() is ConcertMusic music)
        {
            float db = float.NegativeInfinity;
            foreach (var s in music.Speakers)
                if (s.Heard.Db > db) { db = s.Heard.Db; late = s.Heard.Delay + AudioServer.GetOutputLatency(); }
        }
        double t = Net.ClockSync.ServerNow - late;
        if (!m.BeatAt(t, out float phase, out int beat, out _, out _) || !m.BeatAt(t - 0.25, out float phase0, out int beat0, out _, out _))
            return false;
        beats = beat + phase;
        double step = beats - (beat0 + phase0);
        if (step <= 0.01) return false;   // a song's first moment, or a seek
        secondsPerBeat = 0.25 / step;
        return true;
    }

    private sealed partial class View : Control
    {
        public PogoBeat Beat = null!;
        private double _beats;
        private int _shownBeat = int.MinValue, _shownCombo = -1;
        private double _gradeAt = double.NegativeInfinity, _hintsAt = double.NegativeInfinity;
        private string _grade = "", _combo = "", _hint = "";
        private Color _gradeColour;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Green = new(0.45f, 0.95f, 0.5f);
        private static readonly Color Red = new(0.9f, 0.25f, 0.2f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public void Step(double beats)
        {
            _beats = beats;
            if (GameClock.Now - _hintsAt > 1.0)
            {
                _hintsAt = GameClock.Now;
                _hint = InputHints.Format(Beat.Pogoing ? "{jump} on every beat" : "{jump} on the beat to pogo");
            }
            if (Beat.LastBeat != _shownBeat)
            {
                _shownBeat = Beat.LastBeat;
                _gradeAt = GameClock.Now;
                (_grade, _gradeColour) = Beat.Last switch
                {
                    PogoGrade.Perfect => ("PERFECT", Gold),
                    PogoGrade.Good => ("GOOD", Green),
                    PogoGrade.Miss => ("MISS", Red),
                    _ => ("", UiTheme.Text),
                };
            }
            if (Beat.Combo != _shownCombo)
            {
                _shownCombo = Beat.Combo;
                _combo = Beat.Combo >= 2 ? $"x{Beat.Combo}" + (Beat.Best > Beat.Combo ? $"   best x{Beat.Best}" : "") : "";
            }
        }

        public override void _Draw()
        {
            var c = new Vector2(Size.X * 0.5f, Size.Y * 0.72f);
            float phase = (float)(_beats - Math.Floor(_beats));
            const float target = 22f;
            // the ring closes onto the target on each beat, and the target flashes as it lands
            float flash = Mathf.Clamp(1f - phase / 0.15f, 0f, 1f);
            DrawCircle(c, target, new Color(Beat.Pogoing ? Gold : UiTheme.Text, 0.18f + 0.5f * flash));
            DrawArc(c, target, 0, Mathf.Tau, 40, Beat.Pogoing ? Gold : UiTheme.Text, 3f, true);
            DrawArc(c, target + 70f * (1f - phase), 0, Mathf.Tau, 48, new Color(UiTheme.Text, 0.35f + 0.6f * phase), 2.5f, true);
            float age = (float)(GameClock.Now - _gradeAt);
            if (_grade.Length > 0 && age < 0.5f)
                Text(_grade, c + new Vector2(0, -target - 18f - age * 30f), UiTheme.FontHeading, new Color(_gradeColour, 1f - age * 2f), bold: true);
            Text(_combo, c + new Vector2(0, 6f), UiTheme.FontBody, Gold, bold: true);
            Text(_hint, c + new Vector2(0, target + 108f), UiTheme.FontSmall, UiTheme.TextDim);
        }

        private void Text(string s, Vector2 at, int size, Color colour, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            var left = at - new Vector2(200f, 0);
            DrawString(font, left + new Vector2(2f, 2f), s, HorizontalAlignment.Center, 400f, size, new Color(Shadow, Shadow.A * colour.A));
            DrawString(font, left, s, HorizontalAlignment.Center, 400f, size, colour);
        }
    }
}
