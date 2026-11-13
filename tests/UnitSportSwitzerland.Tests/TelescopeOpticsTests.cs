using UnitSport.Items;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The telescope's lens (#1440, src/Items/TelescopeOptics.cs).</summary>
public class TelescopeOpticsTests
{
    [Fact]
    public void The_moon_fills_the_view_near_2300_mm()
    {
        // the moon is 0.52 deg wide
        Assert.InRange(TelescopeOptics.Fov(2300f), 0.55f, 0.65f);
        Assert.True(TelescopeOptics.Fov(TelescopeOptics.FocalMax) < 0.52f / 2.5f, "at the longest end the moon is over 2.5 views tall");
        // #1813: Mars, 3.5-25 arcseconds, is half the view or more at the longest end
        Assert.InRange(TelescopeOptics.Fov(TelescopeOptics.FocalMax) * 3600f, 7f, 12f);
        // the camera's rule: 35 mm is 38 deg
        Assert.Equal(37.85f, TelescopeOptics.Fov(35f), 0.05f);
    }

    [Fact]
    public void Zoom_steps_and_stays_in_range()
    {
        Assert.Equal(TelescopeOptics.FocalStart * TelescopeOptics.Step, TelescopeOptics.Zoom(TelescopeOptics.FocalStart, 1), 0.01f);
        Assert.Equal(TelescopeOptics.FocalMax, TelescopeOptics.Zoom(TelescopeOptics.FocalMax, 3));
        Assert.Equal(TelescopeOptics.FocalMin, TelescopeOptics.Zoom(TelescopeOptics.FocalMin, -3));
        // from the start to the moon filling the view in a handful of notches
        float f = TelescopeOptics.FocalStart;
        int notches = 0;
        while (f < 2300f) { f = TelescopeOptics.Zoom(f, 1); notches++; }
        Assert.InRange(notches, 6, 10);
    }

    [Fact]
    public void Look_slows_with_the_zoom_without_a_floor()
    {
        Assert.Equal(TelescopeOptics.Fov(400f) / 76f, TelescopeOptics.LookScale(400f), 1e-6f);
        // the camera's own floor (0.04) would turn the longest view by many widths a mouse count
        Assert.True(TelescopeOptics.LookScale(TelescopeOptics.FocalMax) < 0.003f);
    }
}
