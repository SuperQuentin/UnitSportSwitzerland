using System.Buffers.Binary;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The leaves a server holds (#1655, G2 C7): every leaf's key, its entry's size and its bundle's stamp, so a client that
/// lacks the region's <c>.cubl</c> bundles knows which leaves are real before it asks for one (the fill, the blend and
/// the globe decide on that), and a leaf it cached on an earlier visit is used again only while its bundle is unchanged.
/// Sent once per join as <c>AssetKind.LeafIndex</c>; the entries themselves come by key (<c>AssetKind.Leaf</c>).
/// ponytail: one index for the whole region (about 1.5 MB for Switzerland's ~110 000 leaves before deflate); a planet's
/// would be sent per bundle on demand, the same rows keyed by their level-9 bundle.
/// </summary>
public sealed class LeafIndex
{
    public const uint Magic = 0x5844494C; // "LIDX"
    public const ushort Version = 1;

    /// <summary>The copy in the client's cache, rewritten on every join.</summary>
    public const string FileName = "leaf_index.bin";

    private const int HeadBytes = 12, RowBytes = 18;

    /// <summary>A leaf's entry size in bytes, and the stamp of the bundle that holds it (changes when the bundle is rebuilt).</summary>
    public readonly record struct Entry(int Length, uint Stamp);

    public IReadOnlyDictionary<CubeKey, Entry> Leaves { get; }

    public LeafIndex(IReadOnlyDictionary<CubeKey, Entry> leaves) => Leaves = leaves;

    public int Count => Leaves.Count;

    /// <summary>The leaves a store holds on disk (the server's).</summary>
    public static LeafIndex Of(LeafStore store)
    {
        var rows = new Dictionary<CubeKey, Entry>();
        foreach (var key in store.LocalKeys) rows[key] = new Entry(store.Bytes(key), store.Stamp(key));
        return new LeafIndex(rows);
    }

    /// <summary>The wire and cache form: a head, then a row a leaf in key order (face, level, x, y, length, stamp).</summary>
    public byte[] ToBytes()
    {
        var keys = Leaves.Keys.OrderBy(k => k.Face).ThenBy(k => k.Level).ThenBy(k => k.Y).ThenBy(k => k.X).ToList();
        var bytes = new byte[HeadBytes + keys.Count * RowBytes];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], keys.Count);
        int at = HeadBytes;
        foreach (var k in keys)
        {
            var e = Leaves[k];
            span[at] = (byte)k.Face;
            span[at + 1] = (byte)k.Level;
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 2)..], k.X);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 6)..], k.Y);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 10)..], e.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 14)..], e.Stamp);
            at += RowBytes;
        }
        return bytes;
    }

    /// <summary>The index read back; null when the bytes are not one (another version, truncated, a key off the sphere).</summary>
    public static LeafIndex? FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeadBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version) return null;
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (count < 0 || (long)HeadBytes + (long)count * RowBytes != bytes.Length) return null;
        var rows = new Dictionary<CubeKey, Entry>(count);
        for (int n = 0, at = HeadBytes; n < count; n++, at += RowBytes)
        {
            var row = bytes[at..];
            var key = new CubeKey(row[0], row[1], BinaryPrimitives.ReadInt32LittleEndian(row[2..]), BinaryPrimitives.ReadInt32LittleEndian(row[6..]));
            int length = BinaryPrimitives.ReadInt32LittleEndian(row[10..]);
            if (!key.IsValid || length <= 0) return null;
            rows[key] = new Entry(length, BinaryPrimitives.ReadUInt32LittleEndian(row[14..]));
        }
        return new LeafIndex(rows);
    }

    /// <summary>The leaves whose row differs between two indexes (added, gone, or rebuilt): what a client must read again.</summary>
    public static List<CubeKey> Changed(IReadOnlyDictionary<CubeKey, Entry>? before, IReadOnlyDictionary<CubeKey, Entry> after)
    {
        var changed = new List<CubeKey>();
        foreach (var (k, e) in after)
            if (before == null || !before.TryGetValue(k, out var old) || old != e) changed.Add(k);
        if (before != null)
            foreach (var k in before.Keys)
                if (!after.ContainsKey(k)) changed.Add(k);
        return changed;
    }

    /// <summary>
    /// A bundle's stamp (FNV-1a over its file length and its index rows): cheap at open, and it changes whenever a stage
    /// rewrites the bundle, since every entry's offset or length moves with its sections.
    /// ponytail: a rebuild that leaves every offset and length as it was keeps the stamp; a content hash per entry would be
    /// exact, at the cost of reading every bundle whole when the server starts.
    /// </summary>
    public static uint StampOf(long fileLength, IEnumerable<(CubeKey Key, long Offset, int Length)> rows)
    {
        uint h = 2166136261;
        void Mix(long v)
        {
            for (int b = 0; b < 8; b++, v >>= 8) h = (h ^ (uint)(v & 0xFF)) * 16777619;
        }
        Mix(fileLength);
        foreach (var (k, o, l) in rows.OrderBy(r => r.Offset))
        {
            Mix(k.Face | ((long)k.Level << 8) | ((long)k.X << 16) | ((long)k.Y << 40));
            Mix(o);
            Mix(l);
        }
        return h;
    }
}
