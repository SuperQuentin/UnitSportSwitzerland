using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Serves <see cref="ProceduralWorld"/> tiles wherever there is no real terrain, blended into the
/// real tiles beside them.
///
/// <para>
/// A decorator on the ordinary chain, like <see cref="NetworkChunkSource"/>: a tile this source
/// <see cref="Covers"/> is generated here, everything else goes to the inner source untouched. A
/// tile is covered when it is not real and lies inside the fill domain: the box round the spawn
/// tile and the real set's bounding box, each grown by <see cref="FillRadiusTiles"/>. So a partial
/// region — a MapSetup zone, a server streaming part of the country, a download with holes — is
/// surrounded by generated land instead of void, and a fresh clone with no terrain at all is
/// generated throughout. Clients never ask a server for a generated tile: this sits above
/// <see cref="NetworkChunkSource"/> and answers first.
/// </para>
///
/// <para>
/// What is real lives in an immutable <see cref="Snapshot"/>, swapped whole by
/// <see cref="SetReal"/> and read lock-free by the ring evaluator and the tile workers. Its
/// <see cref="Snapshot.Version"/> keys every cached blend, so a merge never serves a blend made
/// against the old real set. Unloading the affected tiles and flushing the cache above this source
/// is <see cref="ChunkManager.MergeAvailableTiles"/>'s job.
/// </para>
///
/// <para>No Godot types: <c>tools/BlendCheck</c> compiles it to test the source end to end.</para>
/// </summary>
public sealed class FallbackChunkSource : IChunkSource
{
    /// <summary>How far the fill reaches past the spawn tile and past the real set, in tiles.</summary>
    public const int FillRadiusTiles = ProceduralWorld.RadiusTiles;

    /// <summary>How far the generated horizon reaches past the fill domain: the default 60 km view.</summary>
    public const int HorizonMarginTiles = ProceduralWorld.HorizonRadiusTiles - ProceduralWorld.RadiusTiles;

    /// <summary>
    /// Blends kept. Each holds a 102² lattice and references up to four full real grids (2 MB
    /// each, usually shared with the tile cache), so this bounds the worst case near 200 MB.
    /// </summary>
    private const int BlendCacheSize = 24;

    private readonly IChunkSource _inner;
    private volatile Snapshot _snapshot;

    public ProceduralWorld World { get; }

    /// <summary>
    /// Where the blend loads real neighbours from. The cache above this source, so a real grid
    /// decoded for a blend is the one the loader then draws, and the other way round. It cannot
    /// recurse: a real id is never covered, so the cache hands it straight to the inner source.
    /// Defaults to the inner source.
    /// </summary>
    public IChunkSource? Neighbours { get; set; }

    /// <summary>Where warnings and timings go; the game points it at <c>GD.Print</c>.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Folder for the generated horizon samples, so a launch reads them instead of spending
    /// ~7 s of every core regenerating them. Null keeps them in memory only.
    /// </summary>
    public string? HorizonCacheDir { get; set; }

    /// <summary>An inclusive tile rectangle.</summary>
    public readonly record struct TileRect(int MinE, int MinN, int MaxE, int MaxN)
    {
        public bool Contains(TileId id) => id.E >= MinE && id.E <= MaxE && id.N >= MinN && id.N <= MaxN;
        public TileRect Grow(int by) => new(MinE - by, MinN - by, MaxE + by, MaxN + by);
        public TileRect Union(TileRect o) =>
            new(Math.Min(MinE, o.MinE), Math.Min(MinN, o.MinN), Math.Max(MaxE, o.MaxE), Math.Max(MaxN, o.MaxN));
    }

    /// <summary>What is real and what may be generated, as of one <see cref="SetReal"/>.</summary>
    public sealed class Snapshot
    {
        public IReadOnlySet<TileId> Real { get; }
        public TileRect Spawn { get; }

        /// <summary>The real set's bounding box grown by the fill radius; null with no real tiles yet.</summary>
        public TileRect? RealBox { get; }

        public bool Enabled { get; }
        public long Version { get; }

        internal Snapshot(IReadOnlySet<TileId> real, TileRect spawn, TileRect? realBox, bool enabled, long version)
        {
            Real = real;
            Spawn = spawn;
            RealBox = realBox;
            Enabled = enabled;
            Version = version;
        }

        public bool InDomain(TileId id) => Spawn.Contains(id) || RealBox?.Contains(id) == true;

        public bool Covers(TileId id) => Enabled && !Real.Contains(id) && InDomain(id);

        /// <summary>One rectangle holding the whole domain, for sizing the horizon's coverage texture.</summary>
        public TileRect Bounds => RealBox is { } r ? Spawn.Union(r) : Spawn;
    }

    /// <param name="spawnE">Where the player starts: the fill always reaches this far round it.</param>
    public FallbackChunkSource(IChunkSource inner, ProceduralWorld world, double spawnE, double spawnN,
        bool enabled = true)
    {
        _inner = inner;
        World = world;
        var t = TileId.FromLv95(spawnE, spawnN);
        var spawn = new TileRect(t.E, t.N, t.E, t.N).Grow(FillRadiusTiles);
        _snapshot = new Snapshot(new HashSet<TileId>(), spawn, null, enabled, 0);
    }

    public Snapshot Current => _snapshot;

    /// <summary>Whether this source generates the tile, as of now.</summary>
    public bool Covers(TileId id) => _snapshot.Covers(id);

    /// <summary>
    /// Takes the set of real tiles (a copy is kept) and optionally switches generation on or off.
    /// The domain only ever grows: the real set only grows, and the spawn box is fixed.
    /// </summary>
    public Snapshot SetReal(IEnumerable<TileId> real, bool? enabled = null)
    {
        var set = real.ToHashSet();
        TileRect? box = null;
        if (set.Count > 0)
        {
            int minE = int.MaxValue, minN = int.MaxValue, maxE = int.MinValue, maxN = int.MinValue;
            foreach (var t in set)
            {
                minE = Math.Min(minE, t.E);
                maxE = Math.Max(maxE, t.E);
                minN = Math.Min(minN, t.N);
                maxN = Math.Max(maxN, t.N);
            }
            box = new TileRect(minE, minN, maxE, maxN).Grow(FillRadiusTiles);
        }
        var old = _snapshot;
        // never shrink: a tile that was generated keeps being generated
        if (old.RealBox is { } previous) box = box is { } b ? b.Union(previous) : previous;
        var next = new Snapshot(set, old.Spawn, box, enabled ?? old.Enabled, old.Version + 1);
        _snapshot = next;
        return next;
    }

    // ---- IChunkSource ------------------------------------------------------------------------

    public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) =>
        _inner.LoadManifestAsync(ct);

    public async Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default)
    {
        if (!Covers(id)) return await _inner.LoadChunkAsync(id, ct).ConfigureAwait(false);
        var blend = await BlendFor(id, full: true, ct).ConfigureAwait(false);
        return await Task.Run<ChunkGrid?>(() => World.BuildGrid(id, 1, blend), ct).ConfigureAwait(false);
    }

    public async Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default)
    {
        if (!Covers(id)) return await _inner.LoadCoarseChunkAsync(id, ct).ConfigureAwait(false);
        var blend = await BlendFor(id, full: false, ct).ConfigureAwait(false);
        return await Task.Run<ChunkGrid?>(() => World.BuildGrid(id, ChunkFormat.CoarseStride, blend), ct)
            .ConfigureAwait(false);
    }

    public async Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default)
    {
        if (!Covers(id)) return await _inner.LoadRoadsAsync(id, ct).ConfigureAwait(false);
        var blend = await BlendFor(id, full: true, ct).ConfigureAwait(false);
        return await Task.Run(() => World.BuildRoads(id, blend), ct).ConfigureAwait(false);
    }

    // no tunnels, so no carved portals
    public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default) =>
        Covers(id) ? Task.FromResult<HashSet<int>?>(null) : _inner.LoadHolesAsync(id, ct);

    public async Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default)
    {
        if (!Covers(id)) return await _inner.LoadBuildingsAsync(id, ct).ConfigureAwait(false);
        var blend = await BlendFor(id, full: true, ct).ConfigureAwait(false);
        return await Task.Run(() => World.BuildBuildings(id, blend), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cover reads the blend only at 10 m points, where a coarse neighbour holds exactly the full
    /// one's vertices — so the coarse blend gives the same raster without reading any real tile at
    /// full resolution, which matters because every tile in the rings asks for cover.
    /// </summary>
    public async Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default)
    {
        if (!Covers(id)) return await _inner.LoadCoverAsync(id, ct).ConfigureAwait(false);
        var blend = await BlendFor(id, full: false, ct).ConfigureAwait(false);
        return await Task.Run<byte[]?>(() => World.BuildCover(id, blend), ct).ConfigureAwait(false);
    }

    public async Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default)
    {
        if (!Covers(id)) return await _inner.LoadTreesAsync(id, ct).ConfigureAwait(false);
        var blend = await BlendFor(id, full: true, ct).ConfigureAwait(false);
        return await Task.Run<List<TreeInstance>?>(() => World.BuildTrees(id, blend), ct).ConfigureAwait(false);
    }

    // ---- the blend -----------------------------------------------------------------------------

    private readonly object _blendGate = new();
    private readonly Dictionary<(TileId, bool, long), Task<ProceduralWorld.Blend?>> _blends = new();
    private readonly LinkedList<(TileId, bool, long)> _blendOrder = new();

    /// <summary>The real <c>horizon.bin</c>, once loaded: knots without reading any tile.</summary>
    private volatile HorizonIndex? _realHorizon;

    /// <summary>
    /// The blend for a generated tile against the current real set, or null when no real tile is
    /// near enough to matter. One task per (tile, resolution, version), shared by the chunk, cover,
    /// roads, buildings and trees of a tile and by concurrent requests for it.
    /// </summary>
    /// <param name="full">
    /// Whether the tile is wanted at full resolution: the edge neighbours' full grids then carry
    /// the real detail across the seam; otherwise their coarse grids do, which is exact at the
    /// 10 m points a coarse tile has.
    /// </param>
    public Task<ProceduralWorld.Blend?> BlendFor(TileId id, bool full, CancellationToken ct = default)
    {
        var snap = _snapshot;
        // nothing real within reach is the common case away from real ground: no task, no cache slot
        bool any = false;
        foreach (var k in ProceduralWorld.BlendWindow(id))
            if (snap.Real.Contains(k)) { any = true; break; }
        if (!any) return Task.FromResult<ProceduralWorld.Blend?>(null);

        var key = (id, full, snap.Version);
        Task<ProceduralWorld.Blend?> task;
        lock (_blendGate)
        {
            if (!_blends.TryGetValue(key, out task!) || task.IsFaulted || task.IsCanceled)
            {
                // not tied to the caller's token: another build may be waiting on the same blend,
                // and whatever it reads lands in the shared tile cache either way
                if (_blends.Remove(key)) _blendOrder.Remove(key);
                task = Task.Run(() => MakeBlendAsync(id, full, snap));
                _blends[key] = task;
                _blendOrder.AddLast(key);
                while (_blendOrder.Count > BlendCacheSize)
                {
                    _blends.Remove(_blendOrder.First!.Value);
                    _blendOrder.RemoveFirst();
                }
            }
        }
        return task.IsCompleted ? task : task.WaitAsync(ct);
    }

    private async Task<ProceduralWorld.Blend?> MakeBlendAsync(TileId id, bool full, Snapshot snap)
    {
        var near = ProceduralWorld.BlendWindow(id).Where(snap.Real.Contains).ToList();
        var reals = await Task.WhenAll(near.Select(k => LoadRealAsync(id, k, full))).ConfigureAwait(false);
        return World.CreateBlend(id, reals.OfType<ProceduralWorld.RealTile>(), snap.Version);
    }

    /// <summary>
    /// A real tile as the blend needs it: its 100 m knots, and for the eight neighbours a grid.
    /// Null when the tile is listed but cannot be read; it then leaves a hole, as it always has.
    /// </summary>
    private async Task<ProceduralWorld.RealTile?> LoadRealAsync(TileId tile, TileId k, bool full)
    {
        var source = Neighbours ?? _inner;
        int stride = ProceduralWorld.BlendGridStride(tile, k, full);
        try
        {
            ChunkGrid? grid = null;
            if (stride == 1) grid = await source.LoadChunkAsync(k).ConfigureAwait(false);
            else if (stride != 0) grid = await LoadCoarseOrFullAsync(source, k).ConfigureAwait(false);
            if (stride != 0 && grid == null) return null;

            // the same numbers either way: horizon.bin is extracted from the tiles
            ushort[] knots;
            if (_realHorizon is { } index && index.TryGet(k, out var fromIndex)) knots = fromIndex;
            else
            {
                grid ??= await LoadCoarseOrFullAsync(source, k).ConfigureAwait(false);
                if (grid == null) return null;
                knots = HorizonFormat.Extract(grid);
            }
            return new ProceduralWorld.RealTile(k, knots, stride == 0 ? null : grid);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log?.Invoke($"[fallback] real tile {k} unreadable for the blend of {tile}: {e.Message}");
            return null;
        }
    }

    /// <summary>The coarse companion, else the full grid (a region built before coarse tiles existed).</summary>
    private static async Task<ChunkGrid?> LoadCoarseOrFullAsync(IChunkSource source, TileId k) =>
        await source.LoadCoarseChunkAsync(k).ConfigureAwait(false)
        ?? await source.LoadChunkAsync(k).ConfigureAwait(false);

    /// <summary>Forgets every blend, for a world being thrown away (a rebase).</summary>
    public void ClearBlends()
    {
        lock (_blendGate)
        {
            _blends.Clear();
            _blendOrder.Clear();
        }
    }

    // ---- the horizon ---------------------------------------------------------------------------

    /// <summary>
    /// Generated 100 m samples of tiles no real tile reaches. They depend on the generator alone,
    /// so they are kept across reloads and a merge only recomputes the ones near real ground.
    /// </summary>
    private readonly ConcurrentDictionary<TileId, ushort[]> _plainHorizon = new();

    /// <summary>
    /// The real index (<c>horizon.bin</c>, if there is one) with generated samples for every other
    /// tile of the fill domain and <see cref="HorizonMarginTiles"/> beyond it. A generated tile
    /// next to real ones blends on their knots alone — at a 100 m point the detail correction is
    /// exactly zero — so this reads no real tile. Without a real index there is no blend out
    /// here, which only shows past the rings: a tile drawn by the loader discards the horizon.
    /// </summary>
    public async Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default)
    {
        var real = await _inner.LoadHorizonAsync(ct).ConfigureAwait(false);
        _realHorizon = real;
        var snap = _snapshot;
        if (!snap.Enabled) return real;
        return await Task.Run(() => MergeHorizon(real, snap, ct), ct).ConfigureAwait(false);
    }

    private HorizonIndex MergeHorizon(HorizonIndex? real, Snapshot snap, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var box = snap.Bounds.Grow(HorizonMarginTiles);
        var ids = new List<TileId>();
        for (int n = box.MinN; n <= box.MaxN; n++)
            for (int e = box.MinE; e <= box.MaxE; e++)
            {
                var id = new TileId(e, n);
                if (!snap.Real.Contains(id) && real?.Contains(id) != true) ids.Add(id);
            }

        var knots = new Dictionary<TileId, ProceduralWorld.RealTile>();
        if (real != null)
            foreach (var k in snap.Real)
                if (real.TryGet(k, out var s)) knots[k] = new ProceduralWorld.RealTile(k, s, null);

        LoadHorizonCache();
        int computed = 0, blended = 0;
        var samples = new ushort[ids.Count][];
        var blendKeys = new ulong[ids.Count];
        Parallel.For(0, ids.Count, new ParallelOptions { CancellationToken = ct }, t =>
        {
            var id = ids[t];
            List<ProceduralWorld.RealTile>? near = null;
            if (knots.Count > 0)
                foreach (var k in ProceduralWorld.BlendWindow(id))
                    if (knots.TryGetValue(k, out var r)) (near ??= new()).Add(r);
            if (near == null)
            {
                samples[t] = _plainHorizon.GetOrAdd(id, i =>
                {
                    Interlocked.Increment(ref computed);
                    return World.HorizonSamples(i, null);
                });
                return;
            }
            Interlocked.Increment(ref blended);
            ulong key = blendKeys[t] = BlendKey(near);
            samples[t] = _blendedHorizon.GetOrAdd((id, key), _ =>
            {
                Interlocked.Increment(ref computed);
                return World.HorizonSamples(id, World.CreateBlend(id, near, snap.Version));
            });
        });

        var tiles = new Dictionary<TileId, ushort[]>(ids.Count + (real?.Count ?? 0));
        if (real != null)
            foreach (var id in real.Tiles)
                if (real.TryGet(id, out var s)) tiles[id] = s;
        for (int t = 0; t < ids.Count; t++) tiles[ids[t]] = samples[t];

        Log?.Invoke($"[fallback] horizon: {real?.Count ?? 0} real + {ids.Count} generated tiles "
            + $"({blended} blended, {ids.Count - computed} from cache) in {clock.ElapsedMilliseconds} ms");
        if (computed > 0)
        {
            var current = new List<((TileId, ulong), ushort[])>(ids.Count);
            for (int t = 0; t < ids.Count; t++)
                if (blendKeys[t] != 0) current.Add(((ids[t], blendKeys[t]), samples[t]));
            SaveHorizonCache(current);
        }
        return new HorizonIndex(tiles);
    }

    // ---- the generated horizon on disk -----------------------------------------------------
    //
    // Generating the ~44,000 tiles takes ~7 s of every core, at every launch, for the same
    // numbers: a plain tile depends on the generator's code alone, a blended one also on the
    // knots of the real tiles near it. The file is named after this build of the generator, so
    // a rebuild reads none of an older one's (and deletes it on its first save); a blended tile
    // is keyed by a hash of the knots it blends on, so a changed real set recomputes just those.
    // Not the generator's own lattice cache: a warm launch skips the plain tiles that used to
    // fill it, and the 180 blended ones then cost ~4 s rebuilding it.

    private const uint HorizonCacheMagic = 0x43484755;   // "UGHC"
    private const int HorizonCacheVersion = 1;

    private readonly ConcurrentDictionary<(TileId Id, ulong Key), ushort[]> _blendedHorizon = new();
    private bool _horizonCacheRead;

    private string? HorizonCachePath => HorizonCacheDir == null ? null
        : Path.Combine(HorizonCacheDir, $"generated-horizon-{typeof(ProceduralWorld).Module.ModuleVersionId:N}.bin");

    /// <summary>FNV-1a over the ids and knots a blend is made from; never 0, which marks a plain tile on disk.</summary>
    private static ulong BlendKey(List<ProceduralWorld.RealTile> near)
    {
        ulong h = 14695981039346656037UL;
        void Mix(int v) { h = (h ^ (uint)v) * 1099511628211UL; }
        foreach (var r in near)
        {
            Mix(r.Id.E);
            Mix(r.Id.N);
            foreach (ushort k in r.Knots) Mix(k);
        }
        return h | 1;
    }

    private void LoadHorizonCache()
    {
        if (_horizonCacheRead || HorizonCachePath is not { } path) return;
        _horizonCacheRead = true;
        if (!File.Exists(path)) return;
        try
        {
            using var reader = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 16));
            if (reader.ReadUInt32() != HorizonCacheMagic || reader.ReadInt32() != HorizonCacheVersion) return;
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var id = new TileId(reader.ReadInt32(), reader.ReadInt32());
                ulong key = reader.ReadUInt64();
                var samples = new ushort[HorizonFormat.SamplesPerTile];
                for (int j = 0; j < samples.Length; j++) samples[j] = reader.ReadUInt16();
                if (key == 0) _plainHorizon.TryAdd(id, samples);
                else _blendedHorizon.TryAdd((id, key), samples);
            }
        }
        catch (Exception e) { Log?.Invoke($"[fallback] generated horizon cache unreadable: {e.Message}"); }
    }

    /// <summary>Every plain tile known, and the blended tiles of the current real set.</summary>
    private void SaveHorizonCache(List<((TileId Id, ulong Key), ushort[] Samples)> blended)
    {
        if (HorizonCachePath is not { } path) return;
        try
        {
            Directory.CreateDirectory(HorizonCacheDir!);
            foreach (var old in Directory.GetFiles(HorizonCacheDir!, "generated-horizon-*.bin"))
                if (old != path) File.Delete(old);
            string temp = path + ".tmp";
            var plain = _plainHorizon.ToArray();
            using (var writer = new BinaryWriter(new BufferedStream(File.Create(temp), 1 << 16)))
            {
                writer.Write(HorizonCacheMagic);
                writer.Write(HorizonCacheVersion);
                writer.Write(plain.Length + blended.Count);
                void Entry(TileId id, ulong key, ushort[] samples)
                {
                    writer.Write(id.E);
                    writer.Write(id.N);
                    writer.Write(key);
                    foreach (ushort v in samples) writer.Write(v);
                }
                foreach (var (id, samples) in plain) Entry(id, 0, samples);
                foreach (var ((id, key), samples) in blended) Entry(id, key, samples);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) { Log?.Invoke($"[fallback] could not cache the generated horizon: {e.Message}"); }
    }
}
