using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Net;

/// <summary>
/// The far-marker list (#1852, C3b of docs/plans/planets.md section 9), at <c>World/FarMarkers</c>: once a second the
/// server tells each peer where every other player is that is out of its interest (another body, or simply far): the
/// peer id, the body, the body-fixed place (three doubles) and what they ride, ~30 bytes a player. A player in the peer's
/// interest is skipped (it has the body already). Bots are not listed. The telescope labels these
/// (<see cref="Items.TelescopeView"/>) and its target menu follows them; names come from the chat's tag names.
/// </summary>
public partial class FarMarkers : Node
{
    public const string NodeName = "FarMarkers";

    /// <summary>The server sends the list this often, seconds.</summary>
    public const double PeriodSeconds = 1.0;

    /// <summary>One player out of this client's interest: where they were at the last list.</summary>
    public readonly record struct Marker(long Peer, GlobalPos At, RideKind Ride);

    /// <summary>Client: the list (this client's own) as the server last sent it.</summary>
    public static IReadOnlyList<Marker> Current => _current;
    private static List<Marker> _current = new();

    /// <summary>Client: how many lists arrived (a check waits for one).</summary>
    public static int Received { get; private set; }

    private bool _server;
    private InterestService? _interest;
    private Node? _players;
    private double _next;
    private readonly HashSet<long> _sentNonEmpty = new();
    private readonly List<(long Id, FootPlayer P)> _humans = new();
    private readonly List<long> _ids = new();
    private readonly List<int> _bodies = new();
    private readonly List<double> _xyz = new();
    private readonly List<int> _rides = new();

    public static FarMarkers CreateServer(Node world, InterestService interest, Node players)
    {
        var m = new FarMarkers { Name = NodeName, _server = true, _interest = interest, _players = players };
        world.AddChild(m);
        return m;
    }

    public static FarMarkers CreateClient(Node world)
    {
        _current = new();
        Received = 0;
        var m = new FarMarkers { Name = NodeName };
        world.AddChild(m);
        return m;
    }

    public override void _Ready()
    {
        if (_server) Multiplayer.PeerDisconnected += id => _sentNonEmpty.Remove(id);
    }

    public override void _Process(double delta)
    {
        if (!_server || _players == null || _interest == null || !NetLink.Online(this)) return;
        double now = RealClock.Now;
        if (now < _next) return;
        _next = now + PeriodSeconds;
        _humans.Clear();
        foreach (var child in _players.GetChildren())
            if (child is FootPlayer { Puppet: false } p && FootPlayer.NetId(p.Name) is long id && id > 0) _humans.Add((id, p));
        foreach (int peer in Multiplayer.GetPeers())
        {
            _ids.Clear(); _bodies.Clear(); _xyz.Clear(); _rides.Clear();
            foreach (var (id, p) in _humans)
            {
                if (id == peer || _interest.ServerSees(peer, id)) continue;
                var at = p.NetGlobal;
                if (!at.IsFinite) continue;
                var (x, y, z) = at.ToWire();
                _ids.Add(id);
                _bodies.Add(at.WireBody);
                _xyz.Add(x); _xyz.Add(y); _xyz.Add(z);
                _rides.Add((int)p.Ride);
            }
            // nothing to say twice: an empty list goes once, after a list that was not
            if (_ids.Count == 0 && !_sentNonEmpty.Remove(peer)) continue;
            if (_ids.Count > 0) _sentNonEmpty.Add(peer);
            RpcId(peer, MethodName.Markers, _ids.ToArray(), _bodies.ToArray(), _xyz.ToArray(), _rides.ToArray());
        }
    }

    /// <summary>Client: the server's list for this peer.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Markers(long[] ids, int[] bodies, double[] xyz, int[] rides)
    {
        var list = new List<Marker>(ids.Length);
        for (int i = 0; i < ids.Length && 3 * i + 2 < xyz.Length && i < bodies.Length && i < rides.Length; i++)
            list.Add(new Marker(ids[i], GlobalPos.FromWire(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2], (BodyId)bodies[i]), (RideKind)rides[i]));
        _current = list;
        Received++;
    }

    /// <summary>Client: a marker's name, from the chat's tag names (the peer id when unknown).</summary>
    public string NameOf(long peer) =>
        GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName) is { } chat && chat.TagNames.TryGetValue(peer, out var name) ? name : $"player {peer}";

    /// <summary>Client: the marker for <paramref name="peer"/> in the last list, if it is there.</summary>
    public static Marker? Of(long peer)
    {
        foreach (var m in _current)
            if (m.Peer == peer) return m;
        return null;
    }
}
