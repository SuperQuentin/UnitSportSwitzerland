using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// Where a joining player starts, and whether they may teleport afterwards (#799), at
/// <c>World/Arrival</c> on the server and every client (RPCs route by node path).
///
/// <para>
/// <b>The server decides.</b> A client used to land wherever it clicked on the landing map; online
/// that is the server's choice, the same for everyone: <c>--spawn E,N</c> (LV95, default Riddes).
/// <see cref="SendTo"/> runs on accept, right after the peer's player is spawned, and the client holds
/// its loading screen until <c>Rules</c> arrives (<c>ClientWorld.OnServerSpawn</c>). A returning sleeper
/// is moved on from there by <c>Sleepers.WakeAt</c>, and a <c>--at</c> on the client's command line
/// still wins, so every probe keeps its spot.
/// </para>
///
/// <para>
/// <b>Teleporting.</b> <c>--no-teleport</c> keeps players where they walk, drive and fly: the client
/// greys out the place search and the VR hand map (<see cref="Permissions.CanTeleport"/>) and walks
/// back from the fly camera to the body where it stands; the server refuses <c>/city</c>. Admins keep
/// both. Transforms are client-authoritative, so this is a rule an honest client follows, not a wall.
/// </para>
/// </summary>
public partial class Arrival : Node
{
    public const string NodeName = "Arrival";

    private bool _server;

    /// <summary>Server: where every new player starts, LV95 metres.</summary>
    public (double E, double N) Spawn { get; private set; } = (SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);

    /// <summary>Server: players who are not admins may teleport (place search, <c>/city</c>, the fly camera).</summary>
    public bool FreeTeleport { get; private set; } = true;

    /// <summary>Client: the server said where to start, LV95 metres.</summary>
    public event Action<double, double>? Told;

    public static Arrival CreateServer(Node world)
    {
        var a = new Arrival { Name = NodeName, _server = true };
        if (SpawnPoint.ParseLv95("--spawn") is { } spawn) a.Spawn = spawn;
        a.FreeTeleport = !CmdArgs.Has("--no-teleport");
        GD.Print($"[arrival] players start at {a.Spawn.E:F0}/{a.Spawn.N:F0}, teleporting {(a.FreeTeleport ? "allowed" : "admins only")}");
        world.AddChild(a);
        return a;
    }

    public static Arrival CreateClient(Node world)
    {
        var a = new Arrival { Name = NodeName };
        world.AddChild(a);
        return a;
    }

    /// <summary>Server: a peer was accepted; tell it where it starts and what it may do.</summary>
    public void SendTo(long peer) => RpcId(peer, MethodName.Rules, Spawn.E, Spawn.N, FreeTeleport);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Rules(double e, double n, bool freeTeleport)
    {
        if (_server) return;
        Permissions.SetServerTeleport(freeTeleport);
        Told?.Invoke(e, n);
    }
}
