using Godot;
using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The road bike's points derived from its geometry chart, and the rider's fit to it (#1124).</summary>
public class BicycleFrameTests
{
    private static readonly BikeFrame Frame = new(BicycleCatalog.AddictRc15);

    [Fact]
    public void Derived_wheelbase_matches_the_chart()
    {
        // the chart's wheelbase is not an input: it has to come out of stack, reach, angles, chainstay and offset
        Assert.InRange(Frame.Wheelbase, BicycleCatalog.AddictRc15.Geometry.Wheelbase - 0.015f,
            BicycleCatalog.AddictRc15.Geometry.Wheelbase + 0.015f);
    }

    [Fact]
    public void Trail_is_a_road_bikes()
    {
        // 72° head, 43.6 mm offset, 28c: a road bike's trail is 55-70 mm
        Assert.InRange(Frame.Trail, 0.055f, 0.070f);
    }

    [Fact]
    public void Both_wheels_stand_on_the_ground()
    {
        Assert.Equal(Frame.WheelRadius, Frame.RearAxle.Y, 4);
        Assert.Equal(Frame.WheelRadius, Frame.FrontAxle.Y, 4);
        Assert.Equal(Frame.WheelRadius - BicycleCatalog.AddictRc15.Geometry.BbDrop, Frame.BottomBracket.Y, 4);
    }

    [Fact]
    public void The_saddle_bends_the_knee_35_degrees_at_the_bottom_of_the_stroke()
    {
        var hip = Frame.LegHip(1);
        float reach = hip.DistanceTo(Frame.Pedal(Mathf.Pi / 2f, 1));   // a quarter turn on: the pedal at the bottom
        float t = BikeFrame.ThighLength, s = BikeFrame.ShinLength;
        float knee = Mathf.RadToDeg(Mathf.Acos((t * t + s * s - reach * reach) / (2f * t * s)));
        Assert.InRange(180f - knee, 33f, 37f);
        // behind the bottom bracket by the seat angle: never over it
        Assert.True(Frame.Hip.Z < Frame.BottomBracket.Z - 0.12f);
    }

    [Fact]
    public void The_hands_are_ahead_of_the_head_tube_and_above_the_drops()
    {
        Assert.True(Frame.Hood.Z > Frame.HeadTop.Z + 0.08f);
        Assert.True(Frame.Drop.Y < Frame.Hood.Y - 0.10f);
        Assert.Equal(BicycleCatalog.AddictRc15.Geometry.BarWidth / 2f, Frame.Hood.X, 4);
    }

    [Fact]
    public void Pedals_turn_forwards_and_opposite()
    {
        // zero is forward and level; a little further on, the front pedal has gone down
        Assert.True(Frame.Pedal(0f, 1).Z > Frame.BottomBracket.Z + 0.17f);
        Assert.True(Frame.Pedal(0.1f, 1).Y < Frame.BottomBracket.Y);
        Assert.Equal(Frame.Pedal(0f, 1).Z - Frame.BottomBracket.Z, Frame.BottomBracket.Z - Frame.Pedal(0f, -1).Z, 4);
    }

    [Fact]
    public void The_addict_is_a_22_speed_compact_with_discs()
    {
        var spec = BicycleCatalog.AddictRc15;
        Assert.Equal(new[] { 52, 36 }, spec.Chainrings);
        Assert.Equal(11, spec.Cassette.Length);
        Assert.Equal(11, spec.Cassette[0]);
        Assert.Equal(28, spec.Cassette[^1]);
        Assert.Equal(BikeBrakes.Disc, spec.Brakes);
        Assert.InRange(spec.Circumference, 2.12f, 2.15f);   // 700×28c rolls ~2.136 m
    }

    [Fact]
    public void Each_riding_position_moves_the_hips_and_hands_the_way_riders_do()
    {
        var hoods = Frame.Fit(RidingPosition.Hoods);
        var drops = Frame.Fit(RidingPosition.Drops);
        var tuck = Frame.Fit(RidingPosition.Tuck);
        var standing = Frame.Fit(RidingPosition.Standing);
        Assert.True(drops.Wrist.Y < hoods.Wrist.Y - 0.10f, "the drops are lower");
        Assert.True(tuck.Hip.Z > hoods.Hip.Z + 0.05f, "tucked on the saddle's nose");
        Assert.True(tuck.Wrist.Z < hoods.Wrist.Z, "hands back on the tops");
        Assert.InRange(Mathf.RadToDeg(tuck.Back), 5f, 20f);
        Assert.True(standing.Hip.Y > hoods.Hip.Y + 0.05f && standing.Hip.Z > hoods.Hip.Z + 0.10f, "off the saddle, forward");
        // standing, the leg is near straight at the bottom of the stroke, never stretched past it by more than a few cm
        float reach = Frame.LegHip(1, RidingPosition.Standing).DistanceTo(Frame.Pedal(Mathf.Pi / 2f, 1));
        Assert.InRange(reach, BikeFrame.ThighLength + BikeFrame.ShinLength - 0.05f, BikeFrame.ThighLength + BikeFrame.ShinLength + 0.04f);
    }
}
