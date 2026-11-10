using System.Globalization;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// swissTLM3D's lakes for the globe's Swiss levels (#1678, <c>--cube-swiss --tlm</c>): past the leaves ETOPO's 1.8 km
/// cells smear a steep shore far into the lake (east of Lavaux, 1.5-2 km of ground 3-35 m over Léman), so a vertex inside
/// one of TLM's big standing waters stands at the leaves' level of that lake, and the ground within <see cref="ShoreM"/>
/// of it comes down to that level. Rasterised once onto an LV95 grid (<see cref="CellM"/>) over the leaves' box and a
/// margin; each lake's level is the leaves' lake nearest ETOPO's median over it, so a pond on the plateau is never
/// taken for Léman. Read-only after it is built: thread-safe.
/// </summary>
public sealed class SwissLakeMask
{
    /// <summary>The mask's cell, metres.</summary>
    public const double CellM = 10;

    /// <summary>The shore distances' cell, metres.</summary>
    public const double ShoreCellM = 20;

    /// <summary>Over this distance from a lake the ground comes down to its level, metres (ETOPO stands it tens of metres up there).</summary>
    public const double ShoreM = 300;

    /// <summary>A standing water smaller than this is left to ETOPO (its cells cannot hold it anyway), m².</summary>
    public const double MinAreaM2 = 2e6;

    /// <summary>How far ETOPO's median over a lake may stand from the leaves' level of it (Léman: 418.3 against 422.2).</summary>
    public const double LevelMatchM = 10;

    private readonly double _minE, _minN;
    private readonly int _w, _h, _sw, _sh;
    // the lake level index + 1 of each cell (0: dry)
    private readonly byte[] _wet;
    // how much of the cells round each is lake, 0..255: the shore taken at its half, rounder than the cells' staircase
    private readonly byte[] _wetness;
    // the distance to the nearest lake cell, metres, and that lake's level index + 1 (0: none within the margin)
    private readonly float[] _shore;
    private readonly byte[] _shoreLake;
    private readonly float[] _levels;

    public int Lakes { get; }

    private SwissLakeMask(double minE, double minN, int w, int h, byte[] wet, float[] levels, int lakes)
    {
        _minE = minE;
        _minN = minN;
        _w = w;
        _h = h;
        _wet = wet;
        _wetness = Smoothed(wet, w, h);
        _levels = levels;
        Lakes = lakes;
        // the shore distances on a coarser grid: each cell the nearest lake cell's distance and its lake
        int f = (int)(ShoreCellM / CellM);
        _sw = w / f + 1;
        _sh = h / f + 1;
        var sites = new bool[_sw * _sh];
        var siteLake = new byte[_sw * _sh];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (wet[y * w + x] is var l && l != 0)
                {
                    int k = (y / f) * _sw + x / f;
                    sites[k] = true;
                    siteLake[k] = l;
                }
        var dist = new float[_sw * _sh];
        var site = new int[_sw * _sh];
        DistanceTransform.Run(sites, _sw, _sh, dist, site);
        _shore = new float[_sw * _sh];
        _shoreLake = new byte[_sw * _sh];
        for (int k = 0; k < dist.Length; k++)
        {
            _shore[k] = (float)(dist[k] * ShoreCellM);
            _shoreLake[k] = site[k] >= 0 ? siteLake[site[k]] : (byte)0;
        }
    }

    /// <summary>
    /// TLM's standing waters over an LV95 box, each matched to one of the leaves' lake levels by ETOPO's median over it
    /// (<paramref name="etopoAt"/>, at a latitude and longitude); null where none matches.
    /// </summary>
    public static SwissLakeMask? Build(string gpkg, double minE, double minN, double maxE, double maxN, float[] lakeLevels,
        Func<double, double, double> etopoAt)
    {
        if (lakeLevels.Length == 0) return null;
        int w = (int)Math.Ceiling((maxE - minE) / CellM) + 1, h = (int)Math.Ceiling((maxN - minN) / CellM) + 1;
        var wet = new byte[w * h];
        int matched = 0, small = 0, unmatched = 0;
        using var conn = GeoPackageReader.Open(gpkg);
        var region = new Box(minE, minN, maxE, maxN);
        var cells = new List<int>();
        foreach (var row in GeoPackageReader.TileRows(conn, "tlm_bb_bodenbedeckung", new[] { "objektart" }, region))
        {
            if (row.IsDBNull(0) || row.IsDBNull(1) || row.GetString(0) != "Stehende Gewaesser") continue;
            var rings = GeoPackageReader.ParsePolygons((byte[])row.GetValue(1));
            if (rings.Count == 0) continue;
            cells.Clear();
            Fill(rings, minE, minN, w, h, cells);
            if (cells.Count * CellM * CellM < MinAreaM2) { small++; continue; }
            // ETOPO's median over the lake (every so many cells): its surface a few metres off the survey
            var samples = new List<double>();
            int every = Math.Max(1, cells.Count / 4000);
            for (int c = 0; c < cells.Count; c += every)
            {
                double e = minE + (cells[c] % w) * CellM, n = minN + (cells[c] / w) * CellM;
                var (lat, lon) = Lv95Source.Instance.ToLatLon(e, n);
                samples.Add(etopoAt(lat, lon));
            }
            samples.Sort();
            double median = samples[samples.Count / 2];
            int best = -1;
            for (int l = 0; l < lakeLevels.Length; l++)
                if (Math.Abs(lakeLevels[l] - median) <= LevelMatchM && (best < 0 || Math.Abs(lakeLevels[l] - median) < Math.Abs(lakeLevels[best] - median)))
                    best = l;
            if (best < 0)
            {
                unmatched++;
                continue;
            }
            foreach (int c in cells) wet[c] = (byte)(best + 1);
            matched++;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"cube-swiss: TLM lake of {cells.Count * CellM * CellM / 1e6:F1} km² (ETOPO's median {median:F1} m) at {lakeLevels[best]:F1} m"));
        }
        Console.WriteLine($"cube-swiss: TLM lakes: {matched} matched to the leaves' levels, {unmatched} at another level, {small} too small");
        return matched == 0 ? null : new SwissLakeMask(minE, minN, w, h, wet, lakeLevels, matched);
    }

    /// <summary>Radius of the box the shore is smoothed over, cells (twice, separably: ~50 m across).</summary>
    private const int SmoothCells = 2;

    /// <summary>The share of lake round each cell (0..255), two separable box passes of <see cref="SmoothCells"/>.</summary>
    private static byte[] Smoothed(byte[] wet, int w, int h)
    {
        var a = new float[wet.Length];
        for (int k = 0; k < a.Length; k++) a[k] = wet[k] != 0 ? 1 : 0;
        var b = new float[a.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            Box1(a, b, w, h, 1, 0);
            Box1(b, a, w, h, 0, 1);
        }
        var o = new byte[a.Length];
        for (int k = 0; k < o.Length; k++) o[k] = (byte)Math.Clamp((int)Math.Round(a[k] * 255), 0, 255);
        return o;
    }

    private static void Box1(float[] src, float[] dst, int w, int h, int dx, int dy) =>
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int r = -SmoothCells; r <= SmoothCells; r++)
                {
                    int xx = Math.Clamp(x + r * dx, 0, w - 1), yy = Math.Clamp(y + r * dy, 0, h - 1);
                    sum += src[yy * w + xx];
                }
                dst[y * w + x] = sum / (2 * SmoothCells + 1);
            }
        });

    /// <summary>Even-odd scanline fill of a polygon's rings (holes are islands) at the cells' centres.</summary>
    private static void Fill(List<GeoPackageReader.Ring> rings, double minE, double minN, int w, int h, List<int> into)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var r in rings)
            for (int i = 0; i < r.Count; i++)
            {
                lo = Math.Min(lo, r.Xyz[i * 3 + 1]);
                hi = Math.Max(hi, r.Xyz[i * 3 + 1]);
            }
        int y0 = Math.Max(0, (int)Math.Ceiling((lo - minN) / CellM)), y1 = Math.Min(h - 1, (int)Math.Floor((hi - minN) / CellM));
        var xs = new List<double>();
        for (int y = y0; y <= y1; y++)
        {
            double n = minN + y * CellM;
            xs.Clear();
            foreach (var r in rings)
                for (int i = 0; i + 1 < r.Count; i++)
                {
                    double ya = r.Xyz[i * 3 + 1], yb = r.Xyz[(i + 1) * 3 + 1];
                    if (ya == yb || n < Math.Min(ya, yb) || n >= Math.Max(ya, yb)) continue;
                    double xa = r.Xyz[i * 3], xb = r.Xyz[(i + 1) * 3];
                    xs.Add(xa + (n - ya) / (yb - ya) * (xb - xa));
                }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Math.Max(0, (int)Math.Ceiling((xs[k] - minE) / CellM)), x1 = Math.Min(w - 1, (int)Math.Floor((xs[k + 1] - minE) / CellM));
                for (int x = x0; x <= x1; x++) into.Add(y * w + x);
            }
        }
    }

    /// <summary>
    /// ETOPO's height <paramref name="etopo"/> at a latitude and longitude as the mask has it: a lake's level inside one
    /// (<paramref name="snapped"/>), brought down towards it within <see cref="ShoreM"/> of one, else as it is.
    /// </summary>
    public float Apply(double latDeg, double lonDeg, float etopo, out bool snapped)
    {
        snapped = false;
        var (e, n) = Lv95Source.Instance.FromLatLon(latDeg, lonDeg);
        double fx = (e - _minE) / CellM, fy = (n - _minN) / CellM;
        if (fx < 0 || fy < 0 || fx > _w - 1 || fy > _h - 1) return etopo;
        // wet where the bilinear of the four cells' wetness is past a half: a shore smoother than the cells
        int x0 = Math.Min((int)fx, _w - 2), y0 = Math.Min((int)fy, _h - 2);
        double u = fx - x0, v = fy - y0;
        int k = y0 * _w + x0;
        double wetness = ((1 - v) * ((1 - u) * _wetness[k] + u * _wetness[k + 1]) + v * ((1 - u) * _wetness[k + _w] + u * _wetness[k + _w + 1])) / 255;
        // the shore: the distance bilinear on its grid, the level of the nearest lake
        double sx = (e - _minE) / ShoreCellM, sy = (n - _minN) / ShoreCellM;
        int s0 = Math.Clamp((int)sx, 0, _sw - 2), t0 = Math.Clamp((int)sy, 0, _sh - 2);
        double su = Math.Clamp(sx - s0, 0, 1), sv = Math.Clamp(sy - t0, 0, 1);
        int q = t0 * _sw + s0;
        byte near = _shoreLake[q];
        if (near == 0) return etopo;
        if (wetness >= 0.5)
        {
            snapped = true;
            return _levels[near - 1];
        }
        double dist = (1 - sv) * ((1 - su) * _shore[q] + su * _shore[q + 1]) + sv * ((1 - su) * _shore[q + _sw] + su * _shore[q + _sw + 1]);
        if (dist >= ShoreM) return etopo;
        double t = Math.Clamp(dist / ShoreM, 0, 1), s = t * t * (3 - 2 * t);
        float level = _levels[near - 1];
        return (float)(level + (etopo - level) * s);
    }

    private sealed class Box(double minE, double minN, double maxE, double maxN) : ISourceRegion
    {
        public double MinE => minE;
        public double MinN => minN;
        public double MaxE => maxE;
        public double MaxN => maxN;
        public int Count => 1;
        public bool Touches(double x0, double x1, double y0, double y1) => x1 >= minE && x0 <= maxE && y1 >= minN && y0 <= maxN;
        public bool Touches(double x, double y) => Touches(x, x, y, y);
    }
}
