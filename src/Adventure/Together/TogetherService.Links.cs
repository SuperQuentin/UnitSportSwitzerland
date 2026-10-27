using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// The links (#1266 part 3, <c>docs/notes/adventure/links.md</c>): holding hands and the rope. The server keeps the
/// <see cref="LinkBook"/> and sends it whole to every peer on any change (<c>Links</c>, a few bytes); everyone draws
/// the hands and the ropes from it and the bodies' replicated positions. A body simulated here (the own, a check's
/// partner, a server-run bot) gets the tether on its walk intent, the rope that catches its falls and the company
/// for its stamina; nothing new is sent per frame.
/// </summary>
public partial class TogetherService
{
    /// <summary>The invite kind that asks to hold hands or rope up (key: the <see cref="LinkMode"/>).</summary>
    public const string RopeKind = "rope";
    /// <summary>Asking from this far at most, m (a hand needs reach, a rope a few steps).</summary>
    public const float AskHandM = 4f, AskRopeM = 12f;

    /// <summary>The links: the server's (and offline the client's) truth, a mirror on clients.</summary>
    public LinkBook Links { get; } = new();
    /// <summary>The book changed (any peer).</summary>
    public event Action? LinksChanged;

    /// <summary>Glacier or snowfield cover under a point (the chunks' cover raster, kept near players), or null when unknown.</summary>
    public Func<GlobalPos, bool?>? GlacierAt { get; set; }
    /// <summary>Client: shows a line to the local player (the chat's toast).</summary>
    public Action<string>? Toast { get; set; }

    /// <summary>
    /// <c>--together-glacier E,N,R</c> (checks, on every peer): glacier cover within R m of (E, N), whatever the terrain
    /// says (the fixture course has none).
    /// </summary>
    public static (double E, double N, double R)? GlacierBox { get; set; } = ParseGlacier();

    private static (double, double, double)? ParseGlacier()
    {
        if (CmdArgs.Value("--together-glacier") is not { } v) return null;
        var p = v.Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return p.Length == 3 && double.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out double e)
            && double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out double n)
            && double.TryParse(p[2], System.Globalization.NumberStyles.Float, inv, out double r) ? (e, n, r) : null;
    }

    /// <summary>Glacier cover at a point: the check's box first, then the terrain's cover.</summary>
    public bool OnGlacier(GlobalPos at)
    {
        if (GlacierBox is { } box && (at.E - box.E) * (at.E - box.E) + (at.N - box.N) * (at.N - box.N) <= box.R * box.R) return true;
        return GlacierAt?.Invoke(at) == true;
    }

    private ChatManager? Chat => GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName);

    partial void ReadyLinks()
    {
        if (!_server) return;
        InviteService.Server?.Register(new InviteKind
        {
            Id = RopeKind, Label = "together", Seconds = 30, Mutual = true, TellAccepted = false,
            Accept = (peer, invite) => ServeLink(invite.From, peer, invite.Key == (long)LinkMode.Rope ? LinkMode.Rope : LinkMode.Hand) is { } why
                ? $"[together] {why}" : $"[together] You and {Who(invite.From)} are {(invite.Key == (long)LinkMode.Rope ? "roped up" : "holding hands")}.",
        });
        Multiplayer.PeerDisconnected += id => ServeUnlink(id, "left the server");
        _adventure.Loaded += SendLinksTo;
    }

    // ---- authority ---------------------------------------------------------------------------------

    /// <summary>Whether <paramref name="peer"/> may be asked to link by the local player now (a party member, adventuring here).</summary>
    public bool MayAskLink(long peer)
    {
        if (Permissions.InMatch || peer <= 0 || peer == Self) return false;
        foreach (long m in MyParty()) if (m == peer) return true;
        return false;
    }

    /// <summary>
    /// Asks <paramref name="to"/> to hold hands or rope up (hold E at them, the Activities row, <c>/rope</c>): an invite
    /// of kind <see cref="RopeKind"/>. Two asking each other link at once. Null when sent, else why not.
    /// </summary>
    public string? ServeAsk(long from, long to, LinkMode mode)
    {
        if (!Authority) return "Not here.";
        if (!_adventure.Active(from) || !_adventure.Active(to)) return Refuse(from, "No adventuring now.");
        if (!SameParty(from, to)) return Refuse(from, "Only with a party member.");
        if (Links.CanLink(from, to, mode) is { } why) return Refuse(from, why);
        if (Apart(from, to) is not float d || d > (mode == LinkMode.Rope ? AskRopeM : AskHandM)) return Refuse(from, "Get closer first.");
        if (!_networked || to == PairPeer)
        {
            // offline (a check's partner): it takes the hand at once
            return ServeLink(from, to, mode) is { } no ? Refuse(from, no) : null;
        }
        if (InviteService.Server is not { } invites) return Refuse(from, "Invites are not running.");
        string reply = invites.Send(RopeKind, from, to, mode == LinkMode.Rope ? $"{Who(from)} wants to rope up with you" : $"{Who(from)} holds out a hand",
            mode == LinkMode.Rope ? "A rope team: a fall is caught" : "Hold hands: walk together", out var sent, (long)mode, 30);
        Chat?.Tell(from, $"[together] {reply}", ChatKind.System);
        return sent == null && !reply.StartsWith("[together]", StringComparison.Ordinal) ? reply : null;
    }

    /// <summary>
    /// Ties <paramref name="follower"/> to <paramref name="leader"/> (the invite's accept, a bot's call, the checks'
    /// seam): both adventuring, in one party, close enough; then the book, and every peer is told. Null when tied.
    /// </summary>
    public string? ServeLink(long leader, long follower, LinkMode mode)
    {
        if (!Authority) return "Not here.";
        // a server-run bot (#837) is in no party: it may be led (a body, no brain asks yet)
        bool bot = Body(follower) is { Bot: true } || Body(leader) is { Bot: true };
        if (bot) { _adventure.SessionFor(leader); _adventure.SessionFor(follower); }
        if (!_adventure.Active(leader) || !_adventure.Active(follower)) return "No adventuring now.";
        if (!bot && !SameParty(leader, follower)) return "Only with a party member.";
        if (Apart(leader, follower) is not float d || d > (mode == LinkMode.Rope ? AskRopeM : AskHandM) + 2f) return "Too far apart.";
        if (Links.Link(leader, follower, mode) is { } why) return why;
        if (mode == LinkMode.Rope) Links.Force(leader, true);
        GD.Print($"[together] server link {leader}>{follower} {mode.ToString().ToLowerInvariant()}");
        BroadcastLinks();
        return null;
    }

    /// <summary>Unties <paramref name="peer"/> from everyone: letting go, leaving the party or the server, a vehicle, a round.</summary>
    public void ServeUnlink(long peer, string why)
    {
        if (!Authority) return;
        var broken = Links.Unlink(peer);
        if (broken.Count == 0) return;
        foreach (var l in broken)
        {
            GD.Print($"[together] server unlink {l.Leader}>{l.Follower} ({why})");
            long other = l.Leader == peer ? l.Follower : l.Leader;
            Tell(other, $"{Who(peer)} let go ({why}).");
        }
        BroadcastLinks();
    }

    /// <summary>
    /// A haul (hold E at the rope): the partner hanging below <paramref name="peer"/> on its rope rises toward it while
    /// <paramref name="on"/>. The hanging owner moves its own body (<c>TellHaul</c>).
    /// </summary>
    public void ServeHaul(long peer, bool on)
    {
        if (!Authority || Links.ModeOf(peer) != LinkMode.Rope || PositionOf(peer) is not { } at) return;
        foreach (long other in new[] { Links.LeaderOf(peer), Links.FollowerOf(peer) })
        {
            if (other == 0 || PositionOf(other) is not { } p || p.Alt > at.Alt - 1.5) continue;
            GD.Print($"[together] haul {peer} -> {other} {(on ? "on" : "off")}");
            if (Connected(other)) RpcId(other, MethodName.TellHaul, on);
            else if (!_networked || Body(other) is { Bot: true }) SetHauled(other, on);
        }
    }

    private float? Apart(long a, long b) =>
        PositionOf(a) is { } pa && PositionOf(b) is { } pb ? (float)pa.DistanceTo(pb) : null;

    private string Refuse(long peer, string why)
    {
        Tell(peer, why);
        return why;
    }

    /// <summary>A line to a player: its chat online, the toast offline (a check's partner hears nothing).</summary>
    private void Tell(long peer, string line)
    {
        if (Connected(peer)) Chat?.Tell(peer, $"[together] {line}", ChatKind.System);
        else if (!_networked && peer == AdventureService.LocalPeer) Toast?.Invoke(line);
    }

    private void BroadcastLinks()
    {
        var (f, l, m) = Links.Wire();
        // to every adventuring peer: everyone draws the hands and the ropes
        if (_server)
            foreach (long peer in _adventure.Peers)
                if (Connected(peer)) RpcId(peer, MethodName.TellLinks, f, l, m);
        OnLinksChanged();
    }

    private void SendLinksTo(long peer)
    {
        if (!_server || !Connected(peer)) return;
        var (f, l, m) = Links.Wire();
        RpcId(peer, MethodName.TellLinks, f, l, m);
    }

    // per pair, the authority's timers: hands pulled apart, far apart
    private readonly Dictionary<long, (float Apart, float Far, double GlacierM)> _pairs = new();
    private readonly List<Link> _linkScratch = new();
    private double _nextLinkTick;
    private const double LinkTickEvery = 0.25;

    private static bool OnFoot(FootPlayer? b) => b != null && b.RideKindId == 0 && b.RidingWith == 0;
    private static bool OwnVehicle(FootPlayer? b) => b != null && b.RideKindId != 0 && b.RidingWith == 0;
    private static bool Climbing(FootPlayer? b) => b != null && b.PoseKind == FootPlayer.PoseWallClimb;
    private static bool Swimming(FootPlayer? b) => b != null && b.RideKindId == 0 && b.PoseKind == FootPlayer.PoseSwim;

    /// <summary>The authority's 4 Hz look at every link: breaks, and hands paying out to a rope and back.</summary>
    private void TickLinksAuthority(double now)
    {
        if (now < _nextLinkTick || Links.Count == 0) return;
        float dt = (float)(now - _nextLinkTick + LinkTickEvery);
        _nextLinkTick = now + LinkTickEvery;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _linkScratch.Clear();
        foreach (var l in Links.All) _linkScratch.Add(l);
        bool changed = false;
        foreach (var link in _linkScratch)
        {
            if (Links.Between(link.Follower, link.Leader) is not { } l) continue;
            var pa = PositionOf(l.Follower);
            var pb = PositionOf(l.Leader);
            var ba = Body(l.Follower);
            var bb = Body(l.Leader);
            var timers = _pairs.GetValueOrDefault(l.Follower);
            float dist = pa is { } a && pb is { } b ? (float)a.DistanceTo(b) : 1e6f;
            bool bot = ba is { Bot: true } || bb is { Bot: true };
            bool inactive = !_adventure.Active(l.Follower) || !_adventure.Active(l.Leader) || !bot && !SameParty(l.Follower, l.Leader)
                || pa == null || pb == null;
            float far = timers.Far;
            if (LinkRules.Breaks(dist, ref far, dt, OwnVehicle(ba) || OwnVehicle(bb), inactive))
            {
                Links.UnlinkPair(l.Follower, l.Leader);
                _pairs.Remove(l.Follower);
                string why = inactive ? "not adventuring together" : OwnVehicle(ba) || OwnVehicle(bb) ? "a vehicle" : "too far apart";
                GD.Print($"[together] server unlink {l.Leader}>{l.Follower} ({why})");
                Tell(l.Follower, $"The link broke ({why}).");
                Tell(l.Leader, $"The link broke ({why}).");
                changed = true;
                continue;
            }
            bool glacier = pa is { } ga && OnGlacier(ga) || pb is { } gb && OnGlacier(gb);
            bool rough = glacier || Climbing(ba) || Climbing(bb) || Swimming(ba) || Swimming(bb);
            bool bothOnFoot = OnFoot(ba) && OnFoot(bb) && ba!.PoseKind == FootPlayer.PoseStride && bb!.PoseKind == FootPlayer.PoseStride;
            float apart = timers.Apart;
            var next = LinkRules.Next(l.Mode, Links.Forced(l.Follower), Links.ChainSize(l.Follower), rough, bothOnFoot, dist, ref apart, dt);
            double glacierM = timers.GlacierM;
            _pairs[l.Follower] = (apart, far, glacierM);
            if (next != l.Mode && Links.SetMode(l.Follower, next))
            {
                GD.Print(FormattableString.Invariant($"[together] server {l.Leader}>{l.Follower} now {next.ToString().ToLowerInvariant()} ({dist:F1} m{(glacier ? ", glacier" : "")}{(rough && !glacier ? ", climbing or swimming" : "")})"));
                changed = true;
            }
        }
        if (changed) BroadcastLinks();
        TickGlacierCrossing(dt);
        ServerStats.Ran("together", t0);
    }

    partial void TickGlacierCrossing(float dt);

    // ---- every peer: the book arrives, the hands and the ropes are drawn ---------------------------------------

    /// <summary>Server to every peer: the whole link book (the plan's <c>Links</c>), on any change and to a peer that arrives.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellLinks(long[] followers, long[] leaders, byte[] modes)
    {
        if (_server) return;
        Links.Load(followers, leaders, modes);
        OnLinksChanged();
    }

    private readonly Dictionary<long, Link> _shown = new();
    private readonly List<long> _gone = new();

    /// <summary>What changed since the last book: logged, the clink on a new rope, the trails and rope nodes kept in step.</summary>
    private void OnLinksChanged()
    {
        long self = Self;
        foreach (var l in Links.All)
        {
            bool had = _shown.TryGetValue(l.Follower, out var was);
            if (had && was == l) continue;
            if (!had || was.Leader != l.Leader) ResetTrail(l.Follower);
            GD.Print($"[together] link {Who(l.Leader)}>{Who(l.Follower)} {l.Mode.ToString().ToLowerInvariant()}");
            if (l.Mode == LinkMode.Rope && (!had || was.Mode != LinkMode.Rope) && (l.Leader == self || l.Follower == self || l.Follower == PairPeer))
                PlayAt(l.Follower, TogetherSounds.Clink);
            _shown[l.Follower] = l;
        }
        _gone.Clear();
        foreach (var (follower, l) in _shown)
            if (Links.Between(follower, l.Leader) == null) _gone.Add(follower);
        foreach (long follower in _gone)
        {
            var l = _shown[follower];
            _shown.Remove(follower);
            GD.Print($"[together] unlink {Who(l.Leader)}>{Who(l.Follower)}");
        }
        SyncRopes();
        LinksChanged?.Invoke();
    }

    /// <summary>Whether the local player is tied to anyone.</summary>
    public bool LinkedHere => Links.IsLinked(Self);

    private static string? _promptTap, _promptHeld;

    /// <summary>
    /// The prompt for E at a player (the fight's <paramref name="tap"/>): a party member who may be linked adds
    /// "hold: hold hands". Cached by the tap's text: no string a frame.
    /// </summary>
    public static string LinkPrompt(long peer, string tap)
    {
        if (Instance is not { } s || !s.MayAskLink(peer) || s.Links.Between(s.Self, peer) != null) return tap;
        if (!ReferenceEquals(tap, _promptTap)) (_promptTap, _promptHeld) = (tap, $"{tap} Â· hold: hold hands");
        return _promptHeld!;
    }

    private long _haulShown;
    private string _haulPrompt = "";

    /// <summary>E with nothing else at hand while tied: "Hold: haul Lea up" over a hanging partner, else "Let go". Null when free.</summary>
    public string? InteractPrompt(FootPlayer me)
    {
        if (PotPrompt() is { } pot) return pot;
        if (!LinkedHere) return null;
        long hanging = HaulTarget(me);
        if (hanging == 0) return "Let go";
        if (hanging != _haulShown) (_haulShown, _haulPrompt) = (hanging, $"Hold: haul {Who(hanging)} up");
        return _haulPrompt;
    }

    // ---- the owner's requests ---------------------------------------------------------------------------

    public void AskLink(long peer, LinkMode mode)
    {
        if (Authority) ServeAsk(Self, peer, mode);
        else RpcId(1, MethodName.RequestLink, peer, (byte)mode);
    }

    public void AskUnlink()
    {
        if (Authority) ServeUnlink(Self, "let go");
        else RpcId(1, MethodName.RequestUnlink);
    }

    public void AskHaul(bool on)
    {
        if (Authority) ServeHaul(Self, on);
        else RpcId(1, MethodName.RequestHaul, on);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLink(long peer, byte mode) { if (_server) ServeAsk(Multiplayer.GetRemoteSenderId(), peer, (LinkMode)mode); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestUnlink() { if (_server) ServeUnlink(Multiplayer.GetRemoteSenderId(), "let go"); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestHaul(bool on) { if (_server) ServeHaul(Multiplayer.GetRemoteSenderId(), on); }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellHaul(bool on)
    {
        if (_server) return;
        SetHauled(Self, on);
        if (on) PlayAt(Self, TogetherSounds.Haul);
    }

    /// <summary>
    /// <c>/rope &lt;player&gt;</c> asks to rope up, <c>/rope hands &lt;player&gt;</c> to hold hands, <c>/rope off</c> lets go,
    /// <c>/rope</c> says who you are tied to. The reply.
    /// </summary>
    public string Command(long sender, string verb, string rest) => verb == "rope" ? RopeCommand(sender, rest) : FondueCommand(sender, rest);

    /// <summary>The <c>/rope</c> half of <see cref="Command"/>.</summary>
    public string RopeCommand(long sender, string rest)
    {
        long peer = _networked ? sender : AdventureService.LocalPeer;
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            if (!Links.IsLinked(peer)) return "[together] You are tied to nobody. /rope <player> ropes up with a party member.";
            var chain = Links.Chain(peer);
            return $"[together] {(Links.ModeOf(peer) == LinkMode.Rope ? "On the rope" : "Holding hands")}: {string.Join(" - ", chain.Select(Who))}.";
        }
        if (words[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (!Links.IsLinked(peer)) return "[together] You are tied to nobody.";
            ServeUnlink(peer, "let go");
            return "[together] You let go.";
        }
        var mode = LinkMode.Rope;
        if (words[0].Equals("hands", StringComparison.OrdinalIgnoreCase)) { mode = LinkMode.Hand; words = words[1..]; }
        string name = string.Join(' ', words);
        long other = PeerNamed(name);
        if (other <= 0) return $"[together] No party member matching '{name}'.";
        if (ServeAsk(peer, other, mode) is { } why) return $"[together] {why}";
        return "";
    }

    private long PeerNamed(string name)
    {
        if (Party(_networked ? 0 : AdventureService.LocalPeer) is { } offline && !_networked)
            foreach (long p in offline) if (Who(p).Equals(name, StringComparison.OrdinalIgnoreCase) || p.ToString() == name) return p;
        return Chat?.PeerByName(name) ?? -1;
    }
}
