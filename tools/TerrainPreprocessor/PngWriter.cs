using System.Buffers.Binary;
using System.IO.Compression;

namespace UnitSport.Tools.Preprocessor;

/// <summary>Minimal 8-bit grayscale PNG writer (no external dependencies).</summary>
public static class PngWriter
{
    public static void WriteGray8(string path, byte[] pixels, int width, int height)
    {
        if (pixels.Length != width * height)
            throw new ArgumentException("pixel buffer size mismatch");

        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // color type: grayscale
        WriteChunk(fs, "IHDR", ihdr.ToArray());

        // scanlines with filter byte 0, zlib-compressed
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var filter = new byte[] { 0 };
            for (int y = 0; y < height; y++)
            {
                z.Write(filter);
                z.Write(pixels, y * width, width);
            }
        }
        WriteChunk(fs, "IDAT", raw.ToArray());
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFF);
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
