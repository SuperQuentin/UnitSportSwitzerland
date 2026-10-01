using System.Globalization;
using System.Linq;
using System.Text;
using Godot;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// On-screen FPS counter and, in its detailed mode, the frame-time spread, render counters and
/// what the tile loader is doing. F3 cycles Off -> FPS -> Detailed; the choice is a saved setting.
///
/// <para>
/// The text is rebuilt four times a second, not every frame: formatting a dozen lines per frame
/// would itself show up in the numbers it reports. Frame times are kept per frame, though, so
/// the p99 and max cover every frame of the last two seconds rather than a sample of them.
/// </para>
///
/// <para>
/// "ground" is build start to the first surface on screen, "complete" to the last result
/// committed (roads, buildings, trees). Both are measured on the main thread, so they include
/// time spent waiting in the commit queue — which is what a player actually waits for.
/// </para>
/// </summary>
public partial class PerfOverlay : CanvasLayer
{
    private const double RefreshSeconds = 0.25;
    private const double WindowSeconds = 2.0;

    private readonly ChunkManager _chunks;
    private readonly CachingChunkSource? _cache;
    private readonly PerfRecorder? _recorder;

    private readonly double[] _frames = new double[4096];
    private readonly double[] _scratch = new double[4096];
    private int _frameNext, _frameCount;

    private PanelContainer _panel = null!;
    private Label _label = null!;
    private double _sinceRefresh = RefreshSeconds;
    private int _lastCompleted;
    private double _buildsPerSecond;

    public PerfOverlay(ChunkManager chunks, CachingChunkSource? cache, PerfRecorder? recorder = null)
    {
        _chunks = chunks;
        _cache = cache;
        _recorder = recorder;
        Name = "PerfOverlay";
        Layer = 45;   // over the menus, so settings changes can be watched; under export progress
    }

    public override void _Ready()
    {
        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.72f),
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4,
            CornerRadiusBottomLeft = 4,
        });
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _panel.GrowHorizontal = Control.GrowDirection.Begin;

        _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeFontOverride("font", new SystemFont { FontNames = new[] { "Consolas", "Courier New", "monospace" } });
        _label.AddThemeFontSizeOverride("font_size", 12);
        _panel.AddChild(_label);
        AddChild(_panel);

        PerfRecorder.EnableRenderTiming(GetViewport());
        GameSettings.Changed += ApplyVisibility;
        ApplyVisibility();
    }

    /// <summary>A recording, or the path it was just saved to, shows even with the overlay off.</summary>
    private string RecorderLine()
    {
        if (_recorder == null) return "";
        if (_recorder.Recording)
        {
            int t = (int)_recorder.RecordingSeconds;
            return $"\u25CF REC {t / 60:D2}:{t % 60:D2}  (F4 to stop)";
        }
        return _recorder.SinceSaved < 8 ? $"log saved: {_recorder.LastSaved}" : "";
    }

    public override void _ExitTree() => GameSettings.Changed -= ApplyVisibility;

    private void ApplyVisibility() => Visible = GameSettings.Current.PerfOverlay != PerfOverlayMode.Off
        || RecorderLine() != "";

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F3 }) return;
        if (UiFocus.TextEntryActive) return;

        var s = GameSettings.Current;
        s.PerfOverlay = (PerfOverlayMode)(((int)s.PerfOverlay + 1) % 3);
        s.Commit();
        _sinceRefresh = RefreshSeconds; // show the new mode now, not a quarter second later
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        _frames[_frameNext] = delta;
        _frameNext = (_frameNext + 1) % _frames.Length;
        _frameCount = Math.Min(_frameCount + 1, _frames.Length);

        // a video export captures the whole viewport; the overlay must not end up in the film
        _panel.Visible = !_chunks.OfflineMode;

        ApplyVisibility();
        _sinceRefresh += delta;
        if (!Visible || _sinceRefresh < RefreshSeconds) return;

        double elapsed = _sinceRefresh;
        _sinceRefresh = 0;
        Refresh(elapsed);
    }

    private void Refresh(double elapsed)
    {
        // frames in the last WindowSeconds, newest first
        int n = 0;
        double sum = 0, max = 0;
        int over33 = 0;
        for (int i = 0; i < _frameCount && sum < WindowSeconds; i++)
        {
            double dt = _frames[(_frameNext - 1 - i + _frames.Length) % _frames.Length];
            _scratch[n++] = dt;
            sum += dt;
            max = Math.Max(max, dt);
            if (dt > 1.0 / 30) over33++;
        }
        if (n == 0) return;
        double avgMs = sum / n * 1000;
        double fps = Engine.GetFramesPerSecond();

        _label.AddThemeColorOverride("font_color",
            avgMs <= 17.5 ? new Color(0.55f, 0.95f, 0.55f)
            : avgMs <= 34 ? new Color(1f, 0.85f, 0.35f)
            : new Color(1f, 0.45f, 0.4f));

        var ci = CultureInfo.InvariantCulture;
        string rec = RecorderLine();
        if (GameSettings.Current.PerfOverlay == PerfOverlayMode.Off)
        {
            _label.Text = rec;
            return;
        }
        if (GameSettings.Current.PerfOverlay == PerfOverlayMode.Fps)
        {
            _label.Text = string.Format(ci, "{0,4:F0} fps  {1,5:F1} ms", fps, avgMs) + (rec == "" ? "" : "\n" + rec);
            return;
        }

        Array.Sort(_scratch, 0, n);
        double p99 = _scratch[Math.Min(n - 1, (int)(0.99 * n))] * 1000;

        var stats = _chunks.GetPerfStats();
        _buildsPerSecond = (stats.Completed - _lastCompleted) / Math.Max(elapsed, 1e-3);
        _lastCompleted = stats.Completed;

        static double Mon(Performance.Monitor m) => Performance.GetMonitor(m);
        static string Ms(double v) => double.IsNaN(v) ? "   -" : v.ToString("F0", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendFormat(ci, "{0,4:F0} fps  {1,5:F1} ms avg\n", fps, avgMs);
        sb.AppendFormat(ci, "frame  p99 {0:F1}  max {1:F1} ms  >33ms {2}\n", p99, max * 1000, over33);
        var (gpuMs, renderCpuMs) = PerfRecorder.RenderTimes(GetViewport());
        sb.AppendFormat(ci, "gpu    {0:F1} ms  render cpu {1:F1} ms\n", gpuMs, renderCpuMs);
        sb.AppendFormat(ci, "cpu    process {0:F1}  physics {1:F1} ms\n",
            Mon(Performance.Monitor.TimeProcess) * 1000, Mon(Performance.Monitor.TimePhysicsProcess) * 1000);
        sb.AppendFormat(ci, "render {0:F0} draws  {1:F2} M prims\n",
            Mon(Performance.Monitor.RenderTotalDrawCallsInFrame),
            Mon(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1e6);
        sb.AppendFormat(ci, "memory {0:F0} MB static  {1:F0} MB video\n",
            Mon(Performance.Monitor.MemoryStatic) / 1048576, Mon(Performance.Monitor.RenderVideoMemUsed) / 1048576);
        if (OriginShifter.Instance is { } shifter && _chunks.Origin is { } origin)
        {
            // the floating origin (#185): how far out the camera is, and float32's step there
            var cam = GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
            float away = new Vector2(cam.X, cam.Z).Length();
            float step = away < 1f ? 0f : MathF.Pow(2f, MathF.Floor(MathF.Log2(away)) - 23f);
            sb.AppendFormat(ci, "origin {0:F0}/{1:F0}  cam {2:F0} m out ({3:F2} mm)  shifts {4} ({5:F1} ms)\n",
                origin.E, origin.N, away, step * 1000f, shifter.ShiftCount, shifter.LastShiftMs);
        }

        sb.Append("-- tiles --\n");
        sb.AppendFormat(ci, "loaded {0}/{1}  horizon blocks {2}\n", stats.Loaded, stats.Desired, stats.HorizonBlocks);
        sb.AppendFormat(ci, "build  {0} in flight  {1} pending  {2} ready\n",
            stats.InFlight, stats.Pending, stats.ReadyQueue);
        sb.AppendFormat(ci, "done   {0} ({1:F1}/s)  cancelled {2}\n", stats.Completed, _buildsPerSecond, stats.Cancelled);
        sb.AppendFormat(ci, "ground   p50 {0} p95 {1} ms\n", Ms(stats.GroundP50), Ms(stats.GroundP95));
        sb.AppendFormat(ci, "complete p50 {0} p95 {1} ms\n", Ms(stats.CompleteP50), Ms(stats.CompleteP95));
        sb.AppendFormat(ci, "commit {0:F1} ms last frame ({1} results)\n", stats.LastCommitMs, stats.LastCommits);
        sb.AppendFormat(ci, "reads  {0} full  {1} coarse", stats.FullLoads, stats.CoarseLoads);
        if (_cache != null) sb.AppendFormat(ci, "  cache {0:P0}", _cache.HitRate);
        sb.Append('\n');

        if (stats.Completed > 0)
        {
            sb.Append("-- worker ms / tile --\n");
            foreach (var (name, ms) in stats.StageAvgMs.OrderByDescending(x => x.Ms).Take(4))
                sb.AppendFormat(ci, "{0,-12}{1,7:F1}\n", name, ms);
        }

        if (rec != "") sb.Append(rec).Append('\n');
        _label.Text = sb.ToString().TrimEnd('\n');
    }
}
