using Godot;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// A chunk source that falls back to the server for anything the client does not have.
///
/// <para>Three tiers, tried in order:</para>
/// <list type="number">
/// <item><b>shipped</b> — the local <c>terrain_chunks/</c> directory, unchanged;</item>
/// <item><b>cache</b> — <c>user://chunk_cache/</c>, everything fetched in earlier sessions;</item>
/// <item><b>server</b> — the server's HTTP mirror when it names one (#651), else ENet; then written into the cache.</item>
/// </list>
///
/// <para>
/// Fetched files are cached under their ordinary filename, so nothing downstream knows the
/// difference: <see cref="ChunkCodec"/> and friends decode a streamed tile exactly as they
/// decode a shipped one. That is also why the transfer unit is the raw file rather than a
/// decoded structure — re-encoding would produce bytes the preprocessor never wrote.
/// </para>
///
/// <para>
/// A miss at every tier returns null, which the streamer already treats as "tile unavailable"
/// and renders as a hole in the world rather than an error.
/// </para>
/// </summary>
public sealed class NetworkChunkSource : IChunkSource
{
    private readonly IChunkSource _local;
    private readonly string _localDirectory;
    private readonly string _cacheDirectory;
    private readonly ChunkStreamer _streamer;

    /// <summary>In-flight fetches, so two LOD rings asking for the same tile share one transfer.</summary>
    private readonly Dictionary<Ask, Task<AssetResult>> _inFlight = new();

    /// <summary>Tiles the server has already said it does not have, so we stop asking.</summary>
    private readonly HashSet<Ask> _knownMissing = new();

    /// <summary>What one transfer asks for: a tile's file, or a cube key's entry and section (#1655).</summary>
    private readonly record struct Ask(AssetKind Kind, TileId Tile, CubeKey Cube = default, int Section = 0)
    {
        public override string ToString() => Kind is AssetKind.Leaf or AssetKind.Pyramid ? $"{Kind} {Cube}" : $"{Kind} {Tile}";

        /// <summary>Through the two section slots: a leaf's one section, never ahead of the near ground.</summary>
        public bool Bounded => Section != 0 && Kind != AssetKind.Pyramid;
    }

    /// <summary>
    /// Ceiling on transfers in flight from this client.
    ///
    /// <para>
    /// The LOD rings reach nine tiles in every direction, so arriving somewhere new makes
    /// 361 tiles want their .terr at the same instant — around 177 MB of simultaneous demand.
    /// Without a budget here the client simply floods the server, which refuses most of it,
    /// and the retries then fight each other: measured 1,135 refusals in a 30 second window
    /// while only 33 MB actually arrived.
    /// </para>
    /// <para>
    /// Holding the queue short instead lets the server's bandwidth meter do the pacing, which
    /// is what it is for.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _slots = new(6, 6);

    /// <summary>
    /// A leaf's one section (the globe's far heights, the traffic's roads, #1655) takes one of these too: two at most, so
    /// hundreds of far asks never queue the near ground (whole entries) behind them on the server's metered link.
    /// </summary>
    private readonly SemaphoreSlim _sectionSlots = new(2, 2);

    /// <summary>
    /// The globe's pyramid tiles (#1656) take these, none of the others: a climb asks for hundreds, and through the two
    /// section slots (shared with the leaves' far heights) it streamed three tiles a second and drew levels 4 coarser than
    /// a copy with the pyramid on its disk. The server's meter paces them with the rest.
    /// </summary>
    private readonly SemaphoreSlim _pyramidSlots = new(3, 3);

    private readonly object _gate = new();
    private bool _trimming;
    private long _lastTrim = -1;   // Stopwatch timestamp of the last trim, -1 = never
    private long _cacheBytes;

    public NetworkChunkSource(
        IChunkSource local, string localDirectory, ChunkStreamer streamer, string? cacheDirectory = null)
    {
        _local = local;
        _localDirectory = localDirectory;
        _streamer = streamer;
        _cacheDirectory = cacheDirectory ?? ProjectSettings.GlobalizePath("user://chunk_cache");

        Directory.CreateDirectory(_cacheDirectory);
        _cacheBytes = MeasureCache(_cacheDirectory);

        GD.Print($"[stream] cache at {_cacheDirectory} holding {_cacheBytes / (1024.0 * 1024):F0} MB");
    }

    /// <summary>
    /// Cap on the on-disk cache. The full region is ~112 GB, so an unbounded cache would
    /// quietly fill a disk over a few sessions.
    /// </summary>
    public long MaxCacheBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Files served from the cache or the network this session.</summary>
    public int StreamedFiles { get; private set; }

    // ---- IChunkSource --------------------------------------------------------------------

    /// <summary>
    /// The manifest always comes from local disk. In multiplayer the server's manifest is
    /// fetched separately by <see cref="ClientTerrainSync"/> and merged, because the origin it
    /// implies has to be reconciled before any coordinate is computed.
    /// </summary>
    public Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default) =>
        _local.LoadManifestAsync(ct);

    public async Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadChunkAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Chunk, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return ChunkCodec.Decode(ms); })
            .ConfigureAwait(false);
    }

    public async Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadCoarseChunkAsync(id, ct).ConfigureAwait(false) is { } local)
            return local;

        return await ObtainAsync(AssetKind.ChunkCoarse, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return ChunkCodec.Decode(ms); })
            .ConfigureAwait(false);
    }

    public async Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadRoadsAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Roads, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return RoadCodec.Decode(ms); })
            .ConfigureAwait(false);
    }

    public async Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadHolesAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Holes, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return HoleFormat.Decode(ms); })
            .ConfigureAwait(false);
    }

    public async Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadBuildingsAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Buildings, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return Stadiums.StadiumClip.Clip(BuildingCodec.Decode(ms)); })
            .ConfigureAwait(false);
    }

    public async Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadCoverAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Cover, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return CoverFormat.Decode(ms); })
            .ConfigureAwait(false);
    }

    /// <summary>The <c>.water</c> layer (#298): shipped, cached, else streamed like the cover.</summary>
    public async Task<WaterTile?> LoadWaterAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadWaterAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Water, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return WaterFormat.Decode(ms).ToTile(); })
            .ConfigureAwait(false);
    }

    public async Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadTreesAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Trees, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return Stadiums.StadiumClip.Trees(id, TreeFormat.Decode(ms)); })
            .ConfigureAwait(false);
    }

    /// <summary>The farm fields (#494): shipped, cached, else streamed like the trees. An older server answers "missing".</summary>
    public async Task<List<FieldPolygon>?> LoadFieldsAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadFieldsAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Fields, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return FieldFormat.Decode(ms); })
            .ConfigureAwait(false);
    }

    /// <summary>The ski slopes (#887): shipped, cached, else streamed. An older server answers "missing".</summary>
    public async Task<PisteTile?> LoadPistesAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadPistesAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Pistes, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return PisteFormat.Decode(ms); })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shipped copy first, else the one <see cref="ClientTerrainSync"/> pulled into the cache
    /// during sync. Not fetched on demand here: it is a single region-wide file, and the sync
    /// already knows the moment a server is there to ask.
    /// </summary>
    public async Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default)
    {
        if (await _local.LoadHorizonAsync(ct).ConfigureAwait(false) is { } local) return local;

        string path = Path.Combine(_cacheDirectory, HorizonFormat.FileName);
        if (!File.Exists(path)) return null;
        return await Task.Run(() =>
        {
            using var fs = File.OpenRead(path);
            return HorizonFormat.Decode(fs);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The shipped landings first, else the server's, pulled into the cache during sync (#377).</summary>
    public Task<AirportIndex?> LoadAirportsAsync(CancellationToken ct = default) => _local.LoadAirportsAsync(ct);

    /// <summary>The lifts' OSM details (#915): shipped, else the copy <see cref="ClientTerrainSync"/> pulled into the cache.</summary>
    public async Task<RopewayInfoIndex?> LoadRopewayInfoAsync(CancellationToken ct = default) =>
        await _local.LoadRopewayInfoAsync(ct).ConfigureAwait(false)
        ?? await LocalChunkSource.ReadRopewayInfoAsync(_cacheDirectory, ct).ConfigureAwait(false);

    /// <summary>The points of interest (#1224): shipped, else the server's copy <see cref="ClientTerrainSync"/> pulled into the cache.</summary>
    public async Task<byte[]?> LoadPoisAsync(CancellationToken ct = default) =>
        await _local.LoadPoisAsync(ct).ConfigureAwait(false)
        ?? await LocalChunkSource.ReadPoisAsync(_cacheDirectory, ct).ConfigureAwait(false);

    public async Task<LandingIndex?> LoadLandingsAsync(CancellationToken ct = default) =>
        await _local.LoadLandingsAsync(ct).ConfigureAwait(false)
        ?? await LocalChunkSource.ReadLandingsAsync(_cacheDirectory, ct).ConfigureAwait(false);

    // ---- leaves (#1655) --------------------------------------------------------------------

    /// <summary>
    /// The earth world's leaves the server holds and this copy lacks (#1655, <see cref="Leaves.LeafStore.UseRemote"/>'s
    /// <see cref="Leaves.LeafFetch"/>): the cache first (<see cref="AssetStream.LeafCacheName"/>: the whole entry serves a
    /// section too), else the stream, with the tiles' retries, coalescing, request slots and "the server has none"
    /// memory; written into the cache, counted in <see cref="ChunkStreamer.SessionBytes"/>. Called on a worker: blocks for
    /// the transfer. Null when it did not come (the caller asks again later) or did not check out.
    /// </summary>
    public byte[]? FetchLeaf(CubeKey key, Leaves.LeafIndex.Entry entry, LeafKind? section)
    {
        string wholePath = Path.Combine(_cacheDirectory, AssetStream.LeafCacheName(key, entry.Stamp, null));
        if (ReadCache(wholePath) is { } whole && Fits(whole, null)) return whole;
        string path = section == null ? wholePath : Path.Combine(_cacheDirectory, AssetStream.LeafCacheName(key, entry.Stamp, section));
        if (section != null && ReadCache(path) is { } part && Fits(part, section)) return part;

        // Low data (#63): the near leaves are the only ground and always come; a leaf's heights alone are the globe's far
        // view past them (and the blend's, which reads a held leaf's own), so they come from the disk and the cache only
        if (section == LeafKind.Heights && Core.GameSettings.Current.LowDataActive) return null;

        var ask = new Ask(AssetKind.Leaf, default, key, (int)(section ?? 0));
        lock (_gate)
        {
            if (_knownMissing.Contains(ask)) return null;
        }
        if (!_streamer.ServerReachable) return null;
        var bytes = FetchWithRetryAsync(ask, CancellationToken.None).GetAwaiter().GetResult();
        if (bytes == null) return null;
        if (!Fits(bytes, section))
        {
            GD.PushWarning($"[stream] leaf {key} came as {bytes.Length} bytes, not the index's {entry.Length}: not used");
            return null;
        }
        WriteCache(path, bytes);
        StreamedFiles++;
        return bytes;

        // a whole entry is the index's size; any entry is this leaf's
        bool Fits(byte[] b, LeafKind? s)
        {
            try { return (s != null || b.Length == entry.Length) && CubeLeafCodec.ReadTable(b).Key == key; }
            catch (InvalidDataException) { return false; }
        }
    }

    /// <summary>
    /// Under Low data (#63) the pyramid's levels deeper than this come from the cache only (#1656): the globe from orbit
    /// and the country's shape still come (levels 0-6, a few MB), the fine relief round the player does not.
    /// </summary>
    public const int LowDataPyramidLevel = 6;

    /// <summary>
    /// A tile of the server's pyramid this copy lacks (#1656, <see cref="Leaves.PyramidSource.UseRemote"/>'s
    /// <see cref="Leaves.PyramidFetch"/>): section 0 its heights, 1-12 a month's colour. The cache first
    /// (<see cref="AssetStream.PyramidCacheName"/>), else the stream, with the leaves' retries, slots and memory of what the
    /// server lacks; written into the cache. Called on a worker: blocks for the transfer. Null when it did not come.
    /// </summary>
    public byte[]? FetchPyramid(CubeKey key, Leaves.PyramidIndex.Entry entry, int section)
    {
        string path = Path.Combine(_cacheDirectory, AssetStream.PyramidCacheName(key, entry.Stamp, section));
        if (ReadCache(path) is { } cached && Fits(cached)) return cached;
        if (key.Level > LowDataPyramidLevel && Core.GameSettings.Current.LowDataActive) return null;

        // through the pyramid's own slots: the near leaves keep theirs
        var ask = new Ask(AssetKind.Pyramid, default, key, section);
        lock (_gate)
        {
            if (_knownMissing.Contains(ask)) return null;
        }
        if (!_streamer.ServerReachable) return null;
        var bytes = FetchWithRetryAsync(ask, CancellationToken.None).GetAwaiter().GetResult();
        if (bytes == null) return null;
        if (!Fits(bytes))
        {
            GD.PushWarning($"[stream] pyramid tile {key} section {section} came as {bytes.Length} bytes that do not read as it: not used");
            return null;
        }
        WriteCache(path, bytes);
        StreamedFiles++;
        return bytes;

        // the heights are the index's size and this tile's; a colour entry is this tile's
        bool Fits(byte[] b)
        {
            try
            {
                return section switch
                {
                    0 => b.Length == entry.Length && CubeTileCodec.ReadHeader(b).Key == key,
                    Leaves.PyramidSource.FeaturesSection => CubeFarFeaturesCodec.Decode(b).Key == key,
                    _ => CubeAlbedoCodec.Decode(b).Key == key,
                };
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException) { return false; }
        }
    }

    // ---- cache and fetch ------------------------------------------------------------------

    /// <summary>
    /// Reads the cache, else streams from the server, then decodes. Decoding is done by the
    /// caller's delegate so each asset kind keeps its own codec.
    /// </summary>
    private async Task<T?> ObtainAsync<T>(
        AssetKind kind, TileId id, CancellationToken ct, Func<byte[], T> decode) where T : class
    {
        var key = new Ask(kind, id);
        lock (_gate)
        {
            if (_knownMissing.Contains(key)) return null;
        }

        string cachePath = Path.Combine(_cacheDirectory, AssetStream.FileNameFor(kind, id));
        byte[]? bytes = ReadCache(cachePath);

        // The cache is keyed by filename only, so a .bldg fetched before a format bump would be
        // served forever. v1 still decodes, it just files garages as Annex: refetch it.
        if (bytes is not null && kind == AssetKind.Buildings && bytes.Length >= 6
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) < BuildingFormat.Version)
        {
            TryDelete(cachePath);
            bytes = null;
        }

        // Same for a .road cached before the v3 bump: it still decodes (no attributes), so it is
        // only refetched while a server can send the new one; offline the old copy still serves.
        // ponytail: a v2-only server gets asked again every session; per-file hashes in the
        // manifest would make every cache check exact.
        if (bytes is not null && kind == AssetKind.Roads && bytes.Length >= 6 && _streamer.ServerReachable
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) < RoadFormat.Version)
            bytes = null;

        if (bytes is null)
        {
            // With no server there is nothing to fetch from, and the retry ladder cannot help.
            //
            // This one check is worth more than everything else in the loading path put together.
            // Most tiles legitimately have no .holes file — 632 of 6,699 do — so an offline
            // single-player session asked the network for a file that does not exist, was told
            // "not connected", treated that as *transient*, and retried five times with backoff:
            // 0.4 + 0.9 + 2 + 4 = 7.3 seconds per tile, while holding one of the six global fetch
            // slots. Six slots over 7.3 seconds is a hard ceiling of 0.8 tiles per second no
            // matter how fast the disk is, and that ceiling — not file size, not meshing — is
            // what made a cold start take ten minutes.
            //
            // Deliberately not recorded in _knownMissing: a client that connects later must be
            // able to ask for exactly these files.
            if (!_streamer.ServerReachable) return null;

            bytes = await FetchWithRetryAsync(key, ct).ConfigureAwait(false);
            if (bytes is null) return null;

            WriteCache(cachePath, bytes);
        }

        StreamedFiles++;

        try
        {
            return decode(bytes);
        }
        catch (Exception e)
        {
            // A file that will not decode is worse than a missing one, because it will be
            // read again next session. Drop it from the cache and treat the tile as absent.
            GD.PushWarning($"[stream] {kind} {id} did not decode ({e.Message}); dropping from cache");
            TryDelete(cachePath);
            lock (_gate) _knownMissing.Add(key);
            return null;
        }
    }

    /// <summary>
    /// Delays between retries of a transiently failed transfer.
    ///
    /// <para>
    /// Retrying here rather than letting the caller see a null matters because
    /// <see cref="ChunkManager"/> records a tile as having no roads, no buildings or no trees
    /// the first time a load comes back empty — which is correct for a local file, where
    /// absent means absent, and wrong over a network, where it usually means "the server was
    /// busy for a moment". Without this a client that arrives somewhere new gets bare terrain
    /// with no roads or buildings for the rest of the session.
    /// </para>
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private async Task<byte[]?> FetchWithRetryAsync(
        Ask key, CancellationToken ct)
    {
        try
        {
            if (key.Kind == AssetKind.Pyramid) await _pyramidSlots.WaitAsync(ct).ConfigureAwait(false);
            else
            {
                if (key.Bounded) await _sectionSlots.WaitAsync(ct).ConfigureAwait(false);
                try { await _slots.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (key.Bounded) _sectionSlots.Release(); throw; }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            return await FetchLoopAsync(key, ct).ConfigureAwait(false);
        }
        finally
        {
            if (key.Kind == AssetKind.Pyramid) _pyramidSlots.Release();
            else
            {
                _slots.Release();
                if (key.Bounded) _sectionSlots.Release();
            }
        }
    }

    private async Task<byte[]?> FetchLoopAsync(
        Ask key, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var result = await FetchSharedAsync(key, ct).ConfigureAwait(false);
            if (result.Data is { } data) return data;

            // The server said it genuinely does not have this file. Remember it: with the LOD
            // rings re-evaluating every frame, asking again forever would be a request storm.
            if (result.PermanentlyMissing)
            {
                lock (_gate) _knownMissing.Add(key);
                return null;
            }

            if (ct.IsCancellationRequested || attempt >= RetryDelays.Length) return null;

            try
            {
                await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Coalesces concurrent requests for the same file. The LOD rings ask for a tile from
    /// several distances at once, and without this each would open its own transfer.
    /// </summary>
    private Task<AssetResult> FetchSharedAsync(Ask key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var existing)) return existing;

            var task = key.Kind is AssetKind.Leaf or AssetKind.Pyramid
                ? _streamer.FetchCubeAsync(key.Kind, key.Cube, key.Section, ct)
                : _streamer.FetchAsync(key.Kind, key.Tile, ct);
            _inFlight[key] = task;

            _ = task.ContinueWith(_ =>
            {
                lock (_gate) _inFlight.Remove(key);
            }, TaskScheduler.Default);

            return task;
        }
    }

    private static byte[]? ReadCache(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] cache read failed for {path}: {e.Message}");
            return null;
        }
    }

    private void WriteCache(string path, byte[] bytes)
    {
        try
        {
            // Write beside then move, so a crash mid-write cannot leave a truncated file that
            // would be trusted on the next run. The temp name is unique per write: two fetches
            // of the same asset (a coarse and a full load, or the rings and a blend) complete
            // concurrently, and a shared ".part" was moved away by one under the other. The
            // move replaces atomically, and both write the same bytes, so the last one wins.
            string temp = $"{path}.{Guid.NewGuid():N}.part";
            try
            {
                File.WriteAllBytes(temp, bytes);
                try
                {
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && File.Exists(path))
                {
                    // Windows refuses to replace a file another thread is reading: the twin fetch
                    // already put the same bytes there (HTTP delivers fast enough to hit this often).
                    return;
                }
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            lock (_gate) _cacheBytes += bytes.Length;
            EvictIfOversized();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] cache write failed for {path}: {e.Message}");
        }
    }

    /// <summary>Bytes the tile cache in <paramref name="directory"/> holds (Settings → Data shows it).</summary>
    public static long MeasureCache(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles()
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The source of the world being played, if any: Settings → Data clears through it.</summary>
    public static NetworkChunkSource? Active { get; set; }

    /// <summary>
    /// Settings → Data → Clear (#63): deletes the cached tiles in <paramref name="directory"/>
    /// (never a download in progress, a <c>.part</c>). Tiles already loaded stay; they come back
    /// from the server when next needed. Returns the bytes freed.
    /// </summary>
    public static long ClearCache(string directory)
    {
        long freed = 0;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (file.Extension == ".part") continue;
                try
                {
                    long size = file.Length;
                    file.Delete();
                    freed += size;
                }
                catch (IOException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        if (Active is { } live && Path.GetFullPath(live._cacheDirectory) == Path.GetFullPath(directory))
            lock (live._gate) live._cacheBytes = MeasureCache(directory);
        GD.Print($"[stream] cache cleared: {freed / (1024.0 * 1024):F0} MB");
        return freed;
    }

    /// <summary>
    /// Trims the cache back under the cap, oldest first.
    ///
    /// Ordering is by last write rather than last access: Windows disables access-time
    /// updates by default, so an access-ordered policy would silently degrade to arbitrary.
    /// </summary>
    private void EvictIfOversized()
    {
        lock (_gate)
        {
            if (!CacheTrimPolicy.ShouldTrim(_cacheBytes, MaxCacheBytes, _trimming,
                    _lastTrim < 0 ? double.MaxValue : System.Diagnostics.Stopwatch.GetElapsedTime(_lastTrim).TotalSeconds)) return;
            _trimming = true;
        }

        // off the write path (#1381): the scan sorts the whole directory, and writes arrive in bursts
        Task.Run(Trim);
    }

    private void Trim()
    {
        try
        {
            var files = new DirectoryInfo(_cacheDirectory)
                .EnumerateFiles()
                .Where(f => f.Extension != ".part")   // another write's, still in flight
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            long freed = 0;
            long target;
            lock (_gate) target = _cacheBytes - (long)(MaxCacheBytes * 0.9);

            foreach (var file in files)
            {
                if (freed >= target) break;
                long size = file.Length;
                try
                {
                    file.Delete();
                    freed += size;
                }
                catch { /* in use, skip it */ }
            }

            lock (_gate) _cacheBytes = Math.Max(0, _cacheBytes - freed);
            GD.Print($"[stream] cache trimmed by {freed / (1024.0 * 1024):F0} MB");
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] cache eviction failed: {e.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _lastTrim = System.Diagnostics.Stopwatch.GetTimestamp();
                _trimming = false;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* nothing useful to do */ }
    }
}
