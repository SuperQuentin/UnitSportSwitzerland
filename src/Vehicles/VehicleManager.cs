using Godot;
using UnitSport.Terrain;

namespace UnitSport.Vehicles;

/// <summary>
/// Owns every vehicle standing in the world, at <c>World/Vehicles</c> on the server and on every
/// client alike — the path has to match, because RPCs and the spawner route by node path.
///
/// <para>
/// Offline it simply adds and frees <see cref="VehicleBody"/> nodes. Online the server decides:
/// a client asks to <see cref="Park"/> a vehicle (it got out, or crashed) and the server spawns it
/// for everyone through <c>World/VehicleSpawner</c>; a client asks to <see cref="Claim"/> one (it
/// wants to get in) and the server removes it for everyone and hands its state back, so two
/// players can never drive off in the same helicopter. The client that parked a vehicle simulates
/// it — client authority, as for the players themselves.
/// </para>
/// </summary>
public partial class VehicleManager : Node3D
{
    public const string NodeName = "Vehicles";

    public ChunkManager? Terrain { get; set; }

    /// <summary>Where the players are, for despawning what nobody is near.</summary>
    public Func<IEnumerable<Vector3>>? PlayerPositions { get; set; }

    private MultiplayerSpawner? _spawner;
    private int _counter;
    private Action<VehicleState>? _pendingClaim;
    private readonly HashSet<string> _claimed = new();
    /// <summary>Server: vehicles each peer took out of the world and has not put back yet.</summary>
    private readonly Dictionary<long, int> _driving = new();

    /// <summary>
    /// Server: may this peer put a vehicle into the world that it did not take out of it, i.e. one
    /// it conjured from the travel menu? Null allows everything. Wired to "admin, or the race gave
    /// you that car" by <c>ServerWorld</c>; see <see cref="Core.Permissions"/> for why.
    /// </summary>
    public Func<long, VehicleState, bool>? MayPark { get; set; }

    /// <summary>Client: the server refused to park a vehicle this player spawned.</summary>
    public static event Action<string>? Refused;
    private double _housekeeping;

    /// <summary>Wrecks are cleared this long after they burn.</summary>
    private const double WreckLifetime = 90;
    /// <summary>A vehicle this far from every player, for this long, is cleared.</summary>
    private const float LonelyDistance = 3000f;
    private const double LonelyTime = 300;

    /// <summary>The manager of this process's world, if there is one.</summary>
    public static VehicleManager? Instance { get; private set; }

    public static VehicleManager Create(Node world, ChunkManager? terrain)
    {
        var manager = new VehicleManager { Name = NodeName, Terrain = terrain };
        world.AddChild(manager);
        manager._spawner = new MultiplayerSpawner
        {
            Name = "VehicleSpawner",
            SpawnPath = new NodePath("../" + NodeName),
            SpawnFunction = Callable.From((Variant data) =>
                (Node)VehicleBody.Create(VehicleState.FromDict(data.AsGodotDictionary()), manager.Terrain)),
        };
        world.AddChild(manager._spawner);
        Instance = manager;
        return manager;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>Puts a vehicle into the world.</summary>
    public void Park(VehicleState state)
    {
        if (!Online)
        {
            AddChild(VehicleBody.Create(state with { SpawnedAt = VehicleState.Now }, Terrain));
            return;
        }
        RpcId(1, MethodName.RequestPark, state.ToDict());
    }

    /// <summary>Asks for a vehicle to get into. <paramref name="granted"/> runs if it is still free.</summary>
    public void Claim(VehicleBody vehicle, Action<VehicleState> granted)
    {
        if (vehicle.Wrecked) return;
        if (!Online)
        {
            var state = vehicle.Capture();
            vehicle.QueueFree();
            granted(state);
            return;
        }
        _pendingClaim = granted;
        RpcId(1, MethodName.RequestClaim, vehicle.Name);
    }

    /// <summary>The nearest drivable vehicle within reach of a point, or null.</summary>
    public VehicleBody? Nearest(Vector3 point, float reach)
    {
        VehicleBody? best = null;
        float bestDist = reach;
        foreach (var node in GetChildren())
            if (node is VehicleBody { Wrecked: false } v && !_claimed.Contains(v.Name))
            {
                // measured to the box, roughly: a plane's cockpit is metres from its origin
                float d = v.GlobalPosition.DistanceTo(point) - v.Ride.ParkedBox.Size.X * 0.25f;
                if (d < bestDist) { bestDist = d; best = v; }
            }
        return best;
    }

    // ---- server side --------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPark(Godot.Collections.Dictionary data)
    {
        if (!Multiplayer.IsServer() || _spawner == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        var parked = VehicleState.FromDict(data);

        // A vehicle a player got out of is one they got into, and those are counted; anything
        // beyond that was spawned from the menu, which the client only offers an admin. A client
        // that offers it anyway is refused here, where the answer cannot be edited.
        if (_driving.TryGetValue(sender, out int driving) && driving > 0)
            _driving[sender] = driving - 1;
        else if (MayPark != null && !MayPark(sender, parked))
        {
            GD.Print($"[vehicles] peer {sender} may not spawn a {parked.Kind}; not parked");
            RpcId(sender, MethodName.ParkRefused, parked.Kind.ToString());
            return;
        }

        var state = parked with
        {
            Owner = sender,
            Name = $"veh_{sender}_{++_counter}",
            SpawnedAt = VehicleState.Now,
        };
        _spawner.Spawn(state.ToDict());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestClaim(string name)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (GetNodeOrNull<VehicleBody>(name) is not { Wrecked: false } vehicle || !_claimed.Add(name))
        {
            RpcId(sender, MethodName.ClaimRefused);
            return;
        }
        var state = vehicle.Capture();
        vehicle.QueueFree();   // the spawner removes it on every client
        _claimed.Remove(name);
        _driving[sender] = _driving.GetValueOrDefault(sender) + 1;
        RpcId(sender, MethodName.ClaimGranted, state.ToDict());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClaimGranted(Godot.Collections.Dictionary data)
    {
        var claim = _pendingClaim;
        _pendingClaim = null;
        claim?.Invoke(VehicleState.FromDict(data));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClaimRefused() => _pendingClaim = null;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ParkRefused(string kind) =>
        Refused?.Invoke($"Only an admin can spawn vehicles on this server; your {kind} was not left in the world.");

    /// <summary>Server: a player left, and nobody is simulating their vehicles any more.</summary>
    public void ForgetOwner(long peer)
    {
        _driving.Remove(peer);
        foreach (var node in GetChildren())
            if (node is VehicleBody v && v.Owner == peer) v.QueueFree();
    }

    /// <summary>
    /// Clears burnt-out wrecks and vehicles nobody has been near for a long time. Done by whoever
    /// owns the list: the server online, the client offline.
    /// </summary>
    public override void _Process(double delta)
    {
        if (Online && !Multiplayer.IsServer()) return;
        _housekeeping += delta;
        if (_housekeeping < 5) return;
        double step = _housekeeping;
        _housekeeping = 0;

        var players = PlayerPositions?.Invoke().ToList() ?? new List<Vector3>();
        foreach (var node in GetChildren())
        {
            if (node is not VehicleBody v) continue;
            if (v.Wrecked && v.WreckAge > WreckLifetime) { v.QueueFree(); continue; }
            bool near = players.Any(p => p.DistanceTo(v.GlobalPosition) < LonelyDistance);
            v.LonelyFor = near ? 0 : v.LonelyFor + step;
            if (v.LonelyFor > LonelyTime) v.QueueFree();
        }
    }
}
