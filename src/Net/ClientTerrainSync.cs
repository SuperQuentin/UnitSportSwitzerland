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
        if (Synced) return;

        // The manifest is not tile-scoped, so any TileId will do as the request key.
        byte[]? bytes = (await _streamer
            .FetchAsync(AssetKind.Manifest, new TileId(0, 0), ct)
            .ConfigureAwait(false)).Data;

        if (bytes is null)
        {
            GD.PushWarning("[stream] server sent no manifest; only local tiles will be available");
            Status?.Invoke("Server sent no terrain index — playing with local tiles only.");
            return;
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
            return;
        }

        double de = Math.Abs(manifest.SuggestedOriginLv95.E - _origin.E);
        double dn = Math.Abs(manifest.SuggestedOriginLv95.N - _origin.N);

        // A client with no terrain of its own has no world to contradict, so it adopts the
        // server's anchor instead of refusing. This is the fresh-clone path: nothing has been
        // placed yet, so there is nothing for a rebase to invalidate, and the whole world then
        // streams in. Refusing here would make a clone with no data unable to play at all.
        if ((de > 0.5 || dn > 0.5) && _chunks.AvailableTileCount == 0)
        {
            _origin.Rebase(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N);
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
            return;
        }

        int added = _chunks.MergeAvailableTiles(manifest.Tiles.Select(t => t.Id));
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

        await SyncPlacesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls the town index so the Tab teleport search works on a client that shipped without
    /// one. Purely cosmetic if it fails — /city still resolves server-side.
    /// </summary>
    private async Task SyncPlacesAsync(CancellationToken ct)
    {
        string dir = Core.TerrainPaths.FindCacheDir();
        string path = Path.Combine(dir, PlaceIndex.FileName);

        byte[]? bytes = (await _streamer
            .FetchAsync(AssetKind.Places, new TileId(0, 0), ct)
            .ConfigureAwait(false)).Data;

        if (bytes is null)
        {
            GD.Print("[stream] server has no place index; the Tab search will stay empty");
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] could not cache the place index: {e.Message}");
            return;
        }

        int count = PlaceIndex.FromJson(System.Text.Encoding.UTF8.GetString(bytes)).Places.Count;
        GD.Print($"[stream] place index received: {count} towns");
        PlacesReceived?.Invoke();
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
