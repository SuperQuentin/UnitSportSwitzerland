using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.World;

/// <summary>
/// A traffic endpoint (#971): a point on a road (LV95), facing into the network along <see cref="Heading"/> (radians, LV95:
/// east 0, north π/2). As a <see cref="Generates">generator</see> it makes <see cref="PerMinute"/> cars a minute there, heading
/// that way (one that finds no room waits in its queue); as a <see cref="Destination">destination</see> it takes the cars
/// driving out past it, the other way. <see cref="Shares"/>: where its cars go, each a share of them (the user): to a
/// destination endpoint (its id, the way found to it) or along the road it stands on, picking its way at random
/// (<see cref="AlongHeading"/> its heading, <see cref="AgainstHeading"/> the other way); none is all along its heading.
/// <see cref="Mode"/> (#1134): what it makes and takes, cars (the only ones the shared list carries), riders, walkers or trams
/// (a world's own endpoints, <see cref="Traffic.WorldEndpoints"/>).
/// </summary>
public sealed record TrafficEndpoint(int Id, double E, double N, float Heading, bool Generates, bool Destination, float PerMinute,
    TrafficShare[]? Shares = null, MoverKind Mode = MoverKind.Car)
{
    /// <summary><see cref="TrafficShare.To"/> for cars that set off along the heading and pick their way at random.</summary>
    public const int AlongHeading = 0;
    /// <summary><see cref="TrafficShare.To"/> for cars that set off the other way along the road and pick their way at random.</summary>
    public const int AgainstHeading = -1;
}

/// <summary>A share of a generator's cars (#971): <see cref="Weight"/> of them go to <see cref="To"/>, a destination's id or a direction.</summary>
public readonly record struct TrafficShare(int To, float Weight);

/// <summary>
/// The traffic endpoints everyone's traffic runs (#971): the server's list, shared. Traffic is local and cosmetic per client
/// (<see cref="Traffic"/>), so every client runs the same endpoints on its own cars. An admin asks to set or remove one
/// (<see cref="RequestSet"/>, <see cref="RequestRemove"/>; the server checks <see cref="IsAdmin"/>), the server tells every
/// peer, and a joining peer gets the whole list. Offline this client plays the server's part through the same methods, which
/// is how <c>--trafficcheck</c> puts its own. Not saved: a session's. Same node path on server and client.
/// </summary>
public partial class TrafficGenerators : Node
{
    public const string NodeName = "TrafficGenerators";

    /// <summary>A generator's cars a minute when nothing says otherwise: a busy arm.</summary>
    public const float DefaultPerMinute = 12f;

    /// <summary>The most endpoints the server keeps (a stuck admin script cannot flood every client).</summary>
    private const int MaxEndpoints = 64;

    public static TrafficGenerators? Instance { get; private set; }

    /// <summary>Server: whether a peer may change the list (an admin).</summary>
    public Func<long, bool>? IsAdmin { get; set; }

    /// <summary>The list changed (an endpoint set, removed, or the server's snapshot).</summary>
    public event Action? Changed;

    public IReadOnlyCollection<TrafficEndpoint> All => _endpoints.Values;

    private bool _server;
    private int _nextId = 1;
    private readonly SortedDictionary<int, TrafficEndpoint> _endpoints = new();

    public static TrafficGenerators Create(Node world, bool server)
    {
        var g = new TrafficGenerators { Name = NodeName, _server = server };
        world.AddChild(g);
        if (!server) Instance = g;
        return g;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    // ---- client API -----------------------------------------------------------------------------

    /// <summary>Asks to add an endpoint (<see cref="TrafficEndpoint.Id"/> 0) or replace the one with its id.</summary>
    public void RequestSet(TrafficEndpoint ep)
    {
        int[] to = (ep.Shares ?? []).Select(x => x.To).ToArray();
        float[] weight = (ep.Shares ?? []).Select(x => x.Weight).ToArray();
        if (Online) RpcId(1, MethodName.AskSet, ep.Id, ep.E, ep.N, ep.Heading, ep.Generates, ep.Destination, ep.PerMinute, to, weight);
        else ServeSet(1, ep.Id, ep.E, ep.N, ep.Heading, ep.Generates, ep.Destination, ep.PerMinute, to, weight);
    }

    /// <summary>The next id <see cref="RequestSet"/> gives a new endpoint offline (the probe wires its shares by it).</summary>
    public int NextId => _nextId;

    /// <summary>Asks to remove an endpoint; 0 removes them all.</summary>
    public void RequestRemove(int id)
    {
        if (Online) RpcId(1, MethodName.AskRemove, id);
        else ServeRemove(1, id);
    }

    // ---- server ---------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskSet(int id, double e, double n, float heading, bool generates, bool destination, float perMinute, int[] to, float[] weight) =>
        ServeSet(Multiplayer.GetRemoteSenderId(), id, e, n, heading, generates, destination, perMinute, to, weight);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskRemove(int id) => ServeRemove(Multiplayer.GetRemoteSenderId(), id);

    private bool May(long peer) => !Online || IsAdmin?.Invoke(peer) == true;

    private void ServeSet(long peer, int id, double e, double n, float heading, bool generates, bool destination, float perMinute,
        int[] to, float[] weight)
    {
        if (!May(peer) || !double.IsFinite(e) || !double.IsFinite(n) || !float.IsFinite(heading) || !float.IsFinite(perMinute)
            || perMinute < 0f || perMinute > 600f || to.Length != weight.Length || to.Length > MaxEndpoints
            || weight.Any(w => !float.IsFinite(w) || w < 0f)) return;
        if (id == 0)
        {
            if (_endpoints.Count >= MaxEndpoints) return;
            id = _nextId++;
        }
        else if (!_endpoints.ContainsKey(id)) return;
        if (Online)
            foreach (int p in Multiplayer.GetPeers())
                RpcId(p, MethodName.Set, id, e, n, heading, generates, destination, perMinute, to, weight);
        Set(id, e, n, heading, generates, destination, perMinute, to, weight);
        GD.Print($"[traffic] endpoint #{id} at {e:F0},{n:F0}{(generates ? $" generating {perMinute:0.#}/min" : "")}{(destination ? " destination" : "")}"
            + (to.Length > 0 ? $" shares {string.Join(" ", to.Select((t, k) => $"{(t > 0 ? $"#{t}" : t == 0 ? "along" : "against")}:{weight[k]:0.##}"))}" : ""));
    }

    private static TrafficShare[]? SharesOf(int[] to, float[] weight) =>
        to.Length > 0 && to.Length == weight.Length && weight.Sum() > 0f ? to.Select((t, k) => new TrafficShare(t, weight[k])).ToArray() : null;

    private void ServeRemove(long peer, int id)
    {
        if (!May(peer)) return;
        if (Online)
            foreach (int p in Multiplayer.GetPeers())
                RpcId(p, MethodName.Remove, id);
        Remove(id);
    }

    /// <summary>Server: hands a joining peer the whole list.</summary>
    public void SendTo(long peer)
    {
        var list = _endpoints.Values.ToList();
        RpcId(peer, MethodName.Snapshot,
            list.Select(x => x.Id).ToArray(),
            list.SelectMany(x => new[] { x.E, x.N }).ToArray(),
            list.SelectMany(x => new[] { x.Heading, x.PerMinute }).ToArray(),
            list.Select(x => (x.Generates ? 1 : 0) | (x.Destination ? 2 : 0)).ToArray(),
            list.Select(x => x.Shares?.Length ?? 0).ToArray(),
            list.SelectMany(x => (x.Shares ?? []).Select(s => s.To)).ToArray(),
            list.SelectMany(x => (x.Shares ?? []).Select(s => s.Weight)).ToArray());
    }

    // ---- every peer -----------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Set(int id, double e, double n, float heading, bool generates, bool destination, float perMinute, int[] to, float[] weight)
    {
        _endpoints[id] = new TrafficEndpoint(id, e, n, heading, generates, destination, perMinute, SharesOf(to, weight));
        _nextId = Math.Max(_nextId, id + 1);
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Remove(int id)
    {
        if (id == 0) _endpoints.Clear();
        else _endpoints.Remove(id);
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Snapshot(int[] ids, double[] en, float[] headingRate, int[] flags, int[] shareCounts, int[] shareTo, float[] shareWeight)
    {
        _endpoints.Clear();
        int at = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            int count = shareCounts[i];
            _endpoints[ids[i]] = new TrafficEndpoint(ids[i], en[2 * i], en[2 * i + 1], headingRate[2 * i], (flags[i] & 1) != 0,
                (flags[i] & 2) != 0, headingRate[2 * i + 1], SharesOf(shareTo[at..(at + count)], shareWeight[at..(at + count)]));
            at += count;
            _nextId = Math.Max(_nextId, ids[i] + 1);
        }
        Changed?.Invoke();
    }
}
