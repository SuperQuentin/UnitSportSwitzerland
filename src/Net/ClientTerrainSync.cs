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
/// The origin is checked, not adopted. Every coordinate in the session is an offset from it,
/// so if the two sides disagree the players are in different worlds while appearing to be in
/// one: positions would be wrong by the difference and nothing would look obviously broken.
/// Refusing loudly is the only safe answer, and in practice both sides derive it from the
/// same generated manifest so it matches.
/// </para>
/// </summary>
public sealed partial class ClientTerrainSync : Node
{
    private readonly ChunkStreamer _streamer;
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    public ClientTerrainSync(ChunkStreamer streamer, ChunkManager chunks, WorldOrigin origin)
    {
        _streamer = streamer;
        _chunks = chunks;
        _origin = origin;
        Name = "TerrainSync";
    }

    /// <summary>Raised with a human-readable status line, for the chat log.</summary>
    public event Action<string>? Status;

    /// <summary>Raised when the two sides disagree about the world origin.</summary>
    public event Action<string>? OriginMismatch;

    /// <summary>Raised once the town index has been cached, so the Tab search can reload.</summary>
    public event Action? PlacesReceived;

    /// <summary>The far-horizon file arrived from the server and is in the cache.</summary>
    public event Action? HorizonReceived;

    /// <summary>
    /// Raised when a client with no terrain adopted the server's origin. The host should
    /// respawn whatever it had placed, since its world position now means something else.
    /// </summary>
    public event Action? Rebased;

    /// <summary>True once the server manifest has been merged.</summary>
    public bool Synced { get; private set; }

    /// <summary>
    /// Fetches and merges the server's manifest. Safe to call more than once; the second call
    /// returns immediately.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        bool merged;
        try { merged = await SyncIndex(ct); }
        finally { IndexFinished = true; }
        if (!merged) return;
        await SyncPlacesAsync(ct).ConfigureAwait(false);
        await SyncHorizonAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The tile index step is over, merged or not: the origin is settled and the LOD rings know
    /// every tile. What the loading screen waits on; the place index and the horizon that follow
    /// arrive through their events while you play.
    /// </summary>
    public bool IndexFinished { get; private set; }

    /// <summary>Fetches and adopts the server's tile index. False when there is nothing more to sync.</summary>
    private async Task<bool> SyncIndex(CancellationToken ct)
    {
        if (Synced) return false;

        // The manifest is not tile-scoped, so any TileId will do as the request key.
        byte[]? bytes = (await _streamer
            .FetchAsync(AssetKind.Manifest, new TileId(0, 0), ct)
            .ConfigureAwait(false)).Data;

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

        // The continuation above runs on the thread pool, and what follows moves the origin and
        // unloads tiles (real ones replacing generated ground): main thread only.
        int added = await OnMainThread(() => Adopt(manifest)).ConfigureAwait(false);
        if (added < 0) return false;
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

    /// <summary>
    /// Checks the server's origin against ours and merges its tile list. Returns how many tiles
    /// were new, or -1 when the worlds disagree and streaming stays off.
    /// </summary>
    private int Adopt(TerrainManifest manifest)
    {
        double de = Math.Abs(manifest.SuggestedOriginLv95.E - _origin.E);
        double dn = Math.Abs(manifest.SuggestedOriginLv95.N - _origin.N);

        // A client with no terrain of its own has no world to contradict, so it adopts the
        // server's anchor instead of refusing. This is the fresh-clone path: the whole world then
        // streams in, and refusing here would make a clone with no data unable to play at all.
        // Generated ground counts as none (AvailableTileCount is real tiles only) — everything
        // built against the old origin is thrown away before it moves, and the generated fill,
        // anchored in LV95 rather than to the origin, comes back identical round the server's.
        bool noWorldOfOurOwn = _chunks.AvailableTileCount == 0;
        if ((de > 0.5 || dn > 0.5) && noWorldOfOurOwn)
        {
            _chunks.ResetAll(() =>
                _origin.Rebase(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N));
            de = dn = 0;
            Rebased?.Invoke();
        }

        if (de > 0.5 || dn > 0.5)
        {
            string message =
                $"World origin mismatch: server is at LV95 {manifest.SuggestedOriginLv95.E:F0}/"
                + $"{manifest.SuggestedOriginLv95.N:F0}, this client at {_origin.E:F0}/{_origin.N:F0}. "
                + "Every position would be offset by the difference, so terrain streaming is off.";

            GD.PushError($"[stream] {message}");
            Status?.Invoke(message);
            OriginMismatch?.Invoke(message);
            return -1;
        }

        return _chunks.MergeAvailableTiles(manifest.Tiles.Select(t => t.Id));
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
    /// session is reachable without a server. The origin is checked again: a cached index from
    /// a different world would silently place the player in the wrong place.
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
                GD.PushWarning("[stream] cached server index is for a different world origin; ignored");
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
