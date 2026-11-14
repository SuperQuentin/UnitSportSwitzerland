using System.Buffers.Binary;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The pyramid a server holds on its own disk (#1656): every tile's key, lattice bits, heights entry size, colour entry
/// size (0: none) and a stamp, and the pyramid's lake levels, so a client streams any level by quadtree request
/// (<c>AssetKind.Pyramid</c>) and uses a tile it cached on an earlier visit again only while it is unchanged. Sent once
/// per join as <c>AssetKind.PyramidIndex</c>, beside the leaves' <see cref="LeafIndex"/>. 23 bytes a tile: 4,090 tiles
/// (the D: pyramid) are 94 KB before deflate.
/// </summary>
public sealed class PyramidIndex
{
    public const uint Magic = 0x58445950; // "PYDX"
    public const ushort Version = 1;

    /// <summary>The copy in the client's cache, rewritten on every join.</summary>
    public const string FileName = "pyramid_index.bin";

    private const int HeadBytes = 16, RowBytes = 23;

    /// <summary>A tile's lattice bits, heights entry size, whether it has colour (nonzero: a month is asked as its own section) and stamp.</summary>
    public readonly record struct Entry(int Bits, int Length, int ColourLength, uint Stamp)
    {
        /// <summary>Bit 0 of <see cref="ColourLength"/>: a month is asked as its own section.</summary>
        public bool HasColour => (ColourLength & ColourBit) != 0;

        /// <summary>Bit 1 (#1657): its far features, section <see cref="PyramidSource.FeaturesSection"/>. A #1656 client reads any
        /// nonzero value as colour: a tile with features and no colour costs it one "missing" a month.</summary>
        public bool HasFeatures => (ColourLength & FeaturesBit) != 0;
    }

    public const int ColourBit = 1, FeaturesBit = 2;

    public IReadOnlyDictionary<CubeKey, Entry> Tiles { get; }

    public IReadOnlyList<float> Lakes { get; }

    public PyramidIndex(IReadOnlyDictionary<CubeKey, Entry> tiles, IReadOnlyList<float>? lakes = null)
    {
        Tiles = tiles;
        Lakes = lakes ?? [];
    }

    public int Count => Tiles.Count;

    public byte[] ToBytes()
    {
        var keys = Tiles.Keys.OrderBy(k => k.Face).ThenBy(k => k.Level).ThenBy(k => k.Y).ThenBy(k => k.X).ToList();
        var bytes = new byte[HeadBytes + Lakes.Count * 4 + keys.Count * RowBytes];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], keys.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], Lakes.Count);
        int at = HeadBytes;
        foreach (float l in Lakes)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[at..], l);
            at += 4;
        }
        foreach (var k in keys)
        {
            var e = Tiles[k];
            span[at] = (byte)k.Face;
            span[at + 1] = (byte)k.Level;
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 2)..], k.X);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 6)..], k.Y);
            span[at + 10] = (byte)e.Bits;
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 11)..], e.Length);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 15)..], e.ColourLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 19)..], e.Stamp);
            at += RowBytes;
        }
        return bytes;
    }

    /// <summary>The index read back; null when the bytes are not one.</summary>
    public static PyramidIndex? FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeadBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version) return null;
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), lakeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        if (count < 0 || lakeCount < 0 || (long)HeadBytes + lakeCount * 4L + (long)count * RowBytes != bytes.Length) return null;
        int at = HeadBytes;
        var lakes = new float[lakeCount];
        for (int n = 0; n < lakeCount; n++, at += 4) lakes[n] = BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
        var rows = new Dictionary<CubeKey, Entry>(count);
        for (int n = 0; n < count; n++, at += RowBytes)
        {
            var row = bytes[at..];
            var key = new CubeKey(row[0], row[1], BinaryPrimitives.ReadInt32LittleEndian(row[2..]), BinaryPrimitives.ReadInt32LittleEndian(row[6..]));
            var e = new Entry(row[10], BinaryPrimitives.ReadInt32LittleEndian(row[11..]), BinaryPrimitives.ReadInt32LittleEndian(row[15..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[19..]));
            if (!key.IsValid || e.Length <= 0 || e.Bits is < 1 or > 12 || e.ColourLength < 0) return null;
            rows[key] = e;
        }
        return new PyramidIndex(rows, lakes);
    }

    /// <summary>
    /// The tiles of a chain that are on this copy's disk (the server's own; never the shipped base every client has):
    /// every heights entry is read once for its stamp (the D: pyramid's 136 MB of heights in a second or two: built off the
    /// main thread).
    /// </summary>
    public static PyramidIndex Of(PyramidSource source)
    {
        var rows = new Dictionary<CubeKey, Entry>();
        foreach (var key in source.Keys)
        {
            if (!source.IsLocal(key) || source.HeightsBytes(key) is not { } h) continue;
            var f = source.FeaturesEntry(key);
            rows[key] = new Entry(source.BitsOf(key), h.Length, (source.HasColour(key) ? ColourBit : 0) | (f != null ? FeaturesBit : 0), StampOf(key, h, f));
        }
        return new PyramidIndex(rows, source.Lakes);
    }

    /// <summary>A tile's stamp: FNV-1a over its key and its heights entry (a rebuilt tile changes it).</summary>
    public static uint StampOf(CubeKey key, byte[] heights, byte[]? features = null)
    {
        uint h = 2166136261;
        void Mix(byte b) => h = (h ^ b) * 16777619;
        long k = key.Face | ((long)key.Level << 8) | ((long)key.X << 16) | ((long)key.Y << 40);
        for (int b = 0; b < 8; b++, k >>= 8) Mix((byte)k);
        foreach (byte b in heights) Mix(b);
        // the far features (#1657) too: baked again, the tile's cached copies miss
        if (features != null) foreach (byte b in features) Mix(b);
        return h;
    }
}
