using System.Collections.Concurrent;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Streams terrain chunks in LOD rings around registered anchors. Workers (Task.Run) do
/// file IO, decoding and array building; the main thread commits a budgeted number of
/// Godot resources per frame to avoid hitches. With BuildMeshes=false (dedicated server)
/// only ChunkGrid data is loaded, which is all height queries need.
/// </summary>
public partial class ChunkManager : Node3D
{
    [Export] public bool BuildMeshes { get; set; } = true;
    [Export] public bool BuildCollision { get; set; } = true;

    private const double PlayEvalInterval = 0.1;
    private const int PlayCollisionCommitsPerFrame = 1;

    /// <summary>
    /// Loads as fast as the machine allows, for an offline render that owns the clock.
    ///
    /// <para>
    /// Every throttle below exists for somebody watching: the commit budget keeps a tile's
    /// ArrayMesh from hitching a live frame, and the build cap keeps a streaming client from
    /// flooding the server. A video export has neither problem — nobody is watching, and the
    /// frame is only captured once the world has settled, so a hitch costs exactly nothing while
    /// the wait for it costs the whole export.
    /// </para>
    /// </summary>
    public bool OfflineMode { get; set; }

    private double EvalInterval => OfflineMode ? 0 : PlayEvalInterval;
    private int CollisionCommitsPerFrame => OfflineMode ? int.MaxValue : PlayCollisionCommitsPerFrame;

    /// <summary>
    /// Main-thread milliseconds a frame may spend turning finished builds into Godot meshes.
    /// A budget in time rather than a count of meshes: a stride-50 horizon tile is 441
    /// vertices and a stride-1 one is a million, and "two per frame" was sized for the latter,
    /// so a teleport spent seconds committing far tiles one pair at a time.
    /// </summary>
    public double CommitBudgetMs { get; set; } = 4;

    public LodPolicy Lod { get; set; } = new();

    /// <summary>
    /// Where the player is looking, unit length, or zero for no preference. Tiles behind the
    /// camera are queued later than tiles in front of it at the same distance - never
    /// skipped, just deferred - so a turn does not wait on the ground behind your back.
    /// </summary>
    public Vector3 ViewDirection { get; set; }

    /// <summary>
    /// Whether tiles are coming over the network right now. Decides the auto build cap: six
    /// chains is what a streaming server can feed, but local disk takes a core each.
    /// </summary>
    public Func<bool>? Streaming { get; set; }

    /// <summary>Explicit cap on builds in flight; 0 = decide from <see cref="Streaming"/>.</summary>
    public int MaxConcurrentBuildsOverride { get; set; }

    /// <summary>The 100 m far-horizon lattice drawn past the last ring. Null on a server.</summary>
    public HorizonLayer? Horizon { get; private set; }

    private IChunkSource? _source;

    /// <summary>
    /// Where tiles come from. Exposed so features that need the underlying data rather than the
    /// rendered world — GPX road matching reads <c>.road</c> tiles the streaming rings may never
    /// have loaded — can ask for it directly, and get the same shipped/cache/server tiering.
    /// </summary>
    public IChunkSource? Source => _source;
    private WorldOrigin? _origin;
    private Material? _material;
    private HashSet<TileId> _available = new();

    /// <summary>
    /// Tile builds allowed to run concurrently. Local disk could take far more, but the
    /// per-frame commit budget is the real limiter there, so a small number costs nothing
    /// locally and is what makes streaming usable.
    /// </summary>
    private const int PlayMaxConcurrentBuilds = 6;

    /// <summary>
    /// Offline the disk and the CPU are the only limits, so use the cores that are there.
    /// Two per core because each build is IO then CPU, and the halves interleave.
    /// </summary>
    private static readonly int OfflineMaxConcurrentBuilds =
        Math.Max(PlayMaxConcurrentBuilds, System.Environment.ProcessorCount * 2);

    private static readonly int LocalMaxConcurrentBuilds =
        Math.Max(PlayMaxConcurrentBuilds, System.Environment.ProcessorCount);

    private int MaxConcurrentBuilds =>
        OfflineMode ? OfflineMaxConcurrentBuilds
        : MaxConcurrentBuildsOverride > 0 ? MaxConcurrentBuildsOverride
        : Streaming?.Invoke() == true ? PlayMaxConcurrentBuilds
        : LocalMaxConcurrentBuilds;

    private int _buildsInFlight;
    private int _cancelledBuilds;

    /// <summary>Builds abandoned mid-flight because their tile stopped being wanted.</summary>
    public int CancelledBuilds => Volatile.Read(ref _cancelledBuilds);
    private int _fullLoads;
    private int _coarseLoads;

    /// <summary>
    /// Worker milliseconds spent in each stage of a tile build, summed across threads.
    ///
    /// <para>
    /// Here because the answer was not what arithmetic predicted. Reading a tile is obviously the
    /// expensive part right up until you time it, and a load path tuned against a guess is tuned
    /// against nothing — so the export prints this and the next change is aimed at whichever line
    /// is actually largest.
    /// </para>
    /// </summary>
    private long _msChunk, _msAux, _msSurface, _msCollision;
    private long _msRoadLoad, _msRoadMesh, _msTrees, _msWater, _msBldgLoad, _msBldgMesh;

    /// <summary>Where the worker time went, longest first. Empty before anything has been built.</summary>
    public string BuildTimeReport()
    {
        var stages = new (string Name, long Ms)[]
        {
            ("chunk", Volatile.Read(ref _msChunk)),
            ("holes+cover", Volatile.Read(ref _msAux)),
            ("surface", Volatile.Read(ref _msSurface)),
            ("collision", Volatile.Read(ref _msCollision)),
            ("road-read", Volatile.Read(ref _msRoadLoad)),
            ("road-mesh", Volatile.Read(ref _msRoadMesh)),
            ("trees", Volatile.Read(ref _msTrees)),
            ("water", Volatile.Read(ref _msWater)),
            ("bldg-read", Volatile.Read(ref _msBldgLoad)),
            ("bldg-mesh", Volatile.Read(ref _msBldgMesh)),
        };

        long total = stages.Sum(x => x.Ms);
        if (total == 0) return "no builds";

        return string.Join(", ", stages
            .Where(x => x.Ms > 0)
            .OrderByDescending(x => x.Ms)
            .Select(x => $"{x.Name} {x.Ms / 1000.0:F1}s ({x.Ms * 100.0 / total:F0}%)"));
    }

    /// <summary>
    /// Tiles read at full and at coarse resolution since boot, and the bytes that implies.
    /// Reported by the video exporter, because "how much did we actually read" is the number
    /// this whole load path is optimised against.
    /// </summary>
    public (int Full, int Coarse, long Bytes) LoadStats
    {
        get
        {
            int full = Volatile.Read(ref _fullLoads);
            int coarse = Volatile.Read(ref _coarseLoads);
            const long fullBytes = ChunkFormat.GridSize * ChunkFormat.GridSize * 2L
                + ChunkFormat.HeaderSize;
            long coarseSide = (ChunkFormat.GridSize - 1) / ChunkFormat.CoarseStride + 1;
            long coarseBytes = coarseSide * coarseSide * 2L + ChunkFormat.HeaderSize;
            return (full, coarse, full * fullBytes + coarse * coarseBytes);
        }
    }
    private readonly List<Node3D> _anchors = new();
    private readonly Dictionary<TileId, ChunkState> _chunks = new();
    private readonly ConcurrentQueue<BuildResult> _ready = new();

    /// <summary>
    /// Tiles whose build threw or found nothing. Without this a failure left
    /// <c>PendingStride</c> set for ever and the tile was never retried or replaced — one
    /// exception on a worker permanently deleted that piece of the world.
    /// </summary>
    private readonly ConcurrentQueue<TileId> _failedBuilds = new();
    private double _sinceEval = double.MaxValue;

    /// <summary>
    /// Tiles the last ring evaluation asked for. Kept so <see cref="SettledNear"/> can tell
    /// "this tile is finished" from "this tile has not been queued yet" — the two look identical
    /// from <see cref="_chunks"/> alone.
    /// </summary>
    private readonly HashSet<TileId> _desired = new();

    private sealed class ChunkState
    {
        public int ActiveStride = -1;   // stride of the committed mesh (0 = grid-only), -1 = none
        public int PendingStride = -1;  // stride currently being built, -1 = idle
        public bool HasCollision;
        public bool PendingCollision;
        public bool HasRoads;
        public bool PendingRoads;
        public bool HasBuildings;
        public bool PendingBuildings;
        public bool HasBuildingCollision;
        public ChunkGrid? Grid;
        public HashSet<int>? Holes;   // tunnel portals; null until the tile is first loaded
        public bool HolesLoaded;
        public byte[]? Cover;
        public bool CoverLoaded;
        public ChunkNode? Node;

        /// <summary>Cancels the build in flight, if any. Bumping Generation orphans its results.</summary>
        public CancellationTokenSource? Cts;
        public int Generation;

        public void CancelPending()
        {
            if (Cts == null) return;
            Cts.Cancel();
            Cts = null;
            Generation++;
            PendingStride = -1;
            PendingCollision = false;
            PendingRoads = false;
            PendingBuildings = false;
        }
    }

    // Meshes arrive as ready ArrayMesh resources: the worker builds them (RenderingServer is
    // thread-safe), so the main thread's share of a commit is assigning them to nodes.
    private readonly record struct BuildResult(
        TileId Id, int Stride, int Generation, ChunkGrid Grid, bool Interim,
        ArrayMesh? Mesh, float[]? CollisionMap,
        ArrayMesh? Roads, bool RoadsRequested,
        HashSet<int>? Holes, byte[]? Cover,
        ArrayMesh? Buildings, Vector3[]? BuildingFaces, bool BuildingsRequested,
        ChunkNode.TreeMeshes? Trees, ArrayMesh? Water,
        Vector3[]? RoadCollisionFaces = null);

    private Material? _roadMaterial;
    private Material? _buildingMaterial;
    private Material? _treeMaterial;
    private Material? _waterMaterial;

    public void Initialize(IChunkSource source, WorldOrigin origin, TerrainManifest manifest,
        Material? material, Material? roadMaterial = null, Material? buildingMaterial = null,
        Material? treeMaterial = null, Material? waterMaterial = null)
    {
        _source = source;
        _origin = origin;
        _material = material;
        _roadMaterial = roadMaterial;
        _buildingMaterial = buildingMaterial;
        _treeMaterial = treeMaterial;
        _waterMaterial = waterMaterial;
        _available = manifest.Tiles.Select(t => t.Id).ToHashSet();

        if (BuildMeshes && material is ShaderMaterial terrainShader)
        {
            // its own material instance: the discard rectangle must not touch the tiles
            var horizonMaterial = new ShaderMaterial { Shader = terrainShader.Shader };
            Horizon = new HorizonLayer { Name = "Horizon" };
            Horizon.Initialize(source, origin, horizonMaterial,
                () => _anchors.Select(a => a.GlobalPosition));
            AddChild(Horizon);
            FitHorizonCoverage();
        }

        // A server only holds grids for height queries around players, so the player's
        // render distance means nothing to it: it keeps a small fixed radius of full tiles.
        if (BuildMeshes) ApplySettings(GameSettings.Current);
        else Lod = new LodPolicy { Rings = new LodPolicy.Ring[] { new(ServerGridRadius, 1) } };
    }

    /// <summary>Tiles of height data a server keeps around each player (2 MB each).</summary>
    private const int ServerGridRadius = 2;

    /// <summary>
    /// Takes the player's settings: ring table, budgets, horizon reach. Safe to call at any
    /// time — the next evaluation rebuilds tiles whose stride changed and unloads the rest
    /// through the ordinary hysteresis.
    /// </summary>
    public void ApplySettings(GameSettings s)
    {
        Lod = LodPolicy.FromSettings(s);
        CommitBudgetMs = s.CommitBudgetMs;
        MaxConcurrentBuildsOverride = s.MaxConcurrentBuilds;
        if (Horizon != null)
        {
            Horizon.DistanceM = s.HorizonKm * 1000.0;
            FogUniforms.Apply(Horizon.Material);
        }
        _sinceEval = double.MaxValue;
    }

    /// <summary>
    /// Dissolves world geometry lying on the segment between two points - used to clear the line
    /// between a camera and what it is filming.
    ///
    /// <para>
    /// It lives here because the five world materials are shared by every tile: one uniform write
    /// reaches the whole streamed world, whatever has loaded since. The alternative, per-instance
    /// transparency, is not available - the trees are a MultiMesh sharing a single material, and
    /// every one of these shaders is <c>unshaded</c> with no alpha blend at all.
    /// </para>
    ///
    /// <para>
    /// A radius of 0 disables it, and the shaders branch on that before touching anything, so
    /// modes that never call this render exactly as they did before.
    /// </para>
    ///
    /// <para>
    /// Buildings and trees only - deliberately NOT the terrain. Dissolving ground opens a hole
    /// straight through to the sky, which looks far more broken than the hillside it was hiding;
    /// a camera behind a ridge is already rejected outright by <c>ShotContext.CanSee</c> before
    /// the shot is ever committed. Roads are excluded for the simpler reason that a ribbon lying
    /// flat on the ground never occludes anything.
    /// </para>
    /// </summary>
    public void SetSightlineCut(Vector3 from, Vector3 to, float radius)
    {
        foreach (var m in new[] { _buildingMaterial, _treeMaterial })
        {
            if (m is not ShaderMaterial shader) continue;
            shader.SetShaderParameter("cut_from", from);
            shader.SetShaderParameter("cut_to", to);
            shader.SetShaderParameter("cut_radius", radius);
        }
    }

    /// <summary>
    /// Adds tiles the client did not know about, so they become streamable.
    ///
    /// A client with a partial copy of the world has a manifest listing only what it shipped
    /// with. Merging the server's list is what turns "there is nothing there" into "ask the
    /// server for it"; without it the streamer would never even try, because the LOD rings
    /// skip any tile that is not in <c>_available</c>.
    /// </summary>
    /// <returns>How many tiles were new.</returns>
    public int MergeAvailableTiles(IEnumerable<TileId> tiles)
    {
        int added = 0;
        foreach (var id in tiles)
            if (_available.Add(id)) added++;
        if (added > 0) FitHorizonCoverage();
        return added;
    }

    private void FitHorizonCoverage()
    {
        if (Horizon == null || _available.Count == 0) return;
        Horizon.EnsureCoverage(_available.Min(t => t.E), _available.Max(t => t.E),
            _available.Min(t => t.N), _available.Max(t => t.N));
    }

    /// <summary>Tiles this client believes exist, from local data plus anything merged in.</summary>
    public int AvailableTileCount => _available.Count;

    /// <summary>
    /// Tiles known to exist. Passed to a corridor survey so it never requests one that does not:
    /// a missing asset costs 7.3 s of retries over the network source before it gives up.
    /// </summary>
    public IReadOnlySet<TileId> AvailableTiles => _available;

    /// <summary>
    /// Registers a streaming anchor. Collision is built only around anchors that ask for it -
    /// by default any physics body, since a fly camera or a replay ghost never touches the
    /// ground and a 1001^2 HeightMapShape3D is the most expensive thing a frame can commit.
    /// </summary>
    public void AddAnchor(Node3D anchor, bool? collision = null)
    {
        if (!_anchors.Contains(anchor)) _anchors.Add(anchor);
        if (collision ?? anchor is PhysicsBody3D) _collisionAnchors.Add(anchor);
    }

    public void RemoveAnchor(Node3D anchor)
    {
        _anchors.Remove(anchor);
        _collisionAnchors.Remove(anchor);
    }

    private readonly HashSet<Node3D> _collisionAnchors = new();

    public bool HasAnchor(Node3D anchor) => _anchors.Contains(anchor);

    public int ActiveChunkCount => _chunks.Count;

    /// <summary>Bilinear terrain height at a world position, if that chunk's data is loaded.</summary>
    /// <summary>
    /// True when nothing is loading, building or waiting to be committed.
    ///
    /// <para>
    /// The video exporter waits on this before capturing each frame. Streaming is asynchronous
    /// and deliberately budgeted — a couple of meshes per frame — so a camera that has just
    /// jumped somewhere new renders a hole for a second or two. That is invisible in play and
    /// permanent in a recording.
    /// </para>
    /// </summary>
    public bool Settled
    {
        get
        {
            if (!_ready.IsEmpty) return false;
            if (Interlocked.CompareExchange(ref _buildsInFlight, 0, 0) > 0) return false;
            foreach (var state in _chunks.Values)
                if (state.PendingStride >= 0) return false;
            return true;
        }
    }

    /// <summary>
    /// True when nothing within <paramref name="rings"/> tiles of <paramref name="eye"/> is still
    /// loading, ignoring the rest of the world.
    ///
    /// <para>
    /// <see cref="Settled"/> answers "is anything, anywhere, still loading", which is a far
    /// stronger question than a frame needs: it stalls on tiles nine rings out and behind the
    /// camera. Distance makes the difference small: past the rings the 100 m horizon lattice
    /// is drawn anyway, so a far tile that has not arrived shows the same ridge at a coarser
    /// pitch rather than a hole. (With fog on, the old argument still holds too: the terrain
    /// shader's <c>fog_color</c> and the environment background are the same colour.)
    /// </para>
    /// </summary>
    public bool SettledNear(Vector3 eye, int rings)
    {
        if (_origin == null) return Settled;
        if (!_ready.IsEmpty) return false;

        var centre = _origin.TileAt(eye);
        foreach (var id in _desired)
        {
            if (LodPolicy.Distance(id, centre) > rings) continue;

            // Not started at all. This is the case that matters: EvaluateRings breaks out of its
            // loop when the build cap is reached, so tiles further down the nearest-first order
            // have no state yet — and testing only the states that exist would report a world as
            // settled before most of it had been asked for.
            if (!_chunks.TryGetValue(id, out var state)) return false;

            // Mid-build. StartBuild sets PendingStride on the main thread before the worker
            // starts, so every in-flight build is covered here and _buildsInFlight need not be.
            if (state.PendingStride >= 0) return false;

            if (BuildMeshes && state.ActiveStride < 0) return false;
        }

        return true;
    }

    /// <summary>Why <see cref="Settled"/> is false, for diagnosing a stalled export.</summary>
    public string SettleReport()
    {
        int pending = 0;
        foreach (var state in _chunks.Values) if (state.PendingStride >= 0) pending++;
        return $"tiles={_chunks.Count} ready={_ready.Count} "
            + $"inFlight={Interlocked.CompareExchange(ref _buildsInFlight, 0, 0)} pending={pending} "
            + $"cancelled={CancelledBuilds} horizonBlocks={Horizon?.BlockCount ?? 0}";
    }

    /// <summary>
    /// The same report plus what the nearest unfinished tile is, so a stalled export says which
    /// tile it is waiting for instead of only that it is waiting.
    /// </summary>
    public string SettleReport(Vector3 eye)
    {
        if (_origin == null) return SettleReport();

        var centre = _origin.TileAt(eye);
        int nearest = int.MaxValue;
        TileId worst = centre;
        foreach (var (id, state) in _chunks)
        {
            if (state.PendingStride < 0) continue;
            int d = LodPolicy.Distance(id, centre);
            if (d < nearest) { nearest = d; worst = id; }
        }

        return nearest == int.MaxValue
            ? $"{SettleReport()} nearestPending=none"
            : $"{SettleReport()} nearestPending={worst}@ring{nearest}";
    }

    public bool TryGetHeight(Vector3 worldPos, out float height)
    {
        height = 0f;
        if (_origin == null) return false;
        var (e, n) = _origin.ToLv95(worldPos);
        if (!_chunks.TryGetValue(TileId.FromLv95(e, n), out var state) || state.Grid == null)
            return false;
        // SampleMeshHeight, not SampleHeight: every runtime caller of this - the avatar, the GPX
        // ribbon, the cinema camera's ground and CanSee checks - wants the height of the surface
        // actually on screen, and that surface is flat-shaded triangles, not a bilinear blend of
        // all four quad corners. The two agree to within centimetres at full resolution and
        // diverge by metres on the coarse LOD rings most of a streamed world renders at, which is
        // what put a GPX route's ribbon visibly under the ground the player could see.
        height = (float)state.Grid.SampleMeshHeight(e, n);
        return true;
    }

    /// <summary>
    /// Whether the tile under a world position has its collision shape committed. A body
    /// placed before that falls through the ground it can see.
    /// </summary>
    public bool HasCollisionAt(Vector3 worldPos)
    {
        if (_origin == null) return false;
        var (e, n) = _origin.ToLv95(worldPos);
        return _chunks.TryGetValue(TileId.FromLv95(e, n), out var state) && state.HasCollision;
    }

    /// <summary>Script/debug-friendly variant of TryGetHeight; -inf when unknown.</summary>
    public float GetHeightAt(Vector3 worldPos) =>
        TryGetHeight(worldPos, out float h) ? h : float.NegativeInfinity;

    public override void _Process(double delta)
    {
        if (_source == null || _origin == null) return;

        while (_failedBuilds.TryDequeue(out var failedId))
            if (_chunks.TryGetValue(failedId, out var failed))
            {
                failed.PendingStride = -1;
                failed.PendingCollision = false;
                failed.PendingRoads = false;
                failed.PendingBuildings = false;
            }

        int committed = CommitReadyResults();

        // Re-evaluate the moment a build slot frees, not on the next tick. Builds routinely
        // finish in well under the evaluation interval, so waiting for it left the worker slots
        // idle and capped the whole loader at MaxConcurrentBuilds per interval — 24 tiles a
        // second however fast the disk actually is.
        _sinceEval += delta;
        if (_sinceEval >= EvalInterval || committed > 0)
        {
            _sinceEval = 0;
            EvaluateRings();
        }
    }

    /// <summary>Returns how many results were committed, so the caller knows a slot freed.</summary>
    private int CommitReadyResults()
    {
        int collisionBudget = CollisionCommitsPerFrame;
        int committed = 0;
        int meshCommits = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Peek before dequeuing, and stop only on the budget this particular result actually
        // needs. The previous form required *both* budgets to be positive, so the single
        // allowed collision commit ended the whole loop for that frame — leaving the mesh
        // budget untouched and everything behind it waiting, which throttled the queue to
        // roughly one tile per frame exactly when tiles were arriving fastest.
        // Meshes are budgeted by time, at least one a frame; collision stays a count, because
        // one 1001^2 HeightMapShape3D is the single most expensive thing committed here.
        while (_ready.TryPeek(out var next))
        {
            // every result counts against the time budget - a tail with buildings and 60k
            // trees is as expensive as a surface, and letting those through unbudgeted
            // stacked five of them into one 120 ms frame
            bool overBudget = !OfflineMode && meshCommits > 0
                && clock.Elapsed.TotalMilliseconds >= CommitBudgetMs;
            if (overBudget) break;
            if (next.CollisionMap != null && collisionBudget <= 0) break;
            if (!_ready.TryDequeue(out var result)) break;

            if (!_chunks.TryGetValue(result.Id, out var state))
                continue; // chunk was unloaded while building — drop
            if (result.Generation != state.Generation)
                continue; // cancelled and restarted; this is the stale build's output

            committed++;
            double t0 = clock.Elapsed.TotalMilliseconds;
            state.Grid = result.Grid;
            state.Holes = result.Holes;
            state.HolesLoaded = true;
            state.Cover = result.Cover;
            state.CoverLoaded = true;

            // An interim result is the terrain half of a build whose roads and buildings are
            // still being assembled on the worker. The tile must stay marked pending, or the
            // ring evaluator would start a second build for it while the first is mid-flight.
            if (!result.Interim) { state.PendingStride = -1; state.Cts = null; }

            meshCommits++;
            if (result.Mesh != null)
            {
                EnsureNode(result.Id, state).SetMesh(result.Mesh);
                Horizon?.SetCovered(result.Id, true);
            }
            if (result.CollisionMap != null)
            {
                EnsureNode(result.Id, state).SetCollision(result.CollisionMap);
                state.HasCollision = true;
                state.PendingCollision = false;
                collisionBudget--;
            }
            if (result.BuildingFaces != null)
            {
                if (result.BuildingFaces.Length > 0)
                    EnsureNode(result.Id, state).SetBuildingCollision(result.BuildingFaces);
                state.HasBuildingCollision = true;
            }
            if (result.RoadCollisionFaces != null)
                EnsureNode(result.Id, state).SetRoadCollision(result.RoadCollisionFaces);
            if (result.RoadsRequested)
            {
                if (result.Roads != null)
                    EnsureNode(result.Id, state).SetRoads(result.Roads);
                // tiles with no road data still count as done, so we stop re-requesting
                state.HasRoads = true;
                state.PendingRoads = false;
            }
            if (result.BuildingsRequested)
            {
                if (result.Buildings != null)
                    EnsureNode(result.Id, state).SetBuildings(result.Buildings);
                if (result.Trees != null)
                    EnsureNode(result.Id, state).SetTrees(result.Trees);
                if (result.Water != null)
                    EnsureNode(result.Id, state).SetWater(result.Water);
                state.HasBuildings = true;
                state.PendingBuildings = false;
            }
            state.ActiveStride = result.Stride;
            // a single commit past a frame is worth knowing about: it is what a hitch IS
            double took = clock.Elapsed.TotalMilliseconds - t0;
            if (took > 33)
                GD.Print($"[commit] {result.Id} stride {result.Stride} {(result.Interim ? "interim" : "tail")} took {took:F0} ms");
        }

        return committed;
    }

    private ChunkNode EnsureNode(TileId id, ChunkState state)
    {
        if (state.Node == null)
        {
            state.Node = new ChunkNode
            {
                Name = $"Chunk_{id}",
                Position = _origin!.ToWorld(id.MinE, id.MaxN, 0),
            };
            AddChild(state.Node);
        }
        return state.Node;
    }

    private readonly record struct Want(int Stride, bool Collision, bool Roads, bool Buildings, int Dist);

    /// <summary>
    /// The ring evaluation's expensive half - the square scan, the sort, the unload pass - is
    /// only redone when something it depends on has changed: an anchor's tile, the ring table,
    /// the view sector, the set of known tiles. Between those it walks the cached order, which
    /// at 40 rings is 6,561 dictionary lookups rather than a 6,561-entry sort every 0.1 s and
    /// again after every commit - measured as a 51 ms frame at the largest render distance.
    /// </summary>
    private List<KeyValuePair<TileId, Want>> _ordered = new();
    private string _orderedKey = "";

    private void EvaluateRings()
    {
        var view = ViewDirection;
        // eight sectors: enough to keep "in front of me first" without re-sorting on every
        // degree of mouse movement
        int sector = view == Vector3.Zero ? -1
            : (int)Math.Floor((Math.Atan2(view.Z, view.X) + Math.PI) / (Math.PI / 4)) & 7;
        var keyBuilder = new System.Text.StringBuilder();
        foreach (var anchor in _anchors)
            keyBuilder.Append(_origin!.TileAt(anchor.GlobalPosition)).Append(_collisionAnchors.Contains(anchor) ? 'p' : 'c').Append(';');
        keyBuilder.Append('|').Append(Lod.GetHashCode()).Append('|').Append(sector)
            .Append('|').Append(_available.Count).Append('|').Append(BuildMeshes);
        string key = keyBuilder.ToString();

        if (key != _orderedKey)
        {
            _orderedKey = key;
            RecomputeDesired(view);
        }

        foreach (var (id, want) in _ordered)
        {
            if (!_chunks.TryGetValue(id, out var state))
                _chunks[id] = state = new ChunkState();

            bool needMesh = BuildMeshes && state.ActiveStride != want.Stride;
            bool needCollision = want.Collision && !state.PendingCollision
                && (!state.HasCollision || !state.HasBuildingCollision);
            bool needRoads = want.Roads && !state.HasRoads && !state.PendingRoads;
            bool needBuildings = want.Buildings && !state.HasBuildings && !state.PendingBuildings;
            bool needGrid = state.Grid == null;

            // A fine build for a tile that is now far away is work the player has left behind:
            // a stride-1 tile with roads and buildings takes a second, and its result would be
            // replaced by a 441-vertex one on the next pass anyway. Drop it and start coarse.
            if (state.PendingStride > 0 && state.PendingStride < want.Stride
                && want.Dist > Lod.RoadMaxDist)
            {
                state.CancelPending();
                Interlocked.Increment(ref _cancelledBuilds);
            }

            if ((needMesh || needCollision || needRoads || needBuildings || needGrid)
                && state.PendingStride == -1)
            {
                // Cap how many tiles are being built at once. Every tile's load is a chain —
                // chunk, then holes, then cover, then roads — and when that data is streaming,
                // starting all 361 of them means every chain's first request goes out before
                // any chain's second one. The result is a client that downloads 361 height
                // grids and renders none of them, because not one tile has its cover yet.
                // Letting a few tiles finish completely is what puts ground under your feet.
                if (Interlocked.CompareExchange(ref _buildsInFlight, 0, 0) >= MaxConcurrentBuilds)
                    break;

                Interlocked.Increment(ref _buildsInFlight);
                StartBuild(id, state, want.Stride, needCollision, needRoads, needBuildings);
            }
        }
    }

    private void RecomputeDesired(Vector3 view)
    {
        // desired stride per tile = finest over all anchors (0 = grid-only when meshes are off)
        var desired = new Dictionary<TileId, Want>();
        foreach (var anchor in _anchors)
        {
            var center = _origin!.TileAt(anchor.GlobalPosition);
            int radius = Lod.MaxDist;
            for (int de = -radius; de <= radius; de++)
                for (int dn = -radius; dn <= radius; dn++)
                {
                    var id = new TileId(center.E + de, center.N + dn);
                    if (!_available.Contains(id)) continue;
                    int dist = Math.Max(Math.Abs(de), Math.Abs(dn));
                    int stride = BuildMeshes ? Lod.StrideFor(dist) : 0;
                    if (stride < 0) continue;
                    // Only a body needs ground to stand on. The fly camera and a replay's ghosts
                    // are the fast movers, and a 1001^2 HeightMapShape3D costs ~80 ms to commit -
                    // building nine of them under a camera nothing collides with was the single
                    // biggest hitch in a flight.
                    bool collision = BuildCollision && dist <= Lod.CollisionMaxDist
                        && _collisionAnchors.Contains(anchor);
                    bool roads = BuildMeshes && dist <= Lod.RoadMaxDist;
                    bool buildings = BuildMeshes && dist <= Lod.BuildingMaxDist;
                    // strides are all 0 when meshes are off, otherwise all > 0: min = finest
                    if (desired.TryGetValue(id, out var cur))
                        desired[id] = new Want(Math.Min(cur.Stride, stride), cur.Collision || collision,
                            cur.Roads || roads, cur.Buildings || buildings, Math.Min(cur.Dist, dist));
                    else
                        desired[id] = new Want(stride, collision, roads, buildings, dist);
                }
        }

        // Nearest first. With everything on local disk the order barely matters, but when the
        // data is streaming it decides what the player sees: unordered, the tile underfoot
        // queues behind up to 360 others nine rings out, and you stand in a hole for a minute
        // while the horizon fills in. Tiles behind the camera are pushed three rings back in
        // the queue - what is in front of you is what you are waiting for.
        var primary = _anchors.Count > 0 ? _origin!.TileAt(_anchors[0].GlobalPosition) : default;
        double Priority(TileId id, int dist)
        {
            if (dist <= 2 || view == Vector3.Zero) return dist;
            var to = new Vector3(id.E - primary.E, 0, -(id.N - primary.N)).Normalized();
            return to.Dot(view) < -0.3 ? dist + 3 : dist;
        }
        _ordered = desired.OrderBy(kv => Priority(kv.Key, kv.Value.Dist)).ToList();

        _desired.Clear();
        foreach (var (id, _) in _ordered) _desired.Add(id);

        // unload with hysteresis
        var toRemove = new List<TileId>();
        foreach (var (id, state) in _chunks)
        {
            if (desired.ContainsKey(id)) continue;
            int minDist = int.MaxValue;
            foreach (var anchor in _anchors)
                minDist = Math.Min(minDist, LodPolicy.Distance(id, _origin!.TileAt(anchor.GlobalPosition)));
            if (minDist > Lod.MaxDist + Lod.UnloadSlack)
                toRemove.Add(id);
        }
        foreach (var id in toRemove)
        {
            var gone = _chunks[id];
            // free the worker slot now, not when the chain it is reading finishes
            if (gone.Cts != null)
            {
                gone.CancelPending();
                Interlocked.Increment(ref _cancelledBuilds);
            }
            gone.Node?.QueueFree();
            _chunks.Remove(id);
            Horizon?.SetCovered(id, false);
        }
    }

    /// <summary>Charges the time since the last lap to one stage and restarts the clock.</summary>
    private static void Lap(ref long into, System.Diagnostics.Stopwatch clock)
    {
        Interlocked.Add(ref into, clock.ElapsedMilliseconds);
        clock.Restart();
    }

    private void StartBuild(TileId id, ChunkState state, int stride, bool wantCollision,
        bool wantRoads, bool wantBuildings)
    {
        state.PendingStride = stride;
        state.PendingCollision = wantCollision;
        state.PendingRoads = wantRoads;
        state.PendingBuildings = wantBuildings;
        var cts = state.Cts = new CancellationTokenSource();
        var ct = cts.Token;
        int generation = state.Generation;
        // What has to come off disk depends on what is built on top of the ground, not on how
        // finely the ground itself is drawn: collision, roads, watercourses and building
        // footings all sample the heightfield directly and would be laid onto a 20 m
        // approximation of it. Everything else — the horizon rings, which are 280 of the 361
        // tiles an anchor wants — renders one vertex in ten or twenty and can read the 5 KB
        // companion tile instead of the 490 KB original.
        bool needsFullGrid = wantCollision || wantRoads || wantBuildings
            || stride < ChunkFormat.CoarseStride;

        var cachedGrid = state.Grid;
        if (cachedGrid != null && needsFullGrid && cachedGrid.Stride != 1) cachedGrid = null;

        var cachedHoles = state.Holes;
        bool holesLoaded = state.HolesLoaded;
        var cachedCover = state.Cover;
        bool coverLoaded = state.CoverLoaded;
        var source = _source!;
        bool buildMesh = BuildMeshes && stride > 0;
        bool streaming = Streaming?.Invoke() == true;
        var terrainMaterial = _material;
        var roadMaterial = _roadMaterial;
        var buildingMaterial = _buildingMaterial;
        var waterMaterial = _waterMaterial;
        var treeMaterial = _treeMaterial;

        Task.Run(async () =>
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();

                var grid = cachedGrid;
                if (grid == null && !needsFullGrid)
                {
                    grid = await source.LoadCoarseChunkAsync(id, ct);
                    if (grid != null) Interlocked.Increment(ref _coarseLoads);
                }
                if (grid == null)
                {
                    // Also the path for a region built before .terrc existed, which is why the
                    // absence of a coarse tile is a fallback and not an error.
                    grid = await source.LoadChunkAsync(id, ct);
                    if (grid != null) Interlocked.Increment(ref _fullLoads);
                }
                Lap(ref _msChunk, clock);

                // file missing despite the manifest — release the tile so it is not stuck pending
                if (grid == null) { _failedBuilds.Enqueue(id); return; }
                ct.ThrowIfCancellationRequested();

                var holes = holesLoaded ? cachedHoles : await source.LoadHolesAsync(id, ct);
                var cover = coverLoaded ? cachedCover : await source.LoadCoverAsync(id, ct);
                Lap(ref _msAux, clock);
                ct.ThrowIfCancellationRequested();

                ArrayMesh? mesh = null;
                if (buildMesh && terrainMaterial != null)
                    mesh = ChunkNode.ToArrayMesh(TerrainMeshBuilder.BuildSurface(grid, stride, holes, cover), terrainMaterial);
                Lap(ref _msSurface, clock);

                // The collision map is dequantised here in any case, but whether it is published
                // NOW or only once the road tile has been blended in is the difference between
                // one 80 ms HeightMapShape3D commit per tile and two. From local disk the road
                // tile is milliseconds behind, so the ground waits for it; over the network it
                // may be seconds, and a player standing over a hole is worse than a hitch.
                var collision = wantCollision ? TerrainMeshBuilder.BuildCollisionMap(grid, holes) : null;
                bool publishInterimCollision = collision != null && (!wantRoads || streaming);
                Lap(ref _msCollision, clock);
                ct.ThrowIfCancellationRequested();

                // Publish the ground the moment it exists, before the roads and buildings that
                // sit on it have been fetched and meshed.
                //
                // The tile load is a chain — chunk, holes, cover, roads, buildings — and holding
                // every part of it back until the last link finished is what made a streaming
                // client stand over a hole. Splitting the commit costs nothing: the same files
                // are fetched in the same order on the same worker. The terrain simply stops
                // waiting for the tail. Trying instead to render a *coarse* tile from the height
                // grid alone was measurably worse — it adds a second serialised stage per tile
                // and both stages compete for the same six streaming slots.
                if (mesh != null || publishInterimCollision)
                    _ready.Enqueue(new BuildResult(id, stride, generation, grid, Interim: true,
                        mesh, publishInterimCollision ? collision : null, null, false, holes, cover,
                        null, null, false, null, null));

                ArrayMesh? roads = null;
                RoadTile? roadTile = null;
                if (wantRoads)
                {
                    roadTile = await source.LoadRoadsAsync(id, ct);
                    ct.ThrowIfCancellationRequested();
                    Lap(ref _msRoadLoad, clock);

                    // the grid lets bridge piers and cableway pylons find their footing.
                    // Build returns null for a tile whose road segments are all watercourses
                    // (meshed separately, below) or aerial-only with nothing left to draw.
                    if (roadTile != null && roadMaterial != null
                        && RoadMeshBuilder.Build(roadTile, grid) is { } roadData)
                        roads = ChunkNode.ToArrayMesh(roadData, roadMaterial);
                    Lap(ref _msRoadMesh, clock);
                }

                ChunkNode.TreeMeshes? trees = null;
                ArrayMesh? water = null;
                if (wantBuildings)
                {
                    var treeList = await source.LoadTreesAsync(id, ct);
                    ct.ThrowIfCancellationRequested();
                    if (treeList is { Count: > 0 } && treeMaterial != null)
                    {
                        // a tile's trees lie within its square; the height range is the
                        // terrain's plus the tallest tree, generously
                        var bounds = new Aabb(new Vector3(0, grid.MinHeight - 10, 0),
                            new Vector3(1000, grid.MaxHeight - grid.MinHeight + 80, 1000));
                        trees = ChunkNode.BuildTreeMeshes(ChunkNode.BuildTreeBuffers(treeList), treeMaterial, bounds);
                    }
                    Lap(ref _msTrees, clock);

                    // watercourses ride in the road tile but are meshed here, so a stream gets
                    // the water material instead of being drawn as a narrow blue road
                    if (cover != null && waterMaterial != null
                        && WaterMeshBuilder.Build(grid, cover, roadTile) is { } waterData)
                        water = ChunkNode.ToArrayMesh(waterData, waterMaterial);
                    Lap(ref _msWater, clock);
                }

                // Building collision goes with the terrain's, not with the building meshes: a
                // ConcavePolygonShape3D is a BVH build on the main thread (up to 80 ms for a
                // town tile), and only the tile a body stands on needs one. Empty faces still
                // mark the tile done, so it is not asked again.
                ArrayMesh? buildings = null;
                Vector3[]? buildingFaces = null;
                if (wantBuildings || wantCollision)
                {
                    var bTile = await source.LoadBuildingsAsync(id, ct);
                    ct.ThrowIfCancellationRequested();
                    Lap(ref _msBldgLoad, clock);

                    if (wantBuildings && bTile != null && buildingMaterial != null
                        && BuildingMeshBuilder.Build(bTile) is { } buildingData)
                        buildings = ChunkNode.ToArrayMesh(buildingData, buildingMaterial);
                    if (wantCollision)
                        buildingFaces = bTile != null ? BuildingMeshBuilder.BuildCollisionFaces(bTile) : [];
                    Lap(ref _msBldgMesh, clock);
                }

                // The bare-terrain collision already went out above so the ground never waits on
                // roads to load. Now that the road tile is here, replace it with one blended
                // toward each at-grade corridor's own surveyed height - the "seamless" collision
                // fix - and, when the render stride is fine enough to matter (see
                // TerrainMeshBuilder.MaxHoleStride), rebuild the VISUAL mesh with the same blend
                // at a small clearance below it (see TerrainMeshBuilder.VisualBlendClearance),
                // so the ground a player sees now matches the ground they stand on too, instead
                // of only the physics floor knowing about the road. Two clearances, so two
                // separate blend passes - sharing one array would either z-fight the mesh
                // against the road ribbon (clearance 0) or float the collision floor above the
                // ribbon's own surface (clearance 0.35 m).
                bool nearField = stride <= TerrainMeshBuilder.MaxHoleStride;

                float[]? blendedCollision = publishInterimCollision ? null : collision;
                Vector3[]? bridgeCollision = null;
                if (wantCollision && roadTile != null)
                {
                    var collisionHeights = TerrainMeshBuilder.BuildBlendedHeights(grid, roadTile);
                    blendedCollision = TerrainMeshBuilder.BuildCollisionMap(collisionHeights, holes);
                    // A heightfield cannot hold a deck floating above the terrain it crosses, so
                    // bridges get their own small collision body alongside the blended ground.
                    bridgeCollision = RoadMeshBuilder.BuildBridgeCollisionFaces(roadTile);
                }

                ArrayMesh? tailMesh = null;
                if (buildMesh && terrainMaterial != null && roadTile != null && nearField)
                {
                    var visualHeights = TerrainMeshBuilder.BuildBlendedHeights(
                        grid, roadTile, TerrainMeshBuilder.VisualBlendClearance);
                    // Tunnel portal walls close the mouth from the same hole mask the carve
                    // used, so they need the tile's tunnel geometry - computed once here from
                    // the same source RoadMeshBuilder's own bore extrusion uses, so both agree.
                    var portals = RoadMeshBuilder.ComputeTunnelPortals(roadTile, grid);
                    tailMesh = ChunkNode.ToArrayMesh(
                        TerrainMeshBuilder.BuildSurface(grid, stride, holes, cover, visualHeights, portals),
                        terrainMaterial);
                }

                ct.ThrowIfCancellationRequested();
                _ready.Enqueue(new BuildResult(id, stride, generation, grid, Interim: false,
                    tailMesh, blendedCollision, roads, wantRoads,
                    holes, cover, buildings, buildingFaces, wantBuildings, trees, water,
                    bridgeCollision));
            }
            catch (OperationCanceledException)
            {
                // Not a failure: the tile was cancelled on the main thread, which already reset
                // its pending flags and orphaned this generation's results.
            }
            catch (Exception e)
            {
                _failedBuilds.Enqueue(id);
                GD.PushError($"Chunk build failed for {id}: {e}");
            }
            finally
            {
                // Released whatever happened, or the cap would leak slots on the first
                // failed tile and streaming would stop dead.
                Interlocked.Decrement(ref _buildsInFlight);
            }
        });
    }
}
