using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UnitSport.Terrain.Format;

/// <summary>
/// The content fingerprint of built tiles (#924): what a render was made on, so a later render
/// can tell whether the ground under it is the same. A tile's hash covers every file of that tile
/// in the chunk directory (<c>chunk_E_N.terr</c>, <c>.terrc</c>, <c>roads_E_N.road</c>, cover,
/// trees, buildings, water, holes, fields, photo: any <c>&lt;kind&gt;_E_N.&lt;ext&gt;</c>), by name
/// and SHA-256 of the bytes, so a rebuilt road or a new photo changes it and nothing else does
/// (no dates, no paths). Written into <c>manifest.json</c> by the preprocessor
/// (<c>--manifest-hash</c>, and after every run that writes tiles); read by the trailer director.
/// </summary>
public static partial class TileFingerprint
{
    /// <summary>The <c>hashAlgorithm</c> of a manifest whose tiles carry one: SHA-256, its first 64 bits as hex.</summary>
    public const string Algorithm = "sha256-64";

    /// <summary>Hex digits kept of a SHA-256 (64 bits): plenty to tell a changed tile, a quarter of the manifest's size.</summary>
    public const int Digits = 16;

    [GeneratedRegex(@"^[A-Za-z]+_(\d+)_(\d+)(\.[A-Za-z]+)+$")]
    private static partial Regex TileFile();

    /// <summary>
    /// Whether <paramref name="fileName"/> (no directory) is one of a tile's files, and which tile:
    /// <c>roads_2579_1109.road</c>, <c>photo_2579_1109.jpg</c>. The region-wide files
    /// (<c>manifest.json</c>, <c>horizon.bin</c>, <c>places.json</c>) are not.
    /// </summary>
    public static bool TryTileOf(string fileName, out TileId id)
    {
        var m = TileFile().Match(fileName);
        // a write in progress (AtomicFile's ".tmp") is no tile's yet
        if (!m.Success || fileName.EndsWith(".tmp", StringComparison.Ordinal) || !int.TryParse(m.Groups[1].Value, out int e) || !int.TryParse(m.Groups[2].Value, out int n))
        {
            id = default;
            return false;
        }
        id = new TileId(e, n);
        return true;
    }

    /// <summary>The region-wide files a render also reads, hashed into the manifest's <c>shared</c>.</summary>
    public static readonly string[] SharedFiles = ["horizon.bin", "places.json", "landings.json", "airports.json"];

    /// <summary>The SHA-256 of a file's bytes, full hex (cached per file by the preprocessor).</summary>
    public static string OfFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>
    /// A tile's hash from its files' names and hashes, in any order: SHA-256 over "name:hash" lines in
    /// ordinal name order, <see cref="Digits"/> hex digits.
    /// </summary>
    public static string OfTile(IEnumerable<(string Name, string FileHash)> files)
    {
        var sb = new StringBuilder();
        foreach (var (name, hash) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
            sb.Append(name).Append(':').Append(hash).Append('\n');
        return Short(sb.ToString());
    }

    /// <summary>
    /// The fingerprint of a set of tiles (a film's coverage): SHA-256 over "E-N:hash" lines in tile order,
    /// a tile with no hash as "E-N:-" (not built, or a manifest from before #924), <see cref="Digits"/> hex.
    /// The same tiles with the same contents give the same fingerprint, whatever the order given.
    /// </summary>
    public static string OfSet(IEnumerable<(TileId Id, string? Hash)> tiles)
    {
        var sb = new StringBuilder();
        foreach (var (id, hash) in tiles.Distinct().OrderBy(t => t.Id.E).ThenBy(t => t.Id.N))
            sb.Append(id.E).Append('-').Append(id.N).Append(':').Append(hash ?? "-").Append('\n');
        return Short(sb.ToString());
    }

    private static string Short(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, Digits / 2).ToLowerInvariant();
}
