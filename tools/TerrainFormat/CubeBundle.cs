using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Cube tiles packed by their ancestor at <see cref="Level"/> (#1443, G1; docs/plans/global-tiling.md
/// §2 "Packing"): the whole Earth's land is ~100 M leaves at level 13, so tiles are never one file
/// each. A bundle (<c>CUBB</c>, <c>F0_L8_131_127.cubb</c>) holds an index of its tiles (key, offset,
/// length) and their <see cref="CubeTileCodec"/> bytes back to back; a reader seeks to one tile.
/// Colour bundles (<c>CUBA</c>, <c>.cuba</c>, #1430 H4) are the same layout holding
/// <see cref="CubeAlbedoCodec"/> entries instead, bundled by their own level, so a far-view fetch
/// pulls only colour and leaves carry none.
/// Leaf bundles (<c>CUBL</c>, <c>.cubl</c>, #1505 G2 B1) are the same layout again, holding
/// <see cref="CubeLeafCodec"/> entries (every kind of a leaf as a section of one entry), one bundle
/// per level-9 ancestor of the level-14 leaves; the coarse pyramid keeps <c>.cubb</c>.
/// </summary>
public static class CubeBundle
{
    public const uint Magic = 0x42425543; // "CUBB"
    public const uint AlbedoMagic = 0x41425543; // "CUBA"
    public const uint LeafMagic = 0x4C425543; // "CUBL"
    public const ushort Version = 1;

    public static string FileName(CubeKey bundle) => bundle.FileStem + ".cubb";

    public static string AlbedoFileName(CubeKey bundle) => bundle.FileStem + ".cuba";

    public static string LeafFileName(CubeKey bundle) => bundle.FileStem + ".cubl";

    private static string MagicName(uint magic) => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(magic));

    /// <summary>Writes a bundle atomically (a .tmp beside it, then a rename): heights (<see cref="Magic"/>) or colour (<see cref="AlbedoMagic"/>).</summary>
    public static void Write(string path, CubeKey bundle, IReadOnlyList<(CubeKey Key, byte[] Bytes)> tiles, uint magic = Magic)
    {
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(magic);
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
    public static Dictionary<CubeKey, (long Offset, int Length)> ReadIndex(Stream s, uint magic = Magic)
    {
        var r = new BinaryReader(s);
        if (r.ReadUInt32() != magic) throw new InvalidDataException($"not a cube bundle ({MagicName(magic)})");
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
    public static CubeTile? Read(string path, CubeKey key) =>
        ReadBytes(path, key) is { } bytes ? CubeTileCodec.Decode(bytes) : null;

    /// <summary>One leaf of a <c>.cubl</c> bundle with all its sections, or null when the bundle does not hold it.</summary>
    public static CubeLeaf? ReadLeaf(string path, CubeKey key) =>
        ReadBytes(path, key, LeafMagic) is { } bytes ? CubeLeafCodec.Decode(bytes) : null;

    /// <summary>Every leaf entry of a <c>.cubl</c> bundle, undecoded, in file order.</summary>
    public static List<(CubeKey Key, byte[] Bytes)> ReadAllEntries(string path, uint magic = LeafMagic)
    {
        using var fs = File.OpenRead(path);
        var list = new List<(CubeKey, byte[])>();
        foreach (var (key, (offset, length)) in ReadIndex(fs, magic).OrderBy(e => e.Value.Offset))
        {
            fs.Position = offset;
            var bytes = new byte[length];
            fs.ReadExactly(bytes);
            list.Add((key, bytes));
        }
        return list;
    }

    /// <summary>
    /// A build stage's write into a <c>.cubl</c> bundle (#1505): in every leaf of the bundle the
    /// stage's <paramref name="stageKinds"/> are dropped and <paramref name="sections"/> put in; every
    /// other section stays bit for bit (<see cref="CubeLeafCodec.Replace"/>), so a stage reruns alone.
    /// A leaf left with no section is dropped; the bundle is written atomically, leaves in key order.
    /// </summary>
    public static void UpdateLeaves(string path, CubeKey bundle, IReadOnlyDictionary<CubeKey, List<LeafSection>> sections,
        IReadOnlyCollection<LeafKind> stageKinds)
    {
        var entries = File.Exists(path) ? ReadAllEntries(path).ToDictionary(e => e.Key, e => e.Bytes) : new Dictionary<CubeKey, byte[]>();
        foreach (var key in sections.Keys) entries.TryAdd(key, CubeLeafCodec.Encode(new CubeLeaf { Key = key }));
        var tiles = new List<(CubeKey, byte[])>();
        foreach (var (key, bytes) in entries.OrderBy(e => e.Key.Y).ThenBy(e => e.Key.X))
        {
            var put = sections.TryGetValue(key, out var s) ? s : [];
            byte[] updated = CubeLeafCodec.Replace(bytes, put, stageKinds);
            if (CubeLeafCodec.ReadTable(updated).Table.Count > 0) tiles.Add((key, updated));
        }
        Write(path, bundle, tiles, LeafMagic);
    }

    /// <summary>One entry's bytes, or null when the bundle does not hold it.</summary>
    public static byte[]? ReadBytes(string path, CubeKey key, uint magic = Magic)
    {
        using var fs = File.OpenRead(path);
        if (!ReadIndex(fs, magic).TryGetValue(key, out var at)) return null;
        fs.Position = at.Offset;
        var bytes = new byte[at.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }
}

/// <summary>
/// A cube region's manifest (<c>cube_manifest.json</c>): the leaf level and grid, the bundle level,
/// and every tile with its height range, so a reader knows what exists without opening a bundle.
/// Version 2 (#1430 H4): a pyramid holds several levels, each with its own lattice and bundle level
/// (<see cref="Levels"/>); <see cref="LeafLevel"/>, <see cref="Bits"/> and <see cref="BundleLevel"/>
/// stay the finest level's.
/// </summary>
public sealed class CubeManifest
{
    public const string FileName = "cube_manifest.json";
    public const int CurrentVersion = 2;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("leafLevel")] public int LeafLevel { get; set; }
    [JsonPropertyName("bits")] public int Bits { get; set; }
    [JsonPropertyName("bundleLevel")] public int BundleLevel { get; set; }

    /// <summary>Every level the region holds; a version-1 manifest has none, and is its leaf level alone (<see cref="LevelsOrLeaf"/>).</summary>
    [JsonPropertyName("levels")] public List<CubeManifestLevel> Levels { get; set; } = new();

    /// <summary>The levels, a version-1 manifest's leaf level standing for them.</summary>
    [JsonIgnore] public IReadOnlyList<CubeManifestLevel> LevelsOrLeaf => Levels.Count > 0 ? Levels
        : new[] { new CubeManifestLevel { Level = LeafLevel, Bits = Bits, BundleLevel = BundleLevel } };

    /// <summary>The level entry for <paramref name="level"/>, or null.</summary>
    public CubeManifestLevel? LevelOf(int level) => LevelsOrLeaf.FirstOrDefault(l => l.Level == level);
    [JsonPropertyName("orientation")] public string Orientation { get; set; } =
        FormattableString.Invariant($"face 0 at {CubeSphere.CentreLatDeg}N {CubeSphere.CentreLonDeg}E, twist {CubeSphere.TwistDeg}");
    [JsonPropertyName("sources")] public List<string> Sources { get; set; } = new();

    /// <summary>A leaf region's (<c>.cubl</c>, #1507): every kind some leaf holds; null (not written) for a pyramid.</summary>
    [JsonPropertyName("kinds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Kinds { get; set; }
    /// <summary>
    /// A pyramid's lakes (#1669): the still levels its Swiss levels stand their lakes at, metres (<c>--cube-swiss</c> snaps the
    /// leaves' water and ETOPO's lake beside them to these exactly). The globe draws a level vertex at one as water whatever
    /// its colour says (Blue Marble's shore is too dark to read as water). Null (not written) when none.
    /// </summary>
    [JsonPropertyName("lakes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<float>? Lakes { get; set; }
    [JsonPropertyName("tiles")] public List<CubeManifestTile> Tiles { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static CubeManifest FromJson(string json) => JsonSerializer.Deserialize<CubeManifest>(json)
        ?? throw new InvalidDataException("empty cube manifest");
}

/// <summary>One level of a pyramid: its lattice, where its tiles are bundled, and whether they carry colour.</summary>
public sealed class CubeManifestLevel
{
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("bits")] public int Bits { get; set; }
    [JsonPropertyName("bundleLevel")] public int BundleLevel { get; set; }

    /// <summary>The colour's side in pixels (<c>.cuba</c> bundles at <see cref="AlbedoBundleLevel"/>), 0 when the level has none.</summary>
    [JsonPropertyName("albedo")] public int Albedo { get; set; }
    [JsonPropertyName("albedoBundleLevel")] public int AlbedoBundleLevel { get; set; }
}

public sealed class CubeManifestTile
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("min")] public float Min { get; set; }
    [JsonPropertyName("max")] public float Max { get; set; }
    [JsonPropertyName("roads")] public int Roads { get; set; }
    [JsonPropertyName("buildings")] public int Buildings { get; set; }
    [JsonPropertyName("bytes")] public int Bytes { get; set; }

    /// <summary>A leaf's (#1507): the kinds of its sections; null (not written) for a pyramid tile.</summary>
    [JsonPropertyName("kinds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Kinds { get; set; }
}
