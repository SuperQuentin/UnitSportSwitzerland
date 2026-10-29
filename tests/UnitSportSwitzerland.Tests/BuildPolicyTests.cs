using UnitSport.Terrain;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>Loading must not starve the PC (#1381): the build ceiling, the build threads, the cache trim trigger.</summary>
public class BuildPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(6, 4)]
    [InlineData(12, 10)]
    [InlineData(32, 30)]
    public void Two_cores_stay_free_but_never_fewer_than_two_builds(int cores, int ceiling)
        => Assert.Equal(ceiling, BuildPolicy.Ceiling(cores));

    [Fact]
    public void A_saved_override_is_held_to_the_ceiling()
    {
        Assert.Equal(10, BuildPolicy.Clamp(32, 10, out bool clamped));
        Assert.True(clamped);
        Assert.Equal(4, BuildPolicy.Clamp(4, 10, out clamped));
        Assert.False(clamped);
    }

    [Fact]
    public async Task Builds_run_on_below_normal_threads_and_stay_there_after_an_await()
    {
        var scheduler = new BuildScheduler(2);
        var seen = await Task.Factory.StartNew(async () =>
        {
            var first = Thread.CurrentThread.Priority;
            await Task.Run(() => Thread.Sleep(20));
            return (first, after: Thread.CurrentThread.Priority);
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();
        Assert.Equal(ThreadPriority.BelowNormal, seen.first);
        Assert.Equal(ThreadPriority.BelowNormal, seen.after);
    }

    [Fact]
    public void A_trim_waits_for_the_cap_the_interval_and_a_finished_trim()
    {
        long max = 100;
        Assert.False(CacheTrimPolicy.ShouldTrim(100, max, false, 999));
        Assert.True(CacheTrimPolicy.ShouldTrim(101, max, false, CacheTrimPolicy.MinIntervalSeconds));
        Assert.False(CacheTrimPolicy.ShouldTrim(101, max, false, 1));
        Assert.False(CacheTrimPolicy.ShouldTrim(101, max, true, 999));
    }
}
