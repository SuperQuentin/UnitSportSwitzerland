using UnitSport.Tools.RoadGen.Rewrite;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The lanes across a signalised approach (#351, <see cref="TileRewriter.ApproachLayout"/>): the one
/// place their offsets are worked out. Both right-pocket layouts take the same width, only the order
/// of the pocket and the painted bike lane differs; the lanes always tile the widened half.
/// </summary>
public class ApproachLayoutTests
{
    private static (double, double) L(TileRewriter.ApproachLayout.Lane? lane) => (Math.Round(lane!.Value.From, 6), Math.Round(lane.Value.To, 6));

    [Fact]
    public void LeftAndRightPockets_KerbsideOrBetween_SameWidth()
    {
        // a 9 m street with 1.5 m bike lanes: 3 m of car lane each side, no extra; the left pocket's
        // widening adds the through lane and the left-turn bike lane (4.5 m)
        var a = new TileRewriter.ApproachLayout(Half: 4.5, Bike: 1.5, LeftPocket: 3.0, LeftBike: 1.5, LeftFull: 4.5, Right: true, RightExtra: 0);
        var b = a with { BikeBetween = true };
        foreach (var x in new[] { a, b })
        {
            Assert.Equal((0.0, 3.0), L(x.LeftPocketLane));
            Assert.Equal((3.0, 4.5), L(x.LeftBikeLane));
            Assert.Equal((4.5, 7.5), L(x.Through()));
            Assert.Equal(12.0, x.Edge(), 6);
        }
        // (a) kerbside: the pocket, then the bike lane; (b) between: the bike lane, then the pocket
        Assert.Equal((7.5, 10.5), L(a.RightPocket()));
        Assert.Equal((10.5, 12.0), L(a.BikeLane()));
        Assert.True(a.KerbsideBike);
        Assert.Equal((7.5, 9.0), L(b.BikeLane()));
        Assert.Equal((9.0, 12.0), L(b.RightPocket()));
        Assert.False(b.KerbsideBike);
    }

    [Fact]
    public void RightPocketAlone_WidensTheNarrowLane_AndOpensOverItsTaper()
    {
        // a 7 m street with 1.5 m bike lanes: 2 m of car lane, made a full 3 m beside the bike lane
        var a = new TileRewriter.ApproachLayout(Half: 3.5, Bike: 1.5, LeftPocket: 0, LeftBike: 0, LeftFull: 0, Right: true, RightExtra: 1.0);
        var b = a with { BikeBetween = true };
        Assert.Null(a.LeftPocketLane);
        Assert.Equal((0.0, 3.0), L(a.Through()));
        Assert.Equal((3.0, 6.0), L(a.RightPocket()));
        Assert.Equal((6.0, 7.5), L(a.BikeLane()));
        Assert.Equal((3.0, 4.5), L(b.BikeLane()));
        Assert.Equal((4.5, 7.5), L(b.RightPocket()));
        Assert.Equal(7.5, a.Edge(), 6);
        // before the taper both are the street as it was
        foreach (var x in new[] { a, b })
        {
            Assert.Equal((0.0, 2.0), L(x.Through(0)));
            Assert.Equal((2.0, 3.5), L(x.BikeLane(0)));
            Assert.Equal(3.5, x.Edge(0), 6);
        }
    }
}
