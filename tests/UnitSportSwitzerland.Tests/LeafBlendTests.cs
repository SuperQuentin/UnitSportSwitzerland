using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The fill blended into real leaves (#1510, G2 C1 PR 3): <see cref="RealLeaf"/>'s real cells and <see cref="LeafBlend"/>
/// on synthetic leaves near Riddes (the worst case of the flat world's gotcha: steep real ground against a flat fill).
/// The region's own border is BlendCheck's <c>--leaf-blend</c>.
/// </summary>
public class LeafBlendTests
{
    private static readonly CubeKey Fill = LeafFrames.LeafAt(46.17, 7.22);
    private static readonly CubeKey East = Fill.Neighbour(1, 0)!.Value;
    private const int N = 513;

    /// <summary>A leaf's heights from a function of its lattice point, above sea level (NaN: none).</summary>
    private static CubeTile Leaf(CubeKey key, Func<int, int, double> hSea)
    {
        var t = LeafFrames.Swiss.TransformOf(key);
        var h = new float[N * N];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                double s = hSea(i, j);
                h[j * N + i] = double.IsNaN(s) ? float.NaN : (float)(s + t.UndulationAt(i, j));
            }
        return new CubeTile { Key = key, Bits = 9, Heights = h };
    }

    /// <summary>Real ground: 1,400 m with a 20 % slope rising east and a ripple on it.</summary>
    private static double Steep(int i, int j) => 1400 + 0.2 * 1.19 * i + 3 * Math.Sin(i * 0.05) * Math.Cos(j * 0.07);

    [Fact]
    public void A_half_real_leaf_keeps_whole_real_cells_and_hands_the_rest_to_the_fill()
    {
        // ground west of vertex 300, none east of it (a source's edge through the leaf)
        var tile = Leaf(East, (i, j) => i <= 300 ? Steep(i, j) : double.NaN);
        var real = RealLeaf.Of(tile, LeafFrames.Swiss.TransformOf(East))!;
        Assert.False(real.Full);
        Assert.NotEmpty(real.Blocks);
        Assert.All(real.Blocks, b => Assert.True(b.I0 + b.Size <= 300));
        Assert.True(real.Kept(0, 0));
        Assert.True(real.Kept(296, 400));     // the last whole cell's edge
        Assert.False(real.Kept(299, 400));    // real, but under a cell that is not
        Assert.False(real.Kept(400, 400));
    }

    [Fact]
    public void The_fill_meets_a_real_leaf_on_their_shared_edge_to_the_bit_and_stays_between_both_grounds()
    {
        var realTile = Leaf(East, Steep);
        var blend = new LeafBlend(k => k == East, k => k == East ? realTile : null);
        // the fill: flat at 500 m, 900 m under the real ground
        var fill = Leaf(Fill, (_, _) => 500);
        var r = blend.Heights(Fill, fill.Heights)!;
        Assert.Null(r.Kept);
        // the fill's east edge is the real leaf's west edge
        for (int j = 0; j < N; j++)
            Assert.Equal(BitConverter.SingleToInt32Bits(realTile.Heights[j * N]), BitConverter.SingleToInt32Bits(r.Heights[j * N + N - 1]));
        // convex: between the generator's and the real ground everywhere
        float realMax = realTile.Heights.Max();
        for (int k = 0; k < r.Heights.Length; k++)
            Assert.InRange(r.Heights[k], fill.Heights[k] - 0.01f, realMax + 0.01f);
        // it rises toward the real leaf along a row
        int mid = N / 2;
        Assert.True(r.Heights[mid * N + N - 2] > r.Heights[mid * N + N / 2]);
        Assert.True(r.Heights[mid * N + N / 2] > r.Heights[mid * N]);
    }

    [Fact]
    public void No_step_at_the_seam_the_first_fill_vertex_continues_the_real_slope()
    {
        var realTile = Leaf(East, Steep);
        var blend = new LeafBlend(k => k == East, k => k == East ? realTile : null);
        var r = blend.Heights(Fill, Leaf(Fill, (_, _) => 500).Heights)!;
        for (int j = 8; j < N - 8; j += 16)
        {
            // the real surface carried one step west of the shared edge
            double carried = 2.0 * realTile.Heights[j * N] - realTile.Heights[j * N + 1];
            Assert.InRange(r.Heights[j * N + N - 2] - carried, -0.05, 0.05);
        }
    }

    [Fact]
    public void A_part_real_leaf_keeps_its_real_cells_to_the_bit_and_fills_the_rest()
    {
        var realTile = Leaf(East, (i, j) => i <= 300 ? Steep(i, j) : double.NaN);
        var blend = new LeafBlend(k => k == East, k => k == East ? realTile : null);
        var generated = Leaf(East, (_, _) => 500);
        var r = blend.Heights(East, generated.Heights, realTile.Heights)!;
        var real = blend.Summary(East)!;
        Assert.NotNull(r.Kept);
        for (int j = 0; j < N; j += 7)
            for (int i = 0; i < N; i += 3)
            {
                int k = j * N + i;
                Assert.False(float.IsNaN(r.Heights[k]));
                Assert.Equal(real.Kept(i, j), r.Kept![k]);
                if (r.Kept[k]) Assert.Equal(BitConverter.SingleToInt32Bits(realTile.Heights[k]), BitConverter.SingleToInt32Bits(r.Heights[k]));
            }
    }

    [Fact]
    public void Far_from_real_ground_there_is_nothing_to_blend()
    {
        var far = LeafFrames.LeafAt(46.6, 7.22);   // ~48 km north
        var blend = new LeafBlend(k => k == East, k => k == East ? Leaf(East, Steep) : null);
        Assert.False(blend.Reaches(far));
        Assert.Null(blend.Heights(far, Leaf(far, (_, _) => 500).Heights));
    }
}
