using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Net;

namespace UnitSport.Items;

/// <summary>
/// Every radio lying in the world, at <c>World/Radios</c> on the server and on every client alike
/// (RPCs and the spawner route by node path), spawned through <c>World/RadioSpawner</c>. The
/// <c>VehicleManager</c> pattern: offline it adds and frees <see cref="RadioBody"/> nodes; online a
/// client asks and the server decides — a throw spawns it for everyone with the thrower as the
/// authority over its fall, a pick-up removes it for everyone and only one player can win it, and
/// play/stop set the state the server's synchronizer then carries to all.
/// </summary>
public partial class RadioManager : Node3D
{
    public const string NodeName = "Radios";

    /// <summary>How close a player must be to a playing radio to dance to it.</summary>
    public const float DanceRadius = 20f;

    /// <summary>How close to a radio to open it or pick it up.</summary>
    public const float Reach = 2.5f;

    /// <summary>A radio this far from every player, for this long, is cleared.</summary>
    private const float LonelyDistance = 3000f;
    private const double LonelyTime = 600;

    public static RadioManager? Instance { get; private set; }

    /// <summary>Where the players are, for despawning what nobody is near.</summary>
    public Func<IEnumerable<Vector3>>? PlayerPositions { get; set; }

    /// <summary>Client: the server refused something, with a line for the player.</summary>
    public static event Action<string>? Refused;

    private MultiplayerSpawner? _spawner;
    private int _counter;
    private Action? _pendingPickUp;
    private readonly HashSet<string> _claimed = new();
    private double _housekeeping;

    public static RadioManager Create(Node world)
    {
        var manager = new RadioManager { Name = NodeName };
        world.AddChild(manager);
        manager._spawner = new MultiplayerSpawner
        {
            Name = "RadioSpawner",
            SpawnPath = new NodePath("../" + NodeName),
            SpawnFunction = Callable.From((Variant data) => (Node)RadioBody.Create(RadioState.FromDict(data.AsGodotDictionary()))),
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

    // ---- client API ----------------------------------------------------------------------------

    /// <summary>Throws a radio into the world from where the player stands.</summary>
    public void Throw(RadioState state)
    {
        if (!Online)
        {
            AddChild(RadioBody.Create(state with { Owner = 0, Name = "" }));
            return;
        }
        RpcId(1, MethodName.RequestThrow, state.ToDict());
    }

    /// <summary>Asks to take a radio back. <paramref name="granted"/> runs if nobody was quicker.</summary>
    public void PickUp(RadioBody radio, Action granted)
    {
        if (!Online)
        {
            radio.QueueFree();
            granted();
            return;
        }
        _pendingPickUp = granted;
        RpcId(1, MethodName.RequestPickUp, radio.Name);
    }

    /// <summary>Puts a CD in and starts it, from the beginning, for everyone.</summary>
    public void Play(RadioBody radio, int cdId)
    {
        if (!Online) { StartOn(radio, cdId); return; }
        RpcId(1, MethodName.RequestPlay, radio.Name, cdId);
    }

    public void Stop(RadioBody radio)
    {
        if (!Online) { radio.Playing = false; return; }
        RpcId(1, MethodName.RequestStop, radio.Name);
    }

    /// <summary>The nearest radio within reach of a point, or null.</summary>
    public RadioBody? Nearest(Vector3 point, float reach)
    {
        RadioBody? best = null;
        float bestDist = reach;
        foreach (var node in GetChildren())
            if (node is RadioBody r && !_claimed.Contains(r.Name))
            {
                float d = r.GlobalPosition.DistanceTo(point);
                if (d < bestDist) { bestDist = d; best = r; }
            }
        return best;
    }

    /// <summary>The nearest radio that is playing within <paramref name="radius"/>, or null — the one to dance to.</summary>
    public RadioBody? NearestPlaying(Vector3 point, float radius)
    {
        RadioBody? best = null;
        float bestDist = radius;
        foreach (var node in GetChildren())
            if (node is RadioBody { Playing: true } r && r.Cd is { } cd && r.WantedPosition < cd.Duration)
            {
                float d = r.GlobalPosition.DistanceTo(point);
                if (d < bestDist) { bestDist = d; best = r; }
            }
        return best;
    }

    // ---- server side ---------------------------------------------------------------------------

    private static void StartOn(RadioBody radio, int cdId)
    {
        radio.CdId = cdId;
        radio.StartedAt = ClockSync.ServerNow;
        radio.Playing = true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestThrow(Godot.Collections.Dictionary data)
    {
        if (!Multiplayer.IsServer() || _spawner == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        var thrown = RadioState.FromDict(data);
        if (thrown.Velocity.Length() > 30f || !thrown.Position.IsFinite())
        {
            RpcId(sender, MethodName.ThrowRefused);
            return;
        }
        var state = thrown with { Owner = sender, Name = $"radio_{sender}_{++_counter}", Playing = false, Settled = false };
        _spawner.Spawn(state.ToDict());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPickUp(string name)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (GetNodeOrNull<RadioBody>(name) is not { } radio || !_claimed.Add(name))
        {
            RpcId(sender, MethodName.PickUpRefused);
            return;
        }
        radio.QueueFree();   // the spawner removes it on every client
        _claimed.Remove(name);
        RpcId(sender, MethodName.PickUpGranted);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPlay(string name, int cdId)
    {
        if (!Multiplayer.IsServer()) return;
        if (GetNodeOrNull<RadioBody>(name) is not { } radio) return;
        if (CdLibrary.Instance?.All.ContainsKey(cdId) != true) return;
        StartOn(radio, cdId);
        GD.Print($"[radio] {name} plays CD {cdId} from {radio.StartedAt:F2}");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestStop(string name)
    {
        if (!Multiplayer.IsServer()) return;
        if (GetNodeOrNull<RadioBody>(name) is { } radio) radio.Playing = false;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PickUpGranted()
    {
        var granted = _pendingPickUp;
        _pendingPickUp = null;
        granted?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PickUpRefused()
    {
        _pendingPickUp = null;
        Refused?.Invoke("Someone else picked that radio up first.");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ThrowRefused() => Refused?.Invoke("The server refused that throw; the radio is back in your pocket.");

    /// <summary>
    /// Server: a player left, and nobody simulates their radios any more. They are put back
    /// where they were last seen, standing, with the server as their authority.
    /// </summary>
    public void ForgetOwner(long peer)
    {
        if (_spawner == null) return;
        var respawn = new List<RadioState>();
        foreach (var node in GetChildren())
            if (node is RadioBody r && r.Owner == peer)
            {
                respawn.Add(r.Capture() with { Owner = 0, Name = $"radio_srv_{++_counter}", Settled = true });
                r.QueueFree();
            }
        foreach (var state in respawn) _spawner.Spawn(state.ToDict());
    }

    /// <summary>
    /// Ends a CD when it has run out, and clears radios nobody has been near for a long time.
    /// Done by whoever owns the list: the server online, the client offline.
    /// </summary>
    public override void _Process(double delta)
    {
        if (Online && !Multiplayer.IsServer()) return;
        _housekeeping += delta;
        if (_housekeeping < 1) return;
        double step = _housekeeping;
        _housekeeping = 0;

        var players = PlayerPositions?.Invoke().ToList() ?? new List<Vector3>();
        foreach (var node in GetChildren())
        {
            if (node is not RadioBody r) continue;
            if (r.Playing && (r.Cd is not { } cd || r.WantedPosition >= cd.Duration)) r.Playing = false;
            bool near = players.Count == 0 || players.Any(p => p.DistanceTo(r.GlobalPosition) < LonelyDistance);
            r.LonelyFor = near ? 0 : r.LonelyFor + step;
            if (r.LonelyFor > LonelyTime) r.QueueFree();
        }
    }
}
