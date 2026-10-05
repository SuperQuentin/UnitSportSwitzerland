using UnitSport.BattleRoyale;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Battle Royale careers (#479, src/BattleRoyale/BrStats.cs, linked in).</summary>
public class BrStatsTests
{
    private static BrStats TwoMatches()
    {
        var s = new BrStats();
        s.Add(new[] { ("Anna", 1, 3, 250f, 900.0), ("Beat", 2, 1, 90f, 800.0), ("Cla", 7, 0, 0f, 100.0) });
        s.Add(new[] { ("anna", 4, 1, 60f, 500.0), ("Beat", 1, 2, 120f, 950.0) });
        return s;
    }

    [Fact]
    public void A_match_adds_to_each_record_by_name_any_case()
    {
        var a = TwoMatches().Find("ANNA")!;
        Assert.Equal((2, 1, 2, 4, 1), (a.Matches, a.Wins, a.TopFive, a.Kills, a.BestPlace));
        Assert.Equal(310f, a.Damage, 3);
        Assert.Equal("anna", a.Name);   // the latest spelling
        var c = TwoMatches().Find("Cla")!;
        Assert.Equal((1, 0, 0, 7), (c.Matches, c.Wins, c.TopFive, c.BestPlace));
    }

    [Fact]
    public void The_board_ranks_wins_then_kills()
    {
        var board = TwoMatches().Board(3).ToList();
        Assert.Equal("1. anna — 1 win, 4 kills", board[0]);
        Assert.Equal("2. Beat — 1 win, 3 kills", board[1]);
        Assert.StartsWith("3. Cla", board[2]);
    }

    [Fact]
    public void A_record_reads_as_one_line()
        => Assert.Equal("2 matches · 1 win · 3 kills · top 5 ×2 · best #1", TwoMatches().Find("Beat")!.Line());

    [Fact]
    public void Blank_names_are_skipped()
    {
        var s = new BrStats();
        s.Add(new[] { ("  ", 1, 0, 0f, 0.0) });
        Assert.Empty(s.Players);
    }

    [Fact]
    public void Records_survive_json()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(TwoMatches());
        var back = System.Text.Json.JsonSerializer.Deserialize<BrStats>(json)!;
        Assert.Equal(3, back.Find("beat")!.Kills);
    }
}
