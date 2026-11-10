using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The earth world's fill on the globe's pyramid (#1678): <see cref="PyramidBase"/> read at a face's global lattice,
/// <see cref="PyramidFill"/> (the generator's detail on the pyramid's relief, its lakes at their level over a shelving
/// bed) and <see cref="LeafBlend"/>'s hand-over on it, on a synthetic pyramid: a lake at 400 m west of a line, ground
/// rising east of it. The real region's border is BlendCheck's <c>--leaf-blend --pyramid</c>.
/// </summary>
public class PyramidFillTests
{
    private static readonly CubeKey Fill = LeafFrames.LeafAt(46.47, 6.80);
    private static readonly CubeKey East = Fill.Neighbour(1, 0)!.Value;
    private const int N = 513;
    private const float Lake = 400f;

    /// <summary>The shore: a level-13 global lattice column a third into the fill leaf (a level-13 sample is two leaf vertices).</summary>
    private static readonly long ShoreGi13 = (((long)Fill.X << 9) + 170) / 2;

    /// <summary>The synthetic pyramid's height at a level-13 global lattice point (ellipsoidal).</summary>
    private static float P13(long gi, long gj) => gi <= ShoreGi13 ? Lake : Lake + 0.25f * (gi - ShoreGi13) + 0.05f * (gj % 7);

    private static PyramidBase Pyramid(Func<CubeKey, CubeTile?>? more = null) => new(key =>
    {
        if (more?.Invoke(key) is { } t) return t;
        if (key.Level != 13) return null;
        var h = new float[N * N];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
                h[j * N + i] = P13(((long)key.X << 9) + i, ((long)key.Y << 9) + j);
        return new CubeTile { Key = key, Bits = 9, Heights = h };
    }, new[] { Lake }, 13);

    private static long Gi(CubeKey leaf, int i) => ((long)leaf.X << 9) + i;
    private static long Gj(CubeKey leaf, int j) => ((long)leaf.Y << 9) + j;

    [Fact]
    public void The_pyramid_is_read_at_its_samples_and_bilinear_between()
    {
        var p = Pyramid();
        long gi = Gi(Fill, 300), gj = Gj(Fill, 100);
        Assert.Equal(P13(gi / 2, gj / 2), p.HeightAt(Fill.Face, gi, gj, 23), 4);
        double mean = (P13(gi / 2, gj / 2) + P13(gi / 2 + 1, gj / 2)) / 2;
        Assert.Equal(mean, p.HeightAt(Fill.Face, gi + 1, gj, 23), 4);
        Assert.Equal(Lake, p.LakeAt(p.HeightAt(Fill.Face, Gi(Fill, 10), gj, 23)));
        Assert.True(float.IsNaN(p.LakeAt(p.HeightAt(Fill.Face, Gi(Fill, 400), gj, 23))));
    }

    [Fact]
    public void Where_the_finest_level_has_no_height_a_coarser_one_answers()
    {
        var l12 = Fill.AncestorAt(12);
        var p = new PyramidBase(key =>
        {
            if (key.Level == 13) return new CubeTile { Key = key, Bits = 9, Heights = Enumerable.Repeat(float.NaN, N * N).ToArray() };
            if (key == l12) return new CubeTile { Key = key, Bits = 9, Heights = Enumerable.Repeat(777f, N * N).ToArray() };
            return null;
        }, null, 13);
        Assert.Equal(777, p.HeightAt(Fill.Face, Gi(Fill, 50), Gj(Fill, 50), 23), 4);
    }

    [Fact]
    public void The_distance_transform_is_exact()
    {
        var rng = new Random(3);
        const int side = 41;
        var flags = new bool[side * side];
        for (int k = 0; k < flags.Length; k++) flags[k] = rng.NextDouble() < 0.03;
        var d = PyramidFill.Distance(flags, side, true, 1000);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double best = double.MaxValue;
                for (int k = 0; k < flags.Length; k++)
                    if (flags[k]) best = Math.Min(best, Math.Sqrt(Math.Pow(k % side - x, 2) + Math.Pow(k / side - y, 2)));
                Assert.Equal(best, d[y * side + x], 3);
            }
    }

    /// <summary>The generator's macro relief (above sea level) and its detail over it, as functions of the global lattice.</summary>
    private static double Macro(long gi, long gj) => 600 + 0.01 * (gi - ((long)Fill.X << 9));

    // short relief, nought at the low-pass's samples (every 32 vertices): what survives the low-pass whole
    private static double Detail(long gi, long gj) => 4 * Math.Sin(gi * Math.PI / 16) * Math.Cos(gj * 0.2);

    /// <summary>The detail as the fill takes it: softly clamped.</summary>
    private static double Clamped(double d) => PyramidFill.DetailClampM * Math.Tanh(d / PyramidFill.DetailClampM);

    private static (float[] Generated, Func<long, long, (double, double)> GeneratorAt, LeafTransform T) Generator(CubeKey leaf, double lake = 0)
    {
        var t = LeafFrames.Swiss.TransformOf(leaf);
        var g = new float[N * N];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
                g[j * N + i] = (float)(Macro(Gi(leaf, i), Gj(leaf, j)) + Detail(Gi(leaf, i), Gj(leaf, j)) + t.UndulationAt(i, j));
        return (g, (gi, gj) => (Macro(gi, gj) + Detail(gi, gj), lake), t);
    }

    [Fact]
    public void The_fill_stands_on_the_pyramid_with_the_generators_detail_and_the_pyramids_lake()
    {
        var p = Pyramid();
        var (g, baseAt, t) = Generator(Fill);
        var ground = PyramidFill.Build(p, t, g, baseAt)!;
        // far into the ground: the pyramid's height plus the generator's detail over its macro (to its control lattice)
        int i = 480, j = 256;
        int i2 = 484;
        double expect = p.HeightAt(Fill.Face, Gi(Fill, i2), Gj(Fill, j), 23) + Clamped(Detail(Gi(Fill, i2), Gj(Fill, j)));
        Assert.Equal(expect, ground.Heights[j * N + i2], 1);
        Assert.True(Math.Abs(Detail(Gi(Fill, i2), Gj(Fill, j))) > 1);
        Assert.True(float.IsNaN(ground.Lake[j * N + i]));
        // in the lake: its level, over a bed shelving down from the shore
        Assert.Equal(Lake, ground.Lake[j * N + 10]);
        Assert.Equal(Lake, ground.Lake[j * N + 160]);
        Assert.True(ground.Heights[j * N + 160] < Lake && ground.Heights[j * N + 160] > Lake - 3);
        Assert.True(ground.Heights[j * N + 10] < ground.Heights[j * N + 160] - 10);
        // the shore is the pyramid's: the detail fades in from it
        Assert.Equal(p.HeightAt(Fill.Face, Gi(Fill, 174), Gj(Fill, j), 23), ground.Heights[j * N + 174], 0.5);
        // at the surface for the globe's far leaf
        var far = PyramidFill.Build(p, t, g, baseAt, surface: true)!;
        Assert.Equal(Lake, far.Heights[j * N + 10]);
    }

    [Fact]
    public void The_generators_own_lake_leaves_no_pit_where_the_pyramid_has_ground()
    {
        var p = Pyramid();
        var (g, _, t) = Generator(Fill);
        // the generator holds a lake over the whole leaf, its bed 30 m under its macro
        for (int k = 0; k < g.Length; k++) g[k] -= 30;
        var ground = PyramidFill.Build(p, t, g, (gi, gj) => (Macro(gi, gj), 1.0))!;
        int i = 480, j = 256;
        Assert.Equal(p.HeightAt(Fill.Face, Gi(Fill, i), Gj(Fill, j), 23), ground.Heights[j * N + i], 0.01);
    }

    [Fact]
    public void A_broad_valley_the_pyramid_lacks_digs_no_trench()
    {
        // the generator's valley pressed 60 m into the ground along a line through the leaf's ground (#1678: along the
        // Lavaux shore its river valley dug a trench 50-70 m deep on the pyramid's slope)
        long axis = Gi(Fill, 400);
        double Valley(long gi) => -60 * Math.Exp(-Math.Pow((gi - axis) / 150.0, 2));
        var p = Pyramid();
        var t = LeafFrames.Swiss.TransformOf(Fill);
        var g = new float[N * N];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
                g[j * N + i] = (float)(Macro(Gi(Fill, i), Gj(Fill, j)) + Valley(Gi(Fill, i)) + t.UndulationAt(i, j));
        var ground = PyramidFill.Build(p, t, g, (gi, gj) => (Macro(gi, gj) + Valley(gi), 0))!;
        for (int j = 0; j < N; j += 16)
            for (int i = 240; i < N; i += 4)
            {
                double ph = p.HeightAt(Fill.Face, Gi(Fill, i), Gj(Fill, j), 23);
                Assert.InRange(ground.Heights[j * N + i] - ph, -PyramidFill.DetailClampM - 0.01, PyramidFill.DetailClampM + 0.01);
            }
    }

    [Fact]
    public void Neighbouring_fill_leaves_agree_on_their_shared_edge()
    {
        var p = Pyramid();
        var (g, baseAt, t) = Generator(Fill);
        var (ge, baseAtE, te) = Generator(East);
        var a = PyramidFill.Build(p, t, g, baseAt)!;
        var b = PyramidFill.Build(p, te, ge, baseAtE)!;
        for (int j = 0; j < N; j++)
            Assert.Equal(a.Heights[j * N + N - 1], b.Heights[j * N], 2);
    }

    [Fact]
    public void Apply_puts_the_pyramids_lake_in_the_cover_and_the_water_and_drops_what_stands_in_it()
    {
        var p = Pyramid();
        var (g, baseAt, t) = Generator(Fill);
        var cover = new byte[N * N];
        var content = new CubeLeafContent
        {
            Key = Fill,
            Heights = new CubeTile { Key = Fill, Bits = 9, Heights = g },
            Cover = cover,
            Trees = new() { Tree(t, 20, 256, g), Tree(t, 480, 256, g) },
            Buildings = new BuildingTile { Buildings = new() },
        };
        var ground = PyramidFill.Build(p, t, g, baseAt)!;
        PyramidFill.Apply(content, ground, t);
        Assert.Equal((byte)CoverClass.Water, content.Cover![256 * N + 20]);
        Assert.Equal((byte)CoverClass.Open, content.Cover[256 * N + 480]);
        Assert.NotNull(content.Water);
        Assert.Equal(Lake, content.Water!.Level[128 * content.Water.Side + 10]);
        Assert.True(float.IsNaN(content.Water.Level[128 * content.Water.Side + 240]));
        // the tree in the lake is gone; the other stands on the new ground
        var tree = Assert.Single(content.Trees);
        var (_, _, hEll) = t.LocalToLattice(tree.X, tree.Y, tree.Z);
        Assert.Equal(ground.Heights[256 * N + 480], hEll, 0.5);
    }

    private static TreeInstance Tree(LeafTransform t, int i, int j, float[] heights)
    {
        var (x, y, z) = t.LatticeToLocal(i, j, heights[j * N + i]);
        return new TreeInstance((float)x, (float)y, (float)z, 10, 0);
    }

    [Fact]
    public void On_the_pyramid_the_fill_meets_a_real_leaf_to_the_bit_and_its_lake_bed_stays_in_the_lake()
    {
        var p = Pyramid();
        // the real leaf east: the pyramid's ground plus a little relief of its own (it is what the pyramid was built from)
        var t = LeafFrames.Swiss.TransformOf(East);
        var real = new float[N * N];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
                real[j * N + i] = (float)(p.HeightAt(East.Face, Gi(East, i), Gj(East, j), 23) + 0.5 * Math.Sin(i * 0.3));
        var realTile = new CubeTile { Key = East, Bits = 9, Heights = real };
        var blend = new LeafBlend(k => k == East, k => k == East ? realTile : null, pyramid: p);
        var (g, baseAt, tf) = Generator(Fill);
        var fill = PyramidFill.Build(p, tf, g, baseAt)!;
        var r = blend.Heights(Fill, fill.Heights)!;
        for (int j = 0; j < N; j++)
            Assert.Equal(BitConverter.SingleToInt32Bits(real[j * N]), BitConverter.SingleToInt32Bits(r.Heights[j * N + N - 1]));
        // convex: between the fill and the real ground on the pyramid, give or take what carrying the real ripple's
        // slope over NearM adds (its 0.15 m a vertex fading over 60 m), never a trench or a hump past both
        for (int j = 0; j < N; j += 7)
            for (int i = 0; i < N - 1; i += 7)
            {
                double ph = p.HeightAt(Fill.Face, Gi(Fill, i), Gj(Fill, j), 23);
                float h = r.Heights[j * N + i], f = fill.Heights[j * N + i];
                Assert.InRange(h, Math.Min(f, ph - 0.5) - 4, Math.Max(f, ph + 0.5) + 4);
            }
        // the first fill vertex beside the real leaf continues its ground: no step at the seam
        for (int j = 8; j < N - 8; j += 16)
        {
            double carried = 2.0 * real[j * N] - real[j * N + 1];
            Assert.InRange(r.Heights[j * N + N - 2], carried - 0.05, carried + 0.05);
        }
        // the lake west of the shore keeps the fill's bed far from the real leaf
        Assert.Equal(fill.Heights[256 * N + 10], r.Heights[256 * N + 10], 2);
    }
}
