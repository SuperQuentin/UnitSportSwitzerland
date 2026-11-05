using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The generated fill's domain in cube cells (#1509, G2 B5): discs on the Earth, growing only.</summary>
public class LeafFillDomainTests
{
    private const double RiddesLat = 46.17, RiddesLon = 7.22;

    [Fact]
    public void The_spawn_s_leaf_is_in_and_a_leaf_50_km_away_is_not()
    {
        var d = LeafFillDomain.At(RiddesLat, RiddesLon);
        Assert.True(d.Contains(LeafFrames.LeafAt(RiddesLat, RiddesLon)));
        Assert.True(d.Contains(RiddesLat + 0.3, RiddesLon));     // 33 km north
        Assert.False(d.Contains(RiddesLat + 0.45, RiddesLon));   // 50 km north
    }

    [Fact]
    public void Reaching_adds_a_disc_and_never_drops_one()
    {
        var d = LeafFillDomain.At(RiddesLat, RiddesLon);
        Assert.Same(d, d.Reach(RiddesLat + 0.1, RiddesLon));
        var geneva = d.Reach(46.2, 6.14);
        Assert.Equal(2, geneva.Discs.Count);
        Assert.True(geneva.Contains(46.2, 6.14));
        Assert.True(geneva.Contains(RiddesLat, RiddesLon));
    }

    [Fact]
    public void The_real_disc_holds_the_real_leaves_and_only_grows()
    {
        var real = new[] { LeafFrames.LeafAt(46.5, 8.0), LeafFrames.LeafAt(46.6, 8.2) };
        var d = LeafFillDomain.At(RiddesLat, RiddesLon).WithReal(real);
        Assert.All(real, k => Assert.True(d.Contains(k)));
        Assert.True(d.Contains(46.55, 8.1 + 0.4));               // 30 km past the real ones
        var smaller = d.WithReal([LeafFrames.LeafAt(46.5, 8.0)]);
        Assert.True(smaller.Contains(LeafFrames.LeafAt(46.6, 8.2)));
    }

    [Fact]
    public void Leaves_round_a_place_cross_a_face_edge()
    {
        // east from Bern (face 0's centre) to the first face edge
        double lon = 7.44;
        while (LeafFrames.LeafAt(46.95, lon + 0.01).Face == LeafFrames.LeafAt(46.95, 7.44).Face) lon += 0.01;
        var d = LeafFillDomain.At(46.95, lon);
        var leaves = d.LeavesAround(46.95, lon, 1500).ToList();
        Assert.Contains(leaves, k => k.Face != leaves[0].Face);
        Assert.All(leaves, k => Assert.True(d.Contains(k)));
        Assert.InRange(leaves.Count, 10, 60);
    }
}
