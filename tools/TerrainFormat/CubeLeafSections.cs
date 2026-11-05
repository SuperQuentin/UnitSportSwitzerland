using System.Buffers.Binary;
using System.IO.Compression;

namespace UnitSport.Terrain.Format;

// The section codecs of a cube leaf (#1505, G2 B1; CubeLeaf.cs holds the container). Each turns one
// kind into a LeafSection and back: its own version, flags and payload. Positions are in the leaf's
// anchor frame (CubeKey.Anchor: x east, y ENU up, z south, metres, floats); lattice kinds are on the
// leaf's lattice (CubeKey.LatticeBits(level): (2^bits + 1)² vertices, row-major from the south-west,
// i along u, j along v, as the heights). Payloads are listed in docs/notes/tools/cube-tiles.md.

/// <summary>Brotli for the section payloads: a small head written as is, then the compressed body.</summary>
internal static class LeafPack
{
    /// <summary>
    /// Brotli quality 6, window 4 MB: measured on a Geneva km² (roads 5.9 MB raw), quality 4 (what
    /// <see cref="CompressionLevel.Optimal"/> gives) kept 72 % of the roads, 6 kept 56 % at ~0.9 s,
    /// 11 kept 45 % at 24 s; decoding costs the same at every quality. Leaves are written once.
    /// </summary>
    public const int Quality = 6, Window = 22;

    public static byte[] Pack(ReadOnlySpan<byte> head, Action<Stream> body)
    {
        using var raw = new MemoryStream();
        body(raw);
        var src = raw.GetBuffer().AsSpan(0, (int)raw.Length);
        var dst = new byte[head.Length + BrotliEncoder.GetMaxCompressedLength(src.Length)];
        head.CopyTo(dst);
        if (!BrotliEncoder.TryCompress(src, dst.AsSpan(head.Length), out int written, Quality, Window))
            throw new InvalidOperationException("Brotli could not compress a leaf section");
        return dst[..(head.Length + written)];
    }

    public static byte[] Pack(Action<Stream> body) => Pack(ReadOnlySpan<byte>.Empty, body);

    /// <summary>The decompressed body after <paramref name="headBytes"/> bytes of head, as a seekable stream.</summary>
    public static MemoryStream Unpack(byte[] payload, int headBytes = 0)
    {
        var ms = new MemoryStream();
        using (var br = new BrotliStream(new MemoryStream(payload, headBytes, payload.Length - headBytes, writable: false), CompressionMode.Decompress))
            br.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    public static void CheckKind(LeafSection s, LeafKind kind, ushort maxVersion)
    {
        if (s.Kind != kind) throw new ArgumentException($"a {s.Kind} section, not {kind}");
        if (s.Version < 1 || s.Version > maxVersion) throw new InvalidDataException($"{kind} section version {s.Version}, this reads 1..{maxVersion}");
    }

    /// <summary>Vertices a side of a lattice of 2^bits cells.</summary>
    public static int Side(int bits) => (1 << bits) + 1;

    public static int CheckBits(int bits) =>
        bits is >= 1 and <= CubeKey.MaxBits ? bits : throw new InvalidDataException($"lattice bits {bits}");
}

/// <summary>Heights: <see cref="CubeTileCodec"/>'s bytes as they are (G1), its version the section's.</summary>
public static class LeafHeights
{
    public const ushort Version = CubeTileCodec.Version;

    public static LeafSection Encode(CubeTile tile) => new(LeafKind.Heights, Version, 0, CubeTileCodec.Encode(tile));

    public static CubeTile Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Heights, Version);
        return CubeTileCodec.Decode(s.Payload);
    }
}

/// <summary>
/// Cover: one <see cref="CoverClass"/> byte per lattice vertex, the codes <c>.cover</c> files and
/// <c>CoverPalette</c> use. Payload: bits u8, then the (2^bits + 1)² bytes, Brotli (long runs of
/// one class: ~100× smaller).
/// </summary>
public static class LeafCover
{
    public const ushort Version = 1;

    public static LeafSection Encode(byte[] cells, int bits)
    {
        int n = LeafPack.Side(LeafPack.CheckBits(bits));
        if (cells.Length != n * n) throw new ArgumentException($"{cells.Length} cover cells for a {n}² lattice");
        return new(LeafKind.Cover, Version, 0, LeafPack.Pack([(byte)bits], s => s.Write(cells)));
    }

    public static byte[] Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Cover, Version);
        int n = LeafPack.Side(LeafPack.CheckBits(s.Payload[0]));
        var cells = LeafPack.Unpack(s.Payload, 1).ToArray();
        if (cells.Length != n * n) throw new InvalidDataException($"{cells.Length} cover cells for a {n}² lattice");
        return cells;
    }
}

/// <summary>
/// Holes: the lattice quads removed from the ground mesh (tunnel portals), quad (i, j) the one whose
/// south-west vertex is lattice vertex (i, j), index i + j·2^bits (<see cref="Quad"/>). Payload:
/// bits u8, then Brotli of the count and the sorted indices as delta varints.
/// </summary>
public static class LeafHoles
{
    public const ushort Version = 1;

    public static int Quad(int i, int j, int bits) => i + (j << bits);

    public static LeafSection Encode(IEnumerable<int> quads, int bits)
    {
        int cells = 1 << LeafPack.CheckBits(bits);
        var sorted = quads.Distinct().Order().ToArray();
        if (sorted.Length > 0 && (sorted[0] < 0 || sorted[^1] >= cells * cells))
            throw new ArgumentException($"a hole quad outside the {cells}² lattice");
        return new(LeafKind.Holes, Version, 0, LeafPack.Pack([(byte)bits], s =>
        {
            using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
            w.Write7BitEncodedInt(sorted.Length);
            int last = 0;
            foreach (int q in sorted) { w.Write7BitEncodedInt(q - last); last = q; }
        }));
    }

    /// <summary>The quads, sorted.</summary>
    public static int[] Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Holes, Version);
        int cells = 1 << LeafPack.CheckBits(s.Payload[0]);
        using var r = new BinaryReader(LeafPack.Unpack(s.Payload, 1));
        var quads = new int[r.Read7BitEncodedInt()];
        int last = 0;
        for (int k = 0; k < quads.Length; k++) quads[k] = last += r.Read7BitEncodedInt();
        if (quads.Length > 0 && quads[^1] >= cells * cells) throw new InvalidDataException($"a hole quad outside the {cells}² lattice");
        return quads;
    }
}

/// <summary>
/// A leaf's still water (#298's <see cref="WaterGrid"/> in a leaf): on every second lattice vertex,
/// (2^(bits−1) + 1)² samples (~2.4 m at level 14, as <see cref="WaterTile"/>'s 2 m), row-major from
/// the south-west; sample (c, r) is lattice vertex (2c, 2r).
/// </summary>
public sealed class CubeWater
{
    /// <summary>Lattice vertices between samples.</summary>
    public const int Stride = 2;

    /// <summary>The leaf's lattice bits (9 at level 14), not the samples'.</summary>
    public required int Bits { get; init; }

    /// <summary>The still surface's WGS84 ellipsoidal height, metres (as the heights, not an altitude); NaN dry.</summary>
    public required float[] Level { get; init; }

    /// <summary>The wind fetch in <see cref="WaterFormat.FetchUnitM"/> steps, as <see cref="WaterGrid.Fetch"/>; 0 dry.</summary>
    public required byte[] Fetch { get; init; }

    /// <summary>Samples a side.</summary>
    public int Side => SideFor(Bits);

    public static int SideFor(int bits) => (1 << (bits - 1)) + 1;

    public static CubeWater Dry(int bits)
    {
        int n = SideFor(bits);
        var level = new float[n * n];
        Array.Fill(level, float.NaN);
        return new CubeWater { Bits = bits, Level = level, Fetch = new byte[n * n] };
    }
}

/// <summary>
/// Water: the surface in 1 cm quanta above the leaf's own base with <see cref="CubeTileCodec"/>'s
/// height encoding (planar prediction, zigzag varints, Brotli; dry is its missing-height code), then
/// the fetch bytes. Payload: bits u8, 3 reserved, base i64 (quanta), the level blob's length i32 and
/// bytes, then the fetch, Brotli.
/// </summary>
public static class LeafWater
{
    public const ushort Version = 1;

    public static LeafSection Encode(CubeWater w)
    {
        int n = CubeWater.SideFor(LeafPack.CheckBits(w.Bits));
        if (w.Level.Length != n * n || w.Fetch.Length != n * n) throw new ArgumentException($"water of {w.Level.Length}/{w.Fetch.Length} samples for {n}²");
        float min = float.PositiveInfinity;
        foreach (float h in w.Level) if (!float.IsNaN(h)) min = Math.Min(min, h);
        // rounded, so the base is the minimum's own quantum and a decoded level re-encodes to the same bytes
        long baseQ = float.IsPositiveInfinity(min) ? 0 : (long)Math.Round(min / (double)CubeTileCodec.Quantum);
        byte[] blob = CubeTileCodec.EncodeHeights(w.Level, n, baseQ, CubeTileCodec.Quantum);
        var head = new byte[16 + blob.Length];
        head[0] = (byte)w.Bits;
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(4), baseQ);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(12), blob.Length);
        blob.CopyTo(head, 16);
        return new(LeafKind.Water, Version, 0, LeafPack.Pack(head, s => s.Write(w.Fetch)));
    }

    public static CubeWater Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Water, Version);
        var p = s.Payload;
        int bits = LeafPack.CheckBits(p[0]), n = CubeWater.SideFor(bits);
        long baseQ = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(4));
        int blobLength = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(12));
        var level = CubeTileCodec.DecodeHeights(p.AsSpan(16, blobLength).ToArray(), n, baseQ, CubeTileCodec.Quantum);
        var fetch = LeafPack.Unpack(p, 16 + blobLength).ToArray();
        if (fetch.Length != n * n) throw new InvalidDataException($"{fetch.Length} fetch samples for {n}²");
        return new CubeWater { Bits = bits, Level = level, Fetch = fetch };
    }
}

/// <summary>
/// Roads: <see cref="RoadCodec.EncodeBody"/> (the <c>.road</c> file after its tile id), Brotli; the
/// section's version is <see cref="RoadFormat.Version"/> and its flags the tile's
/// <see cref="RoadTileFlags"/>. Positions in the anchor frame. The decoded tile's
/// <see cref="RoadTile.Id"/> is default: a leaf is keyed by its cube key.
/// </summary>
public static class LeafRoads
{
    public const ushort Version = RoadFormat.Version;

    public static LeafSection Encode(RoadTile tile) =>
        new(LeafKind.Roads, Version, (ushort)tile.Flags, LeafPack.Pack(s => RoadCodec.EncodeBody(tile, s)));

    public static RoadTile Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Roads, Version);
        return RoadCodec.DecodeBody(LeafPack.Unpack(s.Payload), s.Version, (RoadTileFlags)s.Flags);
    }
}

/// <summary>Buildings: <see cref="BuildingCodec.EncodeBody"/>, Brotli; triangles in the anchor frame.</summary>
public static class LeafBuildings
{
    public const ushort Version = BuildingFormat.Version;

    public static LeafSection Encode(BuildingTile tile) =>
        new(LeafKind.Buildings, Version, 0, LeafPack.Pack(s => BuildingCodec.EncodeBody(tile, s)));

    public static BuildingTile Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Buildings, Version);
        return BuildingCodec.DecodeBody(LeafPack.Unpack(s.Payload));
    }
}

/// <summary>Trees: <see cref="TreeFormat.EncodeBody"/>, Brotli; positions in the anchor frame.</summary>
public static class LeafTrees
{
    public const ushort Version = TreeFormat.Version;

    public static LeafSection Encode(IReadOnlyList<TreeInstance> trees) =>
        new(LeafKind.Trees, Version, 0, LeafPack.Pack(s => TreeFormat.EncodeBody(trees, s)));

    public static List<TreeInstance> Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Trees, Version);
        return TreeFormat.DecodeBody(LeafPack.Unpack(s.Payload));
    }
}

/// <summary>
/// Pistes: <see cref="PisteFormat.EncodeBody"/> with the rasters on the leaf's lattice, Brotli.
/// Payload: bits u8, then the compressed body; flags <see cref="PisteFormat.FlagOsm"/> (ODbL data).
/// Paths are (x, z) in the anchor frame; <see cref="PisteTile.Slot"/> and <see cref="PisteTile.Aux"/>
/// are per lattice vertex, row-major from the south-west (the LV95 file's are per 1 m grid vertex
/// from the north-west, so <see cref="PisteTile.IndexAt"/> does not apply inside a leaf).
/// </summary>
public static class LeafPistes
{
    public const ushort Version = PisteFormat.Version;

    public static LeafSection Encode(PisteTile tile, int bits) =>
        new(LeafKind.Pistes, Version, PisteFormat.FlagOsm,
            LeafPack.Pack([(byte)LeafPack.CheckBits(bits)], s => PisteFormat.EncodeBody(tile, s, LeafPack.Side(bits))));

    public static PisteTile Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Pistes, Version);
        int bits = LeafPack.CheckBits(s.Payload[0]);
        return PisteFormat.DecodeBody(LeafPack.Unpack(s.Payload, 1), LeafPack.Side(bits));
    }
}

/// <summary>
/// Fields: <see cref="FieldFormat.EncodeBody"/>, Brotli. The outlines are what the game reads (it
/// rasterises its farming cells from them at load, <c>FieldTile.Build</c>, and clips the drawing to
/// them), so a leaf carries outlines, not cells: ring points (x, z) in the anchor frame.
/// </summary>
public static class LeafFields
{
    public const ushort Version = FieldFormat.Version;

    public static LeafSection Encode(IReadOnlyList<FieldPolygon> fields) =>
        new(LeafKind.Fields, Version, 0, LeafPack.Pack(s => FieldFormat.EncodeBody(fields, s)));

    public static List<FieldPolygon> Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Fields, Version);
        return FieldFormat.DecodeBody(LeafPack.Unpack(s.Payload));
    }
}

/// <summary>
/// A leaf's aerial photo: an encoded image over the leaf's (u, v) square, rows from the south-west
/// like the heights (image row 0 is the leaf's south edge, so a viewer shows it upside down, as
/// <see cref="CubeAlbedo"/>), pixel (px, py) covering u in [U0 + px·s, U0 + (px+1)·s] with s the
/// cell's size over <see cref="Width"/>, and likewise v. The bytes are never decoded here: the game
/// decodes them with Godot's Image.
/// </summary>
public sealed class CubePhoto
{
    public required CubeAlbedoFormat Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Image { get; init; }
}

/// <summary>Photo: format u8, reserved u8, width u16, height u16, reserved u16, then the image bytes as they are (already compressed).</summary>
public static class LeafPhoto
{
    public const ushort Version = 1;
    private const int HeadBytes = 8;

    public static LeafSection Encode(CubePhoto photo)
    {
        if (photo.Width is < 1 or > ushort.MaxValue || photo.Height is < 1 or > ushort.MaxValue)
            throw new ArgumentException($"photo of {photo.Width}x{photo.Height} px");
        var p = new byte[HeadBytes + photo.Image.Length];
        p[0] = (byte)photo.Format;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), (ushort)photo.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), (ushort)photo.Height);
        photo.Image.CopyTo(p, HeadBytes);
        return new(LeafKind.Photo, Version, 0, p);
    }

    public static CubePhoto Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Photo, Version);
        var p = s.Payload;
        return new CubePhoto
        {
            Format = (CubeAlbedoFormat)p[0],
            Width = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4)),
            Image = p[HeadBytes..],
        };
    }
}

/// <summary>
/// A leaf's content decoded, every kind it holds (null where it holds none): what a builder or the
/// game takes from an entry. <see cref="Decode"/> skips a kind it does not know and a section whose
/// codec version is newer than it reads (listed in <see cref="Skipped"/>); <see cref="ToLeaf"/>
/// encodes every kind present, lattice kinds on <see cref="CubeKey.LatticeBits"/> of the key's level.
/// </summary>
public sealed class CubeLeafContent
{
    public required CubeKey Key { get; init; }
    public CubeTile? Heights { get; set; }
    public byte[]? Cover { get; set; }
    /// <summary>Hole quads, sorted (<see cref="LeafHoles.Quad"/>).</summary>
    public int[]? Holes { get; set; }
    public CubeWater? Water { get; set; }
    public RoadTile? Roads { get; set; }
    public BuildingTile? Buildings { get; set; }
    public List<TreeInstance>? Trees { get; set; }
    public PisteTile? Pistes { get; set; }
    public List<FieldPolygon>? Fields { get; set; }
    public CubePhoto? Photo { get; set; }

    /// <summary>Sections <see cref="Decode"/> left out: unknown kinds, or codec versions newer than this build reads.</summary>
    public List<LeafKind> Skipped { get; } = new();

    public int Bits => CubeKey.LatticeBits(Key.Level);

    /// <summary>The highest codec version this build reads, per kind it knows.</summary>
    public static ushort? ReadsUpTo(LeafKind kind) => kind switch
    {
        LeafKind.Heights => LeafHeights.Version,
        LeafKind.Cover => LeafCover.Version,
        LeafKind.Holes => LeafHoles.Version,
        LeafKind.Water => LeafWater.Version,
        LeafKind.Roads => LeafRoads.Version,
        LeafKind.Buildings => LeafBuildings.Version,
        LeafKind.Trees => LeafTrees.Version,
        LeafKind.Pistes => LeafPistes.Version,
        LeafKind.Fields => LeafFields.Version,
        LeafKind.Photo => LeafPhoto.Version,
        _ => null,
    };

    public static CubeLeafContent Decode(ReadOnlySpan<byte> entry) => From(CubeLeafCodec.Decode(entry));

    public static CubeLeafContent From(CubeLeaf leaf)
    {
        var c = new CubeLeafContent { Key = leaf.Key };
        foreach (var s in leaf.Sections)
        {
            if (ReadsUpTo(s.Kind) is not { } max || s.Version > max)
            {
                c.Skipped.Add(s.Kind);
                continue;
            }
            switch (s.Kind)
            {
                case LeafKind.Heights: c.Heights = LeafHeights.Decode(s); break;
                case LeafKind.Cover: c.Cover = LeafCover.Decode(s); break;
                case LeafKind.Holes: c.Holes = LeafHoles.Decode(s); break;
                case LeafKind.Water: c.Water = LeafWater.Decode(s); break;
                case LeafKind.Roads: c.Roads = LeafRoads.Decode(s); break;
                case LeafKind.Buildings: c.Buildings = LeafBuildings.Decode(s); break;
                case LeafKind.Trees: c.Trees = LeafTrees.Decode(s); break;
                case LeafKind.Pistes: c.Pistes = LeafPistes.Decode(s); break;
                case LeafKind.Fields: c.Fields = LeafFields.Decode(s); break;
                case LeafKind.Photo: c.Photo = LeafPhoto.Decode(s); break;
            }
        }
        return c;
    }

    /// <summary>The sections of every kind present, in kind order.</summary>
    public List<LeafSection> Sections()
    {
        var list = new List<LeafSection>();
        if (Heights is { } h)
        {
            if (h.Key != Key) throw new ArgumentException($"heights of {h.Key} in leaf {Key}");
            list.Add(LeafHeights.Encode(h));
        }
        if (Cover is { } cover) list.Add(LeafCover.Encode(cover, Bits));
        if (Holes is { } holes) list.Add(LeafHoles.Encode(holes, Bits));
        if (Water is { } water)
        {
            if (water.Bits != Bits) throw new ArgumentException($"water on a {water.Bits}-bit lattice in a {Bits}-bit leaf");
            list.Add(LeafWater.Encode(water));
        }
        if (Roads is { } roads) list.Add(LeafRoads.Encode(roads));
        if (Buildings is { } buildings) list.Add(LeafBuildings.Encode(buildings));
        if (Trees is { } trees) list.Add(LeafTrees.Encode(trees));
        if (Pistes is { } pistes) list.Add(LeafPistes.Encode(pistes, Bits));
        if (Fields is { } fields) list.Add(LeafFields.Encode(fields));
        if (Photo is { } photo) list.Add(LeafPhoto.Encode(photo));
        return list;
    }

    public CubeLeaf ToLeaf() => new() { Key = Key, Sections = Sections() };

    public byte[] Encode() => CubeLeafCodec.Encode(ToLeaf());
}

/// <summary>One raw segment's source (G2 B3 PR 2): the TLM uuid, the part (LineString index), and the distance along that part, on the work plane, to the segment's first point.</summary>
public readonly record struct RoadSourceKey(string Uuid, int Part, double FromM);

/// <summary>
/// The raw roads' keys (a temporary <c>.cubl</c>, #1507), one per raw segment in the order of the leaf's raw
/// <see cref="LeafKind.Roads"/> section, null for a feature that is not a TLM road line (rail, water, BD TOPO):
/// what <c>RawRoads.Key</c> is for the LV95 build, with the distance along the line in true metres (the work
/// plane's) rather than LV95 grid metres. Payload: Brotli of the count, then per segment a byte (0 none,
/// 1 a key), the uuid (7-bit length, UTF-8), the part (7-bit) and the distance (f64).
/// </summary>
public static class LeafRoadKeys
{
    public const ushort Version = 1;

    public static LeafSection Encode(IReadOnlyList<RoadSourceKey?> keys) =>
        new(LeafKind.RoadKeys, Version, 0, LeafPack.Pack(s =>
        {
            using var w = new BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write7BitEncodedInt(keys.Count);
            foreach (var k in keys)
            {
                if (k is not { } key) { w.Write((byte)0); continue; }
                w.Write((byte)1);
                w.Write(key.Uuid);
                w.Write7BitEncodedInt(key.Part);
                w.Write(key.FromM);
            }
        }));

    public static RoadSourceKey?[] Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.RoadKeys, Version);
        using var r = new BinaryReader(LeafPack.Unpack(s.Payload), System.Text.Encoding.UTF8);
        var keys = new RoadSourceKey?[r.Read7BitEncodedInt()];
        for (int i = 0; i < keys.Length; i++)
            if (r.ReadByte() != 0) keys[i] = new RoadSourceKey(r.ReadString(), r.Read7BitEncodedInt(), r.ReadDouble());
        return keys;
    }
}

/// <summary>A car park ring in a leaf (G2 B3 PR 2): its cover class, where it came from (tlm, override), and its points (x, y, z) in the anchor frame, on the leaf mesh, unclosed.</summary>
public sealed record LeafParkingLot(CoverClass Cover, string Source, float[] Points)
{
    public int Count => Points.Length / 3;
}

/// <summary>
/// The car parks the network stage lays out (a temporary <c>.cubl</c>, #1507): <c>RawParking</c>'s lots on the leaf
/// holding each ring's centroid (on the work plane), once, even where a ring spills into neighbours. Payload:
/// Brotli of the count, then per lot the cover byte, the source (7-bit length, UTF-8), the point count (7-bit) and
/// the points as f32 triples.
/// </summary>
public static class LeafParking
{
    public const ushort Version = 1;

    public static LeafSection Encode(IReadOnlyList<LeafParkingLot> lots) =>
        new(LeafKind.Parking, Version, 0, LeafPack.Pack(s =>
        {
            using var w = new BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write7BitEncodedInt(lots.Count);
            foreach (var lot in lots)
            {
                w.Write((byte)lot.Cover);
                w.Write(lot.Source);
                w.Write7BitEncodedInt(lot.Count);
                foreach (float f in lot.Points) w.Write(f);
            }
        }));

    public static List<LeafParkingLot> Decode(LeafSection s)
    {
        LeafPack.CheckKind(s, LeafKind.Parking, Version);
        using var r = new BinaryReader(LeafPack.Unpack(s.Payload), System.Text.Encoding.UTF8);
        int n = r.Read7BitEncodedInt();
        var lots = new List<LeafParkingLot>(n);
        for (int i = 0; i < n; i++)
        {
            var cover = (CoverClass)r.ReadByte();
            string source = r.ReadString();
            var pts = new float[r.Read7BitEncodedInt() * 3];
            for (int k = 0; k < pts.Length; k++) pts[k] = r.ReadSingle();
            lots.Add(new LeafParkingLot(cover, source, pts));
        }
        return lots;
    }
}
