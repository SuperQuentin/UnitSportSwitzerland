using UnitSport.Core;
using UnitSport.World;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Environment time layered on the simulation clock (#579): the two pure halves composed the way
/// <c>World.WorldClock</c> composes them, so the claim "env time rides sim speed" is pinned at
/// tier 0 rather than only in the two-peer check. It sets the static <see cref="SimClock"/>, so it
/// shares <see cref="SimClockTests"/>' collection: run in parallel, one set it under the other.
/// </summary>
[Collection(SimClockTests.Collection)]
public class EnvClockTests
{
    public EnvClockTests() => SimClock.Reset();

    private const float Default = 24f;   // GameSettings.DayLengthMinutes

    /// <summary>Env seconds for a server instant, the way WorldClock.HourAt does it.</summary>
    private static double EnvAtServer(double env0, double envEpoch, double serverNow, float mpd) =>
        TimeCommand.EnvAt(env0, envEpoch, SimClock.SimAt(serverNow), mpd);

    [Fact]
    public void TheDefaultDayRunsEnvTimeSixtyTimesSimTime()
    {
        Assert.Equal(60.0, TimeCommand.DayFactor(Default), 9);
        Assert.Equal(TimeCommand.DaySeconds, TimeCommand.DayFactor(Default) * Default * 60.0, 6);
    }

    [Fact]
    public void AStoppedClockDoesNotAdvance()
    {
        Assert.Equal(0.0, TimeCommand.DayFactor(0f), 9);
        Assert.Equal(500.0, TimeCommand.EnvAt(500, 0, 9999, 0f), 9);
    }

    /// <summary>The whole point of phase 3: slow the simulation and the sun slows with it.</summary>
    [Fact]
    public void EnvTimeRidesSimulationSpeed()
    {
        SimClock.Set(0, 0, 1);
        double full = EnvAtServer(0, 0, 10, Default);          // 10 s of server clock at 1x

        SimClock.Set(0, 0, 0.25);
        double quarter = EnvAtServer(0, 0, 10, Default);       // the same 10 s at 0.25x

        Assert.Equal(600.0, full, 6);                          // 10 sim s * 60
        Assert.Equal(150.0, quarter, 6);                       // 2.5 sim s * 60
        Assert.Equal(full * 0.25, quarter, 6);
    }

    /// <summary>
    /// Because the env layer is keyed to simulated time, a speed change needs no env rebase: the
    /// hour is continuous across it.
    /// </summary>
    [Fact]
    public void ASpeedChangeDoesNotJumpTheHour()
    {
        SimClock.Set(0, 0, 1);
        double envBefore = EnvAtServer(0, 0, 10, Default);

        SimClock.Schedule(0.25, 10);
        SimClock.Tick(10);
        double envAfter = EnvAtServer(0, 0, 10, Default);

        Assert.Equal(envBefore, envAfter, 6);                  // no step at the change
        Assert.True(EnvAtServer(0, 0, 20, Default) > envAfter, "and it keeps moving, slower");
    }

    [Fact]
    public void TheHourWrapsRoundTheDial()
    {
        Assert.Equal(0.0, TimeCommand.HourOf(0), 9);
        Assert.Equal(12.0, TimeCommand.HourOf(12 * 3600), 9);
        Assert.Equal(1.0, TimeCommand.HourOf(25 * 3600), 9);   // past midnight
    }

    [Theory]
    [InlineData(0.0, 22.0)]
    [InlineData(5000.0, 3.0)]
    [InlineData(123456.0, 0.0)]
    [InlineData(999999.0, 13.5)]
    public void ShiftForMakesTheClockReadTheHourAsked(double envSeconds, double hour)
        => Assert.Equal(hour, TimeCommand.HourOf(envSeconds, TimeCommand.ShiftFor(envSeconds, hour)), 9);

    /// <summary>
    /// Setting the clock back must not wind the monotonic counter back with it: a campfire lit ten
    /// env-minutes ago must not become one lit in the future. Only the shift moves.
    /// </summary>
    [Fact]
    public void TurningTheSkyBackLeavesTheCounterAlone()
    {
        SimClock.Set(0, 0, 1);
        double env = EnvAtServer(0, 0, 100, Default);           // 6000 env s in
        double shiftTo3 = TimeCommand.ShiftFor(env, 3.0);

        Assert.Equal(3.0, TimeCommand.HourOf(env, shiftTo3), 9);
        // the counter the growth timers read is untouched by the shift
        Assert.Equal(6000.0, env, 6);
        Assert.Equal(env, EnvAtServer(0, 0, 100, Default), 6);
    }

    [Fact]
    public void EnvTimeOnlyEverCountsUp()
    {
        SimClock.Set(0, 0, 1);
        double last = double.NegativeInfinity;
        for (double server = 0; server < 50; server += 0.5)
        {
            double env = EnvAtServer(0, 0, server, Default);
            Assert.True(env >= last, $"env went back at server {server}: {env} < {last}");
            last = env;
        }
    }
}
