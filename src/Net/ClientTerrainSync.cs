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
    /// session is reachable without a server. Its tiles are LV95 like everything else, whatever
    /// origin that server started from.
    /// </summary>
    /// <returns>How many tiles the cached index added.</returns>
    public static int MergeCachedIndex(ChunkManager chunks)
    {
        try
        {
            string path = Path.Combine(Core.TerrainPaths.FindCacheDir(), CachedIndexFile);
            if (!File.Exists(path)) return 0;

            var manifest = TerrainManifest.FromJson(File.ReadAllText(path));

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
