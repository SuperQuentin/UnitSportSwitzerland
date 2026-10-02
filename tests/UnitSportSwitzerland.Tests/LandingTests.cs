using UnitSport.Player;
using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The pier planner and the landings file (#377).</summary>
public class LandingTests
{
    private const double Water = 372.14;

    /// <summary>A straight shore along N at E = 0: land west of it (a quay 1.3 m over the water), a bed falling east.</summary>
    private sealed class Shore(double slope, double maxDepth) : IShoreSampler
    {
        public double Ground(double e, double n) => e < 0 ? Water + 1.3 : Water - Math.Min(maxDepth, 0.3 + e * slope);
        public double Level(double e, double n) => e < 0 ? double.NaN : Water;
    }

    private static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));

    [Fact]
    public void A_landing_gets_a_head_at_the_plank_s_height_a_neck_to_the_quay_and_a_berth_along_the_shore()
    {
        var shore = new Shore(0.1, 8);
        var landing = LandingPlanner.PlanLanding("Test (lac)", 40, 0, shore);
        Assert.NotNull(landing);
        var berth = landing!.Berth!;
        Assert.True(berth.Fits);
        Assert.True(berth.Depth >= LandingPlanner.Default.HullNeeds);
        // along the shore (north or south), offshore of the stop, the pier to port
        Assert.True(Math.Abs(Math.Sin(berth.Heading * Math.PI / 180)) < 0.05, $"heading {berth.Heading}");
        Assert.True(berth.E > 40);
        Assert.Equal(0, berth.Side);
        Assert.Equal(Water + LandingPlanner.Default.DeckOverWater, berth.Deck, 3);

        var head = landing.Ribbons.Single(r => !r.Rails);
        var neck = landing.Ribbons.Single(r => r.Rails);
        Assert.All(head.Points, p => Assert.Equal(berth.Deck, p[2], 3));
        // the neck starts on the head's back edge at its deck, and ends ashore buried 2 cm
        Assert.Equal(berth.Deck, neck.Points[0][2], 3);
        var end = neck.Points[^1];
        Assert.True(end[0] < 0, $"the neck ends ashore ({end[0]:F1})");
        Assert.Equal(shore.Ground(end[0], end[1]) - 0.02, end[2], 3);
        for (int i = 1; i < neck.Points.Count; i++)
        {
            double rise = Math.Abs(neck.Points[i][2] - neck.Points[i - 1][2]) / Dist(neck.Points[i], neck.Points[i - 1]);
            Assert.True(rise <= LandingPlanner.Default.MaxRamp + 1e-3, $"ramp {rise:F3} at {i}");
            // never under the ground it crosses (but the buried end)
            if (i < neck.Points.Count - 1) Assert.True(neck.Points[i][2] >= shore.Ground(neck.Points[i][0], neck.Points[i][1]));
        }
        // the face is FaceOffset from the ship's centreline: the head's outer edge
        double faceE = head.Points.Max(p => p[0]) + head.Width * 0.5;
        Assert.Equal(berth.E - LandingPlanner.Default.FaceOffset, faceE, 1);
    }

    [Fact]
    public void A_shallow_shelf_moves_the_head_out_until_the_hull_floats()
    {
        var shore = new Shore(0.02, 6);   // 2.28 m only 100 m out
        var landing = LandingPlanner.PlanLanding("Shelf", 30, 0, shore)!;
        Assert.True(landing.Berth!.Fits);
        Assert.True(landing.Berth.Depth >= LandingPlanner.Default.HullNeeds);
        Assert.True(landing.Berth.E - LandingPlanner.Default.HullHalfBeam >= (LandingPlanner.Default.HullNeeds - 0.3) / 0.02 - 1);
    }

    [Fact]
    public void Too_shallow_everywhere_the_berth_stays_where_the_boats_lie_and_says_so()
    {
        var shore = new Shore(0.02, 1.5);
        var landing = LandingPlanner.PlanLanding("Shallow", 40, 0, shore)!;
        Assert.False(landing.Berth!.Fits);
        Assert.True(landing.Berth.Depth < LandingPlanner.Default.HullNeeds);
        Assert.True(landing.Berth.E < 40 + 12);
    }

    [Fact]
    public void A_stop_with_no_shore_within_reach_has_no_pier()
    {
        var lake = new Shore(0.1, 8);
        Assert.Null(LandingPlanner.PlanLanding("Mid-lake", 5000, 0, lake));
    }

    [Fact]
    public void A_jetty_stands_over_the_water_and_ramps_onto_the_quay()
    {
        var shore = new Shore(0.05, 4);
        var jetty = LandingPlanner.PlanJetty("j", new List<(double, double, double)> { (-6, 0, Water + 0.1), (30, 0, Water + 0.1) }, shore)!;
        var pts = jetty.Ribbon.Points;
        Assert.Equal(PierKind.Jetty, jetty.Ribbon.Kind);
        Assert.All(pts.Where(p => p[0] > 8), p => Assert.Equal(Water + LandingPlanner.Default.JettyOverWater, p[2], 3));
        Assert.Equal(Water + 1.3 - 0.02, pts[0][2], 3);
        for (int i = 1; i < pts.Count; i++)
            Assert.True(Math.Abs(pts[i][2] - pts[i - 1][2]) / Dist(pts[i], pts[i - 1]) <= LandingPlanner.Default.MaxRamp + 1e-3);
        // all dry: no jetty
        Assert.Null(LandingPlanner.PlanJetty("dry", new List<(double, double, double)> { (-60, 0, 0), (-20, 0, 0) }, shore));
    }

    [Fact]
    public void The_berth_s_numbers_are_the_steamer_s()
    {
        var o = LandingPlanner.Default;
        Assert.Equal(SteamerLines.Beam * 0.5, o.HullHalfBeam, 3);
        Assert.Equal(SteamerLines.WaterlineHalf, o.HullHalfLength, 3);
        Assert.Equal(SteamerLines.Draught + 0.6, o.HullNeeds, 3);
        // the plank's foot (main deck 3.0 over the keel, down 0.3) of a keel floating 1.64 m deep, plus 2 cm
        Assert.Equal(SteamerLines.Depth - 0.3 - (SteamerLines.Draught - 0.04) + 0.02, o.DeckOverWater, 3);
        // the head starts clear of the paddle box (the wheels 1.5 m forward of the centre, the box 4.75 m either side)
        Assert.True(o.HeadAft > 4.75 - SteamerLines.WheelZ);
    }

    [Fact]
    public void The_file_round_trips_and_each_ribbon_is_built_by_one_tile()
    {
        var index = new LandingIndex();
        index.Landings.Add(LandingPlanner.PlanLanding("Test (lac)", 2508040, 1137000, new Offset(2508000))!);
        index.Jetties.Add(LandingPlanner.PlanJetty("j", new List<(double, double, double)> { (2507995, 1137500, 0), (2508030, 1137500, 0) }, new Offset(2508000))!);
        var back = LandingIndex.FromJson(index.ToJson());
        Assert.Equal(index.ToJson(), back.ToJson());
        Assert.Equal("Test (lac)", back.Find("Test")!.Name);
        Assert.Same(back.Landings[0], back.Nearest(2508000, 1137000));
        var tiles = new[] { new TileId(2507, 1136), new TileId(2507, 1137), new TileId(2508, 1136), new TileId(2508, 1137) };
        foreach (var r in back.Ribbons())
            Assert.Equal(1, tiles.Count(t => back.RibbonsOf(t).Contains(r)));
    }

    /// <summary>The straight shore at E = <paramref name="e0"/>.</summary>
    private sealed class Offset(double e0) : IShoreSampler
    {
        private readonly Shore _shore = new(0.1, 8);
        public double Ground(double e, double n) => _shore.Ground(e - e0, n);
        public double Level(double e, double n) => _shore.Level(e - e0, n);
    }

    [Fact]
    public async Task The_fixture_lake_has_a_landing_that_floats_the_steamer_and_a_jetty()
    {
        var source = FixtureChunkSource.Create("lake", 2583300, 1113200)!;
        var index = await source.LoadLandingsAsync();
        Assert.NotNull(index);
        var landing = index!.Find(Lake.LandingName)!;
        Assert.True(landing.Berth!.Fits);
        Assert.Single(index.Jetties);
    }
}
