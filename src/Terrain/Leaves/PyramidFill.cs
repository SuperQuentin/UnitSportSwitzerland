using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The earth world's fill built on the globe's pyramid (#1678): the generator's leaf (<see cref="GeneratedLeaves"/>) moved
/// onto the best ground the game knows, as the user decided on #1678:
/// <list type="bullet">
/// <item>its base relief is the pyramid's heights (<see cref="PyramidBase"/>), and the generator's relief over its own
/// 500 m macro relief rides on it as detail: <c>h = P + (G - M)</c>, so its valleys, ridges, roads and villages stay
/// where it put them, on the pyramid's ground;</item>
/// <item>the pyramid's lakes (<see cref="CubeManifest.Lakes"/>) are water at their level, over a bed shelving from the
/// pyramid's shore (<see cref="WaterBed.OpenDepth"/>); the generator's own lakes are not: none of its lake or its land
/// where the pyramid says what is there, and its detail fades out over its lakes and near the pyramid's shores;</item>
/// <item>the cover is the generator's but at the pyramid's lakes (water there, open ground where the generator's lake was);
/// trees and buildings standing in a lake are dropped, everything else moves with the ground's change under it.</item>
/// </list>
/// Every quantity is a function of a vertex's place on its face's global lattice: the pyramid read there, the
/// generator's base on a 16-vertex control lattice, the distances to the shore exact within <see cref="MarginVertices"/>
/// on a window round the leaf. Leaves agree on their edges without coordinating. Godot-free.
/// </summary>
public static class PyramidFill
{
    /// <summary>The window's margin round a leaf, lattice vertices (~305 m at level 14): how far a shore is seen.</summary>
    public const int MarginVertices = 256;

    /// <summary>The window's spacing, lattice vertices (~4.8 m: the pyramid's level 13 has a sample every 2).</summary>
    public const int WindowStep = 4;

    /// <summary>The generator's base is read every this many vertices and bilinear between (~19 m; its macro is a 500 m spline).</summary>
    public const int ControlUnits = 16;

    /// <summary>Over this distance from a pyramid shore the generator's detail fades in, metres: the shore is the pyramid's.</summary>
    public const double ShoreDetailM = 60;

    /// <summary>The deepest the fill's lake bed gets, metres (the shelf reaches it ~600 m out).</summary>
    public const double MaxBedM = WaterBed.MaxMaxDepthM;

    /// <summary>The fetch the pyramid's lakes are given (their waves): the most the format holds.</summary>
    public const byte LakeFetch = 255;

    /// <summary>A leaf's ground on the pyramid: the heights, each vertex's lake level (NaN dry) and the change from the generator's.</summary>
    public sealed record Ground(float[] Heights, float[] Lake, double[] Delta, float[] GeneratorLake);

    /// <summary>
    /// The fill's heights on the pyramid from the generator's (<paramref name="generated"/>, ellipsoidal, n x n);
    /// <paramref name="baseAt"/> is the generator's macro relief above sea level and its lake weight at a lattice point.
    /// Null where the pyramid holds no height over the leaf. With <paramref name="surface"/> a lake stands at its level
    /// (no bed), as the globe draws a far leaf's lakes.
    /// </summary>
    public static Ground? Build(PyramidBase pyramid, LeafTransform t, float[] generated, Func<int, int, (double MacroSea, double Lake)> baseAt,
        bool surface = false)
    {
        var leaf = t.Key;
        int cells = t.Cells, n = cells + 1, latticeLevel = leaf.Level + t.Bits, face = leaf.Face;
        long gi0 = (long)leaf.X << t.Bits, gj0 = (long)leaf.Y << t.Bits;
        double cellM = CellM(t);

        // the pyramid at every vertex; none at all: the generator's alone
        var p = new double[n * n];
        bool any = false;
        Parallel.For(0, n, j =>
        {
            for (int i = 0; i < n; i++)
            {
                double h = pyramid.HeightAt(face, gi0 + i, gj0 + j, latticeLevel);
                p[j * n + i] = h;
                if (!double.IsNaN(h)) any = true;
            }
        });
        if (!any) return null;

        // the generator's base on the control lattice
        int cs = cells / ControlUnits + 1;
        var macro = new double[cs * cs];
        var genLake = new double[cs * cs];
        for (int cj = 0; cj < cs; cj++)
            for (int ci = 0; ci < cs; ci++)
            {
                var (m, l) = baseAt(ci * ControlUnits, cj * ControlUnits);
                macro[cj * cs + ci] = m;
                genLake[cj * cs + ci] = l;
            }

        // the shores round the leaf: the pyramid's lakes on a window, and the distance to the other side from each sample
        var shores = Shores(pyramid, face, gi0, gj0, cells, latticeLevel, cellM);

        var heights = new float[n * n];
        var lake = new float[n * n];
        var delta = new double[n * n];
        var generatorLake = new float[n * n];
        Parallel.For(0, n, j =>
        {
            int cj = Math.Min(j / ControlUnits, cs - 2);
            double v = (j - cj * ControlUnits) / (double)ControlUnits;
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                int ci = Math.Min(i / ControlUnits, cs - 2);
                double u = (i - ci * ControlUnits) / (double)ControlUnits;
                int c = cj * cs + ci;
                double gl = LeafBlend.Bilerp(genLake[c], genLake[c + 1], genLake[c + cs], genLake[c + cs + 1], u, v);
                generatorLake[k] = (float)gl;
                float g = generated[k];
                double ph = p[k];
                if (double.IsNaN(ph))
                {
                    heights[k] = g;
                    lake[k] = float.NaN;
                    continue;
                }
                float level = pyramid.LakeAt(ph);
                lake[k] = level;
                double h;
                if (!float.IsNaN(level))
                    h = surface ? level : level - WaterBed.OpenDepth(shores?.ToDry(i, j) ?? MarginVertices * cellM, MaxBedM);
                else if (float.IsNaN(g)) h = ph;
                else
                {
                    double und = t.UndulationAt(i, j);
                    double m = LeafBlend.Bilerp(macro[c], macro[c + 1], macro[c + cs], macro[c + cs + 1], u, v) + und;
                    // the generator's relief over its macro; none over its own lakes (their bed), fading in from the pyramid's shore
                    double detail = (g - m) * (1 - LeafBlend.SmoothStep(0.2, 0.5, gl));
                    if (shores != null) detail *= LeafBlend.SmoothStep(0, ShoreDetailM, shores.ToWet(i, j));
                    h = ph + detail;
                }
                heights[k] = (float)h;
                delta[k] = float.IsNaN(g) ? 0 : h - g;
            }
        });
        return new Ground(heights, lake, delta, generatorLake);
    }

    /// <summary>
    /// The generator's leaf on the pyramid's ground: heights, cover and water as <see cref="Build"/> says, its trees,
    /// buildings and roads moved with the ground (those standing in a lake dropped).
    /// </summary>
    public static void Apply(CubeLeafContent content, Ground ground, LeafTransform t)
    {
        int n = t.Cells + 1;
        var gh = content.Heights!;
        content.Heights = new CubeTile { Key = gh.Key, Bits = gh.Bits, Heights = ground.Heights, Quantum = gh.Quantum };
        bool Wet(int k) => !float.IsNaN(ground.Lake[k]);
        bool GeneratorLake(int k) => ground.GeneratorLake[k] >= 0.5f;

        if (content.Cover is { } cover)
            for (int k = 0; k < cover.Length && k < n * n; k++)
            {
                if (Wet(k)) cover[k] = (byte)CoverClass.Water;
                else if (cover[k] == (byte)CoverClass.Water && GeneratorLake(k)) cover[k] = (byte)CoverClass.Open;
            }

        // still water: the pyramid's lakes at their level; the generator's rivers moved with the ground, its lakes dropped
        var water = CubeWater.Dry(t.Bits);
        bool wetAny = false;
        int side = water.Side;
        for (int r = 0; r < side; r++)
            for (int c = 0; c < side; c++)
            {
                int k = r * CubeWater.Stride * n + c * CubeWater.Stride, s = r * side + c;
                if (Wet(k))
                {
                    water.Level[s] = ground.Lake[k];
                    water.Fetch[s] = LakeFetch;
                }
                else if (content.Water is { } gw && !float.IsNaN(gw.Level[s]) && !GeneratorLake(k))
                {
                    water.Level[s] = (float)(gw.Level[s] + ground.Delta[k]);
                    water.Fetch[s] = gw.Fetch[s];
                }
                else continue;
                wetAny = true;
            }
        content.Water = wetAny ? water : null;

        int Nearest(float x, float y, float z)
        {
            var (i, j, _) = t.LocalToLattice(x, y, z);
            int a = Math.Clamp((int)Math.Round(i), 0, n - 1), b = Math.Clamp((int)Math.Round(j), 0, n - 1);
            return b * n + a;
        }
        var lift = new LeafBlend.Lift(t, ground.Delta, n);
        if (content.Trees is { } trees)
        {
            var kept = new List<TreeInstance>(trees.Count);
            foreach (var tree in trees)
                if (!Wet(Nearest(tree.X, tree.Y, tree.Z))) kept.Add(tree with { Y = tree.Y + (float)lift.At(tree.X, tree.Y, tree.Z) });
            content.Trees = kept;
        }
        if (content.Buildings is { } bt)
        {
            var kept = new List<Building>(bt.Buildings.Count);
            foreach (var b in bt.Buildings)
            {
                var (mx, mz) = SourceTileToLeaf.Middle(b);
                if (Wet(Nearest(mx, b.MinY, mz))) continue;
                kept.Add(LeafBlend.Lifted(b, (float)lift.At(mx, b.MinY, mz)));
            }
            content.Buildings = new BuildingTile { Id = bt.Id, Buildings = kept };
        }
        if (content.Roads is { } roads) LeafRecords.Map(roads, lift);
    }

    /// <summary>A leaf's lattice spacing across its middle, metres (~1.19 at level 14).</summary>
    internal static double CellM(LeafTransform t)
    {
        var (ax, ay, az) = t.LatticeToLocal(0, t.Cells / 2, 0);
        var (bx, by, bz) = t.LatticeToLocal(t.Cells, t.Cells / 2, 0);
        return Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az)) / t.Cells;
    }

    /// <summary>Lakes only, from a ground (the globe's far fill leaf, which draws its water from it); null when dry.</summary>
    public static CubeWater? Lakes(Ground ground, int bits)
    {
        var water = CubeWater.Dry(bits);
        int side = water.Side, n = (1 << bits) + 1;
        bool any = false;
        for (int r = 0; r < side; r++)
            for (int c = 0; c < side; c++)
            {
                float level = ground.Lake[r * CubeWater.Stride * n + c * CubeWater.Stride];
                if (float.IsNaN(level)) continue;
                water.Level[r * side + c] = level;
                water.Fetch[r * side + c] = LakeFetch;
                any = true;
            }
        return any ? water : null;
    }

    /// <summary>The distances to the pyramid's shores round a leaf, on the window; null when no lake is within it.</summary>
    private sealed class ShoreField(float[] toWet, float[] toDry, int side, double stepM)
    {
        private double At(float[] f, int i, int j)
        {
            double fx = (i + MarginVertices) / (double)WindowStep, fy = (j + MarginVertices) / (double)WindowStep;
            int x0 = Math.Min((int)fx, side - 2), y0 = Math.Min((int)fy, side - 2);
            int k = y0 * side + x0;
            return LeafBlend.Bilerp(f[k], f[k + 1], f[k + side], f[k + side + 1], fx - x0, fy - y0) * stepM;
        }

        /// <summary>From a dry vertex to the nearest lake sample, metres (the margin at most).</summary>
        public double ToWet(int i, int j) => At(toWet, i, j);

        /// <summary>From a lake vertex to the nearest dry sample, metres (the margin at most).</summary>
        public double ToDry(int i, int j) => At(toDry, i, j);
    }

    private static ShoreField? Shores(PyramidBase pyramid, int face, long gi0, long gj0, int cells, int latticeLevel, double cellM)
    {
        if (pyramid.Lakes.Length == 0) return null;
        int side = (cells + 2 * MarginVertices) / WindowStep + 1;
        var wet = new bool[side * side];
        int wetCount = 0;
        Parallel.For(0, side, b =>
        {
            int count = 0;
            for (int a = 0; a < side; a++)
            {
                double h = pyramid.HeightAt(face, gi0 - MarginVertices + (long)a * WindowStep, gj0 - MarginVertices + (long)b * WindowStep, latticeLevel);
                if (double.IsNaN(h) || float.IsNaN(pyramid.LakeAt(h))) continue;
                wet[b * side + a] = true;
                count++;
            }
            Interlocked.Add(ref wetCount, count);
        });
        if (wetCount == 0) return null;
        float cap = MarginVertices / (float)WindowStep;
        var toWet = Distance(wet, side, true, cap);
        var toDry = Distance(wet, side, false, cap);
        return new ShoreField(toWet, toDry, side, WindowStep * cellM);
    }

    /// <summary>
    /// Each sample's Euclidean distance (in samples, capped) to the nearest one whose flag is <paramref name="to"/>: an
    /// exact distance transform (Felzenszwalb and Huttenlocher), so within the cap it is the same from any window holding it.
    /// </summary>
    internal static float[] Distance(bool[] flags, int side, bool to, float cap)
    {
        // far past any squared distance on a window, small enough that q² still counts beside it
        const double Inf = 1e10;
        var f = new double[side * side];
        for (int k = 0; k < f.Length; k++) f[k] = flags[k] == to ? 0 : Inf;
        var line = new double[side];
        var outLine = new double[side];
        var v = new int[side];
        var z = new double[side + 1];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++) line[x] = f[y * side + x];
            Transform(line, outLine, v, z, side);
            for (int x = 0; x < side; x++) f[y * side + x] = outLine[x];
        }
        for (int x = 0; x < side; x++)
        {
            for (int y = 0; y < side; y++) line[y] = f[y * side + x];
            Transform(line, outLine, v, z, side);
            for (int y = 0; y < side; y++) f[y * side + x] = outLine[y];
        }
        var d = new float[side * side];
        for (int k = 0; k < d.Length; k++) d[k] = (float)Math.Min(Math.Sqrt(f[k]), cap);
        return d;
    }

    private static void Transform(double[] f, double[] d, int[] v, double[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double S(int p) => (f[q] + (double)q * q - (f[p] + (double)p * p)) / (2.0 * q - 2.0 * p);
            double s = S(v[k]);
            // z[0] is -inf: the loop ends at the first parabola at the latest
            while (s <= z[k])
            {
                k--;
                s = S(v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }
}
