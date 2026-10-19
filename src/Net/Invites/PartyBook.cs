namespace UnitSport.Net;

/// <summary>A group of players who play together for the session (#840). <see cref="Members"/> starts with the leader.</summary>
public sealed class Party
{
    public int Id { get; init; }
    public List<long> Members { get; } = new();
    public long Leader => Members.Count > 0 ? Members[0] : 0;

    /// <summary>The team name a Battle Royale entrant of this party takes (<c>BrEntrant.Party</c>): letters and digits.</summary>
    public string TeamName => $"party{Id}";
}

/// <summary>
/// The server's parties (#840), as plain data and rules: no Godot, unit-tested. A party is made by
/// the first accepted invite, holds at most <see cref="MaxMembers"/>, passes its lead to the next
/// member when the leader leaves, and disbands when one member is left.
/// </summary>
public sealed class PartyBook
{
    public const int MaxMembers = 8;

    private readonly Dictionary<int, Party> _parties = new();
    private readonly Dictionary<long, int> _of = new();
    private int _nextId = 1;

    public IEnumerable<Party> All => _parties.Values;

    public Party? Of(long peer) => _of.TryGetValue(peer, out int id) ? _parties.GetValueOrDefault(id) : null;

    /// <summary>Why <paramref name="host"/> may not invite <paramref name="peer"/> now, or null.</summary>
    public string? MayInvite(long host, long peer)
    {
        if (host == peer) return "You cannot invite yourself.";
        if (Of(host) is { } mine)
        {
            if (mine.Leader != host) return "Only your party's leader invites.";
            if (mine.Members.Contains(peer)) return "Already in your party.";
            if (mine.Members.Count >= MaxMembers) return $"Your party is full ({MaxMembers}).";
        }
        return null;
    }

    /// <summary>
    /// <paramref name="peer"/> joins <paramref name="host"/>'s party, which is made if the host has none.
    /// A peer in another party leaves it first (<paramref name="left"/>: that party, still holding its
    /// other members, or null). Null with <paramref name="why"/> when refused.
    /// </summary>
    public Party? Join(long host, long peer, out Party? left, out string? why)
    {
        left = null;
        why = host == peer ? "You cannot join yourself."
            : Of(host) is { } full && full.Members.Count >= MaxMembers ? $"That party is full ({MaxMembers})."
            : null;
        if (why != null) return null;
        if (Of(peer) is { } old && old.Members.Contains(host)) return old;
        if (Of(peer) != null) left = Leave(peer, out _);
        var party = Of(host);
        if (party == null)
        {
            party = new Party { Id = _nextId++ };
            party.Members.Add(host);
            _parties[party.Id] = party;
            _of[host] = party.Id;
        }
        party.Members.Add(peer);
        _of[peer] = party.Id;
        return party;
    }

    /// <summary>
    /// <paramref name="peer"/> leaves. Returns the party they left (null if none); <paramref name="disbanded"/>
    /// when that left one member, who is then out of it too.
    /// </summary>
    public Party? Leave(long peer, out bool disbanded)
    {
        disbanded = false;
        if (Of(peer) is not { } party) return null;
        party.Members.Remove(peer);
        _of.Remove(peer);
        if (party.Members.Count <= 1)
        {
            disbanded = true;
            foreach (long m in party.Members) _of.Remove(m);
            party.Members.Clear();
            _parties.Remove(party.Id);
        }
        return party;
    }

    /// <summary>Why <paramref name="leader"/> may not put <paramref name="peer"/> out, or null.</summary>
    public string? MayKick(long leader, long peer)
    {
        if (Of(leader) is not { } party) return "You are not in a party.";
        if (party.Leader != leader) return "Only the party's leader puts someone out.";
        if (peer == leader) return "To leave, use /party leave.";
        return party.Members.Contains(peer) ? null : "Not in your party.";
    }
}
