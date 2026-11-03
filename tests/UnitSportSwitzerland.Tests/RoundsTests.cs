using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The "in a round" registry (#1268, src/Core/Rounds.cs, linked in): the Battle Royale and Versteckis behind one question.</summary>
[Collection("Rounds")]
public class RoundsTests
{
    private static void TwoModes(HashSet<long> brLobby, HashSet<long> brAlive, HashSet<long> vsGame, HashSet<long> vsPlaying)
    {
        Rounds.Clear();
        Rounds.Register(new Rounds.Mode("br", "a Battle Royale match", brLobby.Contains, brAlive.Contains)
            { NoTeleport = "No teleporting out of a Battle Royale. /br leave to give up." });
        Rounds.Register(new Rounds.Mode("versteckis", "a game of Versteckis", vsGame.Contains, vsPlaying.Contains)
            { ChatScope = p => vsGame.Contains(p) && !vsPlaying.Contains(p) ? "vs:1" : null });
    }

    [Fact]
    public void Every_mode_answers_and_the_first_registered_answers_first()
    {
        TwoModes([1, 2], [2], [3, 4], [3]);
        Assert.True(Rounds.InRound(1));          // a lobby entrant: in a round, not playing
        Assert.False(Rounds.Playing(1));
        Assert.True(Rounds.Playing(2));
        Assert.Equal("br", Rounds.PlayingIn(2)!.Name);
        Assert.True(Rounds.InRound(4));
        Assert.Equal("versteckis", Rounds.PlayingIn(3)!.Name);
        Assert.False(Rounds.InRound(9));
        Assert.Null(Rounds.PlayingIn(9));
        // a peer in both (cannot happen, but the order is the rule): the Battle Royale's words
        TwoModes([5], [5], [5], [5]);
        Assert.Equal("a Battle Royale match", Rounds.PlayingIn(5)!.Label);
        Rounds.Clear();
    }

    [Fact]
    public void Registering_again_replaces_in_place_and_unregister_drops()
    {
        TwoModes([1], [1], [], []);
        Rounds.Register(new Rounds.Mode("br", "x", _ => false, _ => false));
        Assert.Equal(2, Rounds.All.Count);
        Assert.Equal("br", Rounds.All[0].Name);
        Assert.False(Rounds.InRound(1));
        Rounds.Unregister("br");
        Assert.Single(Rounds.All);
        Rounds.Clear();
        Assert.False(Rounds.Playing(1));
    }

    [Fact]
    public void The_chat_scope_comes_from_the_mode_that_has_one()
    {
        TwoModes([], [], [7, 8], [8]);
        Assert.Equal("vs:1", Rounds.ChatScope(7));   // caught: spectators only
        Assert.Null(Rounds.ChatScope(8));             // still hiding: the open chat
        Assert.Null(Rounds.ChatScope(1));
        Rounds.Clear();
    }
}
