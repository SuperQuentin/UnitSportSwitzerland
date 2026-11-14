using System.Collections.Concurrent;
using System.Threading;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A facade door is drawn only if it opens (#1766, #1767). The doors are placed from the walls (<see
/// cref="BuildingFootprint.ComputeDoors"/>) before any plan exists, and a building's extra door the plan could fit
/// no doorway for reads as locked: about a quarter of central Geneva's street doors, and from inside its wall shows
/// no doorway where the facade has one. Planning a building takes from a few milliseconds to seconds, far too long
/// for a tile build, so the client's warden plans the buildings near the camera that have extra doors, one at a time
/// on a thread of its own at low priority, nearest first, and records in <see cref="DoorLocks"/> which of their doors
/// have no doorway. A tile that gains a locked door is built again without it (no leaf, frame or stoop, no E
/// prompt: the door index is the drawn list), and the record is kept on disk, so the next visit draws it right from
/// the start. The main door always has its doorway, so every building keeps a way in.
/// <para>
/// Clients only (offline included): a dedicated server draws nothing, and still refuses a locked door
/// (<see cref="InteriorManager"/>). Off in test runs unless <c>--doorwarden</c>, so a probe's or a shot's doors do not
/// change under it; <c>--no-doorwarden</c> turns it off.
/// </para>
/// </summary>
public partial class DoorWarden : Node
{
    /// <summary>How far round the camera buildings are planned.</summary>
    private const float Radius = 160f;

    /// <summary>A tile whose doors changed is built again this long after the last change: one rebuild for a run of them.</summary>
    private const double RebuildDelay = 2.0;

    private readonly IChunkSource _source;
    private readonly string _storeDir;
    /// <summary>Builds a tile again once its doors changed.</summary>
    public System.Action<TileId>? Rebuild { get; set; }

    private readonly ConcurrentQueue<TileId> _changed = new();
    private readonly System.Collections.Generic.Dictionary<TileId, double> _due = new();
    private readonly System.Collections.Generic.HashSet<BuildingKey> _seen = new();
    private readonly BlockingCollection<BuildingKey> _work = new(boundedCapacity: 1);
    private Thread? _thread;
    private volatile bool _busy, _quit;
    private double _look, _traced;
    /// <summary><c>--doorwarden-trace</c>: each building judged, and the warden's state every ten seconds.</summary>
    private static readonly bool Trace = CmdArgs.Has("--doorwarden-trace");
    private int _planned, _lockedFound;

    private DoorWarden(IChunkSource source, string storeDir)
    {
        _source = source;
        _storeDir = storeDir;
        Name = "DoorWarden";
    }

    /// <summary>Whether this run plans doors: a client, not a test run unless asked, not turned off.</summary>
    public static bool Wanted => !CmdArgs.Has("--no-doorwarden")
        && (!GameSettings.TestRun || CmdArgs.Has("--doorwarden") || DoorWardenCheck.Requested());

    /// <summary>The warden of this run, if any (for checks).</summary>
    public static DoorWarden? Instance { get; private set; }

    /// <summary>Nothing left to judge round the camera, and no tile waiting to be built again (for checks).</summary>
    public bool Settled { get; private set; }

    /// <summary>Buildings planned, and doors found with no doorway, this session.</summary>
    public (int Planned, int Locked) Counts => (_planned, _lockedFound);

    /// <summary>
    /// The shared record (<see cref="DoorLocks.Shared"/>), read by every tile build from now on, and its warden under
    /// <paramref name="parent"/>. Main thread.
    /// </summary>
    public static DoorWarden Create(Node parent, IChunkSource source)
    {
        var locks = UseRecords();
        var warden = new DoorWarden(source, ProjectSettings.GlobalizePath("user://interiors"));
        locks.Changed += warden._changed.Enqueue;
        parent.AddChild(warden);
        Instance = warden;
        return warden;
    }

    /// <summary>
    /// The doors on record (user://door_locks, per plan version) for every tile build from now on: before the first
    /// tile is built, so a tile whose locked doors are known from an earlier visit is drawn without them at once. Main thread.
    /// </summary>
    public static DoorLocks UseRecords() =>
        DoorLocks.Shared ??= new DoorLocks(ProjectSettings.GlobalizePath($"user://door_locks/v{InteriorLayout.CurrentVersion}"));

    /// <summary>
    /// A tile's doors without those known to have no doorway inside (the same array when there are none): what is drawn,
    /// collided with and offered to E. Any thread.
    /// </summary>
    public static DoorSpot[] Openable(BuildingTile tile, DoorSpot[] doors)
    {
        var locks = DoorLocks.Shared;
        if (locks == null || doors.Length == 0 || !locks.AnyLocked(tile.Id)) return doors;
        var kept = new System.Collections.Generic.List<DoorSpot>(doors.Length);
        foreach (var d in doors)
            if (d.Slot == 0 || d.Index < 0 || d.Index >= tile.Buildings.Count
                || !locks.IsLocked(new DoorKey(tile.Id.E, tile.Id.N, d.Index, d.Slot), PrintOf(tile, d.Index)))
                kept.Add(d);
        return kept.Count == doors.Length ? doors : kept.ToArray();
    }

    /// <summary>What a building's plan is made from (<see cref="InteriorLayout.Matches"/>'s terms), as one number.</summary>
    public static uint PrintOf(BuildingTile tile, int index)
    {
        var b = tile.Buildings[index];
        return DoorLocks.Print(b.TriangleCount, b.MinY, b.MaxY, InteriorGenerator.GroupPrint(tile, index), HouseLayouts.HashFor(b.Egid));
    }

    public override void _Ready()
    {
        _thread = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "DoorWarden" };
        _thread.Start();
    }

    public override void _ExitTree()
    {
        _quit = true;
        _work.CompleteAdding();
        if (DoorLocks.Shared is { } locks) locks.Changed -= _changed.Enqueue;
    }

    public override void _Process(double delta)
    {
        // a rebuild waits on real seconds, whatever the game speed: the warden works at the machine's pace
        double now = RealClock.Now;
        while (_changed.TryDequeue(out var tile)) _due[tile] = now + RebuildDelay;
        if (_due.Count > 0)
        {
            TileId? ready = null;
            foreach (var (tile, at) in _due)
                if (at <= now) { ready = tile; break; }
            if (ready is { } t)
            {
                _due.Remove(t);
                Rebuild?.Invoke(t);
            }
        }

        if (Trace && now >= _traced)
        {
            _traced = now + 10;
            int extra = 0;
            foreach (var doors in DoorIndex.ByTile) foreach (var e in doors) if (e.Key.Slot > 0) extra++;
            GD.Print($"[doors] warden: busy {_busy}, settled {Settled}, {_seen.Count} seen, {_planned} planned, {_lockedFound} left out,"
                + $" {extra} extra doors drawn, {_due.Count} tiles to build again, camera {GetViewport().GetCamera3D()?.GlobalPosition}");
        }
        if (_busy) { Settled = false; return; }
        if ((_look -= delta) > 0) return;
        _look = 0.25;
        if (GetViewport().GetCamera3D() is not { } cam) return;
        var eye = cam.GlobalPosition;
        // the nearest building with an extra door not judged this session (a record on disk answers at once)
        BuildingKey? best = null;
        float bestD = Radius * Radius;
        foreach (var doors in DoorIndex.ByTile)
            foreach (var e in doors)
            {
                if (e.Key.Slot == 0) continue;
                float d = e.World.DistanceSquaredTo(eye);
                if (d >= bestD || _seen.Contains(e.Key.Building)) continue;
                (best, bestD) = (e.Key.Building, d);
            }
        Settled = best == null && _due.Count == 0 && _changed.IsEmpty;
        if (best is not { } next) return;
        _seen.Add(next);
        _busy = true;
        if (!_work.TryAdd(next)) _busy = false;
    }

    private void Work()
    {
        try
        {
            foreach (var key in _work.GetConsumingEnumerable())
            {
                if (_quit) return;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { Judge(key); }
                catch (System.Exception e) { GD.PushWarning($"[doors] {key}: {e.Message}"); }
                finally
                {
                    _busy = false;
                    if (Trace) GD.Print($"[doors] {key}: {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
                }
            }
        }
        catch (System.ObjectDisposedException) { }
    }

    /// <summary>
    /// One building, on the warden's thread: its group's plan (the stored one if it still matches, else planned now, not
    /// stored), and for each building of the group the doors it gives no doorway.
    /// </summary>
    private void Judge(BuildingKey key)
    {
        var locks = DoorLocks.Shared!;
        var tile = _source.LoadBuildingsAsync(key.Tile).GetAwaiter().GetResult();
        if (tile == null || key.Index < 0 || key.Index >= tile.Buildings.Count) return;
        if (tile.Buildings[key.Index].Kind == BuildingKind.UnderConstruction) return;
        if (locks.Known(key, PrintOf(tile, key.Index))) return;
        var group = BuildingTypes.For(tile).GroupOf(key.Index);
        int primary = group?.Primary ?? key.Index;
        var roads = _source.LoadRoadsAsync(key.Tile).GetAwaiter().GetResult();
        var grid = _source.LoadChunkAsync(key.Tile).GetAwaiter().GetResult();
        var layout = Stored(tile, primary) ?? InteriorGenerator.Generate(tile, primary, roads, grid);
        if (_quit) return;
        _planned++;
        foreach (int member in group?.Members ?? [key.Index])
        {
            var fp = BuildingFootprint.Compute(tile, member, roads, grid);
            var locked = new System.Collections.Generic.List<int>();
            if (fp != null)
                foreach (var d in fp.Doors)
                    if (d.Slot > 0 && d.Width > 0 && layout?.EntranceOf(new DoorKey(key.TileE, key.TileN, member, d.Slot).ToString()) == null)
                        locked.Add(d.Slot);
            _lockedFound += locked.Count;
            locks.Record(new BuildingKey(key.TileE, key.TileN, member), PrintOf(tile, member), locked);
        }
        if (_planned % 25 == 0) GD.Print($"[doors] warden: {_planned} buildings planned, {_lockedFound} doors with no doorway left out");
    }

    /// <summary>The plan the interiors stored for a building, if it was made from what the building is now.</summary>
    private InteriorLayout? Stored(BuildingTile tile, int index)
    {
        string path = System.IO.Path.Combine(_storeDir, $"{tile.Id.E}_{tile.Id.N}", $"{index}.json");
        try
        {
            return System.IO.File.Exists(path) && InteriorLayout.FromJson(System.IO.File.ReadAllText(path)) is { } stored
                && stored.Matches(tile.Buildings[index], InteriorGenerator.GroupPrint(tile, index)) ? stored : null;
        }
        catch (System.IO.IOException) { return null; }
    }
}
