using System.Buffers.Binary;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Per-tile set of terrain quads to omit from the mesh, so tunnel portals open into the
/// hillside instead of being sealed by it. Quads are indexed on the full-resolution grid
/// (GridSize-1 squared); coarser LODs drop a quad if any fine quad it covers is a hole.
///
/// Only tiles that actually contain a portal get a file, so this costs nothing elsewhere.
/// </summary>
public static class HoleFormat
{
    /// <summary>"USHL" little-endian.</summary>
    public const uint Magic = 0x4C485355;

    public const ushort Version = 1;
    public const int HeaderSize = 20;

    /// <summary>Quads per tile edge (500 for a 501-vertex grid).</summary>
    public const int QuadsPerSide = ChunkFormat.GridSize - 1;

    public static string FileName(TileId id) => $"holes_{id.E}_{id.N}.holes";

    public static int CellIndex(int col, int row) => row * QuadsPerSide + col;

    public static void Encode(TileId id, IReadOnlyCollection<int> cells, Stream output)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], id.E);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], id.N);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)cells.Count);
        output.Write(header);

        Span<byte> rec = stackalloc byte[4];
        foreach (int cell in cells)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(rec[0..], (ushort)(cell % QuadsPerSide));
            BinaryPrimitives.WriteUInt16LittleEndian(rec[2..], (ushort)(cell / QuadsPerSide));
            output.Write(rec);
        }
    }

    public static HashSet<int> Decode(Stream input)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        input.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != Magic)
            throw new InvalidDataException($"Bad hole magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != Version)
            throw new InvalidDataException($"Unsupported hole version {version}");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);

        var cells = new HashSet<int>((int)count);
        var bytes = new byte[count * 4];
        input.ReadExactly(bytes);
        for (uint i = 0; i < count; i++)
        {
            int col = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)i * 4));
            int row = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)i * 4 + 2));
            cells.Add(CellIndex(col, row));
        }
        return cells;
    }
}
