using UnitSport.Occasions.Festivals.Fahnen;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The flag's motion (#1269 part 5): finite and bounded for every figure and phase, a throw rises and comes back to the hand.</summary>
public class FlagMotionTests
{
    [Fact]
    public void Every_figure_moves_the_flag_within_a_arms_reach_and_never_blows_up()
    {
        foreach (FahnenFigure kind in Enum.GetValues<FahnenFigure>())
            for (double p = -0.2; p <= 1.2; p += 0.05)
            {
                var pose = FlagMotion.Of(kind, p);
                Assert.True(double.IsFinite(pose.X) && double.IsFinite(pose.Y) && double.IsFinite(pose.Roll), $"{kind} at {p}");
                Assert.InRange(pose.X, -0.7, 0.7);
                Assert.InRange(pose.Y, -0.3, 0.7);
                Assert.InRange(pose.Swing, 0, 1);
            }
    }

    [Fact]
    public void A_mill_wheel_turns_a_full_circle()
    {
        Assert.Equal(0, FlagMotion.Of(FahnenFigure.Muehlirad, 0).Roll, 6);
        Assert.Equal(Math.Tau, FlagMotion.Of(FahnenFigure.Muehlirad, 1).Roll, 6);
    }

    [Fact]
    public void A_throw_is_wound_back_while_held_rises_by_its_flight_and_lands_in_the_hand()
    {
        Assert.True(FlagMotion.Throw(1, -1, 3).Roll < 0, "wound back while held");
        Assert.Equal(0, FlagMotion.Throw(1, 0, 3).Y, 6);
        double top = FlagMotion.Throw(0, 1.5, 3).Y;
        Assert.Equal(1.1 * 3, top, 6);
        Assert.True(FlagMotion.Throw(0, 2, 4).Y > FlagMotion.Throw(0, 1, 2).Y, "a longer flight goes higher");
        Assert.Equal(0, FlagMotion.Throw(0, 3, 3).Y, 6);
    }
}
