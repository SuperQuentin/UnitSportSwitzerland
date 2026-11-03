using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// swissALTI3D for the cube tiler (#1443, G1): the 0.5 m tiles as float metres (not the build's 7 cm
/// quantisation), parsed once into <c>E_N.f32</c> files in a cache directory, then held in an LRU of
/// <see cref="Capacity"/> tiles (16 MB each). Heights are LHN95; NaN where the tile has none.
/// </summary>
public sealed class SwissAltiSource
{
    public const int Cells = XyzParser.CellsPerSide;

    private static readonly Regex Name = new(@"swissalti3d_\d{4}_(\d{4})-(\d{4})_0\.5_2056", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Dictionary<TileId, string> _files = new();
    private readonly string _cacheDir;
    private readonly object _gate = new();
    private readonly Dictionary<TileId, (Lazy<float[]> Data, long Used)> _loaded = new();
    private long _clock;

    public int Capacity { get; init; } = 300;

    public IReadOnlyCollection<TileId> Tiles => _files.Keys;

    public SwissAltiSource(IEnumerable<string> dirs, string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var dir in dirs)
            foreach (var path in Directory.EnumerateFiles(dir, "*.xyz*", opts))
                if (Name.Match(Path.GetFileName(path)) is { Success: true } m)
                    _files[new TileId(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))] = path; // the newest year last, by name order
    }

    public bool Has(TileId id) => _files.ContainsKey(id);

    /// <summary>The tile's 2000² heights, row 0 north, centres at MinE + 0.25 + 0.5c, MaxN − 0.25 − 0.5r; null with no source.</summary>
    public float[]? Grid(TileId id)
    {
        if (!_files.ContainsKey(id)) return null;
        Lazy<float[]> lazy;
        lock (_gate)
        {
            if (_loaded.TryGetValue(id, out var hit))
            {
                _loaded[id] = (hit.Data, ++_clock);
                lazy = hit.Data;
            }
            else
            {
                if (_loaded.Count >= Capacity)
                {
                    var oldest = _loaded.MinBy(kv => kv.Value.Used).Key;
                    _loaded.Remove(oldest);
                }
                lazy = new Lazy<float[]>(() => Load(id), LazyThreadSafetyMode.ExecutionAndPublication);
                _loaded[id] = (lazy, ++_clock);
            }
        }
        return lazy.Value;
    }

    private float[] Load(TileId id)
    {
        string cache = Path.Combine(_cacheDir, $"{id.E}_{id.N}.f32");
        var grid = new float[Cells * Cells];
        if (File.Exists(cache) && new FileInfo(cache).Length == grid.Length * 4L)
        {
            using var fs = File.OpenRead(cache);
            fs.ReadExactly(MemoryMarshal.AsBytes(grid.AsSpan()));
            return grid;
        }
        XyzParser.ParseFloat(File.ReadAllBytes(_files[id]), _files[id], id, grid, new byte[1 << 20]);
        string tmp = cache + ".tmp";
        using (var fs = File.Create(tmp)) fs.Write(MemoryMarshal.AsBytes(grid.AsSpan()));
        File.Move(tmp, cache, overwrite: true);
        return grid;
    }

    /// <summary>The tile a worker read last, so most samples skip the lookup.</summary>
    public struct Cursor
    {
        public TileId Id;
        public float[]? Grid;
        public bool Missing;
    }

    /// <summary>LHN95 height at an LV95 point, bilinear between the four nearest 0.5 m cells (the nearest alone at a data edge), or NaN.</summary>
    public double Sample(double e, double n, ref Cursor cur)
    {
        double gx = (e - 0.25) * 2, gy = (n - 0.25) * 2;
        long c = (long)Math.Floor(gx), s = (long)Math.Floor(gy);
        double fx = gx - c, fy = gy - s;
        double h00 = At(c, s, ref cur), h10 = At(c + 1, s, ref cur), h01 = At(c, s + 1, ref cur), h11 = At(c + 1, s + 1, ref cur);
        if (double.IsNaN(h00) || double.IsNaN(h10) || double.IsNaN(h01) || double.IsNaN(h11))
            return (fx < 0.5, fy < 0.5) switch { (true, true) => h00, (false, true) => h10, (true, false) => h01, _ => h11 };
        return h00 + (h10 - h00) * fx + (h01 - h00) * fy + (h00 - h10 - h01 + h11) * fx * fy;
    }

    /// <summary>The cell whose centre is at 0.25 + 0.5·(c, s) metres (s counted northward).</summary>
    private double At(long c, long s, ref Cursor cur)
    {
        var id = new TileId((int)Math.Floor(c / (double)Cells), (int)Math.Floor(s / (double)Cells));
        if (cur.Grid == null && !cur.Missing || cur.Id != id)
        {
            cur.Id = id;
            cur.Grid = Grid(id);
            cur.Missing = cur.Grid == null;
        }
        if (cur.Grid == null) return double.NaN;
        int col = (int)(c - (long)id.E * Cells), row = Cells - 1 - (int)(s - (long)id.N * Cells);
        return cur.Grid[row * Cells + col];
    }
}

/// <summary>
/// The heights under a point of the Earth, from every source in order (#1443, G1): swissALTI3D where
/// it has data, then the neighbours' DEMs (<see cref="ForeignDem"/>, their datum offset added, as the
/// LV95 build does). Heights come out LHN95-like; the tiler turns them into ellipsoidal heights
/// through <see cref="SwissGeodesy"/>, the same model both ways (core/swiss-geodesy).
/// </summary>
public sealed class CubeHeightSource
{
    public readonly SwissAltiSource? Swiss;
    public readonly IReadOnlyList<ForeignDem> Dems;

    public CubeHeightSource(SwissAltiSource? swiss, IReadOnlyList<ForeignDem> dems)
    {
        Swiss = swiss;
        Dems = dems;
    }

    public sealed class Cursors
    {
        public SwissAltiSource.Cursor Swiss;
        public readonly ForeignDem.Cursor[] Dems;
        public Cursors(int dems) { Dems = new ForeignDem.Cursor[dems]; for (int i = 0; i < dems; i++) Dems[i] = new ForeignDem.Cursor(); }
    }

    /// <summary>A DEM's grid coordinates of a WGS84 / ETRS89 latitude and longitude (radians).</summary>
    public static (double X, double Y) GridOf(DemCrs crs, double phi, double lam) => crs switch
    {
        DemCrs.Lambert93 => DemProjection.Lambert93(phi, lam),
        DemCrs.Utm32 => DemProjection.Utm(phi, lam, 9 * Math.PI / 180),
        _ => DemProjection.Laea3035(phi, lam),
    };

    /// <summary>
    /// The height (LHN95, with each DEM's offset) at a point given by its LV95 position and its grid
    /// position in every DEM, or NaN where no source has data. <paramref name="demXy"/> holds x, y per DEM.
    /// </summary>
    public double Height(double e, double n, ReadOnlySpan<double> demXy, Cursors cur)
    {
        if (Swiss != null)
        {
            double h = Swiss.Sample(e, n, ref cur.Swiss);
            if (!double.IsNaN(h)) return h;
        }
        for (int d = 0; d < Dems.Count; d++)
        {
            double h = Dems[d].Sample(demXy[2 * d], demXy[2 * d + 1], ref cur.Dems[d]);
            if (!double.IsNaN(h)) return h + Dems[d].OffsetZ;
        }
        return double.NaN;
    }

    /// <summary>
    /// The ellipsoidal height of the ground along a geodetic normal, rigorously: the point on the
    /// normal whose LHN95 height equals the sources' height at its own LV95 position (a fixed point:
    /// a metre of height moves the LV95 point 3e-5 m, so it converges in two or three rounds). A pure
    /// function of the normal, so the same lattice point from two tiles gets the same height to the bit.
    /// </summary>
    public double ExactHeight(EcefPos normal, Cursors cur)
    {
        Span<double> xy = stackalloc double[Math.Max(2, Dems.Count * 2)];
        double h = 550;
        for (int it = 0; it < 8; it++)
        {
            var p = CubeSphere.OnEllipsoid(normal, h);
            var (e, n, hp) = SwissGeodesy.FromEcef(p);
            if (Dems.Count > 0)
            {
                var g = Ellipsoid.Wgs84.ToGeodetic(p);
                for (int d = 0; d < Dems.Count; d++)
                {
                    var (x, y) = GridOf(Dems[d].Crs, g.LatDeg * Math.PI / 180, g.LonDeg * Math.PI / 180);
                    xy[2 * d] = x;
                    xy[2 * d + 1] = y;
                }
            }
            double hs = Height(e, n, xy, cur);
            if (double.IsNaN(hs)) return double.NaN;
            double step = hs - hp;
            h += step;
            if (Math.Abs(step) < 1e-5) break;
        }
        return h;
    }
}
