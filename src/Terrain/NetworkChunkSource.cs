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
/// <item><b>server</b> — streamed over ENet, then written into the cache.</item>
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
    private readonly Dictionary<(AssetKind, TileId), Task<AssetResult>> _inFlight = new();

    /// <summary>Tiles the server has already said it does not have, so we stop asking.</summary>
    private readonly HashSet<(AssetKind, TileId)> _knownMissing = new();

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

    private readonly object _gate = new();
    private long _cacheBytes;

    public NetworkChunkSource(
        IChunkSource local, string localDirectory, ChunkStreamer streamer, string? cacheDirectory = null)
    {
        _local = local;
        _localDirectory = localDirectory;
        _streamer = streamer;
        _cacheDirectory = cacheDirectory ?? ProjectSettings.GlobalizePath("user://chunk_cache");

        Directory.CreateDirectory(_cacheDirectory);
        _cacheBytes = MeasureCache();

        GD.Print($"[stream] cache at {_cacheDirectory} holding {_cacheBytes / (1024.0 * 1024):F0} MB");
    }

    /// <summary>
    /// Cap on the on-disk cache. The full region is 5.3 GB, so an unbounded cache would
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
            bytes => { using var ms = new MemoryStream(bytes); return BuildingCodec.Decode(ms); })
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
            bytes => { using var ms = new MemoryStream(bytes); return TreeFormat.Decode(ms); })
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

    // ---- cache and fetch ------------------------------------------------------------------

    /// <summary>
    /// Reads the cache, else streams from the server, then decodes. Decoding is done by the
    /// caller's delegate so each asset kind keeps its own codec.
    /// </summary>
    private async Task<T?> ObtainAsync<T>(
        AssetKind kind, TileId id, CancellationToken ct, Func<byte[], T> decode) where T : class
    {
        var key = (kind, id);
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

            bytes = await FetchWithRetryAsync(kind, id, key, ct).ConfigureAwait(false);
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
        AssetKind kind, TileId id, (AssetKind, TileId) key, CancellationToken ct)
    {
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            return await FetchLoopAsync(kind, id, key, ct).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<byte[]?> FetchLoopAsync(
        AssetKind kind, TileId id, (AssetKind, TileId) key, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var result = await FetchSharedAsync(kind, id, ct).ConfigureAwait(false);
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
    private Task<AssetResult> FetchSharedAsync(AssetKind kind, TileId id, CancellationToken ct)
    {
        var key = (kind, id);
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var existing)) return existing;

            var task = _streamer.FetchAsync(kind, id, ct);
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
                File.Move(temp, path, overwrite: true);
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

    private long MeasureCache()
    {
        try
        {
            return new DirectoryInfo(_cacheDirectory)
                .EnumerateFiles()
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
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
            if (_cacheBytes <= MaxCacheBytes) return;
        }

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
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* nothing useful to do */ }
    }
}
