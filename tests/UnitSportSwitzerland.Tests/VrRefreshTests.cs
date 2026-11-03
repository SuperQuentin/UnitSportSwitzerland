using UnitSport.XR;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The headset's refresh rate among the runtime's (src/XR/VrRefresh.cs, linked in, #1036).</summary>
public class VrRefreshTests
{
    private static readonly float[] Quest2Link = { 72f, 80f, 90f, 120f };

    [Fact]
    public void Takes_the_wanted_rate_when_offered()
    {
        Assert.Equal(72f, VrRefresh.Pick(Quest2Link, 72f));
        Assert.Equal(90f, VrRefresh.Pick(Quest2Link, 90f));
    }

    [Fact]
    public void Otherwise_the_nearest_under_it()
    {
        Assert.Equal(90f, VrRefresh.Pick(Quest2Link, 100f));
        Assert.Equal(72f, VrRefresh.Pick(new[] { 60f, 72f, 90f }, 80f));
        Assert.Equal(89.9f, VrRefresh.Pick(new[] { 72f, 89.9f }, 90f));
    }

    [Fact]
    public void Else_the_lowest_and_none_from_nothing()
    {
        Assert.Equal(90f, VrRefresh.Pick(new[] { 120f, 90f }, 72f));
        Assert.Null(VrRefresh.Pick(System.Array.Empty<float>(), 72f));
        Assert.Null(VrRefresh.Pick(new[] { 0f }, 72f));
    }
}
