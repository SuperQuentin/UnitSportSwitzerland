using Godot;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.Audio.Cd;

/// <summary>
/// Where a client finds a CD's audio: the library's own folder (offline, or the server's
/// machine), else the streaming cache, else fetched from the server through the terrain
/// streamer as <see cref="AssetKind.Cd"/> — a CD is just another file the server has and
/// the client lacks, metered on the bulk channel like a tile.
/// </summary>
public static class CdCache
{
    /// <summary>Client-side copies, beside the streamed terrain (so <c>--cache</c> separates test clients).</summary>
    private static string CacheDirectory => Path.Combine(Core.TerrainPaths.FindCacheDir(), "cds");

    private static readonly Dictionary<int, Task<string?>> InFlight = new();

    /// <summary>The Ogg for a CD if this machine already has it, else null.</summary>
    public static string? LocalPath(int id)
    {
        if (id < 0) return CdLibrary.Instance?.PersonalPath(id);
        string own = Path.Combine(CdLibrary.Directory, $"{id}.ogg");
        if (File.Exists(own)) return own;
        string cached = Path.Combine(CacheDirectory, $"{id}.ogg");
        return File.Exists(cached) ? cached : null;
    }

    /// <summary>
    /// The Ogg for a CD, fetching it from the server when it is not here yet. One transfer per
    /// CD however many radios ask. Null when the server does not have it either, or the link is
    /// down; the caller may ask again later. Safe to call from the main thread; the result is
    /// delivered on the thread pool.
    /// </summary>
    public static Task<string?> FetchAsync(ChunkStreamer streamer, int id, CancellationToken ct = default)
    {
        if (LocalPath(id) is { } here) return Task.FromResult<string?>(here);
        lock (InFlight)
        {
            if (InFlight.TryGetValue(id, out var running)) return running;
            var task = Fetch(streamer, id, ct);
            InFlight[id] = task;
            _ = task.ContinueWith(_ => { lock (InFlight) InFlight.Remove(id); });
            return task;
        }
    }

    private static async Task<string?> Fetch(ChunkStreamer streamer, int id, CancellationToken ct)
    {
        var result = await streamer.FetchAsync(AssetKind.Cd, new TileId(id, 0), ct);
        if (result.Data is not { } bytes)
        {
            GD.Print($"[cd] CD {id} not received{(result.PermanentlyMissing ? " (the server does not have it)" : "")}");
            return null;
        }
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            string path = Path.Combine(CacheDirectory, $"{id}.ogg");
            string temp = $"{path}.{Guid.NewGuid():N}.part";
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, ct);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            GD.Print($"[cd] CD {id} received, {bytes.Length / 1024} KB");
            return path;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[cd] could not cache CD {id}: {e.Message}");
            return null;
        }
    }
}
