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

    /// <summary>
    /// The decimated companion tile, for rings that render one vertex in ten or twenty.
    ///
    /// <para>
    /// Null both when the tile does not exist and when the region was built before coarse tiles
    /// did; the caller falls back to <see cref="LoadChunkAsync"/>, which is correct in either
    /// case and simply reads 490 KB to use 5 of them.
    /// </para>
    /// </summary>
    Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default);

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

    /// <summary>
    /// The tile's still water (#299): level per 2 m sample, NaN where dry, optional fetch. Null when
    /// the source has no water layer for it; the runtime then derives the legacy layer from the
    /// cover raster (<see cref="WaterLayer.FromCover"/>). The preprocessor's water file (#298)
    /// answers here; decorators forward it.
    /// </summary>
    Task<WaterTile?> LoadWaterAsync(TileId id, CancellationToken ct = default) => Task.FromResult<WaterTile?>(null);

    /// <summary>Tree instances for a tile; null when the tile has none.</summary>
    Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default);

    /// <summary>
    /// The region-wide far-horizon lattice (<c>horizon.bin</c>); null when the region was built
    /// before it existed, in which case the world simply ends at the last LOD ring as it used to.
    /// </summary>
    Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default);

    /// <summary>
    /// The region's boat landings and harbour jetties (#377, <c>landings.json</c>); null when the
    /// region has none or was built before them. Decorators forward it.
    /// </summary>
    Task<LandingIndex?> LoadLandingsAsync(CancellationToken ct = default) => Task.FromResult<LandingIndex?>(null);

    /// <summary>
    /// The region's airports and their stands (#422, <c>airports.json</c>); null when the region has
    /// none. Only the deciding peer reads it (the server, or a client offline): it is not streamed.
    /// Decorators forward it.
    /// </summary>
    Task<AirportIndex?> LoadAirportsAsync(CancellationToken ct = default) => Task.FromResult<AirportIndex?>(null);
    /// The tile's farm fields (#494, <c>fields_E_N.fld</c>, <see cref="FieldFormat"/>); null when the
    /// tile has none or the source has no field layer. Decorators forward it; a network client streams
    /// it like the cover (<c>AssetKind.Fields</c>).
    /// </summary>
    Task<List<FieldPolygon>?> LoadFieldsAsync(TileId id, CancellationToken ct = default) => Task.FromResult<List<FieldPolygon>?>(null);
}
