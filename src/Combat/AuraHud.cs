using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Ui;

namespace UnitSport.Combat;

/// <summary>
/// The aura battle's screen (#907), drawn over the world while the local player battles: both
/// names with their aura (the local one on the left), the round, the phase's banner and clock;
/// while performing, the beat ring that closes on each cut and the move after it; every press's
/// PERFECT / GOOD / OK / MISS; the verdict counted up part by part; at the end, the winner and each
/// battler's best combo. Every key it names comes from <see cref="InputHints"/>.
/// </summary>
public partial class AuraHud : CanvasLayer
{
    private readonly View _view;

    public AuraHud()
    {
        Layer = 8;
        Name = "AuraHud";
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _Ready()
    {
        Callable.From(() =>
        {
            if (AuraManager.Client is { } auras)
            {
                auras.GradedEvent += _view.OnGraded;
                auras.VerdictEvent += _view.OnVerdict;
            }
        }).CallDeferred();
    }

    public override void _Process(double delta)
    {
        var auras = AuraManager.Client;
        bool show = auras?.Current != null;
        _view.Visible = show;
        if (!show) return;
        _view.Step(auras!.Current!);
        _view.QueueRedraw();
    }

    private sealed partial class View : Control
    {
        private AuraManager.View? _v;
        private int _lastId;
        private string _me = "", _them = "", _meTotal = "", _themTotal = "", _round = "", _clock = "";
        private int _shownMine = -1, _shownTheirs = -1, _shownRound = -1, _shownSecs = -1;
        private (AuraPhase, int, long) _bannerKey = ((AuraPhase)255, -1, -1);
        private string? _big, _small;
        private Color _colour = Gold;
        private string _pressHint = "";
        private double _hintsAt = double.NegativeInfinity;
        // the last graded press, popping over the performer
        private string _popWord = "";
        private Color _popColour;
        private double _popAt = double.NegativeInfinity;
        // the verdict, line by line
        private readonly List<(string Text, Color Colour)> _verdict = new();
        private string _verdictTotal = "";
        private double _verdictAt = double.NegativeInfinity;
        // the move names now / next, rebuilt when the move changes
        private int _shownMove = -2;
        private string _moveNow = "", _moveNext = "";
        private string _bestMine = "", _bestTheirs = "";

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Violet = new(0.72f, 0.45f, 1f);
        private static readonly Color Red = new(0.9f, 0.25f, 0.2f);
        private static readonly Color Green = new(0.45f, 0.95f, 0.5f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public void OnGraded(int turn, int index, AuraGrade grade)
        {
            _popWord = AuraRules.GradeWord(grade);
            _popColour = Of(grade);
            _popAt = GameClock.Now;
        }

        public void OnVerdict(AuraTurn t)
        {
            _verdict.Clear();
            var s = t.Score;
            for (int i = 0; i < AuraScore.PartCount; i++)
            {
                float p = s.Part(i);
                if (Mathf.Abs(p) < 0.05f) continue;
                var line = new StringBuilder();
                line.Append(AuraScore.PartNames[i]).Append("  ").Append(p > 0 ? "+" : "").Append(p.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                _verdict.Add((line.ToString(), p > 0 ? UiTheme.Text : Red));
            }
            _verdictTotal = $"+{t.Score.Total * 100} AURA";
            _verdictAt = GameClock.Now;
        }

        private static Color Of(AuraGrade g) => g switch
        {
            AuraGrade.Perfect => Gold,
            AuraGrade.Good => Green,
            AuraGrade.Ok => UiTheme.Text,
            _ => Red,
        };

        public void Step(AuraManager.View v)
        {
            _v = v;
            if (v.Id != _lastId)
            {
                _lastId = v.Id;
                _me = (v.IsA ? v.NameA : v.NameB).ToUpperInvariant();
                _them = v.OpponentName.ToUpperInvariant();
                _shownMine = _shownTheirs = _shownRound = -1;
                _verdict.Clear();
            }
            if (v.MyTotal != _shownMine) { _shownMine = v.MyTotal; _meTotal = $"{v.MyTotal * 100} aura"; }
            if (v.TheirTotal != _shownTheirs) { _shownTheirs = v.TheirTotal; _themTotal = $"{v.TheirTotal * 100} aura"; }
            if (v.Round != _shownRound) { _shownRound = v.Round; _round = $"ROUND {Math.Min(v.Round, AuraMatch.Rounds)}/{AuraMatch.Rounds}"; }
            int secs = Mathf.CeilToInt(v.Left);
            if (secs != _shownSecs) { _shownSecs = secs; _clock = secs.ToString(); }
            if (GameClock.Now - _hintsAt > 1.0)
            {
                _hintsAt = GameClock.Now;
                _pressHint = InputHints.Format("{aura_next} on the beat to cut to the next move");
                _bannerKey = ((AuraPhase)255, -1, -1);
            }

            // the move on now, and the one after it, while performing
            int moveIdx = v.Phase == AuraPhase.Perform ? AuraRules.MoveAt(v.Plan, v.T0, ClockSync.ServerNow) : -1;
            if (moveIdx != _shownMove)
            {
                _shownMove = moveIdx;
                _moveNow = moveIdx >= 0 ? AuraRules.Name(v.Plan[moveIdx]).ToUpperInvariant() : "";
                _moveNext = moveIdx >= 0 && moveIdx + 1 < v.Plan.Length ? $"next: {AuraRules.Name(v.Plan[moveIdx + 1])}" : moveIdx >= 0 ? "hold the pose!" : "";
            }

            var key = (v.Phase, v.Turn, v.Winner);
            if (key == _bannerKey) return;
            _bannerKey = key;
            string? big = null, small = null;
            Color colour = Gold;
            string performer = v.NameOf(v.Performer);
            switch (v.Phase)
            {
                case AuraPhase.Intro:
                    big = "AURA BATTLE";
                    small = $"{v.NameA} vs {v.NameB}: plan a combo, cut it on the beat";
                    break;
                case AuraPhase.Plan:
                    big = v.MyTurn ? "PLAN YOUR COMBO" : $"{performer.ToUpperInvariant()} IS PLANNING...";
                    small = v.MyTurn ? null : "their combo stays secret until they perform";
                    colour = Violet;
                    break;
                case AuraPhase.Countdown:
                    big = v.MyTurn ? "GET READY" : $"{performer.ToUpperInvariant()} PERFORMS";
                    small = v.MyTurn ? _pressHint : null;
                    break;
                case AuraPhase.Verdict:
                    big = null;
                    break;
                case AuraPhase.Done:
                    big = v.Winner == 0 ? "DRAW" : v.Winner == v.Me ? "YOU WIN" : $"{v.NameOf(v.Winner).ToUpperInvariant()} WINS";
                    small = v.Forfeited ? "by forfeit" : null;
                    colour = v.Winner == v.Me ? Gold : UiTheme.Text;
                    _bestMine = Best(v, v.Me);
                    _bestTheirs = Best(v, v.Opponent);
                    break;
            }
            _big = big;
            _small = small;
            _colour = colour;
        }

        private static string Best(AuraManager.View v, long who) =>
            v.BestOf(who) is { } t ? $"{v.NameOf(who)}'s best combo: {AuraManager.Combo(t.Moves)}  (+{t.Score.Total * 100})" : "";

        public override void _Draw()
        {
            if (_v is not { } v) return;
            var size = Size;
            float top = 34f, w = Mathf.Min(size.X * 0.3f, 420f);

            // ---- the two battlers and their aura ------------------------------------------
            var left = new Rect2(size.X * 0.5f - 60f - w, top - 22f, w, 52f);
            var right = new Rect2(size.X * 0.5f + 60f, top - 22f, w, 52f);
            DrawRect(left, UiTheme.Glass);
            DrawRect(right, UiTheme.Glass);
            bool myTurn = v.MyTurn && v.Phase is not (AuraPhase.Done or AuraPhase.Intro);
            DrawRect(new Rect2(left.Position, new Vector2(4f, left.Size.Y)), myTurn ? Gold : UiTheme.Hairline);
            DrawRect(new Rect2(right.End.X - 4f, right.Position.Y, 4f, right.Size.Y), !v.MyTurn && v.Phase is not (AuraPhase.Done or AuraPhase.Intro) ? Gold : UiTheme.Hairline);
            Text(_me, left.Position + new Vector2(12f, 22f), UiTheme.FontBody, UiTheme.Text, HorizontalAlignment.Left, w - 24f, bold: true);
            Text(_meTotal, left.Position + new Vector2(12f, 44f), UiTheme.FontBody, Violet, HorizontalAlignment.Left, w - 24f);
            Text(_them, right.Position + new Vector2(12f, 22f), UiTheme.FontBody, UiTheme.Text, HorizontalAlignment.Right, w - 24f, bold: true);
            Text(_themTotal, right.Position + new Vector2(12f, 44f), UiTheme.FontBody, Violet, HorizontalAlignment.Right, w - 24f);
            var mid = new Rect2(size.X * 0.5f - 52f, top - 22f, 104f, 52f);
            DrawRect(mid, UiTheme.Glass);
            Text(_round, mid.Position + new Vector2(0, 18f), UiTheme.FontSmall, UiTheme.TextDim, HorizontalAlignment.Center, mid.Size.X);
            if (v.Phase is AuraPhase.Plan or AuraPhase.Countdown)
                Text(_clock, mid.Position + new Vector2(0, 46f), 28, v.Phase == AuraPhase.Plan && _shownSecs <= 3 ? Red : Gold, HorizontalAlignment.Center, mid.Size.X, bold: true);

            // ---- banners ------------------------------------------------------------------
            if (_big != null)
                Text(_big, new Vector2(0, size.Y * 0.26f), 52, _colour, HorizontalAlignment.Center, size.X, bold: true);
            if (_small != null)
                Text(_small, new Vector2(0, size.Y * 0.26f + 36f), UiTheme.FontHeading, UiTheme.Text, HorizontalAlignment.Center, size.X);

            if (v.Phase == AuraPhase.Countdown) DrawCountdown(v, size);
            if (v.Phase == AuraPhase.Perform) DrawPerform(v, size);
            if (v.Phase == AuraPhase.Verdict) DrawVerdict(size);
            if (v.Phase == AuraPhase.Done)
            {
                Text(_bestMine, new Vector2(0, size.Y * 0.26f + 76f), UiTheme.FontBody, Gold, HorizontalAlignment.Center, size.X);
                Text(_bestTheirs, new Vector2(0, size.Y * 0.26f + 102f), UiTheme.FontBody, UiTheme.Text, HorizontalAlignment.Center, size.X);
            }

            // ---- the last press's grade --------------------------------------------------------
            float since = (float)(GameClock.Now - _popAt);
            if (since < 0.7f && _popWord.Length > 0)
            {
                float pop = 1f + 0.4f * Mathf.Max(0f, 1f - since * 6f);
                var c = _popColour;
                c.A = 1f - Mathf.Max(0f, since - 0.4f) / 0.3f;
                Text(_popWord, new Vector2(0, size.Y * 0.52f), (int)(40 * pop), c, HorizontalAlignment.Center, size.X, bold: true);
            }
        }

        /// <summary>The beats to the first move, counted down on the shared clock.</summary>
        private void DrawCountdown(AuraManager.View v, Vector2 size)
        {
            if (v.T0 <= 0) return;
            double left = v.T0 - ClockSync.ServerNow;
            int beats = Mathf.CeilToInt((float)(left / AuraRules.BeatSeconds));
            if (beats is < 1 or > 4) return;
            Text(beats.ToString(), new Vector2(0, size.Y * 0.46f), 72, Gold, HorizontalAlignment.Center, size.X, bold: true);
        }

        /// <summary>
        /// Performing: the move on now, big, and the beat ring closing on its cut. The performer sees
        /// the ring and the key; the watcher sees the moves and the grades land.
        /// </summary>
        private void DrawPerform(AuraManager.View v, Vector2 size)
        {
            var centre = new Vector2(size.X * 0.5f, size.Y * 0.70f);
            Text(_moveNow, new Vector2(0, centre.Y - 96f), 34, UiTheme.Text, HorizontalAlignment.Center, size.X, bold: true);
            Text(_moveNext, new Vector2(0, centre.Y - 66f), UiTheme.FontBody, UiTheme.TextDim, HorizontalAlignment.Center, size.X);
            // the pips: one per move, lit by its grade
            float pipX = centre.X - (v.Plan.Length - 1) * 14f;
            for (int i = 0; i < v.Plan.Length; i++)
            {
                var g = i < v.Grades.Length ? v.Grades[i] : AuraGrade.Pending;
                var c = new Vector2(pipX + i * 28f, centre.Y + 70f);
                DrawCircle(c, 9f, Shadow);
                DrawCircle(c, 7f, g == AuraGrade.Pending ? new Color(0.25f, 0.25f, 0.3f) : Of(g));
            }
            if (!v.MyTurn || v.NextIndex >= v.Plan.Length) return;

            // the ring closes on the cut: full at a beat out, on the target circle at the downbeat
            double now = ClockSync.ServerNow;
            double cut = AuraRules.CutAt(v.Plan, v.NextIndex, v.T0);
            float toCut = (float)((cut - now) / AuraRules.BeatSeconds);
            const float target = 34f;
            DrawArc(centre, target, 0f, Mathf.Tau, 48, Gold, 4f, true);
            if (toCut is > -0.3f and < 2f)
            {
                float r = target + Mathf.Max(0f, toCut) * 60f;
                var c = toCut < 0f ? Red : Mathf.Abs(toCut) < 0.12f ? Gold : UiTheme.Text;
                DrawArc(centre, r, 0f, Mathf.Tau, 64, c, 3f, true);
            }
            // the beat itself: a dot pulsing on every beat of the shared clock
            float beat = (float)(now / AuraRules.BeatSeconds % 1.0);
            DrawCircle(centre, 8f + 6f * (1f - beat), Violet);
            Text(_pressHint, new Vector2(0, centre.Y + 108f), UiTheme.FontSmall, UiTheme.TextDim, HorizontalAlignment.Center, size.X);
        }

        /// <summary>The verdict: the parts one by one, then the total.</summary>
        private void DrawVerdict(Vector2 size)
        {
            float since = (float)(GameClock.Now - _verdictAt);
            if (since > 6f) return;
            float width = 360f, rowH = 26f;
            var box = new Rect2(size.X * 0.5f - width * 0.5f, size.Y * 0.30f, width, 40f + rowH * (_verdict.Count + 1));
            DrawRect(box, UiTheme.Glass);
            int shown = Math.Min(_verdict.Count, (int)(since / 0.25f) + 1);
            for (int i = 0; i < shown; i++)
                Text(_verdict[i].Text, box.Position + new Vector2(20f, 30f + i * rowH), UiTheme.FontBody, _verdict[i].Colour, HorizontalAlignment.Left, width - 40f);
            if (shown >= _verdict.Count)
                Text(_verdictTotal, box.Position + new Vector2(0, 40f + _verdict.Count * rowH), 30, Gold, HorizontalAlignment.Center, width, bold: true);
        }

        private void Text(string s, Vector2 at, int size, Color colour, HorizontalAlignment align, float width, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            DrawString(font, at + new Vector2(2f, 2f), s, align, width, size, new Color(0, 0, 0, Shadow.A * colour.A));
            DrawString(font, at, s, align, width, size, colour);
        }
    }
}
