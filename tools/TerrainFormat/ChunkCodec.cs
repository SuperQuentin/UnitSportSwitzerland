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
        // u16 grid size then u16 stride: the tile header's u32 count word, read little-endian
        new TileHeader(ChunkFormat.Magic, ChunkFormat.Version, 0, grid.Id,
            ChunkFormat.GridSize | (uint)(ushort)grid.Stride << 16).Write(header);
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
        Span<byte> bytes = stackalloc byte[ChunkFormat.HeaderSize];
        input.ReadExactly(bytes);
        var header = ReadHeader(bytes);
        var tile = header.Tile;

        tile.CheckMagic(ChunkFormat.Magic, "chunk");
        tile.CheckVersion(ChunkFormat.Version, "chunk");
        if (tile.Flags != 0)
            throw new InvalidDataException($"Unsupported chunk flags 0x{tile.Flags:X4}");
        if (header.GridSize != ChunkFormat.GridSize)
            throw new InvalidDataException($"Unsupported grid size {header.GridSize}");

        int stride = header.Stride;
        if ((ChunkFormat.GridSize - 1) % stride != 0)
            throw new InvalidDataException($"Unsupported chunk stride {stride}");

        int size = (ChunkFormat.GridSize - 1) / stride + 1;
        var heights = new ushort[size * size];
        input.ReadExactly(MemoryMarshal.AsBytes(heights.AsSpan()));
        if (!BitConverter.IsLittleEndian)
            for (int i = 0; i < heights.Length; i++)
                heights[i] = BinaryPrimitives.ReverseEndianness(heights[i]);

        return new ChunkGrid(tile.Id, heights, header.MinHeight, header.MaxHeight, stride);
    }

    /// <summary>
    /// The 32-byte .terr header, parsed without any check: <see cref="Decode"/> validates it and
    /// throws, the preprocessor's resume scan only looks (<c>TerrainBuild.IsValidTerr</c>).
    /// </summary>
    public static ChunkHeader ReadHeader(ReadOnlySpan<byte> h)
    {
        var tile = TileHeader.Read(h);
        return new ChunkHeader(tile, (ushort)tile.Count, (ushort)(tile.Count >> 16),
            BinaryPrimitives.ReadSingleLittleEndian(h[20..]),
            BinaryPrimitives.ReadSingleLittleEndian(h[24..]));
    }
}

/// <param name="RawStride">
/// The stride word as stored. It was written as zero and ignored before coarse tiles existed, which
/// is what lets the coarse tile share the format instead of needing one of its own: every old .terr
/// reads back as stride 1, and the decimated companion is the same file with a different number here.
/// </param>
public readonly record struct ChunkHeader(TileHeader Tile, ushort GridSize, ushort RawStride, float MinHeight, float MaxHeight)
{
    /// <summary><see cref="RawStride"/> with the legacy 0 read as 1.</summary>
    public int Stride => RawStride == 0 ? 1 : RawStride;
}
