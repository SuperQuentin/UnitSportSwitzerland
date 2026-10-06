using Godot;
using UnitSport.Ui;

namespace UnitSport.Movie;

/// <summary>
/// The studio's timeline (#638): a ruler, one lane per actor, the clips on them and the playhead.
/// Click the ruler or an empty spot to seek and drag to scrub; drag a clip to move it, its edges to
/// trim it. The wheel scrolls, Ctrl + wheel zooms. Focused (pad), left and right step a frame.
/// </summary>
public partial class TimelineView : Control
{
    private const float Ruler = 24, LaneH = 34, Labels = 120, Edge = 7;

    private readonly MovieStage _stage;
    private MovieProject Project => _stage.Project;
    private float _pxPerSec = 12;
    private double _left;   // the time at the left edge of the clip area
    private Drag _drag;
    private (double, double, float, int, int, bool, Vector2) _drawn;
    private int _dragClip;
    private double _grabOffset;

    /// <summary>The selected clip's id, 0 for none.</summary>
    public int Selected { get; set; }
    public event Action? SelectionChanged;

    private enum Drag { None, Scrub, Move, TrimStart, TrimEnd }

    public TimelineView(MovieStage stage)
    {
        _stage = stage;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        CustomMinimumSize = new Vector2(0, Ruler + LaneH * 3);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    private float Width0 => Size.X - Labels;
    private float X(double t) => Labels + (float)((t - _left) * _pxPerSec);
    private double T(float x) => _left + (x - Labels) / _pxPerSec;

    /// <summary>Fits the whole movie in view.</summary>
    public void Fit()
    {
        double d = Math.Max(Project.Duration, 10);
        _pxPerSec = Math.Clamp((float)(Math.Max(Width0, 200) / (d * 1.05)), 0.5f, 400f);
        _left = 0;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        float wanted = Ruler + LaneH * Math.Max(3, Project.Lanes.Count);
        if (!Mathf.IsEqualApprox(CustomMinimumSize.Y, wanted)) CustomMinimumSize = new Vector2(0, wanted);
        // playing off screen: the view follows the playhead
        if (_stage.Playing && _drag == Drag.None && Width0 > 0)
        {
            double right = T(Size.X);
            if (_stage.Time > right || _stage.Time < _left) _left = Math.Max(0, _stage.Time - (right - _left) * 0.1);
        }
        // drawn again only when something it shows moved
        var drawn = (_stage.Time, _left, _pxPerSec, Project.Clips.Count, Selected, HasFocus(), Size);
        if (drawn != _drawn) { _drawn = drawn; QueueRedraw(); }
    }

    public override void _Draw()
    {
        var font = UiTheme.Font;
        float w = Size.X, h = Size.Y;
        DrawRect(new Rect2(0, 0, w, h), new Color(0.03f, 0.035f, 0.045f, 0.75f));
        DrawRect(new Rect2(0, 0, w, Ruler), new Color(1, 1, 1, 0.04f));

        // ruler: a tick step that keeps labels ~80 px apart
        double step = TickStep(80 / _pxPerSec);
        for (double t = Math.Floor(_left / step) * step; X(t) < w; t += step)
        {
            if (t < 0) continue;
            float x = X(t);
            if (x < Labels) continue;
            DrawLine(new Vector2(x, Ruler - 8), new Vector2(x, h), UiTheme.Hairline);
            DrawString(font, new Vector2(x + 3, Ruler - 8), Label((int)Math.Round(t)), HorizontalAlignment.Left, -1, UiTheme.FontTiny, UiTheme.TextFaint);
        }

        for (int lane = 0; lane < Project.Lanes.Count; lane++)
        {
            float y = Ruler + lane * LaneH;
            if (lane % 2 == 1) DrawRect(new Rect2(0, y, w, LaneH), new Color(1, 1, 1, 0.025f));
            DrawString(font, new Vector2(10, y + LaneH * 0.62f), Project.Lanes[lane].Label, HorizontalAlignment.Left, Labels - 16,
                UiTheme.FontSmall, _stage.Puppet(lane) != null ? UiTheme.Text : UiTheme.TextDim);
        }

        foreach (var c in Project.Clips)
        {
            float x0 = Math.Max(X(c.Start), Labels), x1 = X(c.End);
            if (x1 < Labels || x0 > w) continue;
            var r = new Rect2(x0, Ruler + c.Lane * LaneH + 4, Math.Max(2, x1 - x0), LaneH - 8);
            bool sel = c.Id == Selected;
            DrawRect(r, sel ? new Color(UiTheme.Amber, 0.85f) : new Color(0.35f, 0.47f, 0.62f, 0.85f));
            DrawRect(r, sel ? Colors.White : new Color(1, 1, 1, 0.25f), filled: false, width: sel ? 2 : 1);
            if (r.Size.X > 40)
                DrawString(font, r.Position + new Vector2(6, r.Size.Y * 0.68f), Label((int)Math.Round(c.Length)),
                    HorizontalAlignment.Left, r.Size.X - 10, UiTheme.FontTiny, sel ? Colors.Black : UiTheme.Text);
        }

        DrawLine(new Vector2(Labels, 0), new Vector2(Labels, h), UiTheme.Hairline);
        float px = X(_stage.Time);
        if (px >= Labels && px <= w)
        {
            DrawLine(new Vector2(px, 0), new Vector2(px, h), UiTheme.Amber, 2);
            DrawRect(new Rect2(px - 5, 0, 10, 8), UiTheme.Amber);
        }
        if (HasFocus()) DrawRect(new Rect2(1, 1, w - 2, h - 2), UiTheme.AmberDim, filled: false, width: 2);
    }

    private static readonly double[] Steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200 };

    private static double TickStep(double atLeast)
    {
        foreach (double s in Steps)
            if (s >= atLeast) return s;
        return Steps[^1];
    }

    // "m:ss" by whole second, made once each: the ruler and the clips are drawn while playing
    private static readonly Dictionary<int, string> ClockLabels = new();
    private static string Label(int seconds)
    {
        if (!ClockLabels.TryGetValue(seconds, out var text)) ClockLabels[seconds] = text = MovieSession.Clock(seconds);
        return text;
    }

    private (Clip? Clip, Drag Part) HitClip(Vector2 at)
    {
        if (at.Y < Ruler || at.X < Labels) return (null, Drag.None);
        int lane = (int)((at.Y - Ruler) / LaneH);
        // the topmost (latest-starting) clip under the pointer, as ActiveClip plays it
        Clip? hit = null;
        foreach (var c in Project.Clips)
            if (c.Lane == lane && at.X >= X(c.Start) - Edge && at.X <= X(c.End) + Edge && (hit == null || c.Start >= hit.Start)) hit = c;
        if (hit == null) return (null, Drag.None);
        if (Math.Abs(at.X - X(hit.Start)) <= Edge) return (hit, Drag.TrimStart);
        if (Math.Abs(at.X - X(hit.End)) <= Edge) return (hit, Drag.TrimEnd);
        return (hit, Drag.Move);
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                if (!b.Pressed) { _drag = Drag.None; AcceptEvent(); return; }
                GrabFocus();
                var (clip, part) = HitClip(b.Position);
                if (clip == null || b.Position.Y < Ruler)
                {
                    _drag = Drag.Scrub;
                    _stage.Playing = false;
                    _stage.Seek(T(b.Position.X));
                }
                else
                {
                    Select(clip.Id);
                    _drag = part;
                    _dragClip = clip.Id;
                    _grabOffset = T(b.Position.X) - clip.Start;
                }
                AcceptEvent();
                return;

            case InputEventMouseMotion m when _drag != Drag.None:
                double t = T(m.Position.X);
                switch (_drag)
                {
                    case Drag.Scrub: _stage.Seek(t); break;
                    case Drag.Move: Project.Move(_dragClip, t - _grabOffset); break;
                    case Drag.TrimStart: Project.TrimStart(_dragClip, t); break;
                    case Drag.TrimEnd: Project.TrimEnd(_dragClip, t); break;
                }
                if (_drag != Drag.Scrub) _stage.Apply();
                QueueRedraw();
                AcceptEvent();
                return;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                float dir = wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                if (wheel.CtrlPressed)
                {
                    // zoom about the pointer
                    double at = T(wheel.Position.X);
                    _pxPerSec = Math.Clamp(_pxPerSec * (dir > 0 ? 1.25f : 0.8f), 0.5f, 400f);
                    _left = Math.Max(0, at - (wheel.Position.X - Labels) / _pxPerSec);
                }
                else _left = Math.Max(0, _left - dir * 60 / _pxPerSec);
                AcceptEvent();
                return;
        }

        // focused, a pad's (or the arrows') left and right step a frame, up and down pick a lane's clip
        if (e.IsActionPressed("ui_left", allowEcho: true)) { Step(-1); AcceptEvent(); }
        else if (e.IsActionPressed("ui_right", allowEcho: true)) { Step(1); AcceptEvent(); }
    }

    private void Step(int frames)
    {
        _stage.Playing = false;
        _stage.Seek(_stage.Time + frames / Channels.Rate);
    }

    public void Select(int id)
    {
        if (Selected == id) return;
        Selected = id;
        SelectionChanged?.Invoke();
    }
}
