using Godot;
using UnitSport.Ui;

namespace UnitSport.Movie;

/// <summary>
/// The studio's timeline (#638): a ruler, one lane per actor, the sound lanes under them (#656),
/// the clips, the beats and markers, and the playhead. Click the ruler or an empty spot to seek
/// and drag to scrub; drag a clip to move it, its edges to trim it (snapping to markers, beats and
/// other clips' edges; Shift moves freely). The wheel scrolls, Ctrl + wheel zooms. The music's
/// beats show as faint ghost markers: double-click one to keep it as a marker, double-click a
/// marker to drop it. Focused (pad), left and right step a frame.
/// </summary>
public partial class TimelineView : Control
{
    private const float Ruler = 24, LaneH = 34, Labels = 120, Edge = 7, SnapPx = 8, HitPx = 6;

    private static readonly Color ActorClip = new(0.35f, 0.47f, 0.62f, 0.85f);
    private static readonly Color SoundClip = new(0.18f, 0.5f, 0.46f, 0.85f);
    private static readonly Color GameSoundClip = new(0.42f, 0.4f, 0.55f, 0.85f);
    private static readonly Color Wave = new(1, 1, 1, 0.45f);
    private static readonly Color Ghost = new(1, 1, 1, 0.13f), GhostDown = new(1, 1, 1, 0.28f);
    private static readonly Color Marker = new(1f, 0.45f, 0.25f, 0.95f);

    private readonly MovieStage _stage;
    private MovieProject Project => _stage.Project;
    private float _pxPerSec = 12;
    private double _left;   // the time at the left edge of the clip area
    private Drag _drag;
    private (double, double, float, int, int, bool, Vector2, double, int) _drawn;
    private int _dragClip;
    private double _grabOffset;

    // the ghost beats and the sound lanes' names, worked out again only when a sound clip changes
    private List<(double T, bool Downbeat)> _beats = new();
    private readonly List<string> _soundLabels = new();
    private double _soundKey = double.NaN;

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
    private int Rows => Project.Lanes.Count + Project.AudioLanes.Count;
    private int Row(Clip c) => c.Audio ? Project.Lanes.Count + c.Lane : c.Lane;

    /// <summary>Fits the whole movie in view.</summary>
    public void Fit()
    {
        double d = Math.Max(Project.Duration, 10);
        _pxPerSec = Math.Clamp((float)(Math.Max(Width0, 200) / (d * 1.05)), 0.5f, 400f);
        _left = 0;
        _soundKey = double.NaN;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        float wanted = Ruler + LaneH * Math.Max(3, Rows);
        if (!Mathf.IsEqualApprox(CustomMinimumSize.Y, wanted)) CustomMinimumSize = new Vector2(0, wanted);
        // playing off screen: the view follows the playhead
        if (_stage.Playing && _drag == Drag.None && Width0 > 0)
        {
            double right = T(Size.X);
            if (_stage.Time > right || _stage.Time < _left) _left = Math.Max(0, _stage.Time - (right - _left) * 0.1);
        }
        double soundKey = SoundKey();
        if (soundKey != _soundKey) { _soundKey = soundKey; Resound(); }
        // drawn again only when something it shows moved
        var drawn = (_stage.Time, _left, _pxPerSec, Project.Clips.Count, Selected, HasFocus(), Size, soundKey, Project.Markers.Count);
        if (drawn != _drawn) { _drawn = drawn; QueueRedraw(); }
    }

    /// <summary>A number that changes whenever any sound clip is added, cut, moved or trimmed. No allocation.</summary>
    private double SoundKey()
    {
        double k = Project.AudioLanes.Count;
        foreach (var c in Project.Clips)
            if (c.Audio) k = k * 1.000123 + c.Id * 0.731 + c.Track * 1.37 + c.In * 3.1 + c.Out * 5.3 + c.Start * 7.7;
        return k;
    }

    private void Resound()
    {
        _beats = Project.Beats();
        _soundLabels.Clear();
        for (int lane = 0; lane < Project.AudioLanes.Count; lane++)
        {
            var music = Project.Clips.Find(c => c.Audio && c.Lane == lane && Project.Audio[c.Track].Beat.Bpm > 0);
            _soundLabels.Add(music == null ? Project.AudioLanes[lane]
                : $"{Project.AudioLanes[lane]} · {Project.Audio[music.Track].Beat.Bpm:F0} BPM");
        }
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

        for (int row = 0; row < Rows; row++)
        {
            float y = Ruler + row * LaneH;
            bool sound = row >= Project.Lanes.Count;
            if (row % 2 == 1) DrawRect(new Rect2(0, y, w, LaneH), new Color(1, 1, 1, 0.025f));
            if (sound && row == Project.Lanes.Count) DrawLine(new Vector2(0, y), new Vector2(w, y), UiTheme.Hairline);
            string label = sound
                ? (row - Project.Lanes.Count < _soundLabels.Count ? _soundLabels[row - Project.Lanes.Count] : Project.AudioLanes[row - Project.Lanes.Count])
                : Project.Lanes[row].Label;
            var color = sound || _stage.Puppet(row) != null ? UiTheme.Text : UiTheme.TextDim;
            DrawString(font, new Vector2(10, y + LaneH * 0.62f), label, HorizontalAlignment.Left, Labels - 16, UiTheme.FontSmall, color);
        }

        foreach (var c in Project.Clips)
        {
            float x0 = Math.Max(X(c.Start), Labels), x1 = Math.Min(X(c.End), w);
            if (x1 < Labels || x0 > w) continue;
            var r = new Rect2(x0, Ruler + Row(c) * LaneH + 4, Math.Max(2, x1 - x0), LaneH - 8);
            bool sel = c.Id == Selected;
            var fill = !c.Audio ? ActorClip : Project.Audio[c.Track].Game ? GameSoundClip : SoundClip;
            DrawRect(r, sel ? new Color(UiTheme.Amber, 0.85f) : fill);
            if (c.Audio) DrawWave(c, r);
            DrawRect(r, sel ? Colors.White : new Color(1, 1, 1, 0.25f), filled: false, width: sel ? 2 : 1);
            if (r.Size.X > 40)
                DrawString(font, r.Position + new Vector2(6, r.Size.Y * 0.68f), Label((int)Math.Round(c.Length)),
                    HorizontalAlignment.Left, r.Size.X - 10, UiTheme.FontTiny, sel ? Colors.Black : UiTheme.Text);
        }

        // the music's beats: faint ghosts across every lane, so an actor's clip can be lined up on them
        foreach (var (t, down) in _beats)
        {
            float x = X(t);
            if (x < Labels) continue;
            if (x > w) break;
            DrawLine(new Vector2(x, Ruler - 6), new Vector2(x, h), down ? GhostDown : Ghost, down ? 2 : 1);
        }
        // the markers kept: solid, a flag on the ruler
        foreach (double t in Project.Markers)
        {
            float x = X(t);
            if (x < Labels || x > w) continue;
            DrawLine(new Vector2(x, 0), new Vector2(x, h), Marker, 2);
            DrawColoredPolygon(new[] { new Vector2(x, 2), new Vector2(x + 9, 7), new Vector2(x, 12) }, Marker);
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

    /// <summary>The sound's waveform inside its clip's box, one line every two pixels.</summary>
    private void DrawWave(Clip c, Rect2 r)
    {
        var peaks = Project.Audio[c.Track].Peaks;
        if (peaks.Length == 0) return;
        float mid = r.Position.Y + r.Size.Y * 0.5f, half = r.Size.Y * 0.45f;
        for (float x = r.Position.X; x < r.End.X; x += 2)
        {
            double source = c.In + (T(x) - c.Start);
            int i = (int)(source * AudioAsset.PeaksPerSecond);
            if (i < 0 || i >= peaks.Length) continue;
            float a = peaks[i] / 255f * half;
            DrawLine(new Vector2(x, mid - a), new Vector2(x, mid + a), Wave);
        }
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
        int row = (int)((at.Y - Ruler) / LaneH);
        // the topmost (latest-starting) clip under the pointer, as ActiveClip plays it
        Clip? hit = null;
        foreach (var c in Project.Clips)
            if (Row(c) == row && at.X >= X(c.Start) - Edge && at.X <= X(c.End) + Edge && (hit == null || c.Start >= hit.Start)) hit = c;
        if (hit == null) return (null, Drag.None);
        if (Math.Abs(at.X - X(hit.Start)) <= Edge) return (hit, Drag.TrimStart);
        if (Math.Abs(at.X - X(hit.End)) <= Edge) return (hit, Drag.TrimEnd);
        return (hit, Drag.Move);
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true } d:
                // a ghost beat becomes a marker, a marker goes
                ToggleMarkerAt(T(d.Position.X), HitPx / _pxPerSec, ghostsOnly: true);
                AcceptEvent();
                return;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                if (!b.Pressed) { _drag = Drag.None; _stage.Scrubbing = false; AcceptEvent(); return; }
                GrabFocus();
                var (clip, part) = HitClip(b.Position);
                if (clip == null || b.Position.Y < Ruler)
                {
                    _drag = Drag.Scrub;
                    _stage.Playing = false;
                    _stage.Scrubbing = true;
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
                bool snap = !m.ShiftPressed;
                double tolerance = SnapPx / _pxPerSec;
                switch (_drag)
                {
                    case Drag.Scrub: _stage.Seek(t); break;
                    case Drag.Move: Project.Move(_dragClip, snap ? SnapMove(t - _grabOffset, tolerance) : t - _grabOffset); break;
                    case Drag.TrimStart: Project.TrimStart(_dragClip, snap ? Project.Snap(t, tolerance, _dragClip) : t); break;
                    case Drag.TrimEnd: Project.TrimEnd(_dragClip, snap ? Project.Snap(t, tolerance, _dragClip) : t); break;
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

        // focused, a pad's (or the arrows') left and right step a frame
        if (e.IsActionPressed("ui_left", allowEcho: true)) { Step(-1); AcceptEvent(); }
        else if (e.IsActionPressed("ui_right", allowEcho: true)) { Step(1); AcceptEvent(); }
    }

    /// <summary>A moved clip's start, snapped by its start or else by its end.</summary>
    private double SnapMove(double start, double tolerance)
    {
        if (Project.Find(_dragClip) is not { } c) return start;
        double s = Project.Snap(start, tolerance, c.Id);
        if (s != start) return s;
        double e = Project.Snap(start + c.Length, tolerance, c.Id);
        return e - c.Length;
    }

    /// <summary>
    /// A marker near <paramref name="t"/> goes; otherwise the ghost beat near it becomes one (or,
    /// unless <paramref name="ghostsOnly"/>, <paramref name="t"/> itself when no beat is near). Whether anything changed.
    /// </summary>
    public bool ToggleMarkerAt(double t, double tolerance, bool ghostsOnly)
    {
        bool changed = Project.RemoveMarker(t, tolerance);
        if (!changed && (Project.NearestBeat(t, tolerance) ?? (ghostsOnly ? null : t)) is { } at) changed = Project.AddMarker(at);
        if (changed) QueueRedraw();
        return changed;
    }

    /// <summary>Forget the cached beats: after a project is swapped or a sound imported.</summary>
    public void Resync() => _soundKey = double.NaN;

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
