using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The seeds a film's shot pins (#924): the same scope gives the same streams in any process, a
/// different shot or system a different one, and normal play stays unseeded. Also the calendar's
/// <c>--date</c>. One class, so the static scope is never shared with a test running in parallel.
/// </summary>
[Collection(nameof(RunSeedsTests))]
public class RunSeedsTests
{
    private static int[] Draw(System.Random r) => [r.Next(), r.Next(), r.Next()];

    [Fact]
    public void A_pinned_scope_draws_the_same_and_tells_the_systems()
    {
        int told = 0;
        void OnReseeded() => told++;
        RunSeeds.Reseeded += OnReseeded;
        try
        {
            RunSeeds.Begin("story/12/go");
            Assert.True(RunSeeds.Pinned);
            var birds = Draw(RunSeeds.Rng("birds"));
            long id = RunSeeds.NewId();
            RunSeeds.Begin("story/5/go");
            RunSeeds.Begin("story/12/go");
            Assert.Equal(3, told);
            // the same shot: the same stream and the same ids, whatever ran in between
            Assert.Equal(birds, Draw(RunSeeds.Rng("birds")));
            Assert.Equal(id, RunSeeds.NewId());
            // another system, another stream
            Assert.NotEqual(birds, Draw(RunSeeds.Rng("traffic")));
            // a fixed number for a fixed scope: FNV-1a, never string.GetHashCode
            Assert.Equal(Fnv.Hash("story/12/go|birds"), RunSeeds.Of("birds"));
            RunSeeds.Begin("story/13/go");
            Assert.NotEqual(birds, Draw(RunSeeds.Rng("birds")));
        }
        finally
        {
            RunSeeds.Reseeded -= OnReseeded;
            RunSeeds.End();
        }
        Assert.False(RunSeeds.Pinned);
        Assert.Null(RunSeeds.Scope);
    }

    [Fact]
    public void Calendar_date_reads_iso_dates_only()
    {
        Assert.Equal(new DateOnly(2026, 7, 15), Calendar.DateFrom(["--trailer", "all", "--date", "2026-07-15"]));
        Assert.Null(Calendar.DateFrom(["--date", "15.07.2026"]));
        Assert.Null(Calendar.DateFrom(["--date"]));
        Assert.Null(Calendar.DateFrom([]));
    }
}
