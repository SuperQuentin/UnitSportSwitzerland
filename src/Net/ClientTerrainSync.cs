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
    /// Fetches and merges the server's manifest. Safe to call more than once; the second call
    /// returns immediately.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (Synced) return;
        await SyncHttpAsync(ct).ConfigureAwait(false);
        bool merged;
        try { merged = await SyncIndex(ct); }
        finally { IndexFinished = true; }
        if (!merged) return;
        await SyncPlacesAsync(ct).ConfigureAwait(false);
        await SyncLandingsAsync(ct).ConfigureAwait(false);
        await SyncRopewaysAsync(ct).ConfigureAwait(false);
        await SyncPoisAsync(ct).ConfigureAwait(false);
        await SyncAtlasFactsAsync(ct).ConfigureAwait(false);
        await SyncHorizonAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The tile index step is over, merged or not: the origin is settled and the LOD rings know
    /// every tile. What the loading screen waits on; the place index and the horizon that follow
    /// arrive through their events while you play.
    /// </summary>
    public bool IndexFinished { get; private set; }

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

    /// <summary>Fetches and adopts the server's tile index. False when there is nothing more to sync.</summary>
    private async Task<bool> SyncIndex(CancellationToken ct)
    {
        if (Synced) return false;

        byte[]? bytes = (await FetchPersistentAsync(AssetKind.Manifest, "terrain index", ct).ConfigureAwait(false)).Data;

        if (bytes is null)
        {
            GD.PushWarning("[stream] server sent no manifest; only local tiles will be available");
            Status?.Invoke("Server sent no terrain index — playing with local tiles only.");
            return false;
        }

        TerrainManifest manifest;
        try
        {
            manifest = TerrainManifest.FromJson(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception e)
        {
            GD.PushError($"[stream] server manifest did not parse: {e.Message}");
            Status?.Invoke("Server terrain index is unreadable — playing with local tiles only.");
            return false;
        }

        int dropped = DropChangedTiles(manifest);
        if (dropped > 0) GD.Print($"[stream] {dropped} cached tiles changed on the server; they stream again");

        // The continuation above runs on the thread pool, and what follows unloads tiles (real
        // ones replacing generated ground): main thread only.
        int added = await OnMainThread(() => _chunks.MergeAvailableTiles(manifest.Tiles.Select(t => t.Id))).ConfigureAwait(false);
        Synced = true;

        // Persist it beside the cache. Without this the cached tiles are unreachable offline:
        // the local manifest never listed them, so the LOD rings skip them and the player sees
        // nothing where they walked yesterday.
        SaveCachedIndex(bytes);

        string line = added == 0
            ? $"Terrain index synced: {manifest.Tiles.Count} tiles, all already local."
            : $"Terrain index synced: {added} of {manifest.Tiles.Count} tiles will stream from the server.";

        GD.Print($"[stream] {line}");
        Status?.Invoke(line);
        return true;
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
    private Task SyncHorizonAsync(CancellationToken ct) =>
        SyncFileAsync(AssetKind.Horizon, HorizonFormat.FileName, "horizon",
            "server has no horizon file; the world ends at the last ring", ct, bytes =>
            {
                GD.Print($"[stream] horizon received: {bytes.Length / 1024} KB");
                HorizonReceived?.Invoke();
            });

    /// <summary>
    /// Pulls the town index so the Tab teleport search works on a client that shipped without
    /// one. Purely cosmetic if it fails — /city still resolves server-side.
    /// </summary>
    private Task SyncPlacesAsync(CancellationToken ct) =>
        SyncFileAsync(AssetKind.Places, PlaceIndex.FileName, "place index",
            "server has no place index; the Tab search will stay empty", ct, bytes =>
            {
                int count = PlaceIndex.FromJson(System.Text.Encoding.UTF8.GetString(bytes)).Places.Count;
                GD.Print($"[stream] place index received: {count} towns");
                PlacesReceived?.Invoke();
            });

    /// <summary>
    /// Pulls the server's landings (#377): the piers its world has, and where its steamer lies.
    /// A few KB; a server without them (or an older one) leaves the local ones, if any.
    /// </summary>
    /// <summary>
    /// Pulls the lifts' OSM details (#915): their types, speeds, seats and names. A server without
    /// them leaves every lift as swissTLM3D gives it.
    /// </summary>
    private Task SyncRopewaysAsync(CancellationToken ct) =>
        SyncFileAsync(AssetKind.Ropeways, RopewayInfoIndex.FileName, "ropeways",
            "server has no ropeways.json; lifts keep their swissTLM3D kind and default speeds", ct, bytes =>
            {
                GD.Print($"[stream] ropeways received: {bytes.Length / 1024} KB");
                RopewaysReceived?.Invoke();
            });

    /// <summary>Pulls the points of interest (#1224): a few hundred KB, once per session.</summary>
    /// <summary>Pulls the Atlas's Wikidata facts (#1224): a few hundred KB of JSON, once per session, read when the Atlas opens.</summary>
    private Task SyncAtlasFactsAsync(CancellationToken ct) =>
        SyncFileAsync(AssetKind.AtlasFacts, "atlas_facts.json", "Atlas facts",
            "server has no atlas_facts.json; Atlas cards keep their generated line", ct,
            bytes => GD.Print($"[stream] Atlas facts received: {bytes.Length / 1024} KB"));

    private Task SyncPoisAsync(CancellationToken ct) =>
        SyncFileAsync(AssetKind.Pois, PoiFile.FileName, "points of interest",
            "server has no pois.bin; adventure places stay idle", ct, bytes =>
            {
                GD.Print($"[stream] points of interest received: {bytes.Length / 1024} KB");
                PoisReceived?.Invoke();
            });

    private Task SyncLandingsAsync(CancellationToken ct) =>
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
            });

    /// <summary>
    /// Fetches one region-wide file (tile 0,0 of <paramref name="kind"/>), writes it into the cache
    /// dir as <paramref name="fileName"/> and calls <paramref name="onDone"/>; logs and returns when
    /// the server has none or the write fails.
    /// </summary>
    private async Task SyncFileAsync(AssetKind kind, string fileName, string what, string missingMsg,
        CancellationToken ct, Action<byte[]> onDone)
    {
        string dir = Core.TerrainPaths.FindCacheDir();

        byte[]? bytes = (await _streamer
            .FetchAsync(kind, new TileId(0, 0), ct)
            .ConfigureAwait(false)).Data;

        if (bytes is null)
        {
            GD.Print($"[stream] {missingMsg}");
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes, ct).ConfigureAwait(false);
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
    /// old copy for ever. The last server index is still on disk: a tile whose height range
    /// changed since (#298 dug the lake beds, which lowered every lake tile's minimum) loses its
    /// cached height files and water layer, so it streams again. Returns how many tiles changed.
    /// </summary>
    private static int DropChangedTiles(TerrainManifest fresh)
    {
        try
        {
            string dir = Core.TerrainPaths.FindCacheDir();
            string path = Path.Combine(dir, CachedIndexFile);
            if (!File.Exists(path)) return 0;
            var before = new Dictionary<TileId, ManifestTile>();
            foreach (var t in TerrainManifest.FromJson(File.ReadAllText(path)).Tiles) before[t.Id] = t;

            int changed = 0;
            foreach (var t in fresh.Tiles)
            {
                if (!before.TryGetValue(t.Id, out var old) || (old.Min == t.Min && old.Max == t.Max)) continue;
                changed++;
                foreach (string name in new[] { ChunkFormat.ChunkFileName(t.Id), ChunkFormat.CoarseFileName(t.Id), WaterFormat.FileName(t.Id) })
                {
                    string file = Path.Combine(dir, name);
                    if (File.Exists(file)) File.Delete(file);
                }
            }
            return changed;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] could not compare the cached server index: {e.Message}");
            return 0;
        }
    }

    private void SaveCachedIndex(byte[] json)
    {
        try
        {
            string dir = Core.TerrainPaths.FindCacheDir();
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, CachedIndexFile), json);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] could not save the server index: {e.Message}");
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

            var manifest = TerrainManifest.FromJson(File.ReadAllText(path));

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
