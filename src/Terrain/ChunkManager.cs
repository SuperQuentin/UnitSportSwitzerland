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
    private WorldOrigin? _origin;
    private Material? _material;
    private HashSet<TileId> _available = new();
    private readonly List<Node3D> _anchors = new();
    private readonly Dictionary<TileId, ChunkState> _chunks = new();
    private readonly ConcurrentQueue<BuildResult> _ready = new();
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
        TileId Id, int Stride, ChunkGrid Grid,
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

        CommitReadyResults();

        _sinceEval += delta;
        if (_sinceEval >= EvalInterval)
        {
            _sinceEval = 0;
            EvaluateRings();
        }
    }

    private void CommitReadyResults()
    {
        int meshBudget = MeshCommitsPerFrame;
        int collisionBudget = CollisionCommitsPerFrame;

        while (meshBudget > 0 && collisionBudget > 0 && _ready.TryDequeue(out var result))
        {
            if (!_chunks.TryGetValue(result.Id, out var state))
                continue; // chunk was unloaded while building — drop

            state.Grid = result.Grid;
            state.Holes = result.Holes;
            state.HolesLoaded = true;
            state.Cover = result.Cover;
            state.CoverLoaded = true;
            state.PendingStride = -1;

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

        foreach (var (id, want) in desired)
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
                StartBuild(id, state, want.Stride, needCollision, needRoads, needBuildings);
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
                if (grid == null) return; // file missing despite manifest — treated as unavailable
                var holes = holesLoaded ? cachedHoles : await source.LoadHolesAsync(id);
                var cover = coverLoaded ? cachedCover : await source.LoadCoverAsync(id);
                var mesh = buildMesh ? TerrainMeshBuilder.BuildSurface(grid, stride, holes, cover) : null;
                var collision = wantCollision ? TerrainMeshBuilder.BuildCollisionMap(grid, holes) : null;

                RoadMeshBuilder.MeshData? roads = null;
                if (wantRoads)
                {
                    var roadTile = await source.LoadRoadsAsync(id);
                    // the grid lets bridge piers find their footing on the terrain
                    if (roadTile != null) roads = RoadMeshBuilder.Build(roadTile, grid);
                }

                List<TreeInstance>? trees = null;
                WaterMeshBuilder.MeshData? water = null;
                if (wantBuildings)
                {
                    trees = await source.LoadTreesAsync(id);
                    if (cover != null) water = WaterMeshBuilder.Build(grid, cover);
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

                _ready.Enqueue(new BuildResult(id, stride, grid, mesh, collision, roads, wantRoads,
                    holes, cover, buildings, buildingFaces, wantBuildings, trees, water));
            }
            catch (Exception e)
            {
                GD.PushError($"Chunk build failed for {id}: {e}");
            }
        });
    }
}
