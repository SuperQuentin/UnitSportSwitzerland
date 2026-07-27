using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Where chunk data comes from. LocalChunkSource reads shipped files today; an
/// HttpChunkSource can serve all of Switzerland from a CDN later without touching
/// anything else in the game.
/// </summary>
public interface IChunkSource
{
    Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default);

    /// <summary>Returns null when the tile does not exist in this source.</summary>
    Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default);

    /// <summary>Roads/railways for a tile; null when the tile has no road file.</summary>
    Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default);

    /// <summary>
    /// Terrain quads to omit (tunnel portals). Empty for the vast majority of tiles.
    /// </summary>
    Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default);

    /// <summary>Buildings for a tile; null when the tile has none.</summary>
    Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default);

    /// <summary>Ground-cover raster for a tile; null when unclassified.</summary>
    Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default);

    /// <summary>Tree instances for a tile; null when the tile has none.</summary>
    Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default);
}
