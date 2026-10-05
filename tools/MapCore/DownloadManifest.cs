using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Map;

// Everything SwissDownload needs to decide "is this file still what the server has"
// without any network request: the per-directory manifest, the STAC multihash format,
// and the id/bbox arithmetic for picking swissALTI3D tiles. Kept dependency-free (no
// HttpClient, no IStepProgress) on purpose: these are the pure parts of the
// port of tools/swiss_data.py (its manifest_path/load_manifest/save_manifest/
// known_current/is_up_to_date/sha256_of_asset, around lines 428-486), and
// keeping them in their own file is what lets the unit tests link just this one file instead of
// dragging in Pipeline.cs (whose IStepProgress would pull in types tier-0 tests have
// no project reference for). SwissDownload.cs holds the HTTP/orchestration half.

/// <summary>
/// A HEAD response's bearing on "has this file changed on the server", in the shape
/// <c>swiss_data.py</c>'s <c>http_head</c> returns it. Null (not this struct at all) means the
/// file was unreachable (404, or still failing after retries).
/// </summary>
public readonly record struct HeadInfo(long? Size, string? ETag, string? LastModified, string? Sha256, bool Ranges);

/// <summary>
/// One file's record in <c>.swiss_data_manifest.json</c>. Field names mirror the Python tool's
/// dict keys exactly (snake_case) so the two tools read and write the very same file: a machine
/// that has used either one must never look stale to the other and re-download gigabytes.
/// </summary>
public sealed record ManifestEntry
{
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("size")] public long? Size { get; init; }
    [JsonPropertyName("etag")] public string? ETag { get; init; }
    [JsonPropertyName("last_modified")] public string? LastModified { get; init; }
    // Not init-only: known_current's caller backfills this once a HEAD confirms a hand-downloaded
    // file (no entry yet) is current, same as the Python tool's `entry["sha256"] = ...`.
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("downloaded_at")] public string? DownloadedAt { get; init; }
}

/// <summary>
/// The per-directory manifest at <c>&lt;out_dir&gt;/.swiss_data_manifest.json</c>, and the two
/// skip decisions it exists for. Both decisions and the file format must match
/// <c>tools/swiss_data.py</c> exactly (see the file-level doc comment) -- this is the single most
/// important compatibility point of this port.
/// </summary>
public static class DownloadManifest
{
    private const string FileName = ".swiss_data_manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Path(string outDir) => System.IO.Path.Combine(outDir, FileName);

    /// <summary>
    /// Loads the manifest, keyed by filename (the same key <c>swiss_data.py</c> uses). An absent
    /// file is an empty manifest -- the normal state for a directory this tool has never visited.
    /// A corrupt file is left to throw, same as the Python tool's unguarded <c>json.load</c>:
    /// silently resetting it would forget every checksum recorded so far and reopen the gigabytes
    /// of re-downloading this manifest exists to avoid.
    /// </summary>
    public static Dictionary<string, ManifestEntry> Load(string outDir)
    {
        var path = Path(outDir);
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(File.ReadAllText(path), JsonOptions)
               ?? new();
    }

    public static void Save(string outDir, Dictionary<string, ManifestEntry> manifest) =>
        File.WriteAllText(Path(outDir), JsonSerializer.Serialize(manifest, JsonOptions));

    /// <summary>
    /// True when the manifest already recorded this exact file (same checksum as STAC publishes
    /// right now, same size still on disk) -- decided with <b>no request at all</b>. Port of
    /// <c>known_current</c>.
    /// </summary>
    public static bool KnownCurrent(string dest, ManifestEntry? entry, string? sha256) =>
        sha256 != null && entry != null && entry.Sha256 == sha256
        && File.Exists(dest) && new FileInfo(dest).Length == entry.Size;

    /// <summary>
    /// True when a HEAD already in hand says the file has not changed. Port of
    /// <c>is_up_to_date</c>, including its most important case: no manifest entry at all (a file
    /// that predates this tool, downloaded by hand) still counts as current when its size already
    /// matches the server's -- the caller is expected to then backfill a manifest entry from the
    /// same HEAD, exactly as <c>run()</c> does right after calling this.
    /// </summary>
    public static bool IsUpToDate(string dest, ManifestEntry? entry, HeadInfo head)
    {
        if (!File.Exists(dest)) return false;
        long localSize = new FileInfo(dest).Length;

        if (entry is null)
            return head.Size is { } size && size == localSize;

        if (localSize != entry.Size) return false;
        if (head.Sha256 != null && entry.Sha256 != null && head.Sha256 != entry.Sha256) return false;
        if (head.Size is { } headSize && headSize != entry.Size) return false;
        if (head.ETag != null && entry.ETag != null && head.ETag != entry.ETag) return false;
        if (head.LastModified != null && entry.LastModified != null && head.LastModified != entry.LastModified) return false;
        return true;
    }
}

/// <summary>
/// Pure STAC/geometry helpers for the swissALTI3D tile-list path: turning a multihash into the
/// checksum we verify against, an item id into the tile and flight year it names, a resolution
/// into the asset-filename pattern for it, and a tile set into the WGS84 bbox the STAC query
/// needs. None of these touch the network; <c>SwissDownload.AltiAsync</c> is built out of them.
/// </summary>
public static partial class SwissStacUtil
{
    /// <summary>
    /// STAC's <c>checksum:multihash</c> is <c>0x12</c> (sha2-256), <c>0x20</c> (32 bytes), then the
    /// digest: 2 bytes of header plus 32 bytes of digest, hex-encoded, is 68 characters starting
    /// "1220". Anything else (wrong prefix, wrong length, absent) has no checksum to verify
    /// against. Port of <c>sha256_of_asset</c>.
    /// </summary>
    public static string? Sha256OfAsset(string? checksumMultihash)
    {
        var mh = checksumMultihash?.ToLowerInvariant() ?? "";
        return mh.StartsWith("1220", StringComparison.Ordinal) && mh.Length == 68 ? mh[4..] : null;
    }

    [GeneratedRegex(@"^swissalti3d_(\d{4})_(\d+)-(\d+)$")]
    private static partial Regex AltiItemIdPatternRegex();

    /// <summary>
    /// swisstopo re-flies tiles over the years and keeps every past flight as a separate STAC item
    /// with the same tile id (e.g. <c>swissalti3d_2019_2583-1113</c> and
    /// <c>swissalti3d_2024_2583-1113</c>); this is the id format, split into the flight year and
    /// the 1 km tile it covers. Port of the <c>id_re</c> match in <c>resolve_swissalti3d</c>.
    /// </summary>
    public static bool TryParseAltiItemId(string id, out int year, out TileId tile)
    {
        year = 0;
        tile = default;
        var m = AltiItemIdPatternRegex().Match(id);
        if (!m.Success) return false;
        year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        tile = new TileId(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                           int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>
    /// The asset filename pattern for a swissALTI3D grid resolution (e.g. "0.5" or "2"). Port of
    /// the pattern built in <c>resolve_swissalti3d</c>.
    /// </summary>
    public static Regex AltiAssetPattern(string res) =>
        new(Regex.Escape("_" + res + "_2056_5728") + @"\.xyz\.zip$", RegexOptions.None);

    /// <summary>
    /// An LV95 bounding box as WGS84 (lon/lat), the shape the STAC bbox query wants. Built from
    /// <see cref="SwissProjection.ToWgs84"/> (the same formula <c>swiss_data.py</c>'s own
    /// <c>lv95_to_wgs84</c> uses) rather than a second copy of the maths. Port of
    /// <c>bbox_lv95_to_wgs84</c>.
    /// </summary>
    public static (double W, double S, double E, double N) BboxLv95ToWgs84(double minE, double minN, double maxE, double maxN)
    {
        var (lat1, lon1) = SwissProjection.ToWgs84(minE, minN);
        var (lat2, lon2) = SwissProjection.ToWgs84(maxE, maxN);
        return (Math.Min(lon1, lon2), Math.Min(lat1, lat2), Math.Max(lon1, lon2), Math.Max(lat1, lat2));
    }

    /// <summary>
    /// The WGS84 bbox around a set of 1 km tiles -- just wide enough to cover every tile's
    /// footprint, for the STAC query that lists candidate items. Port of <c>tiles_bbox_lv95</c>
    /// composed with <see cref="BboxLv95ToWgs84"/>.
    /// </summary>
    public static (double W, double S, double E, double N) TilesBboxWgs84(IReadOnlyCollection<TileId> tiles)
    {
        double minE = tiles.Min(t => t.E) * 1000.0;
        double minN = tiles.Min(t => t.N) * 1000.0;
        double maxE = (tiles.Max(t => t.E) + 1) * 1000.0;
        double maxN = (tiles.Max(t => t.N) + 1) * 1000.0;
        return BboxLv95ToWgs84(minE, minN, maxE, maxN);
    }
}
