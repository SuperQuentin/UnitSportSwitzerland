namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>
/// The road network stage's input: the extractor's untouched output, kept in
/// <c>&lt;temp&gt;/roads_raw/</c> so every run of the stage starts from the same bytes. Beside each
/// <c>roads_E_N.road</c> sits <c>roads_E_N.keys</c>, one line per segment: the TLM uuid, part and
/// along-line distance of its first point (<c>-</c> for non-TLM-road features), which is how the
/// OSM overlay finds its rows.
/// </summary>
public static class RawRoads
{
    public const string DirName = "roads_raw";

    public readonly record struct Key(string Uuid, int Part, double FromM);

    public static string DirFor(string tempDir) => Path.Combine(tempDir, DirName);

    /// <summary>The temp dir the preprocessor uses by default for a chunk dir.</summary>
    public static string DefaultTempDir(string chunkDir) => chunkDir.TrimEnd('/', '\\') + "_temp";

    public static string RoadPath(string rawDir, TileId id) => Path.Combine(rawDir, RoadFormat.FileName(id));
    public static string KeysPath(string rawDir, TileId id) => Path.ChangeExtension(RoadPath(rawDir, id), ".keys");

    public static void Write(string rawDir, RoadTile tile, IReadOnlyList<Key?>? keys)
    {
        Directory.CreateDirectory(rawDir);
        string path = RoadPath(rawDir, tile.Id);
        using (var fs = File.Create(path + ".part")) RoadCodec.Encode(tile, fs);
        File.Move(path + ".part", path, overwrite: true);

        string keysPath = KeysPath(rawDir, tile.Id);
        if (keys is null) { File.Delete(keysPath); return; }
        var lines = keys.Select(k => k is { } key
            ? string.Create(CultureInfo.InvariantCulture, $"{key.Uuid}\t{key.Part}\t{key.FromM:F2}")
            : "-");
        File.WriteAllText(keysPath, string.Join('\n', lines) + "\n");
    }

    /// <summary>Keys aligned with the raw tile's segments, or null when there is no key file.</summary>
    public static Key?[]? ReadKeys(string rawDir, TileId id, int segmentCount)
    {
        string path = KeysPath(rawDir, id);
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path);
        if (lines.Length != segmentCount) return null;   // stale: written for another tile version
        var keys = new Key?[segmentCount];
        for (int i = 0; i < lines.Length; i++)
        {
            var bits = lines[i].Split('\t');
            if (bits.Length == 3)
                keys[i] = new Key(bits[0], int.Parse(bits[1], CultureInfo.InvariantCulture),
                    double.Parse(bits[2], CultureInfo.InvariantCulture));
        }
        return keys;
    }

    /// <summary>
    /// True for a tile the network stage already wrote (v2, or v3 with the Network flag). Only
    /// the 24-byte header is read. Such a tile is never valid stage input: its roads are trimmed.
    /// </summary>
    public static bool IsRewritten(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[RoadFormat.HeaderSize];
        if (stream.Read(header) < RoadFormat.HeaderSize) return false;
        ushort version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var flags = (RoadTileFlags)System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        return version == 2 || (version >= 3 && (flags & RoadTileFlags.Network) != 0);
    }
}
