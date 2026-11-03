using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Binary encode/decode of .terr chunk files. Shared by the offline preprocessor and the game
/// so the two can never drift apart.
/// </summary>
public static class ChunkCodec
{
    public static void Encode(ChunkGrid grid, Stream output) => Encode(grid, output, ChunkFormat.Current);

    /// <summary>Writes the tile stamped with <paramref name="encoding"/>'s version; the heights must already be quantized with it.</summary>
    public static void Encode(ChunkGrid grid, Stream output, HeightEncoding encoding)
    {
        Span<byte> header = stackalloc byte[ChunkFormat.HeaderSize];
        // u16 grid size then u16 stride: the tile header's u32 count word, read little-endian
        new TileHeader(ChunkFormat.Magic, encoding.Version, 0, grid.Id,
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

    /// <summary>Reads a version 2 or 3 tile; the process-wide encoding follows the first one (see <see cref="ChunkFormat.Adopt"/>).</summary>
    public static ChunkGrid Decode(Stream input)
    {
        var grid = Decode(input, out var encoding);
        ChunkFormat.Adopt(encoding);
        return grid;
    }

    /// <summary>Reads a tile of either version without touching the process-wide choice; says which one it was.</summary>
    public static ChunkGrid Decode(Stream input, out HeightEncoding encoding)
    {
        Span<byte> bytes = stackalloc byte[ChunkFormat.HeaderSize];
        input.ReadExactly(bytes);
        var header = ReadHeader(bytes);
        var tile = header.Tile;

        tile.CheckMagic(ChunkFormat.Magic, "chunk");
        encoding = tile.Version is ChunkFormat.LegacyVersion or ChunkFormat.WideVersion
            ? HeightEncoding.ForVersion(tile.Version)
            : throw new InvalidDataException($"Unsupported chunk version {tile.Version}");
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
