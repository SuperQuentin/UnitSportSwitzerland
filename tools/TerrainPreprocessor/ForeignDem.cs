using System.Buffers.Text;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A neighbour's elevation model as a terrain source for the LV95 tiles swissALTI3D does not cover,
/// and for the -9999 gaps of its border tiles (#936 France, #965 Germany / Austria / Italy):
/// GeoTIFFs (<see cref="TiffFloat"/>) or zipped XYZ text (Baden-Württemberg's DGM1), in one of the
/// <see cref="DemCrs"/> grids, with a height offset to swissALTI3D's LN02.
///
/// <para>
/// Each LV95 tile is resampled straight onto the 2000x2000 cell lattice <see cref="XyzParser"/>
/// fills, so everything after it — <see cref="TileReducer"/>, seams, <c>.terr</c>/<c>.terrc</c>, the
/// manifest — is the swissALTI3D path unchanged. Cells are bilinear in the source grid; their
/// positions in it are exact at every 25 m knot (<see cref="DemProjection"/>) and bilinear between,
/// far below a millimetre on a map this smooth.
/// </para>
///
/// <para>
/// Decoded blocks (a TIFF tile, a striped file, or one XYZ kilometre) sit in an LRU capped at
/// <see cref="CacheBytes"/>, so a run stays a few hundred MB whatever the region's size — BEV's
/// 50 km tiles are 10 GB decoded. The build walks LV95 rows north to south, so the hit rate is high.
/// </para>
/// </summary>
public sealed class ForeignDem
{
    /// <summary>Bumped when the resampling changes, so cached edges of foreign tiles are rebuilt.</summary>
    public const long SourceVersion = 936_001;

    /// <summary>Marks a <see cref="SourceTile"/> as resampled from foreign DEMs rather than read from a file.</summary>
    public const string SourcePath = "dem:";

    public readonly DemCrs Crs;
    public readonly string Dir;
    /// <summary>Added to every height: the source's datum -> the LN02 heights of swissALTI3D.</summary>
    public double OffsetZ;
    public long CacheBytes = 384L << 20;
    /// <summary>Threads <see cref="TilesIn"/> uses (the run's --jobs; the probes are cheap but there are a million tiles in France).</summary>
    public int Jobs = 4;
    /// <summary>
    /// Only the top-level folders of the DEM directory whose name holds <c>_D074_</c> (for <c>D074</c>)
    /// are scanned. For a test run on one or two départements of the France tree; null: all.
    /// </summary>
    public static string[]? OnlyFolders;

    /// <summary>
    /// True once any raster is a <see cref="Entry.Lazy"/> one (IGN RGE ALTI named by its corner):
    /// no file was opened, so <see cref="TilesIn"/> judges coverage by the files' extents alone.
    /// </summary>
    public bool CoverageOnly { get; private set; }

    /// <summary>A georeferenced raster in the source grid: one TIFF, or one XYZ kilometre in a zip.</summary>
    private sealed class Entry
    {
        public required string Name;
        public TiffFloat? Tiff;
        public string? Zip, Member;
        public double OriginX, OriginY, PixelX, PixelY; // pixel (0,0)'s centre, pixel size
        public int Width, Height, BlockW, BlockH;
        public float? NoData;
        /// <summary>
        /// A 1 km RGE ALTI tile placed from its file name (<c>RGEALTI_FXX_0982_6550_MNT_...</c>: the
        /// north-west corner in Lambert-93 km); opened only when a block is read and closed after it, so
        /// 560k of them cost no file handles. The header is checked against the name on that first read.
        /// </summary>
        public bool Lazy;
        /// <summary>
        /// For a lazy tile on the edge of the delivered area (a neighbour is missing: the coast, a
        /// border): which 100 m squares (10x10, row-major from the north-west) hold data. Shared by the
        /// copies of one tile; null: a tile with all eight neighbours present has data throughout.
        /// </summary>
        public bool[]? Mask;
        public int KmX, KmY;
        public double MinX => OriginX - PixelX / 2;
        public double MaxX => OriginX + (Width - 0.5) * PixelX;
        public double MaxY => OriginY + PixelY / 2;
        public double MinY => OriginY - (Height - 0.5) * PixelY;
    }

    private readonly List<Entry> _entries = new();
    /// <summary>Entries by the 1 km cells of the source grid they touch.</summary>
    private readonly Dictionary<(int, int), List<int>> _index = new();

    private readonly object _gate = new();
    private readonly LinkedList<(int Entry, int Bx, int By)> _lru = new();
    private readonly Dictionary<(int, int, int), (float[] Data, LinkedListNode<(int, int, int)> Node)> _cache = new();
    private readonly Dictionary<(int, int, int), Lazy<float[]>> _loading = new();
    private long _cachedBytes;

    /// <summary>RGE ALTI 1 m height tiles: <c>RGEALTI_FXX_&lt;E km&gt;_&lt;N km&gt;_MNT_...tif</c> (the SRC and DST companions are not heights). The sample (0,0) is at the integer Lambert-93 metre the name gives (north-west corner), checked against each header on the first read.</summary>
    private static readonly Regex RgeName = new(@"^RGEALTI_FXX_(\d{4})_(\d{4})_MNT_.*\.tif$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex XyzKm = new(@"_(\d{3,4})_(\d{4})_1_", RegexOptions.CultureInvariant);

    public int FileCount => _entries.Count;
    public string Name => $"{Crs} {Path.GetFileName(Dir.TrimEnd('/', '\\'))}";

    public ForeignDem(DemCrs crs, string dir, double offsetZ = 0)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"DEM directory not found: {dir}");
        Crs = crs; Dir = dir; OffsetZ = offsetZ;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        int skipped = 0;
        string? lastSkip = null;
        foreach (var path in EnumerateRasters(dir, opts).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            {
                if (RgeName.Match(System.IO.Path.GetFileName(path)) is { Success: true } rge)
                {
                    double x0 = int.Parse(rge.Groups[1].Value) * 1000.0, y0 = int.Parse(rge.Groups[2].Value) * 1000.0;
                    Add(new Entry
                    {
                        Name = path, Lazy = true, KmX = int.Parse(rge.Groups[1].Value), KmY = int.Parse(rge.Groups[2].Value), OriginX = x0, OriginY = y0, PixelX = 1, PixelY = 1, // IGN puts the sample at the integer metre
                        Width = 1000, Height = 1000, BlockW = 1000, BlockH = 1000, NoData = -99999f,
                    });
                    CoverageOnly = true;
                    continue;
                }
                TiffFloat t;
                try { t = TiffFloat.Open(path); }
                catch (InvalidDataException e) { skipped++; lastSkip = e.Message; continue; } // e.g. RGE ALTI's 8-bit DST/SRC companions
                Add(new Entry
                {
                    Name = path, Tiff = t, OriginX = t.OriginX, OriginY = t.OriginY, PixelX = t.PixelX, PixelY = t.PixelY,
                    Width = t.Width, Height = t.Height, BlockW = t.BlockW, BlockH = t.BlockH, NoData = t.NoData,
                });
            }
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Baden-Württemberg: dgm1_32_<E km>_<N km>_1_bw_<year>.xyz, 1 m point centres at .5,
                // the kilometre whose south-west corner the name gives
                using var zip = ZipFile.OpenRead(path);
                foreach (var z in zip.Entries)
                {
                    if (!z.Name.EndsWith(".xyz", StringComparison.OrdinalIgnoreCase)) continue;
                    var m = XyzKm.Match(z.Name);
                    if (!m.Success) continue;
                    double e0 = int.Parse(m.Groups[1].Value) * 1000.0, n0 = int.Parse(m.Groups[2].Value) * 1000.0;
                    Add(new Entry
                    {
                        Name = $"{path}!{z.FullName}", Zip = path, Member = z.FullName,
                        OriginX = e0 + 0.5, OriginY = n0 + 999.5, PixelX = 1, PixelY = 1,
                        Width = 1000, Height = 1000, BlockW = 1000, BlockH = 1000,
                    });
                }
            }
        }
        if (skipped > 0) Console.WriteLine($"  {Name}: skipped {skipped} files that are not one float32 band, e.g. {lastSkip}");
    }

    /// <summary>
    /// The rasters under <paramref name="dir"/>, skipping what is not a height: folders still being
    /// extracted (<c>*.tmp</c>), IGN's SRC and DST companions, metadata and supplements, and (with
    /// <see cref="OnlyFolders"/>) the départements not asked for.
    /// </summary>
    private static IEnumerable<string> EnumerateRasters(string dir, EnumerationOptions opts)
    {
        var tops = Directory.EnumerateDirectories(dir).Where(d => !IsSkippedFolder(System.IO.Path.GetFileName(d))
            && (OnlyFolders is not { } only
                || only.Any(o => System.IO.Path.GetFileName(d).Contains($"_{o}_", StringComparison.OrdinalIgnoreCase)))).Append(dir);
        foreach (var top in tops)
        {
            bool root = top == dir;
            var o2 = root ? new EnumerationOptions { IgnoreInaccessible = true } : opts;
            var en = new FileSystemEnumerable<string>(top, (ref FileSystemEntry e) => e.ToFullPath(), o2)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && !IsCompanion(e.FileName),
                ShouldRecursePredicate = (ref FileSystemEntry e) => !IsSkippedFolder(e.FileName),
            };
            foreach (var p in en) yield return p;
        }
    }

    private static bool IsCompanion(ReadOnlySpan<char> name) =>
        name.Contains("_SRC_", StringComparison.OrdinalIgnoreCase) || name.Contains("_DST_", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkippedFolder(ReadOnlySpan<char> name) =>
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("2_METADONNEES") || name.StartsWith("3_SUPPLEMENTS")
        || IsCompanion(name);

    private void Add(Entry e)
    {
        int i = _entries.Count;
        _entries.Add(e);
        // cells the raster's half-open extent touches: a 1 km tile on the lattice is one cell, not four
        for (int y = (int)Math.Floor((e.MinY + 1e-6) / 1000); y <= (int)Math.Floor(e.MaxY / 1000); y++)
            for (int x = (int)Math.Floor(e.MinX / 1000); x <= (int)Math.Floor((e.MaxX - 1e-6) / 1000); x++)
            {
                if (!_index.TryGetValue((x, y), out var list)) _index[(x, y)] = list = new List<int>();
                list.Add(i);
            }
    }

    // ---- which tiles ----------------------------------------------------------------------------

    /// <summary>
    /// The LV95 tiles in the box with real data under at least <paramref name="minProbes"/> of a 5x5
    /// grid of points (the corners included), minus <paramref name="exclude"/>. Reads the blocks under
    /// the probes: a file that covers a tile with no-data alone does not make it a tile, and a tile the
    /// data only clips is left to the generated fill rather than stretched from a few rows.
    /// </summary>
    public List<TileId> TilesIn(int minE, int minN, int maxE, int maxN, IReadOnlySet<TileId> exclude, int minProbes = 1)
    {
        if (CoverageOnly) EnsureMasks();
        var ids = new List<TileId>();
        for (int n = minN; n < maxN; n++)
            for (int e = minE; e < maxE; e++)
                if (!exclude.Contains(new TileId(e, n))) ids.Add(new TileId(e, n));
        var hit = new System.Collections.Concurrent.ConcurrentBag<TileId>();
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Jobs) }, id =>
        {
            var cur = new Cursor();
            int found = 0;
            // the corners and edges are shared with the neighbours; a tile of lazy rasters is judged by
            // 10x10 points strictly inside it (one per 100 m square of the edge masks), so data that lies
            // only next door does not make it a tile and a sliver of data (a summit on the border) does
            int side = CoverageOnly ? 10 : 5;
            double first = CoverageOnly ? 50 : 0, step = CoverageOnly ? 100 : 250;
            int need = CoverageOnly && minProbes > 1 ? minProbes * 4 : minProbes;
            if (CoverageOnly)
            {
                // sea, other countries: nothing within two kilometres of the centre, so none of the 100 probes can hit
                var (cx, cy) = DemProjection.FromLv95(Crs, id.MinE + 500.0, id.MaxN - 500.0);
                int kx = (int)Math.Floor(cx / 1000), ky = (int)Math.Floor(cy / 1000);
                bool near = false;
                for (int dy = -2; dy <= 2 && !near; dy++)
                    for (int dx = -2; dx <= 2 && !near; dx++)
                        near = _index.ContainsKey((kx + dx, ky + dy));
                if (!near) return;
            }
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    var (x, y) = DemProjection.FromLv95(Crs, id.MinE + first + step * i, id.MaxN - first - step * j);
                    if (!Covers(x, y)) continue;
                    // France's tree: a file under the probe is the data (reading 560k tiles' blocks to
                    // ask would decode every French tile several times before the build even starts)
                    if ((CoverageOnly ? HasData(x, y) : !double.IsNaN(Sample(x, y, ref cur))) && ++found >= need) { hit.Add(id); return; }
                }
        });
        return hit.OrderByDescending(t => t.N).ThenBy(t => t.E).ToList();
    }

    /// <summary>
    /// The LV95 tiles box (km, max exclusive) that holds this DEM, found by probing a 5 km lattice
    /// of LV95 points for rasters and padding by 10 km: the box for <c>--dem-box auto</c>.
    /// </summary>
    public (int MinE, int MinN, int MaxE, int MaxN) BoundsLv95Km()
    {
        int minE = int.MaxValue, minN = int.MaxValue, maxE = int.MinValue, maxN = int.MinValue;
        for (int n = 0; n <= 2400; n += 5)
            for (int e = 800; e <= 3800; e += 5)
            {
                var (x, y) = DemProjection.FromLv95(Crs, e * 1000.0 + 2500, n * 1000.0 + 2500);
                if (!Covers(x, y) || FindAt(x, y) < 0) continue;
                minE = Math.Min(minE, e); maxE = Math.Max(maxE, e);
                minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
            }
        if (minE == int.MaxValue) throw new InvalidOperationException($"{Name}: no raster lands in the LV95 window E 800..3800 km, N 0..2400 km");
        return (minE - 10, minN - 10, maxE + 15, maxN + 15);
    }

    private int FindAt(double x, double y) => Find(x, y, -1);

    private bool _masked;

    /// <summary>
    /// Reads the tiles on the edge of the delivered area, once, to learn where their data stops (the
    /// coast, the border: IGN delivers a whole tile with -99999 beyond it). Every other tile has all
    /// eight neighbours, so it is data throughout and is never opened here: a tile choice that read the
    /// 560k blocks would decode each French tile several times before the build starts, and this reads
    /// about one in fifty, in parallel.
    /// </summary>
    private void EnsureMasks()
    {
        lock (_gate)
        {
            if (_masked) return;
            var maskClock = System.Diagnostics.Stopwatch.StartNew();
            var byKm = new Dictionary<(int, int), List<Entry>>();
            foreach (var e in _entries)
                if (e.Lazy)
                {
                    if (!byKm.TryGetValue((e.KmX, e.KmY), out var l)) byKm[(e.KmX, e.KmY)] = l = new List<Entry>();
                    l.Add(e);
                }
            var edge = byKm.Where(kv =>
            {
                var (x, y) = kv.Key;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (!byKm.ContainsKey((x + dx, y + dy))) return true;
                return false;
            }).Select(kv => kv.Value).ToList();
            Parallel.ForEach(edge, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Jobs) }, copies =>
            {
                var mask = new bool[100];
                foreach (var e in copies)
                {
                    var data = Decode(e, 0, 0);
                    for (int r = 0; r < 1000; r++)
                        for (int c = 0; c < 1000; c++)
                        {
                            float h = data[r * 1000 + c];
                            if (!(float.IsNaN(h) || h == e.NoData || h < -1000 || h > 9000)) mask[r / 100 * 10 + c / 100] = true;
                        }
                }
                foreach (var e in copies) e.Mask = mask;
            });
            Console.WriteLine($"  {Name}: {edge.Count} of {byKm.Count} tiles are on the edge of the delivered area; their data read in {maskClock.Elapsed.TotalSeconds:F0}s");
            _masked = true;
        }
    }

    /// <summary>Whether a raster holds data at (x, y), judged by <see cref="Entry.Mask"/> for an edge tile (100 m squares).</summary>
    private bool HasData(double x, double y)
    {
        int ei = Find(x, y, -1);
        if (ei < 0) return false;
        var e = _entries[ei];
        if (e.Mask == null) return true;
        int col = (int)Math.Clamp(Math.Round((x - e.OriginX) / e.PixelX), 0, 999), row = (int)Math.Clamp(Math.Round((e.OriginY - y) / e.PixelY), 0, 999);
        return e.Mask[row / 100 * 10 + col / 100];
    }

    private bool Covers(double x, double y) => _index.ContainsKey(((int)Math.Floor(x / 1000), (int)Math.Floor(y / 1000)));

    /// <summary>Build sources for these tiles; length and mtime stand in as a version stamp for the edge cache.</summary>
    public static SourceTile SourceFor(TileId id) => new(id, SourcePath, 0, SourceVersion, 0);

    // ---- filling ---------------------------------------------------------------------------------

    /// <summary>
    /// Fills a tile's cells from each DEM in turn: the first fills what it has, the next only what is
    /// still missing. Returns the cells filled.
    /// </summary>
    public static long FillAll(IReadOnlyList<ForeignDem> dems, TileId id, ushort[] cells, bool onlyMissing)
    {
        long filled = 0;
        for (int i = 0; i < dems.Count; i++)
            filled += dems[i].Fill(id, cells, onlyMissing || i > 0);
        return filled;
    }

    /// <summary>
    /// Fills a tile's 0.5 m cells (row 0 north, centres at MinE + 0.25 + 0.5c, MaxN - 0.25 - 0.5r) from
    /// this grid; cells with no height stay <see cref="XyzParser.MissingCell"/>. Returns how many
    /// were filled. With <paramref name="onlyMissing"/> only cells still missing are touched.
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
                var (x, y) = DemProjection.FromLv95(Crs, id.MinE + 0.25 + 0.5 * i * Knot, id.MaxN - 0.25 - 0.5 * j * Knot);
                kx[j * Knots + i] = x;
                ky[j * Knots + i] = y;
            }

        long filled = 0;
        var cur = new Cursor();
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

                double z = Sample(x, y, ref cur);
                if (double.IsNaN(z)) continue;
                cells[r * Cells + c] = ChunkFormat.Quantize(z + OffsetZ);
                filled++;
            }
        }
        return filled;
    }

    // ---- sampling --------------------------------------------------------------------------------

    /// <summary>The block a worker read last: most samples land in it again.</summary>
    public struct Cursor
    {
        internal int Entry, Bx, By;
        internal float[]? Data;
        public Cursor() { Entry = -1; }
    }

    /// <summary>
    /// Height at (x, y) in the source grid, bilinear between the four nearest samples, or NaN with
    /// no data; at a data edge, the nearest sample alone. A point near a block's edge reads its
    /// neighbours' samples too.
    /// </summary>
    public double Sample(double x, double y, ref Cursor cur)
    {
        int ei = Find(x, y, cur.Entry);
        if (ei < 0) return double.NaN;
        var e = _entries[ei];
        double gx = Math.Floor((x - e.OriginX) / e.PixelX), gy = Math.Floor((e.OriginY - y) / e.PixelY);
        double fx = (x - e.OriginX) / e.PixelX - gx, fy = (e.OriginY - y) / e.PixelY - gy;
        double x0 = e.OriginX + gx * e.PixelX, y0 = e.OriginY - gy * e.PixelY;
        double h00 = At(x0, y0, ref cur), h10 = At(x0 + e.PixelX, y0, ref cur);
        double h01 = At(x0, y0 - e.PixelY, ref cur), h11 = At(x0 + e.PixelX, y0 - e.PixelY, ref cur);
        if (double.IsNaN(h00) || double.IsNaN(h10) || double.IsNaN(h01) || double.IsNaN(h11))
            return (fx < 0.5, fy < 0.5) switch { (true, true) => h00, (false, true) => h10, (true, false) => h01, _ => h11 };
        return Bilerp(h00, h10, h01, h11, fx, fy);
    }

    /// <summary>The sample whose centre is at (x, y), or NaN.</summary>
    private double At(double x, double y, ref Cursor cur)
    {
        int ei = Find(x, y, cur.Entry);
        if (ei < 0) return double.NaN;
        double h = AtIn(ei, x, y, ref cur);
        if (!double.IsNaN(h) || !_entries[ei].Lazy) return h;
        // the same tile delivered with two départements: the other copy may hold the cell
        foreach (int other in _index[((int)Math.Floor(x / 1000), (int)Math.Floor(y / 1000))])
            if (other != ei && Inside(_entries[other], x, y) && !double.IsNaN(h = AtIn(other, x, y, ref cur))) return h;
        return double.NaN;
    }

    private double AtIn(int ei, double x, double y, ref Cursor cur)
    {
        var e = _entries[ei];
        int col = (int)Math.Round((x - e.OriginX) / e.PixelX);
        int row = (int)Math.Round((e.OriginY - y) / e.PixelY);
        if ((uint)col >= (uint)e.Width || (uint)row >= (uint)e.Height) return double.NaN;
        int bx = col / e.BlockW, by = row / e.BlockH;
        if (cur.Entry != ei || cur.Bx != bx || cur.By != by || cur.Data == null)
        {
            cur.Entry = ei; cur.Bx = bx; cur.By = by;
            cur.Data = Block(ei, bx, by);
        }
        float h = cur.Data[(row - by * e.BlockH) * e.BlockW + (col - bx * e.BlockW)];
        return float.IsNaN(h) || h == e.NoData || h < -1000 || h > 9000 ? double.NaN : h;
    }

    /// <summary>The entry holding (x, y), preferring <paramref name="hint"/>; -1 if none.</summary>
    private int Find(double x, double y, int hint)
    {
        if (hint >= 0 && Inside(_entries[hint], x, y)) return hint;
        if (!_index.TryGetValue(((int)Math.Floor(x / 1000), (int)Math.Floor(y / 1000)), out var list)) return -1;
        foreach (int i in list)
            if (Inside(_entries[i], x, y)) return i;
        return -1;
    }

    private static bool Inside(Entry e, double x, double y) => x >= e.MinX && x < e.MaxX && y > e.MinY && y <= e.MaxY;

    private float[] Block(int ei, int bx, int by)
    {
        var key = (ei, bx, by);
        Lazy<float[]> lazy;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit.Node);
                _lru.AddFirst(hit.Node);
                return hit.Data;
            }
            if (!_loading.TryGetValue(key, out lazy!))
                _loading[key] = lazy = new Lazy<float[]>(() => Decode(_entries[ei], bx, by));
        }
        var data = lazy.Value; // decoded outside the lock, once, however many workers wait for it
        lock (_gate)
        {
            _loading.Remove(key);
            if (!_cache.ContainsKey(key))
            {
                _cache[key] = (data, _lru.AddFirst(key));
                _cachedBytes += data.Length * 4L;
                while (_cachedBytes > CacheBytes && _lru.Count > 1)
                {
                    var old = _lru.Last!.Value;
                    _lru.RemoveLast();
                    _cachedBytes -= _cache[old].Data.Length * 4L;
                    _cache.Remove(old);
                }
            }
        }
        return data;
    }

    private static float[] Decode(Entry e, int bx, int by)
    {
        if (e.Tiff != null) return e.Tiff.ReadBlock(bx, by);
        if (e.Lazy)
        {
            using var t = TiffFloat.Open(e.Name);
            if (t.Width != e.Width || t.Height != e.Height || Math.Abs(t.OriginX - e.OriginX) > 1e-6 || Math.Abs(t.OriginY - e.OriginY) > 1e-6)
            {
                // a tile whose header disagrees with its name is not trusted: no data rather than misplaced data
                Console.Error.WriteLine($"  [WARN] {e.Name}: {t.Width}x{t.Height} at ({t.OriginX}, {t.OriginY}), expected {e.Width}x{e.Height} at ({e.OriginX}, {e.OriginY}); ignored");
                var none = new float[e.Width * e.Height];
                Array.Fill(none, float.NaN);
                return none;
            }
            return t.ReadBlock(bx, by);
        }

        // one XYZ kilometre: "x y z" lines, placed by coordinate, missing points NaN
        var grid = new float[e.Width * e.Height];
        Array.Fill(grid, float.NaN);
        byte[] text;
        using (var zip = ZipFile.OpenRead(e.Zip!))
        using (var s = zip.GetEntry(e.Member!)!.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            text = ms.ToArray();
        }
        var span = text.AsSpan();
        Span<double> v = stackalloc double[3];
        int pos = 0;
        while (pos < span.Length)
        {
            int k = 0;
            while (k < 3 && pos < span.Length)
            {
                while (pos < span.Length && (span[pos] == ' ' || span[pos] == '\t' || span[pos] == '\r' || span[pos] == '\n' || span[pos] == ';' || span[pos] == ',')) pos++;
                if (pos >= span.Length) break;
                if (!Utf8Parser.TryParse(span[pos..], out double d, out int used)) { while (pos < span.Length && span[pos] != '\n') pos++; k = 0; continue; }
                v[k++] = d;
                pos += used;
            }
            if (k < 3) break;
            int col = (int)Math.Round((v[0] - e.OriginX) / e.PixelX), row = (int)Math.Round((e.OriginY - v[1]) / e.PixelY);
            if ((uint)col < (uint)e.Width && (uint)row < (uint)e.Height) grid[row * e.Width + col] = (float)v[2];
        }
        return grid;
    }

    private static double Bilerp(double a, double b, double c, double d, double u, double v) =>
        (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v;
}
