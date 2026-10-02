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
public partial class VehicleManager : Node3D, Core.IOriginContainer
{
    public const string NodeName = "Vehicles";

    public ChunkManager? Terrain { get; set; }

    /// <summary>This peer's origin: vehicle states carry LV95 (#185), its bodies stand in world space.</summary>
    public Core.WorldOrigin Origin { get; private set; } = null!;

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
    /// <summary>Drops the subscribers a world left behind when it was freed (<see cref="Core.WorldStatics"/>).</summary>
    internal static void ResetEvents() => Refused = null;
    private double _housekeeping;

    /// <summary>Wrecks are cleared this long after they burn.</summary>
    private const double WreckLifetime = 90;
    /// <summary>A vehicle this far from every player, for this long, is cleared.</summary>
    private const float LonelyDistance = 3000f;
    private const double LonelyTime = 300;

    /// <summary>The manager of this process's world, if there is one.</summary>
    public static VehicleManager? Instance { get; private set; }

    public static VehicleManager Create(Node world, ChunkManager? terrain, Core.WorldOrigin origin)
    {
        var manager = new VehicleManager { Name = NodeName, Terrain = terrain, Origin = origin };
        world.AddChild(manager);
        manager._spawner = new MultiplayerSpawner
        {
            Name = "VehicleSpawner",
            SpawnPath = new NodePath("../" + NodeName),
            SpawnFunction = Callable.From((Variant data) =>
                (Node)VehicleBody.Create(VehicleState.FromDict(data.AsGodotDictionary()), manager.Terrain, manager.Origin)),
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
            AddChild(VehicleBody.Create(state with { SpawnedAt = VehicleState.Now }, Terrain, Origin));
            return;
        }
        RpcId(1, MethodName.RequestPark, state.ToDict());
    }

    /// <summary>
    /// Puts a vehicle nobody parked into the world (a placed one, like the Africa Twin at Riddes):
    /// the server spawns it for everyone with itself as the authority, offline it is simply added.
    /// A client online does nothing — the server places those. Returns the node's name, or null.
    /// </summary>
    public string? Place(VehicleState state, string name)
    {
        state = state with { Owner = 0, Name = name, SpawnedAt = VehicleState.Now };
        if (!Online)
        {
            AddChild(VehicleBody.Create(state, Terrain, Origin));
            return name;
        }
        if (!Multiplayer.IsServer() || _spawner == null) return null;
        _spawner.Spawn(state.ToDict());
        return name;
    }

    /// <summary>A claim is on its way to the server: another E would ask twice.</summary>
    public bool Claiming => _pendingClaim != null;

    /// <summary>Asks for a vehicle to get into. <paramref name="granted"/> runs if it is still free.</summary>
    public void Claim(VehicleBody vehicle, Action<VehicleState> granted)
    {
        if (vehicle.Wrecked) return;
        if (!Online)
        {
            var state = vehicle.Capture();
            vehicle.Retire();
            vehicle.QueueFree();
            granted(state);
            return;
        }
        _pendingClaim = granted;
        RpcId(1, MethodName.RequestClaim, vehicle.Name);
    }

    /// <summary>
    /// Opens or shuts one door of a parked car. Its authority (whoever parked it) does it at once;
    /// anyone else asks the server, which checks they are standing at the car and passes it on.
    /// </summary>
    public void ToggleDoor(VehicleBody vehicle, byte bit)
    {
        if (vehicle.IsMultiplayerAuthority()) { vehicle.ToggleDoor(bit); return; }
        if (Online) RpcId(1, MethodName.RequestDoor, vehicle.Name, bit);
    }

    /// <summary>How far from a car's side a player may be to work its doors, m.</summary>
    public const float DoorReach = 3f;

    /// <summary>A vehicle someone could get into now: not burnt out, not a lone trailer, not being claimed.</summary>
    public bool Enterable(VehicleBody v) =>
        IsInstanceValid(v) && v.GetParent() == this && !v.Wrecked && v.Trailer == null && !_claimed.Contains(v.Name);

    /// <summary>The nearest drivable vehicle within reach of a point, or null.</summary>
    public VehicleBody? Nearest(Vector3 point, float reach)
    {
        VehicleBody? best = null;
        float bestDist = reach;
        foreach (var node in GetChildren())
            if (node is VehicleBody { Wrecked: false, Trailer: null } v && !_claimed.Contains(v.Name))
            {
                // measured to the door where there is one (a truck's cab, a bus's front door, metres
                // from the middle), else to the box, roughly: a plane's cockpit is metres from its origin
                var entry = v.Ride.EntryPoint;
                float d = entry != Vector3.Zero
                    ? v.ToGlobal(entry).DistanceTo(point)
                    : v.GlobalPosition.DistanceTo(point) - v.Ride.ParkedBox.Size.X * 0.25f;
                if (d < bestDist) { bestDist = d; best = v; }
            }
        return best;
    }

    /// <summary>
    /// The lone trailer whose pivot (kingpin, drawbar eye) is nearest a point, within
    /// <paramref name="reach"/> horizontally, and that <paramref name="fits"/> — for a truck backing
    /// its hitch under it.
    /// </summary>
    public VehicleBody? NearestTrailer(Vector3 point, float reach, Func<VehicleBody, bool> fits)
    {
        VehicleBody? best = null;
        float bestDist = reach;
        foreach (var node in GetChildren())
            if (node is VehicleBody { Wrecked: false, Trailer: { } t } v && !_claimed.Contains(v.Name) && fits(v))
            {
                var pivot = v.ToGlobal(t.PivotNode);
                float d = new Vector2(pivot.X - point.X, pivot.Z - point.Z).Length();
                if (d < bestDist && Mathf.Abs(pivot.Y - point.Y) < 2.5f) { bestDist = d; best = v; }
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
        if (_driving.TryGetValue(sender, out int driving) && driving >= parked.Units)
            _driving[sender] = driving - parked.Units;
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

    /// <summary>
    /// Server: puts a vehicle into the world for <paramref name="owner"/>, who simulates it, as if it
    /// had asked to park it: a host whose passengers had all gone by the time it got out (#158).
    /// </summary>
    public void ParkFor(long owner, VehicleState parked)
    {
        if (!Multiplayer.IsServer() || _spawner == null) return;
        if (_driving.TryGetValue(owner, out int driving)) _driving[owner] = Math.Max(0, driving - parked.Units);
        _spawner.Spawn((parked with { Owner = owner, Name = $"veh_{owner}_{++_counter}", SpawnedAt = VehicleState.Now }).ToDict());
    }

    /// <summary>
    /// Server: a vehicle being driven moved from one player to another (a passenger who goes on with
    /// it, #158): the right to put it back into the world goes with it.
    /// </summary>
    public void TransferDriving(long from, long to, int units)
    {
        if (_driving.TryGetValue(from, out int had)) _driving[from] = Math.Max(0, had - units);
        _driving[to] = _driving.GetValueOrDefault(to) + units;
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
        _driving[sender] = _driving.GetValueOrDefault(sender) + state.Units;
        RpcId(sender, MethodName.ClaimGranted, state.ToDict());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClaimGranted(Godot.Collections.Dictionary data)
    {
        var claim = _pendingClaim;
        _pendingClaim = null;
        var state = VehicleState.FromDict(data);
        // the server's despawn of it may come after this: out of the way of its new driver until then
        if (claim != null) GetNodeOrNull<VehicleBody>(state.Name)?.Retire();
        claim?.Invoke(state);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClaimRefused() => _pendingClaim = null;

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDoor(string name, byte bit)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (GetNodeOrNull<VehicleBody>(name) is not { Wrecked: false, Ride: Player.Car or Player.Truck { IsBus: true } } vehicle) return;
        // the server's copy of the asker: only someone standing at the car works its doors
        var asker = GetTree().GetNodesInGroup(Player.FootPlayer.Group).OfType<Player.FootPlayer>()
            .FirstOrDefault(p => p.Name == sender.ToString());
        if (asker == null) return;
        var gap = (asker.GlobalPosition - vehicle.GlobalPosition) with { Y = 0 };
        // a car's doors from its side; a bus's buttons are along its whole length (#162)
        var box = vehicle.Ride.ParkedBox.Size;
        float half = vehicle.Ride is Player.Truck ? Mathf.Max(box.X, box.Z) * 0.5f : box.X * 0.5f;
        if (gap.Length() - half > DoorReach) return;
        int authority = vehicle.GetMultiplayerAuthority();
        if (authority == 1) vehicle.ToggleDoor(bit);
        else RpcId(authority, MethodName.DoorToggled, name, bit);
    }

    /// <summary>On the car's authority: the server let someone else work one of its doors.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DoorToggled(string name, byte bit)
    {
        if (GetNodeOrNull<VehicleBody>(name) is { } vehicle && vehicle.IsMultiplayerAuthority()) vehicle.ToggleDoor(bit);
    }

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
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        // a car parked in a garage is 3 km under it: measured from up in the world
        float? Ground(Vector3 at) => Terrain != null && Terrain.TryGetHeight(at, out float g) ? g : null;
        var players = (PlayerPositions?.Invoke() ?? []).Select(p => Interiors.InteriorManager.SurfacePoint(p, Ground)).ToList();
        foreach (var node in GetChildren())
        {
            if (node is not VehicleBody v) continue;
            if (v.Wrecked && v.WreckAge > WreckLifetime) { v.QueueFree(); continue; }
            var at = Interiors.InteriorManager.SurfacePoint(v.GlobalPosition, Ground);
            bool near = players.Any(p => p.DistanceTo(at) < LonelyDistance);
            v.LonelyFor = near ? 0 : v.LonelyFor + step;
            if (v.LonelyFor > LonelyTime) v.QueueFree();
        }
        Net.ServerStats.Ran("vehicle housekeeping", t0);
    }
}
