using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Serves <see cref="ProceduralWorld"/> tiles while there is no real terrain, and steps aside the
/// moment there is.
///
/// <para>
/// A decorator on the ordinary chain, like <see cref="NetworkChunkSource"/>: only tiles the
/// generator owns are answered here, and only while <see cref="Active"/>; everything else goes
/// to the inner source untouched. Retiring is <see cref="ChunkManager.RetireFallback"/>'s job —
/// it clears <see cref="Active"/>, unloads every generated tile and flushes the
/// <see cref="CachingChunkSource"/> above this one, so no generated grid can be served for a real
/// tile that happens to share its id.
/// </para>
/// </summary>
public sealed class FallbackChunkSource : IChunkSource
{
    private readonly IChunkSource _inner;
    private volatile bool _active = true;

    public ProceduralWorld World { get; }

    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    public FallbackChunkSource(IChunkSource inner, ProceduralWorld world)
    {
        _inner = inner;
        World = world;
    }

    private bool Serves(TileId id) => _active && World.Contains(id);

    public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) =>
        _active ? Task.FromResult(World.BuildManifest()) : _inner.LoadManifestAsync(ct);

    public Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default) =>
        Serves(id) ? Task.Run<ChunkGrid?>(() => World.BuildGrid(id, 1), ct) : _inner.LoadChunkAsync(id, ct);

    public Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default) =>
        Serves(id)
            ? Task.Run<ChunkGrid?>(() => World.BuildGrid(id, ChunkFormat.CoarseStride), ct)
            : _inner.LoadCoarseChunkAsync(id, ct);

    public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) =>
        Serves(id) ? Task.Run(() => World.BuildRoads(id), ct) : _inner.LoadRoadsAsync(id, ct);

    // no tunnels, so no carved portals
    public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) =>
        Serves(id) ? Task.FromResult<HashSet<int>?>(null) : _inner.LoadHolesAsync(id, ct);

    public Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default) =>
        Serves(id) ? Task.Run(() => World.BuildBuildings(id), ct) : _inner.LoadBuildingsAsync(id, ct);

    public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default) =>
        Serves(id) ? Task.Run<byte[]?>(() => World.BuildCover(id), ct) : _inner.LoadCoverAsync(id, ct);

    public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) =>
        Serves(id) ? Task.Run<List<TreeInstance>?>(() => World.BuildTrees(id), ct) : _inner.LoadTreesAsync(id, ct);

    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) =>
        _active ? Task.Run<HorizonIndex?>(World.BuildHorizon, ct) : _inner.LoadHorizonAsync(ct);
}
