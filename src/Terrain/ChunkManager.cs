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

    private const double EvalInterval = 0.25;
    private const int MeshCommitsPerFrame = 2;
    private const int CollisionCommitsPerFrame = 1;

    public LodPolicy Lod { get; set; } = new();

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
    private const int MaxConcurrentBuilds = 6;

    private int _buildsInFlight;
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
        public ChunkGrid? Grid;
        public HashSet<int>? Holes;   // tunnel portals; null until the tile is first loaded
        public bool HolesLoaded;
        public byte[]? Cover;
        public bool CoverLoaded;
        public ChunkNode? Node;
    }

    private readonly record struct BuildResult(
        TileId Id, int Stride, ChunkGrid Grid, bool Interim,
        TerrainMeshBuilder.MeshData? Mesh, float[]? CollisionMap,
        RoadMeshBuilder.MeshData? Roads, bool RoadsRequested,
        HashSet<int>? Holes, byte[]? Cover,
        BuildingMeshBuilder.MeshData? Buildings, Vector3[]? BuildingFaces, bool BuildingsRequested,
        List<TreeInstance>? Trees, WaterMeshBuilder.MeshData? Water);

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
        return added;
    }

    /// <summary>Tiles this client believes exist, from local data plus anything merged in.</summary>
    public int AvailableTileCount => _available.Count;

    public void AddAnchor(Node3D anchor) => _anchors.Add(anchor);

    public void RemoveAnchor(Node3D anchor) => _anchors.Remove(anchor);

    public int ActiveChunkCount => _chunks.Count;

    /// <summary>Bilinear terrain height at a world position, if that chunk's data is loaded.</summary>
    public bool TryGetHeight(Vector3 worldPos, out float height)
    {
        height = 0f;
        if (_origin == null) return false;
        var (e, n) = _origin.ToLv95(worldPos);
        if (!_chunks.TryGetValue(TileId.FromLv95(e, n), out var state) || state.Grid == null)
            return false;
        height = (float)state.Grid.SampleHeight(e, n);
        return true;
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
        int meshBudget = MeshCommitsPerFrame;
        int collisionBudget = CollisionCommitsPerFrame;
        int committed = 0;

        // Peek before dequeuing, and stop only on the budget this particular result actually
        // needs. The previous form required *both* budgets to be positive, so the single
        // allowed collision commit ended the whole loop for that frame — leaving the mesh
        // budget untouched and everything behind it waiting, which throttled the queue to
        // roughly one tile per frame exactly when tiles were arriving fastest.
        while (_ready.TryPeek(out var next))
        {
            if (next.Mesh != null && meshBudget <= 0) break;
            if (next.CollisionMap != null && collisionBudget <= 0) break;
            if (!_ready.TryDequeue(out var result)) break;

            if (!_chunks.TryGetValue(result.Id, out var state))
                continue; // chunk was unloaded while building — drop

            committed++;
            state.Grid = result.Grid;
            state.Holes = result.Holes;
            state.HolesLoaded = true;
            state.Cover = result.Cover;
            state.CoverLoaded = true;

            // An interim result is the terrain half of a build whose roads and buildings are
            // still being assembled on the worker. The tile must stay marked pending, or the
            // ring evaluator would start a second build for it while the first is mid-flight.
            if (!result.Interim) state.PendingStride = -1;

            if (result.Mesh != null)
            {
                EnsureNode(result.Id, state).SetMesh(result.Mesh, _material!);
                meshBudget--;
            }
            if (result.CollisionMap != null)
            {
                EnsureNode(result.Id, state).SetCollision(result.CollisionMap);
                state.HasCollision = true;
                state.PendingCollision = false;
                collisionBudget--;
            }
            if (result.RoadsRequested)
            {
                if (result.Roads != null && _roadMaterial != null)
                    EnsureNode(result.Id, state).SetRoads(result.Roads, _roadMaterial);
                // tiles with no road data still count as done, so we stop re-requesting
                state.HasRoads = true;
                state.PendingRoads = false;
            }
            if (result.BuildingsRequested)
            {
                if (result.Buildings != null && _buildingMaterial != null)
                {
                    var node = EnsureNode(result.Id, state);
                    node.SetBuildings(result.Buildings, _buildingMaterial);
                    if (result.BuildingFaces is { Length: > 0 })
                        node.SetBuildingCollision(result.BuildingFaces);
                }
                if (result.Trees is { Count: > 0 } && _treeMaterial != null)
                    EnsureNode(result.Id, state).SetTrees(result.Trees, _treeMaterial);
                if (result.Water != null && _waterMaterial != null)
                    EnsureNode(result.Id, state).SetWater(result.Water, _waterMaterial);
                state.HasBuildings = true;
                state.PendingBuildings = false;
            }
            state.ActiveStride = result.Stride;
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

    private void EvaluateRings()
    {
        // desired stride per tile = finest over all anchors (0 = grid-only when meshes are off)
        var desired = new Dictionary<TileId, (int Stride, bool Collision, bool Roads, bool Buildings, int Dist)>();
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
                    bool collision = BuildCollision && dist <= Lod.CollisionMaxDist;
                    bool roads = BuildMeshes && dist <= Lod.RoadMaxDist;
                    bool buildings = BuildMeshes && dist <= Lod.BuildingMaxDist;
                    // strides are all 0 when meshes are off, otherwise all > 0: min = finest
                    if (desired.TryGetValue(id, out var cur))
                        desired[id] = (Math.Min(cur.Stride, stride), cur.Collision || collision,
                            cur.Roads || roads, cur.Buildings || buildings, Math.Min(cur.Dist, dist));
                    else
                        desired[id] = (stride, collision, roads, buildings, dist);
                }
        }

        // Nearest first. With everything on local disk the order barely matters, but when the
        // data is streaming it decides what the player sees: unordered, the tile underfoot
        // queues behind up to 360 others nine rings out, and you stand in a hole for a minute
        // while the horizon fills in.
        var ordered = desired.OrderBy(kv => kv.Value.Dist).ToList();

        foreach (var (id, want) in ordered)
        {
            if (!_chunks.TryGetValue(id, out var state))
                _chunks[id] = state = new ChunkState();

            bool needMesh = BuildMeshes && state.ActiveStride != want.Stride;
            bool needCollision = want.Collision && !state.HasCollision && !state.PendingCollision;
            bool needRoads = want.Roads && !state.HasRoads && !state.PendingRoads;
            bool needBuildings = want.Buildings && !state.HasBuildings && !state.PendingBuildings;
            bool needGrid = state.Grid == null;

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
            _chunks[id].Node?.QueueFree();
            _chunks.Remove(id);
        }
    }

    private void StartBuild(TileId id, ChunkState state, int stride, bool wantCollision,
        bool wantRoads, bool wantBuildings)
    {
        state.PendingStride = stride;
        state.PendingCollision = wantCollision;
        state.PendingRoads = wantRoads;
        state.PendingBuildings = wantBuildings;
        var cachedGrid = state.Grid;
        var cachedHoles = state.Holes;
        bool holesLoaded = state.HolesLoaded;
        var cachedCover = state.Cover;
        bool coverLoaded = state.CoverLoaded;
        var source = _source!;
        bool buildMesh = BuildMeshes && stride > 0;

        Task.Run(async () =>
        {
            try
            {
                var grid = cachedGrid ?? await source.LoadChunkAsync(id);
                // file missing despite the manifest — release the tile so it is not stuck pending
                if (grid == null) { _failedBuilds.Enqueue(id); return; }
                var holes = holesLoaded ? cachedHoles : await source.LoadHolesAsync(id);
                var cover = coverLoaded ? cachedCover : await source.LoadCoverAsync(id);
                var mesh = buildMesh ? TerrainMeshBuilder.BuildSurface(grid, stride, holes, cover) : null;
                var collision = wantCollision ? TerrainMeshBuilder.BuildCollisionMap(grid, holes) : null;

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
                if (mesh != null || collision != null)
                    _ready.Enqueue(new BuildResult(id, stride, grid, Interim: true,
                        mesh, collision, null, false, holes, cover, null, null, false, null, null));

                RoadMeshBuilder.MeshData? roads = null;
                RoadTile? roadTile = null;
                if (wantRoads)
                {
                    roadTile = await source.LoadRoadsAsync(id);
                    // the grid lets bridge piers and cableway pylons find their footing
                    if (roadTile != null) roads = RoadMeshBuilder.Build(roadTile, grid);
                }

                List<TreeInstance>? trees = null;
                WaterMeshBuilder.MeshData? water = null;
                if (wantBuildings)
                {
                    trees = await source.LoadTreesAsync(id);
                    // watercourses ride in the road tile but are meshed here, so a stream gets
                    // the water material instead of being drawn as a narrow blue road
                    if (cover != null) water = WaterMeshBuilder.Build(grid, cover, roadTile);
                }

                BuildingMeshBuilder.MeshData? buildings = null;
                Vector3[]? buildingFaces = null;
                if (wantBuildings)
                {
                    var bTile = await source.LoadBuildingsAsync(id);
                    if (bTile != null)
                    {
                        buildings = BuildingMeshBuilder.Build(bTile);
                        buildingFaces = BuildingMeshBuilder.BuildCollisionFaces(bTile);
                    }
                }

                // the mesh and collision already went out above, so this carries only the tail
                _ready.Enqueue(new BuildResult(id, stride, grid, Interim: false,
                    null, null, roads, wantRoads,
                    holes, cover, buildings, buildingFaces, wantBuildings, trees, water));
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
