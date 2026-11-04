using UnitSport.Player;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary><c>/movespeed</c>'s factor (#1485): any positive number, no upper cap, 1 = normal.</summary>
public class MoveSpeedCommandTests
{
    [Theory]
    [InlineData("5", 5f)]
    [InlineData("0.5", 0.5f)]
    [InlineData("0,25", 0.25f)]
    [InlineData("x20", 20f)]
    [InlineData("20x", 20f)]
    [InlineData("1000", 1000f)]
    [InlineData("1", 1f)]
    [InlineData("normal", 1f)]
    [InlineData(" Reset ", 1f)]
    [InlineData("off", 1f)]
    public void Reads(string text, float expected)
    {
        Assert.True(MoveSpeedCommand.TryParse(text, out float factor, out string error), error);
        Assert.Equal(expected, factor);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("fast")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e40")]
    public void Refuses(string text)
    {
        Assert.False(MoveSpeedCommand.TryParse(text, out _, out string error));
        Assert.Contains("/movespeed", error);
    }

    [Fact]
    public void Describes()
    {
        Assert.Equal("normal (x1)", MoveSpeedCommand.Describe(1f));
        Assert.Equal("x5", MoveSpeedCommand.Describe(5f));
        Assert.Equal("x0.5", MoveSpeedCommand.Describe(0.5f));
    }
}
