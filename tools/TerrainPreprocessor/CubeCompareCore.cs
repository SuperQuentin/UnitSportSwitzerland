using System.Globalization;

namespace UnitSport.Tools.Preprocessor;

// The pure half of --cube-compare (#1507, G2 B3 PR 0): statistics, matching and the bars, in a local
// Cartesian frame (x east, y up, z south, metres: a leaf's anchor frame), with no file, projection or
// tile in sight. CubeCompare.cs reads both builds and puts their records here. Unit-tested
// (CubeCompareTests), linked into the test project by path.

/// <summary>A sample of differences: how many, the signed mean, and the percentiles and maximum of their size.</summary>
public readonly record struct Spread(long Count, double Mean, double P50, double P95, double P99, double Max)
{
    /// <summary>Sorts <paramref name="values"/> by size in place. Nearest-rank percentiles; an empty sample is all zero.</summary>
    public static Spread Of(List<double> values)
    {
        if (values.Count == 0) return default;
        double sum = 0;
        foreach (double v in values) sum += v;
        values.Sort((a, b) => Math.Abs(a).CompareTo(Math.Abs(b)));
        double At(double p) => Math.Abs(values[Math.Clamp((int)Math.Ceiling(p * values.Count) - 1, 0, values.Count - 1)]);
        return new(values.Count, sum / values.Count, At(0.50), At(0.95), At(0.99), Math.Abs(values[^1]));
    }

    /// <summary>A signed number to two decimals, its sign always shown (and none on a zero).</summary>
    public static string Signed(double v)
    {
        string a = Math.Abs(v).ToString("F2", CultureInfo.InvariantCulture);
        return a == "0.00" ? a : (v < 0 ? "-" : "+") + a;
    }

    public double Get(Stat s) => s switch
    {
        Stat.Mean => Math.Abs(Mean), Stat.P50 => P50, Stat.P95 => P95, Stat.P99 => P99, _ => Max,
    };

    /// <summary>The printed line: <c>n 1234, mean +0.3, p50 1.2, p95 3.4, p99 5.6, max 9.9 cm</c>.</summary>
    public string Format(double scale, string unit) => string.Create(CultureInfo.InvariantCulture,
        $"n {Count}, mean {Signed(Mean * scale)}, p50 {P50 * scale:F2}, p95 {P95 * scale:F2}, p99 {P99 * scale:F2}, max {Max * scale:F2} {unit}");
}

/// <summary>
/// A sample too large to keep (every vertex of a region): sizes binned (0.05 mm to 10 cm, then 1 mm to 10 m,
/// then 10 cm), the signed sum and the exact maximum. A percentile is its bin's upper edge, never above the maximum.
/// </summary>
public sealed class Tally
{
    private const int FineBins = 2000, MidBins = 9900, CoarseBins = 9900;
    private const double Fine = 0.00005, Mid = 0.001, Coarse = 0.1;
    private readonly long[] _bins = new long[FineBins + MidBins + CoarseBins + 1];
    private double _sum, _max;
    private long _n;

    public long Count => _n;

    public void Add(double v)
    {
        double a = Math.Abs(v);
        int bin = a < FineBins * Fine ? (int)(a / Fine)
            : a < FineBins * Fine + MidBins * Mid ? FineBins + (int)((a - FineBins * Fine) / Mid)
            : Math.Min(FineBins + MidBins + (int)((a - FineBins * Fine - MidBins * Mid) / Coarse), _bins.Length - 1);
        _bins[bin]++;
        _sum += v;
        _max = Math.Max(_max, a);
        _n++;
    }

    public void Merge(Tally o)
    {
        for (int i = 0; i < _bins.Length; i++) _bins[i] += o._bins[i];
        _sum += o._sum;
        _max = Math.Max(_max, o._max);
        _n += o._n;
    }

    private static double Upper(int bin) => bin < FineBins ? (bin + 1) * Fine
        : bin < FineBins + MidBins ? FineBins * Fine + (bin - FineBins + 1) * Mid
        : FineBins * Fine + MidBins * Mid + (bin - FineBins - MidBins + 1) * Coarse;

    public Spread Spread()
    {
        if (_n == 0) return default;
        double At(double p)
        {
            long rank = Math.Max(1, (long)Math.Ceiling(p * _n)), seen = 0;
            for (int i = 0; i < _bins.Length; i++)
                if ((seen += _bins[i]) >= rank) return Math.Min(Upper(i), _max);
            return _max;
        }
        return new Spread(_n, _sum / _n, At(0.50), At(0.95), At(0.99), _max);
    }
}

public enum Stat { Mean, P50, P95, P99, Max }

/// <summary>A limit on one statistic of a <see cref="Spread"/> (the mean's size for <see cref="Stat.Mean"/>), metres.</summary>
public readonly record struct Bar(Stat Stat, double Limit);

/// <summary>What one kind's comparison printed and which of its bars it broke; a kind with no data on one side is skipped.</summary>
public sealed class KindReport(string kind)
{
    public string Kind { get; } = kind;
    public List<string> Lines { get; } = new();
    public List<string> Breaches { get; } = new();
    public string? Skipped { get; private set; }

    public KindReport Skip(string why) { Skipped = why; return this; }

    public void Note(string line) => Lines.Add(line);

    /// <summary>A sample against its bars, printed in centimetres (or metres when <paramref name="cm"/> is false).</summary>
    public void Check(string what, Spread s, bool cm, params Bar[] bars)
    {
        double scale = cm ? 100 : 1;
        string unit = cm ? "cm" : "m";
        var broken = bars.Where(b => s.Count > 0 && s.Get(b.Stat) > b.Limit).ToList();
        string limits = bars.Length == 0 ? "" : "  [bar " + string.Join(", ", bars.Select(b =>
            string.Create(CultureInfo.InvariantCulture, $"{Name(b.Stat)} {b.Limit * scale:G4}"))) + "]";
        Lines.Add($"{what}: {s.Format(scale, unit)}{limits}");
        foreach (var b in broken)
            Breaches.Add(string.Create(CultureInfo.InvariantCulture,
                $"{Kind} {what}: {Name(b.Stat)} {s.Get(b.Stat) * scale:F2} {unit} > {b.Limit * scale:G4}"));
    }

    /// <summary>A share or a count against a floor.</summary>
    public void AtLeast(string what, double value, double min, string format = "P3")
    {
        Lines.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: {value.ToString(format, CultureInfo.InvariantCulture)}  [bar >= {min.ToString(format, CultureInfo.InvariantCulture)}]"));
        if (value < min) Breaches.Add(string.Create(CultureInfo.InvariantCulture, $"{Kind} {what}: {value.ToString(format, CultureInfo.InvariantCulture)} < {min.ToString(format, CultureInfo.InvariantCulture)}"));
    }

    /// <summary>A share or a count against a ceiling.</summary>
    public void AtMost(string what, double value, double max, string format = "P3")
    {
        Lines.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: {value.ToString(format, CultureInfo.InvariantCulture)}  [bar <= {max.ToString(format, CultureInfo.InvariantCulture)}]"));
        if (value > max) Breaches.Add(string.Create(CultureInfo.InvariantCulture, $"{Kind} {what}: {value.ToString(format, CultureInfo.InvariantCulture)} > {max.ToString(format, CultureInfo.InvariantCulture)}"));
    }

    private static string Name(Stat s) => s switch { Stat.Mean => "|mean|", _ => s.ToString().ToLowerInvariant() };
}

/// <summary>
/// The bars of docs/plans/global-tiling-g2.md's B3 section, per kind: 2 cm on source vertices, plus
/// 2e-4 of the distance along a line for what is placed by distance (LV95 grid lengths are 80-190 ppm
/// long); categories ≥ 99.5 % per class; scatter density ±1 %. Heights against <c>.terr</c> cannot meet
/// 2 cm: the LV95 tiles hold 7.2 cm quanta on a 1 m grid averaged from the 0.5 m source, the leaves the
/// source itself on a ~1.2 m lattice, so the bar there is on the bias and the fitted horizontal shift
/// (what a frame error moves), and on the median, with the tails only printed.
/// </summary>
public static class CompareBars
{
    public const double Vertex = 0.02;
    public const double PerDistance = 2e-4;
    public const double ClassAgreement = 0.995;
    public const double Density = 0.01;
    /// <summary>A class needs this many samples before its agreement is held to the bar.</summary>
    public const long MinClassSamples = 1000;

    /// <summary>Heights: |mean| and the fitted horizontal shift within 2 cm, the median within the LV95 quantum (7.2 cm).</summary>
    public const double HeightBias = 0.02, HeightShift = 0.02, HeightMedian = 0.072;

    /// <summary>A lattice kind's features (holes) within a lattice quad's diagonal of each other: 1.2 m quads against 1 m.</summary>
    public const double Quad = 2.0;

    /// <summary>Water levels: the LV95 quantum's half (3.6 cm) plus the leaf's 1 cm, as p99.</summary>
    public const double WaterLevel = 0.05;
}

/// <summary>A point record in a local frame: an id when the source has one (0 none), a class, and its height above its own ground (NaN when not draped).</summary>
public readonly record struct Feature(double X, double Y, double Z, long Id = 0, int Class = 0, double Above = double.NaN);

/// <summary>Matched pairs (<c>ById</c> when the id did it) and what each side kept alone.</summary>
public sealed record Matches(List<(int A, int B, bool ById)> Pairs, List<int> OnlyA, List<int> OnlyB);

/// <summary>Buckets points by their plan position (x, z) for neighbour searches.</summary>
public sealed class PlaneHash
{
    private readonly Dictionary<(int, int), List<int>> _cells = new();
    private readonly double _cell;

    public PlaneHash(double cell) => _cell = cell;

    public (int, int) Cell(double x, double z) => ((int)Math.Floor(x / _cell), (int)Math.Floor(z / _cell));

    public void Add(int index, double x, double z)
    {
        var c = Cell(x, z);
        if (!_cells.TryGetValue(c, out var list)) _cells[c] = list = new List<int>(4);
        list.Add(index);
    }

    /// <summary>Every index whose cell is within <paramref name="radius"/> of (x, z) (a superset: the caller measures).</summary>
    public IEnumerable<int> Near(double x, double z, double radius)
    {
        var (c0, r0) = Cell(x - radius, z - radius);
        var (c1, r1) = Cell(x + radius, z + radius);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (_cells.TryGetValue((c, r), out var list))
                    foreach (int i in list) yield return i;
    }
}

public static class FeatureMatch
{
    /// <summary>
    /// Pairs <paramref name="a"/> and <paramref name="b"/>: first by id where an id is on both sides exactly
    /// once, then the rest by mutual nearest neighbour in plan within <paramref name="radius"/> (of the same
    /// class when <paramref name="byClass"/>). What is left is each side's alone.
    /// </summary>
    public static Matches Match(IReadOnlyList<Feature> a, IReadOnlyList<Feature> b, double radius, bool byClass = true)
    {
        var pairs = new List<(int, int, bool)>();
        var usedA = new bool[a.Count];
        var usedB = new bool[b.Count];
        var idsA = UniqueIds(a);
        var idsB = UniqueIds(b);
        foreach (var (id, ia) in idsA)
            if (idsB.TryGetValue(id, out int ib)) { pairs.Add((ia, ib, true)); usedA[ia] = usedB[ib] = true; }

        var hashA = new PlaneHash(Math.Max(radius, 1e-3));
        var hashB = new PlaneHash(Math.Max(radius, 1e-3));
        for (int i = 0; i < a.Count; i++) if (!usedA[i]) hashA.Add(i, a[i].X, a[i].Z);
        for (int i = 0; i < b.Count; i++) if (!usedB[i]) hashB.Add(i, b[i].X, b[i].Z);
        int Nearest(Feature f, IReadOnlyList<Feature> other, PlaneHash hash, bool[] used)
        {
            int best = -1;
            double bestD = radius * radius;
            foreach (int k in hash.Near(f.X, f.Z, radius))
            {
                if (used[k] || (byClass && other[k].Class != f.Class)) continue;
                double dx = other[k].X - f.X, dz = other[k].Z - f.Z, d = dx * dx + dz * dz;
                if (d <= bestD && (d < bestD || best < 0 || k < best)) { bestD = d; best = k; }
            }
            return best;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (usedA[i]) continue;
            int j = Nearest(a[i], b, hashB, usedB);
            if (j < 0 || Nearest(b[j], a, hashA, usedA) != i) continue;
            pairs.Add((i, j, false));
            usedA[i] = usedB[j] = true;
        }
        var onlyA = Enumerable.Range(0, a.Count).Where(i => !usedA[i]).ToList();
        var onlyB = Enumerable.Range(0, b.Count).Where(i => !usedB[i]).ToList();
        return new Matches(pairs, onlyA, onlyB);
    }

    private static Dictionary<long, int> UniqueIds(IReadOnlyList<Feature> f)
    {
        var seen = new Dictionary<long, int>();
        var twice = new HashSet<long>();
        for (int i = 0; i < f.Count; i++)
        {
            if (f[i].Id == 0) continue;
            if (!seen.TryAdd(f[i].Id, i)) twice.Add(f[i].Id);
        }
        foreach (long id in twice) seen.Remove(id);
        return seen;
    }

    /// <summary>
    /// How far <paramref name="b"/> is from <paramref name="a"/>: in plan, and vertically (b − a) as height
    /// above each one's own ground when both are draped, as absolute heights otherwise.
    /// </summary>
    public static (double H, double V) Offset(Feature a, Feature b)
    {
        double dx = b.X - a.X, dz = b.Z - a.Z;
        double v = double.IsNaN(a.Above) || double.IsNaN(b.Above) ? b.Y - a.Y : b.Above - a.Above;
        return (Math.Sqrt(dx * dx + dz * dz), v);
    }
}

/// <summary>A polyline in a local frame (x, y, z triples), its class, and its points' heights above their own ground (null when not draped).</summary>
public sealed record Line(double[] Points, int Class, double[]? Above = null)
{
    public int Count => Points.Length / 3;
}

/// <summary>The nearest line to a point, in plan, among lines of its class: for vertices against the other build's lines.</summary>
public sealed class LineIndex
{
    private readonly IReadOnlyList<Line> _lines;
    private readonly PlaneHash _hash;
    private readonly List<(int Line, int Seg)> _segs = new();
    private readonly double _cell;

    public LineIndex(IReadOnlyList<Line> lines, double cell = 8)
    {
        _lines = lines;
        _cell = cell;
        _hash = new PlaneHash(cell);
        for (int l = 0; l < lines.Count; l++)
        {
            var p = lines[l].Points;
            for (int s = 0; s + 1 < lines[l].Count; s++)
            {
                // a segment goes into every cell its plan box touches (they are short: draped lines are densified)
                double x0 = Math.Min(p[s * 3], p[s * 3 + 3]), x1 = Math.Max(p[s * 3], p[s * 3 + 3]);
                double z0 = Math.Min(p[s * 3 + 2], p[s * 3 + 5]), z1 = Math.Max(p[s * 3 + 2], p[s * 3 + 5]);
                int id = _segs.Count;
                _segs.Add((l, s));
                var (c0, r0) = _hash.Cell(x0, z0);
                var (c1, r1) = _hash.Cell(x1, z1);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                        _hash.Add(id, (c + 0.5) * cell, (r + 0.5) * cell);
            }
        }
    }

    /// <summary>
    /// The closest segment of class <paramref name="cls"/> within <paramref name="maxR"/> in plan: the plan
    /// distance, the point's height minus the line's there, and the same for heights above ground (NaN
    /// when either is not draped). False when none is that near.
    /// </summary>
    public bool Nearest(double x, double y, double z, double above, int cls, double maxR, out double h, out double dy, out double dAbove)
    {
        h = dy = dAbove = double.NaN;
        double best = double.PositiveInfinity;
        // a segment sits in every cell its plan box touches, so one may come twice: the minimum does not mind
        foreach (int id in _hash.Near(x, z, maxR))
        {
            var (l, s) = _segs[id];
            var line = _lines[l];
            if (line.Class != cls) continue;
            var p = line.Points;
            double ax = p[s * 3], ay = p[s * 3 + 1], az = p[s * 3 + 2], bx = p[s * 3 + 3], by = p[s * 3 + 4], bz = p[s * 3 + 5];
            double ex = bx - ax, ez = bz - az, len2 = ex * ex + ez * ez;
            double t = len2 > 0 ? Math.Clamp(((x - ax) * ex + (z - az) * ez) / len2, 0, 1) : 0;
            double cx = ax + ex * t - x, cz = az + ez * t - z, d = Math.Sqrt(cx * cx + cz * cz);
            if (d > maxR || d >= best) continue;
            best = d;
            h = d;
            dy = y - (ay + (by - ay) * t);
            dAbove = line.Above is { } ab && !double.IsNaN(above) ? above - (ab[s] + (ab[s + 1] - ab[s]) * t) : double.NaN;
        }
        return !double.IsPositiveInfinity(best);
    }

    /// <summary>A line's length (3D) over the edges whose midpoint <paramref name="keep"/> takes (x, y, z).</summary>
    public static double Length(Line line, Func<double, double, double, bool>? keep = null)
    {
        var p = line.Points;
        double sum = 0;
        for (int s = 0; s + 1 < line.Count; s++)
        {
            // in plan (x, z of the frame): the drape's ups and downs follow each build's own ground (B3 PR 2: streams in gullies 0.2 % apart in 3D)
            double ax = p[s * 3], ay = p[s * 3 + 1], az = p[s * 3 + 2], bx = p[s * 3 + 3], by = p[s * 3 + 4], bz = p[s * 3 + 5];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            if (keep == null) { sum += len; continue; }
            bool ka = keep(ax, ay, az), kb = keep(bx, by, bz);
            if (len > 4.5)
            {
                // a long edge (a cable's span) may leave the kept ground and come back: in metre steps, each cut where it changes
                int n = (int)Math.Ceiling(len);
                for (int q = 0; q < n; q++)
                {
                    double t0 = (double)q / n, t1 = (double)(q + 1) / n;
                    bool k0 = q == 0 ? ka : keep(ax + (bx - ax) * t0, ay + (by - ay) * t0, az + (bz - az) * t0);
                    bool k1 = q == n - 1 ? kb : keep(ax + (bx - ax) * t1, ay + (by - ay) * t1, az + (bz - az) * t1);
                    sum += len / n * KeptFraction(k0, k1, t => keep(ax + (bx - ax) * (t0 + (t1 - t0) * t), ay + (by - ay) * (t0 + (t1 - t0) * t), az + (bz - az) * (t0 + (t1 - t0) * t)));
                }
            }
            else if (ka && kb) sum += len;
            else if (ka != kb)
            {
                // an edge across the filter's boundary: cut where it changes (bisection), so the part kept counts and no more
                double lo = 0, hi = 1;
                for (int it = 0; it < 30; it++)
                {
                    double t = 0.5 * (lo + hi);
                    if (keep(ax + (bx - ax) * t, ay + (by - ay) * t, az + (bz - az) * t) == ka) lo = t; else hi = t;
                }
                double cut = 0.5 * (lo + hi);
                sum += len * (ka ? cut : 1 - cut);
            }
        }
        return sum;
    }

    /// <summary>The kept share of an edge whose ends are kept or not: 1, 0, or where it changes (bisection).</summary>
    private static double KeptFraction(bool ka, bool kb, Func<double, bool> keepAt)
    {
        if (ka == kb) return ka ? 1 : 0;
        double lo = 0, hi = 1;
        for (int it = 0; it < 30; it++)
        {
            double t = 0.5 * (lo + hi);
            if (keepAt(t) == ka) lo = t; else hi = t;
        }
        double cut = 0.5 * (lo + hi);
        return ka ? cut : 1 - cut;
    }
}

/// <summary>Categories sampled at the same places in both builds: how often the cube build agrees, per class of the LV95 one.</summary>
public sealed class Agreement
{
    private readonly Dictionary<int, (long Count, long Agree)> _byClass = new();

    public void Add(int expected, int got)
    {
        var (c, a) = _byClass.GetValueOrDefault(expected);
        _byClass[expected] = (c + 1, a + (expected == got ? 1 : 0));
    }

    public void Merge(Agreement other)
    {
        foreach (var (k, (c, a)) in other._byClass)
        {
            var (c0, a0) = _byClass.GetValueOrDefault(k);
            _byClass[k] = (c0 + c, a0 + a);
        }
    }

    public long Count => _byClass.Values.Sum(v => v.Count);

    public double Overall => Count == 0 ? 1 : (double)_byClass.Values.Sum(v => v.Agree) / Count;

    public IEnumerable<(int Class, long Count, double Share)> PerClass() =>
        _byClass.OrderBy(e => e.Key).Select(e => (e.Key, e.Value.Count, (double)e.Value.Agree / e.Value.Count));
}

/// <summary>
/// The horizontal shift and vertical bias that best explain height differences on slopes: if the cube
/// surface is the LV95 one moved by s, then dh = cube − lv95 ≈ c − ∇h·s. Least squares over (c, sE, sN);
/// a frame or projection error shows here as centimetres where the raw differences are noise.
/// </summary>
public sealed class ShiftFit
{
    // the normal equations' sums, regressors (1, −gE, −gN)
    private double _s11, _s12, _s13, _s22, _s23, _s33, _t1, _t2, _t3;
    private long _n;

    public void Add(double dh, double gEast, double gNorth)
    {
        double a = -gEast, b = -gNorth;
        _s11 += 1; _s12 += a; _s13 += b; _s22 += a * a; _s23 += a * b; _s33 += b * b;
        _t1 += dh; _t2 += a * dh; _t3 += b * dh;
        _n++;
    }

    public void Merge(ShiftFit o)
    {
        _s11 += o._s11; _s12 += o._s12; _s13 += o._s13; _s22 += o._s22; _s23 += o._s23; _s33 += o._s33;
        _t1 += o._t1; _t2 += o._t2; _t3 += o._t3; _n += o._n;
    }

    public long Count => _n;

    /// <summary>The shift east and north (metres) and the bias; zeros when the slopes cannot tell (flat or too few).</summary>
    public (double East, double North, double Bias) Solve()
    {
        // Cramer's rule on the symmetric 3x3
        double det = _s11 * (_s22 * _s33 - _s23 * _s23) - _s12 * (_s12 * _s33 - _s23 * _s13) + _s13 * (_s12 * _s23 - _s22 * _s13);
        if (_n < 3 || Math.Abs(det) < 1e-12 * Math.Max(1, _s11 * _s22 * _s33)) return (0, 0, _n > 0 ? _t1 / _s11 : 0);
        double c = (_t1 * (_s22 * _s33 - _s23 * _s23) - _s12 * (_t2 * _s33 - _s23 * _t3) + _s13 * (_t2 * _s23 - _s22 * _t3)) / det;
        double e = (_s11 * (_t2 * _s33 - _s23 * _t3) - _t1 * (_s12 * _s33 - _s23 * _s13) + _s13 * (_s12 * _t3 - _t2 * _s13)) / det;
        double n = (_s11 * (_s22 * _t3 - _t2 * _s23) - _s12 * (_s12 * _t3 - _t2 * _s13) + _t1 * (_s12 * _s23 - _s22 * _s13)) / det;
        return (e, n, c);
    }
}

/// <summary>Counts of a thing in both builds over the same ground, as a relative difference.</summary>
public static class Density
{
    public static double Relative(long lv95, long cube) => lv95 == 0 ? (cube == 0 ? 0 : 1) : Math.Abs(cube - lv95) / (double)lv95;
}
