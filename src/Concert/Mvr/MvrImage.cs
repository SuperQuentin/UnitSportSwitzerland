using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// A 3D file's texture as the rig carries it (#973): at most <see cref="MvrImages.MaxSize"/> pixels a
/// side, RGB, as a PNG (what <see cref="RigData.Textures"/> holds), and its mean colour (sRGB), which
/// its faces take when the texture is left off.
/// </summary>
public sealed record MvrImage(int Width, int Height, byte[] Png, Color Average);

/// <summary>
/// Textures in pure .NET (#973): a PNG decoded row by row straight into a box filter down to
/// <see cref="MaxSize"/> (any size reads in little memory), an RGB PNG written back. What the pure
/// decoder cannot read (JPEG, interlaced PNG) goes to <see cref="Fallback"/>, which the game points at
/// Godot's loaders (unit tests have none). Untrusted data: anything wrong is null, never an exception.
/// </summary>
public static class MvrImages
{
    /// <summary>The longest side a texture keeps: small and nearest-filtered, the PS1 look.</summary>
    public const int MaxSize = 64;
    /// <summary>A source larger than this is not read (pixels; a side).</summary>
    private const long MaxPixels = 8192L * 8192;
    private const int MaxSide = 16384;

    /// <summary>Decodes what the PNG reader cannot (JPEG...) into RGB8 rows; null when it cannot either.</summary>
    public static Func<byte[], (int W, int H, byte[] Rgb)?>? Fallback { get; set; }

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>An encoded image (PNG, or what <see cref="Fallback"/> reads) shrunk to a rig texture; null when it is none.</summary>
    public static MvrImage? Shrink(byte[] encoded)
    {
        try
        {
            if (ReadPng(encoded) is { } box) return box.Finish();
            if (Fallback?.Invoke(encoded) is { } px && px.W > 0 && px.H > 0 && px.Rgb.Length >= (long)px.W * px.H * 3) return FromRgb(px.W, px.H, px.Rgb);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException)
        {
        }
        return null;
    }

    /// <summary>RGB8 pixels (rows top down) box-filtered down to <see cref="MaxSize"/>, as a rig texture.</summary>
    public static MvrImage FromRgb(int w, int h, byte[] rgb)
    {
        var box = new Box(w, h, MaxSize);
        for (int y = 0; y < h; y++) box.Row(y, rgb.AsSpan(y * w * 3, w * 3));
        return box.Finish();
    }

    /// <summary>A PNG's size from its header, without decoding it (for <see cref="RigData.Check"/>).</summary>
    public static (int W, int H)? PngSize(byte[]? b)
    {
        if (b == null || b.Length < 33 || !b.AsSpan(0, 8).SequenceEqual(Signature) || BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12)) != 0x49484452) return null;
        uint w = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16)), h = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20));
        return w is >= 1 and <= MaxSide && h is >= 1 and <= MaxSide ? ((int)w, (int)h) : null;
    }

    /// <summary>A PNG's pixels as RGB8 at its own size (at most <see cref="MaxSize"/> a side: a rig texture); null when it is not one.</summary>
    public static (int W, int H, byte[] Rgb)? DecodeSmall(byte[] png)
    {
        try
        {
            // a texture within the size: the box filter is the identity
            if (PngSize(png) is not { W: <= MaxSize, H: <= MaxSize } || ReadPng(png) is not { } box) return null;
            return (box.Width, box.Height, box.Pixels());
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- reading --------------------------------------------------------------------------------------

    /// <summary>A non-interlaced PNG, every colour type and depth, into the box filter; null when it is not one this reads.</summary>
    private static Box? ReadPng(byte[] b)
    {
        if (PngSize(b) is not { } size) return null;
        var (w, h) = size;
        if ((long)w * h > MaxPixels) return null;
        int depth = b[24], type = b[25], interlace = b[28];
        if (interlace != 0 || b[26] != 0 || b[27] != 0) return null;
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || depth is not (1 or 2 or 4 or 8 or 16) || depth < 8 && type is not (0 or 3) || depth == 16 && type == 3) return null;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        int off = 8;
        while (off + 12 <= b.Length)
        {
            int len = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off)), int.MaxValue);
            uint id = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 4));
            if (len > b.Length - off - 12) return null;
            if (id == 0x504C5445) palette = b.AsSpan(off + 8, len).ToArray();   // PLTE
            else if (id == 0x49444154) idat.Write(b, off + 8, len);             // IDAT
            else if (id == 0x49454E44) break;                                   // IEND
            off += 12 + len;
        }
        if (type == 3 && (palette == null || palette.Length < 3)) return null;
        int bits = channels * depth, bpp = Math.Max(1, bits / 8);
        int rowBytes = (int)(((long)w * bits + 7) / 8);
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var prev = new byte[rowBytes];
        var row = new byte[rowBytes];
        var rgb = new byte[w * 3];
        var box = new Box(w, h, MaxSize);
        Span<byte> filter = stackalloc byte[1];
        for (int y = 0; y < h; y++)
        {
            z.ReadExactly(filter);
            z.ReadExactly(row);
            Unfilter(filter[0], row, prev, bpp);
            for (int x = 0; x < w; x++)
            {
                byte r, g, bl;
                if (type == 3)
                {
                    int i = Sample(row, x, depth) * 3;
                    if (i + 2 >= palette!.Length) i = 0;
                    r = palette[i]; g = palette[i + 1]; bl = palette[i + 2];
                }
                else if (depth < 8)
                {
                    int max = (1 << depth) - 1;
                    r = g = bl = (byte)(Sample(row, x, depth) * 255 / max);
                }
                else
                {
                    int step = depth / 8, p = x * channels * step;
                    // 16 bits: the high byte (PNG is big-endian)
                    r = row[p];
                    g = channels >= 3 ? row[p + step] : r;
                    bl = channels >= 3 ? row[p + 2 * step] : r;
                }
                rgb[x * 3] = r; rgb[x * 3 + 1] = g; rgb[x * 3 + 2] = bl;
            }
            box.Row(y, rgb);
            (prev, row) = (row, prev);
        }
        return box;
    }

    private static int Sample(byte[] row, int x, int depth)
    {
        int bit = x * depth;
        return row[bit >> 3] >> (8 - depth - (bit & 7)) & ((1 << depth) - 1);
    }

    private static void Unfilter(byte f, byte[] row, byte[] prev, int bpp)
    {
        switch (f)
        {
            case 0: break;
            case 1: for (int i = bpp; i < row.Length; i++) row[i] += row[i - bpp]; break;
            case 2: for (int i = 0; i < row.Length; i++) row[i] += prev[i]; break;
            case 3: for (int i = 0; i < row.Length; i++) row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + prev[i]) >> 1); break;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
            default: throw new InvalidDataException("a PNG row filter out of range");
        }
    }

    /// <summary>Sums source rows into the cells of the shrunk image; the mean of each cell, and of all.</summary>
    private sealed class Box
    {
        private readonly int _w, _h, _ow, _oh;
        private readonly long[] _sum;
        private readonly int[] _count;
        private readonly int[] _cellX;

        public Box(int w, int h, int max)
        {
            _w = w; _h = h;
            int side = Math.Max(w, h);
            _ow = side <= max ? w : Math.Max(1, (int)((long)w * max / side));
            _oh = side <= max ? h : Math.Max(1, (int)((long)h * max / side));
            _sum = new long[_ow * _oh * 3];
            _count = new int[_ow * _oh];
            _cellX = new int[w];
            for (int x = 0; x < w; x++) _cellX[x] = (int)((long)x * _ow / w);
        }

        public void Row(int y, ReadOnlySpan<byte> rgb)
        {
            int cy = (int)((long)y * _oh / _h) * _ow;
            for (int x = 0; x < _w; x++)
            {
                int c = cy + _cellX[x];
                _sum[c * 3] += rgb[x * 3]; _sum[c * 3 + 1] += rgb[x * 3 + 1]; _sum[c * 3 + 2] += rgb[x * 3 + 2];
                _count[c]++;
            }
        }

        public int Width => _ow;
        public int Height => _oh;

        public byte[] Pixels()
        {
            var px = new byte[_ow * _oh * 3];
            for (int c = 0; c < _count.Length; c++)
            {
                int n = Math.Max(1, _count[c]);
                for (int k = 0; k < 3; k++) px[c * 3 + k] = (byte)((_sum[c * 3 + k] + n / 2) / n);
            }
            return px;
        }

        public MvrImage Finish()
        {
            var px = Pixels();
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < px.Length; i += 3) { r += px[i]; g += px[i + 1]; b += px[i + 2]; }
            float all = 255f * _count.Length;
            return new MvrImage(_ow, _oh, EncodePng(_ow, _oh, px), new Color(r / all, g / all, b / all));
        }
    }

    // ---- writing --------------------------------------------------------------------------------------

    /// <summary>RGB8 pixels as a PNG (no row filters: small images, deflate does the rest).</summary>
    public static byte[] EncodePng(int w, int h, byte[] rgb)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 2;
        WriteChunk(ms, "IHDR"u8, ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
                for (int y = 0; y < h; y++)
                {
                    z.WriteByte(0);
                    z.Write(rgb, y * w * 3, w * 3);
                }
            WriteChunk(ms, "IDAT"u8, raw.ToArray());
        }
        WriteChunk(ms, "IEND"u8, Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(four, (uint)data.Length);
        s.Write(four);
        s.Write(type);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        s.Write(four);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte x in data) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
