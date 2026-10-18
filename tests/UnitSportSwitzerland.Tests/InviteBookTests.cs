using UnitSport.Net;
using Xunit;

namespace UnitSport.Tests;

public class InviteBookTests
{
    private static Invite Add(InviteBook book, string kind, long from, long to, long key = 0, double now = 0, double seconds = 60) =>
        book.Add(kind, from, to, "t", "d", key, now, seconds, out _, out string? why) ?? throw new Exception(why);

    [Fact]
    public void SendingAgainReplacesAndRestartsTheTimer()
    {
        var book = new InviteBook();
        var first = Add(book, "tp", 1, 2, now: 0);
        var second = book.Add("tp", 1, 2, "t", "d", 0, 50, 60, out var replaced, out _)!;
        Assert.Same(first, replaced);
        Assert.Equal(1, book.Count);
        Assert.Equal(110, second.Expires);
        // another key, kind or recipient is another invite
        Add(book, "tp", 1, 3);
        Add(book, "race", 1, 2, key: 7);
        Add(book, "race", 1, 2, key: 8);
        Assert.Equal(4, book.Count);
    }

    [Fact]
    public void RefusesSelfAndTooMany()
    {
        var book = new InviteBook();
        Assert.Null(book.Add("tp", 4, 4, "", "", 0, 0, 60, out _, out string? why));
        Assert.NotNull(why);
        for (int i = 0; i < InviteBook.MaxPerSender; i++) Add(book, "tp", 1, 10 + i);
        Assert.Null(book.Add("tp", 1, 99, "", "", 0, 0, 60, out _, out why));
        Assert.NotNull(why);
        // the console is not limited
        for (int i = 0; i < InviteBook.MaxPerSender + 2; i++) Add(book, "br", 0, Invite.Everyone, key: i);
    }

    [Fact]
    public void LifetimeIsClamped()
    {
        var book = new InviteBook();
        Assert.Equal(InviteBook.MinSeconds, Add(book, "a", 1, 2, seconds: 1).Expires);
        Assert.Equal(InviteBook.MaxSeconds, Add(book, "b", 1, 2, seconds: 9999).Expires);
    }

    [Fact]
    public void DirectInviteIsGoneOnceAnswered()
    {
        var book = new InviteBook();
        var i = Add(book, "fight", 1, 2);
        Assert.True(i.Asks(2));
        Assert.False(i.Asks(3));
        Assert.True(book.Answer(i, 2));
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void InviteToEveryoneIsAnsweredPerPeer()
    {
        var book = new InviteBook();
        var i = Add(book, "br", 1, Invite.Everyone, key: 42);
        Assert.False(i.Asks(1));   // not the sender
        Assert.True(i.Asks(2));
        Assert.True(i.Asks(3));
        Assert.False(book.Answer(i, 2));
        Assert.False(i.Asks(2));
        Assert.True(i.Asks(3));
        Assert.Single(book.To(3));
        Assert.Empty(book.To(2));
        Assert.Equal(1, book.Count);
    }

    [Fact]
    public void ReverseFindsTheOtherWayRound()
    {
        var book = new InviteBook();
        var mine = Add(book, "fight", 2, 1);
        Assert.Same(mine, book.Reverse("fight", 1, 2));
        Assert.Null(book.Reverse("fight", 2, 1));
        Assert.Null(book.Reverse("tp", 1, 2));
    }

    [Fact]
    public void ExpiresAndWithdrawsByKey()
    {
        var book = new InviteBook();
        Add(book, "race", 1, Invite.Everyone, key: 5, seconds: 30);
        Add(book, "race", 1, 2, key: 6, seconds: 60);
        Add(book, "tp", 3, 2, seconds: 60);
        Assert.Single(book.Expire(30));
        Assert.Single(book.Withdraw("race", 6));
        Assert.Empty(book.Withdraw("race", 6));
        Assert.Equal(1, book.Count);
    }

    [Fact]
    public void PeerLeftDropsTheirInvitesBothWays()
    {
        var book = new InviteBook();
        Add(book, "tp", 1, 2);
        Add(book, "tp", 2, 3);
        var all = Add(book, "br", 0, Invite.Everyone);
        book.Answer(all, 2);
        Add(book, "tp", 3, 4);
        Assert.Equal(2, book.PeerLeft(2).Count);
        Assert.Equal(2, book.Count);
        // a new peer given the same id is asked again
        Assert.True(all.Asks(2));
    }

    [Fact]
    public void ResolveByIdKindNameOrNewest()
    {
        var book = new InviteBook();
        var tp = Add(book, "tp", 5, 2);
        var race = Add(book, "race", 6, 2, key: 3);
        Add(book, "tp", 5, 9);
        long ByName(string n) => n == "lea" ? 5 : n == "marc" ? 6 : -1;
        Assert.Same(race, book.Resolve(2, "", ByName));
        Assert.Same(tp, book.Resolve(2, $"#{tp.Id}", ByName));
        Assert.Same(tp, book.Resolve(2, "TP", ByName));
        Assert.Same(tp, book.Resolve(2, "lea", ByName));
        Assert.Same(race, book.Resolve(2, "marc", ByName));
        Assert.Null(book.Resolve(2, "nobody", ByName));
        Assert.Null(book.Resolve(7, "", ByName));
    }
}

public class PartyBookTests
{
    [Fact]
    public void FirstJoinMakesThePartyWithTheHostLeading()
    {
        var book = new PartyBook();
        var p = book.Join(1, 2, out var left, out string? why)!;
        Assert.Null(why);
        Assert.Null(left);
        Assert.Equal(1, p.Leader);
        Assert.Equal(new long[] { 1, 2 }, p.Members);
        Assert.Same(p, book.Of(1));
        Assert.Same(p, book.Of(2));
        Assert.Matches("^[a-z0-9]+$", p.TeamName);
    }

    [Fact]
    public void OnlyTheLeaderInvitesAndAPartyFills()
    {
        var book = new PartyBook();
        book.Join(1, 2, out _, out _);
        Assert.NotNull(book.MayInvite(2, 3));
        Assert.NotNull(book.MayInvite(1, 2));
        Assert.Null(book.MayInvite(1, 3));
        for (long m = 3; m <= PartyBook.MaxMembers; m++) Assert.NotNull(book.Join(1, m, out _, out _));
        Assert.NotNull(book.MayInvite(1, 99));
        Assert.Null(book.Join(1, 99, out _, out string? why));
        Assert.NotNull(why);
    }

    [Fact]
    public void JoiningAnotherPartyLeavesTheOldOne()
    {
        var book = new PartyBook();
        var a = book.Join(1, 2, out _, out _)!;
        book.Join(1, 3, out _, out _);
        var b = book.Join(5, 3, out var left, out _)!;
        Assert.Same(a, left);
        Assert.Equal(new long[] { 1, 2 }, a.Members);
        Assert.Equal(new long[] { 5, 3 }, b.Members);
    }

    [Fact]
    public void LeaderLeavingHandsOverAndOneLeftDisbands()
    {
        var book = new PartyBook();
        var p = book.Join(1, 2, out _, out _)!;
        book.Join(1, 3, out _, out _);
        book.Leave(1, out bool disbanded);
        Assert.False(disbanded);
        Assert.Equal(2, p.Leader);
        book.Leave(3, out disbanded);
        Assert.True(disbanded);
        Assert.Null(book.Of(2));
        Assert.Empty(book.All);
    }

    [Fact]
    public void KickRules()
    {
        var book = new PartyBook();
        book.Join(1, 2, out _, out _);
        Assert.NotNull(book.MayKick(2, 1));
        Assert.NotNull(book.MayKick(1, 1));
        Assert.NotNull(book.MayKick(1, 7));
        Assert.Null(book.MayKick(1, 2));
        Assert.NotNull(book.MayKick(9, 2));
    }
}
