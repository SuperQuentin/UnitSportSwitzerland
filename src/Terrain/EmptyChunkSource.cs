using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// A region with no terrain at all: a fresh clone, or a <c>--generated-world</c> server with no
/// tiles. Under <see cref="FallbackChunkSource"/> it gives the generated world every peer plays on
/// such a machine, which is what a check builds when it means to test that world (#943).
/// </summary>
public sealed class EmptyChunkSource : IChunkSource
{
    public static readonly EmptyChunkSource Instance = new();

    public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) => Task.FromResult(new TerrainManifest());
    public Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default) => Task.FromResult<ChunkGrid?>(null);
    public Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default) => Task.FromResult<ChunkGrid?>(null);
    public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) => Task.FromResult<RoadTile?>(null);
    public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) => Task.FromResult<HashSet<int>?>(null);
    public Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default) => Task.FromResult<BuildingTile?>(null);
    public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) => Task.FromResult<List<TreeInstance>?>(null);
    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) => Task.FromResult<HorizonIndex?>(null);
}
