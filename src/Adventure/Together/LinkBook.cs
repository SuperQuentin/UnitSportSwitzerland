namespace UnitSport.Adventure.Together;

/// <summary>How two players are tied (#1266 part 3): holding hands, or roped up. Sent as a byte: append only.</summary>
public enum LinkMode : byte
{
    None = 0,
    /// <summary>Holding hands (Sky): pairs only, <see cref="LinkBook.HandLength"/> at most.</summary>
    Hand = 1,
    /// <summary>The rope (a Seilschaft): a chain of up to <see cref="LinkBook.MaxChain"/>, <see cref="LinkBook.RopeLength"/> between neighbours.</summary>
    Rope = 2,
}

/// <summary>A follower tied to its leader.</summary>
public readonly record struct Link(long Follower, long Leader, LinkMode Mode);

/// <summary>
/// Who is tied to whom (#1266 part 3.1): follower to leader pairs, a chain of up to four on a rope, a pair
/// holding hands. Plain C#, the server's (and an offline client's) truth, mirrored on every client from the
/// <c>Links</c> RPC (<see cref="Wire"/>, <see cref="Load"/>). Linked into tier 0 (<c>LinkBookTests</c>).
/// </summary>
public sealed class LinkBook
{
    /// <summary>Hands at most this far apart, m.</summary>
    public const float HandLength = 1.6f;
    /// <summary>A rope goes back to hands when both are on foot within this, m.</summary>
    public const float HandBackWithin = 1.2f;
    /// <summary>Rope between neighbours, m.</summary>
    public const float RopeLength = 8f;
    /// <summary>More than this apart for <see cref="BreakSeconds"/>: the link breaks (lag, a teleport).</summary>
    public const float BreakDistance = 25f;
    public const float BreakSeconds = 3f;
    /// <summary>Hands more than <see cref="HandLength"/> apart this long pay out to a rope.</summary>
    public const float PayOutSeconds = 1f;
    /// <summary>A rope team: four at most.</summary>
    public const int MaxChain = 4;

    private readonly Dictionary<long, Link> _up = new();
    private readonly Dictionary<long, long> _down = new();
    /// <summary>Chains forced to the rope ("Rope up"), by their head.</summary>
    private readonly HashSet<long> _forced = new();

    public int Count => _up.Count;
    /// <summary>Every link (a struct enumerator: no allocation per frame).</summary>
    public Dictionary<long, Link>.ValueCollection All => _up.Values;

    /// <summary>Who <paramref name="peer"/> follows, 0 for nobody.</summary>
    public long LeaderOf(long peer) => _up.TryGetValue(peer, out var l) ? l.Leader : 0;
    /// <summary>Who follows <paramref name="peer"/>, 0 for nobody.</summary>
    public long FollowerOf(long peer) => _down.GetValueOrDefault(peer);
    public bool IsLinked(long peer) => _up.ContainsKey(peer) || _down.ContainsKey(peer);

    /// <summary>The mode <paramref name="peer"/> is tied in (a chain has one), <see cref="LinkMode.None"/> when free.</summary>
    public LinkMode ModeOf(long peer) =>
        _up.TryGetValue(peer, out var l) ? l.Mode : _down.TryGetValue(peer, out long f) && _up.TryGetValue(f, out var d) ? d.Mode : LinkMode.None;

    /// <summary>The link between two peers either way, or null.</summary>
    public Link? Between(long a, long b) =>
        _up.TryGetValue(a, out var l) && l.Leader == b ? l : _up.TryGetValue(b, out var m) && m.Leader == a ? m : null;

    /// <summary>The top of <paramref name="peer"/>'s chain (itself when it follows nobody).</summary>
    public long Head(long peer)
    {
        long p = peer;
        for (int guard = 0; guard < 16 && _up.TryGetValue(p, out var l); guard++) p = l.Leader;
        return p;
    }

    /// <summary>The chain <paramref name="peer"/> is in, from its head down (just the peer when free).</summary>
    public List<long> Chain(long peer)
    {
        var list = new List<long>();
        long p = Head(peer);
        for (int guard = 0; guard < 16 && p != 0; guard++)
        {
            list.Add(p);
            p = _down.GetValueOrDefault(p);
        }
        return list;
    }

    public int ChainSize(long peer) => IsLinked(peer) ? Chain(peer).Count : 1;

    /// <summary>
    /// The one other end for a pair, or the nearer neighbour in a chain (the leader first): whom the
    /// owner's tether and belay read. 0 when free.
    /// </summary>
    public long PartnerOf(long peer) => LeaderOf(peer) is long l and not 0 ? l : FollowerOf(peer);

    /// <summary>Why <paramref name="leader"/> may not take <paramref name="follower"/> in <paramref name="mode"/>, or null.</summary>
    public string? CanLink(long leader, long follower, LinkMode mode)
    {
        if (leader == follower || leader == 0 || follower == 0) return "Not with yourself.";
        if (mode == LinkMode.None) return "No such link.";
        if (Between(leader, follower) != null) return null;   // already tied: a mode change
        if (_up.ContainsKey(follower)) return "They already follow someone.";
        if (_down.ContainsKey(leader)) return "Someone already follows you.";
        if (Head(leader) == follower) return "You are already on their rope.";
        int size = ChainSize(leader) + ChainSize(follower);
        if (mode == LinkMode.Hand && size > 2) return "Hands are for two. Rope up for more.";
        if (size > MaxChain) return $"A rope team is {MaxChain} at most.";
        return null;
    }

    /// <summary>
    /// Ties <paramref name="follower"/> to <paramref name="leader"/> (or changes the mode of the link they have). A
    /// chain of three or more is always a rope. Null when done, else why not.
    /// </summary>
    public string? Link(long leader, long follower, LinkMode mode)
    {
        if (CanLink(leader, follower, mode) is { } why) return why;
        if (Between(leader, follower) != null)
        {
            SetMode(leader, mode);
            return null;
        }
        long oldHead = Head(follower);
        _up[follower] = new Link(follower, leader, mode);
        _down[leader] = follower;
        // a forced half keeps the whole forced; one mode per chain: a third on the rope makes it all a rope
        if (_forced.Remove(oldHead)) _forced.Add(Head(leader));
        if (ChainSize(leader) > 2 || mode == LinkMode.Rope || Forced(leader)) SetMode(leader, LinkMode.Rope);
        return null;
    }

    /// <summary>Sets the mode of <paramref name="peer"/>'s whole chain. Hands only for a pair. True when it changed.</summary>
    public bool SetMode(long peer, LinkMode mode)
    {
        if (!IsLinked(peer) || mode == LinkMode.None) return false;
        var chain = Chain(peer);
        if (mode == LinkMode.Hand && chain.Count > 2) return false;
        bool changed = false;
        foreach (long p in chain)
            if (_up.TryGetValue(p, out var l) && l.Mode != mode)
            {
                _up[p] = l with { Mode = mode };
                changed = true;
            }
        return changed;
    }

    /// <summary>"Rope up" (the Activities screen): the chain stays a rope whatever the ground until untied.</summary>
    public void Force(long peer, bool rope)
    {
        if (!IsLinked(peer)) return;
        if (rope) { _forced.Add(Head(peer)); SetMode(peer, LinkMode.Rope); }
        else _forced.Remove(Head(peer));
    }

    public bool Forced(long peer) => _forced.Contains(Head(peer));

    /// <summary>
    /// Unties <paramref name="peer"/> from everyone (letting go, leaving, a vehicle, inactive): the links it was in,
    /// which the server tells both sides. A chain splits round it; what is left of a chain of two may be a pair again.
    /// </summary>
    public List<Link> Unlink(long peer)
    {
        var broken = new List<Link>();
        long head = Head(peer);
        bool forced = _forced.Remove(head);
        if (_up.Remove(peer, out var up))
        {
            _down.Remove(up.Leader);
            broken.Add(up);
        }
        if (_down.Remove(peer, out long f) && _up.Remove(f, out var down)) broken.Add(down);
        // the two halves keep the rope if the whole was forced and they are still tied
        if (forced)
            foreach (long rest in new[] { head == peer ? 0 : head, f })
                if (rest != 0 && IsLinked(rest)) _forced.Add(Head(rest));
        return broken;
    }

    /// <summary>Unties one link between two neighbours, or nothing.</summary>
    public Link? UnlinkPair(long a, long b)
    {
        if (Between(a, b) is not { } l) return null;
        bool forced = _forced.Remove(Head(a));
        _up.Remove(l.Follower);
        _down.Remove(l.Leader);
        if (forced)
        {
            if (IsLinked(l.Leader)) _forced.Add(Head(l.Leader));
            if (IsLinked(l.Follower)) _forced.Add(Head(l.Follower));
        }
        return l;
    }

    public void Clear()
    {
        _up.Clear();
        _down.Clear();
        _forced.Clear();
    }

    /// <summary>The book as the <c>Links</c> RPC carries it: three parallel arrays, a few bytes a link.</summary>
    public (long[] Followers, long[] Leaders, byte[] Modes) Wire()
    {
        var f = new long[_up.Count];
        var l = new long[_up.Count];
        var m = new byte[_up.Count];
        int i = 0;
        foreach (var link in _up.Values)
        {
            f[i] = link.Follower;
            l[i] = link.Leader;
            m[i++] = (byte)link.Mode;
        }
        return (f, l, m);
    }

    /// <summary>A client's mirror: the server's book, replaced whole.</summary>
    public void Load(long[] followers, long[] leaders, byte[] modes)
    {
        _up.Clear();
        _down.Clear();
        int n = Math.Min(followers.Length, Math.Min(leaders.Length, modes.Length));
        for (int i = 0; i < n; i++)
        {
            if (followers[i] == leaders[i] || _up.ContainsKey(followers[i]) || _down.ContainsKey(leaders[i])) continue;
            _up[followers[i]] = new Link(followers[i], leaders[i], (LinkMode)modes[i]);
            _down[leaders[i]] = followers[i];
        }
    }
}

/// <summary>
/// When a link changes by itself (#1266 part 3.1), pure: a pair's hands pay out to a rope on rough ground or when
/// pulled apart, a rope goes back to hands on easy ground side by side, and any link breaks far apart.
/// </summary>
public static class LinkRules
{
    /// <summary>
    /// The next mode of a pair (a chain of three or more, or a forced chain, is always a rope).
    /// <paramref name="rough"/>: either body on glacier or snowfield cover, climbing or swimming.
    /// <paramref name="bothOnFoot"/>: both walking on the floor. <paramref name="apart"/> counts the seconds over
    /// <see cref="LinkBook.HandLength"/> (kept by the caller per pair).
    /// </summary>
    public static LinkMode Next(LinkMode mode, bool forced, int chainSize, bool rough, bool bothOnFoot, float dist, ref float apart, float dt)
    {
        if (forced || chainSize > 2) { apart = 0; return LinkMode.Rope; }
        if (mode == LinkMode.Hand)
        {
            if (rough) { apart = 0; return LinkMode.Rope; }
            apart = dist > LinkBook.HandLength ? apart + dt : 0;
            if (apart < LinkBook.PayOutSeconds) return LinkMode.Hand;
            apart = 0;
            return LinkMode.Rope;
        }
        apart = 0;
        return !rough && bothOnFoot && dist <= LinkBook.HandBackWithin ? LinkMode.Hand : LinkMode.Rope;
    }

    /// <summary>
    /// Whether a link breaks now: a vehicle that is not a shared ride, either side not adventuring, or more than
    /// <see cref="LinkBook.BreakDistance"/> apart for <see cref="LinkBook.BreakSeconds"/> (<paramref name="far"/>, the
    /// caller's per pair).
    /// </summary>
    public static bool Breaks(float dist, ref float far, float dt, bool ownVehicle, bool inactive)
    {
        if (ownVehicle || inactive) return true;
        far = dist > LinkBook.BreakDistance ? far + dt : 0;
        return far >= LinkBook.BreakSeconds;
    }

    /// <summary>The length a link allows, m.</summary>
    public static float Length(LinkMode mode) => mode == LinkMode.Rope ? LinkBook.RopeLength : LinkBook.HandLength;
}
