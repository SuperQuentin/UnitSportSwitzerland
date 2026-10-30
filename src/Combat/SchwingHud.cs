using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Combat;

/// <summary>
/// Schwingen's screen (#1269), drawn over the world while the local player wrestles: the two balance bars at the top (the local
/// wrestler on the left) with a thin grip bar under each, the Gang's clock, a banner for each phase, the telegraph of a throw against
/// the local player with how to answer it (named for the device in hand), and the tug meter on the ground. Out of a bout it shows the
/// ring's queue. Every key it names comes from <see cref="InputHints"/>; text is rebuilt only when it changes, never per frame.
/// Edges (ui/ux-guidelines): bars and clock on top, banners and the telegraph just above the middle, the tug meter and the controls at
/// the bottom, the centre of the screen clear.
/// </summary>
public partial class SchwingHud : CanvasLayer
{
    private readonly View _view;

    public SchwingHud()
    {
        Layer = 8;
        Name = "SchwingHud";
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _Process(double delta)
    {
        var bouts = SchwingManager.Client;
        bool show = bouts != null && (bouts.Current != null || bouts.Queued);
        _view.Visible = show;
        if (!show) return;
        _view.Step(bouts!);
        _view.QueueRedraw();
    }

    /// <summary>How the stick (or the other device's pad) makes <paramref name="lean"/>, in words.</summary>
    public static string Say(SchwingLean lean)
    {
        bool vr = XR.XrSession.Active;
        return lean switch
        {
            SchwingLean.In => vr ? "forward" : "stick right",
            SchwingLean.Out => vr ? "back" : "stick left",
            SchwingLean.Left => vr ? "left" : "stick up",
            SchwingLean.Right => vr ? "right" : "stick down",
            _ => "",
        };
    }

    private sealed partial class View : Control
    {
        private SchwingManager? _bouts;
        private int _lastId;
        private string _me = "", _them = "", _clockText = "", _controls = "", _tugHint = "", _queueText = "";
        private int _secs = -1;
        private double _hintsAt = double.NegativeInfinity;
        private (SchwingPhase, SchwingEnd, long, SchwingThrow, long) _key = ((SchwingPhase)255, 0, 0, 0, 0);
        private string? _big, _small, _answer;
        private Color _colour;
        private float _mine = 1f, _theirs = 1f;

        private static readonly Color Red = new(0.86f, 0.16f, 0.12f);
        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);
        private static readonly Color Sawdust = new(0.84f, 0.7f, 0.46f);

        public void Step(SchwingManager bouts)
        {
            _bouts = bouts;
            if (bouts.Current is not { } v)
            {
                if (GameClock.Now - _hintsAt > 1.0)
                {
                    _hintsAt = GameClock.Now;
                    _queueText = InputHints.Format("{interact_mount} to leave the queue");
                }
                return;
            }
            if (v.Id != _lastId)
            {
                _lastId = v.Id;
                _me = (v.IsA ? v.NameA : v.NameB).ToUpperInvariant();
                _them = v.OpponentName.ToUpperInvariant();
                _mine = _theirs = 1f;
                _key = ((SchwingPhase)255, 0, 0, 0, 0);
            }
            _mine = Mathf.MoveToward(_mine, v.MyBalance / 100f, 0.02f);
            _theirs = Mathf.MoveToward(_theirs, v.TheirBalance / 100f, 0.02f);
            if (Mathf.CeilToInt(v.Clock) is var secs && secs != _secs)
            {
                _secs = secs;
                _clockText = $"{secs / 60}:{secs % 60:00}";
            }
            if (GameClock.Now - _hintsAt > 1.0)
            {
                _hintsAt = GameClock.Now;
                _controls = InputHints.Format("Lean: move   ·   Throw: {fight_punch} (the move picks it), {fight_kick} hooks a leg   ·   Brace: hold {fight_block}");
                _tugHint = InputHints.Format("Mash {fight_punch} to pin them");
                _key = ((SchwingPhase)255, 0, 0, 0, 0);
            }
            var key = (v.Phase, v.End, v.Winner, v.Pending, v.ThrowBy);
            if (key == _key) return;
            _key = key;
            string? big = null, small = null, answer = null;
            Color colour = Gold;
            switch (v.Phase)
            {
                case SchwingPhase.Intro:
                    big = v.Schlussgang ? "SCHLUSSGANG" : "SCHWINGEN";
                    small = "Gripped: hold your own";
                    break;
                case SchwingPhase.Live when v.Pending != SchwingThrow.None:
                    var d = SchwingRules.Def(v.Pending);
                    if (v.ThrowBy == v.Me)
                    {
                        big = d.Name.ToUpperInvariant();
                        small = "They have a moment to answer";
                        colour = Sawdust;
                    }
                    else
                    {
                        big = d.Name.ToUpperInvariant() + "!";
                        colour = Red;
                        answer = InputHints.Format($"Hold {{fight_block}} and lean {SchwingHud.Say(d.Brace)}, or lean {SchwingHud.Say(d.Counter)} to counter");
                    }
                    break;
                case SchwingPhase.Ground:
                    big = v.IAmTop ? "PIN THEM" : "GET OUT";
                    small = _tugHint;
                    colour = v.IAmTop ? Gold : Red;
                    break;
                case SchwingPhase.Done:
                    big = v.Winner == 0 ? "GESTELLT" : v.Winner == v.Me ? "YOU WIN" : $"{(v.Winner == v.A ? v.NameA : v.NameB).ToUpperInvariant()} WINS";
                    small = v.End switch
                    {
                        SchwingEnd.Forfeit => "by forfeit",
                        SchwingEnd.Won => "both shoulders in the sawdust",
                        _ => "9 points each",
                    };
                    colour = v.Winner == v.Me ? Gold : UiTheme.Text;
                    break;
            }
            _big = big;
            _small = small;
            _answer = answer;
            _colour = colour;
        }

        public override void _Draw()
        {
            if (_bouts == null) return;
            var size = Size;
            if (_bouts.Current is not { } v)
            {
                var box = new Rect2(size.X * 0.5f - 260f, 90f, 520f, 64f);
                DrawRect(box, UiTheme.Glass);
                DrawRect(new Rect2(box.Position, new Vector2(4f, box.Size.Y)), Sawdust);
                Text("In the queue for the Schwingen", box.Position + new Vector2(0, 26f), UiTheme.FontHeading, Gold, HorizontalAlignment.Center, box.Size.X, bold: true);
                Text(_queueText, box.Position + new Vector2(0, 50f), UiTheme.FontSmall, UiTheme.TextDim, HorizontalAlignment.Center, box.Size.X);
                return;
            }

            // ---- balance bars, grip under them --------------------------------------------------
            float w = Mathf.Min(size.X * 0.36f, 520f), h = 20f, top = 34f, gapMid = 56f;
            float leftX = size.X * 0.5f - gapMid - w, rightX = size.X * 0.5f + gapMid;
            Bar(new Rect2(leftX, top, w, h), _mine, true, v.MyGrip / 100f);
            Bar(new Rect2(rightX, top, w, h), _theirs, false, v.TheirGrip / 100f);
            Text(_me, new Vector2(leftX, top - 8f), UiTheme.FontBody, UiTheme.Text, HorizontalAlignment.Left, w);
            Text(_them, new Vector2(rightX, top - 8f), UiTheme.FontBody, UiTheme.Text, HorizontalAlignment.Right, w);

            var clock = new Rect2(size.X * 0.5f - 44f, top - 12f, 88f, 50f);
            DrawRect(clock, UiTheme.Glass);
            Text(_clockText, new Vector2(clock.Position.X, clock.Position.Y + 38f), 30,
                v.Phase == SchwingPhase.Live && _secs <= 10 ? Red : Gold, HorizontalAlignment.Center, clock.Size.X);

            // ---- the banner, the telegraph's bar and how to answer ------------------------------
            if (_big != null)
            {
                float since = (float)(GameClock.Now - v.PhaseAt);
                float pop = 1f + 0.3f * Mathf.Max(0f, 1f - since * 5f);
                Text(_big, new Vector2(0, size.Y * 0.30f), (int)(54 * pop), _colour, HorizontalAlignment.Center, size.X, bold: true);
            }
            if (_small != null)
                Text(_small, new Vector2(0, size.Y * 0.30f + 38f), UiTheme.FontHeading, UiTheme.Text, HorizontalAlignment.Center, size.X);
            if (v.Pending != SchwingThrow.None)
            {
                var bar = new Rect2(size.X * 0.5f - 130f, size.Y * 0.30f + 56f, 260f, 10f);
                DrawRect(bar.Grow(2f), Shadow);
                DrawRect(bar, new Color(0.2f, 0.06f, 0.05f));
                DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * v.Winding, bar.Size.Y)), v.ThrowBy == v.Me ? Gold : Red);
                if (_answer != null)
                    Text(_answer, new Vector2(0, bar.Position.Y + 34f), UiTheme.FontHeading, UiTheme.Text, HorizontalAlignment.Center, size.X, bold: true);
            }

            // ---- the tug meter, bottom ---------------------------------------------------------
            if (v.Phase == SchwingPhase.Ground)
            {
                var meter = new Rect2(size.X * 0.5f - 200f, size.Y - 120f, 400f, 18f);
                DrawRect(meter.Grow(3f), Shadow);
                DrawRect(meter, new Color(0.16f, 0.1f, 0.06f));
                float f = v.Tug / 100f;
                // the pinned side fills from its end: full means shoulders down
                DrawRect(new Rect2(meter.Position, new Vector2(meter.Size.X * (v.IAmTop ? f : 1f - f), meter.Size.Y)), v.IAmTop ? Gold : Red);
                DrawRect(meter, UiTheme.Hairline, filled: false, width: 1f);
            }
            else if (v.Phase == SchwingPhase.Live)
                Text(_controls, new Vector2(0, size.Y - 84f), UiTheme.FontSmall, UiTheme.TextDim, HorizontalAlignment.Center, size.X);
        }

        private void Bar(Rect2 r, float f, bool fromRight, float grip)
        {
            DrawRect(r.Grow(3f), Shadow);
            DrawRect(r, new Color(0.16f, 0.1f, 0.06f));
            Rect2 Part(float x, float thickness) => fromRight
                ? new Rect2(r.Position.X + r.Size.X * (1f - x), r.Position.Y, r.Size.X * x, thickness)
                : new Rect2(r.Position, new Vector2(r.Size.X * x, thickness));
            DrawRect(Part(f, r.Size.Y), f > 0.3f ? Sawdust : Red);
            DrawRect(r, UiTheme.Hairline, filled: false, width: 1f);
            // the grip: a thin strip under the bar
            var under = new Rect2(r.Position.X, r.End.Y + 3f, r.Size.X, 5f);
            DrawRect(under, new Color(0.1f, 0.1f, 0.12f));
            var g = fromRight
                ? new Rect2(under.Position.X + under.Size.X * (1f - grip), under.Position.Y, under.Size.X * grip, under.Size.Y)
                : new Rect2(under.Position, new Vector2(under.Size.X * grip, under.Size.Y));
            DrawRect(g, grip > 0.15f ? UiTheme.Amber : Red);
        }

        private void Text(string s, Vector2 at, int size, Color colour, HorizontalAlignment align, float width, bool bold = false)
        {
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            DrawString(font, at + new Vector2(2f, 2f), s, align, width, size, Shadow);
            DrawString(font, at, s, align, width, size, colour);
        }
    }
}
