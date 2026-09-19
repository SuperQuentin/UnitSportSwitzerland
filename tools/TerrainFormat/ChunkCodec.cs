using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Binary encode/decode of .terr chunk files. Shared by the offline preprocessor and the game
/// so the two can never drift apart.
/// </summary>
public static class ChunkCodec
{
    public static void Encode(ChunkGrid grid, Stream output)
    {
        Span<byte> header = stackalloc byte[ChunkFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], ChunkFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], ChunkFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0); // flags
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], grid.Id.E);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], grid.Id.N);
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], ChunkFormat.GridSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)grid.Stride);
        BinaryPrimitives.WriteSingleLittleEndian(header[20..], grid.MinHeight);
        BinaryPrimitives.WriteSingleLittleEndian(header[24..], grid.MaxHeight);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 0); // reserved
        output.Write(header);

        if (BitConverter.IsLittleEndian)
        {
            output.Write(MemoryMarshal.AsBytes(grid.Heights.AsSpan()));
        }
        else
        {
            Span<byte> two = stackalloc byte[2];
            foreach (ushort h in grid.Heights)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(two, h);
                output.Write(two);
            }
        }
    }

    public static ChunkGrid Decode(Stream input)
    {
        Span<byte> header = stackalloc byte[ChunkFormat.HeaderSize];
        input.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != ChunkFormat.Magic)
            throw new InvalidDataException($"Bad chunk magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != ChunkFormat.Version)
            throw new InvalidDataException($"Unsupported chunk version {version}");
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (flags != 0)
            throw new InvalidDataException($"Unsupported chunk flags 0x{flags:X4}");
        var id = new TileId(
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]));
        ushort gridSize = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
        if (gridSize != ChunkFormat.GridSize)
            throw new InvalidDataException($"Unsupported grid size {gridSize}");

        // This word was written as zero and ignored, which is what lets the coarse tile share the
        // format instead of needing one of its own: every .terr already on disk reads back as
        // stride 1, and the decimated companion is the same file with a different number here.
        int stride = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        if (stride == 0) stride = 1;
        if ((ChunkFormat.GridSize - 1) % stride != 0)
            throw new InvalidDataException($"Unsupported chunk stride {stride}");

        float minH = BinaryPrimitives.ReadSingleLittleEndian(header[20..]);
        float maxH = BinaryPrimitives.ReadSingleLittleEndian(header[24..]);

        int size = (ChunkFormat.GridSize - 1) / stride + 1;
        var heights = new ushort[size * size];
        input.ReadExactly(MemoryMarshal.AsBytes(heights.AsSpan()));
        if (!BitConverter.IsLittleEndian)
            for (int i = 0; i < heights.Length; i++)
                heights[i] = BinaryPrimitives.ReverseEndianness(heights[i]);

        return new ChunkGrid(id, heights, minH, maxH, stride);
    }
}
