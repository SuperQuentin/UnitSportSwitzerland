using UnitSport.Core;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The server-owned simulation clock (#579). Static state, so every test calls
/// <see cref="SimClock.Reset"/> first: xUnit runs a class's tests one at a time, and these do not
/// run in parallel with each other. Every class that sets the clock joins <see cref="Collection"/>,
/// which runs its classes one after another: <c>EnvClockTests</c> once failed in parallel with this one.
/// </summary>
[Collection(Collection)]
public class SimClockTests
{
    /// <summary>The xUnit collection every test class that sets the static clock is in.</summary>
    public const string Collection = "SimClock";

    public SimClockTests() => SimClock.Reset();

    [Fact]
    public void NormalSpeedTracksTheServerClockOneForOne()
    {
        SimClock.Set(0, 100, 1);
        Assert.Equal(0, SimClock.SimAt(100), 9);
        Assert.Equal(10, SimClock.SimAt(110), 9);
    }

    [Fact]
    public void AScaleStretchesTheServerClock()
    {
        SimClock.Set(0, 100, 0.25);
        Assert.Equal(2.5, SimClock.SimAt(110), 9);

        SimClock.Set(0, 100, 4);
        Assert.Equal(40, SimClock.SimAt(110), 9);
    }

    [Fact]
    public void RebaseKeepsSimulatedTimeContinuousAcrossAChange()
    {
        SimClock.Set(0, 100, 1);
        // 10 s of server time at 1x: 10 simulated seconds elapsed
        SimClock.Rebase(110, 0.25);
        Assert.Equal(10, SimClock.SimAt(110), 9);          // no jump at the change
        Assert.Equal(12.5, SimClock.SimAt(120), 9);        // and the new rate from there
    }

    [Fact]
    public void AScheduledChangeDoesNotApplyBeforeItsInstant()
    {
        SimClock.Set(0, 100, 1);
        SimClock.Schedule(0.5, 110);

        Assert.False(SimClock.Tick(105));
        Assert.Equal(1, SimClock.Scale);
        Assert.Equal(5, SimClock.SimAt(105), 9);

        Assert.True(SimClock.Tick(110));
        Assert.Equal(0.5, SimClock.Scale);
    }

    /// <summary>
    /// The point of scheduling: a peer that notices late must land on the same numbers as one that
    /// noticed on time, or the two disagree about simulated time from then on.
    /// </summary>
    [Fact]
    public void ALatePeerAgreesWithAPromptOne()
    {
        SimClock.Set(0, 100, 1);
        SimClock.Schedule(0.5, 110);
        SimClock.Tick(110);                                // noticed exactly on time
        double prompt = SimClock.SimAt(130);

        SimClock.Reset();
        SimClock.Set(0, 100, 1);
        SimClock.Schedule(0.5, 110);
        SimClock.Tick(113.7);                              // noticed three and a bit seconds late
        double late = SimClock.SimAt(130);

        Assert.Equal(prompt, late, 9);
        Assert.Equal(20, prompt, 9);                       // 10 s at 1x, then 20 s at 0.5x
    }

    [Fact]
    public void TickIsSpentOnce()
    {
        SimClock.Set(0, 100, 1);
        SimClock.Schedule(2, 110);
        Assert.True(SimClock.Tick(110));
        Assert.False(SimClock.Tick(120));                  // nothing left to promote
        Assert.Equal(2, SimClock.Scale);
    }

    [Fact]
    public void ScaleIsClampedToTheAllowedRange()
    {
        Assert.Equal(SimClock.MaxScale, SimClock.Clamp(1000));
        Assert.Equal(SimClock.MinScale, SimClock.Clamp(0));
        Assert.Equal(SimClock.Normal, SimClock.Clamp(double.NaN));
        Assert.Equal(SimClock.Normal, SimClock.Clamp(double.PositiveInfinity));
    }

    [Theory]
    [InlineData("0.25", 0.25)]
    [InlineData("1", 1.0)]
    [InlineData("8", 8.0)]
    [InlineData("normal", 1.0)]
    [InlineData("  4  ", 4.0)]
    public void ParsesASpeed(string text, double expected)
    {
        Assert.True(SimClock.TryParse(text, out double scale, out _));
        Assert.Equal(expected, scale, 9);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("")]
    [InlineData("9")]
    [InlineData("-1")]
    [InlineData("nan")]
    public void RefusesWhatIsNotASpeed(string text)
    {
        Assert.False(SimClock.TryParse(text, out _, out string error));
        Assert.NotEqual("", error);
    }

    /// <summary>Zero is refused with its own answer: it would freeze the world, which is a different feature.</summary>
    [Fact]
    public void ZeroIsRefusedAndPointsAtTimeSpeedInstead()
    {
        Assert.False(SimClock.TryParse("0", out _, out string error));
        Assert.Contains("/time speed 0", error);
    }

    /// <summary>The French locale would read "1.5" as 15 (docs/notes/general/invariant-culture-floats).</summary>
    [Fact]
    public void ParsesWithTheInvariantCulture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            Assert.True(SimClock.TryParse("1.5", out double scale, out _));
            Assert.Equal(1.5, scale, 9);
            Assert.Equal("x1.5", SimClock.Describe(1.5));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void DescribesNormalSpeedByName()
    {
        Assert.Equal("normal speed", SimClock.Describe(1.0));
        Assert.Equal("x0.25", SimClock.Describe(0.25));
    }
}
