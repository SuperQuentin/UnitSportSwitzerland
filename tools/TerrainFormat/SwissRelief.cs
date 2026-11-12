using System.Buffers.Binary;
using System.IO.Compression;

namespace UnitSport.Terrain.Format;

/// <summary>
/// The game's 500 m heightmap of Switzerland and its border areas (<c>src/Terrain/swiss_relief.gz</c>, swisstopo
/// swissALTIRegio averaged down by <c>tools/swiss_relief.py</c>), read by the build tools (#1746): the flat world's
/// generated fill stands on it (<c>ProceduralWorld.Relief</c>), and the globe's pyramid takes it past the leaves
/// instead of ETOPO's 1.8 km cells (<c>CubeSwiss</c>), so the earth world's far mountains are the flat world's.
/// Godot-free and thread-safe once loaded. Heights come back ellipsoidal (WGS84), as the cube tiles hold them.
/// </summary>
public sealed class SwissRelief
{
    public int Cols { get; }
    public int Rows { get; }
    public double MinE { get; }
    public double MaxN { get; }
    public double Spacing { get; }
    public double MaxE => MinE + (Cols - 1) * Spacing;
    public double MinN => MaxN - (Rows - 1) * Spacing;

    /// <summary>Over how many metres inside the map's edge its heights hand over to the outer source's.</summary>
    public const double EdgeFadeM = 5_000;

    /// <summary>Node heights above sea level (LHN95), metres, row 0 the northernmost.</summary>
    private readonly float[] _h;

    // the map's box in latitude and longitude, a little wide: a point outside it is never converted
    private readonly double _latMin, _latMax, _lonMin, _lonMax;

    private SwissRelief(int cols, int rows, double minE, double maxN, double spacing, float[] h)
    {
        Cols = cols;
        Rows = rows;
        MinE = minE;
        MaxN = maxN;
        Spacing = spacing;
        _h = h;
        _latMin = double.MaxValue; _latMax = double.MinValue; _lonMin = double.MaxValue; _lonMax = double.MinValue;
        foreach (var (e, n) in new[] { (MinE, MinN), (MaxE, MinN), (MinE, MaxN), (MaxE, MaxN), ((MinE + MaxE) / 2, MinN), ((MinE + MaxE) / 2, MaxN) })
        {
            var (lat, lon) = SwissGeodesy.ToLatLon(e, n, 0);
            _latMin = Math.Min(_latMin, lat); _latMax = Math.Max(_latMax, lat);
            _lonMin = Math.Min(_lonMin, lon); _lonMax = Math.Max(_lonMax, lon);
        }
        _latMin -= 0.1; _latMax += 0.1; _lonMin -= 0.1; _lonMax += 0.1;
    }

    /// <summary>Reads the gzip'd "SWRL" file (format in <c>tools/swiss_relief.py</c>).</summary>
    public static SwissRelief Load(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        gz.CopyTo(bytes);
        var b = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        if (b.Length < 28 || System.Text.Encoding.ASCII.GetString(b[..4]) != "SWRL") throw new InvalidDataException($"{path}: not a relief file");
        int version = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
        if (version != 1) throw new InvalidDataException($"{path}: relief version {version}");
        int minE = BinaryPrimitives.ReadInt32LittleEndian(b[8..]);
        int maxN = BinaryPrimitives.ReadInt32LittleEndian(b[12..]);
        int spacing = BinaryPrimitives.ReadInt32LittleEndian(b[16..]);
        int cols = BinaryPrimitives.ReadInt32LittleEndian(b[20..]);
        int rows = BinaryPrimitives.ReadInt32LittleEndian(b[24..]);
        var h = new float[cols * rows];
        for (int k = 0; k < h.Length; k++) h[k] = BinaryPrimitives.ReadUInt16LittleEndian(b[(28 + 2 * k)..]) / 10f;
        return new SwissRelief(cols, rows, minE, maxN, spacing, h);
    }

    /// <summary>
    /// The height above sea level at an LV95 point: Catmull-Rom across the nodes, as the game's
    /// <c>ProceduralWorld.Relief.Macro</c> has it (C1, through every node, the edge nodes carried on outward).
    /// </summary>
    public double Lv95(double e, double n)
    {
        double fx = (e - MinE) / Spacing, fy = (MaxN - n) / Spacing;
        int c = (int)Math.Floor(fx), r = (int)Math.Floor(fy);
        double u = fx - c, v = fy - r;
        double Row(int rr) => CatmullRom(At(c - 1, rr), At(c, rr), At(c + 1, rr), At(c + 2, rr), u);
        return CatmullRom(Row(r - 1), Row(r), Row(r + 1), Row(r + 2), v);
    }

    /// <summary>
    /// The ellipsoidal height at a latitude and longitude (degrees) and how much it counts against another source's
    /// (1 inside the map, falling to 0 over its last <see cref="EdgeFadeM"/>); false off the map.
    /// </summary>
    public bool TryAt(double latDeg, double lonDeg, out double hEllipsoid, out double weight)
    {
        hEllipsoid = 0;
        weight = 0;
        if (latDeg < _latMin || latDeg > _latMax || lonDeg < _lonMin || lonDeg > _lonMax) return false;
        double undulation = SwissSeaLevel.Instance.Undulation(latDeg, lonDeg);
        var (e, n) = SwissGeodesy.ToLv95(latDeg, lonDeg, undulation);
        double inside = Math.Min(Math.Min(e - MinE, MaxE - e), Math.Min(n - MinN, MaxN - n));
        if (inside <= 0) return false;
        double t = Math.Min(1, inside / EdgeFadeM);
        weight = t * t * (3 - 2 * t);
        hEllipsoid = Lv95(e, n) + undulation;
        return true;
    }

    private float At(int c, int r) => _h[Math.Clamp(r, 0, Rows - 1) * Cols + Math.Clamp(c, 0, Cols - 1)];

    /// <summary>The Catmull-Rom segment from b to c; exactly b at t = 0.</summary>
    private static double CatmullRom(double a, double b, double c, double d, double t)
    {
        if (t == 0) return b;
        return b + 0.5 * t * (c - a + t * (2 * a - 5 * b + 4 * c - d + t * (3 * (b - c) + d - a)));
    }
}
