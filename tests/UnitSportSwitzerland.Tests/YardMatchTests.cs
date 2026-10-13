using UnitSport.Loot;
using UnitSport.Terrain.Construction;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="YardMatch"/> (#627): during a Battle Royale match, a building site's yard container in
/// the match region rolls the match's table (<c>MatchLoot</c>), as the site's building does; outside
/// the region, or with no match, it rolls the yard's own table. <c>LootTables.YardContents</c> takes
/// its roll from exactly this.
/// </summary>
public class YardMatchTests
{
    private const string Inside = "2588_1118_34", Outside = "2601_1190_7";

    /// <summary>As <c>BrManager.SetMatchLoot</c> sets it: the match epoch for a building whose tile touches the region.</summary>
    private static long? Match(string key) => key.StartsWith("2588_1118_") ? 1_000_000_123L : null;

    [Fact]
    public void In_a_match_region_every_yard_container_rolls_the_match_table()
    {
        foreach (var kind in Enum.GetValues<SiteStockKind>())
            Assert.Equal(MatchTable.Supply, YardMatch.MatchTableOf(Inside, kind, Match));
    }

    [Fact]
    public void Outside_the_region_it_rolls_the_yard_table()
    {
        foreach (var kind in Enum.GetValues<SiteStockKind>())
            Assert.Null(YardMatch.MatchTableOf(Outside, kind, Match));
    }

    [Fact]
    public void With_no_match_it_rolls_the_yard_table()
    {
        foreach (var kind in Enum.GetValues<SiteStockKind>())
            Assert.Null(YardMatch.MatchTableOf(Inside, kind, null));
    }
}
