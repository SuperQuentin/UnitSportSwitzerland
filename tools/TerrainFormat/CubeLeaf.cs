using System.Buffers.Binary;

namespace UnitSport.Terrain.Format;

/// <summary>
/// What a cube leaf section holds (#1505, G2 B1). <b>Append-only</b>: the number is in every leaf
/// entry's section table, and a reader skips a kind it does not know.
/// </summary>
public enum LeafKind : ushort
{
    /// <summary>The ellipsoidal heights on the leaf's lattice: <see cref="CubeTileCodec"/>'s bytes as they are (<see cref="LeafHeights"/>).</summary>
    Heights = 1,
    /// <summary>A <see cref="CoverClass"/> byte per lattice vertex (<see cref="LeafCover"/>).</summary>
    Cover = 2,
    /// <summary>The lattice quads left out of the ground mesh, for tunnel portals (<see cref="LeafHoles"/>).</summary>
    Holes = 3,
    /// <summary>The still water surface and its fetch on every second lattice vertex (<see cref="LeafWater"/>).</summary>
    Water = 4,
    /// <summary>The <see cref="RoadTile"/> record (<see cref="LeafRoads"/>).</summary>
    Roads = 5,
    /// <summary>The <see cref="BuildingTile"/> record (<see cref="LeafBuildings"/>).</summary>
    Buildings = 6,
    /// <summary>The tree instances (<see cref="LeafTrees"/>).</summary>
    Trees = 7,
    /// <summary>The ski slopes and their rasters on the leaf's lattice (<see cref="LeafPistes"/>).</summary>
    Pistes = 8,
    /// <summary>The farm field outlines (<see cref="LeafFields"/>).</summary>
    Fields = 9,
    /// <summary>The aerial photo over the leaf, an encoded image (<see cref="LeafPhoto"/>).</summary>
    Photo = 10,
    /// <summary>Raw (G2 B3 PR 2, a temporary <c>.cubl</c>): each raw road segment's swissTLM3D source, aligned with the raw <see cref="Roads"/> section (<see cref="LeafRoadKeys"/>).</summary>
    RoadKeys = 11,
    /// <summary>Raw (a temporary <c>.cubl</c>): the car park rings the network stage lays out (<see cref="LeafParking"/>).</summary>
    Parking = 12,
}

/// <summary>What a kind is for (#1507).</summary>
public static class LeafKinds
{
    /// <summary>A stage's intermediate (the road stage's raw kinds, in a temporary <c>.cubl</c>): never in a region's leaves, nor in <see cref="CubeLeafContent"/>.</summary>
    public static bool IsRaw(LeafKind kind) => kind is LeafKind.RoadKeys or LeafKind.Parking;
}

/// <summary>
/// One section of a leaf entry: its kind, the version and flags of the section's own codec, and the
/// payload as that codec wrote it (compressed or not, as the codec chose). Opaque to the container.
/// </summary>
public sealed record LeafSection(LeafKind Kind, ushort Version, ushort Flags, byte[] Payload);

/// <summary>
/// A cube leaf with content (#1505, G2 B1; docs/plans/global-tiling-g2.md, "Layout A" chosen by the
/// user on measurements): every tile kind of one level-14 cell as a section of one entry, so a
/// reader fetches a leaf in one read. Positions in every section are in the leaf's anchor frame
/// (<see cref="CubeKey.Anchor"/>: x east, y ENU up, z south, metres, floats; y is not an altitude);
/// lattice sections are on the leaf's lattice (<see cref="CubeKey.LatticeBits"/>, row-major from the
/// south-west, i along u, j along v, as the heights). Entries are bundled by their level-9 ancestor
/// in <c>.cubl</c> files (<see cref="CubeBundle.LeafMagic"/>).
/// </summary>
public sealed class CubeLeaf
{
    public required CubeKey Key { get; init; }

    /// <summary>The sections, one per kind, in kind order (<see cref="Set"/> keeps it).</summary>
    public List<LeafSection> Sections { get; init; } = new();

    public LeafSection? Find(LeafKind kind) => Sections.FirstOrDefault(s => s.Kind == kind);

    /// <summary>Puts a section in, replacing the one of its kind.</summary>
    public void Set(LeafSection section)
    {
        Sections.RemoveAll(s => s.Kind == section.Kind);
        int at = Sections.FindIndex(s => s.Kind > section.Kind);
        Sections.Insert(at < 0 ? Sections.Count : at, section);
    }

    public bool Remove(LeafKind kind) => Sections.RemoveAll(s => s.Kind == kind) > 0;
}

/// <summary>
/// The leaf entry (<c>CULF</c>, little-endian), what a <c>.cubl</c> bundle holds per leaf:
/// <code>
///   head 20 B : magic u32 "CULF", version u16, section count u16, face u8, level u8, reserved u16,
///               x i32, y i32
///   table     : per section 12 B: kind u16, codec version u16, codec flags u16, reserved u16, length u32
///   payloads  : back to back in table order
/// </code>
/// A reader skips a kind it does not know (and one whose codec version is newer than it reads,
/// <see cref="CubeLeafContent"/>), so a kind or a codec version is added without bumping
/// <see cref="Version"/>. <see cref="Decode"/> keeps every section, unknown ones too, so a stage
/// that rewrites its own kinds (<see cref="Replace"/>, <see cref="CubeBundle.UpdateLeaves"/>) keeps
/// the rest bit for bit.
/// </summary>
public static class CubeLeafCodec
{
    public const uint Magic = 0x464C5543; // "CULF"
    public const ushort Version = 1;
    public const int HeadBytes = 20;
    public const int TableEntryBytes = 12;

    public static byte[] Encode(CubeLeaf leaf)
    {
        var kinds = new HashSet<LeafKind>();
        foreach (var s in leaf.Sections)
            if (!kinds.Add(s.Kind)) throw new ArgumentException($"{leaf.Key}: two {s.Kind} sections");
        if (leaf.Sections.Count > ushort.MaxValue) throw new ArgumentException($"{leaf.Key}: {leaf.Sections.Count} sections");

        int size = HeadBytes + leaf.Sections.Count * TableEntryBytes + leaf.Sections.Sum(s => s.Payload.Length);
        var bytes = new byte[size];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)leaf.Sections.Count);
        span[8] = (byte)leaf.Key.Face;
        span[9] = (byte)leaf.Key.Level;
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], leaf.Key.X);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], leaf.Key.Y);
        int at = HeadBytes, payload = HeadBytes + leaf.Sections.Count * TableEntryBytes;
        foreach (var s in leaf.Sections)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)s.Kind);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 2)..], s.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 4)..], s.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 8)..], (uint)s.Payload.Length);
            at += TableEntryBytes;
            s.Payload.CopyTo(span[payload..]);
            payload += s.Payload.Length;
        }
        return bytes;
    }

    /// <summary>One row of an entry's section table: where its payload is in the entry.</summary>
    public readonly record struct TableEntry(LeafKind Kind, ushort Version, ushort Flags, int Offset, int Length);

    /// <summary>The key and the section table, without copying a payload: a reader that wants one kind reads only its bytes.</summary>
    public static (CubeKey Key, List<TableEntry> Table) ReadTable(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < HeadBytes || BinaryPrimitives.ReadUInt32LittleEndian(entry) != Magic)
            throw new InvalidDataException("not a cube leaf (CULF)");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
        if (version != Version) throw new InvalidDataException($"cube leaf version {version}, this reads {Version}");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
        var key = new CubeKey(entry[8], entry[9], BinaryPrimitives.ReadInt32LittleEndian(entry[12..]), BinaryPrimitives.ReadInt32LittleEndian(entry[16..]));
        long payload = HeadBytes + (long)count * TableEntryBytes;
        if (payload > entry.Length) throw new InvalidDataException($"{key}: a {count}-section table past the entry's {entry.Length} bytes");
        var table = new List<TableEntry>(count);
        var kinds = new HashSet<LeafKind>();
        for (int k = 0; k < count; k++)
        {
            var row = entry[(HeadBytes + k * TableEntryBytes)..];
            var kind = (LeafKind)BinaryPrimitives.ReadUInt16LittleEndian(row);
            if (!kinds.Add(kind)) throw new InvalidDataException($"{key}: two sections of kind {(ushort)kind}");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            if (payload + length > entry.Length) throw new InvalidDataException($"{key}: section {(ushort)kind} runs past the entry");
            table.Add(new TableEntry(kind, BinaryPrimitives.ReadUInt16LittleEndian(row[2..]), BinaryPrimitives.ReadUInt16LittleEndian(row[4..]), (int)payload, (int)length));
            payload += length;
        }
        return (key, table);
    }

    /// <summary>Every section, unknown kinds included (a rewrite keeps them).</summary>
    public static CubeLeaf Decode(ReadOnlySpan<byte> entry)
    {
        var (key, table) = ReadTable(entry);
        var leaf = new CubeLeaf { Key = key };
        foreach (var t in table) leaf.Sections.Add(new LeafSection(t.Kind, t.Version, t.Flags, entry.Slice(t.Offset, t.Length).ToArray()));
        return leaf;
    }

    /// <summary>One section of an entry, or null when it has none of that kind.</summary>
    public static LeafSection? ReadSection(ReadOnlySpan<byte> entry, LeafKind kind)
    {
        foreach (var t in ReadTable(entry).Table)
            if (t.Kind == kind) return new LeafSection(t.Kind, t.Version, t.Flags, entry.Slice(t.Offset, t.Length).ToArray());
        return null;
    }

    /// <summary>
    /// The writer's helper for a stage that reruns alone: the entry with <paramref name="put"/>'s
    /// sections in place of those kinds and <paramref name="remove"/>'s kinds dropped; every other
    /// section is kept bit for bit, unknown kinds included.
    /// </summary>
    public static byte[] Replace(ReadOnlySpan<byte> entry, IEnumerable<LeafSection> put, IEnumerable<LeafKind>? remove = null)
    {
        var leaf = Decode(entry);
        foreach (var k in remove ?? []) leaf.Remove(k);
        foreach (var s in put) leaf.Set(s);
        return Encode(leaf);
    }
}
