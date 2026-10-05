using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Combat;

/// <summary>
/// The fist fight's screen (#495), drawn over the world while the local player fights: the two
/// health bars at the top (the local fighter on the left, as P1), a white trail that drains after
/// a blow, round pips, the round clock, and the banners: ROUND n / FIGHT! / K.O. / PERFECT / TIME /
/// FINISH THEM! / FATALITY / the winner. Out of a fight it shows a challenge waiting for an answer.
/// Every key it names comes from <see cref="InputHints"/>.
/// </summary>
public partial class FightHud : CanvasLayer
{
    private readonly View _view;

    public FightHud()
    {
        Layer = 8;
        Name = "FightHud";
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _Process(double delta)
    {
        var fights = FightManager.Client;
        bool show = fights != null && (fights.Current != null || fights.Pending.Count > 0);
        _view.Visible = show;
        if (!show) return;
        _view.Step((float)delta, fights!);
        _view.QueueRedraw();
    }

    private sealed partial class View : Control
    {
        private FightManager? _fights;
        private float _mine = 1f, _theirs = 1f, _mineTrail = 1f, _theirsTrail = 1f;
        private int _lastId;
        private string _specials = "";
        private string _finish = "";
        private double _hintsAt = double.NegativeInfinity;
        // text only rebuilt when it changes (perf-no-per-frame-allocations)
        private int _secs = -1;
        private string _clockText = "";
        private (FightPhase, int, FightEnd, int, long) _bannerKey = ((FightPhase)255, 0, 0, 0, 0);
        private string? _big, _small, _me = "", _them = "";
        private Color _colour;
        private long _pendingFrom;
        private int _pendingLeft = -1;
        private string _pendingWho = "", _pendingHow = "";

        private static readonly Color Red = new(0.86f, 0.16f, 0.12f);
        private static readonly Color Yellow = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public void Step(float dt, FightManager fights)
        {
            _fights = fights;
            if (fights.Current is not { } v)
            {
                StepPending(fights);
                return;
            }
            if (v.Id != _lastId)
            {
                _lastId = v.Id;
                _mine = _theirs = _mineTrail = _theirsTrail = 1f;
                _me = (v.IsA ? v.NameA : v.NameB).ToUpperInvariant();
                _them = v.OpponentName.ToUpperInvariant();
            }
            float mine = v.MyHp / (float)FightRules.MaxHp, theirs = v.TheirHp / (float)FightRules.MaxHp;
            // the bar drops at once, the trail behind it drains after a beat
            _mine = mine;
            _theirs = theirs;
            _mineTrail = _mineTrail < mine ? mine : Mathf.MoveToward(_mineTrail, mine, dt * 0.45f);
            _theirsTrail = _theirsTrail < theirs ? theirs : Mathf.MoveToward(_theirsTrail, theirs, dt * 0.45f);
            // the move list names the device in hand; rebuilt now and then, not every frame
            if (GameClock.Now - _hintsAt > 1.0)
            {
                _hintsAt = GameClock.Now;
                _specials = InputHints.Format("Uppercut: down, forward + {fight_punch}    String: {fight_punch} {fight_punch} {fight_kick}    Sweep: down + {fight_kick}    Jump kick: up + {fight_kick}");
                _finish = InputHints.Format("down, down + {fight_kick}");
                _bannerKey = ((FightPhase)255, 0, 0, 0, 0);
            }
            if (Mathf.CeilToInt(v.Clock) is var secs && secs != _secs)
            {
                _secs = secs;
                _clockText = secs.ToString();
            }
            // ---- banners: rebuilt when the phase or its stage changes ----
            float since = (float)(GameClock.Now - v.PhaseAt);
            int stage = v.Phase switch
            {
                FightPhase.Intro => since < FightMatch.IntroSeconds * 0.6f ? 0 : 1,
                FightPhase.Live => since < 0.6f ? 0 : 1,
                FightPhase.RoundOver => since > 1.2f ? 1 : 0,
                _ => 0,
            };
            var key = (v.Phase, v.Round, v.End, stage, v.Winner);
            if (key == _bannerKey) return;
            _bannerKey = key;
            string? big = null, small = null;
            Color colour = Yellow;
            switch (v.Phase)
            {
                case FightPhase.Intro:
                    bool final = v.WinsA == FightRules.WinsNeeded - 1 && v.WinsB == FightRules.WinsNeeded - 1;
                    big = since < FightMatch.IntroSeconds * 0.6f ? (final ? "FINAL ROUND" : $"ROUND {v.Round}") : "FIGHT!";
                    if (v.Round == 1) small = _specials;
                    break;
                case FightPhase.Live when since < 0.6f:
                    big = "FIGHT!";
                    break;
                case FightPhase.RoundOver:
                    big = v.End switch { FightEnd.Perfect => "PERFECT", FightEnd.Time => "TIME", FightEnd.Draw => "DRAW", _ => "K.O." };
                    colour = v.End == FightEnd.Perfect ? UiTheme.Amber : Red;
                    if (since > 1.2f && v.Winner != 0) small = $"{Winner(v)} wins the round";
                    break;
                case FightPhase.FinishHim:
                    big = "FINISH THEM!";
                    colour = Red;
                    small = v.Winner == v.Me ? _finish : "Dazed...";
                    break;
                case FightPhase.Done:
                    big = v.End == FightEnd.Fatality ? "FATALITY" : v.Winner == 0 ? "DRAW" : $"{Winner(v).ToUpperInvariant()} WINS";
                    colour = v.End == FightEnd.Fatality ? Red : Yellow;
                    if (v.End == FightEnd.Forfeit) small = "by forfeit";
                    else if (v.End == FightEnd.Fatality) small = $"{Winner(v)} wins";
                    break;
            }
            _big = big;
            _small = small;
            _colour = colour;
        }

        private void StepPending(FightManager fights)
        {
            long from = 0;
            double until = 0;
            foreach (var (peer, at) in fights.Pending)
                if (at > until) { until = at; from = peer; }
            int left = Mathf.CeilToInt((float)(until - GameClock.Now));
            if (from == _pendingFrom && left == _pendingLeft) return;
            _pendingFrom = from;
            _pendingLeft = left;
            _pendingWho = $"{fights.NameOf(from) ?? "Someone"} challenges you to a fight!";
            _pendingHow = InputHints.Format($"Look at them, {{interact_mount}} to accept  ·  {left} s");
        }

        public override void _Draw()
        {
            if (_fights == null) return;
            var size = Size;
            if (_fights.Current is not { } v)
            {
                DrawPending(size);
                return;
            }

            // ---- bars --------------------------------------------------------------------
            float w = Mathf.Min(size.X * 0.36f, 520f), h = 22f, top = 34f, gapMid = 56f;
            float leftX = size.X * 0.5f - gapMid - w, rightX = size.X * 0.5f + gapMid;
            int myWins = v.IsA ? v.WinsA : v.WinsB, theirWins = v.IsA ? v.WinsB : v.WinsA;
            Bar(new Rect2(leftX, top, w, h), _mine, _mineTrail, fromRight: true);
            Bar(new Rect2(rightX, top, w, h), _theirs, _theirsTrail, fromRight: false);
            Text(_me!, new Vector2(leftX, top - 8f), UiTheme.FontBody, UiTheme.Text, HorizontalAlignment.Left, w);
            Text(_them!, new Vector2(rightX, top - 8f), UiTheme.FontBody, UiTheme.Text, HorizontalAlignment.Right, w);
            Pips(new Vector2(leftX + w - 10f, top + h + 12f), myWins, -1f);
            Pips(new Vector2(rightX + 10f, top + h + 12f), theirWins, 1f);

            // ---- clock ---------------------------------------------------------------------
            var clock = new Rect2(size.X * 0.5f - 44f, top - 12f, 88f, 50f);
            DrawRect(clock, UiTheme.Glass);
            Text(_clockText, new Vector2(clock.Position.X, clock.Position.Y + 40f), 36,
                v.Phase == FightPhase.Live && _secs <= 10 ? Red : Yellow, HorizontalAlignment.Center, clock.Size.X);

            if (_big != null)
            {
                // a punch-in: big for an instant, then settling
                float since = (float)(GameClock.Now - v.PhaseAt);
                float pop = 1f + 0.35f * Mathf.Max(0f, 1f - since * 5f);
                Text(_big, new Vector2(0, size.Y * 0.40f), (int)(64 * pop), _colour, HorizontalAlignment.Center, size.X, bold: true);
            }
            if (_small != null)
                Text(_small, new Vector2(0, size.Y * 0.40f + 44f), UiTheme.FontHeading, UiTheme.Text, HorizontalAlignment.Center, size.X);
        }

        private static string Winner(FightManager.View v) => v.Winner == v.A ? v.NameA : v.NameB;

        /// <summary>A challenge to the local player, waiting: who, how to answer, how long is left.</summary>
        private void DrawPending(Vector2 size)
        {
            if (_pendingFrom == 0) return;
            var box = new Rect2(size.X * 0.5f - 260f, 90f, 520f, 64f);
            DrawRect(box, UiTheme.Glass);
            DrawRect(new Rect2(box.Position, new Vector2(4f, box.Size.Y)), Red);
            Text(_pendingWho, box.Position + new Vector2(0, 26f), UiTheme.FontHeading, Yellow, HorizontalAlignment.Center, box.Size.X, bold: true);
            Text(_pendingHow, box.Position + new Vector2(0, 50f), UiTheme.FontSmall, UiTheme.TextDim, HorizontalAlignment.Center, box.Size.X);
        }

        private void Bar(Rect2 r, float hp, float trail, bool fromRight)
        {
            DrawRect(r.Grow(3f), Shadow);
            DrawRect(r, new Color(0.18f, 0.04f, 0.04f));
            // drains towards the middle of the screen, like every fighter's bars
            Rect2 Part(float f) => fromRight
                ? new Rect2(r.Position.X + r.Size.X * (1f - f), r.Position.Y, r.Size.X * f, r.Size.Y)
                : new Rect2(r.Position, new Vector2(r.Size.X * f, r.Size.Y));
            DrawRect(Part(trail), new Color(1f, 1f, 1f, 0.85f));
            DrawRect(Part(hp), hp > 0.3f ? Yellow : Red);
            DrawRect(r, UiTheme.Hairline, filled: false, width: 1f);
        }

        private void Pips(Vector2 at, int wins, float dir)
        {
            for (int i = 0; i < FightRules.WinsNeeded; i++)
            {
                var c = at + new Vector2(dir * i * 18f, 0f);
                DrawCircle(c, 6.5f, Shadow);
                DrawCircle(c, 5f, i < wins ? UiTheme.Amber : new Color(0.25f, 0.25f, 0.28f));
            }
        }

        private void Text(string s, Vector2 at, int size, Color colour, HorizontalAlignment align, float width, bool bold = false)
        {
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            DrawString(font, at + new Vector2(2f, 2f), s, align, width, size, Shadow);
            DrawString(font, at, s, align, width, size, colour);
        }
    }
}
