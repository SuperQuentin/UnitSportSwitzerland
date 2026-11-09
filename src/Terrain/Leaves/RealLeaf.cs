using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A real leaf as the blend sees it (#1510, G2 C1 PR 3): where it has ground, and its ground's low-pass.
///
/// <para>
/// A leaf at the region's border is real only in part: the build wrote NaN where its sources stop (B4's D: region: 434
/// of its 462 border leaves, the LV95 tiles' edges cutting them at the face's twist). What counts as real is the
/// <see cref="CellUnits"/>-vertex cells whose every vertex has ground, gathered into quadtree <see cref="Blocks"/> (a
/// whole leaf is one block). A real vertex outside them (the fringe, under a cell) is handed to the fill: the boundary
/// then runs along whole cells, where the blend continues the real surface to first order.
/// </para>
///
/// <para>
/// The low-pass: knots every <see cref="KnotUnits"/> vertices (~76 m, the flat world's 100 m knots), above sea level,
/// and their area averages at twice and four times the spacing and the leaf's mean (<see cref="Levels"/>), as
/// <c>ProceduralWorld.RealTile</c>'s pyramid. A knot with no ground takes its neighbours' mean, so a part-real leaf's
/// lattice is whole (only ever read at a point of its blocks).
/// </para>
/// </summary>
public sealed class RealLeaf
{
    /// <summary>The side of a real cell, lattice vertices (~9.5 m at level 14): the boundary's granularity.</summary>
    public const int CellUnits = 8;

    /// <summary>Knot spacing, lattice vertices (~76 m at level 14).</summary>
    public const int KnotUnits = 64;

    /// <summary>A quadtree block of real cells: its south-west vertex and its side, in the leaf's lattice.</summary>
    public readonly record struct Block(int I0, int J0, int Size);

    public CubeKey Key { get; }

    /// <summary>Lattice cells a side.</summary>
    public int Cells { get; }

    /// <summary>The leaf's south-west vertex on its face's global lattice (level + bits).</summary>
    public long Gi0 { get; }
    public long Gj0 { get; }

    /// <summary>Metres a lattice step along i and along j (at the leaf's middle).</summary>
    public double ScaleI { get; }
    public double ScaleJ { get; }

    /// <summary>Every vertex has ground.</summary>
    public bool Full { get; }

    public IReadOnlyList<Block> Blocks { get; }

    /// <summary>Knots and their averages above sea level: level 0 every <see cref="KnotUnits"/>, each next one twice as coarse, the last the mean.</summary>
    internal readonly double[][] Levels;

    /// <summary>Each level's spacing in lattice steps (the last is the leaf).</summary>
    internal readonly int[] LevelUnits;

    private readonly bool[] _cellReal;
    private readonly int _cellsSide;

    private RealLeaf(CubeKey key, int cells, double scaleI, double scaleJ, bool full, bool[] cellReal, List<Block> blocks, double[][] levels, int[] levelUnits)
    {
        Key = key;
        Cells = cells;
        int bits = CubeKey.LatticeBits(key.Level);
        Gi0 = (long)key.X << bits;
        Gj0 = (long)key.Y << bits;
        ScaleI = scaleI;
        ScaleJ = scaleJ;
        Full = full;
        _cellReal = cellReal;
        _cellsSide = cells / CellUnits;
        Blocks = blocks;
        Levels = levels;
        LevelUnits = levelUnits;
    }

    /// <summary>Whether lattice vertex (i, j) lies in a real cell (its edges included): its height is the region's.</summary>
    public bool Kept(int i, int j)
    {
        if (Full) return true;
        int ci = i / CellUnits, cj = j / CellUnits;
        // a vertex on cell lines belongs to every cell round it
        for (int a = (i % CellUnits == 0 ? ci - 1 : ci); a <= ci; a++)
            for (int b = (j % CellUnits == 0 ? cj - 1 : cj); b <= cj; b++)
                if ((uint)a < _cellsSide && (uint)b < _cellsSide && _cellReal[b * _cellsSide + a]) return true;
        return false;
    }

    /// <summary>
    /// The summary of a leaf's heights (ellipsoidal, NaN where none) in its transform; null when no cell is real.
    /// </summary>
    public static RealLeaf? Of(CubeTile heights, LeafTransform t)
    {
        int cells = t.Cells, n = cells + 1;
        var h = heights.Heights;
        if (h.Length != n * n) throw new ArgumentException($"{heights.Key}: {h.Length} heights for {n}²");

        // the real cells
        int side = cells / CellUnits;
        var real = new bool[side * side];
        bool full = true;
        for (int cj = 0; cj < side; cj++)
            for (int ci = 0; ci < side; ci++)
            {
                bool all = true;
                for (int j = cj * CellUnits; all && j <= (cj + 1) * CellUnits; j++)
                    for (int i = ci * CellUnits; i <= (ci + 1) * CellUnits; i++)
                        if (float.IsNaN(h[j * n + i])) { all = false; break; }
                real[cj * side + ci] = all;
                full &= all;
            }
        if (full)
            for (int k = 0; k < h.Length && full; k++) full = !float.IsNaN(h[k]);
        var blocks = new List<Block>();
        Quad(real, side, 0, 0, side, blocks);
        if (blocks.Count == 0) return null;

        // the knots above sea level, gaps filled from their neighbours
        int ks = cells / KnotUnits + 1;
        var knots = new double[ks * ks];
        for (int kj = 0; kj < ks; kj++)
            for (int ki = 0; ki < ks; ki++)
            {
                int i = ki * KnotUnits, j = kj * KnotUnits;
                float v = h[j * n + i];
                knots[kj * ks + ki] = float.IsNaN(v) ? double.NaN : v - t.UndulationAt(i, j);
            }
        Fill(knots, ks);

        var levelUnits = new List<int> { KnotUnits };
        var levels = new List<double[]> { knots };
        for (int u = KnotUnits * 2; u < cells; u *= 2)
        {
            levelUnits.Add(u);
            levels.Add(Average(knots, ks, cells / u + 1, (double)u / KnotUnits));
        }
        levelUnits.Add(cells);
        levels.Add([Mean(knots, ks)]);

        // metres a step along each axis, across the middle
        var (ax, ay, az) = t.LatticeToLocal(0, cells / 2, 0);
        var (bx, by, bz) = t.LatticeToLocal(cells, cells / 2, 0);
        var (cx, cy, cz) = t.LatticeToLocal(cells / 2, 0, 0);
        var (dx, dy, dz) = t.LatticeToLocal(cells / 2, cells, 0);
        double si = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az)) / cells;
        double sj = Math.Sqrt((dx - cx) * (dx - cx) + (dy - cy) * (dy - cy) + (dz - cz) * (dz - cz)) / cells;
        return new RealLeaf(heights.Key, cells, si, sj, full, real, blocks, levels.ToArray(), levelUnits.ToArray());
    }

    /// <summary>The quadtree's whole blocks of real cells, south-west first.</summary>
    private static void Quad(bool[] real, int side, int ci, int cj, int size, List<Block> into)
    {
        bool all = true, any = false;
        for (int b = cj; b < cj + size; b++)
            for (int a = ci; a < ci + size; a++)
            {
                all &= real[b * side + a];
                any |= real[b * side + a];
            }
        if (!any) return;
        if (all)
        {
            into.Add(new Block(ci * CellUnits, cj * CellUnits, size * CellUnits));
            return;
        }
        int half = size / 2;
        Quad(real, side, ci, cj, half, into);
        Quad(real, side, ci + half, cj, half, into);
        Quad(real, side, ci, cj + half, half, into);
        Quad(real, side, ci + half, cj + half, half, into);
    }

    /// <summary>Gaps (NaN) take the mean of their known neighbours, ring by ring, until none is left.</summary>
    private static void Fill(double[] v, int side)
    {
        for (int pass = 0; pass < 2 * side; pass++)
        {
            var next = (double[])v.Clone();
            bool gap = false;
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    if (!double.IsNaN(v[j * side + i])) continue;
                    double sum = 0;
                    int count = 0;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int a = i + di, b = j + dj;
                            if ((uint)a >= side || (uint)b >= side) continue;
                            double w = v[b * side + a];
                            if (double.IsNaN(w)) continue;
                            sum += w;
                            count++;
                        }
                    if (count > 0) next[j * side + i] = sum / count;
                    else gap = true;
                }
            Array.Copy(next, v, v.Length);
            if (!gap) return;
        }
    }

    /// <summary>Tent-weighted area averages of the knots on a lattice <paramref name="ratio"/> times as coarse.</summary>
    private static double[] Average(double[] knots, int ks, int side, double ratio)
    {
        var level = new double[side * side];
        for (int r = 0; r < side; r++)
            for (int c = 0; c < side; c++)
            {
                double sum = 0, wsum = 0;
                for (int kr = 0; kr < ks; kr++)
                    for (int kc = 0; kc < ks; kc++)
                    {
                        double w = Math.Max(0, 1 - Math.Abs(kc / ratio - c)) * Math.Max(0, 1 - Math.Abs(kr / ratio - r));
                        if (w == 0) continue;
                        // an edge knot stands for half the area of an inner one
                        w *= (kc == 0 || kc == ks - 1 ? 0.5 : 1) * (kr == 0 || kr == ks - 1 ? 0.5 : 1);
                        sum += w * knots[kr * ks + kc];
                        wsum += w;
                    }
                level[r * side + c] = sum / wsum;
            }
        return level;
    }

    private static double Mean(double[] knots, int ks)
    {
        double sum = 0, wsum = 0;
        for (int kr = 0; kr < ks; kr++)
            for (int kc = 0; kc < ks; kc++)
            {
                double w = (kc == 0 || kc == ks - 1 ? 0.5 : 1) * (kr == 0 || kr == ks - 1 ? 0.5 : 1);
                sum += w * knots[kr * ks + kc];
                wsum += w;
            }
        return sum / wsum;
    }

    // ---- the low-pass at a point --------------------------------------------------------------------------------

    /// <summary>Metres between level 0's knots (the mean of the two axes).</summary>
    private double StepM => (ScaleI + ScaleJ) / 2;

    /// <summary>
    /// The low-pass at the leaf's lattice point (i, j), as carried <paramref name="d"/> metres: the level whose spacing
    /// is about d/2, blended with the next by a smoothstep in log-distance (<c>ProceduralWorld.Blend</c>'s rule).
    /// Exactly the knots within two knot spacings, so the seam reads them.
    /// </summary>
    public double Low(double i, double j, double d)
    {
        double sigma = Math.Max(LevelUnits[0] * StepM, d / 2);
        int last = Levels.Length - 1;
        if (sigma <= LevelUnits[0] * StepM) return LevelAt(0, i, j);
        if (sigma >= LevelUnits[last] * StepM) return Levels[last][0];
        int l = 0;
        while (sigma >= LevelUnits[l + 1] * StepM) l++;
        double f = LeafBlend.SmoothStep(0, 1, Math.Log(sigma / (LevelUnits[l] * StepM)) / Math.Log((double)LevelUnits[l + 1] / LevelUnits[l]));
        double a = LevelAt(l, i, j), b = LevelAt(l + 1, i, j);
        return a + (b - a) * f;
    }

    internal double LevelAt(int level, double i, double j)
    {
        var values = Levels[level];
        if (values.Length == 1) return values[0];
        int side = Cells / LevelUnits[level] + 1;
        return CatmullRomAt(values, side, LevelUnits[level], i, j);
    }

    /// <summary>
    /// Catmull-Rom through a lattice, ends clamped: C1, so nothing carried outward creases (a bilinear lattice's
    /// slope breaks became strips in the flat world), and exactly the node on a node.
    /// </summary>
    private static double CatmullRomAt(double[] values, int side, double spacing, double i, double j)
    {
        int last = side - 1;
        double u = Math.Clamp(i / spacing, 0, last), v = Math.Clamp(j / spacing, 0, last);
        int c = Math.Min((int)u, last), r = Math.Min((int)v, last);
        double fu = u - c, fv = v - r;
        double Row(int rr)
        {
            rr = Math.Clamp(rr, 0, last);
            double V(int cc) => values[rr * side + Math.Clamp(cc, 0, last)];
            return CatmullRom(V(c - 1), V(c), V(c + 1), V(c + 2), fu);
        }
        return CatmullRom(Row(r - 1), Row(r), Row(r + 1), Row(r + 2), fv);
    }

    private static double CatmullRom(double a, double b, double c, double d, double t)
    {
        if (t == 0) return b;
        return b + 0.5 * t * (c - a + t * (2 * a - 5 * b + 4 * c - d + t * (3 * (b - c) + d - a)));
    }
}
