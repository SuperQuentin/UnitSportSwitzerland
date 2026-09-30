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
    /// <summary>The LV95 origin of world space, once initialised.</summary>
    public WorldOrigin? Origin => _origin;
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

    /// <summary>
    /// Builds finer than the coarse stride, counted apart from the total. A stride-1 build holds
    /// ~300 MB while it runs (the surface, its road-blended copy, and the native copies handed to
    /// the RenderingServer), against a few MB for a far tile. Flying fast keeps every slot busy
    /// with fine tiles, and 32 of them at once exhausted memory and crashed the process in the
    /// native allocator. Capping only these keeps the far rings loading at full parallelism.
    /// </summary>
    private int _fineBuildsInFlight;
    private const int MaxFineBuilds = 6;
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
    private readonly long[] _stageMs = new long[StageNames.Length];

    /// <summary>Build stages in the order a worker runs them; indexes into every stage array.</summary>
    public static readonly string[] StageNames =
    {
        "chunk", "holes+cover", "surface", "collision", "road-read", "road-mesh",
        "trees", "water", "bldg-read", "bldg-mesh", "blend+tail",
    };
    private const int StChunk = 0, StAux = 1, StSurface = 2, StCollision = 3, StRoadLoad = 4,
        StRoadMesh = 5, StTrees = 6, StWater = 7, StBldgLoad = 8, StBldgMesh = 9, StTail = 10;

    /// <summary>One finished tile build, for the session recorder.</summary>
    public readonly record struct BuildLog(TileId Id, int Stride, int Dist, double GroundMs,
        double CompleteMs, bool Collision, bool Roads, bool Buildings, long[] StageMs);

    /// <summary>Raised on the main thread when a build's last result is committed.</summary>
    public event Action<BuildLog>? BuildLogged;

    /// <summary>Raised on the main thread for every result committed: tile, stride, interim, ms.</summary>
    public event Action<TileId, int, bool, double>? CommitLogged;

    /// <summary>The current cap on builds in flight, so a saturated loader can be recognised.</summary>
    public int BuildCap => MaxConcurrentBuilds;
    private readonly (string, double)[] _stageAvgScratch = new (string, double)[StageNames.Length];

    private int _completedBuilds;
    private double _lastCommitMs;
    private int _lastCommits;
    private readonly LatencyWindow _groundLatency = new(128);
    private readonly LatencyWindow _completeLatency = new(128);

    /// <summary>
    /// Build start -> first visible surface ("ground") and -> last result committed ("complete"),
    /// both measured on the main thread, so they include queueing behind the commit budget.
    /// </summary>
    private void RecordLatency(ChunkState state, BuildResult result)
    {
        if (state.BuildStartedTicks == 0) return;
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(state.BuildStartedTicks).TotalMilliseconds;
        if (!state.GroundRecorded && result.Mesh != null)
        {
            _groundLatency.Add(ms);
            state.GroundRecorded = true;
            state.GroundMs = ms;
        }
        if (result.Interim) return;
        _completeLatency.Add(ms);
        _completedBuilds++;
        state.BuildStartedTicks = 0;

        if (BuildLogged != null)
        {
            int dist = int.MaxValue;
            foreach (var anchor in _anchors)
                dist = Math.Min(dist, LodPolicy.Distance(result.Id, _origin!.TileAt(anchor.GlobalPosition)));
            BuildLogged(new BuildLog(result.Id, result.Stride, dist,
                state.GroundRecorded ? state.GroundMs : double.NaN, ms,
                result.CollisionMap != null, result.RoadsRequested, result.BuildingsRequested,
                result.StageMs ?? new long[StageNames.Length]));
        }
    }

    /// <summary>A snapshot of the loader for the performance overlay. Main thread only.</summary>
    public PerfStats GetPerfStats()
    {
        int pending = 0;
        foreach (var state in _chunks.Values) if (state.PendingStride >= 0) pending++;
        int builds = Math.Max(1, _completedBuilds);
        // reused, so the recorder can ask every frame without making garbage
        var stages = _stageAvgScratch;
        for (int i = 0; i < StageNames.Length; i++)
            stages[i] = (StageNames[i], Volatile.Read(ref _stageMs[i]) / (double)builds);
        return new PerfStats(
            Loaded: _chunks.Count, Desired: _desired.Count, Pending: pending,
            InFlight: Volatile.Read(ref _buildsInFlight), ReadyQueue: _ready.Count,
            Cancelled: CancelledBuilds, Completed: _completedBuilds,
            FullLoads: Volatile.Read(ref _fullLoads), CoarseLoads: Volatile.Read(ref _coarseLoads),
            HorizonBlocks: Horizon?.BlockCount ?? 0,
            GroundP50: _groundLatency.Percentile(0.5), GroundP95: _groundLatency.Percentile(0.95),
            CompleteP50: _completeLatency.Percentile(0.5), CompleteP95: _completeLatency.Percentile(0.95),
            LastCommitMs: _lastCommitMs, LastCommits: _lastCommits,
            StageAvgMs: stages);
    }

    /// <summary>The last N samples, for percentiles that follow what is loading now.</summary>
    private sealed class LatencyWindow(int size)
    {
        private readonly double[] _values = new double[size];
        private readonly double[] _scratch = new double[size];
        private int _count, _next;

        public void Add(double v)
        {
            _values[_next] = v;
            _next = (_next + 1) % _values.Length;
            _count = Math.Min(_count + 1, _values.Length);
        }

        public double Percentile(double p)
        {
            if (_count == 0) return double.NaN;
            Array.Copy(_values, _scratch, _count);
            Array.Sort(_scratch, 0, _count);
            return _scratch[Math.Min(_count - 1, (int)(p * _count))];
        }
    }

    /// <summary>Where the worker time went, longest first. Empty before anything has been built.</summary>
    public string BuildTimeReport()
    {
        var stages = StageNames.Select((name, i) => (Name: name, Ms: Volatile.Read(ref _stageMs[i]))).ToArray();

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

        /// <summary>Stopwatch timestamp of the build in flight, 0 when idle; for the perf overlay.</summary>
        public long BuildStartedTicks;
        public bool GroundRecorded;
        public double GroundMs;

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
        Vector3[]? RoadCollisionFaces = null, long[]? StageMs = null,
        Interiors.DoorSpot[]? Doors = null);

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
        // render distance means nothing to it: it keeps a small fixed radius of tiles, and only
        // the 5 KB coarse companions — nothing on the server builds on the ground (no meshes,
        // no collision, players are proxies), and 2 MB full grids made its memory grow ~50 MB
        // per player spread out across the country.
        if (BuildMeshes) ApplySettings(GameSettings.Current);
        else Lod = new LodPolicy { Rings = new LodPolicy.Ring[] { new(ServerGridRadius, ChunkFormat.CoarseStride) } };
    }

    /// <summary>Tiles of height data a server keeps around each player (coarse, 5 KB each).</summary>
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

    /// <summary>A loaded tile's building collision body, if it has one.</summary>
    public StaticBody3D? BuildingBodyAt(TileId id) =>
        _chunks.TryGetValue(id, out var state) ? state.Node?.BuildingBody : null;

    /// <summary>
    /// The buildings with players inside, for the facade shader's occupancy cues (more lit
    /// windows, figures behind the glass). Each box is (world x, world z, half width along the
    /// axis, half depth across it); each axis (cos, sin, how busy 0..1, 0). At most 8.
    /// </summary>
    public void SetOccupancy(Vector4[] boxes, Vector4[] axes, int count)
    {
        if (_buildingMaterial is not ShaderMaterial shader) return;
        shader.SetShaderParameter("occupied_box", boxes);
        shader.SetShaderParameter("occupied_axis", axes);
        shader.SetShaderParameter("occupied_count", Math.Min(count, 8));
    }

    /// <summary>
    /// Adds tiles the client did not know about, so they become streamable.
    ///
    /// A client with a partial copy of the world has a manifest listing only what it shipped
    /// with. Merging the server's list is what turns "there is nothing there" into "ask the
    /// server for it"; without it the streamer would never even try, because the LOD rings
    /// skip any tile that is not in <c>_available</c>.
    ///
    /// <para>
    /// With a generated fill, the new tiles stop being generated and the generated tiles round
    /// them blend differently, so those are unloaded — not rebuilt in place: a commit whose roads,
    /// buildings or trees are null leaves the old ones standing, which would put generated houses
    /// on real ground — their cached assets dropped, and <see cref="TerrainReplaced"/> raised
    /// for them. Main thread only.
    /// </para>
    /// </summary>
    /// <returns>How many tiles were new.</returns>
    public int MergeAvailableTiles(IEnumerable<TileId> tiles)
    {
        var added = new HashSet<TileId>();
        foreach (var id in tiles)
            if (_available.Add(id)) added.Add(id);
        if (added.Count == 0) return 0;

        if (_fallback != null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _fallback.SetReal(_available);
            // a tile whose blend window holds a new real tile: ProceduralWorld.BlendWindow's reach
            bool Affected(TileId id)
            {
                if (added.Contains(id)) return true;
                if (_available.Contains(id)) return false;   // real before, and unchanged
                for (int dn = -BlendReachTiles; dn <= BlendReachTiles; dn++)
                    for (int de = -BlendReachTiles; de <= BlendReachTiles; de++)
                        if (added.Contains(new TileId(id.E + de, id.N + dn))) return true;
                return false;
            }
            ReplaceTiles(Affected);
            GD.Print($"[terrain] {added.Count} real tile(s) merged into the generated fill "
                + $"in {clock.Elapsed.TotalMilliseconds:F0} ms");
        }
        else
        {
            _worldVersion++;
            FitHorizonCoverage();
        }
        return added.Count;
    }

    // ---- the generated fill ------------------------------------------------------------------

    /// <summary>Tiles either side a generated tile's blend looks at (<see cref="ProceduralWorld.BlendWindow"/>).</summary>
    private const int BlendReachTiles = 4;

    private FallbackChunkSource? _fallback;
    private Action<Func<TileId, bool>?>? _invalidate;

    /// <summary>Bumped whenever the set of tiles that exist changes; part of the ring evaluator's key.</summary>
    private long _worldVersion;

    /// <summary>The generated fill, when there is one (<c>--generated off</c> leaves it null).</summary>
    public FallbackChunkSource? Fallback => _fallback;

    /// <summary>Whether a tile is drawn from generated rather than real data, as of now.</summary>
    public bool IsGenerated(TileId id) => _fallback?.Covers(id) == true;

    /// <summary>
    /// Raised on the main thread after tiles were unloaded because the world under them changed,
    /// for anything that cached what it read from them (road surfaces, streams, trees to gather,
    /// lane graphs). The argument selects the affected tiles; null means all of them.
    /// </summary>
    public event Action<Func<TileId, bool>?>? TerrainReplaced;

    /// <summary>
    /// Main thread: a tile's buildings, doors and trees have just committed, for systems that
    /// dress a tile (occasion decorations). The node is the tile's own, so anything parented to it
    /// unloads with it. Doors are tile-local, like <see cref="Interiors.DoorSpot.Position"/>.
    /// </summary>
    public event Action<TileId, ChunkNode, Interiors.DoorSpot[]>? TileFurnished;

    /// <summary>Main thread: a tile has just come into the streamed rings (nothing is built yet).</summary>
    public event Action<TileId>? TileEntered;

    /// <summary>Main thread: a tile has been unloaded and its node freed.</summary>
    public event Action<TileId>? TileUnloaded;

    /// <summary>
    /// Generates every tile the real set does not have, inside the fallback's domain.
    /// <paramref name="invalidate"/> drops the selected tiles (all when null) from whatever cache
    /// sits above the source, since this class does not know the chain it was handed.
    /// </summary>
    public void UseFallback(FallbackChunkSource fallback, Action<Func<TileId, bool>?> invalidate)
    {
        _fallback = fallback;
        _invalidate = invalidate;
        var snap = fallback.SetReal(_available);
        _worldVersion++;
        _sinceEval = double.MaxValue;
        FitHorizonCoverage();
        // Initialize only deferred the horizon's first read: this is it, now with the fill
        Horizon?.Reload();
        GD.Print($"[terrain] generated fill {(snap.Enabled ? "on" : "off")}: "
            + $"{_available.Count} real tiles, domain {snap.Bounds}");
    }

    /// <summary>
    /// Switches the generated fill on or off while running: every generated tile is unloaded (or
    /// every missing one starts generating) and the horizon is rebuilt.
    /// </summary>
    public void SetFallbackEnabled(bool enabled)
    {
        if (_fallback == null || _fallback.Current.Enabled == enabled) return;
        _fallback.SetReal(_available, enabled);
        ReplaceTiles(id => !_available.Contains(id));
        GD.Print($"[terrain] generated fill {(enabled ? "on" : "off")}");
    }

    /// <summary>Unloads, uncaches and re-announces the tiles <paramref name="affected"/> selects.</summary>
    private void ReplaceTiles(Func<TileId, bool> affected)
    {
        foreach (var id in _chunks.Keys.Where(affected).ToList()) UnloadTile(id);
        _invalidate?.Invoke(affected);
        _worldVersion++;
        _sinceEval = double.MaxValue;
        FitHorizonCoverage();
        Horizon?.Reload();
        TerrainReplaced?.Invoke(affected);
    }

    /// <summary>
    /// Throws the whole world away — every tile, every cached asset and blend, the horizon — for
    /// a rebase, which changes what every world coordinate means. <paramref name="moveOrigin"/>
    /// runs once nothing placed against the old origin is left; the rings then rebuild everything
    /// round the new one. Main thread only.
    /// </summary>
    public void ResetAll(Action? moveOrigin = null)
    {
        foreach (var id in _chunks.Keys.ToList()) UnloadTile(id);
        _desired.Clear();
        _ordered.Clear();
        _orderedKey = "";
        _worldVersion++;
        _sinceEval = double.MaxValue;
        _invalidate?.Invoke(null);
        _fallback?.ClearBlends();
        Horizon?.Clear();
        moveOrigin?.Invoke();
        // the coverage texture is placed in world space: after the move, not before
        FitHorizonCoverage();
        Horizon?.Reload();
        GD.Print("[terrain] world reset");
        TerrainReplaced?.Invoke(null);
    }

    /// <summary>
    /// Sizes the horizon's coverage texture to every tile that can be drawn: the real set and the
    /// generated fill's domain.
    /// </summary>
    private void FitHorizonCoverage()
    {
        if (Horizon == null) return;
        int minE = int.MaxValue, minN = int.MaxValue, maxE = int.MinValue, maxN = int.MinValue;
        foreach (var t in _available)
        {
            minE = Math.Min(minE, t.E);
            maxE = Math.Max(maxE, t.E);
            minN = Math.Min(minN, t.N);
            maxN = Math.Max(maxN, t.N);
        }
        if (_fallback?.Current is { Enabled: true } snap)
        {
            var b = snap.Bounds;
            minE = Math.Min(minE, b.MinE);
            maxE = Math.Max(maxE, b.MaxE);
            minN = Math.Min(minN, b.MinN);
            maxN = Math.Max(maxN, b.MaxN);
        }
        if (minE > maxE) return;
        Horizon.EnsureCoverage(minE, maxE, minN, maxN);
    }

    /// <summary>Real or generated: whether the rings may ask for a tile.</summary>
    private bool IsAvailable(TileId id) => _available.Contains(id) || _fallback?.Covers(id) == true;

    /// <summary>
    /// Real tiles this client believes exist, from local data plus anything merged in. Generated
    /// ones are not counted: this answers "does this client have a world of its own?".
    /// </summary>
    public int AvailableTileCount => _available.Count;

    /// <summary>
    /// Real tiles known to exist. Passed to a corridor survey so it never requests one that does not:
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

    /// <summary>The anchors that want collision around them (<see cref="World.TreeColliders"/> follows them too).</summary>
    public IEnumerable<Node3D> CollisionAnchors => _collisionAnchors.Where(GodotObject.IsInstanceValid);

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

    /// <summary>
    /// The land-cover class under a point, for tiles loaded at a fine stride (the ones around a
    /// player — far tiles drop their cover once meshed). False when that is not known.
    /// </summary>
    public bool TryGetCover(Vector3 worldPos, out CoverClass cover)
    {
        cover = CoverClass.Open;
        if (_origin == null) return false;
        var (e, n) = _origin.ToLv95(worldPos);
        var id = TileId.FromLv95(e, n);
        if (!_chunks.TryGetValue(id, out var state) || state.Cover is not { } raster) return false;
        int col = (int)Math.Round(e - id.MinE), row = (int)Math.Round(id.MaxN - n);
        if ((uint)col >= ChunkFormat.GridSize || (uint)row >= ChunkFormat.GridSize) return false;
        int i = row * ChunkFormat.GridSize + col;
        if (i >= raster.Length) return false;
        cover = (CoverClass)raster[i];
        return true;
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

    private long _allocatedAtLastCollect;
    private const long CollectEveryBytes = 1L << 30;

    /// <summary>
    /// Every tile build throws away multi-MB arrays, which live on the large-object heap and are
    /// only reclaimed by a gen-2 collection - and the GC, seeing no pressure, deferred that for a
    /// whole 40 s flight while the dead arrays piled up from 1 to 4.4 GB (8.3 GB process peak at
    /// 300 m/s, and an out-of-memory crash in the native allocator on a smaller machine budget).
    /// A background gen-2 per GB allocated keeps the heap near its live size; it runs concurrently
    /// with the game, so the main-thread pause is the short marking phases only.
    /// </summary>
    private void CollectBuildGarbage()
    {
        long allocated = GC.GetTotalAllocatedBytes();
        if (allocated - _allocatedAtLastCollect < CollectEveryBytes) return;
        _allocatedAtLastCollect = allocated;
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
    }

    public override void _Process(double delta)
    {
        if (_source == null || _origin == null) return;
        CollectBuildGarbage();

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

    /// <summary>
    /// Cancels every build and waits, briefly, for the workers to let go.
    ///
    /// <para>
    /// A worker makes Godot objects as it goes (the ArrayMeshes and MultiMeshes it hands to the
    /// main thread), and one that does so after the engine has begun tearing down is an access
    /// violation in <c>ArrayMesh..ctor</c>, not an exception — the process dies on quit. It took
    /// something always building at the moment of quitting to show it, which the generated
    /// fallback world is. Each worker checks its token right before it touches Godot, so after
    /// the cancel it either stops at that check or is already inside the call, and this wait
    /// lets that finish while the engine is still alive.
    /// </para>
    /// </summary>
    public override void _ExitTree()
    {
        foreach (var state in _chunks.Values) state.CancelPending();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _buildsInFlight) > 0 && clock.ElapsedMilliseconds < 3000)
            Thread.Sleep(5);
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

            if (!_chunks.TryGetValue(result.Id, out var state)
                || result.Generation != state.Generation)
            {
                // unloaded while building, or a cancelled build's stale output. Free its
                // resources now: their memory is native, invisible to the GC, and would
                // otherwise sit there until a finalizer got round to it.
                Release(result);
                continue;
            }

            committed++;
            double t0 = clock.Elapsed.TotalMilliseconds;
            state.Grid = result.Grid;
            state.Holes = result.Holes;
            state.HolesLoaded = true;
            // The cover raster is 1001^2 bytes whatever the stride, so keeping it on every tile
            // cost 1 MB per tile - ~1 GB at 15 rings, 6.5 GB at 40 - for tiles drawn one vertex
            // in ten or fifty. Only tiles rendered finer than the coarse stride keep it; a far
            // tile that comes back closer gets it from CachingChunkSource or disk (~2 KB deflated).
            bool keepCover = result.Stride > 0 && result.Stride < ChunkFormat.CoarseStride;
            state.Cover = keepCover ? result.Cover : null;
            state.CoverLoaded = keepCover;

            // An interim result is the terrain half of a build whose roads and buildings are
            // still being assembled on the worker. The tile must stay marked pending, or the
            // ring evaluator would start a second build for it while the first is mid-flight.
            if (!result.Interim) { state.PendingStride = -1; state.Cts = null; }
            RecordLatency(state, result);

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
                if (result.Doors != null)
                    Interiors.DoorIndex.SetTile(result.Id, _origin!.ToWorld(result.Id.MinE, result.Id.MaxN, 0), result.Doors);
                TileFurnished?.Invoke(result.Id, EnsureNode(result.Id, state), result.Doors ?? []);
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
            CommitLogged?.Invoke(result.Id, result.Stride, result.Interim, took);
            if (took > 33)
                GD.Print($"[commit] {result.Id} stride {result.Stride} {(result.Interim ? "interim" : "tail")} took {took:F0} ms");
        }

        _lastCommitMs = clock.Elapsed.TotalMilliseconds;
        _lastCommits = committed;
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
            .Append('|').Append(_worldVersion).Append('|').Append(BuildMeshes);
        string key = keyBuilder.ToString();

        if (key != _orderedKey)
        {
            _orderedKey = key;
            RecomputeDesired(view);
        }

        foreach (var (id, want) in _ordered)
        {
            if (!_chunks.TryGetValue(id, out var state))
            {
                _chunks[id] = state = new ChunkState();
                TileEntered?.Invoke(id);
            }

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
                // skip, not break: coarse tiles further down the order still fit
                bool fine = want.Stride > 0 && want.Stride < ChunkFormat.CoarseStride;
                if (fine && !OfflineMode && Volatile.Read(ref _fineBuildsInFlight) >= MaxFineBuilds)
                    continue;

                Interlocked.Increment(ref _buildsInFlight);
                // Collision on a road tile needs the road tile even when its roads are already
                // drawn: the floor is blended toward them. The usual way to get here is exactly
                // that - flying over an area (roads, no collision) and then dropping on foot.
                StartBuild(id, state, want.Stride, needCollision, needRoads, needBuildings,
                    roadsForCollision: needCollision && want.Roads, fine: fine);
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
                    if (!IsAvailable(id)) continue;
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
        foreach (var id in toRemove) UnloadTile(id);
    }

    private void UnloadTile(TileId id)
    {
        var gone = _chunks[id];
        // free the worker slot now, not when the chain it is reading finishes
        if (gone.Cts != null)
        {
            gone.CancelPending();
            Interlocked.Increment(ref _cancelledBuilds);
        }
        gone.Node?.ReleaseResources();
        gone.Node?.QueueFree();
        _chunks.Remove(id);
        Horizon?.SetCovered(id, false);
        Interiors.DoorIndex.ClearTile(id);
        TileUnloaded?.Invoke(id);
    }

    /// <summary>
    /// Charges the time since the last lap to one stage — in the session totals and in this
    /// build's own breakdown — and restarts the clock.
    /// </summary>
    private void Lap(int stage, long[] build, System.Diagnostics.Stopwatch clock)
    {
        long ms = clock.ElapsedMilliseconds;
        Interlocked.Add(ref _stageMs[stage], ms);
        build[stage] += ms;
        clock.Restart();
    }

    private static void Release(BuildResult r)
    {
        r.Mesh?.Dispose();
        r.Roads?.Dispose();
        r.Buildings?.Dispose();
        r.Water?.Dispose();
        r.Trees?.Conifers?.Dispose();
        r.Trees?.Broadleaves?.Dispose();
    }

    private void StartBuild(TileId id, ChunkState state, int stride, bool wantCollision,
        bool wantRoads, bool wantBuildings, bool roadsForCollision = false, bool fine = false)
    {
        if (fine) Interlocked.Increment(ref _fineBuildsInFlight);
        state.PendingStride = stride;
        state.BuildStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        state.GroundRecorded = false;
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
        // A server (no meshes) builds at stride 0 and only answers height queries: the coarse
        // companion does, real or generated. Reading full grids there cost 2 MB per tile held.
        bool needsFullGrid = wantCollision || wantRoads || wantBuildings
            || (BuildMeshes && stride < ChunkFormat.CoarseStride);

        var cachedGrid = state.Grid;
        if (cachedGrid != null && needsFullGrid && cachedGrid.Stride != 1) cachedGrid = null;
        // ...and the other way: a tile that has drifted out of the full-grid rings swaps its
        // 2 MB grid for the 5 KB companion, or every tile the player has ever walked past keeps
        // one. SampleMeshHeight on the coarse grid matches what is now drawn there anyway.
        if (cachedGrid != null && !needsFullGrid && cachedGrid.Stride == 1) cachedGrid = null;

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
                var stageMs = new long[StageNames.Length];

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
                Lap(StChunk, stageMs, clock);

                // file missing despite the manifest — release the tile so it is not stuck pending
                if (grid == null) { _failedBuilds.Enqueue(id); return; }
                ct.ThrowIfCancellationRequested();

                // a headless server draws nothing: holes and the 1 MB cover raster are for meshes
                var holes = holesLoaded || !BuildMeshes ? cachedHoles : await source.LoadHolesAsync(id, ct);
                var cover = coverLoaded || !BuildMeshes ? cachedCover : await source.LoadCoverAsync(id, ct);
                Lap(StAux, stageMs, clock);
                ct.ThrowIfCancellationRequested();

                // the core (grid + skirts, no cut walls) is kept: the road-blended tail patches a
                // copy of it rather than building a million vertices again
                ArrayMesh? mesh = null;
                TerrainMeshBuilder.MeshData? surfaceCore = null;
                if (buildMesh && terrainMaterial != null)
                {
                    surfaceCore = TerrainMeshBuilder.BuildSurfaceCore(grid, stride, holes, cover);
                    // checked right before every Godot object this worker makes: see _ExitTree
                    ct.ThrowIfCancellationRequested();
                    mesh = ChunkNode.ToArrayMesh(
                        TerrainMeshBuilder.FinishSurface(surfaceCore, grid, stride, holes, null), terrainMaterial);
                }
                Lap(StSurface, stageMs, clock);

                // The collision map is dequantised here in any case, but whether it is published
                // NOW or only once the road tile has been blended in is the difference between
                // one 80 ms HeightMapShape3D commit per tile and two. From local disk the road
                // tile is milliseconds behind, so the ground waits for it; over the network it
                // may be seconds, and a player standing over a hole is worse than a hitch.
                // Only built here when it is published now; otherwise the tail builds the blended
                // one directly, and a bare map made here would be thrown away unused.
                // A tile whose roads are drawn already still blends its floor toward them: waiting
                // for the (cached) road tile beats publishing bare terrain, which sits the whole
                // drape offset - 0.35 m plus - under every road and path, so the player walked
                // around sunk to the ankles in them.
                bool blendsRoads = wantRoads || roadsForCollision;
                bool publishInterimCollision = wantCollision && (!blendsRoads || streaming);
                var collision = publishInterimCollision ? TerrainMeshBuilder.BuildCollisionMap(grid, holes) : null;
                Lap(StCollision, stageMs, clock);
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
                    Lap(StRoadLoad, stageMs, clock);

                    // the grid lets bridge piers and cableway pylons find their footing.
                    // Build returns null for a tile whose road segments are all watercourses
                    // (meshed separately, below) or aerial-only with nothing left to draw.
                    if (roadTile != null && roadMaterial != null
                        && RoadMeshBuilder.Build(roadTile, grid) is { } roadData)
                    {
                        ct.ThrowIfCancellationRequested();
                        roads = ChunkNode.ToArrayMesh(roadData, roadMaterial);
                    }
                    Lap(StRoadMesh, stageMs, clock);
                }
                else if (roadsForCollision)
                {
                    // only for the collision blend below; the roads already drawn stay as they
                    // are, since the result says it did not request them
                    roadTile = await source.LoadRoadsAsync(id, ct);
                    ct.ThrowIfCancellationRequested();
                    Lap(StRoadLoad, stageMs, clock);
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
                        var buffers = ChunkNode.BuildTreeBuffers(treeList);
                        ct.ThrowIfCancellationRequested();
                        trees = ChunkNode.BuildTreeMeshes(buffers, treeMaterial, bounds);
                    }
                    Lap(StTrees, stageMs, clock);

                    // watercourses ride in the road tile but are meshed here, so a stream gets
                    // the water material instead of being drawn as a narrow blue road
                    if (cover != null && waterMaterial != null
                        && WaterMeshBuilder.Build(grid, cover, roadTile) is { } waterData)
                    {
                        ct.ThrowIfCancellationRequested();
                        water = ChunkNode.ToArrayMesh(waterData, waterMaterial);
                    }
                    Lap(StWater, stageMs, clock);
                }

                // Building collision goes with the terrain's, not with the building meshes: a
                // ConcavePolygonShape3D is a BVH build on the main thread (up to 80 ms for a
                // town tile), and only the tile a body stands on needs one. Empty faces still
                // mark the tile done, so it is not asked again.
                bool nearField = stride <= TerrainMeshBuilder.MaxHoleStride;
                bool visualBlend = surfaceCore != null && roadTile != null && nearField;

                // one corridor pass, applied twice at different clearances
                var blend = roadTile != null && (wantCollision || visualBlend)
                    ? TerrainMeshBuilder.ComputeRoadBlend(roadTile) : null;

                ArrayMesh? buildings = null;
                Vector3[]? buildingFaces = null;
                Interiors.DoorSpot[]? doors = null;
                // the ground carved under drive-in garages that this tile's holes do not have yet
                bool newGarageCells = false;
                if (wantBuildings || wantCollision)
                {
                    var bTile = await source.LoadBuildingsAsync(id, ct);
                    ct.ThrowIfCancellationRequested();
                    Lap(StBldgLoad, stageMs, clock);

                    // A garage's drive-in bay cuts its facade, its collision and the ground under it
                    // alike, and hangs off its door: a collision-only build computes the doors too,
                    // from the same roads and grid, so the hole and the wall around it agree.
                    bool drawBuildings = wantBuildings && bTile != null && buildingMaterial != null;
                    bool garages = bTile != null && bTile.Buildings.Any(b => b.Kind == BuildingKind.Garage);
                    Interiors.DoorSpot[]? allDoors = null;
                    if (bTile != null && (drawBuildings || (wantCollision && garages)))
                    {
                        // Doors face the street, so they need the road tile even when the roads
                        // themselves are already drawn; it is cached, and without it the door
                        // choice would depend on load order and disagree between peers.
                        var doorRoads = roadTile ?? await source.LoadRoadsAsync(id, ct);
                        ct.ThrowIfCancellationRequested();
                        allDoors = Interiors.BuildingFootprint.ComputeDoors(bTile, doorRoads, grid.Stride == 1 ? grid : null);
                    }
                    var bays = allDoors?.Where(d => d.Bay != null).Select(d => d.Bay!).ToList();
                    bool hasBays = bays is { Count: > 0 } && grid.Stride == 1;
                    var fileHoles = holes;

                    if (drawBuildings)
                    {
                        doors = allDoors;
                        var ground = hasBays
                            ? TerrainMeshBuilder.GroundHeights(grid, visualBlend ? blend : null, TerrainMeshBuilder.VisualBlendClearance)
                            : null;
                        var groundColor = ground != null ? TerrainMeshBuilder.GroundColors(cover, ground) : null;
                        if (BuildingMeshBuilder.Build(bTile!, doors, ground, fileHoles, groundColor) is { } buildingData)
                        {
                            ct.ThrowIfCancellationRequested();
                            buildings = ChunkNode.ToArrayMesh(buildingData, buildingMaterial!);
                        }
                    }
                    if (wantCollision)
                        buildingFaces = bTile != null
                            ? BuildingMeshBuilder.BuildCollisionFaces(bTile, allDoors,
                                hasBays ? TerrainMeshBuilder.GroundHeights(grid, blend, 0.0) : null, fileHoles)
                            : [];

                    if (hasBays)
                    {
                        var cells = Interiors.GarageBay.HoleCells(bays!);
                        newGarageCells = holes == null || !cells.IsSubsetOf(holes);
                        if (newGarageCells)
                        {
                            // a copy: the loaded set may be shared through the tile cache
                            if (holes != null) cells.UnionWith(holes);
                            holes = cells;
                        }
                    }
                    Lap(StBldgMesh, stageMs, clock);
                }

                // The bare-terrain collision already went out above so the ground never waits on
                // roads to load. Now that the road tile is here, replace it with one blended
                // toward each at-grade corridor's own surveyed height - the "seamless" collision
                // fix - and, when the render stride is fine enough to matter (see
                // TerrainMeshBuilder.MaxHoleStride), rebuild the VISUAL mesh with the same blend
                // at a small clearance below it (see TerrainMeshBuilder.VisualBlendClearance),
                // so the ground a player sees now matches the ground they stand on too, instead
                // of only the physics floor knowing about the road. Two clearances but ONE
                // corridor pass: the blend is a sparse list of (cell, weight, road height) and
                // each consumer applies its own clearance - 0 for collision, 0.35 m for the mesh,
                // or the mesh z-fights the road ribbon. The mesh is patched, not rebuilt: only
                // the vertices under corridors move (measured ~33% of all worker time when it
                // rebuilt every vertex of a stride-1 tile a second time).

                float[]? blendedCollision = null;
                Vector3[]? bridgeCollision = null;
                if (wantCollision && roadTile != null)
                {
                    blendedCollision = TerrainMeshBuilder.BuildCollisionMap(grid, holes, blend);
                    // A heightfield cannot hold a deck floating above the terrain it crosses, so
                    // bridges get their own small collision body alongside the blended ground.
                    bridgeCollision = RoadMeshBuilder.BuildBridgeCollisionFaces(roadTile);
                }
                else if (wantCollision && (!publishInterimCollision || newGarageCells))
                    blendedCollision = TerrainMeshBuilder.BuildCollisionMap(grid, holes); // no road tile after all

                ArrayMesh? tailMesh = null;
                // the interim surface was built before the garages' cells were known
                bool garageRebuild = newGarageCells && surfaceCore != null && nearField;
                if (visualBlend || garageRebuild)
                {
                    // Tunnel portal walls close the mouth from the same hole mask the carve
                    // used, so they need the tile's tunnel geometry - computed once here from
                    // the same source RoadMeshBuilder's own bore extrusion uses, so both agree.
                    var portals = roadTile != null ? RoadMeshBuilder.ComputeTunnelPortals(roadTile, grid) : [];
                    bool hasCorridors = visualBlend && blend!.Cells.Length > 0;
                    bool hasPortalWalls = holes is { Count: > 0 } && portals.Count > 0;
                    // Nothing to change on a tile with no at-grade road and no portal: the
                    // surface already committed is the final one.
                    if (hasCorridors || hasPortalWalls || garageRebuild)
                    {
                        var baseCore = garageRebuild
                            ? TerrainMeshBuilder.BuildSurfaceCore(grid, stride, holes, cover)
                            : surfaceCore!;
                        var core = hasCorridors
                            ? TerrainMeshBuilder.PatchSurface(baseCore, grid, stride, cover, blend!,
                                TerrainMeshBuilder.VisualBlendClearance)
                            : baseCore;
                        ct.ThrowIfCancellationRequested();
                        tailMesh = ChunkNode.ToArrayMesh(
                            TerrainMeshBuilder.FinishSurface(core, grid, stride, holes, portals), terrainMaterial!);
                    }
                }

                Lap(StTail, stageMs, clock);
                ct.ThrowIfCancellationRequested();
                _ready.Enqueue(new BuildResult(id, stride, generation, grid, Interim: false,
                    tailMesh, blendedCollision, roads, wantRoads,
                    holes, cover, buildings, buildingFaces, wantBuildings, trees, water,
                    bridgeCollision, stageMs, doors));
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
                if (fine) Interlocked.Decrement(ref _fineBuildsInFlight);
            }
        });
    }
}
