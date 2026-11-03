using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Cube tiles packed by their ancestor at <see cref="Level"/> (#1443, G1; docs/plans/global-tiling.md
/// §2 "Packing"): the whole Earth's land is ~100 M leaves at level 13, so tiles are never one file
/// each. A bundle (<c>CUBB</c>, <c>F0_L8_131_127.cubb</c>) holds an index of its tiles (key, offset,
/// length) and their <see cref="CubeTileCodec"/> bytes back to back; a reader seeks to one tile.
/// </summary>
public static class CubeBundle
{
    public const uint Magic = 0x42425543; // "CUBB"
    public const ushort Version = 1;

    public static string FileName(CubeKey bundle) => bundle.FileStem + ".cubb";

    /// <summary>Writes a bundle atomically (a .tmp beside it, then a rename).</summary>
    public static void Write(string path, CubeKey bundle, IReadOnlyList<(CubeKey Key, byte[] Bytes)> tiles)
    {
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write((ushort)0);
            w.Write((byte)bundle.Face);
            w.Write((byte)bundle.Level);
            w.Write((ushort)0);
            w.Write(bundle.X);
            w.Write(bundle.Y);
            w.Write(tiles.Count);
            long offset = 24 + tiles.Count * 24L;
            foreach (var (key, bytes) in tiles)
            {
                if (key.Face != bundle.Face || key.AncestorAt(bundle.Level) != bundle)
                    throw new ArgumentException($"{key} is not in bundle {bundle}");
                w.Write((byte)key.Level);
                w.Write((byte)0);
                w.Write((ushort)0);
                w.Write(key.X);
                w.Write(key.Y);
                w.Write(offset);
                w.Write(bytes.Length);
                offset += bytes.Length;
            }
            foreach (var (_, bytes) in tiles) w.Write(bytes);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A bundle's index: each tile's byte range in the file.</summary>
    public static Dictionary<CubeKey, (long Offset, int Length)> ReadIndex(Stream s)
    {
        var r = new BinaryReader(s);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a cube bundle (CUBB)");
        ushort version = r.ReadUInt16();
        if (version != Version) throw new InvalidDataException($"cube bundle version {version}, this reads {Version}");
        r.ReadUInt16();
        int face = r.ReadByte();
        r.ReadByte();
        r.ReadUInt16();
        r.ReadInt32();
        r.ReadInt32();
        int count = r.ReadInt32();
        var index = new Dictionary<CubeKey, (long, int)>(count);
        for (int k = 0; k < count; k++)
        {
            int level = r.ReadByte();
            r.ReadByte();
            r.ReadUInt16();
            var key = new CubeKey(face, level, r.ReadInt32(), r.ReadInt32());
            index[key] = (r.ReadInt64(), r.ReadInt32());
        }
        return index;
    }

    /// <summary>Every tile of a bundle, decoded.</summary>
    public static IEnumerable<CubeTile> ReadAll(string path)
    {
        using var fs = File.OpenRead(path);
        var index = ReadIndex(fs);
        foreach (var (_, (offset, length)) in index.OrderBy(e => e.Value.Offset))
        {
            fs.Position = offset;
            var bytes = new byte[length];
            fs.ReadExactly(bytes);
            yield return CubeTileCodec.Decode(bytes);
        }
    }

    /// <summary>One tile of a bundle, or null when the bundle does not hold it.</summary>
    public static CubeTile? Read(string path, CubeKey key)
    {
        using var fs = File.OpenRead(path);
        if (!ReadIndex(fs).TryGetValue(key, out var at)) return null;
        fs.Position = at.Offset;
        var bytes = new byte[at.Length];
        fs.ReadExactly(bytes);
        return CubeTileCodec.Decode(bytes);
    }
}

/// <summary>
/// A cube region's manifest (<c>cube_manifest.json</c>): the leaf level and grid, the bundle level,
/// and every tile with its height range, so a reader knows what exists without opening a bundle.
/// </summary>
public sealed class CubeManifest
{
    public const string FileName = "cube_manifest.json";

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("leafLevel")] public int LeafLevel { get; set; }
    [JsonPropertyName("bits")] public int Bits { get; set; }
    [JsonPropertyName("bundleLevel")] public int BundleLevel { get; set; }
    [JsonPropertyName("orientation")] public string Orientation { get; set; } =
        FormattableString.Invariant($"face 0 at {CubeSphere.CentreLatDeg}N {CubeSphere.CentreLonDeg}E, twist {CubeSphere.TwistDeg}");
    [JsonPropertyName("sources")] public List<string> Sources { get; set; } = new();
    [JsonPropertyName("tiles")] public List<CubeManifestTile> Tiles { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static CubeManifest FromJson(string json) => JsonSerializer.Deserialize<CubeManifest>(json)
        ?? throw new InvalidDataException("empty cube manifest");
}

public sealed class CubeManifestTile
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("min")] public float Min { get; set; }
    [JsonPropertyName("max")] public float Max { get; set; }
    [JsonPropertyName("roads")] public int Roads { get; set; }
    [JsonPropertyName("buildings")] public int Buildings { get; set; }
    [JsonPropertyName("bytes")] public int Bytes { get; set; }
}
