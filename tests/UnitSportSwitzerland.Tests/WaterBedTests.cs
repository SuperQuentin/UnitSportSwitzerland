using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The synthetic bed rules and the exact distance transform the water pass uses (#298).</summary>
public class WaterBedTests
{
    [Fact]
    public void Open_water_is_a_shelf_then_a_drop_off_down_to_the_body_maximum()
    {
        Assert.Equal(0, WaterBed.OpenDepth(0, 30));
        Assert.Equal(1.0, WaterBed.OpenDepth(12, 30), 9);
        Assert.Equal(2.0, WaterBed.OpenDepth(WaterBed.ShelfWidthM, 30), 9);
        Assert.Equal(2.0 + 4 * WaterBed.DropSlope, WaterBed.OpenDepth(WaterBed.ShelfWidthM + 4, 30), 9);
        Assert.Equal(30, WaterBed.OpenDepth(5000, 30));
        // the profile only ever deepens away from the shore
        double last = 0;
        for (double d = 0; d < 400; d += 0.5)
        {
            double v = WaterBed.Depth(d, double.PositiveInfinity, 40);
            Assert.True(v >= last - 1e-12);
            last = v;
        }
    }

    [Fact]
    public void A_channel_is_deepest_in_the_middle_and_never_deeper_than_open_water()
    {
        const double half = 25;   // the Rhône's 50 m
        double middle = WaterBed.Depth(half, half, 20);
        Assert.True(middle > WaterBed.Depth(half / 2, half, 20));
        Assert.True(middle <= WaterBed.ChannelDepth(2 * half) + 1e-9);
        for (double d = 0; d <= half; d += 1)
            Assert.True(WaterBed.Depth(d, half, 20) <= WaterBed.OpenDepth(d, 20) + 1e-9);
        // flush with the bank
        Assert.Equal(0, WaterBed.Depth(0, half, 20), 9);
    }

    [Fact]
    public void Widening_water_blends_from_channel_to_open_without_a_step()
    {
        // the same point 30 m from the shore while the water widens past OpenWidthM
        double prev = WaterBed.Depth(30, WaterBed.ChannelWidthM / 2 - 1, 50);
        for (double w = WaterBed.ChannelWidthM - 2; w <= WaterBed.OpenWidthM + 4; w += 0.25)
        {
            double v = WaterBed.Depth(30, w / 2, 50);
            Assert.True(Math.Abs(v - prev) < 0.2, $"step at width {w}: {prev} -> {v}");
            prev = v;
        }
        Assert.Equal(WaterBed.OpenDepth(30, 50), WaterBed.Depth(30, WaterBed.OpenWidthM / 2, 50), 9);
    }

    [Fact]
    public void Max_depth_grows_with_area_within_bounds()
    {
        Assert.Equal(WaterBed.MinMaxDepthM, WaterBed.MaxDepthForArea(0));
        Assert.InRange(WaterBed.MaxDepthForArea(500 * 500), 10, 15);
        Assert.True(WaterBed.MaxDepthForArea(10e6) > WaterBed.MaxDepthForArea(1e6));
        Assert.Equal(WaterBed.MaxMaxDepthM, WaterBed.MaxDepthForArea(1e15));
    }

    [Fact]
    public void Distance_transform_matches_brute_force()
    {
        const int w = 37, h = 23;
        var rng = new Random(298);
        var sites = new bool[w * h];
        for (int i = 0; i < sites.Length; i++) sites[i] = rng.NextDouble() < 0.03;
        var dist = new float[w * h];
        var site = new int[w * h];
        DistanceTransform.Run(sites, w, h, dist, site);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double best = double.PositiveInfinity;
                for (int j = 0; j < sites.Length; j++)
                    if (sites[j])
                        best = Math.Min(best, Math.Sqrt(Math.Pow(j % w - x, 2) + Math.Pow(j / w - y, 2)));
                Assert.Equal(best, dist[y * w + x], 4);
                int s = site[y * w + x];
                Assert.True(sites[s]);
                Assert.Equal(best, Math.Sqrt(Math.Pow(s % w - x, 2) + Math.Pow(s / w - y, 2)), 4);
            }
    }

    [Fact]
    public void Distance_transform_without_sites_is_infinite()
    {
        var dist = new float[12];
        var site = new int[12];
        DistanceTransform.Run(new bool[12], 4, 3, dist, site);
        Assert.All(dist, d => Assert.True(float.IsPositiveInfinity(d)));
        Assert.All(site, s => Assert.Equal(-1, s));
    }
}
