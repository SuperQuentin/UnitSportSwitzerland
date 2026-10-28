using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// Playing together (#1266, <c>docs/plans/adventure-together.md</c>), at <c>World/Together</c> beside
/// <c>World/Adventure</c> on the server and every client (RPCs route by node path), made when
/// <see cref="Systems.Adventure"/> is on. Offline the client plays the server's part (peer
/// <see cref="AdventureService.LocalPeer"/>), as the Adventure service does.
///
/// <para>
/// <b>The together rule</b> (<see cref="TogetherRules"/>): a party member, adventuring, not knocked out,
/// within 60 m. The server's <see cref="Near(long)"/> counts it for everything the server grants (quest
/// cash, the week's seconds, mentors); the owner's cached <see cref="NearHere"/> (1 Hz, from the party's
/// replicated bodies) for its own loot: gathering, the hand harvest, fishing.
/// </para>
///
/// <para>
/// The other parts live in the partial files: the links (hands and the rope, <c>.Links</c>), the shared pot
/// (<c>.Pots</c>), the week, the punch card and the mentors (<c>.Progress</c>). Docs:
/// <c>docs/notes/adventure/together.md</c>.
/// </para>
/// </summary>
public partial class TogetherService : Node
{
    public const string NodeName = "Together";

    public static TogetherService? Instance { get; private set; }

    /// <summary>The local player's friends near now (0 when the service is off): what local loot multiplies by.</summary>
    public static int LocalNear => Instance?.NearHere ?? 0;

    private bool _server, _networked;
    private AdventureService _adventure = null!;

    /// <summary>This copy decides: the server, or an offline client.</summary>
    public bool Authority => _server || !_networked;

    // ---- hooks, set by the world that makes it --------------------------------------------------------

    /// <summary>Server: the peers of <paramref name="peer"/>'s party (it included), or null when in none.</summary>
    public Func<long, IReadOnlyList<long>?>? PartyOf { get; set; }
    /// <summary>A peer's body: on the server its proxy, on a client the remote copy (or the own body).</summary>
    public Func<long, FootPlayer?>? BodyOf { get; set; }
    /// <summary>Client: the local player's body (null in a vehicle's seat view or the free camera).</summary>
    public Func<FootPlayer?>? Me { get; set; }
    /// <summary>A peer's display name (chat lines, the pot panel).</summary>
    public Func<long, string>? NameOf { get; set; }

    public static TogetherService Create(AdventureService adventure, bool server, bool networked)
    {
        var s = new TogetherService { Name = NodeName, _server = server, _networked = networked, _adventure = adventure };
        adventure.GetParent().AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (_adventure.TogetherNear == Near) _adventure.TogetherNear = null;
    }

    public override void _Ready()
    {
        _adventure.TogetherNear = Near;
        ReadyLinks();
        ReadyPots();
        ReadyProgress();
        ReadyRoutes();
        GD.Print($"[together] {(_server ? "server" : _networked ? "client" : "offline")}");
    }

    // ---- the test seam (the checks' partner body) ------------------------------------------------------

    /// <summary>The checks' partner: a local body (an <c>Npc</c>) counted as a party member with this peer id. 0: none.</summary>
    public long PairPeer { get; private set; }
    private FootPlayer? _pairBody;

    /// <summary>
    /// <c>--togethercheck</c> / <c>--fonduecheck</c> offline (the way <c>BotProbe</c> drives a body): a local body
    /// becomes the local player's party member, peer <paramref name="peer"/>, with session progress
    /// (<see cref="AdventureService.SessionFor"/>). Never for players.
    /// </summary>
    public void Pair(FootPlayer body, long peer = 2)
    {
        PairPeer = peer;
        _pairBody = body;
        _adventure.SessionFor(peer);
        _nextNear = 0;
        GD.Print($"[together] paired with a test body as peer {peer}");
    }

    /// <summary>A body by peer: the seam's, else the world's.</summary>
    public FootPlayer? Body(long peer)
    {
        if (peer == PairPeer && peer != 0) return _pairBody is { } b && IsInstanceValid(b) ? b : null;
        if (!_networked && peer == AdventureService.LocalPeer) return Me?.Invoke();
        return BodyOf?.Invoke(peer);
    }

    /// <summary>Where a peer is (LV95): the seam's body, else the Adventure service's own lookup.</summary>
    public GlobalPos? PositionOf(long peer) =>
        peer == PairPeer && peer != 0 ? Body(peer)?.Global : _adventure.PositionOf?.Invoke(peer);

    /// <summary>The peers of a party (the one asked included); offline, the local player and the seam's body.</summary>
    public IReadOnlyList<long>? Party(long peer)
    {
        if (!_networked) return PairPeer != 0 && (peer == AdventureService.LocalPeer || peer == PairPeer) ? new[] { AdventureService.LocalPeer, PairPeer } : null;
        return PartyOf?.Invoke(peer);
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> share a party.</summary>
    public bool SameParty(long a, long b) => a != b && Party(a) is { } p && p.Contains(b);

    private bool IsDown(long peer) => Body(peer) is { } body && (body.Down != 0 || body.KnockedOut);

    // ---- the together rule, on the server ------------------------------------------------------------

    private readonly Mate[] _serverMates = new Mate[8];

    /// <summary>
    /// The friends counted round <paramref name="peer"/> (0..3): its party's members who adventure, are not knocked
    /// out and are within <see cref="TogetherRules.Radius"/> (or <paramref name="radius"/>). 0 when the peer itself does
    /// not adventure (a Battle Royale or Versteckis round: <see cref="AdventureService.Active"/>). Everything the server
    /// grants for playing together uses this.
    /// </summary>
    public int Near(long peer) => Near(peer, TogetherRules.Radius);

    public int Near(long peer, float radius)
    {
        if (!Authority || !_adventure.Active(peer) || PositionOf(peer) is not { } at || Party(peer) is not { } party) return 0;
        int n = 0;
        foreach (long m in party)
        {
            if (m == peer || n >= _serverMates.Length) continue;
            if (PositionOf(m) is not { } pos) continue;
            _serverMates[n++] = new Mate(pos.E, pos.N, pos.Alt, _adventure.Active(m), IsDown(m));
        }
        return TogetherRules.Count(at.E, at.N, at.Alt, _serverMates.AsSpan(0, n), radius);
    }

    // ---- the together rule, on the owner ------------------------------------------------------------

    /// <summary>Client: the local player's friends near (0..3), refreshed once a second.</summary>
    public int NearHere { get; private set; }
    /// <summary>Client: <see cref="NearHere"/> changed.</summary>
    public event Action<int>? NearChanged;

    private readonly Mate[] _mates = new Mate[8];
    private double _nextNear;

    /// <summary>The local player's id (1 offline, its own peer online).</summary>
    public long Self => _networked ? Multiplayer.GetUniqueId() : AdventureService.LocalPeer;

    /// <summary>The local player's party: peers, the player included (online, <c>Parties.Client</c>; offline, the seam).</summary>
    public IEnumerable<long> MyParty()
    {
        if (!_networked)
        {
            if (PairPeer != 0) { yield return AdventureService.LocalPeer; yield return PairPeer; }
            yield break;
        }
        if (Net.Parties.Client is not { InParty: true } party) yield break;
        foreach (var (peer, _) in party.Members) yield return peer;
    }

    private void RefreshNear()
    {
        int n = 0;
        if (!Permissions.InMatch && Me?.Invoke() is { } me && me.Down == 0 && !me.KnockedOut)
        {
            var at = me.Global;
            long self = Self;
            int count = 0;
            foreach (long peer in MyParty())
            {
                if (peer == self || count >= _mates.Length || Body(peer) is not { } body) continue;
                var g = body.Global;
                _mates[count++] = new Mate(g.E, g.N, g.Alt, true, body.Down != 0 || body.KnockedOut);
            }
            n = TogetherRules.Count(at.E, at.N, at.Alt, _mates.AsSpan(0, count));
        }
        if (n == NearHere) return;
        NearHere = n;
        GD.Print($"[together] near {n}");
        NearChanged?.Invoke(n);
    }

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        if (!_server && now >= _nextNear)
        {
            _nextNear = now + 1.0;
            RefreshNear();
        }
        ProcessLinks(delta, now);
        ProcessPots(delta, now);
        if (Authority) ProcessProgress(delta, now);
    }

    public override void _PhysicsProcess(double delta)
    {
        PhysicsLinks((float)delta);
        if (!_server) StepCrevasses((float)delta);
    }

    // the parts, each in its own file (partial methods: a part not built yet costs nothing)
    partial void ReadyLinks();
    partial void ReadyPots();
    partial void ReadyProgress();
    partial void ReadyRoutes();
    partial void ProcessLinks(double delta, double now);
    partial void ProcessPots(double delta, double now);
    partial void ProcessProgress(double delta, double now);
    partial void PhysicsLinks(float dt);

    private bool Connected(long peer) => peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    private string Who(long peer) => peer == PairPeer && peer != 0 ? "Partner" : NameOf?.Invoke(peer) ?? $"#{peer}";
}
