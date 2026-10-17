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
    public void Kills_of_bots_count_and_show_their_share()   // #837: "10 kills (3 bots)"
    {
        Assert.Equal("10 kills (3 bots)", BrStats.KillText(10, 3));
        Assert.Equal("1 kill (1 bot)", BrStats.KillText(1, 1));
        Assert.Equal("4 kills", BrStats.KillText(4, 0));
        var s = new BrStats();
        s.Add(new[] { ("Anna", 1, 6, 2, 300f, 900.0) });
        s.Add(new[] { ("anna", 3, 4, 1, 100f, 500.0) });
        var a = s.Find("Anna")!;
        Assert.Equal((10, 3), (a.Kills, a.BotKills));
        Assert.Equal("2 matches · 1 win · 10 kills (3 bots) · top 5 ×2 · best #1", a.Line());
        Assert.Equal("1. anna — 1 win, 10 kills (3 bots)", s.Board(1).Single());
    }

    [Fact]
    public void Bot_kills_never_exceed_kills()
    {
        var s = new BrStats();
        s.Add(new[] { ("Beat", 2, 1, 5, 0f, 0.0) });
        Assert.Equal(1, s.Find("Beat")!.BotKills);
    }

    [Fact]
    public void Records_survive_json()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(TwoMatches());
        var back = System.Text.Json.JsonSerializer.Deserialize<BrStats>(json)!;
        Assert.Equal(3, back.Find("beat")!.Kills);
    }
}
