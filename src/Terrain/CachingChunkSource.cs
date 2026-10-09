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
    private long _epoch;   // bumped by Clear, so a fetch that straddles it is not cached

    public long CachedBytes { get { lock (_gate) return _bytes; } }
    public int CachedEntries { get { lock (_gate) return _entries.Count; } }
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);

    private enum AssetSlot { Chunk, Coarse, Roads, Holes, Buildings, Cover, Trees, Water, Fields }

    private sealed class Entry
    {
        public object? Value;      // null is a real answer: "this tile has none"
        public long Bytes;
        public long LastUsed;
    }

    public CachingChunkSource(IChunkSource inner, long budgetBytes = 256L * 1024 * 1024)
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

    public Task<WaterTile?> LoadWaterAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Water, id, () => _inner.LoadWaterAsync(id, ct),
            w => 64 + w.Level.LongLength * 4 + (w.FetchM?.LongLength ?? 0) * 4);

    public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Trees, id, () => _inner.LoadTreesAsync(id, ct),
            t => 64 + t.Count * 17L);

    // One object for the whole region, read once at boot: nothing to cache.
    /// <summary>
    /// The horizon lattice, decoded once and shared: it is the whole region (megabytes, and the
    /// generated fill merged in), and the Battle Royale map and site search ask for it every match.
    /// Forgotten with the rest of the cache (<see cref="Invalidate"/>).
    /// </summary>
    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_horizon is { IsFaulted: false, IsCanceled: false } cached) return cached;
            return _horizon = _inner.LoadHorizonAsync(CancellationToken.None);
        }
    }

    private Task<HorizonIndex?>? _horizon;

    /// <summary>Not cached: read once at boot and when a server sends its own.</summary>
    public Task<AirportIndex?> LoadAirportsAsync(CancellationToken ct = default) => _inner.LoadAirportsAsync(ct);

    public Task<LandingIndex?> LoadLandingsAsync(CancellationToken ct = default) => _inner.LoadLandingsAsync(ct);

    /// <summary>The tile's farm fields (#494), cached like the trees (rings: 4 bytes a coordinate).</summary>
    public Task<List<FieldPolygon>?> LoadFieldsAsync(TileId id, CancellationToken ct = default) =>
        GetAsync(AssetSlot.Fields, id, () => _inner.LoadFieldsAsync(id, ct),
            f => 64 + f.Sum(p => 48 + p.Rings.Sum(r => r.LongLength * 4 + 24)));

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
        long epoch;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                hit.LastUsed = ++_tick;
                Hits++;
                return (T?)hit.Value;
            }
            Misses++;
            epoch = _epoch;
        }

        var value = await fetch().ConfigureAwait(false);

        lock (_gate)
        {
            // fetched from a world that has since changed underneath (real tiles arriving where
            // generated ones were): the caller may still use it, but it must not outlive the
            // Clear or Invalidate
            if (epoch != _epoch) return value;
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

    /// <summary>
    /// Forgets everything, including whatever is still being fetched. For leaving a mode that
    /// warmed the cache for its own route, and for a world thrown away by a rebase.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _bytes = 0;
            _epoch++;
        }
    }

    /// <summary>
    /// Forgets every asset of the tiles <paramref name="affected"/> selects (all of them when
    /// null) — real tiles arriving where generated ones were cached, and the generated tiles
    /// round them whose blend just changed. Nothing in flight is cached either, whichever tile it
    /// is for: telling those apart would mean keeping the predicate for ever.
    /// </summary>
    public void Invalidate(Func<TileId, bool>? affected)
    {
        lock (_gate) _horizon = null;
        if (affected == null) { Clear(); return; }
        lock (_gate)
        {
            var drop = _entries.Keys.Where(k => affected(k.Id)).ToList();
            foreach (var key in drop)
            {
                _bytes -= _entries[key].Bytes;
                _entries.Remove(key);
            }
            _epoch++;
        }
    }
}
