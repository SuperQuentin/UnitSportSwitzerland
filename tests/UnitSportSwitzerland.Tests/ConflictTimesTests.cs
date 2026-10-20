using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Layout;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The conflict time between two movements (#979, the user's rule): amber + (clearing + length) / v − entering / v.</summary>
public class ConflictTimesTests
{
    private static ConflictTimes.Line Car(int group, Vec2 from, Vec2 to, double amber = 3) =>
        new(group, ConflictTimes.Mover.Car, [from, to], 3, ConflictTimes.CarClear, ConflictTimes.CarEnter, ConflictTimes.CarLength, amber);

    [Fact]
    public void Two_straight_crossings_by_hand()
    {
        // north from 20 m south of the crossing, east from 20 m west; 3 m wide each: they touch within 3 m of the crossing point
        var north = Car(0, new Vec2(0, -20), new Vec2(0, 20));
        var east = Car(1, new Vec2(-20, 0), new Vec2(20, 0));
        var meet = ConflictTimes.Meet(north.Points, east.Points, 3);
        Assert.NotNull(meet);
        // (walked in 0.5 m steps: within a step of the band's edges)
        Assert.InRange(meet!.Value.Clear, 22.9, 23.6);   // out of the other's band 3 m past the crossing point
        Assert.InRange(meet.Value.Enter, 16.9, 17.6);    // into it 3 m before
        double expected = 3 + (meet.Value.Clear + ConflictTimes.CarLength) / ConflictTimes.CarClear - meet.Value.Enter / ConflictTimes.CarEnter;
        Assert.Equal(expected, ConflictTimes.Time(north, east)!.Value, 3);
        Assert.InRange(expected, 4.2, 4.5);
    }

    [Fact]
    public void A_walker_from_the_far_kerb_clears_slowly_and_can_make_it_negative_the_other_way()
    {
        // a 14 m crossing; a car crossing it 2 m from the near kerb, from a stop line 3 m before
        var walker = new ConflictTimes.Line(0, ConflictTimes.Mover.Walker, [new Vec2(0, 0), new Vec2(14, 0)], 3,
            ConflictTimes.WalkClear, ConflictTimes.WalkEnter, ConflictTimes.WalkLength, 0);
        var car = Car(1, new Vec2(12, -3), new Vec2(12, 20));
        var t = ConflictTimes.Time(walker, car)!.Value;
        Assert.InRange(t, 8, 14);   // the far-kerb walker walks most of the crossing at 1.2 m/s
        // the other way: the car's last one is past long before a walker from the far kerb reaches its lane
        var back = ConflictTimes.Time(Car(1, new Vec2(12, -3), new Vec2(12, 20)), walker with { Group = 0 })!.Value;
        Assert.True(back < 0, $"{back:F1} s");
    }

    [Fact]
    public void A_walker_enters_and_clears_each_lane_where_it_is_not_at_the_zebras_edges()
    {
        // #979, the user: the middle of the crossing is entered later and left sooner by the walkers. A 4 m wide zebra 14 m long,
        // a car's 3 m lane across it at 7 m: a walker is in that lane from 7 - (3 + 0.5) / 2 to 7 + (3 + 0.5) / 2, not over the
        // zebra's whole width
        var walker = new ConflictTimes.Line(0, ConflictTimes.Mover.Walker, [new Vec2(0, 0), new Vec2(14, 0)], 4,
            ConflictTimes.WalkClear, ConflictTimes.WalkEnter, ConflictTimes.WalkLength, 0);
        var car = Car(1, new Vec2(7, -10), new Vec2(7, 10));
        var (clear, _) = ConflictTimes.Meet(walker, car)!.Value;
        Assert.InRange(clear, 8.75, 8.75 + 0.5);   // (walked in 0.5 m steps)
        var (_, enter) = ConflictTimes.Meet(car, walker)!.Value;
        Assert.InRange(enter, 5.25 - 0.01, 5.25 + 0.5);
        // the car still keeps clear of the whole zebra: it is in it from 10 - (4 + 3) / 2 m along its line
        var (_, carEnters) = ConflictTimes.Meet(walker, car)!.Value;
        Assert.InRange(carEnters, 6.5 - 0.01, 6.5 + 0.5);
    }

    [Fact]
    public void Lines_that_never_meet_have_no_time()
    {
        Assert.Null(ConflictTimes.Time(Car(0, new Vec2(0, 0), new Vec2(0, 20)), Car(1, new Vec2(10, 0), new Vec2(10, 20))));
    }
}
