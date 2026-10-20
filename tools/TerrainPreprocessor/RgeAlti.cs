using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// IGN RGE ALTI 1 m (Lambert-93 / IGN69 GeoTIFFs, Licence Ouverte 2.0) as a terrain source for the
/// LV95 tiles swissALTI3D does not cover (#936): the French side of Lake Geneva.
///
/// <para>
/// Each LV95 tile is resampled straight onto the 2000x2000 cell lattice <see cref="XyzParser"/>
/// fills, so everything after it — <see cref="TileReducer"/>, seams, <c>.terr</c>/<c>.terrc</c>, the
/// manifest — is the swissALTI3D path unchanged. Cells are bilinear in the 1 m French grid; their
/// Lambert-93 positions are exact at every 25 m knot (<see cref="Lv95ToLambert93"/>) and bilinear
/// in between, which is far below a millimetre on a map this smooth.
/// </para>
///
/// <para>
/// Decoded French tiles (4 MB each) sit in a small LRU, capped by <see cref="CacheTiles"/>, so a run
/// stays a few hundred MB whatever the region's size. The build walks LV95 rows north to south and
/// each LV95 tile touches at most a 3x3 block of French ones, so the hit rate stays high.
/// </para>
/// </summary>
public sealed class RgeAlti
{
    /// <summary>Bumped when the resampling changes, so cached edges of French tiles are rebuilt.</summary>
    public const long SourceVersion = 936_001;

    private static readonly Regex NameRe = new(@"_(\d{4})_(\d{4})_MNT_LAMB93_IGN69\.tif$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>File per French 1 km tile, keyed by (x km of its west edge, y km of its north edge).</summary>
    private readonly Dictionary<(int X, int Y), string> _files = new();
    private readonly LinkedList<(int X, int Y)> _lru = new();
    private readonly Dictionary<(int X, int Y), (TiffFloat Tile, LinkedListNode<(int X, int Y)> Node)> _cache = new();
    private readonly Dictionary<(int X, int Y), Lazy<TiffFloat?>> _loading = new();
    private readonly object _gate = new();

    public int CacheTiles = 96;
    /// <summary>Added to every French height: IGN69 -> the LN02 heights of swissALTI3D.</summary>
    public double OffsetZ;

    public int FileCount => _files.Count;

    public RgeAlti(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"--rgealti directory not found: {dir}");
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(dir, "*.tif", opts))
        {
            var m = NameRe.Match(Path.GetFileName(path));
            if (m.Success) _files[(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))] = path;
        }
    }

    /// <summary>
    /// The LV95 tiles in the box with any French file under them, sampled on a 5x5 grid of points
    /// (the corners included), minus <paramref name="exclude"/>.
    /// </summary>
    public List<TileId> TilesIn(int minE, int minN, int maxE, int maxN, IReadOnlySet<TileId> exclude)
    {
        var list = new List<TileId>();
        for (int n = minN; n < maxN; n++)
            for (int e = minE; e < maxE; e++)
            {
                var id = new TileId(e, n);
                if (exclude.Contains(id)) continue;
                bool any = false;
                for (int j = 0; j <= 4 && !any; j++)
                    for (int i = 0; i <= 4 && !any; i++)
                    {
                        var (x, y) = Lv95ToLambert93(id.MinE + 250.0 * i, id.MaxN - 250.0 * j);
                        any = _files.ContainsKey(KeyOf(x, y));
                    }
                if (any) list.Add(id);
            }
        return list;
    }

    /// <summary>Marks a <see cref="SourceTile"/> as resampled from RGE ALTI rather than read from a file.</summary>
    public const string SourcePath = "rgealti:";

    /// <summary>Build sources for <see cref="TilesIn"/>; length and mtime stand in as a version stamp for the edge cache.</summary>
    public List<SourceTile> Sources(int minE, int minN, int maxE, int maxN, IReadOnlySet<TileId> exclude) =>
        TilesIn(minE, minN, maxE, maxN, exclude).Select(id => new SourceTile(id, SourcePath, 0, SourceVersion, 0)).ToList();

    /// <summary>The French tile holding Lambert-93 point (x, y): files are named by west and north edge.</summary>
    private static (int X, int Y) KeyOf(double x, double y) => ((int)Math.Floor(x / 1000), (int)Math.Floor(y / 1000) + 1);

    /// <summary>
    /// Fills a tile's 0.5 m cells (row 0 north, centres at MinE + 0.25 + 0.5c, MaxN - 0.25 - 0.5r) from
    /// the French grid; cells with no French height stay <see cref="XyzParser.MissingCell"/>. Returns
    /// how many cells were filled. With <paramref name="onlyMissing"/> only cells still
    /// <see cref="XyzParser.MissingCell"/> are touched: a swissALTI3D border tile's gaps.
    /// </summary>
    public long Fill(TileId id, ushort[] cells, bool onlyMissing = false)
    {
        const int Cells = XyzParser.CellsPerSide, Knot = 50; // 25 m
        const int Knots = Cells / Knot + 1;
        if (!onlyMissing) Array.Fill(cells, XyzParser.MissingCell);

        var kx = new double[Knots * Knots];
        var ky = new double[Knots * Knots];
        for (int j = 0; j < Knots; j++)
            for (int i = 0; i < Knots; i++)
            {
                // knot (i, j) at the position of cell (i*Knot, j*Knot)'s centre
                var (x, y) = Lv95ToLambert93(id.MinE + 0.25 + 0.5 * i * Knot, id.MaxN - 0.25 - 0.5 * j * Knot);
                kx[j * Knots + i] = x;
                ky[j * Knots + i] = y;
            }

        long filled = 0;
        (int X, int Y) key = (int.MinValue, 0);
        TiffFloat? tile = null;
        for (int r = 0; r < Cells; r++)
        {
            int j = Math.Min(r / Knot, Knots - 2);
            double v = (r - j * Knot) / (double)Knot;
            for (int c = 0; c < Cells; c++)
            {
                if (onlyMissing && cells[r * Cells + c] != XyzParser.MissingCell) continue;
                int i = Math.Min(c / Knot, Knots - 2);
                double u = (c - i * Knot) / (double)Knot;
                int k = j * Knots + i;
                double x = Bilerp(kx[k], kx[k + 1], kx[k + Knots], kx[k + Knots + 1], u, v);
                double y = Bilerp(ky[k], ky[k + 1], ky[k + Knots], ky[k + Knots + 1], u, v);

                double z = Sample(x, y, ref key, ref tile);
                if (double.IsNaN(z)) continue;
                cells[r * Cells + c] = ChunkFormat.Quantize(z + OffsetZ);
                filled++;
            }
        }
        return filled;
    }

    /// <summary>
    /// Height at Lambert-93 (x, y), bilinear between the four nearest 1 m samples, or NaN with no
    /// data. A point near a French tile's edge reads its neighbours' samples too.
    /// </summary>
    public double Sample(double x, double y, ref (int X, int Y) key, ref TiffFloat? tile)
    {
        // sample centres sit on whole metres (x = 916000 + i); fx, fy are the cell's fraction
        double gx = Math.Floor(x), gy = Math.Floor(y);
        double fx = x - gx, fy = y - gy;
        double h00 = At(gx, gy + 1, ref key, ref tile), h10 = At(gx + 1, gy + 1, ref key, ref tile);
        double h01 = At(gx, gy, ref key, ref tile), h11 = At(gx + 1, gy, ref key, ref tile);
        // top row at gy + 1 (north), bottom at gy: v runs north -> south
        double v = 1 - fy;
        if (double.IsNaN(h00) || double.IsNaN(h10) || double.IsNaN(h01) || double.IsNaN(h11))
        {
            // at a data edge, the nearest sample alone
            double near = (fx < 0.5, v < 0.5) switch { (true, true) => h00, (false, true) => h10, (true, false) => h01, _ => h11 };
            return near;
        }
        return Bilerp(h00, h10, h01, h11, fx, v);
    }

    /// <summary>The 1 m sample at whole-metre Lambert-93 (x, y), or NaN.</summary>
    private double At(double x, double y, ref (int X, int Y) key, ref TiffFloat? tile)
    {
        var k = ((int)Math.Floor(x / 1000), (int)Math.Floor((y - 1) / 1000) + 1);
        if (k != key) { key = k; tile = Load(k); }
        if (tile == null) return double.NaN;
        int col = (int)Math.Round((x - tile.OriginX) / tile.PixelX);
        int row = (int)Math.Round((tile.OriginY - y) / tile.PixelY);
        if ((uint)col >= (uint)tile.Width || (uint)row >= (uint)tile.Height) return double.NaN;
        float h = tile.Data[row * tile.Width + col];
        return h == tile.NoData || h < -1000 ? double.NaN : h;
    }

    private TiffFloat? Load((int X, int Y) k)
    {
        Lazy<TiffFloat?> lazy;
        lock (_gate)
        {
            if (_cache.TryGetValue(k, out var hit))
            {
                _lru.Remove(hit.Node);
                _lru.AddFirst(hit.Node);
                return hit.Tile;
            }
            if (!_files.TryGetValue(k, out var path)) return null;
            if (!_loading.TryGetValue(k, out lazy!))
                _loading[k] = lazy = new Lazy<TiffFloat?>(() => TiffFloat.Read(path));
        }
        var tile = lazy.Value; // decoded outside the lock, once, however many workers wait for it
        lock (_gate)
        {
            _loading.Remove(k);
            if (tile != null && !_cache.ContainsKey(k))
            {
                _cache[k] = (tile, _lru.AddFirst(k));
                while (_cache.Count > CacheTiles)
                {
                    var old = _lru.Last!.Value;
                    _lru.RemoveLast();
                    _cache.Remove(old);
                }
            }
        }
        return tile;
    }

    private static double Bilerp(double a, double b, double c, double d, double u, double v) =>
        (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v;

    // ---- LV95 -> Lambert-93 ------------------------------------------------------------------------
    // swisstopo's rigorous inverse oblique Mercator (Bessel), CH1903+ -> ETRS89 by the three-parameter
    // shift, then the Lambert-93 conformal conic (GRS80). Matches GDAL's EPSG:2056 -> EPSG:2154 to the
    // millimetre around Lake Geneva; SwissProjection's approximate formulas are off by 1-2.5 m there.

    private const double BesselA = 6377397.155, BesselE2 = 0.006674372230614;
    private static readonly double BesselE = Math.Sqrt(BesselE2);
    private static readonly double Phi0 = Deg(46 + 57 / 60.0 + 8.66 / 3600), Lam0 = Deg(7 + 26 / 60.0 + 22.5 / 3600);
    private static readonly double SphereR = BesselA * Math.Sqrt(1 - BesselE2) / (1 - BesselE2 * Math.Pow(Math.Sin(Phi0), 2));
    private static readonly double Alpha = Math.Sqrt(1 + BesselE2 / (1 - BesselE2) * Math.Pow(Math.Cos(Phi0), 4));
    private static readonly double B0 = Math.Asin(Math.Sin(Phi0) / Alpha);
    private static readonly double K = Math.Log(Math.Tan(Math.PI / 4 + B0 / 2)) - Alpha * Math.Log(Math.Tan(Math.PI / 4 + Phi0 / 2))
        + Alpha * BesselE / 2 * Math.Log((1 + BesselE * Math.Sin(Phi0)) / (1 - BesselE * Math.Sin(Phi0)));

    private const double Grs80A = 6378137.0, Grs80F = 1 / 298.257222101;
    private static readonly double Grs80E2 = 2 * Grs80F - Grs80F * Grs80F, Grs80E = Math.Sqrt(Grs80E2);
    private static readonly double L93Lam0 = Deg(3);
    private static readonly double L93N, L93F, L93Rho0;

    static RgeAlti()
    {
        double p1 = Deg(44), p2 = Deg(49), p0 = Deg(46.5);
        L93N = (Math.Log(M(p1)) - Math.Log(M(p2))) / (Math.Log(T(p1)) - Math.Log(T(p2)));
        L93F = M(p1) / (L93N * Math.Pow(T(p1), L93N));
        L93Rho0 = Grs80A * L93F * Math.Pow(T(p0), L93N);
    }

    private static double Deg(double d) => d * Math.PI / 180;
    private static double M(double p) => Math.Cos(p) / Math.Sqrt(1 - Grs80E2 * Math.Sin(p) * Math.Sin(p));
    private static double T(double p) =>
        Math.Tan(Math.PI / 4 - p / 2) / Math.Pow((1 - Grs80E * Math.Sin(p)) / (1 + Grs80E * Math.Sin(p)), Grs80E / 2);

    public static (double X, double Y) Lv95ToLambert93(double e, double n)
    {
        // LV95 -> Bessel geodetic
        double lb = (e - 2600000) / SphereR;
        double bb = 2 * (Math.Atan(Math.Exp((n - 1200000) / SphereR)) - Math.PI / 4);
        double b = Math.Asin(Math.Cos(B0) * Math.Sin(bb) + Math.Sin(B0) * Math.Cos(bb) * Math.Cos(lb));
        double l = Math.Atan(Math.Sin(lb) / (Math.Cos(B0) * Math.Cos(lb) - Math.Sin(B0) * Math.Tan(bb)));
        double lam = Lam0 + l / Alpha, phi = b;
        for (int i = 0; i < 6; i++)
        {
            double s = (Math.Log(Math.Tan(Math.PI / 4 + b / 2)) - K) / Alpha
                + BesselE * Math.Log(Math.Tan(Math.PI / 4 + Math.Asin(BesselE * Math.Sin(phi)) / 2));
            phi = 2 * Math.Atan(Math.Exp(s)) - Math.PI / 2;
        }

        // Bessel -> ECEF, shift, ECEF -> GRS80 (height 0: its effect on position is far below a cm)
        double nb = BesselA / Math.Sqrt(1 - BesselE2 * Math.Sin(phi) * Math.Sin(phi));
        double x = nb * Math.Cos(phi) * Math.Cos(lam) + 674.374;
        double y = nb * Math.Cos(phi) * Math.Sin(lam) + 15.056;
        double z = nb * (1 - BesselE2) * Math.Sin(phi) + 405.346;
        lam = Math.Atan2(y, x);
        double p = Math.Sqrt(x * x + y * y);
        phi = Math.Atan2(z, p * (1 - Grs80E2));
        for (int i = 0; i < 6; i++)
        {
            double ng = Grs80A / Math.Sqrt(1 - Grs80E2 * Math.Sin(phi) * Math.Sin(phi));
            double h = p / Math.Cos(phi) - ng;
            phi = Math.Atan2(z, p * (1 - Grs80E2 * ng / (ng + h)));
        }

        // Lambert-93
        double rho = Grs80A * L93F * Math.Pow(T(phi), L93N);
        double th = L93N * (lam - L93Lam0);
        return (700000 + rho * Math.Sin(th), 6600000 + L93Rho0 - rho * Math.Cos(th));
    }
}
