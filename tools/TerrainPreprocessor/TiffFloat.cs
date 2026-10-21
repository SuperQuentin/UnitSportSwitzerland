using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Reads single-band float32 GeoTIFFs block by block, with no GDAL (#936, #965): classic TIFF or
/// BigTIFF, either byte order, strips or tiles, no compression / LZW / deflate, horizontal (2) or
/// floating-point (3) predictor. That covers IGN RGE ALTI (LZW strips), BEV's ALS-DTM (50 km tiled
/// LZW BigTIFFs, 10 GB decoded — never read whole) and Bolzano's WCS output (big-endian tiles).
/// Anything else is refused rather than misread.
///
/// <para>
/// <see cref="Open"/> reads the header only. A <em>block</em> is one TIFF tile, or for a striped
/// file the whole image (the strips decoded together), so a caller caches decoded blocks and a
/// 2 GB file costs only the blocks it touches. Reads are positional on one shared handle, so any
/// number of threads may decode blocks of the same file at once.
/// </para>
/// </summary>
public sealed class TiffFloat : IDisposable
{
    public string Path = "";
    public int Width, Height;
    /// <summary>Block size in pixels: the TIFF tile, or the whole image for a striped file.</summary>
    public int BlockW, BlockH;
    public int BlocksAcross => (Width + BlockW - 1) / BlockW;
    public int BlocksDown => (Height + BlockH - 1) / BlockH;
    /// <summary>Geographic position of pixel (0,0)'s centre and the pixel size, from the GeoTIFF tags.</summary>
    public double OriginX, OriginY, PixelX, PixelY;
    public float? NoData;

    private bool _bigEndian, _tiled;
    private int _compression, _predictor, _tileW, _tileH, _rowsPerStrip;
    private long[] _offsets = Array.Empty<long>(), _counts = Array.Empty<long>();
    private SafeFileHandle? _handle;

    public static TiffFloat Open(string path)
    {
        var t = new TiffFloat { Path = path };
        t._handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try { t.ReadHeader(); }
        catch { t.Dispose(); throw; }
        return t;
    }

    public void Dispose() => _handle?.Dispose();

    /// <summary>
    /// Decodes block (<paramref name="bx"/>, <paramref name="by"/>): <see cref="BlockW"/> x
    /// <see cref="BlockH"/> floats, row-major, rows north to south (edge tiles keep the tile's padding).
    /// </summary>
    public float[] ReadBlock(int bx, int by)
    {
        int rowBytes = BlockW * 4;
        var raw = new byte[(long)rowBytes * BlockH];
        if (_tiled)
            DecodeChunk(by * ((Width + _tileW - 1) / _tileW) + bx, raw);
        else
            for (int s = 0; s < _offsets.Length; s++)
            {
                int row0 = s * _rowsPerStrip;
                int rows = Math.Min(_rowsPerStrip, Height - row0);
                DecodeChunk(s, raw.AsSpan(row0 * rowBytes, rows * rowBytes));
            }

        var data = new float[BlockW * BlockH];
        if (_predictor == 3)
        {
            // floating point: bytes differenced along the row, laid out as byte planes, MSB plane first
            var tmp = new byte[rowBytes];
            for (int r = 0; r < BlockH; r++)
            {
                var row = raw.AsSpan(r * rowBytes, rowBytes);
                for (int i = 1; i < rowBytes; i++) row[i] += row[i - 1];
                row.CopyTo(tmp);
                for (int c = 0; c < BlockW; c++)
                {
                    uint v = (uint)tmp[c] << 24 | (uint)tmp[BlockW + c] << 16 | (uint)tmp[2 * BlockW + c] << 8 | tmp[3 * BlockW + c];
                    data[r * BlockW + c] = BitConverter.UInt32BitsToSingle(v);
                }
            }
            return data;
        }

        for (int r = 0; r < BlockH; r++)
        {
            var row = raw.AsSpan(r * rowBytes, rowBytes);
            uint prev = 0;
            for (int c = 0; c < BlockW; c++)
            {
                uint v = _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(row[(4 * c)..]) : BinaryPrimitives.ReadUInt32LittleEndian(row[(4 * c)..]);
                if (_predictor == 2) { v += prev; prev = v; } // horizontal differencing on 32-bit words
                data[r * BlockW + c] = BitConverter.UInt32BitsToSingle(v);
            }
        }
        return data;
    }

    private void DecodeChunk(int index, Span<byte> dst)
    {
        long count = _counts[index];
        if (count == 0) { dst.Fill(0); return; } // sparse tile: never written
        var src = new byte[count];
        RandomAccess.Read(_handle!, src, _offsets[index]);
        switch (_compression)
        {
            case 1:
                src.AsSpan(0, (int)Math.Min(src.Length, dst.Length)).CopyTo(dst);
                break;
            case 5:
                Lzw(src, dst);
                break;
            default: // 8 / 32946: zlib-wrapped deflate
            {
                using var z = new ZLibStream(new MemoryStream(src), CompressionMode.Decompress);
                int got = 0, n;
                while (got < dst.Length && (n = z.Read(dst[got..])) > 0) got += n;
                break;
            }
        }
    }

    private void ReadHeader()
    {
        var head = new byte[16];
        RandomAccess.Read(_handle!, head, 0);
        if (head[0] == 'I' && head[1] == 'I') _bigEndian = false;
        else if (head[0] == 'M' && head[1] == 'M') _bigEndian = true;
        else throw new InvalidDataException($"{Path}: not a TIFF");
        int magic = U16(head, 2);
        bool big = magic == 43;
        if (magic != 42 && !big) throw new InvalidDataException($"{Path}: TIFF magic {magic}");
        long ifd = big ? (long)U64(head, 8) : U32(head, 4);

        var countBuf = new byte[8];
        RandomAccess.Read(_handle!, countBuf, ifd);
        long count = big ? (long)U64(countBuf, 0) : U16(countBuf, 0);
        int entrySize = big ? 20 : 12;
        var entries = new byte[count * entrySize];
        RandomAccess.Read(_handle!, entries, ifd + (big ? 8 : 2));

        int bits = 0, format = 1, samples = 1;
        _compression = 1; _predictor = 1;
        double[] scale = Array.Empty<double>(), tie = Array.Empty<double>(), matrix = Array.Empty<double>();
        long[] geoKeys = Array.Empty<long>();

        for (int i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * entrySize, entrySize);
            int tag = U16(e, 0), type = U16(e, 2);
            long n = big ? (long)U64(e, 4) : U32(e, 4);
            int unit = type switch { 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 or 16 or 17 => 8, _ => 1 };
            long size = unit * n;
            int inline = big ? 8 : 4;
            byte[] val;
            if (size <= inline) val = e.Slice(big ? 12 : 8, inline).ToArray();
            else
            {
                long at = big ? (long)U64(e, 12) : U32(e, 8);
                val = new byte[size];
                RandomAccess.Read(_handle!, val, at);
            }

            long Int(int k) => type switch
            {
                3 => U16(val, 2 * k),
                4 => U32(val, 4 * k),
                16 => (long)U64(val, 8 * k),
                _ => val[k],
            };
            long[] Ints() { var a = new long[n]; for (int k = 0; k < n; k++) a[k] = Int(k); return a; }
            double[] Doubles() { var a = new double[n]; for (int k = 0; k < n; k++) a[k] = BitConverter.Int64BitsToDouble((long)U64(val, 8 * k)); return a; }

            switch (tag)
            {
                case 256: Width = (int)Int(0); break;
                case 257: Height = (int)Int(0); break;
                case 258: bits = (int)Int(0); break;
                case 259: _compression = (int)Int(0); break;
                case 273: case 324: _offsets = Ints(); break;
                case 277: samples = (int)Int(0); break;
                case 278: _rowsPerStrip = (int)Int(0); break;
                case 279: case 325: _counts = Ints(); break;
                case 317: _predictor = (int)Int(0); break;
                case 339: format = (int)Int(0); break;
                case 322: _tileW = (int)Int(0); _tiled = true; break;
                case 323: _tileH = (int)Int(0); break;
                case 33550: scale = Doubles(); break;
                case 33922: tie = Doubles(); break;
                case 34264: matrix = Doubles(); break; // ModelTransformation (GeoServer's WCS)
                case 34735: geoKeys = Ints(); break;
                case 42113:
                {
                    var s = System.Text.Encoding.ASCII.GetString(val, 0, (int)Math.Min(n, val.Length)).TrimEnd('\0', ' ');
                    if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nd))
                        NoData = nd;
                    break;
                }
            }
        }

        if (bits != 32 || format != 3 || samples != 1)
            throw new InvalidDataException($"{Path}: expected one float32 band (bits {bits}, format {format}, samples {samples})");
        if (_compression is not (1 or 5 or 8 or 32946) || _predictor is not (1 or 2 or 3))
            throw new InvalidDataException($"{Path}: compression {_compression} / predictor {_predictor} not supported");
        if (matrix.Length >= 8 && (scale.Length < 2 || tie.Length < 6))
        {
            // x = m0 col + m1 row + m3, y = m4 col + m5 row + m7: north-up only
            if (matrix[1] != 0 || matrix[4] != 0)
                throw new InvalidDataException($"{Path}: a rotated ModelTransformation is not supported");
            scale = new[] { matrix[0], -matrix[5], 0 };
            tie = new[] { 0, 0, 0, matrix[3], matrix[7], 0 };
        }
        if (scale.Length < 2 || tie.Length < 6)
            throw new InvalidDataException($"{Path}: no GeoTIFF pixel scale / tie point");
        if (_rowsPerStrip <= 0) _rowsPerStrip = Height;
        if (_tiled) { BlockW = _tileW; BlockH = _tileH; }
        else
        {
            if ((long)Width * Height > 64L << 20)
                throw new InvalidDataException($"{Path}: a striped image this large ({Width}x{Height}) is not supported");
            BlockW = Width; BlockH = Height;
        }

        // GTRasterTypeGeoKey (1025): 1 = the tie point is a pixel's corner, 2 = its centre
        int rasterType = 1;
        for (int k = 4; k + 3 < geoKeys.Length; k += 4)
            if (geoKeys[k] == 1025 && geoKeys[k + 1] == 0) rasterType = (int)geoKeys[k + 3];
        PixelX = scale[0];
        PixelY = scale[1];
        double half = rasterType == 2 ? 0 : 0.5;
        OriginX = tie[3] + (half - tie[0]) * PixelX;
        OriginY = tie[4] - (half - tie[1]) * PixelY;
    }

    private ushort U16(ReadOnlySpan<byte> s, int at) =>
        _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
    private uint U32(ReadOnlySpan<byte> s, int at) =>
        _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
    private ulong U64(ReadOnlySpan<byte> s, int at) =>
        _bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s[at..]) : BinaryPrimitives.ReadUInt64LittleEndian(s[at..]);

    /// <summary>TIFF LZW (MSB-first codes, early change); returns the bytes written.</summary>
    private static int Lzw(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        const int Clear = 256, Eoi = 257;
        Span<short> prefix = stackalloc short[4096];
        Span<byte> suffix = stackalloc byte[4096];
        Span<short> length = stackalloc short[4096];
        Span<byte> stack = stackalloc byte[4096];
        for (int i = 0; i < 256; i++) { prefix[i] = -1; suffix[i] = (byte)i; length[i] = 1; }

        int next = 258, width = 9, old = -1, outPos = 0;
        long bitBuf = 0; int bitCount = 0, pos = 0;
        byte first = 0;
        while (outPos < dst.Length)
        {
            while (bitCount < width && pos < src.Length) { bitBuf = (bitBuf << 8) | src[pos++]; bitCount += 8; }
            if (bitCount < width) break;
            int code = (int)((bitBuf >> (bitCount - width)) & ((1 << width) - 1));
            bitCount -= width;

            if (code == Eoi) break;
            if (code == Clear) { next = 258; width = 9; old = -1; continue; }

            bool kwkwk = old >= 0 && code >= next; // the old string plus its own first byte
            if (!kwkwk && code >= next) throw new InvalidDataException("bad LZW code");
            int emit = kwkwk ? old : code;

            int n = length[emit], k = n;
            for (int c = emit; c >= 0; c = prefix[c]) stack[--k] = suffix[c];
            first = stack[0];
            int room = Math.Min(n, dst.Length - outPos);
            stack[..room].CopyTo(dst[outPos..]);
            outPos += room;
            if (kwkwk && outPos < dst.Length) dst[outPos++] = first;

            if (old >= 0 && next < 4096)
            {
                prefix[next] = (short)old;
                suffix[next] = first;
                length[next] = (short)(length[old] + 1);
                next++;
            }
            old = code;
            if (next + 1 >= (1 << width) && width < 12) width++;
        }
        return outPos;
    }
}
