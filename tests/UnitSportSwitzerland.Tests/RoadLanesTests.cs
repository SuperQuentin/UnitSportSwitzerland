using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Roads carry their lanes (#700): width from lanes, the shared two-way lane layout, bike lanes beside several lanes.</summary>
public class RoadLanesTests
{
    private static readonly TileId Tile = new(2600, 1200);

    private static CrossSectionPlanner.Line Line(RoadClass cls, RoadAttributes a, float classWidth)
    {
        var pts = new float[3 * 3];
        for (int i = 0; i < 3; i++) (pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]) = (100 + 100 * i, 400f, 500f);
        return new CrossSectionPlanner.Line
        {
            Tile = Tile, Write = true,
            Segment = new RoadSegment { Class = cls, Surface = RoadSurface.Paved, Width = classWidth, Points = pts, Attributes = a },
        };
    }

    private static CrossSectionPlanner.Line Planned(RoadClass cls, RoadAttributes a, float classWidth)
    {
        var line = Line(cls, a, classWidth);
        CrossSectionPlanner.Plan([line], null, new CrossSectionPlanner.Stats());
        return line;
    }

    [Theory]
    [InlineData(1, 1, 1000, 10f)]   // the class width: a 10 m road stays two 5 m lanes
    [InlineData(1, 1, 600, 6f)]
    [InlineData(2, 2, 1000, 12f)]   // 4 lanes of 3 m
    [InlineData(2, 1, 800, 9f)]
    [InlineData(2, 2, 1600, 16f)]   // already wider than its lanes need
    public void An_ordinary_two_way_road_is_at_least_its_lanes_wide(int back, int fwd, int widthCm, float expected)
    {
        var line = Planned(RoadClass.Major, new RoadAttributes(LanesForward: (byte)fwd, LanesBackward: (byte)back, WidthCm: (ushort)widthCm), 9f);
        Assert.Equal(expected, line.Width, 3);
        Assert.Equal((ushort)Math.Round(expected * 100), CrossSectionPlanner.Attributes(line).WidthCm);
    }

    [Fact]
    public void A_one_way_road_with_two_lanes_is_six_metres()
    {
        var line = Planned(RoadClass.Minor, new RoadAttributes(OneWay: 1, LanesForward: 2), 4f);
        Assert.Equal(6f, line.Width, 3);
        var single = Planned(RoadClass.Minor, new RoadAttributes(OneWay: 1, LanesForward: 1), 4f);
        Assert.Equal(4f, single.Width, 3);
    }

    [Fact]
    public void A_road_without_lane_data_keeps_its_class_width()
    {
        var line = Planned(RoadClass.Major, new RoadAttributes(), 9f);
        Assert.Equal(9f, line.Width, 3);
    }

    [Fact]
    public void Two_way_lanes_share_the_width_beside_bike_lanes()
    {
        // 12 m, two lanes each way: lines at -3, 0 (centre) and 3
        Assert.Equal(-3f, RoadCrossSection.TwoWayLineOffset(12, 0, 0, 2, 2, 1), 4);
        Assert.Equal(0f, RoadCrossSection.TwoWayLineOffset(12, 0, 0, 2, 2, 2), 4);
        Assert.Equal(3f, RoadCrossSection.TwoWayLineOffset(12, 0, 0, 2, 2, 3), 4);
        // each direction's rightmost lane, and the one inside it
        Assert.Equal(4.5f, RoadCrossSection.TwoWayLaneOffset(12, 0, 0, 2, 2), 4);
        Assert.Equal(1.5f, RoadCrossSection.TwoWayLaneOffset(12, 0, 0, 2, 2, inner: 1), 4);
        // one lane each way: the width / 4 traffic has always kept to
        Assert.Equal(2.5f, RoadCrossSection.TwoWayLaneOffset(10, 0, 0, 1, 1), 4);
        // a 1.8 m bike lane on the right: the car lane (8 - 3.6) / 2 = 2.2 m is clear of it
        Assert.Equal(4f - 1.8f - 1.1f, RoadCrossSection.TwoWayLaneOffset(8, 1.8f, 1.8f, 1, 1), 4);
        // 2 + 1: the lane is a third of the width, the two forward lanes are on the right of the centre line
        Assert.Equal(3f, RoadCrossSection.TwoWayLaneWidth(9, 0, 0, 1, 2), 4);
        Assert.Equal(-1.5f, RoadCrossSection.TwoWayLineOffset(9, 0, 0, 1, 2, 1), 4);
    }

    [Fact]
    public void The_paint_draws_the_lane_lines_where_traffic_keeps_to_its_lanes()
    {
        var seg = new RoadSegment
        {
            Class = RoadClass.Major, Surface = RoadSurface.Paved, Width = 12f,
            Points = [100, 400, 500, 200, 400, 500, 300, 400, 500],
            Attributes = new RoadAttributes(LanesForward: 2, LanesBackward: 2, WidthCm: 1200),
        };
        var paint = new List<RoadPaint>();
        PaintEmitter.Emit(seg, 0, paint, startsAtJunction: true, endsAtJunction: true);
        var dashed = paint.Where(p => p.Type == PaintType.WhiteDashed).Select(p => p.Offset).Distinct().OrderBy(o => o).ToList();
        Assert.Equal(new[] { -3f, 0f, 3f }, dashed);
    }

    [Theory]
    [InlineData(10.0, 2, false, 18, BikePlanner.Why.Lane)]    // the default: 10 - 3.6 >= 2 x 2.75
    [InlineData(12.0, 4, false, 0, BikePlanner.Why.Narrow)]   // four lanes of 2.75 m beside 2 x 1.8 m need 14.6 m
    [InlineData(15.0, 4, false, 18, BikePlanner.Why.Lane)]
    [InlineData(10.0, 3, false, 0, BikePlanner.Why.Narrow)]   // three lanes: no Kernfahrbahn, it shares one lane each way
    [InlineData(8.0, 2, false, 18, BikePlanner.Why.Kern)]
    public void A_bike_lane_needs_a_car_lane_wide_enough_for_every_car_lane(double width, int carLanes, bool urban, int dm, BikePlanner.Why why)
    {
        var (got, gotWhy) = BikePlanner.LaneFor(width, false, urban, carLanes);
        Assert.Equal(dm, got);
        Assert.Equal(why, gotWhy);
    }

    [Fact]
    public void A_one_way_road_keeps_a_lane_wide_enough_for_each_of_its_car_lanes()
    {
        Assert.Equal(((byte)15, BikePlanner.Why.Lane), BikePlanner.LaneFor(4.5, true, true));
        Assert.Equal(((byte)0, BikePlanner.Why.Narrow), BikePlanner.LaneFor(6.5, true, true, 2));   // 2 x 3.0 + 0.5 < the 1.25 minimum
        Assert.Equal(((byte)15, BikePlanner.Why.Lane), BikePlanner.LaneFor(7.5, true, true, 2));
    }
}
