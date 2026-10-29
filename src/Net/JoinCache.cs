using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnitSport.Terrain.Format;

namespace UnitSport.Net;

/// <summary>
/// What a returning player keeps from an earlier connection to the same server (#1380): the tile index
/// per server, and the hashes that say whether a cached region file is still the server's. Pure logic,
/// no Godot, unit tested (tests/UnitSportSwitzerland.Tests/JoinCacheTests).
/// </summary>
public static class JoinCache
{
    /// <summary>The region files a client pulls once per session, by the name they have in the cache dir.</summary>
    public static readonly string[] RegionFiles =
    [
        PlaceIndex.FileName, LandingIndex.FileName, RopewayInfoIndex.FileName, PoiFile.FileName,
        "atlas_facts.json", HorizonFormat.FileName,
    ];

    /// <summary>
    /// A server's identity for the cache: its address and the world it runs, so a hosted test world on
    /// 127.0.0.1:7777 never reads the index of the open world that ran there before.
    /// </summary>
    public static string ServerKey(string endpoint, string worldId)
    {
        string e = endpoint.Trim().ToLowerInvariant();
        if (e.Length == 0) e = "127.0.0.1";
        if (!e.Contains(':')) e += ":7777";
        return e + "|" + worldId;
    }

    /// <summary>The cached index of a server: readable name, plus a short hash so two keys never share a file.</summary>
    public static string ManifestFileName(string serverKey)
    {
        var name = new StringBuilder("server-manifest-");
        foreach (char c in serverKey) name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_');
        name.Append('-').Append(Sha256Hex(Encoding.UTF8.GetBytes(serverKey))[..8]).Append(".json");
        return name.ToString();
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>True when the file exists and its SHA-256 is <paramref name="hash"/> (any case).</summary>
    public static bool FileHasHash(string path, string hash)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var s = File.OpenRead(path);
            return string.Equals(Convert.ToHexString(SHA256.HashData(s)), hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The tiles of <paramref name="fresh"/> whose content changed since <paramref name="old"/>: the
    /// content hash when both indexes carry one, else the height range (the only thing older indexes say).
    /// A tile the old index did not list is new, not changed.
    /// </summary>
    public static List<ManifestTile> ChangedTiles(TerrainManifest? old, TerrainManifest fresh)
    {
        var changed = new List<ManifestTile>();
        if (old == null) return changed;
        var before = new Dictionary<TileId, ManifestTile>(old.Tiles.Count);
        foreach (var t in old.Tiles) before[t.Id] = t;
        foreach (var t in fresh.Tiles)
        {
            if (!before.TryGetValue(t.Id, out var o)) continue;
            bool differs = o.Hash != null && t.Hash != null ? o.Hash != t.Hash : o.Min != t.Min || o.Max != t.Max;
            if (differs) changed.Add(t);
        }
        return changed;
    }
}

/// <summary>
/// The server's answer to <c>AssetKind.Digest</c> (#1380): the SHA-256 of the tile index as it serves it
/// and of each region file it has. A returning client compares them with its cached copies and fetches
/// only what differs. An older server does not know the kind and answers "missing": the client then
/// fetches everything as before.
/// </summary>
public sealed class AssetDigest
{
    public string Manifest { get; set; } = "";

    /// <summary>File name to SHA-256; a file the server does not have is absent.</summary>
    public Dictionary<string, string> Files { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Null when the bytes are not a digest.</summary>
    public static AssetDigest? FromBytes(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<AssetDigest>(bytes, Json) is { Manifest.Length: > 0 } d ? d : null; }
        catch (JsonException) { return null; }
    }

    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, string Hash)> Stamped = new();

    /// <summary>A file's hash, recomputed only when its length or time changed (the horizon is 11 MB).</summary>
    private static string? HashOf(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (Stamped.TryGetValue(path, out var s) && s.Length == info.Length && s.Ticks == info.LastWriteTimeUtc.Ticks) return s.Hash;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Stamped[path] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
        return hash;
    }

    /// <summary>
    /// The digest of what a server serves from <paramref name="directory"/>; an override replaces the
    /// manifest or landings file (what <c>ChunkStreamer</c> sends instead of it). Null without a manifest.
    /// </summary>
    public static AssetDigest? Build(string directory, byte[]? manifestOverride, byte[]? landingsOverride)
    {
        string? manifest = manifestOverride != null ? JoinCache.Sha256Hex(manifestOverride) : HashOf(Path.Combine(directory, "manifest.json"));
        if (manifest == null) return null;
        var digest = new AssetDigest { Manifest = manifest };
        foreach (string name in JoinCache.RegionFiles)
        {
            string? hash = name == LandingIndex.FileName && landingsOverride != null
                ? JoinCache.Sha256Hex(landingsOverride)
                : HashOf(Path.Combine(directory, name));
            if (hash != null) digest.Files[name] = hash;
        }
        return digest;
    }
}
