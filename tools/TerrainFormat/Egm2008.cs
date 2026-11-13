using System.IO.Compression;

namespace UnitSport.Terrain.Format;

/// <summary>
/// The EGM2008 geoid, mean sea level the world over (#1808): its height over the WGS84 ellipsoid, bilinear in a 0.25° grid
/// (1440 × 721 points, rows from 90° N, columns from 180° W, int16 centimetres) made from ETOPO 2022's 60″ geoid grid,
/// the one <c>cube_global</c>'s sea stands on (<c>CubeGlobal</c>: <c>max(H, 0) + N</c>). Against that grid the coarse one is
/// off by 2 cm at the median, 0.3 m at the 99th percentile, 1.2 m at the worst (200,000 random points). Embedded in this
/// assembly (1.7 MB): the game, the server, the tests and BlendCheck all have it. Switzerland's own sea level stays
/// <see cref="SwissSeaLevel"/> (LHN95), which this is not: 50 m at Geneva either way, but the Swiss chain
/// extrapolated is 345 m off in Ladakh and 1.25 km over the Pacific.
/// Thread-safe; loaded on first use (~10 ms).
/// </summary>
public static class Egm2008
{
    public const int Cols = 1440, Rows = 721;
    public const double Step = 0.25;
    private const string ResourceName = "egm2008_15min.gz";

    private static readonly Lazy<short[]?> Grid = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether the grid is there (an assembly built without it has none).</summary>
    public static bool Available => Grid.Value != null;

    /// <summary>The geoid's height over the WGS84 ellipsoid at a latitude and longitude, degrees; NaN without the grid.</summary>
    public static double Undulation(double latDeg, double lonDeg)
    {
        if (Grid.Value is not { } g) return double.NaN;
        double fx = (lonDeg + 180) / Step, fy = (90 - latDeg) / Step;
        fx -= Math.Floor(fx / Cols) * Cols;
        fy = Math.Clamp(fy, 0, Rows - 1);
        int x0 = Math.Min((int)fx, Cols - 1), y0 = Math.Min((int)fy, Rows - 2);
        double tx = fx - x0, ty = fy - y0;
        int x1 = (x0 + 1) % Cols;
        double a = g[y0 * Cols + x0], b = g[y0 * Cols + x1], c = g[(y0 + 1) * Cols + x0], d = g[(y0 + 1) * Cols + x1];
        double top = a + (b - a) * tx, bottom = c + (d - c) * tx;
        return (top + (bottom - top) * ty) / 100.0;
    }

    private static short[]? Load()
    {
        using var raw = typeof(Egm2008).Assembly.GetManifestResourceStream(ResourceName);
        if (raw == null) return null;
        using var gz = new GZipStream(raw, CompressionMode.Decompress);
        var bytes = new byte[Cols * Rows * 2];
        gz.ReadExactly(bytes);
        var grid = new short[Cols * Rows];
        Buffer.BlockCopy(bytes, 0, grid, 0, bytes.Length);
        return grid;
    }
}
