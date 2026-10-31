using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Net;

/// <summary>
/// Reconciles the client's idea of the world with the server's, once on connect.
///
/// <para>
/// A client that shipped with part of Switzerland has a manifest listing only its own tiles.
/// The LOD rings skip anything outside that list, so without this step the streamer would
/// never be asked for a tile the client does not already have — the whole feature would
/// silently do nothing.
/// </para>
///
/// <para>
/// The server's origin is neither checked nor adopted (#185): every position on the wire is LV95,
/// and tiles are named in LV95, so each side keeps its own origin. The manifest's suggested origin
/// only says where a server's world starts.
/// </para>
/// </summary>
public sealed partial class ClientTerrainSync : Node
{
    private readonly ChunkStreamer _streamer;
    private readonly ChunkManager _chunks;

    public ClientTerrainSync(ChunkStreamer streamer, ChunkManager chunks)
    {
        _streamer = streamer;
        _chunks = chunks;
        Name = "TerrainSync";
    }

    /// <summary>Raised with a human-readable status line, for the chat log.</summary>
    public event Action<string>? Status;

    /// <summary>Raised once the town index has been cached, so the Tab search can reload.</summary>
    public event Action? PlacesReceived;

    /// <summary>The server's landings (#377) arrived and are in the cache. Any thread.</summary>
    public event Action<LandingIndex>? LandingsReceived;

    /// <summary>The lifts' OSM details arrived in the cache (#915): the lifts are rebuilt with them.</summary>
    public event Action? RopewaysReceived;

    /// <summary>The server's points of interest (#1224) are in the cache dir; fired on the sync task's thread.</summary>
    public event Action? PoisReceived;

    /// <summary>The far-horizon file arrived from the server and is in the cache.</summary>
    public event Action? HorizonReceived;

    /// <summary>True once the server manifest has been merged.</summary>
    public bool Synced { get; private set; }

    /// <summary>
    /// The server this client joined (<see cref="JoinCache.ServerKey"/>): its tile index is cached under that
    /// key (#1380). Null: no per-server cache.
    /// </summary>
    public string? ServerKey { get; set; }

    /// <summary>
    /// The index of an earlier visit to this server was merged at boot (#1380): the LOD rings already know
    /// its tiles, so the loading screen does not wait for the server to confirm it.
    /// </summary>
    public bool CachedIndexMerged { get; set; }

    /// <summary>
    /// Reconciles the client with the server, once per join. Safe to call more than once; the second call
    /// returns immediately. A returning client (#1380) asks for the server's digest first and fetches only
    /// what differs from its cache; the region files then come in parallel.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (Synced) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // the mirror probe runs beside the rest: only the bulk files need it, and the index does not
        var http = SyncHttpAsync(ct);
        var digest = await FetchDigestAsync(ct).ConfigureAwait(false);
        long digestMs = clock.ElapsedMilliseconds;
        bool merged;
        try { merged = await SyncIndex(digest, ct); }
        finally { IndexFinished = true; }
        long indexMs = clock.ElapsedMilliseconds;
        await http.ConfigureAwait(false);
        if (!merged) return;
        await Task.WhenAll(
            SyncPlacesAsync(digest, ct), SyncLandingsAsync(digest, ct), SyncRopewaysAsync(digest, ct),
            SyncPoisAsync(digest, ct), SyncAtlasFactsAsync(digest, ct), SyncHorizonAsync(digest, ct)).ConfigureAwait(false);
        GD.Print($"[stream] sync done: digest {(digest == null ? "none" : $"{digestMs} ms")}, tile index at {indexMs} ms, "
            + $"region files at {clock.ElapsedMilliseconds} ms");
    }

    private bool _indexFinished;

    /// <summary>
    /// The tile index step is over, merged or not: the origin is settled and the LOD rings know
    /// every tile. What the loading screen waits on; the place index and the horizon that follow
    /// arrive through their events while you play. True from the start when an earlier visit's index
    /// was merged at boot (<see cref="CachedIndexMerged"/>): the server's answer then only refreshes it.
    /// </summary>
    public bool IndexFinished
    {
        get => _indexFinished || CachedIndexMerged;
        private set => _indexFinished = value;
    }

    /// <summary>
    /// The hashes of the server's index and region files (#1380). Null from an older server, which does
    /// not know the kind: everything is fetched as before.
    /// </summary>
    private async Task<AssetDigest?> FetchDigestAsync(CancellationToken ct)
    {
        var result = await FetchPersistentAsync(AssetKind.Digest, "digest", ct).ConfigureAwait(false);
        return result.Data is { Length: > 0 } bytes ? AssetDigest.FromBytes(bytes) : null;
    }

    /// <summary>
    /// Asks the server for its HTTP mirror (#651) and, when it answers, routes the bulk files through
    /// it from here on. A server without one (or an older one) says "missing": ENet only, as before.
    /// </summary>
    private async Task SyncHttpAsync(CancellationToken ct)
    {
        var result = await FetchPersistentAsync(AssetKind.HttpBase, "HTTP mirror address", ct).ConfigureAwait(false);
        if (result.Data is not { Length: > 0 } bytes) return;

        string url = Encoding.UTF8.GetString(bytes).Trim();
        if (await HttpAssetSource.ProbeAsync(url, ct).ConfigureAwait(false) is not { } http) return;

        _streamer.Http = http;
        GD.Print($"[stream] tiles from {http.BaseUrl} (HTTP), the game link as fallback");
    }

    private const int PersistentAttempts = 6;

    /// <summary>
    /// A file the whole session depends on (the mirror address, the tile index), asked again with
    /// backoff after a timeout, "busy" or "not connected yet" (#1032): one lost answer used to cost a
    /// phone its terrain for the session. Only "not on this server" is final. Not tile-scoped, so any
    /// TileId does as the request key.
    /// </summary>
    private async Task<AssetResult> FetchPersistentAsync(AssetKind kind, string what, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (int attempt = 1; ; attempt++)
        {
            var result = await _streamer.FetchAsync(kind, new TileId(0, 0), ct).ConfigureAwait(false);
            if (result.Data != null || result.PermanentlyMissing || attempt == PersistentAttempts || ct.IsCancellationRequested)
                return result;
            GD.Print($"[stream] no {what} yet (attempt {attempt} of {PersistentAttempts}); asking again in {delay.TotalSeconds:0} s");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return result; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    /// <summary>
    /// Adopts the server's tile index: this client's cached copy of it when the digest says it is still
    /// the server's, else a fetch. False when there is nothing more to sync.
    /// </summary>
    private async Task<bool> SyncIndex(AssetDigest? digest, CancellationToken ct)
    {
        if (Synced) return false;

        string dir = Core.TerrainPaths.FindCacheDir();
        byte[]? bytes = null;
        TerrainManifest? cached = ReadCachedIndex(dir, ServerKey == null ? null : JoinCache.ManifestFileName(ServerKey));
        bool unchanged = false;
        if (cached != null && digest != null && ServerKey != null)
        {
            byte[] stored = File.ReadAllBytes(Path.Combine(dir, JoinCache.ManifestFileName(ServerKey)));
            if (JoinCache.Sha256Hex(stored) == digest.Manifest) { bytes = stored; unchanged = true; }
        }

        TerrainManifest manifest;
        if (unchanged)
        {
            manifest = cached!;
            GD.Print("[stream] terrain index: the copy from the last visit is still the server's, not fetched again");
        }
        else
        {
            bytes = (await FetchPersistentAsync(AssetKind.Manifest, "terrain index", ct).ConfigureAwait(false)).Data;

            if (bytes is null)
            {
                GD.PushWarning("[stream] server sent no manifest; only local tiles will be available");
                Status?.Invoke("Server sent no terrain index — playing with local tiles only.");
                return false;
            }

            try
            {
                manifest = TerrainManifest.FromJson(Encoding.UTF8.GetString(bytes)).UseFormat();
            }
            catch (Exception e)
            {
                GD.PushError($"[stream] server manifest did not parse: {e.Message}");
                Status?.Invoke("Server terrain index is unreadable — playing with local tiles only.");
                return false;
            }

            int dropped = DropChangedTiles(cached ?? ReadCachedIndex(dir, CachedIndexFile), manifest);
            if (dropped > 0) GD.Print($"[stream] {dropped} cached tiles changed on the server; they stream again");
        }

        // The continuation above runs on the thread pool, and what follows unloads tiles (real
        // ones replacing generated ground): main thread only. Nothing to do when the boot merge
        // already knew these tiles.
        int added = await OnMainThread(() => _chunks.MergeAvailableTiles(manifest.Tiles.Select(t => t.Id))).ConfigureAwait(false);
        Synced = true;

        // Persist it beside the cache. Without this the cached tiles are unreachable offline:
        // the local manifest never listed them, so the LOD rings skip them and the player sees
        // nothing where they walked yesterday. Per server too (#1380), so the next join merges it at boot.
        if (!unchanged) SaveCachedIndex(bytes!);

        string line = unchanged
            ? $"Terrain index unchanged since the last visit: {manifest.Tiles.Count} tiles."
            : $"Terrain index synced: {manifest.Tiles.Count} tiles ({added} new to this client).";

        GD.Print($"[stream] {line}");
        Status?.Invoke(line);
        _ = Task.Run(() => CountUncached(manifest, dir), CancellationToken.None);
        return true;
    }

    /// <summary>Log only: how many of the index's tiles are in neither the tile cache nor the local terrain (they stream on demand).</summary>
    private static void CountUncached(TerrainManifest manifest, string cacheDir)
    {
        try
        {
            string chunkDir = Core.TerrainPaths.FindChunkDir();
            int uncached = 0;
            foreach (var t in manifest.Tiles)
            {
                string coarse = ChunkFormat.CoarseFileName(t.Id), full = ChunkFormat.ChunkFileName(t.Id);
                if (File.Exists(Path.Combine(cacheDir, coarse)) || File.Exists(Path.Combine(cacheDir, full))
                    || File.Exists(Path.Combine(chunkDir, coarse)) || File.Exists(Path.Combine(chunkDir, full))) continue;
                uncached++;
            }
            GD.Print($"[stream] {uncached} of {manifest.Tiles.Count} tiles are not on this client yet and stream when they come near");
        }
        catch (Exception e) { GD.PushWarning($"[stream] could not count the uncached tiles: {e.Message}"); }
    }

    private static Task<T> OnMainThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        }).CallDeferred();
        return done.Task;
    }

    /// <summary>
    /// Pulls the region's far-horizon lattice so a client streaming everything still sees the
    /// mountains past its LOD rings. 1.6 MB for the current region, once per session.
    /// </summary>
    private Task SyncHorizonAsync(AssetDigest? digest, CancellationToken ct) =>
        SyncFileAsync(AssetKind.Horizon, HorizonFormat.FileName, "horizon",
            "server has no horizon file; the world ends at the last ring", ct, bytes =>
            {
                GD.Print($"[stream] horizon received: {bytes.Length / 1024} KB");
                HorizonReceived?.Invoke();
            }, digest, readAtBoot: true);

    /// <summary>
    /// Pulls the town index so the Tab teleport search works on a client that shipped without
    /// one. Purely cosmetic if it fails — /city still resolves server-side.
    /// </summary>
    private Task SyncPlacesAsync(AssetDigest? digest, CancellationToken ct) =>
        SyncFileAsync(AssetKind.Places, PlaceIndex.FileName, "place index",
            "server has no place index; the Tab search will stay empty", ct, bytes =>
            {
                int count = PlaceIndex.FromJson(System.Text.Encoding.UTF8.GetString(bytes)).Places.Count;
                GD.Print($"[stream] place index received: {count} towns");
                PlacesReceived?.Invoke();
            }, digest);

    /// <summary>
    /// Pulls the server's landings (#377): the piers its world has, and where its steamer lies.
    /// A few KB; a server without them (or an older one) leaves the local ones, if any.
    /// </summary>
    /// <summary>
    /// Pulls the lifts' OSM details (#915): their types, speeds, seats and names. A server without
    /// them leaves every lift as swissTLM3D gives it.
    /// </summary>
    private Task SyncRopewaysAsync(AssetDigest? digest, CancellationToken ct) =>
        SyncFileAsync(AssetKind.Ropeways, RopewayInfoIndex.FileName, "ropeways",
            "server has no ropeways.json; lifts keep their swissTLM3D kind and default speeds", ct, bytes =>
            {
                GD.Print($"[stream] ropeways received: {bytes.Length / 1024} KB");
                RopewaysReceived?.Invoke();
            }, digest);

    /// <summary>Pulls the points of interest (#1224): a few hundred KB, once per session.</summary>
    /// <summary>Pulls the Atlas's Wikidata facts (#1224): a few hundred KB of JSON, once per session, read when the Atlas opens.</summary>
    private Task SyncAtlasFactsAsync(AssetDigest? digest, CancellationToken ct) =>
        SyncFileAsync(AssetKind.AtlasFacts, "atlas_facts.json", "Atlas facts",
            "server has no atlas_facts.json; Atlas cards keep their generated line", ct,
            bytes => GD.Print($"[stream] Atlas facts received: {bytes.Length / 1024} KB"), digest);

    private Task SyncPoisAsync(AssetDigest? digest, CancellationToken ct) =>
        SyncFileAsync(AssetKind.Pois, PoiFile.FileName, "points of interest",
            "server has no pois.bin; adventure places stay idle", ct, bytes =>
            {
                GD.Print($"[stream] points of interest received: {bytes.Length / 1024} KB");
                PoisReceived?.Invoke();
            }, digest);

    private Task SyncLandingsAsync(AssetDigest? digest, CancellationToken ct) =>
        SyncFileAsync(AssetKind.Landings, LandingIndex.FileName, "landings",
            "server has no landings; piers only where this client's own data has them", ct, bytes =>
            {
                LandingIndex index;
                try { index = LandingIndex.FromJson(Encoding.UTF8.GetString(bytes)); }
                catch (Exception e)
                {
                    GD.PushWarning($"[stream] the server's landings do not parse: {e.Message}");
                    return;
                }
                GD.Print($"[stream] landings received: {index.Landings.Count} landings, {index.Jetties.Count} jetties");
                LandingsReceived?.Invoke(index);
            }, digest);

    /// <summary>
    /// Fetches one region-wide file (tile 0,0 of <paramref name="kind"/>), writes it into the cache
    /// dir as <paramref name="fileName"/> and calls <paramref name="onDone"/>; logs and returns when
    /// the server has none or the write fails. With the server's <paramref name="digest"/> (#1380) a
    /// cached copy that has the server's hash is not fetched again (<paramref name="onDone"/> still runs on
    /// it, unless <paramref name="readAtBoot"/>: the world read that file when it started), and a file the
    /// digest does not list is not asked for.
    /// </summary>
    private async Task SyncFileAsync(AssetKind kind, string fileName, string what, string missingMsg,
        CancellationToken ct, Action<byte[]> onDone, AssetDigest? digest = null, bool readAtBoot = false)
    {
        string dir = Core.TerrainPaths.FindCacheDir();
        string path = Path.Combine(dir, fileName);

        if (digest != null)
        {
            if (!digest.Files.TryGetValue(fileName, out string? hash))
            {
                GD.Print($"[stream] {missingMsg}");
                return;
            }
            if (await Task.Run(() => JoinCache.FileHasHash(path, hash), ct).ConfigureAwait(false))
            {
                GD.Print($"[stream] {what}: the copy from the last visit is still the server's");
                if (!readAtBoot)
                {
                    try { onDone(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        GD.PushWarning($"[stream] could not read the cached {what}: {e.Message}");
                    }
                }
                return;
            }
        }

        // a busy server is asked again, as for the index: only "not on this server" is final
        AssetResult result = default;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            result = await _streamer.FetchAsync(kind, new TileId(0, 0), ct).ConfigureAwait(false);
            if (result.Data != null || result.PermanentlyMissing || ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        byte[]? bytes = result.Data;

        if (bytes is null)
        {
            GD.Print($"[stream] {missingMsg}");
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] could not cache the {what}: {e.Message}");
            return;
        }

        onDone(bytes);
    }

    /// <summary>Filename of the cached copy of the server's index.</summary>
    public const string CachedIndexFile = "server-manifest.json";

    /// <summary>
    /// The cache is keyed by file name only, so a tile the server rebuilt would be served from the
    /// old copy for ever. The last server index is still on disk: a tile whose content changed since
    /// (its hash, else its height range: #298 dug the lake beds, which lowered every lake tile's minimum)
    /// loses its cached height files and water layer, so it streams again. Returns how many tiles changed.
    /// </summary>
    private static int DropChangedTiles(TerrainManifest? before, TerrainManifest fresh)
    {
        try
        {
            string dir = Core.TerrainPaths.FindCacheDir();
            var changed = JoinCache.ChangedTiles(before, fresh);
            foreach (var t in changed)
                foreach (string name in new[] { ChunkFormat.ChunkFileName(t.Id), ChunkFormat.CoarseFileName(t.Id), WaterFormat.FileName(t.Id) })
                {
                    string file = Path.Combine(dir, name);
                    if (File.Exists(file)) File.Delete(file);
                }
            return changed.Count;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] could not compare the cached server index: {e.Message}");
            return 0;
        }
    }

    /// <summary>A cached index, or null when there is none or it does not read.</summary>
    private static TerrainManifest? ReadCachedIndex(string dir, string? fileName)
    {
        if (fileName == null) return null;
        try
        {
            string path = Path.Combine(dir, fileName);
            return File.Exists(path) ? TerrainManifest.FromJson(File.ReadAllText(path)) : null;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] cached server index {fileName} unreadable: {e.Message}");
            return null;
        }
    }

    private void SaveCachedIndex(byte[] json)
    {
        try
        {
            string dir = Core.TerrainPaths.FindCacheDir();
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, CachedIndexFile), json);
            if (ServerKey != null) File.WriteAllBytes(Path.Combine(dir, JoinCache.ManifestFileName(ServerKey)), json);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] could not save the server index: {e.Message}");
        }
    }

    /// <summary>
    /// Merges the index of an earlier visit to this server at boot (#1380), before the first frame, so the
    /// rings may ask for its tiles at once: no origin check, the key is the server's own address and world.
    /// </summary>
    /// <returns>True when a cached index of this server was merged (even if it added nothing).</returns>
    public static bool MergeServerIndex(ChunkManager chunks, string serverKey)
    {
        try
        {
            var manifest = ReadCachedIndex(Core.TerrainPaths.FindCacheDir(), JoinCache.ManifestFileName(serverKey));
            if (manifest == null) return false;
            int added = chunks.MergeAvailableTiles(manifest.Tiles.Select(t => t.Id));
            GD.Print($"[stream] index of the last visit to this server merged at boot: {manifest.Tiles.Count} tiles ({added} new to this client)");
            return true;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] cached index of this server unreadable: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Merges a previously saved server index at boot, so terrain streamed in an earlier
    /// session is reachable without a server. Only an index from this world: its suggested
    /// origin must be this copy's starting one. That is no longer about precision (positions on
    /// the wire are LV95, #185) but about identity: an index from another server lists real tiles
    /// this one may not have, and merged they replace the generated ground with tiles that never
    /// arrive (a stale loopback index left the spawn with no ground at all).
    /// </summary>
    /// <returns>How many tiles the cached index added.</returns>
    public static int MergeCachedIndex(ChunkManager chunks, WorldOrigin origin)
    {
        try
        {
            string path = Path.Combine(Core.TerrainPaths.FindCacheDir(), CachedIndexFile);
            if (!File.Exists(path)) return 0;

            var manifest = TerrainManifest.FromJson(File.ReadAllText(path)).UseFormat();

            if (Math.Abs(manifest.SuggestedOriginLv95.E - origin.E) > 0.5
                || Math.Abs(manifest.SuggestedOriginLv95.N - origin.N) > 0.5)
            {
                GD.PushWarning("[stream] cached server index is from another world (another origin); ignored");
                return 0;
            }

            int added = chunks.MergeAvailableTiles(manifest.Tiles.Select(t => t.Id));
            if (added > 0)
                GD.Print($"[stream] cached server index adds {added} tile(s) from earlier sessions");
            return added;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] cached server index unreadable: {e.Message}");
            return 0;
        }
    }
}
