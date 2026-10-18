namespace UnitSport.Net;

/// <summary>
/// One invite (#840): someone asks a player, or everyone online, to do something together. The kind
/// says what (<see cref="InviteKind"/>); <see cref="Key"/> is the kind's own handle on the thing it
/// points at (a race id, a lobby's seed, a party), so the kind can withdraw it when that is gone.
/// </summary>
public sealed class Invite
{
    /// <summary>Who an invite to everyone is addressed to.</summary>
    public const long Everyone = 0;

    public int Id { get; init; }
    public string Kind { get; init; } = "";
    /// <summary>The sender's peer id; 0 for the server's console (only for an invite to everyone).</summary>
    public long From { get; init; }
    /// <summary>The one player asked, or <see cref="Everyone"/>.</summary>
    public long To { get; init; }
    /// <summary>The line the recipient reads: "Nico invites you to a Battle Royale".</summary>
    public string Title { get; init; } = "";
    /// <summary>The second line: "Zug, 6 × 6 km, duos".</summary>
    public string Detail { get; init; } = "";
    public long Key { get; init; }
    /// <summary>Server clock, s, at which it lapses.</summary>
    public double Expires { get; set; }
    /// <summary>An invite to everyone: the peers who have answered it (or no longer need it).</summary>
    public HashSet<long> Answered { get; } = new();

    public bool ToEveryone => To == Everyone;

    /// <summary>Whether <paramref name="peer"/> is asked by it and has not answered yet.</summary>
    public bool Asks(long peer) => ToEveryone ? peer != From && !Answered.Contains(peer) : To == peer;
}

/// <summary>
/// The server's open invites (#840), as plain data and rules: no Godot, unit-tested. The node
/// (<see cref="InviteService"/>) sends what changes to the peers and runs each kind's accept.
/// <list type="bullet">
/// <item>One live invite per (kind, sender, recipient, key): sending it again replaces it and restarts its timer.</item>
/// <item>An invite to one player is gone once answered; an invite to everyone is answered per peer.</item>
/// <item>At most <see cref="MaxPerSender"/> open per sender; lifetimes clamped to <see cref="MinSeconds"/>..<see cref="MaxSeconds"/>.</item>
/// </list>
/// </summary>
public sealed class InviteBook
{
    public const int MaxPerSender = 8;
    public const double MinSeconds = 10, MaxSeconds = 300;

    private readonly Dictionary<int, Invite> _open = new();
    private int _nextId = 1;

    public int Count => _open.Count;
    public IEnumerable<Invite> All => _open.Values;

    public Invite? Find(int id) => _open.GetValueOrDefault(id);

    /// <summary>
    /// Opens an invite. <paramref name="replaced"/> is the one it takes the place of (same kind, sender,
    /// recipient and key), already removed. Null with <paramref name="why"/> when refused.
    /// </summary>
    public Invite? Add(string kind, long from, long to, string title, string detail, long key, double now, double seconds,
        out Invite? replaced, out string? why)
    {
        replaced = null;
        why = null;
        if (to != Invite.Everyone && to == from) { why = "You cannot invite yourself."; return null; }
        if (to == Invite.Everyone && from < 0) { why = "Only a player or the server invites everyone."; return null; }
        replaced = _open.Values.FirstOrDefault(i => i.Kind == kind && i.From == from && i.To == to && i.Key == key);
        if (replaced != null) _open.Remove(replaced.Id);
        else if (from != 0 && _open.Values.Count(i => i.From == from) >= MaxPerSender)
        {
            why = $"You have {MaxPerSender} invites waiting already.";
            return null;
        }
        var invite = new Invite
        {
            Id = _nextId++, Kind = kind, From = from, To = to, Title = title, Detail = detail, Key = key,
            Expires = now + Math.Clamp(seconds, MinSeconds, MaxSeconds),
        };
        _open[invite.Id] = invite;
        return invite;
    }

    /// <summary>The open invites asking <paramref name="peer"/>, newest first.</summary>
    public IEnumerable<Invite> To(long peer) => _open.Values.Where(i => i.Asks(peer)).OrderByDescending(i => i.Id);

    /// <summary>The open invites <paramref name="peer"/> sent, newest first.</summary>
    public IEnumerable<Invite> From(long peer) => _open.Values.Where(i => i.From == peer).OrderByDescending(i => i.Id);

    /// <summary>
    /// An open invite of <paramref name="kind"/> from <paramref name="to"/> to <paramref name="from"/>:
    /// the other way round. Sending one back answers it, for a kind that says so (a fight challenge).
    /// </summary>
    public Invite? Reverse(string kind, long from, long to) =>
        _open.Values.FirstOrDefault(i => i.Kind == kind && i.From == to && i.To == from);

    /// <summary>
    /// The invite <paramref name="peer"/> means by what they typed: an id ("#3" or "3"), a sender
    /// (<paramref name="peerByName"/>) or a kind ("br", "race"), else the newest one asking them.
    /// </summary>
    public Invite? Resolve(long peer, string typed, Func<string, long> peerByName)
    {
        var mine = To(peer).ToList();
        typed = typed.Trim();
        if (typed.Length == 0) return mine.FirstOrDefault();
        if (int.TryParse(typed.TrimStart('#'), out int id)) return mine.FirstOrDefault(i => i.Id == id);
        if (mine.FirstOrDefault(i => i.Kind.Equals(typed, StringComparison.OrdinalIgnoreCase)) is { } byKind) return byKind;
        long from = peerByName(typed);
        return from > 0 ? mine.FirstOrDefault(i => i.From == from) : null;
    }

    /// <summary>
    /// <paramref name="peer"/> answered (or no longer needs) <paramref name="invite"/>. True when it is
    /// gone for good: an invite to one player always, an invite to everyone never (others may still answer).
    /// </summary>
    public bool Answer(Invite invite, long peer)
    {
        if (invite.ToEveryone)
        {
            invite.Answered.Add(peer);
            return false;
        }
        return _open.Remove(invite.Id);
    }

    /// <summary>Removes and returns the invites lapsed at <paramref name="now"/>.</summary>
    public List<Invite> Expire(double now) => Take(i => now >= i.Expires);

    /// <summary>Removes and returns the invites of <paramref name="kind"/> pointing at <paramref name="key"/>.</summary>
    public List<Invite> Withdraw(string kind, long key) => Take(i => i.Kind == kind && i.Key == key);

    /// <summary>Removes and returns the invites of <paramref name="kind"/> that <paramref name="peer"/> sent or is the one asked by.</summary>
    public List<Invite> WithdrawInvolving(string kind, long peer) => Take(i => i.Kind == kind && (i.From == peer || i.To == peer));

    /// <summary>Removes one invite; null if it was not open.</summary>
    public Invite? Withdraw(int id) => _open.Remove(id, out var i) ? i : null;

    /// <summary>
    /// <paramref name="peer"/> left: every invite they sent, and every one asking only them, is gone.
    /// Invites to everyone forget their answer, so the peer id can be used again.
    /// </summary>
    public List<Invite> PeerLeft(long peer)
    {
        foreach (var i in _open.Values) i.Answered.Remove(peer);
        return Take(i => i.From == peer || i.To == peer);
    }

    private List<Invite> Take(Func<Invite, bool> which)
    {
        var gone = _open.Values.Where(which).ToList();
        foreach (var i in gone) _open.Remove(i.Id);
        return gone;
    }
}
