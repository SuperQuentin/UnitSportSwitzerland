using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Movie;

/// <summary>
/// The studio's timeline (#638): a ruler, the camera's keyframes (#669), one lane per actor, the
/// sound lanes under them (#656), the clips, the beats and markers, and the playhead.
/// <list type="bullet">
/// <item>Click the ruler or an empty spot to seek, drag to scrub. Drag a clip to move it, its edges to
/// trim it, Ctrl+click to cut it there; all snap to markers, beats and other clips' edges (Shift: free).</item>
/// <item>The wheel zooms about the pointer, down to single frames; Shift+wheel scrolls (#669).</item>
/// <item>The music's beats are faint ghost markers: double-click one to keep it, double-click a marker to drop it.</item>
/// <item>Camera keys: click to pick (the playhead goes there), drag to retime, right-click for their options.</item>
/// <item>Focused (pad): left / right step a frame, LB / RB zoom.</item>
/// </list>
/// </summary>
public partial class TimelineView : Control
{
    private const float Ruler = 24, LaneH = 34, Labels = 120, Edge = 7, SnapPx = 8, HitPx = 6, KeyPx = 8;
    public const float MinZoom = 0.5f, MaxZoom = 2000f;

    private static readonly Color ActorClip = new(0.35f, 0.47f, 0.62f, 0.85f);
    private static readonly Color SoundClip = new(0.18f, 0.5f, 0.46f, 0.85f);
    private static readonly Color GameSoundClip = new(0.42f, 0.4f, 0.55f, 0.85f);
    private static readonly Color Wave = new(1, 1, 1, 0.45f);
    private static readonly Color Ghost = new(1, 1, 1, 0.13f), GhostDown = new(1, 1, 1, 0.28f);
    private static readonly Color Marker = new(1f, 0.45f, 0.25f, 0.95f);
    private static readonly Color CameraPath = new(0.55f, 0.8f, 1f, 0.8f), CameraKeyColor = new(0.7f, 0.88f, 1f);
    private static readonly StringName ZoomIn = PlayerInput.MapZoomIn, ZoomOut = PlayerInput.MapZoomOut;

    private readonly MovieStage _stage;
    private MovieProject Project => _stage.Project;
    private float _pxPerSec = 12;
    private double _left;   // the time at the left edge of the clip area
    private Drag _drag;
    private (double, double, float, int, int, bool, Vector2, double, int, int, CameraKey?) _drawn;
    private int _dragClip;
    private CameraKey? _dragKey;
    private double _grabOffset;

    // the ghost beats and the sound lanes' names, worked out again only when a sound clip changes
    private List<(double T, bool Downbeat)> _beats = new();
    private readonly List<string> _soundLabels = new();
    private double _soundKey = double.NaN;

    /// <summary>The selected clip's id, 0 for none.</summary>
    public int Selected { get; private set; }
    public event Action? SelectionChanged;
    /// <summary>The selected camera key, or null (#669). Selecting one drops the clip selection, and back.</summary>
    public CameraKey? SelectedKey { get; private set; }
    public event Action? KeySelectionChanged;
    /// <summary>Right-click on a camera key: the studio shows its options there (screen position).</summary>
    public event Action<CameraKey, Vector2>? KeyMenuRequested;
    /// <summary>The zoom or the scroll changed: the scrollbar follows.</summary>
    public event Action? ViewChanged;

    private enum Drag { None, Scrub, Move, TrimStart, TrimEnd, Key }

    public TimelineView(MovieStage stage)
    {
        _stage = stage;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        CustomMinimumSize = new Vector2(0, Ruler + LaneH * 4);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    private float Width0 => Size.X - Labels;
    private float X(double t) => Labels + (float)((t - _left) * _pxPerSec);
    private double T(float x) => _left + (x - Labels) / _pxPerSec;
    // row 0 is the camera; then the actors; then the sound
    private int Rows => 1 + Project.Lanes.Count + Project.AudioLanes.Count;
    private int Row(Clip c) => 1 + (c.Audio ? Project.Lanes.Count + c.Lane : c.Lane);
    private static float RowY(int row) => Ruler + row * LaneH;

    /// <summary>The time at the left edge, for the scrollbar.</summary>
    public double Left
    {
        get => _left;
        set { _left = Math.Clamp(value, 0, Math.Max(0, Project.Duration + 5 - VisibleSeconds * 0.5)); ViewChanged?.Invoke(); }
    }

    public double VisibleSeconds => Math.Max(1, Width0) / _pxPerSec;

    /// <summary>Fits the whole movie in view.</summary>
    public void Fit()
    {
        double d = Math.Max(Project.Duration, 10);
        _pxPerSec = Math.Clamp((float)(Math.Max(Width0, 200) / (d * 1.05)), MinZoom, MaxZoom);
        _left = 0;
        _soundKey = double.NaN;
        QueueRedraw();
        ViewChanged?.Invoke();
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping <paramref name="about"/> (the playhead by default) where it is on screen.</summary>
    public void ZoomBy(float factor, double? about = null)
    {
        double at = about ?? _stage.Time;
        float x = X(at);
        if (x < Labels || x > Size.X) x = Labels + Width0 * 0.5f;   // the playhead off screen: zoom about the middle
        _pxPerSec = Math.Clamp(_pxPerSec * factor, MinZoom, MaxZoom);
        _left = Math.Max(0, at - (x - Labels) / _pxPerSec);
        ViewChanged?.Invoke();
    }

    public override void _Process(double delta)
    {
        float wanted = Ruler + LaneH * Math.Max(4, Rows);
        if (!Mathf.IsEqualApprox(CustomMinimumSize.Y, wanted)) CustomMinimumSize = new Vector2(0, wanted);
        // playing off screen: the view follows the playhead
        if (_stage.Playing && _drag == Drag.None && Width0 > 0)
        {
            double right = T(Size.X);
            if (_stage.Time > right || _stage.Time < _left) { _left = Math.Max(0, _stage.Time - (right - _left) * 0.1); ViewChanged?.Invoke(); }
        }
        // focused, a pad's shoulders zoom the timeline (the studio camera leaves them alone then)
        if (HasFocus())
        {
            if (Input.IsActionPressed(ZoomIn)) ZoomBy(Mathf.Exp(2f * (float)delta));
            if (Input.IsActionPressed(ZoomOut)) ZoomBy(Mathf.Exp(-2f * (float)delta));
        }
        double soundKey = SoundKey();
        if (soundKey != _soundKey) { _soundKey = soundKey; Resound(); }
        // drawn again only when something it shows moved
        var drawn = (_stage.Time, _left, _pxPerSec, Project.Clips.Count, Selected, HasFocus(), Size, soundKey,
            Project.Markers.Count, Project.Camera.Keys.Count, SelectedKey);
        if (drawn != _drawn) { _drawn = drawn; QueueRedraw(); }
    }

    /// <summary>A number that changes whenever any sound clip is added, cut, moved or trimmed. No allocation.</summary>
    private double SoundKey()
    {
        double k = Project.AudioLanes.Count;
        foreach (var c in Project.Clips)
            if (c.Audio)
                // the beat count too: a game sound's beat arrives from a worker thread after the grab
                k = k * 1.000123 + c.Id * 0.731 + c.Track * 1.37 + c.In * 3.1 + c.Out * 5.3 + c.Start * 7.7
                    + Project.Audio[c.Track].Beat.Beats.Length * 0.917;
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

        // ruler: a tick step that keeps labels ~80 px apart, tenths of a second when zoomed in
        double step = TickStep(80 / _pxPerSec);
        double first = Math.Floor(_left / step) * step;
        for (int n = 0; ; n++)
        {
            double t = first + n * step;
            float x = X(t);
            if (x >= w) break;
            if (t < -1e-9 || x < Labels) continue;
            DrawLine(new Vector2(x, Ruler - 8), new Vector2(x, h), UiTheme.Hairline);
            DrawString(font, new Vector2(x + 3, Ruler - 8), Label((int)Math.Round(t * 10), step < 1),
                HorizontalAlignment.Left, -1, UiTheme.FontTiny, UiTheme.TextFaint);
        }

        for (int row = 0; row < Rows; row++)
        {
            float y = RowY(row);
            int actorLanes = Project.Lanes.Count;
            bool sound = row > actorLanes;
            if (row % 2 == 1) DrawRect(new Rect2(0, y, w, LaneH), new Color(1, 1, 1, 0.025f));
            if (row == 1 || (sound && row == actorLanes + 1)) DrawLine(new Vector2(0, y), new Vector2(w, y), UiTheme.Hairline);
            string label = row == 0 ? "Camera"
                : !sound ? Project.Lanes[row - 1].Label
                : row - 1 - actorLanes < _soundLabels.Count ? _soundLabels[row - 1 - actorLanes] : Project.AudioLanes[row - 1 - actorLanes];
            var color = row == 0 ? CameraKeyColor : sound || _stage.Puppet(row - 1) != null ? UiTheme.Text : UiTheme.TextDim;
            DrawString(font, new Vector2(10, y + LaneH * 0.62f), label, HorizontalAlignment.Left, Labels - 16, UiTheme.FontSmall, color);
        }

        foreach (var c in Project.Clips)
        {
            float x0 = Math.Max(X(c.Start), Labels), x1 = Math.Min(X(c.End), w);
            if (x1 < Labels || x0 > w) continue;
            var r = new Rect2(x0, RowY(Row(c)) + 4, Math.Max(2, x1 - x0), LaneH - 8);
            bool sel = c.Id == Selected;
            var fill = !c.Audio ? ActorClip : Project.Audio[c.Track].Game ? GameSoundClip : SoundClip;
            DrawRect(r, sel ? new Color(UiTheme.Amber, 0.85f) : fill);
            if (c.Audio) DrawWave(c, r);
            DrawRect(r, sel ? Colors.White : new Color(1, 1, 1, 0.25f), filled: false, width: sel ? 2 : 1);
            if (r.Size.X > 40)
                DrawString(font, r.Position + new Vector2(6, r.Size.Y * 0.68f), Label((int)Math.Round(c.Length) * 10, false),
                    HorizontalAlignment.Left, r.Size.X - 10, UiTheme.FontTiny, sel ? Colors.Black : UiTheme.Text);
        }

        DrawCamera(w);

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

    /// <summary>
    /// The camera row: the path between keys (solid: smooth, thin: linear, dashed and a step:
    /// a cut) and each key as a diamond, ringed when it aims at an actor.
    /// </summary>
    private void DrawCamera(float w)
    {
        var keys = Project.Camera.Keys;
        float mid = RowY(0) + LaneH * 0.5f;
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            float a = Math.Max(X(keys[i].T), Labels), b = Math.Min(X(keys[i + 1].T), w);
            if (b < Labels || a > w) continue;
            switch (keys[i].Ease)
            {
                case KeyEase.Smooth: DrawLine(new Vector2(a, mid), new Vector2(b, mid), CameraPath, 3); break;
                case KeyEase.Linear: DrawLine(new Vector2(a, mid), new Vector2(b, mid), CameraPath, 1); break;
                case KeyEase.Cut:
                    DrawDashedLine(new Vector2(a, mid), new Vector2(b, mid), CameraPath, 1, 4);
                    DrawLine(new Vector2(b - 1, mid - 8), new Vector2(b - 1, mid + 8), CameraPath, 2);
                    break;
            }
        }
        foreach (var k in keys)
        {
            float x = X(k.T);
            if (x < Labels - KeyPx || x > w + KeyPx) continue;
            bool sel = k == SelectedKey;
            var color = sel ? UiTheme.Amber : CameraKeyColor;
            float s = sel ? 7 : 6;
            DrawColoredPolygon(new[] { new Vector2(x, mid - s), new Vector2(x + s, mid), new Vector2(x, mid + s), new Vector2(x - s, mid) }, color);
            if (k.LookAt >= 0) DrawArc(new Vector2(x, mid), s + 3, 0, Mathf.Tau, 16, color, 1.5f);
        }
    }

    /// <summary>The sound's waveform inside its clip's box, one line every two pixels.</summary>
    private void DrawWave(Clip c, Rect2 r)
    {
        var asset = Project.Audio[c.Track];
        var peaks = asset.Peaks;
        if (peaks.Length == 0) return;
        float mid = r.Position.Y + r.Size.Y * 0.5f, half = r.Size.Y * 0.45f / asset.PeakMax;
        for (float x = r.Position.X; x < r.End.X; x += 2)
        {
            double source = c.In + (T(x) - c.Start);
            int i = (int)(source * AudioAsset.PeaksPerSecond);
            if (i < 0 || i >= peaks.Length) continue;
            float a = peaks[i] * half;
            DrawLine(new Vector2(x, mid - a), new Vector2(x, mid + a), Wave);
        }
    }

    private static readonly double[] Steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200 };

    private static double TickStep(double atLeast)
    {
        foreach (double s in Steps)
            if (s >= atLeast) return s;
        return Steps[^1];
    }

    // "m:ss" (or "m:ss.t" zoomed in) by tenth of a second, made once each: the ruler and the clips are drawn while playing
    private static readonly Dictionary<int, string> ClockLabels = new(), TenthLabels = new();
    private static string Label(int tenths, bool withTenths)
    {
        var cache = withTenths ? TenthLabels : ClockLabels;
        int key = withTenths ? tenths : (int)Math.Round(tenths / 10.0);
        if (!cache.TryGetValue(key, out var text))
            cache[key] = text = withTenths ? $"{MovieSession.Clock(Math.Floor(key / 10.0))}.{Math.Abs(key) % 10}" : MovieSession.Clock(key);
        return text;
    }

    private (Clip? Clip, Drag Part) HitClip(Vector2 at)
    {
        if (at.Y < Ruler + LaneH || at.X < Labels) return (null, Drag.None);
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

    /// <summary>The camera key under the pointer, on the camera row.</summary>
    private CameraKey? HitKey(Vector2 at)
    {
        if (at.Y < RowY(0) || at.Y > RowY(1) || at.X < Labels - KeyPx) return null;
        return Project.Camera.Near(T(at.X), KeyPx / _pxPerSec);
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

            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } r:
                if (HitKey(r.Position) is { } menuKey)
                {
                    SelectKey(menuKey);
                    KeyMenuRequested?.Invoke(menuKey, GetScreenTransform() * r.Position);
                    AcceptEvent();
                }
                return;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                if (!b.Pressed) { _drag = Drag.None; _dragKey = null; _stage.Scrubbing = false; AcceptEvent(); return; }
                GrabFocus();
                if (HitKey(b.Position) is { } key)
                {
                    SelectKey(key);
                    _stage.Playing = false;
                    _stage.Seek(key.T);
                    _drag = Drag.Key;
                    _dragKey = key;
                    AcceptEvent();
                    return;
                }
                var (clip, part) = HitClip(b.Position);
                if (clip != null && b.CtrlPressed)
                {
                    // the blade: cut the clip where it was clicked
                    double at = b.ShiftPressed ? T(b.Position.X) : Project.Snap(T(b.Position.X), SnapPx / _pxPerSec, clip.Id);
                    if (Project.Split(clip.Id, at) is { } right) Select(right.Id);
                    _stage.Apply();
                    QueueRedraw();
                }
                else if (clip == null || b.Position.Y < Ruler)
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
                    case Drag.Key when _dragKey != null:
                        Project.Camera.Retime(_dragKey, snap ? Project.Snap(t, tolerance) : t);
                        _stage.Seek(_dragKey.T);
                        break;
                }
                if (_drag != Drag.Scrub) _stage.Apply();
                QueueRedraw();
                AcceptEvent();
                return;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                float dir = wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                if (wheel.ShiftPressed) Left = _left - dir * 60 / _pxPerSec;
                else ZoomBy(dir > 0 ? 1.25f : 0.8f, T(wheel.Position.X));   // about the pointer
                AcceptEvent();
                return;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelLeft or MouseButton.WheelRight } side:
                Left = _left + (side.ButtonIndex == MouseButton.WheelRight ? 1 : -1) * 60 / _pxPerSec;
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
        if (id != 0 && SelectedKey != null) SelectKey(null);
        if (Selected == id) return;
        Selected = id;
        SelectionChanged?.Invoke();
    }

    public void SelectKey(CameraKey? key)
    {
        if (key != null && Selected != 0) Select(0);
        if (SelectedKey == key) return;
        SelectedKey = key;
        QueueRedraw();
        KeySelectionChanged?.Invoke();
    }
}
