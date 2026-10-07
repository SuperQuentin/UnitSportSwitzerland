using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.TestRegion;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Intersections laid out from lane data (#700), on the test region's J6 (a 2+2 artery whose OSM
/// <c>turn:lanes</c> are assigned to its own lanes) and J7 (<c>lanes:forward=3</c> on the way before the
/// lights: a double left pocket; the east way marks no left lane: no pocket).
/// </summary>
public class LaneDataRegionTests(SignalTestRegionFixture region) : IClassFixture<SignalTestRegionFixture>
{
    private RoadApproach Approach(string junction, bool east, bool west = false, bool north = false, bool south = false)
    {
        var j = region.Junction(junction);
        var tile = region.Tile(j.E, j.N);
        int k = tile.Signals.Select((s, i) => (s, i)).MinBy(x =>
            Math.Pow(TileId.FromLv95(j.E, j.N).MinE + x.s.X - j.E, 2) + Math.Pow(TileId.FromLv95(j.E, j.N).MaxN - x.s.Z - j.N, 2)).i;
        return tile.Approaches.Single(a => a.Signal == k
            && (east ? Math.Cos(a.Heading) > 0.95 : west ? Math.Cos(a.Heading) < -0.95 : north ? Math.Sin(a.Heading) > 0.95 : Math.Sin(a.Heading) < -0.95));
    }

    private static string Cars(RoadApproach a) =>
        string.Join("|", a.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).Select(l =>
            (l.Moves.HasFlag(SignalMoves.Left) ? "L" : "") + (l.Moves.HasFlag(SignalMoves.Through) ? "T" : "") + (l.Moves.HasFlag(SignalMoves.Right) ? "R" : "")));

    [Fact]
    public void The_artery_is_twelve_metres_and_its_lanes_are_assigned_in_place()
    {
        var j = region.Junction("J6");
        var tile = region.Tile(j.E, j.N);
        // 2 + 2 lanes: 4 x 3 m
        Assert.Contains(tile.Segments, s => s.Class == RoadClass.Major && Math.Abs(s.Width - 12f) < 0.01f);

        foreach (var arm in new[] { Approach("J6", east: false, west: true), Approach("J6", east: true) })
        {
            Assert.Equal("L|TR", Cars(arm));
            var cars = arm.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).ToList();
            // the carriageway's own lanes, a lane's width apart, there all along: nothing widened
            Assert.Equal(3f, cars[1].Offset - cars[0].Offset, 2);
            Assert.All(cars, l => Assert.True(l.TaperFrom >= l.FullFrom));
        }
        // the data's arrows on them: a left arrow in the left lane and a straight + right in the other
        var variants = tile.Paint.Where(p => p.Type == PaintType.Arrow).Select(p => (PaintArrow)p.Variant).ToHashSet();
        Assert.Contains(PaintArrow.Left, variants);
        Assert.Contains(PaintArrow.Straight | PaintArrow.Right, variants);
    }

    [Fact]
    public void The_cross_road_gets_its_pocket_from_data()
    {
        Assert.Equal("L|TR", Cars(Approach("J6", east: false, south: true)));
        Assert.Equal("L|TR", Cars(Approach("J6", east: false, north: true)));
    }

    [Fact]
    public void A_double_left_pocket_from_lanes_forward_3_and_the_turn_lanes()
    {
        var west = Approach("J7", east: false, west: true);
        Assert.Equal("L|L|TR", Cars(west));
        var cars = west.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).ToList();
        // two pocket lanes of one lane width, the left-turn bike lane between them and the through lane
        float lane = cars[1].Offset - cars[0].Offset;
        Assert.InRange(lane, 2.8f, 4.2f);
        Assert.True(cars[2].Offset - cars[1].Offset >= lane - 0.01f);
        Assert.True(cars[0].Offset < cars[1].Offset);
        // the pocket lanes appear together: a lane change out of the through lane, not two
        Assert.All(cars.Take(2), l => Assert.Equal(SignalMoves.Left, l.Moves));
    }

    [Fact]
    public void No_left_pocket_where_the_data_marks_no_left_lane()
    {
        // a road leaves to the left of the east approach, but its way says through | through;right
        Assert.Equal("TR", Cars(Approach("J7", east: true)));
    }
}
