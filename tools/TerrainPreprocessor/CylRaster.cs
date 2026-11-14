using System.Globalization;
using System.Text.RegularExpressions;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A whole-body height raster in simple cylindrical (equirectangular) form (#1849): the planetary DEMs USGS
/// distributes (LRO LOLA's 118 m LDEM, MGS MOLA's 463 m). One band of int16, uint16 or float32, uncompressed, in
/// strips, classic TIFF or BigTIFF, either byte order; the GDAL metadata's SCALE and OFFSET and the NoData tag
/// applied. Pixel (0, 0) is the north-west corner of the body: longitude −180° to 180°, latitude 90° to −90°
/// (Width = 2 × Height). Anything else is refused rather than misread.
///
/// <para>
/// Nothing is read at open. <see cref="Window"/> reads a band of rows and a span of longitudes into memory as the
/// file's own 16-bit samples (a 90° × 90° window of LOLA is 1 GB), row after row in file order, so an 8.5 GB file is
/// read once and in order however its windows are cut.
/// </para>
/// </summary>
public sealed class CylRaster : IDisposable
{
    public string Path { get; private init; } = "";
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>A sample's value in metres: raw × <see cref="Scale"/> + <see cref="Offset"/>.</summary>
    public double Scale { get; private set; } = 1;
    public double Offset { get; private set; }

    /// <summary>Degrees a pixel.</summary>
    public double Step => 360.0 / Width;

    private Microsoft.Win32.SafeHandles.SafeFileHandle? _handle;
    private bool _little, _big;
    private int _bits, _format;
    private long[] _offsets = Array.Empty<long>();
    private int _rowsPerStrip;
    private double? _noData;

    public static CylRaster Open(string path)
    {
        var r = new CylRaster { Path = path };
        r._handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        try { r.ReadHeader(); }
        catch { r.Dispose(); throw; }
        return r;
    }

    public void Dispose() => _handle?.Dispose();

    private byte[] ReadAt(long at, int count)
    {
        var b = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = RandomAccess.Read(_handle!, b.AsSpan(got), at + got);
            if (n <= 0) throw new EndOfStreamException($"{Path}: short read at {at + got}");
            got += n;
        }
        return b;
    }

    private ulong U(byte[] b, int at, int size)
    {
        ulong v = 0;
        for (int i = 0; i < size; i++)
            v |= (ulong)b[at + (_little ? i : size - 1 - i)] << (8 * i);
        return v;
    }

    private void ReadHeader()
    {
        var head = ReadAt(0, 16);
        if (head[0] == 'I' && head[1] == 'I') _little = true;
        else if (head[0] != 'M' || head[1] != 'M') throw new InvalidDataException($"{Path}: not a TIFF");
        int magic = (int)U(head, 2, 2);
        _big = magic == 43;
        if (magic != 42 && !_big) throw new InvalidDataException($"{Path}: TIFF magic {magic}");
        long ifd = _big ? (long)U(head, 8, 8) : (long)U(head, 4, 4);

        int entrySize = _big ? 20 : 12, countSize = _big ? 8 : 2;
        long n = (long)U(ReadAt(ifd, countSize), 0, countSize);
        var entries = ReadAt(ifd + countSize, (int)(n * entrySize));
        int compression = 1, samples = 1, planar = 1;
        long stripCount = 0, stripPtr = 0, stripType = 0;
        string? gdal = null, noData = null;
        for (int e = 0; e < n; e++)
        {
            int at = e * entrySize;
            int tag = (int)U(entries, at, 2), type = (int)U(entries, at + 2, 2);
            long count = _big ? (long)U(entries, at + 4, 8) : (long)U(entries, at + 4, 4);
            int valueAt = at + (_big ? 12 : 8), slot = _big ? 8 : 4;
            int size = type switch { 3 or 8 => 2, 4 or 9 or 11 => 4, 16 or 17 or 12 => 8, _ => 1 };
            ulong First() => U(entries, valueAt, size);
            string Text()
            {
                var bytes = count <= slot ? entries.AsSpan(valueAt, (int)count).ToArray() : ReadAt((long)U(entries, valueAt, slot), (int)count);
                return System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
            }
            switch (tag)
            {
                case 256: Width = (int)First(); break;
                case 257: Height = (int)First(); break;
                case 258: _bits = (int)First(); break;
                case 259: compression = (int)First(); break;
                case 277: samples = (int)First(); break;
                case 278: _rowsPerStrip = (int)First(); break;
                case 284: planar = (int)First(); break;
                case 339: _format = (int)First(); break;
                case 273:
                    stripCount = count; stripType = size;
                    stripPtr = count * size <= slot ? -1 - valueAt : (long)U(entries, valueAt, slot);
                    break;
                case 42112: gdal = Text(); break;
                case 42113: noData = Text(); break;
                case 322: throw new InvalidDataException($"{Path}: a tiled TIFF (only strips are read)");
            }
        }
        if (compression != 1) throw new InvalidDataException($"{Path}: compression {compression} (only uncompressed)");
        if (samples != 1 || planar is not (1 or 2)) throw new InvalidDataException($"{Path}: {samples} samples a pixel (one band only)");
        if (!(_bits == 16 && _format is 1 or 2) && !(_bits == 32 && _format == 3))
            throw new InvalidDataException($"{Path}: {_bits} bits, format {_format} (int16, uint16 or float32)");
        if (Width != 2 * Height) throw new InvalidDataException($"{Path}: {Width} x {Height} is not a whole body (2:1)");
        if (_rowsPerStrip <= 0) _rowsPerStrip = Height;

        _offsets = new long[stripCount];
        var table = stripPtr < 0 ? entries.AsSpan((int)(-1 - stripPtr), (int)(stripCount * stripType)).ToArray() : ReadAt(stripPtr, (int)(stripCount * stripType));
        for (int s = 0; s < stripCount; s++) _offsets[s] = (long)U(table, (int)(s * stripType), (int)stripType);

        if (gdal != null)
        {
            var scale = Regex.Match(gdal, "role=\"scale\">([^<]+)<");
            var offset = Regex.Match(gdal, "role=\"offset\">([^<]+)<");
            if (scale.Success) Scale = double.Parse(scale.Groups[1].Value, CultureInfo.InvariantCulture);
            if (offset.Success) Offset = double.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        if (noData != null && double.TryParse(noData, NumberStyles.Float, CultureInfo.InvariantCulture, out double nd)) _noData = nd;
    }

    /// <summary>
    /// Rows <paramref name="y0"/> to <paramref name="y1"/> (inclusive, clamped) and the columns from <paramref name="x0"/>
    /// over <paramref name="w"/> (wrapping round the body; the whole ring when <paramref name="w"/> ≥ <see cref="Width"/>)
    /// as a window in memory. Float32 files are rounded to the file's metres in 16 bits: refused if a value would not fit.
    /// </summary>
    public Window Read(int y0, int y1, int x0, int w)
    {
        y0 = Math.Max(0, y0);
        y1 = Math.Min(Height - 1, y1);
        if (w >= Width) { x0 = 0; w = Width; }
        x0 = ((x0 % Width) + Width) % Width;
        int rows = y1 - y0 + 1, bytes = _bits / 8;
        var data = new short[(long)rows * w];
        var row = new byte[Width * bytes];
        for (int r = 0; r < rows; r++)
        {
            int y = y0 + r;
            long at = _offsets[y / _rowsPerStrip] + (long)(y % _rowsPerStrip) * Width * bytes;
            int got = 0;
            while (got < row.Length)
            {
                int k = RandomAccess.Read(_handle!, row.AsSpan(got), at + got);
                if (k <= 0) throw new EndOfStreamException($"{Path}: short read in row {y}");
                got += k;
            }
            long o = (long)r * w;
            for (int c = 0; c < w; c++)
            {
                int x = x0 + c;
                if (x >= Width) x -= Width;
                data[o + c] = Sample(row, x);
            }
        }
        return new Window(this, y0, rows, x0, w, data);
    }

    private const short Missing = short.MinValue;

    private short Sample(byte[] row, int x)
    {
        if (_bits == 32)
        {
            float f = BitConverter.Int32BitsToSingle((int)U(row, x * 4, 4));
            if (_noData is { } nd && f == nd) return Missing;
            double m = Math.Round(f * Scale + Offset);
            if (m is < -32767 or > 32767) throw new InvalidDataException($"{Path}: {m} m does not fit 16 bits");
            return (short)m;
        }
        int raw = (int)U(row, x * 2, 2);
        if (_format == 2) raw = (short)raw;
        if (_noData is { } n16 && raw == n16) return Missing;
        return _format == 2 ? (short)raw : (short)(raw - 32768);
    }

    /// <summary>The raw 16-bit sample of a uint16 file is shifted by 32,768 into a short: its metres.</summary>
    private double Metres(short s) => _bits == 32 ? s : ((_format == 2 ? s : s + 32768.0) * Scale + Offset);

    /// <summary>
    /// A band of the raster in memory: bilinear heights in metres at a latitude and longitude, longitude wrapping, the
    /// poles clamped; a missing sample takes the mean of the valid ones round it.
    /// </summary>
    public sealed class Window
    {
        private readonly CylRaster _r;
        private readonly short[] _data;
        public int Y0 { get; }
        public int Rows { get; }
        public int X0 { get; }
        public int Columns { get; }

        internal Window(CylRaster r, int y0, int rows, int x0, int columns, short[] data)
        {
            _r = r; Y0 = y0; Rows = rows; X0 = x0; Columns = columns; _data = data;
        }

        public long Bytes => _data.LongLength * 2;

        private bool Has(int x, int y, out double m)
        {
            int c = x - X0;
            c = ((c % _r.Width) + _r.Width) % _r.Width;
            int r = y - Y0;
            m = 0;
            if (r < 0 || r >= Rows || c >= Columns)
                throw new ArgumentOutOfRangeException(nameof(x), $"pixel {x},{y} is outside the window (rows {Y0}+{Rows}, columns {X0}+{Columns})");
            short s = _data[(long)r * Columns + c];
            if (s == Missing) return false;
            m = _r.Metres(s);
            return true;
        }

        /// <summary>The height at a latitude and longitude, degrees: bilinear between the four pixel centres round it.</summary>
        public double At(double lat, double lon)
        {
            double step = _r.Step;
            double fx = (lon + 180) / step - 0.5, fy = (90 - lat) / step - 0.5;
            fy = Math.Clamp(fy, 0, _r.Height - 1);
            int x0 = (int)Math.Floor(fx), y0 = Math.Min((int)fy, _r.Height - 2);
            double tx = fx - x0, ty = fy - y0, sum = 0, weight = 0;
            for (int b = 0; b < 2; b++)
                for (int a = 0; a < 2; a++)
                {
                    double wgt = (a == 0 ? 1 - tx : tx) * (b == 0 ? 1 - ty : ty);
                    if (Has(x0 + a, y0 + b, out double m)) { sum += wgt * m; weight += wgt; }
                }
            if (weight > 1e-6) return sum / weight;
            // on a missing sample: the plain mean of the valid ones in the 4 x 4 round it
            sum = 0;
            int count = 0;
            for (int b = -1; b <= 2; b++)
                for (int a = -1; a <= 2; a++)
                {
                    int y = Math.Clamp(y0 + b, 0, _r.Height - 1);
                    if (Has(x0 + a, y, out double m)) { sum += m; count++; }
                }
            return count > 0 ? sum / count : 0;
        }

        /// <summary>
        /// The mean over a square footprint <paramref name="halfM"/> metres each way round a point on a body of radius
        /// <paramref name="radius"/>: a k × k grid of bilinear samples, k from the footprint against the pixel (1 to 8),
        /// as <c>EtopoGrid.Footprint</c>.
        /// </summary>
        public double Footprint(double lat, double lon, double halfM, double radius)
        {
            double pixelM = _r.Step * Math.PI / 180 * radius;
            int k = Math.Clamp((int)Math.Ceiling(2 * halfM / pixelM), 1, 8);
            if (k == 1) return At(lat, lon);
            double cos = Math.Max(Math.Cos(lat * Math.PI / 180), 0.01), sum = 0;
            for (int b = 0; b < k; b++)
            {
                double dy = -halfM + (b + 0.5) * 2 * halfM / k;
                double la = Math.Clamp(lat + dy / radius * 180 / Math.PI, -90, 90);
                for (int a = 0; a < k; a++)
                {
                    double dx = -halfM + (a + 0.5) * 2 * halfM / k;
                    sum += At(la, lon + dx / (radius * cos) * 180 / Math.PI);
                }
            }
            return sum / (k * k);
        }
    }
}
