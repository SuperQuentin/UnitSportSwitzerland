using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The decoded-tile cache in RAM (src/Terrain/CachingChunkSource.cs, linked in): a grid that could not be had yet is
/// asked for again (#1816). A join from the menu built its spawn tile before the server link was up; the network
/// source answered null ("not reachable"), the cache kept that null, and the tile's grid was never fetched.
/// </summary>
public class CachingChunkSourceTests
{
    private static readonly TileId Spawn = new(2583, 1113);

    /// <summary>Answers null until <see cref="Up"/>, as the network source does before the link is up.</summary>
    private sealed class LinkSource : IChunkSource
    {
        public bool Up;
        public int ChunkAsks, CoarseAsks, RoadAsks;

        private static readonly int Size = (ChunkFormat.GridSize - 1) / ChunkFormat.CoarseStride + 1;

        private ChunkGrid? Grid(TileId id) => Up ? new ChunkGrid(id, new ushort[Size * Size], 400, 400, ChunkFormat.CoarseStride) : null;

        public Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default) { ChunkAsks++; return Task.FromResult(Grid(id)); }
        public Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default) { CoarseAsks++; return Task.FromResult(Grid(id)); }
        public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) { RoadAsks++; return Task.FromResult<RoadTile?>(null); }
        public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) => Task.FromResult(new TerrainManifest());
        public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) => Task.FromResult<HashSet<int>?>(null);
        public Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default) => Task.FromResult<BuildingTile?>(null);
        public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) => Task.FromResult<List<TreeInstance>?>(null);
        public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) => Task.FromResult<HorizonIndex?>(null);
    }

    [Fact]
    public async Task A_grid_missing_before_the_link_is_up_is_asked_for_again()
    {
        var link = new LinkSource();
        var cache = new CachingChunkSource(link);

        Assert.Null(await cache.LoadChunkAsync(Spawn));
        Assert.Null(await cache.LoadCoarseChunkAsync(Spawn));
        link.Up = true;
        Assert.NotNull(await cache.LoadChunkAsync(Spawn));
        Assert.NotNull(await cache.LoadCoarseChunkAsync(Spawn));
        Assert.Equal(2, link.ChunkAsks);
        Assert.Equal(2, link.CoarseAsks);
    }

    [Fact]
    public async Task A_grid_that_came_is_served_from_memory()
    {
        var link = new LinkSource { Up = true };
        var cache = new CachingChunkSource(link);

        Assert.NotNull(await cache.LoadChunkAsync(Spawn));
        Assert.NotNull(await cache.LoadChunkAsync(Spawn));
        Assert.Equal(1, link.ChunkAsks);
    }

    [Fact]
    public async Task A_tile_without_roads_stays_remembered()
    {
        var link = new LinkSource { Up = true };
        var cache = new CachingChunkSource(link);

        Assert.Null(await cache.LoadRoadsAsync(Spawn));
        Assert.Null(await cache.LoadRoadsAsync(Spawn));
        Assert.Equal(1, link.RoadAsks);
    }
}
