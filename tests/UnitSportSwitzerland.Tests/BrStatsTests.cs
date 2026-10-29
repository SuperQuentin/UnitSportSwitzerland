using UnitSport.BattleRoyale;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Battle Royale careers (#479, src/BattleRoyale/BrStats.cs, linked in).</summary>
public class BrStatsTests
{
    private static readonly Dictionary<string, string> Names = new() { ["acct:1"] = "Anna", ["acct:2"] = "Beat", ["acct:3"] = "Cla" };
    private static string Live(string id, string? backup) => Names.GetValueOrDefault(id, backup ?? "Rider");

    private static BrStats TwoMatches()
    {
        var s = new BrStats();
        s.Add(new[] { ("acct:1", "Anna", 1, 3, 250f, 900.0), ("acct:2", "Beat", 2, 1, 90f, 800.0), ("acct:3", "Cla", 7, 0, 0f, 100.0) });
        s.Add(new[] { ("acct:1", "anna", 4, 1, 60f, 500.0), ("acct:2", "Beat", 1, 2, 120f, 950.0) });
        return s;
    }

    [Fact]
    public void A_match_adds_to_each_record_by_id()
    {
        var a = TwoMatches().Find("acct:1")!;
        Assert.Equal((2, 1, 2, 4, 1), (a.Matches, a.Wins, a.TopFive, a.Kills, a.BestPlace));
        Assert.Equal(310f, a.Damage, 3);
        Assert.Equal("anna", a.Name);   // the cache: the latest spelling
        var c = TwoMatches().Find("acct:3")!;
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
        => Assert.Equal("2 matches · 1 win · 3 kills · top 5 ×2 · best #1", TwoMatches().Find("acct:2")!.Line());

    [Fact]
    public void Blank_ids_are_skipped()
    {
        var s = new BrStats();
        s.Add(new[] { ("  ", "Anna", 1, 0, 0f, 0.0) });
        Assert.Empty(s.Players);
    }

    [Fact]
    public void The_board_shows_names_as_they_are_now()   // #1359
    {
        Names["acct:2"] = "Beatrice";
        try { Assert.Equal("2. Beatrice — 1 win, 3 kills", TwoMatches().Board(3, Live).ElementAt(1)); }
        finally { Names["acct:2"] = "Beat"; }
    }

    [Fact]
    public void A_career_is_found_by_the_name_now_and_a_legacy_one_by_its_own()
    {
        var s = TwoMatches();
        s.Players["dora"] = new BrRecord { Name = "Dora", Matches = 2 };
        Assert.Equal(2, s.FindByName("beat", Live)!.Matches);
        Assert.Equal(2, s.FindByName("DORA", Live)!.Matches);
        Assert.Null(s.FindByName("Nobody", Live));
    }

    [Fact]
    public void A_legacy_career_moves_to_the_id_once()   // #1359
    {
        var s = new BrStats();
        s.Players["dora"] = new BrRecord { Name = "dora", Matches = 4, Wins = 1 };
        Assert.False(s.ClaimLegacy("acct:5", "Other"));
        Assert.True(s.ClaimLegacy("acct:5", "Dora"));
        Assert.Equal((4, 1), (s.Find("acct:5")!.Matches, s.Find("acct:5")!.Wins));
        Assert.Null(s.Find("dora"));
        s.Players["dora"] = new BrRecord { Matches = 9 };   // an id with a career of its own keeps it
        Assert.False(s.ClaimLegacy("acct:5", "Dora"));
        Assert.Equal(4, s.Find("acct:5")!.Matches);
    }

    [Fact]
    public void Kills_of_bots_count_and_show_their_share()   // #837: "10 kills (3 bots)"
    {
        Assert.Equal("10 kills (3 bots)", BrStats.KillText(10, 3));
        Assert.Equal("1 kill (1 bot)", BrStats.KillText(1, 1));
        Assert.Equal("4 kills", BrStats.KillText(4, 0));
        var s = new BrStats();
        s.Add(new[] { ("acct:1", "Anna", 1, 6, 2, 300f, 900.0) });
        s.Add(new[] { ("acct:1", "anna", 3, 4, 1, 100f, 500.0) });
        var a = s.Find("acct:1")!;
        Assert.Equal((10, 3), (a.Kills, a.BotKills));
        Assert.Equal("2 matches · 1 win · 10 kills (3 bots) · top 5 ×2 · best #1", a.Line());
        Assert.Equal("1. anna — 1 win, 10 kills (3 bots)", s.Board(1).Single());
    }

    [Fact]
    public void Bot_kills_never_exceed_kills()
    {
        var s = new BrStats();
        s.Add(new[] { ("acct:2", "Beat", 2, 1, 5, 0f, 0.0) });
        Assert.Equal(1, s.Find("acct:2")!.BotKills);
    }

    [Fact]
    public void Records_survive_json()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(TwoMatches());
        var back = System.Text.Json.JsonSerializer.Deserialize<BrStats>(json)!;
        Assert.Equal(3, back.Find("acct:2")!.Kills);
    }
}
