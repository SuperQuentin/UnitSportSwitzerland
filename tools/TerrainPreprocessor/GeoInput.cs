using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Latitude and longitude from the sources that have them (OpenStreetMap, IGN's BD TOPO, Wikidata) into the LV95
/// build, rigorously (#1507, G2 B3 PR 0): <see cref="SwissGeodesy.ToLv95"/>, never <see cref="SwissProjection"/>'s
/// polynomials (off by up to a metre inside Switzerland, 10 m at Paris), so the LV95 build is an exact baseline for
/// the cube build (<c>--cube-compare</c>), which places the same points on the Earth from their latitude and longitude.
///
/// <para>
/// <b>Heights.</b> The two datums' normals differ, so an LV95 point moves ~3 cm for every kilometre of ellipsoidal
/// height it is converted at (<see cref="SwissGeodesy.HorizontalPerMetre"/>): a summit taken at the Plateau's height
/// lands 10 cm off. A source point is taken at the ground of the built tiles below it when there are tiles
/// (<see cref="Over"/>: their heights every 50 m, a few metres off at worst, a tenth of a millimetre in plan), else
/// at a height the source gives, else at the Plateau's (<see cref="SwissGeodesy.GroundH"/>).
/// </para>
/// </summary>
public sealed class GeoInput
{
    /// <summary>No tiles: every point at its own height when it has one, at the Plateau's otherwise.</summary>
    public static readonly GeoInput NoGround = new(null);

    private const int Stride = 50;   // metres between the kept heights of a tile: 21 × 21 of them
    private const int Side = (ChunkFormat.GridSize - 1) / Stride + 1;

    private readonly string? _dir;
    private readonly Func<double, double, double>? _ground;
    private readonly ConcurrentDictionary<TileId, float[]?> _tiles = new();

    private GeoInput(string? dir, Func<double, double, double>? ground = null) => (_dir, _ground) = (dir, ground);

    /// <summary>Points on any ground (LHN95 at an LV95 point, NaN off it): a cube region's leaves (#1711, <see cref="BuiltLeaves"/>).</summary>
    public static GeoInput On(Func<double, double, double> ground) => new(null, ground);

    /// <summary>Points on the ground of the tiles built in <paramref name="chunkDir"/> (<c>.terrc</c>, else <c>.terr</c>), where there are any.</summary>
    public static GeoInput Over(string? chunkDir) => chunkDir != null && Directory.Exists(chunkDir) ? new GeoInput(chunkDir) : NoGround;

    /// <summary>A point with no height of its own, on the tiles' ground below it (or at <paramref name="hLhn95"/>, then the Plateau's, off the tiles).</summary>
    public (double E, double N) ToLv95(double latDeg, double lonDeg, double? hLhn95 = null)
    {
        var (e, n) = hLhn95 is { } h0 ? At(latDeg, lonDeg, h0) : SwissGeodesy.ToLv95(latDeg, lonDeg);
        double g = Ground(e, n);
        if (double.IsNaN(g)) return (e, n);
        // the ground is a function of the place, and the place moves a few centimetres with it: once is enough
        return At(latDeg, lonDeg, g);
    }

    /// <summary>A point at a height above sea level (LHN95) it is known at, whatever the ground.</summary>
    public static (double E, double N) At(double latDeg, double lonDeg, double hLhn95) =>
        SwissGeodesy.ToLv95(latDeg, lonDeg, hLhn95 + SwissSeaLevel.Instance.Undulation(latDeg, lonDeg));

    /// <summary>The way back for boxes and filters (a box's corners at the Plateau's height: margins, not places).</summary>
    public static (double Lat, double Lon) ToLatLon(double e, double n) => SwissGeodesy.ToLatLon(e, n);

    /// <summary>The tiles' ground (LHN95) at an LV95 point, bilinear over the kept heights; NaN off the tiles.</summary>
    public double Ground(double e, double n)
    {
        if (_ground != null) return _ground(e, n);
        if (_dir == null) return double.NaN;
        var id = TileId.FromLv95(e, n);
        if (_tiles.GetOrAdd(id, Load) is not { } h) return double.NaN;
        double u = Math.Clamp((e - id.MinE) / Stride, 0, Side - 1), v = Math.Clamp((id.MaxN - n) / Stride, 0, Side - 1);
        int c = Math.Min((int)u, Side - 2), r = Math.Min((int)v, Side - 2);
        double fu = u - c, fv = v - r;
        double top = h[r * Side + c] + (h[r * Side + c + 1] - h[r * Side + c]) * fu;
        double bottom = h[(r + 1) * Side + c] + (h[(r + 1) * Side + c + 1] - h[(r + 1) * Side + c]) * fu;
        return top + (bottom - top) * fv;
    }

    private float[]? Load(TileId id)
    {
        foreach (string name in new[] { ChunkFormat.CoarseFileName(id), ChunkFormat.ChunkFileName(id) })
        {
            string path = Path.Combine(_dir!, name);
            if (!File.Exists(path)) continue;
            using var fs = File.OpenRead(path);
            // its own encoding, so the process-wide one (ChunkFormat.Adopt) is left to the stage
            var grid = ChunkCodec.Decode(fs, out var encoding);
            var h = new float[Side * Side];
            for (int r = 0; r < Side; r++)
                for (int c = 0; c < Side; c++)
                    h[r * Side + c] = (float)encoding.Dequantize(grid.HeightAt(c * Stride, r * Stride));
            return h;
        }
        return null;
    }
}
