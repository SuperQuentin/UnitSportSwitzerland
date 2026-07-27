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

    /// <summary>Bytes currently held in the cache.</summary>
    public long CacheBytes { get { lock (_gate) return _cacheBytes; } }

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

    public async Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default)
    {
        if (await _local.LoadTreesAsync(id, ct).ConfigureAwait(false) is { } local) return local;

        return await ObtainAsync(AssetKind.Trees, id, ct,
            bytes => { using var ms = new MemoryStream(bytes); return TreeFormat.Decode(ms); })
            .ConfigureAwait(false);
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

        if (bytes is null)
        {
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
            // would be trusted on the next run.
            string temp = path + ".part";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);

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

    /// <summary>Empties the cache. Exposed for a "clear downloaded terrain" action.</summary>
    public void ClearCache()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_cacheDirectory).EnumerateFiles())
                TryDelete(file.FullName);

            lock (_gate)
            {
                _cacheBytes = 0;
                _knownMissing.Clear();
            }
            GD.Print("[stream] cache cleared");
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] cache clear failed: {e.Message}");
        }
    }
}
