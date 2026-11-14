using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The earth world's fill built on the globe's pyramid (#1678): the generator's leaf (<see cref="GeneratedLeaves"/>) moved
/// onto the best ground the game knows, as the user decided on #1678:
/// <list type="bullet">
/// <item>its base relief is the pyramid's heights (<see cref="PyramidBase"/>), and the generator's own short relief rides
/// on it as detail: <c>h = P + D(G - L)</c>, L the generator's ground smoothed over ~150 m (<see cref="LowStep"/>), D a
/// soft clamp to <see cref="DetailClampM"/>. Its broad shapes are left out: its valleys, pressed flat along its own
/// rivers, are not the pyramid's, and <c>G</c> less its 500 m macro relief dug a trench 50-70 m deep along the Lavaux
/// shore (the additive correction of <c>terrain/blend-convex-not-additive</c>);</item>
/// <item>the pyramid's lakes (<see cref="CubeManifest.Lakes"/>) and its sea (ground at the geoid, <see cref="PyramidBase.SeaSurface"/>,
/// #1808) are water at their level, over a bed shelving from the
/// pyramid's shore (<see cref="WaterBed.OpenDepth"/>); the generator's own lakes are not: none of its lake or its land
/// where the pyramid says what is there, and its detail fades out over its lakes and near the pyramid's shores;</item>
/// <item>the cover is the generator's but at the pyramid's lakes (water there, open ground where the generator's lake was);
/// trees and buildings standing in a lake are dropped, everything else moves with the ground's change under it.</item>
/// </list>
/// Every quantity is a function of a vertex's place on its face's global lattice: the pyramid read there, the
/// generator's low-pass on a window of the global lattice (each value summed in one order over the same samples), the
/// distances to the shore exact within <see cref="MarginVertices"/> on a window round the leaf. Leaves agree on their
/// edges without coordinating. Godot-free.
/// </summary>
public static class PyramidFill
{
    /// <summary>The window's margin round a leaf, lattice vertices (~305 m at level 14): how far a shore is seen.</summary>
    public const int MarginVertices = 256;

    /// <summary>The window's spacing, lattice vertices (~4.8 m: the pyramid's level 13 has a sample every 2).</summary>
    public const int WindowStep = 4;

    /// <summary>The generator's low-pass samples, lattice vertices apart (~38 m), on the face's global lattice.</summary>
    public const int LowStep = 32;

    /// <summary>The low-pass's box radius, samples, applied twice each way (~150 m each side).</summary>
    public const int LowBlur = 4;

    /// <summary>The generator's detail is softly clamped to this, metres (its roughness and channels are a few metres).</summary>
    public const double DetailClampM = 12;

    /// <summary>Over this distance from a pyramid shore the generator's detail fades in, metres: the shore is the pyramid's.</summary>
    public const double ShoreDetailM = 150;

    /// <summary>The deepest the fill's lake bed gets, metres (the shelf reaches it ~600 m out).</summary>
    public const double MaxBedM = WaterBed.MaxMaxDepthM;

    /// <summary>The generator's lake weight past which its water and its Water cover are its lake's, not a river's.</summary>
    public const float GeneratorLakeRim = 0.05f;

    /// <summary>The fetch the pyramid's lakes are given (their waves): the most the format holds.</summary>
    public const byte LakeFetch = 255;

    /// <summary>A leaf's ground on the pyramid: the heights, each vertex's lake level (NaN dry) and the change from the generator's.</summary>
    public sealed record Ground(float[] Heights, float[] Lake, double[] Delta, float[] GeneratorLake);

    /// <summary>
    /// The fill's heights on the pyramid from the generator's (<paramref name="generated"/>, ellipsoidal, n x n);
    /// <paramref name="generatorAt"/> is the generator's ground above sea level (its lakes' surface, not their bed) and its
    /// lake weight at a point of the face's global lattice (the leaf's level and bits), inside the leaf or round it.
    /// Null where the pyramid holds no height over the leaf. With <paramref name="surface"/> a lake stands at its level
    /// (no bed), as the globe draws a far leaf's lakes.
    /// </summary>
    public static Ground? Build(PyramidBase pyramid, LeafTransform t, float[] generated, Func<long, long, (double HSea, double Lake)> generatorAt,
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

        // the generator on a window of the global lattice round the leaf, and its ground's low-pass
        int margin = 2 * LowBlur + 1, ls = cells / LowStep + 1 + 2 * margin;
        long lx0 = gi0 / LowStep - margin, ly0 = gj0 / LowStep - margin;
        var low = new double[ls * ls];
        var genLake = new double[ls * ls];
        Parallel.For(0, ls, y =>
        {
            for (int x = 0; x < ls; x++)
            {
                var (h, l) = generatorAt((lx0 + x) * LowStep, (ly0 + y) * LowStep);
                low[y * ls + x] = h;
                genLake[y * ls + x] = l;
            }
        });
        for (int pass = 0; pass < 2; pass++)
        {
            low = Box(low, ls, 1, 0);
            low = Box(low, ls, 0, 1);
        }

        // the sea over the leaf and the shores' window round it (#1808)
        var sea = pyramid.SeaOver(face, gi0 - MarginVertices, gj0 - MarginVertices, cells + 2 * MarginVertices, latticeLevel);
        // the shores round the leaf: the pyramid's lakes and sea on a window, and the distance to the other side from each sample
        var shores = Shores(pyramid, sea, face, gi0, gj0, cells, latticeLevel, cellM);

        var heights = new float[n * n];
        var lake = new float[n * n];
        var delta = new double[n * n];
        var generatorLake = new float[n * n];
        Parallel.For(0, n, j =>
        {
            double fy = (double)(gj0 + j) / LowStep - ly0;
            int cj = Math.Min((int)fy, ls - 2);
            double v = fy - cj;
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                double fx = (double)(gi0 + i) / LowStep - lx0;
                int ci = Math.Min((int)fx, ls - 2);
                double u = fx - ci;
                int c = cj * ls + ci;
                double gl = LeafBlend.Bilerp(genLake[c], genLake[c + 1], genLake[c + ls], genLake[c + ls + 1], u, v);
                generatorLake[k] = (float)gl;
                float g = generated[k];
                double ph = p[k];
                if (double.IsNaN(ph))
                {
                    heights[k] = g;
                    lake[k] = float.NaN;
                    continue;
                }
                float level = WaterAt(pyramid, sea, gi0 + i, gj0 + j, ph);
                lake[k] = level;
                double h;
                if (!float.IsNaN(level))
                    h = surface ? level : level - WaterBed.OpenDepth(shores?.ToDry(i, j) ?? MarginVertices * cellM, MaxBedM);
                else if (float.IsNaN(g)) h = ph;
                else
                {
                    double und = t.UndulationAt(i, j);
                    double m = LeafBlend.Bilerp(low[c], low[c + 1], low[c + ls], low[c + ls + 1], u, v) + und;
                    // the generator's short relief, softly clamped; none over its own lakes (their bed), fading in from the
                    // pyramid's shore
                    double detail = DetailClampM * Math.Tanh((g - m) / DetailClampM) * (1 - LeafBlend.SmoothStep(0.2, 0.5, gl));
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
        // the generator's lake and its ragged rim: its shore noise puts water where the smooth weight is well under a
        // half, and kept as a river there it stood over the pyramid's ground, dropped and drawn as a pale band
        bool GeneratorLake(int k) => ground.GeneratorLake[k] > GeneratorLakeRim;

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
        // a leaf all water (the open sea, #1808) has no roads; one with a shore keeps them, the drowned ones on the bed
        bool allWet = true;
        for (int k = 0; k < ground.Lake.Length && allWet; k++) allWet = Wet(k);
        if (allWet) content.Roads = null;
        else if (content.Roads is { } roads) LeafRecords.Map(roads, lift);
    }

    /// <summary>A leaf's lattice spacing across its middle, metres (~1.19 at level 14).</summary>
    internal static double CellM(LeafTransform t)
    {
        var (ax, ay, az) = t.LatticeToLocal(0, t.Cells / 2, 0);
        var (bx, by, bz) = t.LatticeToLocal(t.Cells, t.Cells / 2, 0);
        return Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az)) / t.Cells;
    }

    /// <summary>A box mean of radius <see cref="LowBlur"/> along one axis, each value summed afresh in one order (edges clamped).</summary>
    private static double[] Box(double[] f, int side, int dx, int dy)
    {
        var o = new double[f.Length];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double sum = 0;
                for (int r = -LowBlur; r <= LowBlur; r++)
                    sum += f[Math.Clamp(y + r * dy, 0, side - 1) * side + Math.Clamp(x + r * dx, 0, side - 1)];
                o[y * side + x] = sum / (2 * LowBlur + 1);
            }
        return o;
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

    /// <summary>The water the pyramid's ground of height <paramref name="h"/> at a lattice point stands in: a lake's level, the sea's (#1808), or NaN.</summary>
    private static float WaterAt(PyramidBase pyramid, PyramidBase.SeaPatch? sea, long gi, long gj, double h)
    {
        float lake = pyramid.LakeAt(h);
        if (!float.IsNaN(lake) || sea == null) return lake;
        return sea.LevelAt(gi, gj, h);
    }

    private static ShoreField? Shores(PyramidBase pyramid, PyramidBase.SeaPatch? sea, int face, long gi0, long gj0, int cells, int latticeLevel, double cellM)
    {
        if (pyramid.Lakes.Length == 0 && sea == null) return null;
        int side = (cells + 2 * MarginVertices) / WindowStep + 1;
        var wet = new bool[side * side];
        int wetCount = 0;
        Parallel.For(0, side, b =>
        {
            int count = 0;
            for (int a = 0; a < side; a++)
            {
                long gi = gi0 - MarginVertices + (long)a * WindowStep, gj = gj0 - MarginVertices + (long)b * WindowStep;
                double h = pyramid.HeightAt(face, gi, gj, latticeLevel);
                if (double.IsNaN(h) || float.IsNaN(WaterAt(pyramid, sea, gi, gj, h))) continue;
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
