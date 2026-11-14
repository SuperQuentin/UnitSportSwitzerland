using UnitSport.Core;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary><c>/goto &lt;body&gt; &lt;lat&gt; &lt;lon&gt; [km]</c>'s reading (#1850, src/Core/BodyGoto.cs).</summary>
public class BodyGotoTests
{
    [Fact]
    public void A_body_by_name_a_place_and_a_height()
    {
        Assert.True(BodyGoto.TryParse("Moon 26.13 3.63 5", out var body, out double lat, out double lon, out double h, out _));
        Assert.Equal(BodyId.Moon, body.Id);
        Assert.Equal(26.13, lat);
        Assert.Equal(3.63, lon);
        Assert.Equal(5000, h);
        // east longitudes past 180 wrap; the height by default 30 km over a body with ground
        Assert.True(BodyGoto.TryParse("mars 4.6 137.4", out body, out _, out _, out h, out _));
        Assert.Equal(BodyId.Mars, body.Id);
        Assert.Equal(30_000, h);
        Assert.True(BodyGoto.TryParse("phobos 0 311", out _, out _, out lon, out _, out _));
        Assert.Equal(-49, lon, 9);
    }

    [Fact]
    public void Never_under_a_bodys_floor_and_bad_lines_refused()
    {
        // the Sun stops a camera at a fifth of its radius; a giant's floor is deep in its cloud, so its tops are fine
        Assert.True(BodyGoto.TryParse("sun 0 0 0", out var sun, out _, out _, out double h, out _));
        Assert.Equal(sun.FloorM, h);
        Assert.True(BodyGoto.TryParse("jupiter 0 0 0", out _, out _, out _, out h, out _));
        Assert.Equal(0, h);
        Assert.False(BodyGoto.TryParse("pluto 0 0", out _, out _, out _, out _, out string error));
        Assert.Contains("No body called pluto", error);
        Assert.False(BodyGoto.TryParse("moon 95 0", out _, out _, out _, out _, out error));
        Assert.Contains("latitude", error);
        Assert.False(BodyGoto.TryParse("moon 0", out _, out _, out _, out _, out error));
        Assert.Equal(BodyGoto.Usage, error);
    }
}
