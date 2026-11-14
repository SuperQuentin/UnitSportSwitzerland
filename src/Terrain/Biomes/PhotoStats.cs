using System.Buffers.Binary;
using System.IO.Compression;

namespace UnitSport.Terrain.Biomes;

/// <summary>
/// The globe's satellite colour digested for the fill's biome guess (#1809, <c>docs/plans/fill-biomes.md</c>): a
/// latitude/longitude grid baked once from Blue Marble NG's 12 months (the shipped globe colour's source,
/// <c>World/Globe/BiomePhotoBake</c>), embedded in the assembly so every peer, the tools and the tests read the same
/// bytes. Each cell: the sRGB of its greenest month, how many months it is snowy, and whether it is open water all year.
/// Rows from the north, columns from 180° W; cell centres a half cell in.
/// </summary>
public sealed class PhotoStats
{
    public const string ResourceName = "photo_stats.bin.gz";

    /// <summary>"BPHO" little-endian.</summary>
    public const uint Magic = 0x4F485042;

    public const ushort Version = 1;

    /// <summary>Bytes a cell: R, G, B of the greenest month, then the info byte.</summary>
    public const int Stride = 4;

    /// <summary>The info byte's sea flag (water in at least <see cref="SeaMonths"/> months); its low 4 bits the snowy months.</summary>
    public const byte SeaFlag = 0x80;

    /// <summary>Months of water a cell needs for the sea flag (sea ice turns a polar sea white in winter).</summary>
    public const int SeaMonths = 9;

    public int Cols { get; }
    public int Rows { get; }
    public byte[] Cells { get; }

    public double CellDeg => 360.0 / Cols;

    public PhotoStats(int cols, int rows, byte[] cells)
    {
        if (cells.Length != cols * rows * Stride) throw new ArgumentException($"{cells.Length} bytes for {cols} x {rows} cells");
        Cols = cols;
        Rows = rows;
        Cells = cells;
    }

    private static readonly Lazy<PhotoStats?> Shared = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The grid in the assembly, or null where this build has none (then the guess gives up: the Swiss generator).</summary>
    public static PhotoStats? Embedded => Shared.Value;

    private static PhotoStats? LoadEmbedded()
    {
        using var s = typeof(PhotoStats).Assembly.GetManifestResourceStream(ResourceName);
        return s == null ? null : Read(s);
    }

    public static PhotoStats Read(Stream compressed)
    {
        using var gz = new GZipStream(compressed, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var b = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) throw new InvalidDataException("not a photo statistics grid");
        if (BinaryPrimitives.ReadUInt16LittleEndian(b[4..]) != Version) throw new InvalidDataException("photo statistics version");
        int cols = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), rows = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
        return new PhotoStats(cols, rows, b.Slice(10, cols * rows * Stride).ToArray());
    }

    public void Write(Stream output)
    {
        using var gz = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
        Span<byte> head = stackalloc byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(head[6..], (ushort)Cols);
        BinaryPrimitives.WriteUInt16LittleEndian(head[8..], (ushort)Rows);
        gz.Write(head);
        gz.Write(Cells);
    }

    /// <summary>A cell's photo statistics: summer colour (sRGB 0..1), snowy months (0..12), sea (0..1).</summary>
    public readonly record struct Sample(double R, double G, double B, double SnowMonths, double Sea);

    /// <summary>The statistics at a latitude and longitude, bilinear between cell centres (longitude wraps, latitude clamps).</summary>
    public Sample At(double latDeg, double lonDeg)
    {
        double cell = CellDeg;
        double fx = (lonDeg + 180) / cell - 0.5, fy = (90 - latDeg) / cell - 0.5;
        double x0f = Math.Floor(fx), y0f = Math.Floor(fy);
        double u = fx - x0f, v = fy - y0f;
        int x0 = Wrap((long)x0f), x1 = Wrap((long)x0f + 1);
        // past the first or last row's centres both rows clamp to the same one
        int y0 = Math.Clamp((int)y0f, 0, Rows - 1), y1 = Math.Clamp((int)y0f + 1, 0, Rows - 1);
        double r = 0, g = 0, b = 0, snow = 0, sea = 0;
        Add(x0, y0, (1 - u) * (1 - v));
        Add(x1, y0, u * (1 - v));
        Add(x0, y1, (1 - u) * v);
        Add(x1, y1, u * v);
        return new Sample(r / 255, g / 255, b / 255, snow, sea);

        void Add(int x, int y, double w)
        {
            int o = (y * Cols + x) * Stride;
            r += Cells[o] * w;
            g += Cells[o + 1] * w;
            b += Cells[o + 2] * w;
            byte info = Cells[o + 3];
            snow += (info & 0x0F) * w;
            if ((info & SeaFlag) != 0) sea += w;
        }
    }

    private int Wrap(long x)
    {
        long m = x % Cols;
        return (int)(m < 0 ? m + Cols : m);
    }
}
