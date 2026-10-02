using System.IO.Compression;
using System.Security.Cryptography;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Byte-exact goldens for the tile files whose header goes through <see cref="TileHeader"/> (#221):
/// fixed input, SHA-256 of the written bytes. The hashes were taken on the code before the header
/// was shared; a change here means every tile on disk would have to be rebuilt.
/// </summary>
public class TileHeaderGoldenTests
{
    private static readonly TileId Id = new(2579, 1109);

    private static string Sha(Action<Stream> write)
    {
        using var ms = new MemoryStream();
        write(ms);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static ChunkGrid Grid(int stride)
    {
        int size = (ChunkFormat.GridSize - 1) / stride + 1;
        var h = new ushort[size * size];
        for (int i = 0; i < h.Length; i++) h[i] = (ushort)(i * 2654435761u >> 16);
        return new ChunkGrid(Id, h, 372.5f, 1234.25f, stride);
    }

    [Fact]
    public void Chunk_full_bytes_unchanged() =>
        Assert.Equal("2158A9780EC1CEE9F5386ACB98A9A2847DB8F1E91AAC700967D958BF6BBB09ED", Sha(s => ChunkCodec.Encode(Grid(1), s)));

    [Fact]
    public void Chunk_coarse_bytes_unchanged() =>
        Assert.Equal("33C8125A24193E7AC78B6F15A5677249C63BA9D7DF158652BC495DE69FD53C5E", Sha(s => ChunkCodec.Encode(Grid(10), s)));

    [Fact]
    public void Chunk_ReadHeader_splits_the_count_word_into_grid_size_and_stride()
    {
        using var ms = new MemoryStream();
        ChunkCodec.Encode(Grid(10), ms);
        var h = ChunkCodec.ReadHeader(ms.ToArray());
        Assert.Equal(new TileHeader(ChunkFormat.Magic, ChunkFormat.Version, 0, Id, ChunkFormat.GridSize | 10u << 16), h.Tile);
        Assert.Equal((ChunkFormat.GridSize, (ushort)10, 10), (h.GridSize, h.RawStride, h.Stride));
        Assert.Equal((372.5f, 1234.25f), (h.MinHeight, h.MaxHeight));

        var legacy = new TileHeader(ChunkFormat.Magic, ChunkFormat.Version, 0, Id, ChunkFormat.GridSize);
        var bytes = new byte[ChunkFormat.HeaderSize];
        legacy.Write(bytes);
        Assert.Equal((0, 1), (ChunkCodec.ReadHeader(bytes).RawStride, ChunkCodec.ReadHeader(bytes).Stride));
    }

    [Fact]
    public void Holes_bytes_unchanged() =>
        Assert.Equal("1E53A7E212BC6EC8304C1FC9E5601BC8D0DB7BE17B1244F17DE96E832A362089", Sha(s => HoleFormat.Encode(Id, new[] { 0, 1, 499, 500, 124_999, 249_999 }, s)));

    [Fact]
    public void Trees_bytes_unchanged() =>
        Assert.Equal("EA945DA3E9A06A5DBE8A08D29F36837D992B33EC48738AA4A878FD03FA49AF10", Sha(s => TreeFormat.Encode(Id, new[]
        {
            new TreeInstance(1.5f, 400.25f, 2.75f, 18f, 0),
            new TreeInstance(999.5f, 1200f, 0.125f, 6.5f, 3),
        }, s)));

    /// <summary>
    /// Cover's payload is deflate, whose bytes belong to the runtime's zlib, so only the header is
    /// pinned byte for byte and the payload must inflate back to the same cells.
    /// </summary>
    [Fact]
    public void Cover_header_bytes_unchanged_and_payload_inflates_back()
    {
        var cells = new byte[CoverFormat.Size * CoverFormat.Size];
        for (int i = 0; i < cells.Length; i += 7) cells[i] = (byte)(i % 23);
        using var ms = new MemoryStream();
        CoverFormat.Encode(Id, cells, ms);
        var bytes = ms.ToArray();
        Assert.Equal("5553435601000100130A000055040000E9030000", Convert.ToHexString(bytes, 0, CoverFormat.HeaderSize));

        using var inflate = new DeflateStream(new MemoryStream(bytes, CoverFormat.HeaderSize,
            bytes.Length - CoverFormat.HeaderSize), CompressionMode.Decompress);
        var back = new byte[cells.Length];
        inflate.ReadExactly(back);
        Assert.Equal(cells, back);
    }
}
