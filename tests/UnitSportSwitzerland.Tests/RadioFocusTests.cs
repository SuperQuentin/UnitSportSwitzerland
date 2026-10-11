using UnitSport.Items;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Several radios at once: the loudest at the ears plays, the others duck (src/Items/RadioFocus.cs, linked in, #813).</summary>
public class RadioFocusTests
{
    private const float Off = float.NegativeInfinity;

    [Fact]
    public void The_loudest_heard_radio_takes_the_focus()
    {
        Assert.Equal(1, RadioFocus.Pick([-20f, -8f, -15f], -1));
        Assert.Equal(-1, RadioFocus.Pick([Off, Off], -1));
        Assert.Equal(-1, RadioFocus.Pick([], -1));
    }

    [Fact]
    public void Halfway_between_two_the_focus_holds_until_the_other_is_clearly_louder()
    {
        Assert.Equal(0, RadioFocus.Pick([-10f, -8f], 0));                                // 2 dB: stays
        Assert.Equal(1, RadioFocus.Pick([-10f, -10f + RadioFocus.SwitchMarginDb + 0.1f], 0));
        Assert.Equal(1, RadioFocus.Pick([Off, -30f], 0));                                // the old one stopped
    }

    [Fact]
    public void Ones_own_radio_always_wins()
    {
        Assert.Equal(1, RadioFocus.Pick([5f, RadioFocus.CloseDb], 0));
    }

    [Fact]
    public void Level_falls_with_distance_and_is_gone_past_the_reach()
    {
        Assert.Equal(-8f, RadioFocus.LevelAt(-8f, 2f, 3f, 45f), 3);     // inside the unit size: full
        Assert.Equal(-8f - 6.02f, RadioFocus.LevelAt(-8f, 6f, 3f, 45f), 2);
        Assert.Equal(Off, RadioFocus.LevelAt(-8f, 45f, 3f, 45f));
    }

    [Fact]
    public void The_duck_fades_over_about_a_second()
    {
        float d = 0f;
        for (int i = 0; i < 30; i++) d = RadioFocus.Step(d, false, 1f / 60f);
        Assert.InRange(d, RadioFocus.DuckDb * 0.6f, RadioFocus.DuckDb * 0.4f);       // half way at 0.5 s
        for (int i = 0; i < 60; i++) d = RadioFocus.Step(d, false, 1f / 60f);
        Assert.Equal(RadioFocus.DuckDb, d);
        for (int i = 0; i < 61; i++) d = RadioFocus.Step(d, true, 1f / 60f);
        Assert.Equal(0f, d);
    }
}
