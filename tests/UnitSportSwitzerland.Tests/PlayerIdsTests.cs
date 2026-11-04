using System.Collections.Generic;
using System.Linq;
using UnitSport.Net;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Who a peer is apart from its name (src/Net/PlayerIds.cs, linked in, #1359).</summary>
public class PlayerIdsTests
{
    private static (PlayerIds Ids, Dictionary<long, string> Names, Dictionary<long, string> Keys, Dictionary<long, string> Accounts) Server()
    {
        var names = new Dictionary<long, string>();
        var keys = new Dictionary<long, string>();
        var accounts = new Dictionary<long, string>();
        var ids = new PlayerIds
        {
            NameOf = p => names.GetValueOrDefault(p),
            KeyOf = p => keys.GetValueOrDefault(p),
            AccountOf = p => accounts.GetValueOrDefault(p),
        };
        ids.Online = () => names.Keys.Concat(keys.Keys).Distinct().ToList();
        return (ids, names, keys, accounts);
    }

    [Fact]
    public void An_account_wins_over_the_key_and_an_unproven_peer_has_no_id()
    {
        var (ids, _, keys, accounts) = Server();
        Assert.Null(ids.IdOf(5));
        keys[5] = "KEY5";
        Assert.Equal("KEY5", ids.IdOf(5));
        accounts[5] = "uid-5";
        Assert.Equal("acct:uid-5", ids.IdOf(5));
    }

    [Fact]
    public void A_bot_is_known_by_its_name()
    {
        var (ids, names, _, _) = Server();
        names[-3] = "Heidi";
        Assert.Equal("bot:Heidi", ids.IdOf(-3));
    }

    [Fact]
    public void The_name_is_the_online_one_then_the_last_seen()
    {
        var (ids, names, keys, _) = Server();
        keys[5] = "KEY5"; names[5] = "Anna";
        ids.Refresh(5);
        Assert.Equal("Anna", ids.NameOfId("KEY5"));
        names[5] = "Annette";
        Assert.Equal("Annette", ids.NameOfId("KEY5"));   // live, before any refresh
        ids.Refresh(5);
        names.Remove(5); keys.Remove(5);                    // left
        Assert.Equal("Annette", ids.NameOfId("KEY5"));
        Assert.Equal("Rider", ids.NameOfId("nobody"));
    }

    [Fact]
    public void A_backup_name_shows_only_for_an_id_nothing_knows_any_more()
    {
        var (ids, names, keys, _) = Server();
        keys[5] = "KEY5"; names[5] = "Anna";
        Assert.Equal("Anna", ids.NameOfId("KEY5", "Old"));            // online wins
        ids.Refresh(5);
        names.Remove(5); keys.Remove(5);
        Assert.Equal("Anna", ids.NameOfId("KEY5", "Old"));            // the cache wins
        Assert.Equal("Heidi", ids.NameOfId("acct:deleted", "Heidi")); // a deleted account: the backup
        Assert.Equal("Rider", ids.NameOfId("acct:deleted", ""));
    }

    [Fact]
    public void Identified_once_both_id_and_name_are_known_and_again_next_session()
    {
        var (ids, names, keys, _) = Server();
        var seen = new List<(long, string, string)>();
        ids.Identified += (p, id, n) => seen.Add((p, id, n));
        names[5] = "Anna";
        ids.Refresh(5);                 // named, not proven
        Assert.Empty(seen);
        keys[5] = "KEY5";
        ids.Refresh(5);
        ids.Refresh(5);
        Assert.Equal(new[] { (5L, "KEY5", "Anna") }, seen);
        ids.Left(5);
        ids.Refresh(5);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void The_cache_is_saved_on_a_new_name_only()
    {
        int saves = 0;
        var names = new Dictionary<long, string> { [5] = "Anna" };
        var ids = new PlayerIds(new Dictionary<string, string> { ["KEY5"] = "Anna" }, _ => saves++)
        {
            NameOf = p => names.GetValueOrDefault(p),
            KeyOf = p => p == 5 ? "KEY5" : null,
        };
        ids.Refresh(5);
        Assert.Equal(0, saves);
        names[5] = "Annette";
        ids.Refresh(5);
        Assert.Equal(1, saves);
        Assert.Equal("Annette", ids.LastNames["KEY5"]);
    }

    [Theory]
    [InlineData("acct:3f2a", true)]
    [InlineData("bot:Heidi", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF:2", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF:", false)]
    [InlineData("Anna", false)]
    [InlineData("Rider12", false)]
    [InlineData("ABCDEF0123456789ABCD", false)]
    public void Ids_and_old_name_keys_are_told_apart(string key, bool id) => Assert.Equal(id, PlayerIds.IsId(key));

    [Fact]
    public void A_signed_in_player_is_also_found_by_their_PlayerKey()
    {
        var (ids, names, keys, accounts) = Server();
        keys[5] = "KEY5"; names[5] = "Anna"; accounts[5] = "uid";
        ids.Refresh(5);
        Assert.Equal(5, ids.PeerOf("KEY5"));
        names.Remove(5); keys.Remove(5); accounts.Remove(5);
        Assert.Equal("Anna", ids.NameOfId("KEY5"));
        Assert.Equal("Anna", ids.NameOfId("acct:uid"));
    }
}
