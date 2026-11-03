using UnitSport.Adventure;
using UnitSport.Adventure.Rewards;
using UnitSport.Net;
using UnitSport.Occasions.Festivals;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Boards, crews, tags and progress show a player's name now, from their id, not the one stored (#1359).</summary>
public class LiveNamesTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static string Live(string id, string? backup) => id == Key ? "Neu" : backup ?? "Rider";

    [Fact]
    public void A_trial_board_shows_the_live_name_for_an_id_and_the_stored_one_otherwise()
    {
        var b = new TrialBoard();
        b.Submit("t", Key, "Alt", 10, 1);
        b.Submit("t", "local", "Me", 12, 2);
        Assert.Equal(new[] { "Neu", "Me" }, b.NamesOf("t", Live));
        Assert.Equal(new[] { "Alt", "Me" }, b.NamesOf("t", null));
    }

    [Fact]
    public void A_crew_shows_the_live_name_and_falls_back_to_the_cache()
    {
        var crew = new CrewEntry { Names = { [Key] = "Alt", ["peer:3"] = "Gast" } };
        Assert.Equal("Neu", crew.NameOf(Key, Live));
        Assert.Equal("Gast", crew.NameOf("peer:3", Live));
        Assert.Equal("Alt", crew.NameOf(Key, null));
        Assert.Equal("?", crew.NameOf("acct:9", null));
    }

    [Fact]
    public void A_sleepers_tag_shows_the_live_name_only_for_a_known_key()
    {
        var s = new Sleeper(1, Key, "Alt", 0, 0, 0, 0, 1, 0, 0, 0);
        Assert.Equal("Alt", s.NameShown(null));
        var ids = new PlayerIds(new Dictionary<string, string> { [Key] = "Neu" });
        Assert.Equal("Neu", s.NameShown(ids));
        Assert.Equal("Alt", (s with { Key = "ffffffffffffffffffffffffffffffff" }).NameShown(ids));
    }

    [Fact]
    public void Progress_shows_the_live_name_for_an_id_key()
    {
        Assert.Equal("Neu", new PlayerProgress(Key) { LastName = "Alt" }.NameShown(Live));
        Assert.Equal("Alt", new PlayerProgress("local") { LastName = "Alt" }.NameShown(Live));
        Assert.Equal("Alt", new PlayerProgress(Key) { LastName = "Alt" }.NameShown(null));
    }
}
