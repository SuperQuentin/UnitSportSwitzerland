using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Keeps decoded tiles in memory, so ground that is left and returned to is not read twice.
///
/// <para>
/// <see cref="IChunkSource"/> is the established seam — <see cref="NetworkChunkSource"/> already
/// decorates <see cref="LocalChunkSource"/> with the shipped → cache → server tiering — so this
/// slots in without any caller knowing.
/// </para>
///
/// <para>
/// It exists because <see cref="ChunkManager.EvaluateRings"/> unloads a tile once it drifts past
/// the rings and rebuilds it from disk on the way back. That is right for a player wandering off,
/// and wrong for a route that doubles back: <c>france.gpx</c> retreads <b>30.5%</b> of its own
/// ground, and a video export of it therefore decoded a third of its terrain twice for nothing.
/// It is also where the export prefetcher puts what it fetches ahead of the clock.
/// </para>
/// </summary>
public sealed class CachingChunkSource : IChunkSource
{
    private readonly IChunkSource _inner;
    private readonly long _budgetBytes;

    /// <summary>
    /// Cached entries, newest use last. A plain dictionary plus a tick counter rather than a
    /// linked list: eviction happens rarely and in bulk, so an O(n log n) sweep when the budget
    /// is exceeded costs less than maintaining order on every hit.
    /// </summary>
    private readonly Dictionary<(AssetSlot Slot, TileId Id), Entry> _entries = new();

    private readonly object _gate = new();
    private long _bytes;
    private long _tick;

    public long CachedBytes { get { lock (_gate) return _bytes; } }
    public int CachedEntries { get { lock (_gate) return _entries.Count; } }
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);

    private enum AssetSlot { Chunk, Coarse, Roads, Holes, Buildings, Cover, Trees }

    private sealed class Entry
    {
        public object? Value;      // null is a real answer: "this tile has none"
        public long Bytes;
        public long LastUsed;
    }

    public CachingChunkSource(IChunkSource inner, long budgetBytes = 512L * 1024 * 1024)
    {
        _inner = inner;
        _budgetBytes = budgetBytes;
    }

    public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) =>
        _inner.LoadManifestAsync(ct);

    public Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Chunk, id, () => _inner.LoadChunkAsync(id, ct), Weigh);

    public Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Coarse, id, () => _inner.LoadCoarseChunkAsync(id, ct), Weigh);

    public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Roads, id, () => _inner.LoadRoadsAsync(id, ct), Weigh);

    public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Holes, id, () => _inner.LoadHolesAsync(id, ct),
            h => 64 + h.Count * 8L);

    public Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Buildings, id, () => _inner.LoadBuildingsAsync(id, ct), Weigh);

    public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Cover, id, () => _inner.LoadCoverAsync(id, ct), c => c.LongLength + 32);

    public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Trees, id, () => _inner.LoadTreesAsync(id, ct),
            t => 64 + t.Count * 17L);

    // One object for the whole region, read once at boot: nothing to cache.
    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default) =>
        _inner.LoadHorizonAsync(ct);

    private static long Weigh(ChunkGrid g) => g.Heights.LongLength * 2 + 64;
    private static long Weigh(RoadTile t)
    {
        long n = 128;
        foreach (var s in t.Segments) n += s.Points.LongLength * 4 + 48;
        foreach (var j in t.Junctions) n += j.Vertices.LongLength * 4 + j.Indices.LongLength * 2 + 48;
        return n;
    }
    private static long Weigh(BuildingTile t)
    {
        long n = 128;
        foreach (var b in t.Buildings) n += b.Triangles.LongLength * 4 + 48;
        return n;
    }

    /// <summary>
    /// Serves from the cache or from the inner source.
    ///
    /// <para>
    /// Two loads of the same tile can race here and both fetch. That is deliberate: holding the
    /// lock across the await would serialise every worker behind whichever tile is slowest, and
    /// the duplicate is a wasted read, not a wrong answer. <c>ChunkManager</c> only builds a tile
    /// once at a time anyway, so it happens when the prefetcher and the rings meet in the middle.
    /// </para>
    /// </summary>
    private async Task<T?> GetAsync<T>(AssetSlot slot, TileId id, Func<Task<T?>> fetch,
        Func<T, long> weigh) where T : class
    {
        var key = (slot, id);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                hit.LastUsed = ++_tick;
                Hits++;
                return (T?)hit.Value;
            }
            Misses++;
        }

        var value = await fetch().ConfigureAwait(false);

        lock (_gate)
        {
            long bytes = value == null ? 32 : weigh(value);
            if (_entries.TryGetValue(key, out var existing)) _bytes -= existing.Bytes;
            _entries[key] = new Entry { Value = value, Bytes = bytes, LastUsed = ++_tick };
            _bytes += bytes;
            if (_bytes > _budgetBytes) Evict();
        }

        return value;
    }

    /// <summary>Drops the least recently used quarter, so eviction is rare rather than constant.</summary>
    private void Evict()
    {
        long target = _budgetBytes * 3 / 4;
        var order = _entries.OrderBy(kv => kv.Value.LastUsed).ToList();

        foreach (var (key, entry) in order)
        {
            if (_bytes <= target) break;
            _entries.Remove(key);
            _bytes -= entry.Bytes;
        }
    }

    /// <summary>Forgets everything. For leaving a mode that warmed the cache for its own route.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _bytes = 0;
        }
    }
}
