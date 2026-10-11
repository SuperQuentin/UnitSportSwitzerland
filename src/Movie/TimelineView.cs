using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Movie;

/// <summary>
/// The studio's timeline (#638), top to bottom: the ruler; the Program (#675), which camera the
/// movie shows when; one row of keyframes per camera (#669, #675); one lane per actor; the sound
/// lanes (#656). Over them the beats and markers and the playhead.
/// <list type="bullet">
/// <item>Click the ruler or an empty spot to seek, drag to scrub. Drag a clip to move it, its edges to
/// trim it, Ctrl+click to cut it there; all snap to markers, beats and other clips' edges (Shift: free).</item>
/// <item>The wheel zooms about the pointer, down to single frames; Shift+wheel scrolls.</item>
/// <item>The music's beats are faint ghost markers: double-click one to keep it, double-click a marker to drop it.</item>
/// <item>Camera keys and Program cuts: click to pick (the playhead goes there), drag to retime,
/// right-click for their options. Click a camera's name to pick that camera, right-click it to rename or delete it.</item>
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
    private static readonly StringName ZoomIn = PlayerInput.MapZoomIn, ZoomOut = PlayerInput.MapZoomOut;

    /// <summary>Each camera's colour, by index, on the timeline and its gizmo in the view.</summary>
    public static readonly Color[] CameraColors =
    {
        new(0.55f, 0.82f, 1f), new(1f, 0.62f, 0.85f), new(0.6f, 0.95f, 0.55f), new(1f, 0.78f, 0.4f),
        new(0.78f, 0.66f, 1f), new(0.45f, 0.95f, 0.88f), new(1f, 0.5f, 0.45f), new(0.92f, 0.92f, 0.6f),
    };
    public static Color CameraColor(int i) => CameraColors[((i % CameraColors.Length) + CameraColors.Length) % CameraColors.Length];

    private readonly MovieStage _stage;
    private MovieProject Project => _stage.Project;
    private float _pxPerSec = 12;
    private double _left;   // the time at the left edge of the clip area
    private Drag _drag;
    private (double, double, float, int, int, bool, Vector2, double, int, double, CameraKey?, CameraCut?, int) _drawn;
    private int _dragClip;
    private CameraKey? _dragKey;
    private CameraCut? _dragCut;
    private double _grabOffset;

    // the ghost beats and the sound lanes' names, worked out again only when a sound clip changes
    private List<(double T, bool Downbeat)> _beats = new();
    private readonly List<string> _soundLabels = new();
    private double _soundKey = double.NaN;

    /// <summary>The selected clip's id, 0 for none.</summary>
    public int Selected { get; private set; }
    public event Action? SelectionChanged;
    /// <summary>The selected camera key, or null (#669). Picking a key, a cut or a clip drops the other two.</summary>
    public CameraKey? SelectedKey { get; private set; }
    public event Action? KeySelectionChanged;
    /// <summary>The selected Program cut, or null (#675).</summary>
    public CameraCut? SelectedCut { get; private set; }
    /// <summary>The camera keys go to and the picker shows (#675).</summary>
    public int PickedCamera { get; private set; }
    public event Action? PickedCameraChanged;
    /// <summary>Right-click on a key, a cut or a camera's name: the studio shows their options there (screen position).</summary>
    public event Action<CameraKey, Vector2>? KeyMenuRequested;
    public event Action<CameraCut, Vector2>? CutMenuRequested;
    public event Action<int, Vector2>? CameraMenuRequested;
    /// <summary>The zoom or the scroll changed: the scrollbar follows.</summary>
    public event Action? ViewChanged;

    private enum Drag { None, Scrub, Move, TrimStart, TrimEnd, Key, Cut }

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
    // row 0 the program, then a row per camera, then the actors, then the sound
    private int CameraRows => Project.Cameras.Count;
    private int FirstActorRow => 1 + CameraRows;
    private int Rows => FirstActorRow + Project.Lanes.Count + Project.AudioLanes.Count;
    private int Row(Clip c) => FirstActorRow + (c.Audio ? Project.Lanes.Count + c.Lane : c.Lane);
    private static float RowY(int row) => Ruler + row * LaneH;
    private static int RowAt(float y) => y < Ruler ? -1 : (int)((y - Ruler) / LaneH);

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
            Project.Markers.Count, CameraShape(), SelectedKey, SelectedCut, PickedCamera);
        if (drawn != _drawn) { _drawn = drawn; QueueRedraw(); }
    }

    /// <summary>A number that changes when any camera key or cut is added, moved or dropped. No allocation.</summary>
    private double CameraShape()
    {
        double k = Project.Cameras.Count;
        foreach (var cam in Project.Cameras)
            foreach (var key in cam.Keys) k = k * 1.000173 + key.T * 3.7 + (int)key.Ease * 0.31 + key.LookAt * 0.17;
        foreach (var cut in Project.Cuts) k = k * 1.000231 + cut.T * 5.3 + cut.Camera * 0.73;
        return k;
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

        int actorLanes = Project.Lanes.Count;
        for (int row = 0; row < Rows; row++)
        {
            float y = RowY(row);
            int cam = row - 1;
            bool camera = row >= 1 && row <= CameraRows, sound = row >= FirstActorRow + actorLanes;
            if (camera && cam == PickedCamera) DrawRect(new Rect2(0, y, w, LaneH), new Color(CameraColor(cam), 0.1f));
            else if (row % 2 == 1) DrawRect(new Rect2(0, y, w, LaneH), new Color(1, 1, 1, 0.025f));
            if (row == 1 || row == FirstActorRow || (sound && row == FirstActorRow + actorLanes))
                DrawLine(new Vector2(0, y), new Vector2(w, y), UiTheme.Hairline);
            string label = row == 0 ? "Program"
                : camera ? Project.Cameras[cam].Name
                : !sound ? Project.Lanes[row - FirstActorRow].Label
                : row - FirstActorRow - actorLanes < _soundLabels.Count ? _soundLabels[row - FirstActorRow - actorLanes]
                : Project.AudioLanes[row - FirstActorRow - actorLanes];
            var color = row == 0 ? UiTheme.Amber : camera ? CameraColor(cam)
                : sound || _stage.Puppet(row - FirstActorRow) != null ? UiTheme.Text : UiTheme.TextDim;
            if (camera && cam == PickedCamera) DrawString(font, new Vector2(2, y + LaneH * 0.62f), "▸", HorizontalAlignment.Left, -1, UiTheme.FontSmall, color);
            DrawString(font, new Vector2(12, y + LaneH * 0.62f), label, HorizontalAlignment.Left, Labels - 18, UiTheme.FontSmall, color);
        }

        DrawProgram(w);
        for (int cam = 0; cam < CameraRows; cam++) DrawCamera(cam, w);

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

    /// <summary>The Program row: each stretch in the colour of the camera it shows, its name in it, a tick at every cut.</summary>
    private void DrawProgram(float w)
    {
        var cuts = Project.Cuts;
        float top = RowY(0) + 5, height = LaneH - 10;
        double end = Math.Max(Project.Duration, T(w));
        for (int i = -1; i < cuts.Count; i++)
        {
            double from = i < 0 ? 0 : cuts[i].T, to = i + 1 < cuts.Count ? cuts[i + 1].T : end;
            int cam = i < 0 ? 0 : Math.Clamp(cuts[i].Camera, 0, CameraRows - 1);
            float a = Math.Max(X(from), Labels), b = Math.Min(X(to), w);
            if (b <= a) continue;
            DrawRect(new Rect2(a, top, b - a, height), new Color(CameraColor(cam), cuts.Count == 0 ? 0.25f : 0.5f));
            if (b - a > 50)
                DrawString(UiTheme.Font, new Vector2(a + 6, top + height * 0.72f), Project.Cameras[cam].Name,
                    HorizontalAlignment.Left, b - a - 10, UiTheme.FontTiny, Colors.Black);
        }
        foreach (var cut in cuts)
        {
            float x = X(cut.T);
            if (x < Labels || x > w) continue;
            bool sel = cut == SelectedCut;
            DrawLine(new Vector2(x, top - 3), new Vector2(x, top + height + 3), sel ? UiTheme.Amber : Colors.White, sel ? 3 : 2);
        }
    }

    /// <summary>
    /// A camera's row: the path between keys (solid: smooth, thin: linear, dashed and a step:
    /// a cut) and each key as a diamond, ringed when it aims at an actor, in the camera's colour.
    /// </summary>
    private void DrawCamera(int cam, float w)
    {
        var keys = Project.Cameras[cam].Keys;
        var color = CameraColor(cam);
        var path = new Color(color, 0.8f);
        float mid = RowY(1 + cam) + LaneH * 0.5f;
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            float a = Math.Max(X(keys[i].T), Labels), b = Math.Min(X(keys[i + 1].T), w);
            if (b < Labels || a > w) continue;
            switch (keys[i].Ease)
            {
                case KeyEase.Smooth: DrawLine(new Vector2(a, mid), new Vector2(b, mid), path, 3); break;
                case KeyEase.Linear: DrawLine(new Vector2(a, mid), new Vector2(b, mid), path, 1); break;
                case KeyEase.Cut:
                    DrawDashedLine(new Vector2(a, mid), new Vector2(b, mid), path, 1, 4);
                    DrawLine(new Vector2(b - 1, mid - 8), new Vector2(b - 1, mid + 8), path, 2);
                    break;
            }
        }
        foreach (var k in keys)
        {
            float x = X(k.T);
            if (x < Labels - KeyPx || x > w + KeyPx) continue;
            bool sel = k == SelectedKey;
            var c = sel ? UiTheme.Amber : color;
            float s = sel ? 7 : 6;
            DrawColoredPolygon(new[] { new Vector2(x, mid - s), new Vector2(x + s, mid), new Vector2(x, mid + s), new Vector2(x - s, mid) }, c);
            if (k.LookAt >= 0) DrawArc(new Vector2(x, mid), s + 3, 0, Mathf.Tau, 16, c, 1.5f);
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
        int row = RowAt(at.Y);
        if (row < FirstActorRow || at.X < Labels) return (null, Drag.None);
        // the topmost (latest-starting) clip under the pointer, as ActiveClip plays it
        Clip? hit = null;
        foreach (var c in Project.Clips)
            if (Row(c) == row && at.X >= X(c.Start) - Edge && at.X <= X(c.End) + Edge && (hit == null || c.Start >= hit.Start)) hit = c;
        if (hit == null) return (null, Drag.None);
        if (Math.Abs(at.X - X(hit.Start)) <= Edge) return (hit, Drag.TrimStart);
        if (Math.Abs(at.X - X(hit.End)) <= Edge) return (hit, Drag.TrimEnd);
        return (hit, Drag.Move);
    }

    /// <summary>The camera key under the pointer, on a camera's row.</summary>
    private CameraKey? HitKey(Vector2 at)
    {
        int row = RowAt(at.Y);
        if (row < 1 || row > CameraRows || at.X < Labels - KeyPx) return null;
        return Project.Cameras[row - 1].Near(T(at.X), KeyPx / _pxPerSec);
    }

    /// <summary>The Program cut under the pointer.</summary>
    private CameraCut? HitCut(Vector2 at) =>
        RowAt(at.Y) == 0 && at.X >= Labels - KeyPx ? Project.CutNear(T(at.X), KeyPx / _pxPerSec) : null;

    /// <summary>The camera whose name is under the pointer, or -1.</summary>
    private int HitCameraName(Vector2 at)
    {
        int row = RowAt(at.Y);
        return at.X < Labels && row >= 1 && row <= CameraRows ? row - 1 : -1;
    }

    /// <summary>The camera a key belongs to (0 if, somehow, none).</summary>
    private int CameraOf(CameraKey key) => Math.Max(0, Project.Cameras.FindIndex(c => c.Keys.Contains(key)));

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
            {
                var screen = GetScreenTransform() * r.Position;
                int named = HitCameraName(r.Position);
                if (HitKey(r.Position) is { } menuKey) { SelectKey(menuKey); KeyMenuRequested?.Invoke(menuKey, screen); AcceptEvent(); }
                else if (HitCut(r.Position) is { } menuCut) { SelectCut(menuCut); CutMenuRequested?.Invoke(menuCut, screen); AcceptEvent(); }
                else if (named >= 0) { Pick(named); CameraMenuRequested?.Invoke(named, screen); AcceptEvent(); }
                return;
            }

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
            {
                if (!b.Pressed) { _drag = Drag.None; _dragKey = null; _dragCut = null; _stage.Scrubbing = false; AcceptEvent(); return; }
                GrabFocus();
                int named = HitCameraName(b.Position);
                if (named >= 0) { Pick(named); AcceptEvent(); return; }
                if (HitKey(b.Position) is { } key)
                {
                    SelectKey(key);
                    Pick(CameraOf(key));
                    _stage.Playing = false;
                    _stage.Seek(key.T);
                    _drag = Drag.Key;
                    _dragKey = key;
                    AcceptEvent();
                    return;
                }
                if (HitCut(b.Position) is { } cut)
                {
                    SelectCut(cut);
                    _stage.Playing = false;
                    _stage.Seek(cut.T);
                    _drag = Drag.Cut;
                    _dragCut = cut;
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
            }

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
                        Project.Cameras[CameraOf(_dragKey)].Retime(_dragKey, snap ? Project.Snap(t, tolerance) : t);
                        _stage.Seek(_dragKey.T);
                        break;
                    case Drag.Cut when _dragCut != null:
                        Project.RetimeCut(_dragCut, snap ? Project.Snap(t, tolerance) : t);
                        _stage.Seek(_dragCut.T);
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
        if (id != 0) { SelectKey(null); SelectCut(null); }
        if (Selected == id) return;
        Selected = id;
        SelectionChanged?.Invoke();
    }

    public void SelectKey(CameraKey? key)
    {
        if (key != null) { Select(0); SelectCut(null); }
        if (SelectedKey == key) return;
        SelectedKey = key;
        QueueRedraw();
        KeySelectionChanged?.Invoke();
    }

    public void SelectCut(CameraCut? cut)
    {
        if (cut != null) { Select(0); SelectKey(null); }
        SelectedCut = cut;
        QueueRedraw();
    }

    /// <summary>Picks the camera keys go to (clamped to the cameras there are).</summary>
    public void Pick(int camera)
    {
        camera = Math.Clamp(camera, 0, Project.Cameras.Count - 1);
        if (PickedCamera == camera) return;
        PickedCamera = camera;
        QueueRedraw();
        PickedCameraChanged?.Invoke();
    }
}
