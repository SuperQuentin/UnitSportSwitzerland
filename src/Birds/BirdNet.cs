using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Birds;

/// <summary>
/// The wire for shared birds (#143), at <c>World/BirdNet</c> on the server and every client (RPCs
/// route by node path). The server's <see cref="BirdLife"/> owns the birds; this node
/// <list type="bullet">
/// <item>sends each peer, 8 times a second, the birds near its player (<see cref="BirdLife.Snapshot"/>,
/// unreliable, flying birds every time and resting ones every fourth): a bird nobody mentions for 3 s is
/// gone on the client, so there is no despawn message and a lost packet costs nothing;</item>
/// <item>takes a peer's <see cref="Report"/> (a shot, a gun round, a strike, a scare), checks the peer is
/// where it says and hands it to <see cref="BirdLife.ServerReport"/>;</item>
/// <item>tells every peer near a pigeon's dropping where it falls and on whom (<see cref="BroadcastDropping"/>);</item>
/// <item>tells every peer near a dead bird (reliable <c>Killed</c>), the shooter included: that is when
/// the shooter's journal scores it, so the kill goes into the shooter's journal only.</item>
/// </list>
/// Offline it does nothing: <see cref="BirdLife.Authority"/> is true there. Every position it sends
/// is LV95 (#185): server and clients each have their own origin.
/// </summary>
public partial class BirdNet : Node
{
    public const string NodeName = "BirdNet";
    public const int ReportShot = 0, ReportKill = 1, ReportFlush = 2;

    /// <summary>
    /// How far from the sender's body a report may start: a shotgun leaves the eye (the body the
    /// server has is up to a tick or two behind a running player), while a gun round or a strike is
    /// wherever the bird was, and no bird exists past <see cref="BirdLife.DespawnDistance"/>.
    /// </summary>
    private const float MaxShotOffset = 8f, MaxFarOffset = BirdLife.DespawnDistance + 60f;
    private const float KilledRange = 400f;
    private const double SendInterval = 0.125;

    /// <summary>Null on a swarm bot (src/Net/Swarm), which takes the RPCs and draws nothing.</summary>
    public BirdLife? Life { get; private set; }
    private bool _server;
    private double _acc;
    private int _tick;

    /// <summary>Species by catalogue index, the way a snapshot names them.</summary>
    public static BirdSpecies? Species(int index) => index >= 0 && index < BirdCatalog.All.Length ? BirdCatalog.All[index] : null;

    public static BirdNet Create(Node world, BirdLife life, bool server)
    {
        var n = new BirdNet { Name = NodeName, Life = life, _server = server };
        life.Net = n;
        world.AddChild(n);
        return n;
    }

    public bool Online => NetLink.Online(this);

    public override void _Process(double delta)
    {
        if (!_server || !Online) return;
        _acc += delta;
        if (_acc < SendInterval) return;
        _acc = 0;
        _tick++;
        foreach (int peer in Multiplayer.GetPeers())
        {
            if (GetNodeOrNull<FootPlayer>("../Players/" + peer) is not { } body) continue;
            foreach (var chunk in Life!.Snapshot(body.Global, _tick))
                RpcId(peer, MethodName.Snapshot, chunk);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void Snapshot(byte[] data)
    {
        if (!_server) Life?.ApplySnapshot(data);
    }

    /// <summary>Client: tell the server what this client's gun, rounds or craft did to a bird.</summary>
    public void Report(int kind, int id, Vector3 from, Vector3 dir)
    {
        if (!Online || Life == null) return;
        var at = Life.Origin.ToGlobal(from);
        RpcId(1, MethodName.ReportRpc, kind, id, at.E, at.N, at.Alt, dir);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReportRpc(int kind, int id, double e, double n, double alt, Vector3 dir)
    {
        if (!_server || kind is < ReportShot or > ReportFlush) return;
        long sender = Multiplayer.GetRemoteSenderId();
        var from = new GlobalPos(e, n, alt);
        // a gun that is not where its owner is does not shoot
        float reach = kind == ReportShot ? MaxShotOffset : MaxFarOffset;
        if (GetNodeOrNull<FootPlayer>("../Players/" + sender) is not { } body || !from.IsFinite || body.Global.DistanceTo(from) > reach)
        {
            GD.Print($"[birds] peer {sender}: report {kind} on #{id} refused, not where its player is");
            return;
        }
        Life!.ServerReport(sender, kind, id, Life.Origin.ToWorld(from), dir);
    }

    /// <summary>Server: a bird fell. Everyone within earshot hears of it; the shooter is always told.</summary>
    public void BroadcastKill(Bird b, Vector3 dir, long shooter)
    {
        if (!_server || !Online) return;
        var at = Life!.Origin.ToGlobal(b.Node.GlobalPosition);
        foreach (int peer in Multiplayer.GetPeers())
            if (peer == shooter || GetNodeOrNull<FootPlayer>("../Players/" + peer) is { } body && body.Global.DistanceTo(at) < KilledRange)
                RpcId(peer, MethodName.Killed, b.Id, b.Species.Index, at.E, at.N, at.Alt, dir, shooter);
    }

    /// <summary>
    /// Server: a pigeon let go. Everyone near sees it fall; <paramref name="victim"/> (a peer, or 0) is who
    /// it lands on; <paramref name="by"/> is the player pigeon that dropped it (#217), told too, 0 for a bird.
    /// </summary>
    public void BroadcastDropping(Vector3 from, Vector3 vel, long victim, long by = 0)
    {
        if (!_server || !Online) return;
        var at = Life!.Origin.ToGlobal(from);
        string name = by == 0 ? "" : GetNodeOrNull<ChatManager>("../" + ChatManager.NodeName)?.NameOfPeer(by) ?? "";
        foreach (int peer in Multiplayer.GetPeers())
            if (peer == victim || peer == by || GetNodeOrNull<FootPlayer>("../Players/" + peer) is { } body && body.Global.DistanceTo(at) < DroppingRange)
                RpcId(peer, MethodName.Dropping, at.E, at.N, at.Alt, vel, victim, by, name);
    }

    private const float DroppingRange = 150f;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Dropping(double e, double n, double alt, Vector3 vel, long victim, long by, string byName)
    {
        if (!_server && Life != null) Life.Dropping(Life.Origin.ToWorld(e, n, alt), vel, victim, by, byName);
    }

    /// <summary>Client: the local player, a pigeon, lets go (#217). The server checks it and picks the victim.</summary>
    public void SendDrop(Vector3 from, Vector3 vel)
    {
        if (!Online || Life == null) return;
        var at = Life.Origin.ToGlobal(from);
        RpcId(1, MethodName.DropRpc, at.E, at.N, at.Alt, vel);
    }

    /// <summary>A player pigeon's droppings: at most one per <see cref="DropInterval"/>, from where its body is.</summary>
    public const double DropInterval = 0.4;
    private readonly Dictionary<long, double> _lastDrop = new();

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DropRpc(double e, double n, double alt, Vector3 vel)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        var from = new GlobalPos(e, n, alt);
        // wall clock (#579): a rate limit on what a client may send must not loosen when that
        // client slows its own simulation
        double now = Core.RealClock.Now;
        if (GetNodeOrNull<FootPlayer>("../Players/" + sender) is not { } body || body.RideKindId != (int)RideKind.Pigeon
            || !from.IsFinite || body.Global.DistanceTo(from) > 4f || !vel.IsFinite() || vel.Length() > 60f
            || _lastDrop.TryGetValue(sender, out double last) && now - last < DropInterval) return;
        _lastDrop[sender] = now;
        var at = Life!.Origin.ToWorld(from);
        BroadcastDropping(at, vel, Victim(at, sender), sender);
    }

    /// <summary>Client: the local player crashed as a pigeon (#519); the server shows the splat to everyone near.</summary>
    public void SendSplat(Vector3 at, Vector3 vel)
    {
        if (!Online || Life == null) return;
        var g = Life.Origin.ToGlobal(at);
        RpcId(1, MethodName.SplatRpc, g.E, g.N, g.Alt, vel);
    }

    private readonly Dictionary<long, double> _lastSplat = new();

    /// <summary>
    /// Server: a splat only looks like something, so the check is light: from where the sender's body is
    /// (its ride may already read on foot), at most one a second.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SplatRpc(double e, double n, double alt, Vector3 vel)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        var from = new GlobalPos(e, n, alt);
        double now = Core.RealClock.Now;   // wall clock, as in DropRpc
        if (GetNodeOrNull<FootPlayer>("../Players/" + sender) is not { } body || !from.IsFinite || body.Global.DistanceTo(from) > 8f
            || !vel.IsFinite() || vel.Length() > 80f || _lastSplat.TryGetValue(sender, out double last) && now - last < 1.0) return;
        _lastSplat[sender] = now;
        foreach (int peer in Multiplayer.GetPeers())
            if (peer != sender && GetNodeOrNull<FootPlayer>("../Players/" + peer) is { } other && other.Global.DistanceTo(from) < DroppingRange)
                RpcId(peer, MethodName.Splat, e, n, alt, vel);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Splat(double e, double n, double alt, Vector3 vel)
    {
        if (!_server && Life != null) Life.Splat(Life.Origin.ToWorld(e, n, alt), vel);
    }

    /// <summary>Who a dropping from <paramref name="at"/> lands on: a person on foot nearly straight below, else nobody.</summary>
    private long Victim(Vector3 at, long dropper)
    {
        long victim = 0;
        float best = 1.3f;
        foreach (var s in PlayerSnapshot.Of(GetTree()))
        {
            if (s.Ride != RideKind.OnFoot || FootPlayer.NetId(s.Player.Name) is not long peer || peer == dropper || peer <= 0) continue;
            float d = new Vector2(at.X - s.Pos.X, at.Z - s.Pos.Z).Length(), above = at.Y - s.Pos.Y;
            if (d < best && above is > 1.7f and < 60f) { best = d; victim = peer; }
        }
        return victim;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Killed(int id, int species, double e, double n, double alt, Vector3 dir, long shooter)
    {
        if (_server) return;
        GD.Print($"[birds] killed #{id} ({Species(species)?.Name}) by peer {shooter}");
        Life?.RemoteKilled(id, species, Life.Origin.ToWorld(e, n, alt), dir, shooter);
    }
}
