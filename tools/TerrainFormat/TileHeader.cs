using System.Buffers.Binary;

namespace UnitSport.Terrain.Format;

/// <summary>
/// The 20-byte prefix every per-tile file starts with, little-endian:
/// u32 magic, u16 version, u16 flags, i32 tile E, i32 tile N, u32 count.
///
/// <para>
/// <see cref="Count"/> is the format's own word: a record count for holes and trees, the cell
/// side for cover, and grid size | stride &lt;&lt; 16 for a .terr (see <see cref="ChunkCodec.ReadHeader"/>).
/// Each decoder still checks only what it checked before (magic always, version and flags per
/// format), so files already on disk read exactly as they did.
/// </para>
/// </summary>
public readonly record struct TileHeader(uint Magic, ushort Version, ushort Flags, TileId Id, uint Count)
{
    public const int Size = 20;

    public void Write(Span<byte> h)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(h[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], Flags);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], Id.E);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], Id.N);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], Count);
    }

    public void Write(Stream output)
    {
        Span<byte> h = stackalloc byte[Size];
        Write(h);
        output.Write(h);
    }

    public static TileHeader Read(ReadOnlySpan<byte> h) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(h[0..]),
        BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),
        new TileId(BinaryPrimitives.ReadInt32LittleEndian(h[8..]), BinaryPrimitives.ReadInt32LittleEndian(h[12..])),
        BinaryPrimitives.ReadUInt32LittleEndian(h[16..]));

    /// <summary>Reads the 20 bytes and throws "Bad {kind} magic" unless they start with <paramref name="magic"/>.</summary>
    public static TileHeader Read(Stream input, uint magic, string kind)
    {
        Span<byte> h = stackalloc byte[Size];
        input.ReadExactly(h);
        var header = Read(h);
        header.CheckMagic(magic, kind);
        return header;
    }

    public void CheckMagic(uint magic, string kind)
    {
        if (Magic != magic) throw new InvalidDataException($"Bad {kind} magic 0x{Magic:X8}");
    }

    public void CheckVersion(ushort version, string kind)
    {
        if (Version != version) throw new InvalidDataException($"Unsupported {kind} version {Version}");
    }
}
