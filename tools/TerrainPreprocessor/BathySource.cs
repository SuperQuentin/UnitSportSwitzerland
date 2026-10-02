using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// swisstopo swissBATHY3D lake-bed surveys (#298), read straight from the downloaded zips.
///
/// <para>
/// swisstopo publishes one STAC item per lake (<c>ch.swisstopo.swissbathy3d</c>, e.g.
/// <c>swissbathy3d_lacleman</c>); its <c>*.esriasciigrid.zip</c> holds one ESRI ASCII grid per
/// kilometre, <c>swissBATHY3D_CHLV95_LN02_E_N.asc</c>, in LV95 / LN02 (the height system of
/// swissALTI3D), the bed's elevation per cell and <c>nodata_value</c> off the survey. Léman is 2 m
/// cells; other lakes are 1–3 m. Loose <c>.asc</c> files under the directory are read too.
/// </para>
///
/// <para>
/// <see cref="SampleBed"/> is a bilinear sample between cell centres, valid only when every cell
/// with a non-zero weight is surveyed, so the survey's edge stays where the survey stopped.
/// Grids are parsed on first use and kept (1 MB per km at 2 m).
/// </para>
/// </summary>
public sealed class BathySource
{
    private static readonly Regex TileName = new(@"_(\d{4})_(\d{4})\.asc$", RegexOptions.IgnoreCase);
    private static readonly Regex LakeName = new(@"swissbathy3d_([a-z]+)_", RegexOptions.IgnoreCase);

    private readonly record struct Entry(string Archive, string? EntryName, string Lake);

    private sealed class Grid
    {
        public required string Lake;
        public required int Cols, Rows;
        public required double XllCorner, YllCorner, CellSize;
        public required float[] Z;   // NaN off the survey; row 0 = north

        public double CellX(int i) => XllCorner + CellSize * (i + 0.5);
        public double CellY(int j) => YllCorner + CellSize * (Rows - j - 0.5);
    }

    private readonly Dictionary<TileId, Entry> _index = new();
    private readonly ConcurrentDictionary<TileId, Lazy<Grid?>> _grids = new();

    public int TileCount => _index.Count;

    public IEnumerable<string> Lakes => _index.Values.Select(e => e.Lake).Distinct().Order();

    public BathySource(string dir)
    {
        foreach (var zip in Directory.EnumerateFiles(dir, "*.zip", SearchOption.AllDirectories))
        {
            string lake = LakeName.Match(Path.GetFileName(zip)) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : Path.GetFileNameWithoutExtension(zip);
            using var archive = ZipFile.OpenRead(zip);
            foreach (var e in archive.Entries)
                if (TileName.Match(e.FullName) is { Success: true } t)
                    _index[new TileId(int.Parse(t.Groups[1].Value), int.Parse(t.Groups[2].Value))] = new Entry(zip, e.FullName, lake);
        }
        foreach (var asc in Directory.EnumerateFiles(dir, "*.asc", SearchOption.AllDirectories))
            if (TileName.Match(Path.GetFileName(asc)) is { Success: true } t)
                _index[new TileId(int.Parse(t.Groups[1].Value), int.Parse(t.Groups[2].Value))] = new Entry(asc, null, "asc");
    }

    public bool Covers(TileId id) => _index.ContainsKey(id);

    /// <summary>The lake a survey kilometre belongs to (the STAC item's name, e.g. "lacleman"), or null.</summary>
    public string? LakeAt(TileId id) => _index.TryGetValue(id, out var e) ? e.Lake : null;

    private Grid? GetGrid(TileId id) =>
        _index.ContainsKey(id) ? _grids.GetOrAdd(id, k => new Lazy<Grid?>(() => Load(_index[k]))).Value : null;

    /// <summary>Bed elevation (LN02 metres) at an LV95 point, or NaN off the survey.</summary>
    public double SampleBed(double e, double n)
    {
        var home = GetGrid(TileId.FromLv95(e, n));
        if (home == null) return double.NaN;

        double cs = home.CellSize;
        // fractional cell index on the lake's grid (cells are aligned across its kilometres)
        double fi = (e - home.XllCorner) / cs - 0.5, fj = (home.YllCorner + home.Rows * cs - n) / cs - 0.5;
        int i0 = (int)Math.Floor(fi), j0 = (int)Math.Floor(fj);
        double fu = fi - i0, fv = fj - j0;

        double sum = 0, w = 0;
        for (int dj = 0; dj <= 1; dj++)
            for (int di = 0; di <= 1; di++)
            {
                double wt = (di == 0 ? 1 - fu : fu) * (dj == 0 ? 1 - fv : fv);
                if (wt <= 1e-9) continue;
                double z = Cell(home, i0 + di, j0 + dj);
                if (double.IsNaN(z)) return double.NaN;
                sum += z * wt;
                w += wt;
            }
        return w > 0 ? sum / w : double.NaN;
    }

    /// <summary>A cell of <paramref name="home"/>'s grid, which may lie in a neighbouring kilometre.</summary>
    private double Cell(Grid home, int i, int j)
    {
        if ((uint)i < (uint)home.Cols && (uint)j < (uint)home.Rows) return home.Z[j * home.Cols + i];
        double x = home.CellX(i), y = home.CellY(j);
        var other = GetGrid(TileId.FromLv95(x, y));
        if (other == null || other.CellSize != home.CellSize) return double.NaN;
        int oi = (int)Math.Floor((x - other.XllCorner) / other.CellSize);
        int oj = (int)Math.Floor((other.YllCorner + other.Rows * other.CellSize - y) / other.CellSize);
        if ((uint)oi >= (uint)other.Cols || (uint)oj >= (uint)other.Rows) return double.NaN;
        return other.Z[oj * other.Cols + oi];
    }

    /// <summary>A cell further than this from the median of its surveyed neighbours is a spike.</summary>
    public const float SpikeM = 3f;

    /// <summary>
    /// Drops single-cell spikes: Léman's survey has isolated cells 8 m below their neighbours along
    /// its edge (2510047,1132676 sits at 359.9 m among 368.0 m), which would read as pits. A cell
    /// more than <see cref="SpikeM"/> from the median of its surveyed 8-neighbours (two at least)
    /// becomes unsurveyed. Judged on the original values, within the kilometre, so it is the same
    /// whichever tile asks.
    /// </summary>
    public static int Despike(float[] z, int cols, int rows)
    {
        var drop = new List<int>();
        Span<float> nb = stackalloc float[8];
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                float v = z[j * cols + i];
                if (float.IsNaN(v)) continue;
                int n = 0;
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int x = i + di, y = j + dj;
                        if ((uint)x >= (uint)cols || (uint)y >= (uint)rows) continue;
                        float u = z[y * cols + x];
                        if (!float.IsNaN(u)) nb[n++] = u;
                    }
                if (n < 2) continue;
                var s = nb[..n];
                s.Sort();
                float median = n % 2 == 1 ? s[n / 2] : 0.5f * (s[n / 2 - 1] + s[n / 2]);
                if (MathF.Abs(v - median) > SpikeM) drop.Add(j * cols + i);
            }
        foreach (int i in drop) z[i] = float.NaN;
        return drop.Count;
    }

    private static Grid? Load(Entry entry)
    {
        using var archive = entry.EntryName != null ? ZipFile.OpenRead(entry.Archive) : null;
        using var stream = archive != null ? archive.GetEntry(entry.EntryName!)!.Open() : File.OpenRead(entry.Archive);
        using var reader = new StreamReader(stream);

        int cols = 0, rows = 0;
        double xll = 0, yll = 0, cs = 0, nodata = -9999;
        bool center = false;
        string? line;
        // header: "key value" lines until the first numeric row
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !char.IsLetter(parts[0][0])) break;
            double v = double.Parse(parts[1], CultureInfo.InvariantCulture);
            switch (parts[0].ToLowerInvariant())
            {
                case "ncols": cols = (int)v; break;
                case "nrows": rows = (int)v; break;
                case "xllcorner": xll = v; break;
                case "yllcorner": yll = v; break;
                case "xllcenter": xll = v; center = true; break;
                case "yllcenter": yll = v; center = true; break;
                case "cellsize": cs = v; break;
                case "nodata_value": nodata = v; break;
            }
        }
        if (cols <= 0 || rows <= 0 || cs <= 0) throw new InvalidDataException($"Bad ASCII grid header in {entry.EntryName ?? entry.Archive}");
        if (center) { xll -= cs / 2; yll -= cs / 2; }

        var z = new float[cols * rows];
        int k = 0;
        while (line != null && k < z.Length)
        {
            foreach (var tok in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (k >= z.Length) break;
                double v = double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture);
                z[k++] = Math.Abs(v - nodata) < 1e-6 ? float.NaN : (float)v;
            }
            line = reader.ReadLine();
        }
        if (k != z.Length) throw new InvalidDataException($"{entry.EntryName ?? entry.Archive}: {k} of {z.Length} cells");
        Despike(z, cols, rows);
        return new Grid { Lake = entry.Lake, Cols = cols, Rows = rows, XllCorner = xll, YllCorner = yll, CellSize = cs, Z = z };
    }
}
