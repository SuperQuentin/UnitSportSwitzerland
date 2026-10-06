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
public partial class ChunkManager : Node3D, IOriginContainer, IOriginShiftAware
{
    [Export] public bool BuildMeshes { get; set; } = true;
    [Export] public bool BuildCollision { get; set; } = true;

    private const double PlayEvalInterval = 0.1;

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

    /// <summary>
    /// Main-thread milliseconds a frame may spend turning finished builds into Godot meshes.
    /// A budget in time rather than a count of meshes: a stride-50 horizon tile is 441
    /// vertices and a stride-1 one is a million, and "two per frame" was sized for the latter,
    /// so a teleport spent seconds committing far tiles one pair at a time.
    /// </summary>
    public double CommitBudgetMs { get; set; } = 4;
    private const double VrCommitBudgetMs = 2;

    public LodPolicy Lod { get; set; } = new();

    /// <summary>
    /// Where the player is looking, unit length, or zero for no preference. Tiles outside the
    /// camera's view cone are queued later than tiles inside it - never skipped, just
    /// deferred - so what is on screen does not wait on the ground behind your back.
    /// Set by <see cref="SetView"/>.
    /// </summary>
    public Vector3 ViewDirection { get; private set; }

    /// <summary>Where the view cone starts: the live camera's global position.</summary>
    public Vector3 ViewPosition { get; private set; }

    /// <summary>Half the camera's horizontal field of view, in degrees.</summary>
    public double ViewHalfFovDeg { get; private set; } = 55;

    /// <summary>
    /// How much later a tile straight behind the camera is queued: its ring distance is
    /// multiplied by this, so at 4 a tile four rings behind waits for the sixteenth ring in
    /// front. The weight climbs from 1 at the edge of the view cone over
    /// <see cref="ViewRampDeg"/>, so the sides wait less than the back.
    /// </summary>
    public double ViewBehindWeight { get; set; } = 4;

    /// <summary>Degrees past the cone's edge over which the weight climbs to <see cref="ViewBehindWeight"/>.</summary>
    public double ViewRampDeg { get; set; } = 60;

    /// <summary>
    /// Degrees added to the cone: covers the 16-sector rounding of the view direction (up to
    /// 11.25°) and a little turning, so the tile at the edge of the screen is not the one
    /// left waiting.
    /// </summary>
    private const double ViewMarginDeg = 15;

    /// <summary>
    /// Rings that ignore the view altogether: the ground underfoot, its collision, and what a
    /// turn of the head shows first. A third-person camera can sit across a tile boundary from
    /// its player, and none of these may wait on which way it points.
    /// </summary>
    private const int ViewExemptRings = 2;

    /// <summary>Points the loader's view cone along <paramref name="camera"/>. Main thread, every frame.</summary>
    public void SetView(Camera3D camera)
    {
        ViewPosition = camera.GlobalPosition;
        ViewDirection = -camera.GlobalTransform.Basis.Z;
        var size = camera.GetViewport().GetVisibleRect().Size;
        double aspect = size.Y > 0 ? size.X / size.Y : 16.0 / 9.0;
        double half = Mathf.DegToRad(camera.Fov) / 2;
        // Fov is the vertical angle unless the camera keeps its width
        if (camera.KeepAspect == Camera3D.KeepAspectEnum.Height)
            half = Math.Atan(Math.Tan(half) * aspect);
        ViewHalfFovDeg = Mathf.RadToDeg(half);
    }

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
    private static readonly int PlayMaxConcurrentBuilds = Platform.IsMobile ? MobileMaxConcurrentBuilds : 6;

    /// <summary>
    /// A phone (#63): half its cores, 2 to 4. They are big.LITTLE: more builds than big cores
    /// only heat it and starve the main thread, and every build holds megabytes of grids.
    /// </summary>
    private static readonly int MobileMaxConcurrentBuilds = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 4);

    /// <summary>
    /// Offline the disk and the CPU are the only limits, so use the cores that are there.
    /// Two per core because each build is IO then CPU, and the halves interleave.
    /// </summary>
    private static readonly int OfflineMaxConcurrentBuilds = Platform.IsMobile ? MobileMaxConcurrentBuilds
        : Math.Max(PlayMaxConcurrentBuilds, System.Environment.ProcessorCount * 2);

    private static readonly int LocalMaxConcurrentBuilds = Platform.IsMobile ? MobileMaxConcurrentBuilds
        : Math.Max(PlayMaxConcurrentBuilds, System.Environment.ProcessorCount);

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

    /// <summary>
    /// Raised on the main thread for every commit: tile, stride, kind, ms. Kind is "ground" (an
    /// interim result), "tail", or one collision piece: "coll-height", "coll-road", "coll-bldg".
    /// </summary>
    public event Action<TileId, int, string, double>? CommitLogged;

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
        /// <summary>Its buildings were built as cells with occluders (#553), not as one mesh; and what the build in flight asked for.</summary>
        public bool BuildingCells, PendingCells;
        public bool HasBuildingCollision;

        // Collision waiting for its frame, one piece at a time (see CommitCollisionPieces).
        // Height cells are bits in ChunkNode.CollisionCell order; Done bits are never cleared,
        // so a rebuild keeps the old cell standing until its replacement lands.
        public float[]? QueuedHeight;
        public int HeightCellsQueued, HeightCellsDone;
        public Vector3[][]? QueuedRoadCells, QueuedBuildingCells;
        public int RoadCellsQueued, RoadCellsDone, BuildingCellsQueued;
        public bool CollisionQueued =>
            HeightCellsQueued != 0 || RoadCellsQueued != 0 || BuildingCellsQueued != 0;

        public ChunkGrid? Grid;
        public HashSet<int>? Holes;   // tunnel portals; null until the tile is first loaded
        /// <summary>The tile's tunnel bores (#119), with its collision: a body inside one is not under the ground.</summary>
        public List<(float[] Points, int Count, float Half, float Height)>? Bores;
        public bool HolesLoaded;
        public byte[]? Cover;
        public bool CoverLoaded;
        /// <summary>
        /// The tile's still water (#299; null: none, or not kept). Kept where the cover is (tiles
        /// drawn finer than the coarse stride) and on a server; see <see cref="TryGetWaterLevel"/>.
        /// </summary>
        public WaterLayer? Water;
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
        ArrayMesh? Buildings, Vector3[][]? BuildingFaces, bool BuildingsRequested,
        ChunkNode.TreeMeshes? Trees, ArrayMesh? Water,
        Vector3[][]? RoadCollisionFaces = null, long[]? StageMs = null,
        Interiors.DoorSpot[]? Doors = null,
        List<(float[] Points, int Count, float Half, float Height)>? Bores = null,
        WaterLayer? WaterLayer = null, SignalBuilder.Lamps? Lamps = null,
        ArrayMesh?[]? BuildingCells = null, Vector3[]? OccluderVertices = null, int[]? OccluderIndices = null,
        ArrayMesh? Sites = null, Construction.CraneRig[]? Cranes = null);

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

        if (BuildMeshes && material is ShaderMaterial)
        {
            // its own material instance: the discard rectangle must not touch the tiles
            var horizonMaterial = Styles.StyleKit.Material(Styles.MaterialRole.Terrain);
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
        if (Styles.StyleKit.Detail != Detail)
        {
            Detail = Styles.StyleKit.Detail;
            RebuildVisuals();
        }
        // a headset frame is 11 ms at 90 Hz, and a missed one is warped and smeared over Link (#244)
        CommitBudgetMs = XR.XrSession.Active ? Math.Min(s.CommitBudgetMs, VrCommitBudgetMs) : s.CommitBudgetMs;
        MaxConcurrentBuildsOverride = s.MaxConcurrentBuilds;
        if (Horizon != null)
        {
            Horizon.DistanceM = s.HorizonKm * 1000.0;
            FogUniforms.Apply(Horizon.Material);
        }
        ApplyOcclusion();
        _sinceEval = double.MaxValue;
    }

    /// <summary>
    /// How finely the tile meshes are built: the visual style's (<see cref="Styles.StyleKit.Detail"/>),
    /// taken by <see cref="ApplySettings"/>. Each build reads it once, when it starts.
    /// </summary>
    public Styles.MeshDetail Detail { get; private set; } = Styles.MeshDetail.Low;

    /// <summary>
    /// Material of the piers and jetties (#377), a <see cref="Styles.MaterialRole.Prop"/> one; null:
    /// they are not drawn (their collision still is).
    /// </summary>
    public Material? PierMaterial { get; set; }

    /// <summary>
    /// The landings changed (#377: <c>landings.json</c> streamed in after the tiles round the player
    /// were built): the tiles <paramref name="affected"/> selects build their roads (the piers' mesh)
    /// and their collision (the piers' faces) again. The old ones stay until the new ones land.
    /// </summary>
    public void RebuildPiers(Func<TileId, bool> affected)
    {
        int tiles = 0;
        foreach (var (id, state) in _chunks)
        {
            if (!affected(id)) continue;
            state.HasRoads = false;
            state.HasCollision = false;
            tiles++;
        }
        _sinceEval = double.MaxValue;
        if (tiles > 0) GD.Print($"[terrain] {tiles} tiles build their piers");
    }

    /// <summary>
    /// Builds every tile's meshes again, in place: ground, roads, buildings, trees and water, for
    /// a style with another <see cref="Detail"/> (or <c>/style rebuild</c>). The old meshes stay
    /// drawn until each tile's new ones commit, and its collision is left alone, so the player,
    /// physics and the network session carry on through it. Builds in flight are cancelled: they
    /// would commit the old detail. Main thread.
    /// </summary>
    public void RebuildVisuals()
    {
        if (!BuildMeshes) return;
        int tiles = 0;
        foreach (var state in _chunks.Values)
        {
            if (state.Cts != null)
            {
                state.CancelPending();
                Interlocked.Increment(ref _cancelledBuilds);
            }
            if (state.ActiveStride < 0 && !state.HasRoads && !state.HasBuildings) continue;
            state.ActiveStride = -1;
            state.HasRoads = false;
            state.HasBuildings = false;
            tiles++;
        }
        _sinceEval = double.MaxValue;
        GD.Print($"[terrain] rebuilding {tiles} tiles at {Detail} detail");
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
        // the cut dissolves the buildings between the camera and its subject; their occluders
        // would still hide it (#553)
        bool cut = radius > 0f;
        if (cut != _cutting) { _cutting = cut; ApplyOcclusion(); }
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

    /// <summary>A loaded tile's ground mesh, for per-tile shader parameters (<see cref="PhotoLayer"/>).</summary>
    public MeshInstance3D? GroundAt(TileId id) =>
        _chunks.TryGetValue(id, out var state) ? state.Node?.Ground : null;

    /// <summary>
    /// The buildings with players inside, for the facade shader's occupancy cues (more lit
    /// windows, figures behind the glass). Each box is (world x, world z, half width along the
    /// axis, half depth across it); each axis (cos, sin, how busy 0..1, 0). At most 8.
    /// </summary>
    public void SetOccupancy(Vector4[] boxes, Vector4[] axes, int count)
    {
        _occupancy = (boxes, axes, count);
        if (_buildingMaterial is not ShaderMaterial shader) return;
        shader.SetShaderParameter("occupied_box", boxes);
        shader.SetShaderParameter("occupied_axis", axes);
        shader.SetShaderParameter("occupied_count", Math.Min(count, 8));
    }

    /// <summary>
    /// Open doors whose portal shows (<c>Interiors.DoorPortals.OpenDoors</c>): the building shader
    /// leaves out their baked closed leaf and handle.
    /// </summary>
    public void SetOpenDoors(Vector4[] boxes, Vector4[] axes, int count)
    {
        _openDoors = (boxes, axes, count);
        if (_buildingMaterial is not ShaderMaterial shader) return;
        shader.SetShaderParameter("open_door_box", boxes);
        shader.SetShaderParameter("open_door_axis", axes);
        shader.SetShaderParameter("open_door_count", Math.Min(count, Interiors.DoorPortals.MaxOpenDoors));
    }

    private (Vector4[] Boxes, Vector4[] Axes, int Count)? _occupancy, _openDoors;

    /// <summary>
    /// The origin moved (#185). The tiles are children and have moved already; what remains is
    /// what this keeps in world space elsewhere: the view, the door index, and the boxes the
    /// building shader was handed, which their owners only push again when they change.
    /// </summary>
    public void OnOriginShifted(OriginShift shift)
    {
        ViewPosition = shift.Point(ViewPosition);
        ViewDirection = shift.Direction(ViewDirection);
        Interiors.DoorIndex.Shift(shift);
        if (_occupancy is { } o) SetOccupancy(ShiftBoxes(o.Boxes, shift), ShiftAxes(o.Axes, shift), o.Count);
        if (_openDoors is { } d) SetOpenDoors(ShiftBoxes(d.Boxes, shift), ShiftAxes(d.Axes, shift), d.Count);
    }

    /// <summary>Boxes of (world x, world z, half width, half depth), moved by a shift.</summary>
    private static Vector4[] ShiftBoxes(Vector4[] boxes, OriginShift shift) =>
        boxes.Select(b =>
        {
            var p = shift.Point(new Vector3(b.X, 0, b.Y));
            return new Vector4(p.X, p.Z, b.Z, b.W);
        }).ToArray();

    /// <summary>Axes of (cos, sin, ...) in the XZ plane, turned by a shift.</summary>
    private static Vector4[] ShiftAxes(Vector4[] axes, OriginShift shift) =>
        axes.Select(a =>
        {
            var v = shift.Direction(new Vector3(a.X, 0, a.Y));
            return new Vector4(v.X, v.Z, a.Z, a.W);
        }).ToArray();

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

    /// <summary>
    /// Main thread: a tile has left the building ring and shed its buildings (#553). Whatever
    /// <see cref="TileFurnished"/> hung on them (signs, decorations) goes too; they come back with
    /// the next <see cref="TileFurnished"/> if the tile comes near again.
    /// </summary>
    public event Action<TileId, ChunkNode>? TileUnfurnished;

    /// <summary>
    /// Buildings round the camera drawn in cells with occluders, for occlusion culling (#553). On:
    /// measured three times each way, it shortened the frame at street level (p50 4.4 to 4.1 ms,
    /// p99 5.6 to 5.0 ms, GPU -8 %) and from the air (mean -4 %, GPU -5 %).
    /// <c>--occlusion off</c> turns it off, for comparisons.
    /// </summary>
    public static bool OcclusionCells { get; set; } = Core.CmdArgs.Value("--occlusion", notFlag: true) != "off";

    /// <summary>Tiles within this many of an anchor's draw their buildings in cells.</summary>
    public const int CellRings = 1;

    private bool _cutting;

    /// <summary>An anchor this close above the ground is at street level: building cells round it (#553).</summary>
    public const float StreetEnterM = 40f;

    /// <summary>...and back to one mesh a tile once every anchor is this high (slack, so a hop does not rebuild).</summary>
    public const float StreetLeaveM = 120f;

    private bool _street;

    /// <summary>True while some anchor is near the ground, with hysteresis: where buildings hide things.</summary>
    private bool StreetLevel()
    {
        float lowest = float.MaxValue;
        foreach (var anchor in _anchors)
            if (IsInstanceValid(anchor) && TryGetHeight(anchor.GlobalPosition, out float ground))
                lowest = Math.Min(lowest, anchor.GlobalPosition.Y - ground);
        if (lowest == float.MaxValue) return _street;
        if (_street && lowest > StreetLeaveM) _street = false;
        else if (!_street && lowest < StreetEnterM) _street = true;
        return _street;
    }

    /// <summary>
    /// Occlusion culling on the main view (#553) while the buildings are occluders: off in VR (two
    /// eyes, one occlusion buffer) and while a sightline cut dissolves the buildings in the way.
    /// The portal cameras' viewports never cull: they stand inside the walls.
    /// </summary>
    public void ApplyOcclusion()
    {
        if (!IsInsideTree()) return;
        bool on = OcclusionCells && !_cutting && !XR.XrSession.Active;
        if (on) ProjectSettings.SetSetting("rendering/occlusion_culling/use_occlusion_culling", true);
        GetViewport().UseOcclusionCulling = on;
    }

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

    // ---- debug menu (#339) -------------------------------------------------------------

    /// <summary>The tile layers the debug menu hid on every tile; none in play.</summary>
    public TileLayers HiddenLayers { get; private set; }

    private bool _hideReal, _hideGenerated;

    /// <summary>
    /// Hides tile layers, and whole tiles by where their data comes from, on every loaded tile and
    /// every tile built later. The horizon and the near trees are the caller's: they are nodes of
    /// their own. Main thread.
    /// </summary>
    public void SetDebugHidden(TileLayers layers, bool real, bool generated)
    {
        HiddenLayers = layers;
        _hideReal = real;
        _hideGenerated = generated;
        foreach (var (id, state) in _chunks)
            if (state.Node != null) ApplyDebugHidden(id, state.Node);
    }

    private void ApplyDebugHidden(TileId id, ChunkNode node)
    {
        node.HideLayers(HiddenLayers);
        node.Visible = !(IsGenerated(id) ? _hideGenerated : _hideReal);
    }

    /// <summary>
    /// Stops the rings following the anchors: what is loaded stays loaded, at the stride it has,
    /// so the camera can leave and look at it from outside. Builds already queued still commit.
    /// </summary>
    public bool FreezeRings
    {
        get => _freezeRings;
        set
        {
            _freezeRings = value;
            _sinceEval = double.MaxValue;
        }
    }

    private bool _freezeRings;

    /// <summary>Every loaded tile and the stride its ground is drawn at (-1: not built yet), for the debug overlay.</summary>
    public void ListTiles(List<(TileId Id, int Stride)> into)
    {
        into.Clear();
        foreach (var (id, state) in _chunks) into.Add((id, state.ActiveStride));
    }

    /// <summary>The stride a loaded tile's ground is drawn at; -1 when it is not loaded or not built yet.</summary>
    public int StrideAt(TileId id) => _chunks.TryGetValue(id, out var state) ? state.ActiveStride : -1;

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

    /// <summary>Every streaming anchor, with or without collision. Check each with <c>IsInstanceValid</c>.</summary>
    public IReadOnlyList<Node3D> Anchors => _anchors;

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

    /// <summary>
    /// How many of the tiles within <paramref name="rings"/> of <paramref name="eye"/> are built,
    /// of how many are wanted there: the loading screen's progress bar. Counted the way
    /// <see cref="SettledNear"/> decides, so done == total is the same moment it turns true.
    /// </summary>
    public (int Done, int Total) ProgressNear(Vector3 eye, int rings)
    {
        if (_origin == null) return (0, 0);
        var centre = _origin.TileAt(eye);
        int done = 0, total = 0;
        foreach (var id in _desired)
        {
            if (LodPolicy.Distance(id, centre) > rings) continue;
            total++;
            if (_chunks.TryGetValue(id, out var state) && state.PendingStride < 0
                && (!BuildMeshes || state.ActiveStride >= 0))
                done++;
        }
        return (done, total);
    }

    /// <summary>
    /// How many of the tiles within <paramref name="rings"/> of <paramref name="eye"/> are
    /// playable, of how many are wanted there: the loading screen's bar. Far weaker than
    /// <see cref="ProgressNear"/> on purpose: a tile counts once any mesh is drawn (an interim or
    /// coarse one will do; refinement, roads and buildings stream in while you play), and a tile
    /// a body stands on also needs its collision. The fly camera wants none, so it never waits on it.
    /// </summary>
    public (int Done, int Total) PlayableNear(Vector3 eye, int rings)
    {
        if (_origin == null) return (0, 0);
        var centre = _origin.TileAt(eye);
        int done = 0, total = 0;
        foreach (var (id, want) in _wanted)
        {
            if (LodPolicy.Distance(id, centre) > rings) continue;
            total++;
            if (!_chunks.TryGetValue(id, out var state)) continue;
            if (BuildMeshes && state.ActiveStride < 0) continue;
            if (want.Collision && !state.HasCollision) continue;
            done++;
        }
        return (done, total);
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

    /// <summary>
    /// The still water level at a point (#299): the altitude of the water surface at rest, without
    /// waves (<see cref="World.WaterField.TryLevelAt"/> adds them). False where there is no water, or
    /// the tile's water is not loaded (far tiles drop it with their cover; a server has it only from
    /// a source layer, never the legacy one, which needs the cover). On a legacy tile (#298 not
    /// there yet) the level is the terrain + 0.12 m, so the water there is 0.12 m deep.
    /// </summary>
    public bool TryGetWaterLevel(Vector3 worldPos, out float stillLevel) =>
        TryGetWater(worldPos, out stillLevel, out _);

    /// <summary>
    /// <see cref="TryGetWaterLevel"/> plus the wave scale there, 0..1 (fetch x depth), the factor the
    /// water mesh bakes into its vertices: what <see cref="World.WaterField"/> multiplies the waves by.
    /// </summary>
    public bool TryGetWater(Vector3 worldPos, out float stillLevel, out float waveScale)
    {
        stillLevel = 0f;
        waveScale = 0f;
        if (_origin == null) return false;
        var (e, n) = _origin.ToLv95(worldPos);
        var id = TileId.FromLv95(e, n);
        return _chunks.TryGetValue(id, out var state) && state.Water is { } water
            && water.TrySample(e - id.MinE, id.MaxN - n, out stillLevel, out waveScale);
    }

    /// <summary>
    /// Where something put down here rests (#299): the ground, or the still water surface where
    /// there is water over it. <see cref="TryGetHeight"/> is the terrain, which under a lake is now
    /// its bed: spawns, respawns, drops, birds and the void rescue want this one.
    /// </summary>
    public bool TryGetSurface(Vector3 worldPos, out float height)
    {
        if (!TryGetHeight(worldPos, out height)) return false;
        if (TryGetWaterLevel(worldPos, out float still) && still > height) height = still;
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
    /// Whether a body at <paramref name="worldPos"/> has collision under it within
    /// <paramref name="depth"/> metres (excluding <paramref name="self"/>): the road blend cuts
    /// ramps and streets metres under the raw terrain the safety nets compare against (#119), and a
    /// body standing on that is not under the world.
    /// </summary>
    public bool FloorBelow(Node3D body, Vector3 worldPos, Rid self, float depth = 4f)
    {
        var q = PhysicsRayQueryParameters3D.Create(worldPos + Vector3.Up * 0.5f, worldPos - Vector3.Up * depth);
        q.Exclude = new Godot.Collections.Array<Rid> { self };
        return body.GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0;
    }

    /// <summary>
    /// Whether a world position lies inside a tunnel bore of a tile whose collision is built (#119):
    /// there a body is legitimately under the terrain surface, and no safety net may lift it out.
    /// </summary>
    public bool InTunnel(Vector3 worldPos)
    {
        if (_origin == null) return false;
        var (e, n) = _origin.ToLv95(worldPos);
        var id = TileId.FromLv95(e, n);
        return _chunks.TryGetValue(id, out var state) && state.Bores is { Count: > 0 } bores
            && RoadTunnels.Inside(bores, e - id.MinE, worldPos.Y, id.MaxN - n);
    }

    /// <summary>
    /// Whether the tile under a world position has its collision shape committed. A body
    /// placed before that falls through the ground it can see.
    /// </summary>
    public bool HasCollisionAt(Vector3 worldPos)
    {
        if (_origin == null) return false;
        var (e, n) = _origin.ToLv95(worldPos);
        var id = TileId.FromLv95(e, n);
        if (!_chunks.TryGetValue(id, out var state)) return false;
        // the cell under the point, not the whole tile: cells land one per frame, nearest first,
        // so a body waits only for its own; and a bridge is ground too
        int cell = ChunkNode.CollisionCell((int)(e - id.MinE), (int)(id.MaxN - n));
        return (state.HeightCellsDone & (1 << cell)) != 0
            && ((state.RoadCellsQueued & (1 << cell)) == 0 || (state.RoadCellsDone & (1 << cell)) != 0);
    }

    /// <summary>
    /// Whether the tile under a world position has every cell of its buildings' collision
    /// committed: until then a spot can look free and be inside a house (#517, the on-foot start).
    /// </summary>
    public bool BuildingCollisionDoneAt(Vector3 worldPos)
    {
        if (_origin == null) return false;
        var (e, n) = _origin.ToLv95(worldPos);
        return _chunks.TryGetValue(TileId.FromLv95(e, n), out var state)
            && state.HasBuildingCollision && state.BuildingCellsQueued == 0;
    }

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
        if (!_freezeRings && (_sinceEval >= EvalInterval || committed > 0))
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
        int committed = 0;
        int meshCommits = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Peek before dequeuing, and stop only on the budget this particular result actually
        // needs. The previous form required *both* budgets to be positive, so the single
        // allowed collision commit ended the whole loop for that frame — leaving the mesh
        // budget untouched and everything behind it waiting, which throttled the queue to
        // roughly one tile per frame exactly when tiles were arriving fastest.
        // Meshes are budgeted by time, at least one a frame. Collision is not committed here at
        // all, only queued: see CommitCollisionPieces.
        while (_ready.TryPeek(out var next))
        {
            // every result counts against the time budget - a tail with buildings and 60k
            // trees is as expensive as a surface, and letting those through unbudgeted
            // stacked five of them into one 120 ms frame
            bool overBudget = !OfflineMode && meshCommits > 0
                && clock.Elapsed.TotalMilliseconds >= CommitBudgetMs;
            if (overBudget) break;
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
            // a server keeps every tile's water (it holds only the tiles round players); a client the fine ones
            state.Water = keepCover || !BuildMeshes ? result.WaterLayer : null;

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
            // collision is only queued here; a newer piece replaces a queued older one
            if (result.CollisionMap != null)
            {
                state.QueuedHeight = result.CollisionMap;
                state.HeightCellsQueued = ChunkNode.AllCollisionCells;
                state.PendingCollision = false;
            }
            if (result.BuildingFaces != null)
            {
                // every cell, empty ones too: an empty one clears what a rebuild replaces
                state.QueuedBuildingCells = result.BuildingFaces;
                state.BuildingCellsQueued = ChunkNode.AllCollisionCells;
                state.HasBuildingCollision = true;
            }
            if (result.RoadCollisionFaces != null)
            {
                state.QueuedRoadCells = result.RoadCollisionFaces;
                state.RoadCellsQueued = ChunkNode.AllCollisionCells;
            }
            if (result.Bores != null) state.Bores = result.Bores;
            if (state.CollisionQueued)
            {
                EnsureNode(result.Id, state);
                _collisionQueue.Add(result.Id);
            }
            if (result.RoadsRequested)
            {
                if (result.Roads != null)
                {
                    var node = EnsureNode(result.Id, state);
                    node.SetRoads(result.Roads);
                    node.SetSignalLamps(result.Lamps);
                }
                // tiles with no road data still count as done, so we stop re-requesting
                state.HasRoads = true;
                state.PendingRoads = false;
            }
            if (result.BuildingsRequested)
            {
                if (result.BuildingCells != null)
                    EnsureNode(result.Id, state).SetBuildingCells(result.BuildingCells, result.OccluderVertices, result.OccluderIndices);
                else if (result.Buildings != null)
                    EnsureNode(result.Id, state).SetBuildings(result.Buildings);
                // the building sites' shells (#608), or none: a rebuild without them clears them
                if (result.Sites != null || state.HasBuildings)
                    EnsureNode(result.Id, state).SetSites(result.Sites, result.Cranes, PierMaterial);
                // what was asked for, not what came back: a tile with no buildings at all (the lake)
                // has no cells to show, and recording that as "not cells" rebuilt it forever
                state.BuildingCells = state.PendingCells;
                if (result.Doors != null)
                    Interiors.DoorIndex.SetTile(result.Id, _origin!.ToWorld(result.Id.MinE, result.Id.MaxN, 0), result.Doors);
                TileFurnished?.Invoke(result.Id, EnsureNode(result.Id, state), result.Doors ?? []);
                if (result.Trees != null)
                {
                    var node = EnsureNode(result.Id, state);
                    node.SetTrees(result.Trees);
                    int dist = int.MaxValue;
                    foreach (var anchor in _anchors)
                        dist = Math.Min(dist, LodPolicy.Distance(result.Id, _origin!.TileAt(anchor.GlobalPosition)));
                    node.SetTreeDensity(Lod.TreeDensity(dist));
                }
                state.HasBuildings = true;
                state.PendingBuildings = false;
            }
            // near tiles' water comes with their buildings, far tiles' (flat, #299) on its own
            if (result.Water != null)
                EnsureNode(result.Id, state).SetWater(result.Water);
            // not from an interim whose surface was held back for the tail: the old stride is
            // still what is drawn, and a cancelled tail must leave the tile wanting the new one
            if (!result.Interim || result.Mesh != null || result.Stride == 0)
                state.ActiveStride = result.Stride;
            // a single commit past a frame is worth knowing about: it is what a hitch IS
            double took = clock.Elapsed.TotalMilliseconds - t0;
            CommitLogged?.Invoke(result.Id, result.Stride, result.Interim ? "ground" : "tail", took);
            if (took > 33)
                GD.Print($"[commit] {result.Id} stride {result.Stride} {(result.Interim ? "interim" : "tail")} took {took:F0} ms");
        }

        committed += CommitCollisionPieces(clock);
        _lastCommitMs = clock.Elapsed.TotalMilliseconds;
        _lastCommits = committed;
        return committed;
    }

    /// <summary>Tiles with collision pieces waiting; see <see cref="CommitCollisionPieces"/>.</summary>
    private readonly HashSet<TileId> _collisionQueue = new();
    private readonly List<Vector3> _anchorScratch = new();
    private readonly List<TileId> _staleScratch = new();

    // piece ids: 0-15 height cells, 16-31 road cells, 32-47 building cells
    private const int PieceRoads = ChunkNode.CollisionCellCount, PieceBuildings = 2 * ChunkNode.CollisionCellCount;
    private static readonly string[] PieceKinds = ["coll-height", "coll-road", "coll-bldg"];

    /// <summary>
    /// Commits queued collision one piece per frame (more while the frame's commit budget
    /// lasts), the piece nearest a collision anchor first. A tile's collision used to land in
    /// one frame: a 1001² HeightMapShape3D (~80 ms), the buildings' BVH (up to 80 ms) and the
    /// bridges together. Now the height field is 16 cells of 251², the bridges and buildings
    /// are a piece each, and the cell a body stands in goes first, then its bridge, so
    /// <see cref="HasCollisionAt"/> turns true as early as it can.
    /// </summary>
    private int CommitCollisionPieces(System.Diagnostics.Stopwatch clock)
    {
        if (_collisionQueue.Count == 0) return 0;
        _anchorScratch.Clear();
        foreach (var anchor in _collisionAnchors)
            if (GodotObject.IsInstanceValid(anchor)) _anchorScratch.Add(anchor.GlobalPosition);

        int done = 0;
        while (done == 0 || OfflineMode || clock.Elapsed.TotalMilliseconds < CommitBudgetMs)
        {
            TileId bestId = default;
            ChunkState? best = null;
            int bestPiece = -1;
            float bestKey = float.MaxValue;
            _staleScratch.Clear();
            foreach (var id in _collisionQueue)
            {
                if (!_chunks.TryGetValue(id, out var s) || s.Node == null || !s.CollisionQueued)
                {
                    _staleScratch.Add(id);
                    continue;
                }
                var corner = s.Node.GlobalPosition;
                // the kind breaks a tie in distance: the ground under a body, then the road
                // collision on it (bridge decks), then the buildings
                for (int cell = 0; cell < ChunkNode.CollisionCellCount; cell++)
                {
                    var rect = ChunkNode.CollisionCellRect(cell);
                    if ((s.HeightCellsQueued & (1 << cell)) != 0) Consider(s, id, cell, rect, corner, 0f);
                    if ((s.RoadCellsQueued & (1 << cell)) != 0) Consider(s, id, PieceRoads + cell, rect, corner, 0.5f);
                    if ((s.BuildingCellsQueued & (1 << cell)) != 0) Consider(s, id, PieceBuildings + cell, rect, corner, 1f);
                }
            }
            foreach (var id in _staleScratch) _collisionQueue.Remove(id);
            if (best == null) break;

            double t0 = clock.Elapsed.TotalMilliseconds;
            var node = best.Node!;
            int kind;
            if (bestPiece >= PieceRoads && bestPiece < PieceBuildings)
            {
                int cell = bestPiece - PieceRoads;
                node.SetRoadCell(best.QueuedRoadCells![cell], cell);
                best.RoadCellsQueued &= ~(1 << cell);
                best.RoadCellsDone |= 1 << cell;
                if (best.RoadCellsQueued == 0) best.QueuedRoadCells = null;
                kind = 1;
            }
            else if (bestPiece >= PieceBuildings)
            {
                int cell = bestPiece - PieceBuildings;
                node.SetBuildingCell(best.QueuedBuildingCells![cell], cell);
                best.BuildingCellsQueued &= ~(1 << cell);
                if (best.BuildingCellsQueued == 0) best.QueuedBuildingCells = null;
                kind = 2;
            }
            else
            {
                node.SetCollisionCell(best.QueuedHeight!, bestPiece);
                best.HeightCellsQueued &= ~(1 << bestPiece);
                best.HeightCellsDone |= 1 << bestPiece;
                if (best.HeightCellsQueued == 0) best.QueuedHeight = null;
                if (best.HeightCellsDone == ChunkNode.AllCollisionCells) best.HasCollision = true;
                kind = 0;
            }
            done++;
            double took = clock.Elapsed.TotalMilliseconds - t0;
            CommitLogged?.Invoke(bestId, best.ActiveStride, PieceKinds[kind], took);
            if (took > 33)
                GD.Print($"[commit] {bestId} {PieceKinds[kind]} {bestPiece} took {took:F0} ms");

            void Consider(ChunkState s, TileId id, int piece, Rect2 rect, Vector3 corner, float tie)
            {
                // tile-local metres from the NW corner (EnsureNode's position): x east, z south
                float dist = 0f;
                if (_anchorScratch.Count > 0)
                {
                    dist = float.MaxValue;
                    foreach (var a in _anchorScratch)
                    {
                        float x = a.X - corner.X, z = a.Z - corner.Z;
                        float dx = Math.Max(Math.Max(rect.Position.X - x, x - rect.End.X), 0f);
                        float dz = Math.Max(Math.Max(rect.Position.Y - z, z - rect.End.Y), 0f);
                        dist = Math.Min(dist, MathF.Sqrt(dx * dx + dz * dz));
                    }
                }
                float key = dist + tie;
                if (key >= bestKey) return;
                (bestKey, best, bestId, bestPiece) = (key, s, id, piece);
            }
        }
        return done;
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
            ApplyDebugHidden(id, state.Node);
            AddChild(state.Node);
        }
        return state.Node;
    }

    private readonly record struct Want(int Stride, bool Collision, bool Roads, bool Buildings, int Dist);

    /// <summary>
    /// The ring evaluation's expensive half - the square scan and the unload pass - is only
    /// redone when something it depends on has changed: an anchor's tile, the ring table, the
    /// set of known tiles. The sort is redone on those too, and on its own when the view turns
    /// to another sector or the camera crosses into another tile; that is one key per tile and
    /// an array sort. Between those it walks the cached order, which at 40 rings is 6,561
    /// dictionary lookups rather than a 6,561-entry scan and sort every 0.1 s and again after
    /// every commit - measured as a 51 ms frame at the largest render distance.
    /// </summary>
    private KeyValuePair<TileId, Want>[] _wanted = [];
    private KeyValuePair<TileId, Want>[] _ordered = [];
    private double[] _orderKeys = [];
    /// <summary>
    /// What the desired set was computed from: each anchor's tile and whether it wants collision,
    /// the LOD policy, the world version and <see cref="BuildMeshes"/>. Compared field by field at
    /// 10 Hz, with nothing allocated (it was a string built every evaluation, #221).
    /// </summary>
    private readonly List<(TileId Tile, bool Collision)> _desiredKey = new(), _keyNow = new();
    private (LodPolicy? Lod, long Version, bool Meshes) _desiredKeyRest;

    private bool RingKeyChanged()
    {
        _keyNow.Clear();
        foreach (var anchor in _anchors)
            _keyNow.Add((_origin!.TileAt(anchor.GlobalPosition), _collisionAnchors.Contains(anchor)));
        (LodPolicy? Lod, long Version, bool Meshes) rest = (Lod, _worldVersion, BuildMeshes);
        if (rest == _desiredKeyRest   // the policy by reference: a new one is a new key
            && System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_keyNow).SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_desiredKey)))
            return false;
        _desiredKeyRest = rest;
        _desiredKey.Clear();
        _desiredKey.AddRange(_keyNow);
        return true;
    }
    private ViewCone? _orderedView;

    /// <summary>
    /// The view as the sort sees it: the camera's tile, its heading rounded to one of
    /// <see cref="ViewSectors"/>, and its half field of view rounded to 5°. Coarse on purpose -
    /// it only changes, and the queue is only re-sorted, when one of those does.
    /// <c>Sector</c> is -1 for no preference.
    /// </summary>
    private readonly record struct ViewCone(TileId Tile, int Sector, int HalfFovDeg);

    private const int ViewSectors = 16;

    /// <summary>Half a tile's diagonal, in tiles: how far from its centre a tile still reaches.</summary>
    private const double TileHalfDiagonal = 0.7071;

    private ViewCone CurrentView()
    {
        // LV95 east and north; world -Z is north
        double e = ViewDirection.X, n = -ViewDirection.Z;
        // Nearly straight down (or up) there is no "in front": every heading is as visible as the next.
        if (e * e + n * n < 0.04)
        {
            var primary = _anchors.Count > 0 ? _origin!.TileAt(_anchors[0].GlobalPosition) : default;
            return new ViewCone(primary, -1, 0);
        }
        double step = 2 * Math.PI / ViewSectors;
        int sector = ((int)Math.Round(Math.Atan2(n, e) / step) % ViewSectors + ViewSectors) % ViewSectors;
        return new ViewCone(_origin!.TileAt(ViewPosition), sector, (int)Math.Round(ViewHalfFovDeg / 5) * 5);
    }

    private void EvaluateRings()
    {
        if (RingKeyChanged())
        {
            _orderedView = null;
            RecomputeDesired();
        }

        var view = CurrentView();
        if (_orderedView != view)
        {
            _orderedView = view;
            SortByView(view);
        }

        bool street = OcclusionCells && StreetLevel();
        foreach (var (id, want) in _ordered)
        {
            if (!_chunks.TryGetValue(id, out var state))
            {
                _chunks[id] = state = new ChunkState();
                TileEntered?.Invoke(id);
            }

            bool needMesh = BuildMeshes && state.ActiveStride != want.Stride;
            bool needCollision = want.Collision && !state.PendingCollision && !state.CollisionQueued
                && (!state.HasCollision || !state.HasBuildingCollision);
            bool needRoads = want.Roads && !state.HasRoads && !state.PendingRoads;
            // A tile round the camera draws its buildings in cells with occluders, one farther out
            // as one mesh (#553), and only near the ground: from the air a building hides next to
            // nothing, and flying across a city rebuilt three tiles a crossing for no gain. Rebuilt
            // (buildings only) when it crosses, with a ring of slack so it does not flap.
            bool wantCells = OcclusionCells && street && want.Buildings && want.Dist <= CellRings;
            bool cellSwitch = state.HasBuildings && state.BuildingCells != wantCells
                && (wantCells || want.Dist > CellRings + 1 || !street);
            bool needBuildings = want.Buildings && !state.PendingBuildings && (!state.HasBuildings || cellSwitch);
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
                // Ground rebuilt under roads that are already drawn still needs the road tile:
                // collision blends its floor toward them, and a near-field mesh is lowered under
                // them. The usual ways to get here are flying over an area (roads, no collision)
                // and then dropping on foot, and flying away and back: the tile coarsens with its
                // roads kept (out to RoadMaxDist), and refined without the road tile its stride-1
                // ground came back unblended, burying the roads it had been lowered under.
                bool nearMesh = needMesh && want.Stride <= TerrainMeshBuilder.MaxHoleStride;
                StartBuild(id, state, want.Stride, needCollision, needRoads, needBuildings,
                    roadsForBlend: (needCollision || nearMesh) && want.Roads, fine: fine, cells: wantCells,
                    buildingsOnly: needBuildings && cellSwitch);
            }
        }
    }

    private void RecomputeDesired()
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

        _wanted = desired.ToArray();
        _desired.Clear();
        foreach (var (id, _) in _wanted) _desired.Add(id);
        // a tile's ring changed: so does how many of its trees are drawn
        foreach (var (id, want) in _wanted)
            if (_chunks.TryGetValue(id, out var loaded) && loaded.Node != null)
                loaded.Node.SetTreeDensity(Lod.TreeDensity(want.Dist));

        // A tile past the building or the road ring (and the slack) sheds them (#553). Both used to
        // stay until the tile itself unloaded, 16 rings out at render distance 15: every building
        // and street a flight had passed stayed drawn, and lowering the detail kept them all.
        foreach (var (id, want) in _wanted)
        {
            if (!_chunks.TryGetValue(id, out var shed) || shed.Node is not { } shedNode) continue;
            if (shed.HasBuildings && !shed.PendingBuildings && want.Dist > Lod.BuildingMaxDist + Lod.UnloadSlack)
            {
                shedNode.ClearBuildings();
                shed.HasBuildings = false;
                Interiors.DoorIndex.ClearTile(id);
                TileUnfurnished?.Invoke(id, shedNode);
            }
            if (shed.HasRoads && !shed.PendingRoads && want.Dist > Lod.RoadMaxDist + Lod.UnloadSlack)
            {
                shedNode.ClearRoads();
                shed.HasRoads = false;
            }
        }

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

    /// <summary>
    /// Orders the wanted tiles: nearest ring first, and tiles off screen later.
    ///
    /// <para>
    /// With everything on local disk the order barely matters, but when the data is streaming it
    /// decides what the player sees: unordered, the tile underfoot queues behind up to 360
    /// others nine rings out, and you stand in a hole for a minute while the horizon fills in.
    /// The same goes for direction - what is in front of you is what you are waiting for, and
    /// the ground behind your back is already drawn by the horizon lattice. So a tile's ring
    /// distance is multiplied by a weight: 1 while any of it is inside the camera's view cone
    /// (horizontal field of view plus <see cref="ViewMarginDeg"/>), climbing to
    /// <see cref="ViewBehindWeight"/> over <see cref="ViewRampDeg"/> past its edge. The angle
    /// is taken from the camera, not the player, which a third-person or fly camera can be far
    /// from, and the innermost <see cref="ViewExemptRings"/> rings are never weighted.
    /// </para>
    /// </summary>
    private void SortByView(ViewCone view)
    {
        double step = 2 * Math.PI / ViewSectors;
        double dirE = Math.Cos(view.Sector * step), dirN = Math.Sin(view.Sector * step);
        double camE = view.Tile.E + 0.5, camN = view.Tile.N + 0.5;
        double edge = view.HalfFovDeg + ViewMarginDeg;

        if (_orderKeys.Length != _wanted.Length) _orderKeys = new double[_wanted.Length];
        var ordered = (KeyValuePair<TileId, Want>[])_wanted.Clone();
        for (int i = 0; i < ordered.Length; i++)
        {
            var (id, want) = ordered[i];
            double de = id.E + 0.5 - camE, dn = id.N + 0.5 - camN;
            double r = Math.Sqrt(de * de + dn * dn);
            double weight = 1;
            if (view.Sector >= 0 && want.Dist > ViewExemptRings && r > TileHalfDiagonal)
            {
                // the angle to the tile's nearest edge, not its centre: a tile half on screen is on screen
                double off = Mathf.RadToDeg(Math.Acos(Math.Clamp((de * dirE + dn * dirN) / r, -1, 1))
                    - Math.Asin(TileHalfDiagonal / r)) - edge;
                if (off > 0) weight = 1 + (ViewBehindWeight - 1) * Math.Min(1, off / ViewRampDeg);
            }
            // nearest first among equals, so a re-sort does not reshuffle them
            _orderKeys[i] = want.Dist * weight + r * 1e-3;
        }
        Array.Sort(_orderKeys, ordered);
        _ordered = ordered;
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

    /// <summary>
    /// A tile's still water (#299): the source's water layer when it has one (fixture courses, #298's
    /// file), else the legacy layer derived from the cover raster, which needs the full grid. Null
    /// when the tile has no water, or when only a coarse grid and no source layer is at hand.
    /// </summary>
    public static async Task<WaterLayer?> LoadWaterLayerAsync(IChunkSource source, TileId id, ChunkGrid grid,
        byte[]? cover, CancellationToken ct)
    {
        if (await source.LoadWaterAsync(id, ct) is { } tile) return WaterLayer.Create(tile, grid);
        return cover != null && grid.Stride == 1 ? WaterLayer.FromCover(grid, cover) : null;
    }

    private static void Release(BuildResult r)
    {
        r.Mesh?.Dispose();
        r.Roads?.Dispose();
        r.Buildings?.Dispose();
        r.Sites?.Dispose();
        if (r.Cranes != null)
            foreach (var crane in r.Cranes) crane.Dispose();
        if (r.BuildingCells != null)
            foreach (var cell in r.BuildingCells) cell?.Dispose();
        r.Water?.Dispose();
        r.Trees?.Dispose();
    }

    private void StartBuild(TileId id, ChunkState state, int stride, bool wantCollision,
        bool wantRoads, bool wantBuildings, bool roadsForBlend = false, bool fine = false, bool cells = false,
        bool buildingsOnly = false)
    {
        if (fine) Interlocked.Increment(ref _fineBuildsInFlight);
        state.PendingStride = stride;
        state.BuildStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        state.GroundRecorded = false;
        state.PendingCollision = wantCollision;
        state.PendingRoads = wantRoads;
        state.PendingBuildings = wantBuildings;
        if (wantBuildings) state.PendingCells = cells;
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
        var cachedWater = state.Water;
        // a coarser mesh is on screen: the new one can wait for its road blend (see the interim publish)
        bool groundShown = state.ActiveStride > 0 && state.Node != null;
        var source = _source!;
        bool buildMesh = BuildMeshes && stride > 0;
        bool streaming = Streaming?.Invoke() == true;
        var terrainMaterial = _material;
        var roadMaterial = _roadMaterial;
        var buildingMaterial = _buildingMaterial;
        var waterMaterial = _waterMaterial;
        var treeMaterial = _treeMaterial;
        var pierMaterial = PierMaterial;
        // the landings as of now (#377): their piers ride in the roads mesh and the road collision
        var landings = World.Landings.Current;
        var detail = Detail;

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
                // still water (#299): the source's layer, else the legacy one from the cover
                var waterLayer = cachedWater ?? await LoadWaterLayerAsync(source, id, grid, cover, ct);
                Lap(StAux, stageMs, clock);
                ct.ThrowIfCancellationRequested();

                // the core (grid + skirts, no cut walls) is kept: the road-blended tail patches a
                // copy of it rather than building a million vertices again
                ArrayMesh? mesh = null;
                TerrainMeshBuilder.MeshData? surfaceCore = null;
                if (buildMesh && terrainMaterial != null)
                {
                    surfaceCore = TerrainMeshBuilder.BuildSurfaceCore(grid, stride, holes, cover, detail: detail);
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
                bool blendsRoads = wantRoads || roadsForBlend;
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
                // Except when the tile already shows ground and this one will be lowered under
                // its roads in the tail: refining it here drew the bare surface over the roads
                // for half a second on the way back to an area, and the coarser mesh it replaces
                // is no hole. It is held back and goes out with the tail instead.
                bool nearField = stride <= TerrainMeshBuilder.MaxHoleStride;
                var heldMesh = groundShown && blendsRoads && nearField ? mesh : null;
                var interimMesh = heldMesh == null ? mesh : null;
                if (interimMesh != null || publishInterimCollision)
                    _ready.Enqueue(new BuildResult(id, stride, generation, grid, Interim: true,
                        interimMesh, publishInterimCollision ? collision : null, null, false, holes, cover,
                        null, null, false, null, null, WaterLayer: waterLayer));

                ArrayMesh? roads = null;
                SignalBuilder.Lamps? lamps = null;   // traffic-light lenses (#350)
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
                        roads = ChunkNode.ToArrayMesh(roadData, roadMaterial, RoadPaintBuilder.Build(roadTile));
                        lamps = SignalBuilder.BuildLamps(roadTile);
                    }
                    // the piers and jetties standing in the tile (#377): one more surface
                    if (pierMaterial != null && PierMeshBuilder.Build(landings, id, grid, mesh: true, collision: false) is { Mesh: { } pierData })
                        roads = ChunkNode.WithPiers(roads, pierData, pierMaterial);
                    Lap(StRoadMesh, stageMs, clock);
                }
                else if (roadsForBlend)
                {
                    // only for the collision and mesh blends below; the roads already drawn stay as they
                    // are, since the result says it did not request them
                    roadTile = await source.LoadRoadsAsync(id, ct);
                    ct.ThrowIfCancellationRequested();
                    Lap(StRoadLoad, stageMs, clock);
                }

                ChunkNode.TreeMeshes? trees = null;
                ArrayMesh? water = null;
                // a switch to or from building cells (#553) redoes the buildings, not the trees and
                // the water committed with them, which stay where they are
                if (wantBuildings && !buildingsOnly)
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
                        trees = ChunkNode.BuildTreeMeshes(buffers, treeMaterial, bounds, detail);
                    }
                    Lap(StTrees, stageMs, clock);

                    // watercourses ride in the road tile but are meshed here, so a stream gets
                    // the water material instead of being drawn as a narrow blue road
                    if (waterMaterial != null
                        && WaterMeshBuilder.Build(waterLayer, roadTile, stride, detail) is { } waterData)
                    {
                        ct.ThrowIfCancellationRequested();
                        water = ChunkNode.ToArrayMesh(waterData, waterMaterial);
                    }
                    Lap(StWater, stageMs, clock);
                }
                else if (buildMesh && waterMaterial != null && waterLayer is { Legacy: false }
                    && WaterMeshBuilder.Build(waterLayer, null, stride, detail) is { } farWater)
                {
                    // a far tile with a source water layer (#298's lakes over their beds): a flat
                    // surface at the still level as coarse as its ground, or the lake is a pit
                    ct.ThrowIfCancellationRequested();
                    water = ChunkNode.ToArrayMesh(farWater, waterMaterial);
                    Lap(StWater, stageMs, clock);
                }

                // Building collision goes with the terrain's, not with the building meshes: a
                // ConcavePolygonShape3D is a BVH build on the main thread (up to 80 ms for a
                // town tile), and only the tile a body stands on needs one. Empty faces still
                // mark the tile done, so it is not asked again.
                ArrayMesh? buildings = null;
                ArrayMesh?[]? buildingCells = null;
                (Vector3[] Vertices, int[] Indices)? occluders = null;
                Vector3[][]? buildingFaces = null;
                Interiors.DoorSpot[]? doors = null;
                ArrayMesh? siteMesh = null;
                Construction.CraneRig[]? craneRigs = null;
                Vector3[]? siteFaces = null;
                if (wantBuildings || wantCollision)
                {
                    var bTile = await source.LoadBuildingsAsync(id, ct);
                    ct.ThrowIfCancellationRequested();
                    Lap(StBldgLoad, stageMs, clock);

                    // the building sites (#605): planned from the tile and its roads, the same on
                    // every peer, their shells drawn in place of their solids (#608)
                    if (bTile != null && Construction.SitePlans.HasSite(bTile))
                    {
                        var siteRoads = roadTile ?? await source.LoadRoadsAsync(id, ct);
                        ct.ThrowIfCancellationRequested();
                        var sites = Construction.SitePlans.For(bTile, siteRoads);
                        bool siteMeshWanted = wantBuildings && pierMaterial != null;
                        if (Construction.SiteShellBuilder.Build(bTile, sites, grid, siteMeshWanted, wantCollision) is { } shells)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (shells.Mesh is { } shellData) siteMesh = ChunkNode.ToPropMesh(shellData, pierMaterial!);
                            siteFaces = shells.Faces;
                            // what the cranes slew (#610): meshes made here, nodes on the main thread
                            if (siteMeshWanted && shells.Cranes.Count > 0)
                                craneRigs = shells.Cranes.Select(c => Construction.CraneRig.Make(c, pierMaterial!)).ToArray();
                        }
                    }

                    if (wantBuildings && bTile != null && buildingMaterial != null)
                    {
                        // Doors face the street, so they need the road tile even when the roads
                        // themselves are already drawn; it is cached, and without it the door
                        // choice would depend on load order and disagree between peers.
                        var doorRoads = roadTile ?? await source.LoadRoadsAsync(id, ct);
                        ct.ThrowIfCancellationRequested();
                        doors = Interiors.BuildingFootprint.ComputeDoors(bTile, doorRoads, grid.Stride == 1 ? grid : null);
                        if (BuildingMeshBuilder.Build(bTile, doors, detail) is { } buildingData)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (cells)
                            {
                                var parts = BuildingOcclusion.SplitByCell(buildingData);
                                buildingCells = new ArrayMesh?[parts.Length];
                                for (int c = 0; c < parts.Length; c++)
                                    if (parts[c] is { } part) buildingCells[c] = ChunkNode.ToArrayMesh(part, buildingMaterial);
                                occluders = BuildingOcclusion.Occluders(bTile);
                            }
                            else buildings = ChunkNode.ToArrayMesh(buildingData, buildingMaterial);
                        }
                    }
                    if (wantCollision)
                        buildingFaces = ChunkNode.SplitByCell(bTile != null ? BuildingMeshBuilder.BuildCollisionFaces(bTile) : []);
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
                bool visualBlend = surfaceCore != null && roadTile != null && nearField;

                // one corridor pass, applied twice at different clearances
                var blend = roadTile != null && (wantCollision || visualBlend)
                    ? TerrainMeshBuilder.ComputeRoadBlend(roadTile) : null;

                float[]? blendedCollision = null;
                Vector3[][]? bridgeCollision = null;
                List<(float[] Points, int Count, float Half, float Height)>? bores = null;
                if (wantCollision && roadTile != null)
                {
                    blendedCollision = TerrainMeshBuilder.BuildCollisionMap(grid, holes, blend);
                    // A heightfield cannot hold a deck floating above the terrain it crosses, so
                    // bridges get their own small collision body alongside the blended ground.
                    // and retaining walls (#125): a heightfield cannot stand a vertical face either,
                    // nor a railing (#126), nor a kerb (#119)
                    bridgeCollision = ChunkNode.SplitByCell([.. RoadMeshBuilder.BuildBridgeCollisionFaces(roadTile),
                        .. RoadWallBuilder.BuildCollisionFaces(roadTile), .. RailingBuilder.BuildCollisionFaces(roadTile),
                        .. IslandBuilder.BuildCollisionFaces(roadTile),   // roundabout islands (#122)
                        .. RoadSignBuilder.BuildCollisionFaces(roadTile),   // sign poles (#121)
                        .. ParkingBuilder.BuildCollisionFaces(roadTile),   // car park props (#499)
                        .. SignalBuilder.BuildCollisionFaces(roadTile),     // traffic-light poles (#350)
                        .. RoadStreetBuilder.BuildCollisionFaces(roadTile)]);   // sidewalks and kerbs (#119)
                    bores = RoadTunnels.Bores(roadTile);
                }
                else if (wantCollision && !publishInterimCollision)
                    blendedCollision = TerrainMeshBuilder.BuildCollisionMap(grid, holes); // no road tile after all
                // piers and jetties (#377) are walked on like a bridge deck and stop a boat
                if (wantCollision && PierMeshBuilder.Build(landings, id, grid, mesh: false, collision: true) is { Faces.Length: > 0 } piers)
                {
                    var cells = ChunkNode.SplitByCell(piers.Faces);
                    if (bridgeCollision == null) bridgeCollision = cells;
                    else
                        for (int c = 0; c < cells.Length; c++)
                            if (cells[c].Length > 0) bridgeCollision[c] = [.. bridgeCollision[c], .. cells[c]];
                }
                // and so are the building sites' slabs, flights and scaffold lifts (#608)
                if (wantCollision && siteFaces is { Length: > 0 })
                {
                    var cells = ChunkNode.SplitByCell(siteFaces);
                    if (bridgeCollision == null) bridgeCollision = cells;
                    else
                        for (int c = 0; c < cells.Length; c++)
                            if (cells[c].Length > 0) bridgeCollision[c] = [.. bridgeCollision[c], .. cells[c]];
                }

                ArrayMesh? tailMesh = null;
                if (visualBlend)
                {
                    // Tunnel portal walls close the mouth from the same hole mask the carve
                    // used, so they need the tile's tunnel geometry - computed once here from
                    // the same source RoadMeshBuilder's own bore extrusion uses, so both agree.
                    var portals = RoadMeshBuilder.ComputeTunnelPortals(roadTile!, grid);
                    bool hasCorridors = blend!.Cells.Length > 0;
                    bool hasPortalWalls = holes is { Count: > 0 } && portals.Count > 0;
                    // Nothing to change on a tile with no at-grade road and no portal: the
                    // surface already committed is the final one.
                    if (hasCorridors || hasPortalWalls)
                    {
                        var core = hasCorridors
                            ? TerrainMeshBuilder.PatchSurface(surfaceCore!, grid, stride, cover, blend,
                                TerrainMeshBuilder.VisualBlendClearance)
                            : surfaceCore!;
                        ct.ThrowIfCancellationRequested();
                        tailMesh = ChunkNode.ToArrayMesh(
                            TerrainMeshBuilder.FinishSurface(core, grid, stride, holes, portals), terrainMaterial!);
                    }
                }

                // a held-back surface goes out now: as it is when the tail found nothing to blend
                if (heldMesh != null)
                {
                    if (tailMesh == null) tailMesh = heldMesh;
                    else heldMesh.Dispose();
                }

                Lap(StTail, stageMs, clock);
                ct.ThrowIfCancellationRequested();
                _ready.Enqueue(new BuildResult(id, stride, generation, grid, Interim: false,
                    tailMesh, blendedCollision, roads, wantRoads,
                    holes, cover, buildings, buildingFaces, wantBuildings, trees, water,
                    bridgeCollision, stageMs, doors, bores, waterLayer, lamps,
                    buildingCells, occluders?.Vertices, occluders?.Indices, siteMesh, craneRigs));
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
