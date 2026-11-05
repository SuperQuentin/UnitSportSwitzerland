using System;
using System.Linq;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The water pass's window maths in metres (#1507, G2 B3 PR 6): a count of cells is the LV95 window's metres as they
/// are and a leaf lattice's rounded from its cell size, distances scale with it, and the bank refine keeps the lowest
/// wet surface round a vertex.
/// </summary>
public class WaterWindowTests
{
    [Fact]
    public void A_length_in_cells_is_the_metres_on_the_lv95_grid_and_rounded_on_a_leaf_lattice()
    {
        Assert.Equal(40, WaterWindow.Cells(WaterWindow.EdgeSmoothM, 1));
        Assert.Equal(600, WaterWindow.Cells(WaterWindow.Halo, 1));
        Assert.Equal(34, WaterWindow.Cells(WaterWindow.EdgeSmoothM, 1.194));
        Assert.Equal(503, WaterWindow.Cells(WaterWindow.Halo, 1.194));
        Assert.Equal(1, WaterWindow.Cells(0.2, 1.194));
    }

    [Fact]
    public void Distances_are_in_metres_whatever_the_cell()
    {
        const int w = 21;
        var site = new bool[w * w];
        site[10 * w + 10] = true;
        var one = WaterWindow.Distances(site, w, 1);
        var leaf = WaterWindow.Distances(site, w, 1.25);
        Assert.Equal(5, one[10 * w + 15], 5);
        Assert.Equal(5 * 1.25, leaf[10 * w + 15], 5);
        Assert.Equal(Math.Sqrt(18) * 1.25, leaf[13 * w + 13], 4);
    }

    [Fact]
    public void A_wet_cell_standing_above_the_water_round_it_is_bank()
    {
        const int w = 9;
        var state = Enumerable.Repeat((byte)1, w * w).ToArray();
        var q = Enumerable.Repeat(1000, w * w).ToArray();
        q[4 * w + 4] = 1060;   // 0.6 m up in centimetres
        q[4 * w + 5] = 1040;   // 0.4 m: still water
        WaterWindow.RefineWet(state, q, w, WaterWindow.Cells(WaterWindow.BankProbeM, 1.2), 50);
        Assert.Equal(0, state[4 * w + 4]);
        Assert.Equal(1, state[4 * w + 5]);
        Assert.Equal(w * w - 1, state.Count(s => s == 1));
    }

    // ---- the vectorised loops against plain ones (#1566): widths that are not a multiple of any vector, empty rows, edges ----

    private static (byte[] State, int[] Surface) RandomWindow(int w, int seed, double wetShare)
    {
        var rnd = new Random(seed);
        var state = new byte[w * w];
        var q = new int[w * w];
        for (int i = 0; i < state.Length; i++)
        {
            // a few rows with no water at all, the rest speckled
            if ((i / w) % 7 == 3) continue;
            state[i] = rnd.NextDouble() < wetShare ? (byte)1 : rnd.NextDouble() < 0.3 ? (byte)2 : (byte)0;
            q[i] = 1000 + rnd.Next(0, 40);
        }
        return (state, q);
    }

    [Theory]
    [InlineData(5, 1, 0.5)]
    [InlineData(37, 2, 0.6)]
    [InlineData(61, 3, 0.9)]
    [InlineData(100, 4, 0.02)]
    [InlineData(64, 5, 1.0)]
    public void RefineWet_is_the_square_minimum_taken_cell_by_cell(int w, int seed, double wetShare)
    {
        const int probe = 4, rise = 7;
        var (state, q) = RandomWindow(w, seed, wetShare);
        var expected = (byte[])state.Clone();
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                if (state[y * w + x] != 1) continue;
                int min = int.MaxValue;
                for (int dy = Math.Max(0, y - probe); dy <= Math.Min(w - 1, y + probe); dy++)
                    for (int dx = Math.Max(0, x - probe); dx <= Math.Min(w - 1, x + probe); dx++)
                        if (state[dy * w + dx] == 1) min = Math.Min(min, q[dy * w + dx]);
                if (q[y * w + x] > (long)min + rise) expected[y * w + x] = 0;
            }
        WaterWindow.RefineWet(state, q, w, probe, rise, new WaterWindow.Pool());
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(33)]
    [InlineData(131)]
    public void MinOfRows_is_the_column_minimum(int w)
    {
        var rnd = new Random(w);
        var rows = Enumerable.Range(0, w * 9).Select(_ => rnd.Next(-1000, 1000)).ToArray();
        var min = new int[w];
        WaterWindow.MinOfRows(rows, w, 2, 8, min);
        for (int x = 0; x < w; x++)
            Assert.Equal(Enumerable.Range(2, 7).Min(y => rows[y * w + x]), min[x]);
    }

    [Theory]
    [InlineData(5, 5, 0.3)]
    [InlineData(33, 40, 0.1)]
    [InlineData(70, 65, 0.5)]
    [InlineData(129, 31, 0.02)]
    [InlineData(64, 64, 1.0)]
    public void Distances_are_the_nearest_site_found_the_slow_way(int w, int h, double siteShare)
    {
        var rnd = new Random(w * 31 + h);
        var sites = new bool[w * h];
        for (int i = 0; i < sites.Length; i++) sites[i] = (i / w) % 5 == 2 ? true : rnd.NextDouble() < siteShare;   // every fifth row all sites
        if (siteShare < 0.05) for (int i = 0; i < w * 3; i++) sites[i] = false;   // and some rows empty of them
        var d = new float[w * h];
        var site = new int[w * h];
        DistanceTransform.Run(sites, w, h, d, site, new WaterWindow.Pool());
        var list = Enumerable.Range(0, w * h).Where(i => sites[i]).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (list.Length == 0) { Assert.True(float.IsPositiveInfinity(d[i])); Assert.Equal(-1, site[i]); continue; }
                long best = list.Min(s => (long)(s % w - x) * (s % w - x) + (long)(s / w - y) * (s / w - y));
                Assert.Equal((float)Math.Sqrt(best), d[i]);
                int sx = site[i] % w, sy = site[i] / w;
                Assert.True(sites[site[i]]);
                Assert.Equal(best, (long)(sx - x) * (sx - x) + (long)(sy - y) * (sy - y));
            }
    }

    [Theory]
    [InlineData(40, 5, 0.0)]
    [InlineData(40, 5, 0.01)]
    [InlineData(57, 9, 0.3)]
    [InlineData(32, 40, 0.5)]
    [InlineData(31, 3, 1.0)]
    public void BoxMean_is_the_mean_of_the_defined_cells_in_the_box(int w, int r, double definedShare)
    {
        var rnd = new Random(w + r);
        var f = new float[w * w];
        for (int i = 0; i < f.Length; i++)
            f[i] = rnd.NextDouble() < definedShare && (i % w) is > 3 and < 50 ? (float)(rnd.NextDouble() * 80 - 10) : float.NaN;   // the defined cells in a band: the rectangle they sit in is smaller than the window
        var got = WaterWindow.BoxMean(f, w, r, new WaterWindow.Pool());
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                if (float.IsNaN(f[y * w + x])) { Assert.True(float.IsNaN(got[y * w + x])); continue; }
                long s = 0;
                int n = 0;
                for (int yy = Math.Max(0, y - r); yy <= Math.Min(w - 1, y + r); yy++)
                    for (int xx = Math.Max(0, x - r); xx <= Math.Min(w - 1, x + r); xx++)
                        if (!float.IsNaN(f[yy * w + xx])) { s += (long)Math.Round(f[yy * w + xx] / 1e-4); n++; }
                Assert.Equal((float)(s * 1e-4 / n), got[y * w + x]);
            }
    }
}

