using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using UnitSport.Core;

namespace UnitSport.Terrain;

/// <summary>
/// <c>godot --headless --path . -- --worldgroundcheck --chunks fixture:lake</c> (#1532, G2 C1): the ground
/// gameplay asks (<see cref="IWorldGround"/>, the flat world's <see cref="ChunkManager.Ground"/>) answers what
/// the streamer did before it, on the lake course's shore, river and lake. Over a 2 km grid round the start:
/// every height bit for bit the streamer's own (<see cref="ChunkManager.HeightAt"/>) and the tile grid's mesh
/// height; the surface the water's level wherever it is over the ground; the water's level and wave scale
/// consistent; the cover known at the start; no tunnel or pit on the course. The tile events: the start's tile
/// (<see cref="IWorldGround.TileAt"/>) entered, its mesh found by <see cref="IWorldGround.GroundAt"/>. Then a
/// microbenchmark of <see cref="IWorldGround.TryGetHeight"/> through the interface against the direct call:
/// best of 7 rounds of 400 000 queries each, no allocation, at most 15 % or 5 ns a query slower.
/// RESULT ok or FAILED.
/// </summary>
public partial class WorldGroundCheck : Node3D
{
    public static bool Requested() => CmdArgs.Has("--worldgroundcheck");

    private const double TimeoutS = 180;
    private const int Half = 40;            // the grid: 81 x 81 points
    private const float Step = 25f;         // 25 m apart: 2 km across
    private const int Rounds = 7, Queries = 400_000;

    /// <summary>The flat world's streamer, whose own answers the ground is held to; null under <c>--frame earth</c>.</summary>
    private readonly ChunkManager? _flat;
    /// <summary>The earth world (#1740): the ground is held to its own height query instead, and there is no tile grid.</summary>
    private readonly Earth.EarthWorld? _earth;
    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly HashSet<GroundTile> _entered = new();
    private readonly List<string> _faults = new();
    private ulong _startMs;
    private int _unloaded, _furnished;
    private bool _done;

    public WorldGroundCheck(IWorldGround ground, ChunkManager? flat, Earth.EarthWorld? earth, WorldOrigin origin)
    {
        _ground = ground;
        _flat = earth == null ? flat : null;
        _earth = earth;
        _origin = origin;
    }

    /// <summary>The world's own height query, under the interface: the streamer's or the earth world's.</summary>
    private bool Direct(Vector3 p, out float h) => _earth != null ? _earth.HeightAt(p, out h) : _flat!.HeightAt(p, out h);

    public override void _Ready()
    {
        _ground.TileEntered += OnEntered;
        _ground.TileUnloaded += OnUnloaded;
        _ground.TileFurnished += OnFurnished;
        var (e, n) = SpawnPoint.ParseTarget();
        Position = _origin.ToWorld(e, n, 0);
        _ground.AddAnchor(this, collision: true);
        _startMs = Time.GetTicksMsec();
        Log($"start {Position}, anchored {_ground.HasAnchor(this)}");
    }

    public override void _ExitTree()
    {
        _ground.TileEntered -= OnEntered;
        _ground.TileUnloaded -= OnUnloaded;
        _ground.TileFurnished -= OnFurnished;
        _ground.RemoveAnchor(this);
    }

    private void OnEntered(GroundTile tile) => _entered.Add(tile);

    private void OnUnloaded(GroundTile tile) => _unloaded++;

    private void OnFurnished(GroundTile tile, ChunkNode node, Interiors.DoorSpot[] doors) => _furnished++;

    public override void _Process(double delta)
    {
        if (_done) return;
        // the wall clock: tiles build on threads, which --fixed-fps would outrun
        double t = (Time.GetTicksMsec() - _startMs) / 1000.0;
        var here = Position;
        bool ready = _ground.HasCollisionAt(here) && _ground.TryGetCover(here, out _)
            && _ground.GroundAt(_ground.TileAt(here)) != null;
        if (!ready)
        {
            if (t > TimeoutS) Finish($"FAILED the start's tile never had its collision, cover and mesh in {TimeoutS} s");
            return;
        }
        _done = true;
        Log($"ready after {t:F1} s: {_entered.Count} tiles entered, {_furnished} furnished, {_unloaded} unloaded");

        if (!_entered.Contains(_ground.TileAt(here))) _faults.Add($"the start's tile {_ground.TileAt(here)} was never entered");
        if (_ground.Origin != _origin) _faults.Add("the ground's origin is not the world's");
        var loaded = Values(here);
        if (_earth != null) Parity(here, loaded);
        Benchmark(loaded);
        foreach (var fault in _faults.GetRange(0, Math.Min(_faults.Count, 12))) Log($"  {fault}");
        Finish(_faults.Count == 0 ? "ok" : $"FAILED {_faults.Count} faults");
    }

    /// <summary>Every query over the grid against the streamer's own answer; the points with ground, for the benchmark.</summary>
    private Vector3[] Values(Vector3 here)
    {
        var loaded = new List<Vector3>();
        int wet = 0, surfaced = 0, mismatched = 0;
        for (int i = -Half; i <= Half; i++)
            for (int j = -Half; j <= Half; j++)
            {
                var p = here + new Vector3(i * Step, 0, j * Step);
                bool has = _ground.TryGetHeight(p, out float h);
                bool direct = Direct(p, out float hd);
                if (has != direct || BitConverter.SingleToInt32Bits(h) != BitConverter.SingleToInt32Bits(hd))
                {
                    if (mismatched++ < 3) _faults.Add($"height at {p}: {has}/{h} through the ground, {direct}/{hd} direct");
                    continue;
                }
                if (!has) continue;
                loaded.Add(p);

                // the grid the tile is drawn from, read the way the height query always has (the flat world's tiles only)
                if (_flat != null)
                {
                    var (e, n) = _origin.ToLv95(p);
                    if (_flat.GridAt(_origin.TileAt(p)) is not { } grid || (float)grid.SampleMeshHeight(e, n) != h)
                        _faults.Add($"height at {p}: {h}, the tile's mesh height {(_flat.GridAt(_origin.TileAt(p)) is { } g ? (float)g.SampleMeshHeight(e, n) : float.NaN)}");
                }

                bool water = _ground.TryGetWater(p, out float still, out float wave);
                bool level = _ground.TryGetWaterLevel(p, out float still2);
                if (water != level || still != still2) _faults.Add($"water at {p}: TryGetWater {water}/{still}, TryGetWaterLevel {level}/{still2}");
                if (water && (wave < 0f || wave > 1f)) _faults.Add($"wave scale {wave} at {p}");
                if (water) wet++;

                if (!_ground.TryGetSurface(p, out float surface)) _faults.Add($"no surface at {p} over ground at {h}");
                else
                {
                    float expected = water && still > h ? still : h;
                    if (surface != expected) _faults.Add($"surface at {p}: {surface}, expected {expected} (ground {h}, water {water}/{still})");
                    if (surface > h) surfaced++;
                }
                if (_ground.InTunnel(p)) _faults.Add($"in a tunnel at {p}: the lake course has none");
                if (_ground.InPit(p)) _faults.Add($"in a pit at {p}: the lake course has none");
            }
        if (mismatched > 3) _faults.Add($"{mismatched} heights differ in all");
        Log($"{loaded.Count} of {(2 * Half + 1) * (2 * Half + 1)} points with ground, {wet} wet, {surfaced} with water over the ground");
        if (loaded.Count < 100) _faults.Add($"only {loaded.Count} points with ground");
        if (surfaced == 0) _faults.Add("no point with water over the ground: the lake was not sampled");
        return loaded.ToArray();
    }

    /// <summary>
    /// The earth world's height query (#1751: a thread's recent leaves, the walk from the lattice guess) against the way it
    /// was (<see cref="Earth.EarthWorld.HeightAtByKey"/>): the same answer within 1 mm (bit for bit nearly everywhere)
    /// over the grid, a 64 m square at 0.4 m round the start leaf's south-west corner (where up to four leaves meet), the
    /// same square 3 km up, all of them again after a re-anchor 3 km away with its small turn, and back.
    /// </summary>
    private void Parity(Vector3 here, Vector3[] grid)
    {
        var points = new List<Vector3>(grid);
        var tile = _ground.TileAt(here);
        if (tile.IsLeaf)
        {
            var key = tile.Leaf;
            var corner = _origin.Frame.FromEcef(key.Vertex(Terrain.Format.CubeKey.LatticeBits(key.Level), 0, 0, 0));
            corner.Y = here.Y;
            for (int i = -80; i <= 80; i++)
                for (int j = -80; j <= 80; j++)
                {
                    var p = corner + new Vector3(i * 0.4f, 0, j * 0.4f);
                    points.Add(p);
                    points.Add(p + new Vector3(0, 3000, 0));
                }
        }
        else _faults.Add($"the start's tile {tile} is not a leaf");
        Compare(points, "before the re-anchor");

        // 3 km east of the start, the ENU there: the turn OriginShifter makes
        var before = _origin.Frame.Axes!;
        var away = _origin.ToEcef(here + new Vector3(3000, 0, 0));
        _origin.MoveTo(FrameAxes.Enu(away));
        var moved = new List<Vector3>(points.Count);
        foreach (var p in points) moved.Add(_origin.Frame.FromEcef(before.ToEcef(p.X, p.Y, p.Z)));
        Compare(moved, "after the re-anchor");
        // and back: each leaf's view from the start's frame made again (once, so the benchmark allocates nothing)
        _origin.MoveTo(before);
        Compare(points, "back at the start");
    }

    private void Compare(List<Vector3> points, string when)
    {
        int has = 0, exact = 0, differ = 0;
        double worst = 0;
        foreach (var p in points)
        {
            bool a = _earth!.HeightAt(p, out float h), b = _earth.HeightAtByKey(p, out float hk);
            if (a != b)
            {
                if (differ++ < 3) _faults.Add($"parity {when} at {p}: {a}/{h} now, {b}/{hk} by the key");
                continue;
            }
            if (!a) continue;
            has++;
            double d = Math.Abs(h - hk);
            worst = Math.Max(worst, d);
            if (BitConverter.SingleToInt32Bits(h) == BitConverter.SingleToInt32Bits(hk)) exact++;
            else if (d > 0.001 && differ++ < 3) _faults.Add($"parity {when} at {p}: {h} now, {hk} by the key");
        }
        Log(FormattableString.Invariant($"parity {when}: {points.Count} points, {has} with ground, {exact} bit for bit, worst {worst * 1000:F3} mm, {differ} differ"));
        if (has < points.Count / 4) _faults.Add($"parity {when}: only {has} of {points.Count} points with ground");
    }

    /// <summary>The height query through the interface against the direct call, best of several rounds each.</summary>
    private void Benchmark(Vector3[] points)
    {
        if (points.Length == 0) return;
        double bestGround = double.MaxValue, bestDirect = double.MaxValue;
        long allocated = 0;
        float sink = 0;
        var clock = new Stopwatch();
        for (int r = 0; r < Rounds; r++)
        {
            // alternate which leg goes first, so a warm cache favours neither
            for (int leg = 0; leg < 2; leg++)
            {
                bool viaGround = (leg == 0) == (r % 2 == 0);
                long before = GC.GetAllocatedBytesForCurrentThread();
                clock.Restart();
                if (viaGround)
                    for (int q = 0; q < Queries; q++) { _ground.TryGetHeight(points[q % points.Length], out float h); sink += h; }
                else if (_earth != null)
                    for (int q = 0; q < Queries; q++) { _earth.HeightAt(points[q % points.Length], out float h); sink += h; }
                else
                    for (int q = 0; q < Queries; q++) { _flat!.HeightAt(points[q % points.Length], out float h); sink += h; }
                clock.Stop();
                double ns = clock.Elapsed.TotalMilliseconds * 1e6 / Queries;
                if (viaGround)
                {
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    bestGround = Math.Min(bestGround, ns);
                }
                else bestDirect = Math.Min(bestDirect, ns);
            }
        }
        if (_earth != null)
        {
            // informational (#1751): the same points shuffled, so successive queries hop between leaves; and the old way
            var shuffled = (Vector3[])points.Clone();
            new Random(1751).Shuffle(shuffled);
            double bestShuffled = double.MaxValue, bestByKey = double.MaxValue;
            for (int r = 0; r < 3; r++)
            {
                clock.Restart();
                for (int q = 0; q < Queries; q++) { _earth.HeightAt(shuffled[q % shuffled.Length], out float h); sink += h; }
                bestShuffled = Math.Min(bestShuffled, clock.Elapsed.TotalMilliseconds * 1e6 / Queries);
                clock.Restart();
                for (int q = 0; q < Queries / 10; q++) { _earth.HeightAtByKey(points[q % points.Length], out float h); sink += h; }
                bestByKey = Math.Min(bestByKey, clock.Elapsed.TotalMilliseconds * 1e7 / Queries);
            }
            Log(FormattableString.Invariant($"earth HeightAt shuffled: {bestShuffled:F1} ns a query; the parity reference (the cube key, walked from the corner): {bestByKey:F1} ns"));
        }
        Log(FormattableString.Invariant(
            $"TryGetHeight: {bestGround:F1} ns a query through IWorldGround, {bestDirect:F1} ns direct ({(bestGround - bestDirect):+0.0;-0.0} ns, x{bestGround / bestDirect:F3}), {allocated} bytes allocated through the ground, best of {Rounds} x {Queries} (sink {sink:G3})"));
        if (allocated != 0) _faults.Add($"{allocated} bytes allocated by {Rounds * Queries} height queries through the ground");
        if (bestGround > Math.Max(bestDirect * 1.15, bestDirect + 5)) _faults.Add($"the ground's height query costs {bestGround:F1} ns against {bestDirect:F1} ns direct");
    }

    private static void Log(string what) => GD.Print($"[worldgroundcheck] {what}");

    private void Finish(string result)
    {
        Log($"RESULT: {result}");
        SetProcess(false);
        GetTree().Quit(result == "ok" ? 0 : 1);
    }
}
