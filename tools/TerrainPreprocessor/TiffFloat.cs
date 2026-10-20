using System.Buffers.Binary;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Reads a single-band float32 GeoTIFF of the shape IGN ships RGE ALTI in: little-endian, strips,
/// no compression or LZW, horizontal predictor (on 32-bit words) or none. Just enough TIFF to need
/// no GDAL (#936); anything else is refused rather than misread.
/// </summary>
public sealed class TiffFloat
{
    public int Width, Height;
    public float[] Data = Array.Empty<float>();
    /// <summary>Geographic position of pixel (0,0)'s centre and the pixel size, from the GeoTIFF tags.</summary>
    public double OriginX, OriginY, PixelX, PixelY;
    public float? NoData;

    public static TiffFloat Read(string path) => Read(File.ReadAllBytes(path), path);

    public static TiffFloat Read(byte[] d, string name)
    {
        if (d.Length < 8 || d[0] != (byte)'I' || d[1] != (byte)'I' || BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)) != 42)
            throw new InvalidDataException($"{name}: not a little-endian classic TIFF");
        int ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(ifd));

        var t = new TiffFloat();
        int bits = 0, compression = 1, predictor = 1, format = 1, samples = 1, rowsPerStrip = 0;
        uint[] offsets = Array.Empty<uint>(), lengths = Array.Empty<uint>();
        double[] scale = Array.Empty<double>(), tie = Array.Empty<double>();
        ushort[] geoKeys = Array.Empty<ushort>();

        for (int i = 0; i < count; i++)
        {
            var e = d.AsSpan(ifd + 2 + 12 * i, 12);
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(e);
            int type = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            int size = type switch { 3 => 2, 4 => 4, 12 => 8, 16 => 8, _ => 1 } * n;
            int at = size <= 4 ? ifd + 2 + 12 * i + 8 : (int)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);

            uint Int(int k) => type == 3 ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + 2 * k))
                : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 4 * k));
            uint[] Ints() { var a = new uint[n]; for (int k = 0; k < n; k++) a[k] = Int(k); return a; }
            double[] Doubles() { var a = new double[n]; for (int k = 0; k < n; k++) a[k] = BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(at + 8 * k)); return a; }

            switch (tag)
            {
                case 256: t.Width = (int)Int(0); break;
                case 257: t.Height = (int)Int(0); break;
                case 258: bits = (int)Int(0); break;
                case 259: compression = (int)Int(0); break;
                case 273: offsets = Ints(); break;
                case 277: samples = (int)Int(0); break;
                case 278: rowsPerStrip = (int)Int(0); break;
                case 279: lengths = Ints(); break;
                case 317: predictor = (int)Int(0); break;
                case 339: format = (int)Int(0); break;
                case 322: throw new InvalidDataException($"{name}: tiled TIFF not supported");
                case 33550: scale = Doubles(); break;
                case 33922: tie = Doubles(); break;
                case 34735: geoKeys = Ints().Select(v => (ushort)v).ToArray(); break;
                case 42113:
                {
                    var s = System.Text.Encoding.ASCII.GetString(d, at, n).TrimEnd('\0', ' ');
                    if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nd))
                        t.NoData = nd;
                    break;
                }
            }
        }

        if (bits != 32 || format != 3 || samples != 1)
            throw new InvalidDataException($"{name}: expected one float32 band (bits {bits}, format {format}, samples {samples})");
        if (compression is not (1 or 5) || predictor is not (1 or 2))
            throw new InvalidDataException($"{name}: compression {compression} / predictor {predictor} not supported");
        if (scale.Length < 2 || tie.Length < 6)
            throw new InvalidDataException($"{name}: no GeoTIFF pixel scale / tie point");
        if (rowsPerStrip <= 0) rowsPerStrip = t.Height;

        // GTRasterTypeGeoKey (1025): 1 = the tie point is a pixel's corner, 2 = its centre
        int rasterType = 1;
        for (int k = 4; k + 3 < geoKeys.Length; k += 4)
            if (geoKeys[k] == 1025 && geoKeys[k + 1] == 0) rasterType = geoKeys[k + 3];
        t.PixelX = scale[0];
        t.PixelY = scale[1];
        double half = rasterType == 2 ? 0 : 0.5;
        t.OriginX = tie[3] + (half - tie[0]) * t.PixelX;
        t.OriginY = tie[4] - (half - tie[1]) * t.PixelY;

        int rowBytes = t.Width * 4;
        var raw = new byte[(long)rowBytes * t.Height];
        for (int s = 0; s < offsets.Length; s++)
        {
            int row0 = s * rowsPerStrip;
            int want = Math.Min(rowsPerStrip, t.Height - row0) * rowBytes;
            var dst = raw.AsSpan(row0 * rowBytes, want);
            var src = d.AsSpan((int)offsets[s], (int)lengths[s]);
            if (compression == 1) src[..want].CopyTo(dst);
            else if (Lzw(src, dst) != want) throw new InvalidDataException($"{name}: strip {s} decoded short");
        }

        if (predictor == 2)
            for (int r = 0; r < t.Height; r++)
            {
                var row = raw.AsSpan(r * rowBytes, rowBytes);
                uint prev = BinaryPrimitives.ReadUInt32LittleEndian(row);
                for (int c = 1; c < t.Width; c++)
                {
                    prev += BinaryPrimitives.ReadUInt32LittleEndian(row[(4 * c)..]);
                    BinaryPrimitives.WriteUInt32LittleEndian(row[(4 * c)..], prev);
                }
            }

        t.Data = new float[t.Width * t.Height];
        Buffer.BlockCopy(raw, 0, t.Data, 0, raw.Length);
        return t;
    }

    /// <summary>TIFF LZW (MSB-first codes, early change); returns the bytes written.</summary>
    private static int Lzw(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        const int Clear = 256, Eoi = 257;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var length = new short[4096];
        var stack = new byte[4096];
        for (int i = 0; i < 256; i++) { prefix[i] = -1; suffix[i] = (byte)i; length[i] = 1; }

        int next = 258, width = 9, old = -1, outPos = 0;
        long bitBuf = 0; int bitCount = 0, pos = 0;
        byte first = 0;
        while (true)
        {
            while (bitCount < width && pos < src.Length) { bitBuf = (bitBuf << 8) | src[pos++]; bitCount += 8; }
            if (bitCount < width) break;
            int code = (int)((bitBuf >> (bitCount - width)) & ((1 << width) - 1));
            bitCount -= width;

            if (code == Eoi) break;
            if (code == Clear) { next = 258; width = 9; old = -1; continue; }

            int emit = code;
            if (old >= 0 && code >= next) emit = old; // KwKwK: the old string plus its own first byte
            else if (code >= next) throw new InvalidDataException("bad LZW code");

            int n = length[emit], k = n;
            for (int c = emit; c >= 0; c = prefix[c]) stack[--k] = suffix[c];
            first = stack[0];
            int room = Math.Min(n, dst.Length - outPos);
            stack.AsSpan(0, room).CopyTo(dst[outPos..]);
            outPos += room;
            if (old >= 0 && code >= next)
            {
                if (outPos < dst.Length) dst[outPos++] = first;
            }

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
