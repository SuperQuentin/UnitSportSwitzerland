using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The smoothed road profile, the joint town ground solve and the blend's reach fade (#1419).</summary>
public class TownGroundTests
{
    private static (double[] S, double[] Y) Line(int n, double step, Func<double, double> y)
    {
        var s = new double[n];
        var v = new double[n];
        for (int i = 0; i < n; i++) { s[i] = i * step; v[i] = y(s[i]); }
        return (s, v);
    }

    [Fact]
    public void Profile_keeps_its_ends_and_junctions_and_a_straight_grade()
    {
        var (s, y) = Line(41, 4, x => 500 + 0.05 * x + 0.3 * Math.Sin(x / 3.0));   // 160 m, a 5 % grade with hummocks
        var anchor = new bool[41];
        anchor[17] = true;
        var d = RoadProfile.Delta(s, y, 7.5, anchor);
        Assert.Equal(0, d[0]);
        Assert.Equal(0, d[^1]);
        Assert.Equal(0, d[17]);
        // the hummocks shrink: the profile's second differences drop
        double Rough(Func<int, double> f) => Enumerable.Range(1, 39).Sum(i => Math.Abs(f(i - 1) - 2 * f(i) + f(i + 1)));
        Assert.True(Rough(i => y[i] + d[i]) < 0.5 * Rough(i => y[i]));

        var (s2, straight) = Line(41, 4, x => 500 + 0.05 * x);
        foreach (double v in RoadProfile.Delta(s2, straight, 7.5)) Assert.InRange(v, -1e-9, 1e-9);
    }

    [Fact]
    public void Profile_change_is_capped()
    {
        // a 6 m spike at one point (a cliff lip under a path): lowered by no more than MaxLower
        var (s, y) = Line(21, 4, _ => 800);
        y[10] = 806;
        var d = RoadProfile.Delta(s, y, 4);
        Assert.Equal(-RoadProfile.MaxLower, d[10], 6);
        Assert.All(d, v => Assert.InRange(v, -RoadProfile.MaxLower, RoadProfile.MaxRaise));
        Assert.Equal(0, RoadProfile.SigmaFor(RoadClass.Watercourse, 0));
        Assert.Equal(0, RoadProfile.SigmaFor(RoadClass.Road, RoadFlags.Bridge));
        Assert.True(RoadProfile.SigmaFor(RoadClass.Motorway, 0) > RoadProfile.SigmaFor(RoadClass.Path, 0));
    }

    private const int Tile = 1000;

    /// <summary>
    /// A window of the global lattice whose origin is (x0, y0): a hummocky slope, a road along y = 500 (south of
    /// the row y = 494..506), a building block at 110 m over its own ground east of x = 1490.. Global, so two
    /// windows over the same cells read the same inputs.
    /// </summary>
    private static TownGround.Window World(int x0, int y0)
    {
        int m = TownGround.Margin, size = Tile + 1 + 2 * m;
        var win = new TownGround.Window(size);
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                int i = r * size + c, x = x0 + c, y = y0 + r;
                win.H0[i] = (float)(100 + 0.02 * y + 0.4 * Math.Sin(x * 0.7) * Math.Cos(y * 0.9));
                win.Kinds[i] = TownGround.Kind.Free;
                if (Math.Abs(y - 500) <= 6)
                {
                    win.Kinds[i] = TownGround.Kind.Road;
                    win.Fixed[i] = (float)(100 + 0.02 * 500 + 0.001 * x);
                    win.Write[i] = win.Fixed[i] - 0.35f;
                }
                else if (x >= 990 && x <= 1010 && y >= 515 && y <= 530)
                {
                    win.Kinds[i] = TownGround.Kind.Footprint;
                    win.H0[i] = 112;
                }
            }
        return win;
    }

    [Fact]
    public void Solve_holds_its_constraints_stays_between_them_and_leaves_the_country_alone()
    {
        int m = TownGround.Margin;
        var win = World(-m, -m);
        int size = win.Size;
        // town west of x = 1050, country past it
        var res = TownGround.Solve(win, i => (i % size) - m < 1050 ? 1 : 0);
        Assert.True(res.Free > 0);
        Assert.True(res.Residual < 0.01f);
        int At(int x, int y) => (y + m) * size + x + m;
        // a road cell is written at its visual height, a footprint keeps its own
        Assert.Equal(win.Write[At(300, 500)], res.Heights[At(300, 500)], 4);
        Assert.Equal(112f, res.Heights[At(1000, 520)]);
        // between the road (110.3..111.3) and the block (112) the fill is smooth: no lattice step over 0.3 m
        for (int y = 507; y < 515; y++)
            Assert.InRange(Math.Abs(res.Heights[At(1000, y + 1)] - res.Heights[At(1000, y)]), 0, 0.3);
        // the hummocks on the town's side of the road are gone; past x = 1050 untouched
        double Rough(int x0, int x1, float[] h) => Enumerable.Range(x0, x1 - x0).Sum(x => Math.Abs(h[At(x + 1, 505 + 3)] - h[At(x, 505 + 3)]));
        Assert.True(Rough(400, 600, res.Heights) < 0.3 * Rough(400, 600, win.H0));
        for (int x = 1060; x < 1150; x++) Assert.Equal(win.H0[At(x, 510)], res.Heights[At(x, 510)]);
        // more than the reach away, nothing moves
        Assert.Equal(win.H0[At(300, 560)], res.Heights[At(300, 560)]);
    }

    [Fact]
    public void Solve_gives_the_shared_border_the_same_heights_in_both_tiles()
    {
        int m = TownGround.Margin;
        // tile A covers x 0..1000, tile B x 1000..2000; the column x = 1000 is in both
        var a = TownGround.Solve(World(-m, -m), _ => 1);
        var b = TownGround.Solve(World(Tile - m, -m), _ => 1);
        int size = Tile + 1 + 2 * m;
        int changed = 0;
        for (int y = 0; y <= Tile; y++)
        {
            float ha = a.Heights[(y + m) * size + Tile + m], hb = b.Heights[(y + m) * size + m];
            Assert.Equal(ha, hb);
            if (Math.Abs(ha - World(-m, -m).H0[(y + m) * size + Tile + m]) > 1e-4) changed++;
        }
        Assert.True(changed > 0);   // the border runs through the solved ground, not around it
    }

    [Fact]
    public void Blend_fades_its_clamp_over_the_outer_reach()
    {
        Assert.Equal(1, TerrainMeshBuilder.ReachWeight(0, RoadEmbankment.RoadReachM));
        Assert.Equal(1, TerrainMeshBuilder.ReachWeight(RoadEmbankment.RoadReachM - TerrainMeshBuilder.ReachFadeM, RoadEmbankment.RoadReachM));
        Assert.Equal(0, TerrainMeshBuilder.ReachWeight(RoadEmbankment.RoadReachM, RoadEmbankment.RoadReachM), 9);
        double mid = TerrainMeshBuilder.ReachWeight(RoadEmbankment.MinorReachM - 0.75, RoadEmbankment.MinorReachM);
        Assert.InRange(mid, 0.45, 0.55);
    }
}
