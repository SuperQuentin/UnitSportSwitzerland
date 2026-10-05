using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Records a play session's performance to disk so a slow or stuttering run can be examined
/// afterwards instead of described from memory. F4 starts and stops it; <c>--perflog [seconds]</c>
/// starts it at boot (and stops it after that long, if given).
///
/// <para>
/// Each recording is a folder under <c>user://perf_logs/</c>:
/// <list type="bullet">
/// <item><c>frames.csv</c> — one row per frame: frame time, CPU/physics time, draws, primitives,
/// memory, GC collections, the loader's queues, what was committed, and where the camera was.</item>
/// <item><c>builds.csv</c> — one row per finished tile build, with its latency and the worker
/// milliseconds of every stage, so a slow tile can be told apart from a slow stage.</item>
/// <item><c>commits.csv</c> — every result the main thread committed and how long it took.</item>
/// <item><c>events.log</c> — hitches with the context that explains them, mode and settings
/// changes, teleports.</item>
/// <item><c>summary.txt</c> — the whole session boiled down, ending in a diagnosis that says
/// which of GPU, main thread, GC or tile loading is the problem.</item>
/// </list>
/// </para>
///
/// <para>
/// Frame attribution: the recorder runs last in the frame (<see cref="Node.ProcessPriority"/>),
/// and the delta Godot hands a frame measures the PREVIOUS frame's work plus its render. So a
/// row pairs this frame's delta with the commits and GC counted during the previous frame —
/// otherwise every commit hitch would be blamed on the frame after the one that caused it.
/// </para>
/// </summary>
public partial class PerfRecorder : Node, IOriginShiftAware
{
    public const double HitchMs = 33.4;
    private const string Root = "user://perf_logs";

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly Func<string> _mode;

    private StreamWriter? _frames, _builds, _commits, _events;
    private string _dir = "";
    private double _elapsed, _autoStop, _sinceFlush;
    private int _frameIndex;
    private readonly int[] _gcBase = new int[3];
    private readonly int[] _gcLast = new int[3];
    private readonly int[] _gcFrame = new int[3];
    private double _gpuSum, _renderCpuSum;
    private Vector3? _lastCam;

    /// <summary>The origin moved (#185): not a jump of the camera, so not a speed or a teleport.</summary>
    public void OnOriginShifted(OriginShift shift)
    {
        if (_lastCam is { } last) _lastCam = shift.Point(last);
    }
    private string _lastMode = "";

    // the previous frame's commits and GC, which this frame's delta paid for
    private int _pendingCommits, _prevCommits;
    private double _pendingCommitMs, _prevCommitMs, _pendingWorstCommit, _prevWorstCommit;
    private string _pendingWorstTile = "", _prevWorstTile = "";

    // kept in memory for the summary
    private readonly List<float> _frameMs = new();
    private readonly List<Hitch> _hitches = new();
    private readonly List<ChunkManager.BuildLog> _buildLogs = new();
    private readonly List<(TileId Id, int Stride, string Kind, double Ms)> _slowCommits = new();
    private double _cpuSum;
    private long _primSum, _drawSum;
    private int _saturatedFrames;
    private double _maxPrims;

    private readonly record struct Hitch(int Frame, double T, double Ms, int Commits, double CommitMs,
        string WorstTile, double WorstCommit, int Gc0, int Gc1, int Gc2, double GpuMs, int InFlight,
        string Where);

    public bool Recording => _frames != null;
    public double RecordingSeconds => _elapsed;

    /// <summary>Folder of the recording last finished, for the overlay to show.</summary>
    public string LastSaved { get; private set; } = "";
    public double SinceSaved { get; private set; } = double.MaxValue;

    public PerfRecorder(ChunkManager chunks, WorldOrigin origin, Func<string> mode)
    {
        _chunks = chunks;
        _origin = origin;
        _mode = mode;
        Name = "PerfRecorder";
        ProcessPriority = 10_000;   // after the chunk manager, so its commits are already counted
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        EnableRenderTiming(GetViewport());
        if (!CmdArgs.Has("--perflog")) return;
        if (CmdArgs.Double("--perflog") is double seconds) _autoStop = seconds;
        Start();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F4 }) return;
        if (UiFocus.TextEntryActive) return;
        if (Recording) Stop(); else Start();
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree() => Stop();

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) Stop();
    }

    public void Start()
    {
        if (Recording) return;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        _dir = Path.Combine(ProjectSettings.GlobalizePath(Root), stamp);
        Directory.CreateDirectory(_dir);

        _frames = Open("frames.csv",
            "frame,t_s,frame_ms,gpu_ms,render_cpu_ms,process_ms,physics_ms,fps,draws,prims,objects,mem_static_mb,vram_mb,"
            + "managed_mb,gc0,gc1,gc2,tiles_loaded,tiles_desired,in_flight,build_cap,pending,ready_queue,"
            + "commits,commit_ms,worst_commit_ms,worst_commit_tile,cam_e,cam_n,cam_alt,speed_mps,mode,"
            + "nodes,objects,resources,orphan_nodes,vram_tex_mb,vram_buf_mb");
        _builds = Open("builds.csv",
            "t_s,tile,stride,ring,ground_ms,complete_ms,collision,roads,buildings,"
            + string.Join(",", ChunkManager.StageNames.Select(n => "w_" + n.Replace('+', '_').Replace('-', '_'))));
        _commits = Open("commits.csv", "t_s,frame,tile,stride,kind,ms");
        _events = Open("events.log", null);

        for (int g = 0; g < 3; g++) _gcBase[g] = _gcLast[g] = GC.CollectionCount(g);
        _elapsed = 0;
        _frameIndex = 0;
        _lastCam = null;
        _lastMode = "";
        _frameMs.Clear(); _hitches.Clear(); _buildLogs.Clear(); _slowCommits.Clear();
        _cpuSum = _maxPrims = _gpuSum = _renderCpuSum = 0;
        _primSum = _drawSum = 0;
        _saturatedFrames = 0;
        _pendingCommits = _prevCommits = 0;
        _pendingCommitMs = _prevCommitMs = _pendingWorstCommit = _prevWorstCommit = 0;

        _chunks.BuildLogged += OnBuild;
        _chunks.CommitLogged += OnCommit;
        GameSettings.Changed += OnSettings;

        Event($"recording started  {SystemLine()}");
        Event($"settings {JsonSerializer.Serialize(GameSettings.Current)}");
        GD.Print($"[perflog] recording to {_dir}");
    }

    public void Stop()
    {
        if (!Recording) return;
        _chunks.BuildLogged -= OnBuild;
        _chunks.CommitLogged -= OnCommit;
        GameSettings.Changed -= OnSettings;

        Event("recording stopped");
        try { File.WriteAllText(Path.Combine(_dir, "summary.txt"), Summary()); }
        catch (Exception e) { GD.PushWarning($"[perflog] summary failed: {e.Message}"); }

        foreach (var w in new[] { _frames, _builds, _commits, _events }) w?.Dispose();
        _frames = _builds = _commits = _events = null;

        LastSaved = _dir;
        SinceSaved = 0;
        GD.Print($"[perflog] saved {_frameIndex} frames, {_buildLogs.Count} builds to {_dir}");
    }

    /// <summary>
    /// Turns on the viewport's GPU/CPU render timers. Needed because <c>TimeProcess</c> cannot
    /// tell the two apart: it absorbs the wait for the renderer, so a GPU-bound frame reads as a
    /// 50 ms process step. Costs a pair of timestamp queries per frame.
    /// </summary>
    public static void EnableRenderTiming(Viewport viewport) =>
        RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);

    /// <summary>(GPU ms, render CPU ms) of the viewport's last measured frame.</summary>
    public static (double Gpu, double Cpu) RenderTimes(Viewport viewport)
    {
        var rid = viewport.GetViewportRid();
        return (RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid),
            RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid) + RenderingServer.GetFrameSetupTimeCpu());
    }

    /// <summary>Opens the folder all recordings go into.</summary>
    public static void OpenLogsFolder()
    {
        string path = ProjectSettings.GlobalizePath(Root);
        Directory.CreateDirectory(path);
        OS.ShellOpen(path);
    }

    public override void _Process(double delta)
    {
        SinceSaved += delta;
        if (!Recording) return;

        _elapsed += delta;
        _frameIndex++;
        double ms = delta * 1000;

        static double Mon(Performance.Monitor m) => Performance.GetMonitor(m);
        double cpu = Mon(Performance.Monitor.TimeProcess) * 1000;
        var (gpuMs, renderCpuMs) = RenderTimes(GetViewport());
        double physics = Mon(Performance.Monitor.TimePhysicsProcess) * 1000;
        double draws = Mon(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double prims = Mon(Performance.Monitor.RenderTotalPrimitivesInFrame);

        var gc = _gcFrame;
        for (int g = 0; g < 3; g++) { int c = GC.CollectionCount(g); gc[g] = c - _gcLast[g]; _gcLast[g] = c; }

        var stats = _chunks.GetPerfStats();
        var cam = GetViewport().GetCamera3D();
        Vector3 pos = cam?.GlobalPosition ?? Vector3.Zero;
        double speed = _lastCam is { } last && delta > 0 ? pos.DistanceTo(last) / delta : 0;
        var (e, n) = _origin.ToLv95(pos);
        string mode = _mode();

        if (_lastCam is { } prev && pos.DistanceTo(prev) > 500)
            Event($"camera jumped {pos.DistanceTo(prev):F0} m to LV95 {e:F0},{n:F0} (teleport or mode change)");
        _lastCam = pos;
        if (mode != _lastMode) { Event($"mode {mode}"); _lastMode = mode; }

        var ci = CultureInfo.InvariantCulture;
        _frames!.WriteLine(string.Format(ci,
            "{0},{1:F3},{2:F2},{30:F2},{31:F2},{3:F2},{4:F2},{5:F0},{6:F0},{7:F0},{8:F0},{9:F0},{10:F0},{11:F0},{12},{13},{14},"
            + "{15},{16},{17},{18},{19},{20},{21},{22:F2},{23:F2},{24},{25:F0},{26:F0},{27:F0},{28:F1},{29},"
            + "{32:F0},{33:F0},{34:F0},{35:F0},{36:F0},{37:F0}",
            _frameIndex, _elapsed, ms, cpu, physics, Engine.GetFramesPerSecond(), draws, prims,
            Mon(Performance.Monitor.RenderTotalObjectsInFrame),
            Mon(Performance.Monitor.MemoryStatic) / 1048576, Mon(Performance.Monitor.RenderVideoMemUsed) / 1048576,
            GC.GetTotalMemory(false) / 1048576.0, gc[0], gc[1], gc[2],
            stats.Loaded, stats.Desired, stats.InFlight, _chunks.BuildCap, stats.Pending, stats.ReadyQueue,
            _prevCommits, _prevCommitMs, _prevWorstCommit, _prevWorstTile,
            e, n, pos.Y, speed, mode, gpuMs, renderCpuMs,
            Mon(Performance.Monitor.ObjectNodeCount), Mon(Performance.Monitor.ObjectCount),
            Mon(Performance.Monitor.ObjectResourceCount), Mon(Performance.Monitor.ObjectOrphanNodeCount),
            Mon(Performance.Monitor.RenderTextureMemUsed) / 1048576, Mon(Performance.Monitor.RenderBufferMemUsed) / 1048576));

        _frameMs.Add((float)ms);
        _cpuSum += cpu;
        _gpuSum += gpuMs;
        _renderCpuSum += renderCpuMs;
        _primSum += (long)prims;
        _drawSum += (long)draws;
        _maxPrims = Math.Max(_maxPrims, prims);
        if (stats.InFlight >= _chunks.BuildCap && stats.Pending > stats.InFlight) _saturatedFrames++;

        if (ms > HitchMs)
        {
            var h = new Hitch(_frameIndex, _elapsed, ms, _prevCommits, _prevCommitMs, _prevWorstTile,
                _prevWorstCommit, gc[0], gc[1], gc[2], gpuMs, stats.InFlight,
                string.Format(ci, "{0:F0},{1:F0} {2}", e, n, mode));
            _hitches.Add(h);
            Event(string.Format(ci,
                "HITCH frame {0} {1:F1} ms [{12}] | gpu {9:F1} ms | commits {2} ({3:F1} ms, worst {4} {5:F1} ms) | gc {6}/{7}/{8} | in flight {10} | at {11}",
                h.Frame, h.Ms, h.Commits, h.CommitMs, h.WorstTile == "" ? "-" : h.WorstTile, h.WorstCommit,
                h.Gc0, h.Gc1, h.Gc2, h.GpuMs, h.InFlight, h.Where, Cause(h)));
        }

        // shift: what was counted during this frame is paid for by the next delta
        (_prevCommits, _prevCommitMs, _prevWorstCommit, _prevWorstTile) =
            (_pendingCommits, _pendingCommitMs, _pendingWorstCommit, _pendingWorstTile);
        (_pendingCommits, _pendingCommitMs, _pendingWorstCommit, _pendingWorstTile) = (0, 0, 0, "");

        // flushed once a second so a crash still leaves most of the session on disk
        _sinceFlush += delta;
        if (_sinceFlush >= 1)
        {
            _sinceFlush = 0;
            _frames.Flush(); _builds?.Flush(); _commits?.Flush(); _events?.Flush();
        }

        if (_autoStop > 0 && _elapsed >= _autoStop) Stop();
    }

    private void OnCommit(TileId id, int stride, string kind, double ms)
    {
        _pendingCommits++;
        _pendingCommitMs += ms;
        if (ms > _pendingWorstCommit) { _pendingWorstCommit = ms; _pendingWorstTile = id.ToString(); }
        _commits?.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:F3},{1},{2},{3},{4},{5:F2}",
            _elapsed, _frameIndex + 1, id, stride, kind, ms));
        if (ms > 8) _slowCommits.Add((id, stride, kind, ms));
    }

    private void OnBuild(ChunkManager.BuildLog b)
    {
        _buildLogs.Add(b);
        var ci = CultureInfo.InvariantCulture;
        _builds?.WriteLine(string.Format(ci, "{0:F3},{1},{2},{3},{4},{5:F0},{6},{7},{8},{9}",
            _elapsed, b.Id, b.Stride, b.Dist == int.MaxValue ? -1 : b.Dist,
            double.IsNaN(b.GroundMs) ? "" : b.GroundMs.ToString("F0", ci), b.CompleteMs,
            b.Collision ? 1 : 0, b.Roads ? 1 : 0, b.Buildings ? 1 : 0, string.Join(",", b.StageMs)));
    }

    private void OnSettings() => Event($"settings changed {JsonSerializer.Serialize(GameSettings.Current)}");

    private StreamWriter Open(string name, string? header)
    {
        var w = new StreamWriter(Path.Combine(_dir, name), false, new UTF8Encoding(false), 1 << 16);
        if (header != null) w.WriteLine(header);
        return w;
    }

    private void Event(string text) =>
        _events?.WriteLine($"[{_elapsed.ToString("F3", CultureInfo.InvariantCulture),9}s] {text}");

    private static string SystemLine() =>
        $"{OS.GetName()} | {OS.GetProcessorName()} x{System.Environment.ProcessorCount} | "
        + $"{RenderingServer.GetVideoAdapterName()} | {RenderingServer.GetVideoAdapterApiVersion()} | "
        + $"Godot {Engine.GetVersionInfo()["string"]}";

    private static double Pct(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];

    private string Summary()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void L(string format, params object[] a) => sb.AppendLine(string.Format(ci, format, a));

        int frames = _frameMs.Count;
        L("UnitSport performance session  {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci));
        L("{0}", SystemLine());
        L("settings {0}", JsonSerializer.Serialize(GameSettings.Current));
        L("");
        if (frames == 0) { L("no frames recorded"); return sb.ToString(); }

        var ft = _frameMs.Select(f => (double)f).OrderBy(f => f).ToList();
        double avg = ft.Average();
        int over33 = ft.Count(f => f > 33.4), over50 = ft.Count(f => f > 50), over100 = ft.Count(f => f > 100);
        L("== FRAMES ==");
        L("duration {0:F1} s, {1} frames, {2:F1} fps average", _elapsed, frames, frames / Math.Max(_elapsed, 1e-6));
        L("frame ms  avg {0:F1}  p50 {1:F1}  p95 {2:F1}  p99 {3:F1}  max {4:F1}",
            avg, Pct(ft, 0.5), Pct(ft, 0.95), Pct(ft, 0.99), ft[^1]);
        L("hitches   >33 ms {0} ({1:P1})  >50 ms {2}  >100 ms {3}", over33, over33 / (double)frames, over50, over100);
        L("gpu avg {0:F1} ms, render cpu avg {1:F1} ms, process step avg {2:F1} ms",
            _gpuSum / frames, _renderCpuSum / frames, _cpuSum / frames);
        L("render avg {0:F0} draws / {1:F2} M prims (peak {2:F2} M)",
            _drawSum / (double)frames, _primSum / (double)frames / 1e6, _maxPrims / 1e6);
        int[] gcTotal = Enumerable.Range(0, 3).Select(g => _gcLast[g] - _gcBase[g]).ToArray();
        L("GC collections gen0 {0}  gen1 {1}  gen2 {2}", gcTotal[0], gcTotal[1], gcTotal[2]);
        L("");

        L("== TILE LOADING ==");
        var stats = _chunks.GetPerfStats();
        L("{0} builds finished, {1} cancelled, loader saturated (all {2} slots busy with work queued) {3:P0} of frames",
            _buildLogs.Count, stats.Cancelled, _chunks.BuildCap, _saturatedFrames / (double)frames);
        if (_buildLogs.Count > 0)
        {
            var ground = _buildLogs.Where(b => !double.IsNaN(b.GroundMs)).Select(b => b.GroundMs).OrderBy(x => x).ToList();
            var complete = _buildLogs.Select(b => b.CompleteMs).OrderBy(x => x).ToList();
            L("time to ground   p50 {0:F0}  p95 {1:F0}  max {2:F0} ms", Pct(ground, 0.5), Pct(ground, 0.95), ground.Count > 0 ? ground[^1] : double.NaN);
            L("time to complete p50 {0:F0}  p95 {1:F0}  max {2:F0} ms", Pct(complete, 0.5), Pct(complete, 0.95), complete[^1]);

            var near = _buildLogs.Where(b => b.Dist <= 1).Select(b => b.CompleteMs).OrderBy(x => x).ToList();
            if (near.Count > 0)
                L("  tiles under the player (ring <= 1): {0}, complete p50 {1:F0} p95 {2:F0} ms", near.Count, Pct(near, 0.5), Pct(near, 0.95));

            long[] totals = new long[ChunkManager.StageNames.Length];
            foreach (var b in _buildLogs) for (int i = 0; i < totals.Length; i++) totals[i] += b.StageMs[i];
            long sum = Math.Max(1, totals.Sum());
            L("worker time by stage (this session):");
            foreach (var (name, ms) in ChunkManager.StageNames.Zip(totals).OrderByDescending(x => x.Second).Where(x => x.Second > 0))
                L("  {0,-12} {1,9:F1} s  {2,4:P0}  {3,7:F1} ms/tile", name, ms / 1000.0, ms / (double)sum, ms / (double)_buildLogs.Count);

            L("slowest builds:");
            foreach (var b in _buildLogs.OrderByDescending(b => b.CompleteMs).Take(10))
            {
                int worst = Array.IndexOf(b.StageMs, b.StageMs.Max());
                L("  {0} stride {1} ring {2}: {3:F0} ms (worker {4} ms, most in {5} {6} ms)",
                    b.Id, b.Stride, b.Dist, b.CompleteMs, b.StageMs.Sum(), ChunkManager.StageNames[worst], b.StageMs[worst]);
            }
        }
        L("");

        L("== MAIN-THREAD COMMITS ==");
        if (_slowCommits.Count == 0) L("no commit over 8 ms");
        else
        {
            L("{0} commits over 8 ms; slowest:", _slowCommits.Count);
            foreach (var c in _slowCommits.OrderByDescending(c => c.Ms).Take(10))
                L("  {0} stride {1} {2}: {3:F1} ms", c.Id, c.Stride, c.Kind, c.Ms);
        }
        L("");

        L("== WORST FRAMES ==");
        foreach (var h in _hitches.OrderByDescending(h => h.Ms).Take(15))
            L("  t={0,7:F1}s {1,6:F1} ms  [{10}]  gpu {8:F1} ms  commits {2} ({3:F1} ms, worst {4})  gc {5}/{6}/{7}  at {9}",
                h.T, h.Ms, h.Commits, h.CommitMs,
                h.WorstTile == "" ? "-" : string.Format(ci, "{0} {1:F1} ms", h.WorstTile, h.WorstCommit),
                h.Gc0, h.Gc1, h.Gc2, h.GpuMs, h.Where, Cause(h));
        if (_hitches.Count == 0) L("  none over {0} ms", HitchMs);
        L("");

        L("== DIAGNOSIS ==");
        foreach (string line in Diagnose(avg, ft, gcTotal)) L("- {0}", line);
        return sb.ToString();
    }

    /// <summary>
    /// The likeliest reason for one slow frame. The GPU figure is the viewport's last measured
    /// frame, which can trail by a frame, so it is trusted only when it covers most of the time.
    /// </summary>
    private static string Cause(Hitch h) =>
        h.WorstCommit > 10 || h.CommitMs > h.Ms * 0.4 ? "commit"
        : h.Gc1 + h.Gc2 > 0 ? "gc"
        : h.GpuMs > h.Ms * 0.6 ? "gpu"
        : "other";

    /// <summary>Rules of thumb over the numbers above, pointing at the likeliest culprit first.</summary>
    private IEnumerable<string> Diagnose(double avg, List<double> ft, int[] gcTotal)
    {
        int frames = ft.Count;
        bool any = false;
        var ci = CultureInfo.InvariantCulture;

        double gpuAvg = _gpuSum / frames;
        if (avg > 20 && gpuAvg > avg * 0.7)
        {
            any = true;
            yield return string.Format(ci,
                "GPU bound: the GPU averaged {0:F1} ms of a {1:F1} ms frame ({2:F1} M primitives, peak {3:F1} M). "
                + "Lower Render distance, Detail or Horizon (fewer primitives) or 3D resolution (fewer pixels) in Settings.",
                gpuAvg, avg, _primSum / (double)frames / 1e6, _maxPrims / 1e6);
        }
        else if (avg > 20)
        {
            any = true;
            yield return string.Format(ci,
                "CPU bound: GPU averaged only {0:F1} ms of a {1:F1} ms frame; render submission {2:F1} ms. "
                + "Many draw calls ({3:F0}) point at the renderer's CPU side; otherwise profile game code.",
                _gpuSum / frames, avg, _renderCpuSum / frames, _drawSum / (double)frames);
        }

        if (_hitches.Count > 0)
        {
            int byCommit = _hitches.Count(h => Cause(h) == "commit");
            int byGc = _hitches.Count(h => Cause(h) == "gc");
            int byGpu = _hitches.Count(h => Cause(h) == "gpu");
            int other = _hitches.Count - byCommit - byGc - byGpu;
            yield return string.Format(ci, "{0} hitches over {1} ms: {2} commit, {3} gc, {4} gpu, {5} other.",
                _hitches.Count, HitchMs, byCommit, byGc, byGpu, other);
            if (byGpu > _hitches.Count * 0.3)
            {
                any = true;
                yield return string.Format(ci,
                    "{0} hitches are GPU frames: the primitive count spikes as new tiles are committed "
                    + "(prims column in frames.csv). Fewer rings or a lower Detail preset flattens them.", byGpu);
            }
            if (byCommit > _hitches.Count * 0.3)
            {
                any = true;
                yield return string.Format(ci,
                    "{0} of {1} hitches line up with slow main-thread commits (see commits.csv / MAIN-THREAD COMMITS). "
                    + "Lower 'Mesh commit budget'; if one kind of commit dominates, that is the thing to move off the main thread.",
                    byCommit, _hitches.Count);
            }
            if (byGc > _hitches.Count * 0.2)
            {
                any = true;
                yield return string.Format(ci,
                    "{0} of {1} hitches had a gen1/gen2 garbage collection in them ({2} gen2 total): look for per-frame allocations.",
                    byGc, _hitches.Count, gcTotal[2]);
            }
            if (other > _hitches.Count * 0.3)
            {
                any = true;
                yield return string.Format(ci,
                    "{0} hitches with no slow commit, GC or heavy GPU frame behind them: likely shader compilation, "
                    + "first draw of new geometry, or the OS — check their timestamps in events.log.", other);
            }
        }

        if (_buildLogs.Count > 0)
        {
            var complete = _buildLogs.Select(b => b.CompleteMs).OrderBy(x => x).ToList();
            double p95 = Pct(complete, 0.95);
            if (p95 > 2000)
            {
                any = true;
                long[] totals = new long[ChunkManager.StageNames.Length];
                foreach (var b in _buildLogs) for (int i = 0; i < totals.Length; i++) totals[i] += b.StageMs[i];
                int worst = Array.IndexOf(totals, totals.Max());
                yield return string.Format(ci,
                    "Tiles are slow to arrive (p95 {0:F0} ms). Most worker time goes to '{1}'. {2}",
                    p95, ChunkManager.StageNames[worst],
                    _saturatedFrames > frames * 0.3
                        ? "All build slots were busy much of the time: raise 'Parallel tile builds' if the CPU has cores to spare."
                        : "Build slots were mostly free, so the wait is per-tile work, not queueing.");
            }
        }

        if (!any) yield return "Nothing stands out: frames are within budget and tiles arrive promptly.";
    }
}
