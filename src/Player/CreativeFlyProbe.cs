using System.Diagnostics;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Earth;

namespace UnitSport.Player;

/// <summary>
/// <c>--creativefly heading,seconds[,boost[,agl]] --at E,N</c> (#1773): a player in creative mode flies level along a compass
/// heading, the fly boost held (<c>boost</c> 1, the default: <see cref="FootPlayer.CreativeBoost"/> times the flight speed,
/// 60 m/s) or not (0), <c>agl</c> metres over the ground at the start (default 40). After the arrival settles it prints the
/// frame-time distribution and the worst frames with what the earth world spent in them (<see cref="EarthWorld.Spent"/>):
/// leaves' meshes, their tile events, their furniture, dropped leaves, collision bodies, planning. Either frame. Exits 0.
/// </summary>
public partial class CreativeFlyProbe : Node
{
    public static bool Requested => CmdArgs.Has("--creativefly");

    private readonly IWorldGround? _ground;
    private readonly WorldOrigin _origin;
    private readonly EarthWorld? _earth;
    private readonly float _heading;
    private readonly double _seconds;
    private readonly bool _boost;
    private readonly float _agl;

    private FootPlayer? _player;
    private bool _anchored, _flying, _done;
    private double _settle;
    private double _flown;
    private long _last;
    private GlobalPos _start;

    private readonly List<(double Ms, EarthWorld.Spent Spent, double At, Gc Gc)> _frames = new();

    /// <summary>Collections and the time paused for them in a frame.</summary>
    private readonly record struct Gc(int Gen0, int Gen1, int Gen2, double PauseMs);
    private int _g0, _g1, _g2;
    private TimeSpan _pause;
    private EarthWorld.Spent _sum;
    private Gc _gcSum;
    private long _allocated;
    private AllocTicks? _allocs;

    public CreativeFlyProbe(IWorldGround? ground, WorldOrigin origin, EarthWorld? earth)
    {
        _ground = ground;
        _origin = origin;
        _earth = earth;
        var parts = CmdArgs.Value("--creativefly")?.Split(',') ?? [];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _heading = parts.Length > 0 ? float.Parse(parts[0], inv) : 0;
        _seconds = parts.Length > 1 ? double.Parse(parts[1], inv) : 30;
        _boost = parts.Length <= 2 || parts[2] != "0";
        _agl = parts.Length > 3 ? float.Parse(parts[3], inv) : 40;
        // last in the frame: the delta measured is the whole frame's
        ProcessPriority = int.MaxValue - 1;
    }

    private static void Log(string what) => GD.Print($"[creativefly] {what}");

    public override void _PhysicsProcess(double delta)
    {
        if (_done || _player != null) return;
        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);
        if (!_anchored) { _anchored = true; TestWorld.AnchorEarthTarget(this, _ground, _origin, e, n); }
        if (!TestWorld.TryGround(_ground, at, out float ground)) return;

        _player = new FootPlayer { Name = "Probe", Terrain = _ground };
        // a compass bearing: a node faces −Z (north) and +yaw turns it toward −X (west)
        _player.Rotation = new Vector3(0, -Mathf.DegToRad(_heading), 0);
        AddChild(_player);
        _player.GlobalPosition = new Vector3(at.X, ground + _agl, at.Z);
        if (_origin.Frame.IsAnchor) _ground?.AddAnchor(_player, collision: true);
        if (_ground == null) _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
        _player.Creative = true;
        _player.CreativeFlying = true;
        _player.WalkControls = () => (Vector3.Zero, false);
        Log($"spawned at LV95 {e:F0}/{n:F0}, {_agl:F0} m over the ground ({ground:F1} m), heading {_heading:F0}°, boost {_boost}");
    }

    public override void _Process(double delta)
    {
        if (_done || _player == null) return;
        long now = Stopwatch.GetTimestamp();
        double ms = _last == 0 ? 0 : Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds;
        _last = now;
        var spent = _earth?.TakeSpent() ?? default;
        int g0 = System.GC.CollectionCount(0), g1 = System.GC.CollectionCount(1), g2 = System.GC.CollectionCount(2);
        var pause = System.GC.GetTotalPauseDuration();
        var gc = new Gc(g0 - _g0, g1 - _g1, g2 - _g2, (pause - _pause).TotalMilliseconds);
        (_g0, _g1, _g2, _pause) = (g0, g1, g2, pause);

        if (!_flying)
        {
            // hover until the arrival has settled: the report is the flight, not the teleport
            _settle += delta;
            if (_settle < 8 || (_earth != null && !_earth.Idle && _settle < 120)) return;
            _flying = true;
            _allocated = System.GC.GetTotalAllocatedBytes();
            if (CmdArgs.Has("--allocs")) _allocs = new AllocTicks();
            _start = _origin.ToGlobal(_player.GlobalPosition);
            var ahead = (-_player.GlobalBasis.Z with { Y = 0 }).Normalized();
            _player.WalkControls = () => (ahead, false);
            if (_boost) Input.ActionPress(PlayerInput.FlyBoost);
            Log($"flying after {_settle:F1} s of settling");
            return;
        }

        _flown += delta;
        _frames.Add((ms, spent, _flown, gc));
        _gcSum = new(_gcSum.Gen0 + gc.Gen0, _gcSum.Gen1 + gc.Gen1, _gcSum.Gen2 + gc.Gen2, _gcSum.PauseMs + gc.PauseMs);
        Add(ref _sum, spent);
        if (_flown < _seconds) return;
        _done = true;
        if (_boost) Input.ActionRelease(PlayerInput.FlyBoost);
        Report();
        GetTree().Quit(0);
    }

    private static void Add(ref EarthWorld.Spent a, EarthWorld.Spent b)
    {
        a.LeafMesh += b.LeafMesh; a.LeafEntered += b.LeafEntered; a.LeafFurnish += b.LeafFurnish; a.LeafDrop += b.LeafDrop;
        a.Bodies += b.Bodies; a.Plan += b.Plan; a.PatchPlan += b.PatchPlan; a.Leaves += b.Leaves; a.BodyCount += b.BodyCount; a.Pieces += b.Pieces;
    }

    private static string Of(EarthWorld.Spent s) =>
        $"earth {s.Total:F1} ms (leaves {s.Leaves}: mesh {s.LeafMesh:F1}, entered {s.LeafEntered:F1}, furnish {s.LeafFurnish:F1}; "
        + $"drop {s.LeafDrop:F1}; bodies {s.BodyCount} ({s.Pieces} pieces): {s.Bodies:F1}; plan {s.Plan:F1}; patch plan {s.PatchPlan:F1})";

    private static string Of(Gc g) => $"gc {g.Gen0}/{g.Gen1}/{g.Gen2} paused {g.PauseMs:F1} ms";

    private void Report()
    {
        double dist = _start.DistanceTo(_origin.ToGlobal(_player!.GlobalPosition));
        var ms = new List<double>(_frames.Count);
        foreach (var f in _frames) ms.Add(f.Ms);
        ms.Sort();
        int n = ms.Count;
        int over20 = 0, over33 = 0;
        foreach (double m in ms) { if (m > 20) over20++; if (m > 33) over33++; }
        Log($"{n} frames over {_flown:F1} s, {dist:F0} m ({dist / _flown:F1} m/s): p50 {ms[n / 2]:F1} ms, p95 {ms[(int)(n * 0.95)]:F1}, "
            + $"p99 {ms[(int)(n * 0.99)]:F1}, max {ms[^1]:F1}; >20 ms: {over20}, >33 ms: {over33}");
        Log($"total {Of(_sum)}; {Of(_gcSum)}; allocated {(System.GC.GetTotalAllocatedBytes() - _allocated) / 1048576.0 / _flown:F0} MB/s, heap {System.GC.GetTotalMemory(false) / 1048576:F0} MB, "
            + $"peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:F0} MB, gc mode {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}");
        _frames.Sort(static (a, b) => b.Ms.CompareTo(a.Ms));
        for (int i = 0; i < Math.Min(12, _frames.Count); i++)
            Log($"worst {i + 1}: {_frames[i].Ms:F1} ms at {_frames[i].At:F2} s, {Of(_frames[i].Gc)}, {Of(_frames[i].Spent)}");
        if (_earth != null) Log($"earth: {_earth.Status}");
        if (_allocs != null)
        {
            var top = new List<KeyValuePair<string, long>>(_allocs.Bytes);
            top.Sort(static (a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < Math.Min(25, top.Count); i++) Log($"allocs {top[i].Value / 1048576.0 / _flown,7:F1} MB/s {top[i].Key}");
            _allocs.Dispose();
        }
        Log("RESULT: ok");
    }
}

/// <summary>
/// <c>--allocs</c>: the runtime's allocation ticks (one every ~100 KB a thread allocates, with the type) summed by type, on
/// every thread: what drives the collector during a flight.
/// </summary>
internal sealed class AllocTicks : System.Diagnostics.Tracing.EventListener
{
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Bytes = new();

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (e.EventName is not { } name || !name.StartsWith("GCAllocationTick") || e.Payload == null || e.PayloadNames == null) return;
        string type = "?";
        long amount = 0;
        for (int i = 0; i < e.PayloadNames.Count; i++)
        {
            if (e.PayloadNames[i] == "TypeName") type = e.Payload[i] as string ?? "?";
            else if (e.PayloadNames[i] == "AllocationAmount64") amount = Convert.ToInt64(e.Payload[i]);
            else if (e.PayloadNames[i] == "AllocationAmount" && amount == 0) amount = Convert.ToInt64(e.Payload[i]);
        }
        Bytes.AddOrUpdate(type, amount, (_, v) => v + amount);
    }
}
