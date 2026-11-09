using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A chunk source that also answers a leaf's stand-in tile (#1510, <see cref="LeafStandIn"/>): its grid, roads,
/// buildings and trees as the leaf's <see cref="LeafStand"/> holds them, so what loads a tile by its name (an
/// interior's building, a venue's roads) loads a leaf's the same way. Every real tile goes to the source underneath.
/// Goes with step D, when the systems ask leaves themselves.
/// </summary>
public sealed class StandInChunkSource(IChunkSource inner, Func<CubeKey, LeafStand?> stands, Func<CubeKey, CubeLeafContent?> leaves) : IChunkSource
{
    public IChunkSource Inner { get; } = inner;

    private Task<LeafStand?> Stand(CubeKey leaf, CancellationToken ct) => Task.Run(() => stands(leaf), ct);

    public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) => Inner.LoadManifestAsync(ct);

    public async Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.TryLeaf(id, out var leaf) ? (await Stand(leaf, ct))?.Grid : await Inner.LoadChunkAsync(id, ct);

    public async Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.TryLeaf(id, out var leaf) ? (await Stand(leaf, ct))?.Grid.Decimate(ChunkFormat.CoarseStride) : await Inner.LoadCoarseChunkAsync(id, ct);

    public async Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.TryLeaf(id, out var leaf) ? (await Stand(leaf, ct))?.Roads : await Inner.LoadRoadsAsync(id, ct);

    public async Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.TryLeaf(id, out var leaf) ? (await Stand(leaf, ct))?.Buildings : await Inner.LoadBuildingsAsync(id, ct);

    // a leaf's holes and cover are on its lattice, not a tile's grid: none through a tile name
    public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.IsStandIn(id) ? Task.FromResult<HashSet<int>?>(null) : Inner.LoadHolesAsync(id, ct);

    public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.IsStandIn(id) ? Task.FromResult<byte[]?>(null) : Inner.LoadCoverAsync(id, ct);

    public Task<WaterTile?> LoadWaterAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.IsStandIn(id) ? Task.FromResult<WaterTile?>(null) : Inner.LoadWaterAsync(id, ct);

    public async Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default)
    {
        if (!LeafStandIn.TryLeaf(id, out var leaf)) return await Inner.LoadTreesAsync(id, ct);
        var content = await Task.Run(() => leaves(leaf), ct);
        return content?.Trees is { } trees ? LeafStandIn.Trees(trees) : null;
    }

    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) => Inner.LoadHorizonAsync(ct);
    public Task<LandingIndex?> LoadLandingsAsync(CancellationToken ct = default) => Inner.LoadLandingsAsync(ct);
    public Task<RopewayInfoIndex?> LoadRopewayInfoAsync(CancellationToken ct = default) => Inner.LoadRopewayInfoAsync(ct);
    public Task<byte[]?> LoadPoisAsync(CancellationToken ct = default) => Inner.LoadPoisAsync(ct);
    public Task<AirportIndex?> LoadAirportsAsync(CancellationToken ct = default) => Inner.LoadAirportsAsync(ct);

    public Task<List<FieldPolygon>?> LoadFieldsAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.IsStandIn(id) ? Task.FromResult<List<FieldPolygon>?>(null) : Inner.LoadFieldsAsync(id, ct);

    public Task<PisteTile?> LoadPistesAsync(TileId id, CancellationToken ct = default) =>
        LeafStandIn.IsStandIn(id) ? Task.FromResult<PisteTile?>(null) : Inner.LoadPistesAsync(id, ct);
}
