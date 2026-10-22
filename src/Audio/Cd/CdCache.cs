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

    private static readonly Dictionary<(int, int), Task<string?>> InFlight = new();

    /// <summary>The asset's part number for a stem (#1153, <see cref="AssetKind.Cd"/>'s <c>TileId.N</c>): 0 the CD itself.</summary>
    public static int Part(CdStem? stem) => stem switch { CdStem.Voice => 1, CdStem.Music => 2, _ => 0 };

    /// <summary>The Ogg for a CD (or one of its stems, #1153) if this machine already has it, else null.</summary>
    public static string? LocalPath(int id, CdStem? stem = null)
    {
        if (CdLibrary.Instance is not { } lib) return null;
        string? path;
        if (id < 0) path = lib.PersonalPath(id);
        // a client's own folder holds its offline burns, whose ids name other songs than the server's
        else if (lib.OwnsFiles) path = Path.Combine(CdLibrary.Directory, $"{id}.ogg");
        else path = CachePath(id);
        if (path != null && stem is { } s) path = CdBurner.StemPath(path, s);
        return path != null && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The cached copy's file: the id and a fingerprint of the CD's title and length, since the
    /// same id is another song on another server (or after a server reused it). Null while the
    /// list does not know the CD.
    /// </summary>
    private static string? CachePath(int id)
    {
        if (CdLibrary.Instance?.Find(id) is not { } cd) return null;
        string key = $"{cd.Title}|{cd.Duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
        byte[] hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Path.Combine(CacheDirectory, $"{id}-{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}.ogg");
    }

    /// <summary>
    /// The Ogg for a CD, fetching it from the server when it is not here yet. One transfer per
    /// CD however many radios ask. Null when the server does not have it either, or the link is
    /// down; the caller may ask again later. Safe to call from the main thread; the result is
    /// delivered on the thread pool.
    /// </summary>
    public static Task<string?> FetchAsync(ChunkStreamer streamer, int id, CdStem? stem = null, CancellationToken ct = default)
    {
        if (LocalPath(id, stem) is { } here) return Task.FromResult<string?>(here);
        var key = (id, Part(stem));
        lock (InFlight)
        {
            if (InFlight.TryGetValue(key, out var running)) return running;
            var task = Fetch(streamer, id, stem, ct);
            InFlight[key] = task;
            _ = task.ContinueWith(_ => { lock (InFlight) InFlight.Remove(key); });
            return task;
        }
    }

    private static async Task<string?> Fetch(ChunkStreamer streamer, int id, CdStem? stem, CancellationToken ct)
    {
        // main thread: the list may change under the worker
        if (CachePath(id) is not { } mix) return null;
        string path = stem is { } s ? CdBurner.StemPath(mix, s) : mix;
        var result = await streamer.FetchAsync(AssetKind.Cd, new TileId(id, Part(stem)), ct);
        if (result.Data is not { } bytes)
        {
            GD.Print($"[cd] CD {id} not received{(result.PermanentlyMissing ? " (the server does not have it)" : "")}");
            return null;
        }
        try
        {
            Directory.CreateDirectory(CacheDirectory);
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
