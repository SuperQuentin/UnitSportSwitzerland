using UnitSport.World;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// What a dedicated server reads back about its environment clock on restart (#579). The saved
/// quantity is the monotonic counter, not the wrapped hour: the hour alone loses the day count and
/// anything growing or burning in environment time would reset with it.
/// </summary>
public class ClockSaveTests
{
    [Fact]
    public void ReadsTheCounterTheServerWrote()
    {
        var saved = ClockSave.Parse("123456.5", "7200", "", "24");
        Assert.NotNull(saved);
        Assert.Equal(123456.5, saved!.Value.EnvNow, 6);
        Assert.Equal(7200, saved.Value.HourShift, 6);
        Assert.Equal(24f, saved.Value.MinutesPerDay);
    }

    /// <summary>
    /// A file written before #579 has the wrapped hour and no counter. Keep the sky, and start the
    /// counter at zero rather than inventing a day count nothing ever recorded — an upgraded
    /// dedicated server must not jump its sky back to the morning.
    /// </summary>
    [Fact]
    public void FallsBackToAPreEnvClockFile()
    {
        var saved = ClockSave.Parse("", "", "21.5", "24");
        Assert.NotNull(saved);
        Assert.Equal(0, saved!.Value.EnvNow);
        Assert.Equal(21.5 * 3600, saved.Value.HourShift, 6);
        Assert.Equal(24f, saved.Value.MinutesPerDay);
        // and the sky really does read back at 21:30
        Assert.Equal(21.5, TimeCommand.HourOf(saved.Value.EnvNow, saved.Value.HourShift), 6);
    }

    [Fact]
    public void AStoppedClockIsKept()
    {
        var saved = ClockSave.Parse("500", "0", "", "0");
        Assert.NotNull(saved);
        Assert.Equal(0f, saved!.Value.MinutesPerDay);
    }

    [Theory]
    [InlineData("", "", "", "")]          // an empty file
    [InlineData("123", "0", "", "")]      // no day length: it tells us nothing
    [InlineData("123", "0", "", "fast")]  // nor does a day length that is not a number
    [InlineData("", "", "", "24")]        // a day length but no clock at all
    [InlineData("", "", "nine", "24")]    // nor an hour that is not a number
    [InlineData("nan", "0", "", "24")]    // non-finite values are not a clock
    [InlineData("123", "0", "", "-1")]    // nor a negative day length
    public void RefusesWhatItCannotRead(string env, string shift, string hour, string mpd)
        => Assert.Null(ClockSave.Parse(env, shift, hour, mpd));

    /// <summary>A counter cannot be negative: it only ever counts up from zero.</summary>
    [Fact]
    public void ANegativeCounterFallsBackRatherThanBeingTrusted()
    {
        var saved = ClockSave.Parse("-5", "0", "9", "24");
        Assert.NotNull(saved);
        Assert.Equal(0, saved!.Value.EnvNow);
        Assert.Equal(9 * 3600, saved.Value.HourShift, 6);
    }

    /// <summary>The French locale writes "123456,5" and would read "123456.5" back as 1234565.</summary>
    [Fact]
    public void ParsesWithTheInvariantCulture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var saved = ClockSave.Parse("123456.5", "0", "", "1.5");
            Assert.NotNull(saved);
            Assert.Equal(123456.5, saved!.Value.EnvNow, 6);
            Assert.Equal(1.5f, saved.Value.MinutesPerDay);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    /// <summary>
    /// The point of saving the counter: a campfire lit before a restart is still burning after it.
    /// Half a day of burn, four env hours in when the server stopped.
    /// </summary>
    [Fact]
    public void AFireLitBeforeARestartIsStillBurningAfterIt()
    {
        double litAt = 100_000;
        double stoppedAt = litAt + 4 * 3600;
        string payload = UnitSport.Crafting.CampfireClock.Lit(litAt);

        var saved = ClockSave.Parse(stoppedAt.ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "", "24");
        Assert.NotNull(saved);
        Assert.True(UnitSport.Crafting.CampfireClock.Burning(payload, saved!.Value.EnvNow),
            "the fire should still be burning when the server picks the counter back up");
        Assert.Equal(8 * 3600, UnitSport.Crafting.CampfireClock.SecondsLeft(payload, saved.Value.EnvNow), 3);
    }
}
