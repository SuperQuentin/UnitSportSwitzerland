using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// How much of the earth world's far band is furnished (#1657 phase 3): <see cref="FarBandGovernor"/> under the preset's cap,
/// moved by the frame budget, quick to shrink, slow to grow, never past the cap.
/// </summary>
public class FarBandGovernorTests
{
    private const double Budget = 1000.0 / 60, Dt = 1.0 / 60;

    private static int Run(FarBandGovernor g, double seconds, double frameMs)
    {
        int changes = 0;
        for (double t = 0; t < seconds; t += Dt)
            if (g.Step(Dt, frameMs, Budget)) changes++;
        return changes;
    }

    [Fact]
    public void Slow_frames_shrink_it_in_steps_down_to_none()
    {
        var g = new FarBandGovernor();
        Run(g, FarBandGovernor.ShrinkAfterS + 0.1, Budget * 1.5);
        Assert.Equal(1 - FarBandGovernor.ShrinkStep, g.Scale, 6);
        Run(g, 60, Budget * 1.5);
        Assert.Equal(0, g.Scale, 6);
    }

    [Fact]
    public void Fast_frames_grow_it_back_slowly_never_past_the_cap()
    {
        var g = new FarBandGovernor();
        Run(g, 60, Budget * 2);
        Assert.Equal(0, g.Scale, 6);
        Run(g, FarBandGovernor.GrowAfterS - 0.5, Budget * 0.5);
        Assert.Equal(0, g.Scale, 6);
        Run(g, 1, Budget * 0.5);
        Assert.Equal(FarBandGovernor.GrowStep, g.Scale, 6);
        g.SetCap(0.5);
        Run(g, 600, Budget * 0.5);
        Assert.Equal(0.5, g.Scale, 6);
    }

    [Fact]
    public void Frames_near_the_budget_change_nothing_and_a_lower_cap_holds_at_once()
    {
        var g = new FarBandGovernor();
        Assert.Equal(0, Run(g, 60, Budget));
        Assert.Equal(1, g.Scale, 6);
        Assert.True(g.SetCap(0));
        Assert.Equal(0, g.Scale, 6);
        g.SetCap(1);
        g.Pin(1);
        Assert.Equal(1, g.Scale, 6);
    }
}
