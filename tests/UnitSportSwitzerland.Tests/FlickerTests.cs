using UnitSport.Core;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The flicker counter (src/Core/Flicker.cs, linked in, #788): back and forth counts, a single change does not.</summary>
public class FlickerTests
{
    /// <summary>One-pixel RGBA frames of these grey levels.</summary>
    private static List<byte[]> Frames(params byte[] levels) =>
        levels.Select(l => new byte[] { l, l, l, 255 }).ToList();

    private static int Turns(params byte[] levels) => Flicker.Reversals(Frames(levels), 1, 1)[0];

    [Fact]
    public void Still_pixel_has_no_turns() => Assert.Equal(0, Turns(100, 100, 100, 100));

    [Fact]
    public void An_edge_passing_is_one_change_not_a_flicker() => Assert.Equal(0, Turns(20, 20, 200, 200, 200));

    [Fact]
    public void Back_and_forth_counts_each_turn() => Assert.Equal(3, Turns(20, 200, 20, 200, 20));

    [Fact]
    public void Noise_under_the_threshold_is_ignored() => Assert.Equal(0, Turns(100, 110, 95, 112, 100));

    [Fact]
    public void A_slow_drift_is_one_way()
    {
        // 10 a frame: each step under the threshold, but measured from the last change it adds up
        Assert.Equal(0, Turns(0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100));
    }

    [Fact]
    public void Count_takes_pixels_with_enough_turns()
    {
        var frames = new List<byte[]>
        {
            new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 },
            new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 },
            new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 },
            new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 },
        };
        var turns = Flicker.Reversals(frames, 2, 1);
        Assert.Equal(new byte[] { 2, 0 }, turns);
        Assert.Equal(1, Flicker.Count(turns));
    }
}
