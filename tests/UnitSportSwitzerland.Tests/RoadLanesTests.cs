using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;
using UnitSport.Tools.RoadGen.Rewrite;
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
    [InlineData(2, 0, 800, 8f)]     // lanes one way only: a one-way in the making, two lanes, not one more the other way
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
        Assert.Equal(new[] { -3f, 3f }, dashed);
        // four lanes: the centre line is solid
        Assert.Contains(paint, p => p.Type == PaintType.WhiteSolid && Math.Abs(p.Offset) < 0.01f);
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

    private static readonly SignalMoves L = SignalMoves.Left, T = SignalMoves.Through, R = SignalMoves.Right;

    [Fact]
    public void Wished_lanes_go_onto_the_carriageway_and_the_leading_left_lanes_become_pockets()
    {
        // one lane toward the lights: left | through;right is today's pocket
        var single = TileRewriter.Assign([L, T | R], 1, rightPocketPossible: true)!;
        Assert.Equal([L], single.Pocket);
        Assert.Equal([T | R], single.Own);
        // a double left
        Assert.Equal([L, L], TileRewriter.Assign([L, L, T | R], 1, false)!.Pocket);
        // two lanes already: assigned in place, nothing built
        var artery = TileRewriter.Assign([L, T | R], 2, false)!;
        Assert.Empty(artery.Pocket);
        Assert.Equal([L, T | R], artery.Own);
        // no left lane in the data: no pocket, the extra through lane folds into the own lane
        var noLeft = TileRewriter.Assign([T, T | R], 1, true)!;
        Assert.Empty(noLeft.Pocket);
        Assert.Equal([T | R], noLeft.Own);
        Assert.Equal(1, noLeft.Folded);
        // a right-only lane at the far right is a right pocket where one can be built, else it folds
        var three = TileRewriter.Assign([L, T, R], 1, true)!;
        Assert.True(three.RightPocket);
        Assert.Equal([T], three.Own);
        Assert.Equal([T | R], TileRewriter.Assign([L, T, R], 1, false)!.Own);
        // fewer wished lanes than the carriageway holds: the leftmost wished lane fills the lanes on the left
        Assert.Equal([L, L, T | R], TileRewriter.Assign([L, T | R], 3, false)!.Own);
        Assert.Null(TileRewriter.Assign(null, 1, true));
    }

    [Fact]
    public void The_row_at_a_line_end_is_the_short_way_there_not_the_one_covering_the_line()
    {
        string path = Path.Combine(Path.GetTempPath(), "overlay-700-" + Guid.NewGuid().ToString("N") + ".tsv");
        static string Row(double from, double to, string lanesFwd, string turnFwd) =>
            $"{{u}}\t0\t{from:F1}\t{to:F1}\t1\t+\tsecondary\t\t\t{lanesFwd}\t\t\t\t\t\t\t{turnFwd}\t\t0\t0";
        File.WriteAllLines(path, ["uuid\tpart", Row(0, 300, "", ""), Row(300, 450, "3", "left|left|through;right")]);
        try
        {
            var overlay = OsmOverlayReader.TryLoad(path)!;
            Assert.Equal("", overlay.Best("{u}", 0, 0, 450)!.LanesFwd);   // the line's own row
            var end = overlay.AtEnd("{u}", 0, 450, towardEnd: true)!;
            Assert.Equal("3", end.LanesFwd);
            Assert.Equal([TurnMove.Left, TurnMove.Left, TurnMove.Through | TurnMove.Right], end.TurnLanesFwd);
            Assert.Equal("", overlay.AtEnd("{u}", 0, 0, towardEnd: false)!.LanesFwd);   // the start of the line
            Assert.Null(overlay.AtEnd("{other}", 0, 450, true));
        }
        finally { File.Delete(path); }
    }
}
