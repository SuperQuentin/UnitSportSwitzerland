using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Building interiors, at <c>World/Interiors</c> on the server and on every client — RPCs route by
/// node path, so the name has to match, as for <c>World/Vehicles</c>.
///
/// <para>
/// <b>An interior is an instance by position, not by world.</b> Each one is built directly under
/// its own building, <see cref="InteriorBaseY"/> below sea level, in the same <see cref="World3D"/>
/// as everything else. Real buildings do not overlap, so neither do their interiors; every peer
/// computes the same place from the plan alone, so nothing has to allocate slots; there is one
/// physics space for a hundred players; and the terrain keeps streaming around a player who is
/// inside. A client builds only the interiors it can see into: the one it is in, and those behind
/// open doors near it.
/// </para>
///
/// <para>
/// <b>Doors open, and you walk through them.</b> E at a front door asks the server to open it (or
/// shut it); the server plans the building if nobody has yet, and tells everyone. While a door
/// stands open, each nearby client builds the interior behind it, and the doorway on either side
/// shows the other (<see cref="DoorPortals"/>). Stepping over the sill carries the player across
/// by the door's map (<see cref="DoorLink"/>), with no fade and no pause. A door nobody is near
/// shuts by itself after a few seconds, so no portal renders for an empty doorway.
/// </para>
///
/// <para>
/// <b>The server generates and remembers.</b> The first door opened on a building makes the server
/// plan it (<see cref="InteriorGenerator"/>) and save the plan under <c>user://interiors/</c>;
/// everyone afterwards is sent that stored plan, so they all stand in the same house. Offline the
/// client plays the server's part for itself.
/// </para>
///
/// <para>
/// <b>Who is where</b> and <b>which doors are open</b> are server tables, broadcast to every client.
/// Clients hide remote players they cannot see, and each player's synchroniser only sends its
/// position to peers who can: the same space, or an interior and the outside while one of its
/// doors is open (<see cref="Linked"/>). The tables come from the server rather than from
/// replicated player state because a filter driven by replicated state stops the very update that
/// would lift it.
/// </para>
/// </summary>
public partial class InteriorManager : Node3D, Core.IOriginContainer, Core.IOriginShiftAware
{
    public const string NodeName = "Interiors";

    /// <summary>Where interiors live: far below the lowest ground in Switzerland (193 m).</summary>
    public const float InteriorBaseY = -3000f;

    private const float DoorReach = 1.6f;
    private const float ExitReach = 1.8f;
    /// <summary>Server-side check: generous, since the player's position is a relayed copy.</summary>
    private const float ServerDoorReach = 7f;
    /// <summary>A vehicle heading at a garage's or a barn's door this close opens it (<see cref="OpenForVehicle"/>)...</summary>
    private const float VehicleOpenReach = 10f;
    /// <summary>...and the server lets it, with the slack of a relayed copy moving at driving speed.</summary>
    private const float ServerVehicleDoorReach = 16f;
    /// <summary>At most this far off square to the door, radians: heading at it, not driving past.</summary>
    private const float VehicleOpenAngle = 0.6f;
    /// <summary>A client builds the interior behind an open door within this distance, to show it through the doorway.</summary>
    private const float BuildRange = 45f;
    /// <summary>A door with nobody this close to either side of it...</summary>
    private const float QuietRadius = 6f;
    /// <summary>...for this long shuts by itself.</summary>
    private const double QuietSeconds = 60;

    public static InteriorManager? Instance { get; private set; }

    public IChunkSource? Source { get; set; }
    public WorldOrigin? Origin { get; set; }
    /// <summary>Server: the replicated player nodes, for checking a request comes from the doorstep.</summary>
    public Node3D? Players { get; set; }
    /// <summary>Client: the player this client controls, resolved when asked.</summary>
    public Func<FootPlayer?>? LocalPlayer { get; set; }
    /// <summary>Client: a tile's building collision, which a player in an open doorway is let through.</summary>
    public Func<TileId, StaticBody3D?>? BuildingBodies { get; set; }
    /// <summary>Client: where the facade shader's occupancy cues go (<c>ChunkManager.SetOccupancy</c>).</summary>
    public Action<Vector4[], Vector4[], int>? OccupancySink { get; set; }
    /// <summary>Client: where the facade shader's open doors go (<c>ChunkManager.SetOpenDoors</c>, see <see cref="DoorPortals.OpenDoors"/>).</summary>
    public Action<Vector4[], Vector4[], int>? OpenDoorsSink { get; set; }

    /// <summary>
    /// Raised on the client when the outside world has to be drawn (true) or may be hidden
    /// (false): hidden only while the local player is inside with every door of the building shut.
    /// </summary>
    public event Action<bool>? OutsideShownChanged;

    // server tables, mirrored on every client
    private readonly Dictionary<long, string> _spaces = new();
    /// <summary>Open doors: door key (the building the door is on) to the key its interior is planned under.</summary>
    private readonly Dictionary<string, string> _doors = new();
    /// <summary>Server: how long each open door has had nobody near it.</summary>
    private readonly Dictionary<string, double> _quiet = new();
    private double _serverTick;

    // plans
    private readonly Dictionary<string, InteriorLayout> _cache = new();
    /// <summary>Door key to the key its plan is stored under, once looked up.</summary>
    private readonly Dictionary<string, string> _planKeys = new();
    private readonly Dictionary<string, Task<InteriorLayout?>> _pending = new();
    private string _storeDir = "";

    // client state
    private bool _presenting;
    private InteriorLayout? _current;
    private readonly Dictionary<string, InteriorNode> _built = new();
    private readonly HashSet<string> _building = new();
    /// <summary>Doors whose plan has been asked of the server and not yet received.</summary>
    private readonly HashSet<string> _asked = new();
    private readonly Dictionary<string, DoorLink> _links = new();
    private DoorPortals? _portals;
    private BuildingSounds? _sounds;
    /// <summary>The building collision the local player is currently let through, standing in a doorway.</summary>
    private StaticBody3D? _passing;
    private bool _outsideShown = true;
    private double _maintain;
    private string? _requestingDoor;
    private double _requestTimer;
    private double _vehicleCheck;
    /// <summary>Doors a vehicle asked open, and when: a refused or ignored door is not asked again at once.</summary>
    private readonly Dictionary<string, double> _vehicleAsked = new();
    private static ShaderMaterial? _material;

    // occupancy cues
    private readonly Dictionary<string, BuildingSounds.Occupied?> _boxes = new();
    private readonly List<BuildingSounds.Occupied> _occupied = new();
    private string _occupancyKey = "";

    // prompt
    private CanvasLayer? _ui;
    private Label? _prompt;
    private double _promptTimer;
    private double _hintTimer;

    public static InteriorManager Create(Node world, IChunkSource source, WorldOrigin origin)
    {
        var m = new InteriorManager { Name = NodeName, Source = source, Origin = origin };
        world.AddChild(m);
        Instance = m;
        return m;
    }

    public override void _Ready()
    {
        _storeDir = ProjectSettings.GlobalizePath("user://interiors");
        if (DisplayServer.GetName() == "headless" && Multiplayer.IsServer() && Online) return;
        _presenting = true;

        _ui = new CanvasLayer { Name = "InteriorUi", Layer = 9 };
        AddChild(_ui);
        _prompt = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _prompt.AddThemeFontSizeOverride("font_size", 18);
        _prompt.AddThemeColorOverride("font_outline_color", Colors.Black);
        _prompt.AddThemeConstantOverride("outline_size", 6);
        _prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _prompt.Position = new Vector2(-150, -140);
        _prompt.Size = new Vector2(300, 30);
        _ui.AddChild(_prompt);

        _portals = new DoorPortals(() => _links.Values, PlanAt)
        {
            Name = "Portals",
            OpenDoors = (boxes, axes, count) => OpenDoorsSink?.Invoke(boxes, axes, count),
        };
        AddChild(_portals);
        _sounds = new BuildingSounds { Name = "Sounds" };
        AddChild(_sounds);
        AddChild(new DoorwayGhosts(() => _links.Values, PlanAt) { Name = "Ghosts" });
        AddChild(new DoorLights(() => _links.Values) { Name = "DoorLights" });
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        DoorIndex.Clear();
    }

    /// <summary>The interior the local player is in, and its node — for probes and tools.</summary>
    public InteriorLayout? Current => _current;
    public InteriorNode? CurrentNode => _current != null && _built.TryGetValue(_current.Key, out var n) ? n : null;
    /// <summary>The door links built on this client (for probes).</summary>
    public IReadOnlyDictionary<string, DoorLink> Links => _links;
    public DoorPortals? Portals => _portals;

    // ---- a camera with no player: the shot queue's "i" heights (#320) ---------------------------

    /// <summary>
    /// Opens the front door nearest <paramref name="at"/> (asked again while a request is pending
    /// is harmless), and returns its key; null while no door within <paramref name="reach"/> has
    /// streamed in. Offline only: online a door is the server's, for a player standing at it.
    /// </summary>
    public string? OpenDoorForCamera(Vector3 at, float reach)
    {
        if (Online) return null;
        var door = DoorIndex.NearestEntrance(at, reach)?.Key.ToString();
        if (door != null && !_doors.ContainsKey(door) && _requestingDoor == null) AskDoor(door, true);
        return door;
    }

    /// <summary>A door's link once its interior is built here, else null.</summary>
    public DoorLink? BuiltLink(string door) =>
        _links.TryGetValue(door, out var link) && _built.ContainsKey(link.Plan) ? link : null;

    /// <summary>
    /// The free camera stands in <paramref name="plan"/>'s rooms, or back outside (null): the
    /// building then stays built and shown, as for a player inside it. Offline, with no player.
    /// </summary>
    public void CameraInside(string? plan)
    {
        _current = plan != null && _cache.TryGetValue(plan, out var layout) ? layout : null;
        Maintain();
    }

    /// <summary>Whether a world point is down where the interiors are, not in the world above.</summary>
    public static bool InInteriorSpace(Vector3 at) => at.Y < InteriorBaseY + 1000f;

    /// <summary>
    /// The same point up in the world, for distances: an interior lies straight under its
    /// building, <see cref="InteriorBaseY"/> standing for the building's ground floor, which
    /// <paramref name="ground"/> gives (the terrain height there, when known).
    /// </summary>
    public static Vector3 SurfacePoint(Vector3 at, Func<Vector3, float?>? ground = null)
    {
        if (!InInteriorSpace(at)) return at;
        float floor = ground?.Invoke(at) ?? 0f;
        return at with { Y = floor + (at.Y - InteriorBaseY) };
    }

    /// <summary>The same in LV95 (#185): an interior differs from its building only in altitude.</summary>
    public static GlobalPos SurfacePoint(GlobalPos at, Func<GlobalPos, float?>? ground = null)
    {
        if (!(at.Alt < InteriorBaseY + 1000f)) return at;
        float floor = ground?.Invoke(at) ?? 0f;
        return at with { Alt = floor + (at.Alt - InteriorBaseY) };
    }

    /// <summary>The built interior whose plan holds a point far underground, if any (none on a dedicated server).</summary>
    public InteriorLayout? LayoutAt(Vector3 at) => InteriorNode.Containing(_built.Values, at)?.Layout;

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    private long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;

    /// <summary>This peer keeps the tables: the server, or a client playing offline.</summary>
    private bool Authoritative => !Online || Multiplayer.IsServer();

    // ---- who is where ----------------------------------------------------------------------

    public string SpaceOf(long peer) => _spaces.TryGetValue(peer, out var k) ? k : "";

    public bool IsOpen(string door) => _doors.ContainsKey(door);

    /// <summary>
    /// Whether players in two spaces can see each other: the same space, or an interior and the
    /// outside while a door between them stands open.
    /// </summary>
    public bool Linked(string a, string b) =>
        a == b || (a.Length == 0 && _doors.ContainsValue(b)) || (b.Length == 0 && _doors.ContainsValue(a));

    /// <summary>Whether a remote peer is somewhere this client can see.</summary>
    public bool SameSpaceAsLocal(long peer) => Linked(SpaceOf(peer), SpaceOf(MyId));

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetPlayerSpace(long peer, string key)
    {
        if (key.Length == 0) _spaces.Remove(peer); else _spaces[peer] = key;
    }

    private void Broadcast(long peer, string key)
    {
        SetPlayerSpace(peer, key);
        if (Online) Rpc(MethodName.SetPlayerSpace, peer, key);
    }

    /// <summary>Server: tells a newly joined client who is already inside where, and which doors stand open.</summary>
    public void SendTableTo(long peer)
    {
        foreach (var (p, key) in _spaces) RpcId(peer, MethodName.SetPlayerSpace, p, key);
        foreach (var (door, plan) in _doors) RpcId(peer, MethodName.SetDoor, door, plan, true);
    }

    /// <summary>Server: a player left; they are no longer inside anything.</summary>
    public void ForgetPeer(long peer)
    {
        if (_spaces.ContainsKey(peer)) Broadcast(peer, "");
    }

    // ---- doors: the shared state -------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetDoor(string door, string plan, bool open)
    {
        bool was = _doors.ContainsKey(door);
        if (open) _doors[door] = plan; else _doors.Remove(door);
        _quiet.Remove(door);
        _planKeys[door] = plan;
        if (_requestingDoor == door) _requestingDoor = null;
        if (was != open && _presenting) DoorMoved(door, open);
    }

    private void BroadcastDoor(string door, string plan, bool open)
    {
        SetDoor(door, plan, open);
        if (Online) Rpc(MethodName.SetDoor, door, plan, open);
    }

    /// <summary>
    /// E at a door, from either side: opens it, or shuts it. False when there is no door in
    /// reach outside, so the caller can try other things; inside it always consumes the press.
    /// </summary>
    public bool TryDoor(FootPlayer player)
    {
        string? door;
        if (player.Indoors)
        {
            if (_current == null) return true;
            door = ExitAt(player)?.Door;
            if (door == null)
            {
                Hint(_current.AllEntrances().Count > 1
                    ? "The ways out are the doors, on the ground floor."
                    : "The way out is the front door, on the ground floor.");
                return true;
            }
        }
        else door = OutsideDoorInReach(player.GlobalPosition);
        if (door == null) return false;
        if (_requestingDoor != null) return true;
        AskDoor(door, !_doors.ContainsKey(door));
        return true;
    }

    /// <summary>How near the doorway a VR hand must be to work a door from outside, m (#243).</summary>
    private const float HandDoorReach = 0.7f;

    /// <summary>
    /// A VR hand gripping a door (#243): as <see cref="TryDoor"/>, but outside only the door the hand
    /// is at, between its sill and its lintel, and never a hint: a grip that finds no door is not a
    /// press of E. False when there is none, so the grip can do something else.
    /// </summary>
    public bool TryDoorByHand(FootPlayer player, Vector3 hand)
    {
        string? door;
        if (player.Indoors) door = _current == null ? null : ExitAt(player)?.Door;
        else
        {
            var e = DoorIndex.NearestEntrance(hand, HandDoorReach, OpenReachOutside);
            door = e is { } d && hand.Y > d.World.Y + 0.3f && hand.Y < d.World.Y + d.Height + 0.3f ? d.Key.ToString() : null;
        }
        if (door == null) return false;
        if (_requestingDoor == null) AskDoor(door, !_doors.ContainsKey(door));
        return true;
    }

    private void AskDoor(string door, bool open)
    {
        _requestingDoor = door;
        _requestTimer = 3;
        if (Online) RpcId(1, MethodName.RequestDoor, door, open);
        else _ = ServeDoor(MyId, door, open);
    }

    /// <summary>
    /// A vehicle heading at a garage's or a barn's door, outside within <see cref="VehicleOpenReach"/>
    /// or inside toward its doorway, asks it open, as a car driving up to a garage would have it.
    /// Where it is going is where it moves, so reversing in opens the door behind.
    /// </summary>
    private void OpenForVehicle(double delta)
    {
        if ((_vehicleCheck -= delta) > 0) return;
        _vehicleCheck = 0.2;
        var p = LocalPlayer?.Invoke();
        if (p == null || !IsInstanceValid(p) || p.DoorwayBox == null || _requestingDoor != null) return;
        var velocity = p.Velocity with { Y = 0 };
        var heading = velocity.LengthSquared() > 1f ? velocity : -p.GlobalTransform.Basis.Z;

        string? door = null;
        if (!p.Indoors) door = DoorIndex.VehicleDoorAhead(p.GlobalPosition, heading, VehicleOpenReach, VehicleOpenAngle)?.Key.ToString();
        else if (_current != null && BuildingFootprint.VehicleDoor(_current.DressedKind()))
        {
            // inside, the room is the approach: any way out it is heading at
            float cos = Mathf.Cos(VehicleOpenAngle);
            foreach (var link in _links.Values)
            {
                if (link.Plan != _current.Key) continue;
                var local = link.Inside.AffineInverse() * p.GlobalPosition;
                var towards = (link.Inside.Basis.Inverse() * heading).Normalized();
                if (local.Z < -VehicleOpenReach || towards.Z < cos || Math.Abs(local.X) > link.InsideWidth / 2 + 1f) continue;
                door = link.Door;
                break;
            }
        }
        if (door == null || _doors.ContainsKey(door)) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (_vehicleAsked.TryGetValue(door, out double asked) && now - asked < 4) return;
        _vehicleAsked[door] = now;
        AskDoor(door, true);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDoor(string door, bool open)
    {
        if (!Multiplayer.IsServer()) return;
        _ = ServeDoor(Multiplayer.GetRemoteSenderId(), door, open);
    }

    /// <summary>Server (or offline): opens or shuts a door for a player standing at it.</summary>
    private async Task ServeDoor(long sender, string door, bool open)
    {
        try
        {
            var layout = BuildingKey.TryParse(door, out _) ? await GetOrCreate(door) : null;
            if (layout == null || Origin == null) { Refuse(sender, "This door is locked."); return; }
            // measured to the door's centre, so a wide door, and its open leaves, reach further; a
            // garage or a barn opens for a vehicle driving up to it (OpenForVehicle), further off still
            float reach = ServerDoorReach + layout.EntranceFor(door).Width;
            if (BuildingFootprint.VehicleDoor(layout.DressedKind())) reach = Math.Max(reach, ServerVehicleDoorReach);
            if (!NearDoor(sender, layout, door, reach)) { Refuse(sender, "Too far from the door."); return; }
            // the plan first: the opener builds the interior while the door starts to swing
            if (open) SendPlan(sender, layout, door);
            if (_doors.ContainsKey(door) != open) BroadcastDoor(door, layout.Key, open);
            else if (sender == MyId) _requestingDoor = null;
            else RpcId(sender, MethodName.SetDoor, door, layout.Key, open);
        }
        catch (Exception e)
        {
            GD.PushError($"[interior] door {door} for peer {sender}: {e}");
            Refuse(sender, "This door is stuck.");
        }
    }

    private void Refuse(long sender, string why)
    {
        if (sender == MyId) Refused(why);
        else RpcId(sender, MethodName.DoorRefused, why);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DoorRefused(string why) => Refused(why);

    private void Refused(string why)
    {
        GD.Print($"[interior] refused: {why}");
        _requestingDoor = null;
        Hint(why);
    }

    /// <summary>Server: whether a player is within reach of either side of a door.</summary>
    private bool NearDoor(long peer, InteriorLayout layout, string door, float reach)
    {
        var body = PlayerBody(peer);
        if (body == null || Origin == null) return true;
        var e = layout.EntranceFor(door);
        var at = body.GlobalPosition;
        return at.DistanceTo(OutsideDoorAt(layout, e)) <= reach || at.DistanceTo(InsideDoorAt(layout, e)) <= reach;
    }

    private Node3D? PlayerBody(long peer) =>
        Players?.GetNodeOrNull<Node3D>(peer.ToString()) ?? (peer == MyId && !Online ? LocalPlayer?.Invoke() : null);

    /// <summary>The door on the facade, world space.</summary>
    public Vector3 OutsideDoorAt(InteriorLayout l, EntrancePlan e)
    {
        BuildingKey.TryParse(e.Door, out var k);
        return Origin!.ToWorld(k.Tile.MinE, k.Tile.MaxN, 0) + new Vector3(e.DoorX, e.DoorY, e.DoorZ);
    }

    /// <summary>The doorway in the interior's wall, world space.</summary>
    public Vector3 InsideDoorAt(InteriorLayout l, EntrancePlan e) => PlacementFor(l, Origin!) * new Vector3(e.X, 0, e.Z);

    /// <summary>Server (or offline): shuts every door nobody has been near for a while.</summary>
    private void TickDoors(double delta)
    {
        _serverTick += delta;
        if (_serverTick < 0.5 || _doors.Count == 0 || Origin == null) return;
        double dt = _serverTick;
        _serverTick = 0;

        var bodies = new List<Vector3>();
        if (Players != null)
            foreach (var n in Players.GetChildren())
                if (n is Node3D body) bodies.Add(body.GlobalPosition);
        if (!Online && LocalPlayer?.Invoke() is { } me && IsInstanceValid(me)) bodies.Add(me.GlobalPosition);

        foreach (var (door, plan) in _doors.ToList())
        {
            if (!_cache.TryGetValue(plan, out var l)) continue;
            var e = l.EntranceFor(door);
            var outside = OutsideDoorAt(l, e);
            var inside = InsideDoorAt(l, e);
            bool near = bodies.Any(p => p.DistanceTo(outside) < QuietRadius || p.DistanceTo(inside) < QuietRadius);
            double quiet = near ? 0 : _quiet.GetValueOrDefault(door) + dt;
            _quiet[door] = quiet;
            if (quiet > QuietSeconds) BroadcastDoor(door, plan, false);
        }
    }

    // ---- plans for doors other players opened -----------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private async void RequestPlan(string door)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        try
        {
            if (BuildingKey.TryParse(door, out _) && await GetOrCreate(door) is { } layout)
                SendPlan(sender, layout, door);
        }
        catch (Exception e) { GD.PushError($"[interior] plan for {door}, peer {sender}: {e}"); }
    }

    private void SendPlan(long peer, InteriorLayout layout, string door)
    {
        if (peer == MyId) ReceivePlan(layout, door);
        else RpcId(peer, MethodName.PlanFor, layout.ToCompressed(), door);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlanFor(byte[] data, string door)
    {
        if (InteriorLayout.FromCompressed(data) is { } layout) ReceivePlan(layout, door);
    }

    private void ReceivePlan(InteriorLayout layout, string door)
    {
        _cache[layout.Key] = layout;
        _planKeys[door] = layout.Key;
        _planKeys[layout.Key] = layout.Key;
        foreach (var e in layout.AllEntrances()) _planKeys[e.Door] = layout.Key;
        _asked.Remove(door);
        if (_presenting) Maintain();
    }

    // ---- client: what this client builds and shows ----------------------------------------------

    /// <summary>A door opened or shut: its sound on both sides, and the interior behind it.</summary>
    private void DoorMoved(string door, bool open)
    {
        var listener = GetViewport()?.GetCamera3D()?.GlobalPosition;
        if (_links.TryGetValue(door, out var link))
        {
            _sounds?.Door(link.Outside.Origin + link.Outside.Basis.Z * 0.3f, open);
            if (_current?.Key == link.Plan) _sounds?.Door(link.Inside.Origin - link.Inside.Basis.Z * 0.3f, open);
        }
        else if (listener is { } ear && BuildingKey.TryParse(door, out var k) && DoorIndex.Find(k) is { } d
                 && d.World.DistanceTo(ear) < 60f)
            _sounds?.Door(d.World, open);
        Maintain();
    }

    /// <summary>
    /// Builds what can be seen: an interior and a link for every open door near the local
    /// player, every door of the building they are in, and, from inside, every open door near
    /// one of its open doors (seen through it, across the street). Drops what can no longer be.
    /// </summary>
    private void Maintain()
    {
        if (!_presenting || Origin == null) return;
        var player = LocalPlayer?.Invoke();
        if (player != null && !IsInstanceValid(player)) player = null;
        string? inside = _current?.Key;
        // where the outside is looked at from: the player, the free camera when there is none,
        // or from inside, the building's own open doorways
        var from = new List<Vector3>();
        if (inside == null)
        {
            if (player != null) from.Add(player.GlobalPosition);
            else if (GetViewport()?.GetCamera3D() is { } cam && cam.GlobalPosition.Y > InteriorBaseY + 1000f)
                from.Add(cam.GlobalPosition);
        }
        else
            foreach (var l in _links.Values)
                if (l.Plan == inside && l.Swing > 0f) from.Add(l.Outside.Origin);

        bool Wanted(string door, string plan)
        {
            if (plan == inside) return true;
            return BuildingKey.TryParse(door, out var k) && DoorIndex.Find(k) is { } d
                && from.Any(at => d.World.DistanceTo(at) < BuildRange);
        }

        foreach (var (door, plan) in _doors)
            if (!_links.ContainsKey(door) && Wanted(door, plan)) EnsureLink(door, plan);

        foreach (var link in _links.Values.ToList())
        {
            link.Open = _doors.ContainsKey(link.Door);
            // a shut door of the building we are in stays linked: a leaf going solid on someone
            // standing in the doorway can push them out through the hole, and that is a way out
            bool gone = !link.Open && link.Swing <= 0f && link.Plan != inside;
            if (gone || !Wanted(link.Door, link.Plan)) DropLink(link);
        }

        foreach (var (plan, node) in _built.ToList())
            if (plan != inside && !_links.Values.Any(l => l.Plan == plan))
            {
                node.QueueFree();
                _built.Remove(plan);
            }

        bool shown = inside == null || _links.Values.Any(l => l.Plan == inside && (l.Open || l.Swing > 0f));
        if (shown != _outsideShown)
        {
            _outsideShown = shown;
            OutsideShownChanged?.Invoke(shown);
        }

        UpdateOccupancy();
    }

    private void EnsureLink(string door, string plan)
    {
        if (!_cache.TryGetValue(plan, out var layout))
        {
            if (!_asked.Add(door)) return;
            if (Online) RpcId(1, MethodName.RequestPlan, door);
            else LoadPlanOffline(door);
            return;
        }
        if (!_built.TryGetValue(plan, out var node))
        {
            BuildInterior(layout);
            return;
        }
        var e = layout.EntranceFor(door);
        var spot = BuildingKey.TryParse(door, out var k) ? DoorIndex.Find(k) : null;
        var link = DoorLink.Create(layout, e, Origin!, spot?.Width, spot?.Height);
        link.Open = _doors.ContainsKey(door);
        link.Leaf = node.Leaf(door);
        link.Shutter = node.Shutter(door);
        link.Shutter?.SetSwing(link.Swing);
        if (link.Leaf == null && DoorLeaf.OnFacade(layout.DressedKind()))
        {
            // a barn's pair or a garage's roll-up door hangs on the facade, and lives as long as the link
            link.Leaf = DoorLeaf.CreateOnFacade(door, link.Outside, link.OutsideWidth, link.OutsideHeight,
                layout.DressedKind(), _material!);
            AddChild(link.Leaf);
            link.Leaf.SetSwing(link.Swing);
        }
        _portals?.Attach(link);
        _links[door] = link;
    }

    private void DropLink(DoorLink link)
    {
        _portals?.Detach(link);
        if (link.Leaf is { Outward: true } pair) pair.QueueFree();
        else link.Leaf?.SetSwing(0);
        link.Shutter?.SetSwing(0);
        _links.Remove(link.Door);
    }

    private async void LoadPlanOffline(string door)
    {
        try
        {
            if (await GetOrCreate(door) is { } layout) ReceivePlan(layout, door);
        }
        catch (Exception e) { GD.PushError($"[interior] plan for {door}: {e}"); }
        finally { _asked.Remove(door); }
    }

    private async void BuildInterior(InteriorLayout layout)
    {
        if (!_building.Add(layout.Key) || Origin == null) return;
        try
        {
            // mesh arrays and the ArrayMesh off the main thread (RenderingServer calls are queued,
            // as for terrain tiles); a tall block is a few thousand boxes
            var material = _material ??= Styles.StyleKit.Material(Styles.MaterialRole.Interior);
            var (data, mesh) = await Task.Run(() =>
            {
                var d = InteriorMeshBuilder.Build(layout);
                return (d, InteriorNode.BuildMesh(d, material));
            });
            if (!IsInsideTree() || _built.ContainsKey(layout.Key)) return;
            var node = InteriorNode.Create(layout, data, material, PlacementFor(layout, Origin), mesh);
            AddChild(node);
            _built[layout.Key] = node;
            // the collision BVH a frame later, so the two costs do not land on one frame (#221)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (IsInstanceValid(node)) node.AddBody(data.Collision);
        }
        finally { _building.Remove(layout.Key); }
        Maintain();
    }

    /// <summary>The built interior a point far underground is in (nearest by plan position), if any.</summary>
    public string? PlanAt(Vector3 at) => InteriorNode.PlanAt(_built.Values, at);

    // ---- client: walking through ------------------------------------------------------------------

    /// <summary>
    /// Before the local player moves: standing in an open doorway outside, they are let through
    /// the building's shell (which has no hole in it) so they can reach the sill. Mounted, only a
    /// garage's or a barn's doorway, and only a vehicle that fits it (<see cref="Fits"/>).
    /// </summary>
    public void BeforeMove(FootPlayer p)
    {
        StaticBody3D? pass = null;
        if (!p.Indoors)
            foreach (var link in _links.Values)
            {
                if (!link.Passable || !Fits(p, link)) continue;
                bool inDoorway = p.DoorwayBox is { } box
                    ? link.InOutsideDoorway(p.GlobalPosition, -p.GlobalTransform.Basis.Z, box.HalfWidth, box.HalfLength)
                    : link.InOutsideDoorway(p.GlobalPosition, 0.32f);
                if (!inDoorway) continue;
                pass = BuildingBodies?.Invoke(link.Tile);
                break;
            }
        if (pass == _passing) return;
        if (_passing != null && IsInstanceValid(_passing)) p.RemoveCollisionExceptionWith(_passing);
        _passing = pass;
        if (pass != null) p.AddCollisionExceptionWith(pass);
    }

    /// <summary>After the local player moved from <paramref name="before"/>: across a sill, into the other side.</summary>
    public void AfterMove(FootPlayer p, Vector3 before)
    {
        foreach (var link in _links.Values)
        {
            bool inside = p.Indoors;
            // Out is let through whatever the door is doing: swinging, its leaf is not solid yet
            // but the door is not passable, and a leaf going solid can push someone standing in it
            // out through the hole. Past the hole is only the void under the terrain. In, the
            // facade's shell stops anyone the door does not let through.
            if (!inside && !link.Passable) continue;
            if (inside && p.InteriorKey != link.Plan) continue;
            if (!Fits(p, link)) continue;
            var frame = (inside ? link.Inside : link.Outside).AffineInverse();
            var a = frame * before;
            var b = frame * p.GlobalPosition;
            bool crossed = inside ? a.Z < 0 && b.Z >= 0 : a.Z >= 0 && b.Z < 0;
            if (!crossed) continue;
            var at = a.Lerp(b, a.Z / (a.Z - b.Z));
            // out: anywhere through the hole, it has no other side
            float half = inside ? link.InsideWidth / 2 : link.HalfPass;
            if (Mathf.Abs(at.X) > half || at.Y < -1.2f || at.Y > 1.2f) continue;
            Cross(p, link, inward: !inside);
            return;
        }
    }

    /// <summary>
    /// Whether the local player can pass a doorway as they are: on foot any, mounted a garage's or a
    /// barn's with a vehicle lower than the opening (a flyer never).
    /// </summary>
    private static bool Fits(FootPlayer p, DoorLink link) =>
        p.Ride == RideKind.OnFoot
        || link.VehicleDoor && p.DoorwayBox is { } box && box.Height < link.PassHeight - 0.05f;

    private void Cross(FootPlayer p, DoorLink link, bool inward)
    {
        if (inward && !_cache.ContainsKey(link.Plan)) return;
        var map = inward ? link.ToInside : link.ToOutside;
        var at = map * p.GlobalPosition;
        if (inward)
        {
            // a sill a little above the street: step up onto the floor, never into the slab under it
            var local = link.Inside.AffineInverse() * at;
            if (local.Y < 0.02f) at = link.Inside * new Vector3(local.X, 0.02f, local.Z);
        }
        var x = map.Basis.X;
        float turn = Mathf.Atan2(-x.Z, x.X);
        p.CrossDoor(inward ? link.Plan : null, at, turn, map.Basis);
        _current = inward ? _cache[link.Plan] : null;

        if (Online) RpcId(1, MethodName.RequestCross, link.Door, inward);
        else SetPlayerSpace(MyId, inward ? link.Plan : "");
        BeforeMove(p);
        Maintain();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestCross(string door, bool inward)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        // through a door that is open, or has just shut behind them
        if (!_planKeys.TryGetValue(door, out var plan)) return;
        Broadcast(sender, inward ? plan : "");
    }

    /// <summary>
    /// Whether a camera arm from <paramref name="from"/> to <paramref name="to"/>, in the player's
    /// space, reaches through an open doorway: where along it (0..1), the map into the space on
    /// the far side, and the building collision the arm must ignore at the sill.
    /// </summary>
    public bool ArmThroughDoor(FootPlayer p, Vector3 from, Vector3 to, out float t, out Transform3D map, out Rid pass)
    {
        t = 0;
        map = Transform3D.Identity;
        pass = default;
        foreach (var link in _links.Values)
        {
            if (!link.Passable) continue;
            bool inside = p.Indoors;
            if (inside && p.InteriorKey != link.Plan) continue;
            var frame = (inside ? link.Inside : link.Outside).AffineInverse();
            var a = frame * from;
            var b = frame * to;
            if (!(inside ? a.Z < 0 && b.Z > 0 : a.Z > 0 && b.Z < 0)) continue;
            float s = a.Z / (a.Z - b.Z);
            var at = a.Lerp(b, s);
            if (Mathf.Abs(at.X) > link.HalfPass || at.Y < 0.1f || at.Y > link.PassHeight - 0.1f) continue;
            t = s;
            map = inside ? link.ToOutside : link.ToInside;
            pass = BuildingBodies?.Invoke(link.Tile)?.GetRid() ?? default;
            return true;
        }
        return false;
    }

    /// <summary>The door E works for someone outside at <paramref name="at"/>, if any.</summary>
    public string? OutsideDoorInReach(Vector3 at) => DoorIndex.NearestEntrance(at, DoorReach, OpenReachOutside)?.Key.ToString();

    /// <summary>Extra reach in front of a door from outside: an open barn pair's leaves stand out there.</summary>
    private float OpenReachOutside(DoorIndex.Entry e) =>
        DoorLeaf.SwingsOut(e.Kind) && _doors.ContainsKey(e.Key.ToString()) ? DoorLeaf.OpenReach(e.Kind, e.Width) : 0f;

    /// <summary>The entrance the player is standing at, on the ground floor, if any.</summary>
    private EntrancePlan? ExitAt(FootPlayer player)
    {
        if (!player.Indoors || _current == null || CurrentNode is not { } node) return null;
        var local = node.ToLocal(player.GlobalPosition);
        if (local.Y > _current.StoreyHeight - 0.5f || local.Y < -0.5f) return null;   // ground floor only, not a cellar
        var at = new Vector2(local.X, local.Z);
        var kind = _current.DressedKind();
        // to the doorway, not its centre: a barn's is 10 m wide. An open leaf swung into the
        // room is in reach as far in as it stands.
        float Distance(EntrancePlan e)
        {
            var inward = new Vector2(e.InX, e.InZ).Normalized();
            var rel = at - new Vector2(e.X, e.Z);
            float along = Math.Max(0, Math.Abs(rel.Dot(new Vector2(-inward.Y, inward.X))) - e.Width / 2);
            float into = rel.Dot(inward);
            float deeper = !DoorLeaf.OnFacade(kind) && _doors.ContainsKey(e.Door) ? DoorLeaf.OpenReach(kind, e.Width) : 0f;
            float depth = into < 0 ? -into : Math.Max(0, into - deeper);
            return Mathf.Sqrt(along * along + depth * depth);
        }
        return _current.AllEntrances()
            .Where(e => Distance(e) <= ExitReach)
            .MinBy(Distance);
    }

    /// <summary>Whether the player is inside, at a door (where E works the door, not a cupboard).</summary>
    public bool AtExit(FootPlayer player) => ExitAt(player) != null;

    /// <summary>Drops the player's indoor state without walking out: a teleport is moving them anyway.</summary>
    public void Leave(FootPlayer player)
    {
        player.LeaveInterior(null, 0);
        _current = null;
        if (Online) RpcId(1, MethodName.RequestExit);
        else SetPlayerSpace(MyId, "");
        Maintain();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestExit()
    {
        if (!Multiplayer.IsServer()) return;
        Broadcast(Multiplayer.GetRemoteSenderId(), "");
    }

    /// <summary>The interior's transform: under its building, turned so its front wall faces the street.</summary>
    public static Transform3D PlacementFor(InteriorLayout l, WorldOrigin origin)
    {
        BuildingKey.TryParse(l.Key, out var key);
        var tile = key.Tile;
        var tileOrigin = origin.ToWorld(tile.MinE, tile.MaxN, 0);
        var at = tileOrigin + new Vector3(l.CenterX, 0, l.CenterZ);
        return new Transform3D(new Basis(Vector3.Up, l.Yaw), new Vector3(at.X, InteriorBaseY, at.Z));
    }

    // ---- client: occupancy cues ---------------------------------------------------------------------

    /// <summary>
    /// Buildings other players are in, for the facade shader and the muffled sounds. A building
    /// whose door stands open near us is left out: it is seen and heard for real, through the doorway.
    /// </summary>
    private void UpdateOccupancy()
    {
        var counts = new Dictionary<string, int>();
        long me = MyId;
        foreach (var (peer, key) in _spaces)
            if (peer != me && key.Length > 0) counts[key] = counts.GetValueOrDefault(key) + 1;
        foreach (var l in _links.Values) counts.Remove(l.Plan);
        if (_current != null) counts.Remove(_current.Key);

        _occupied.Clear();
        foreach (var (plan, n) in counts)
        {
            if (!_boxes.TryGetValue(plan, out var box)) { FetchBox(plan); continue; }
            if (box is { } b) _occupied.Add(b with { Count = n });
        }
        var near = LocalPlayer?.Invoke() is { } p && IsInstanceValid(p) ? p.GlobalPosition : Vector3.Zero;
        _occupied.Sort((x, y) => x.Center.DistanceSquaredTo(near).CompareTo(y.Center.DistanceSquaredTo(near)));

        string key2 = string.Join(";", _occupied.Take(8).Select(o => $"{o.Center}:{o.Count}"));
        if (key2 == _occupancyKey || OccupancySink == null) return;
        _occupancyKey = key2;
        var boxes = new Vector4[8];
        var axes = new Vector4[8];
        int count = Math.Min(8, _occupied.Count);
        for (int i = 0; i < count; i++)
        {
            var o = _occupied[i];
            boxes[i] = new Vector4(o.Center.X, o.Center.Z, o.HalfWidth, o.HalfDepth);
            axes[i] = new Vector4(o.Axis.X, o.Axis.Y, Mathf.Clamp(0.5f + 0.25f * (o.Count - 1), 0f, 1f), 0);
        }
        OccupancySink(boxes, axes, count);
    }

    /// <summary>
    /// The origin moved (#185). The interiors and their doorway quads are nodes and have moved;
    /// the doorway frames and the occupied buildings' boxes are kept here.
    /// </summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        foreach (var link in _links.Values) link.Shift(shift);
        foreach (var plan in _boxes.Keys.ToList())
            if (_boxes[plan] is { } box) _boxes[plan] = box with { Center = shift.Point(box.Center) };
    }

    private async void FetchBox(string plan)
    {
        _boxes[plan] = null;
        if (Source == null || Origin == null || !BuildingKey.TryParse(plan, out var k)) return;
        try
        {
            var tile = await Source.LoadBuildingsAsync(k.Tile);
            if (tile == null || k.Index < 0 || k.Index >= tile.Buildings.Count
                || BuildingTypes.For(tile).Boxes[k.Index] is not { } box) return;
            var origin = Origin.ToWorld(k.Tile.MinE, k.Tile.MaxN, 0);
            var b = tile.Buildings[k.Index];
            _boxes[plan] = new BuildingSounds.Occupied(origin + new Vector3(box.Center.X, b.MinY + 1.2f, box.Center.Y),
                box.AxisU, box.Width / 2, box.Depth / 2, 0);
        }
        catch (Exception e) { GD.PushWarning($"[interior] box of {plan}: {e.Message}"); }
    }

    // ---- per frame ------------------------------------------------------------------------------------

    private void Hint(string text)
    {
        if (_prompt == null) return;
        _prompt.Text = text;
        _prompt.Visible = true;
        _hintTimer = 2.5;
    }

    public override void _Process(double delta)
    {
        if (Authoritative) TickDoors(delta);
        if (!_presenting) return;

        if (_requestingDoor != null && (_requestTimer -= delta) <= 0) _requestingDoor = null;
        OpenForVehicle(delta);

        // the leaves swing toward what the server says, a big one slower
        foreach (var link in _links.Values)
        {
            float target = link.Open ? 1f : 0f;
            if (link.Swing == target) continue;
            link.Swing = Mathf.MoveToward(link.Swing, target, (float)delta / link.SwingSeconds);
            link.SetLeaves(link.Swing);
            if (link.Swing <= 0f) Maintain();
        }

        _maintain -= delta;
        if (_maintain <= 0)
        {
            _maintain = 0.25;
            Maintain();
        }

        if (_sounds != null && GetViewport()?.GetCamera3D() is { } ear)
            _sounds.Tick(delta, ear.GlobalPosition, _occupied, StreetSource(ear.GlobalPosition));

        UpdatePrompt(delta);
    }

    /// <summary>Inside, the way out nearest the listener, and how open it stands.</summary>
    private (Vector3, float)? StreetSource(Vector3 listener)
    {
        if (_current == null || CurrentNode is not { } node) return null;
        (Vector3, float)? best = null;
        float bestD = 12f;
        foreach (var e in _current.AllEntrances())
        {
            var at = node.GlobalTransform * new Vector3(e.X, 1.2f, e.Z);
            float d = at.DistanceTo(listener);
            if (d >= bestD) continue;
            bestD = d;
            best = (at, _links.TryGetValue(e.Door, out var l) ? l.Swing : 0f);
        }
        return best;
    }

    private void UpdatePrompt(double delta)
    {
        if (_prompt == null) return;
        if (_hintTimer > 0) { _hintTimer -= delta; return; }
        _promptTimer -= delta;
        if (_promptTimer > 0) return;
        _promptTimer = 0.15;

        var p = LocalPlayer?.Invoke();
        string? text = null;
        if (p != null && IsInstanceValid(p) && p.IsViewing && p.Ride == RideKind.OnFoot && !UiFocus.TextEntryActive)
        {
            string? door = null;
            if (p.Indoors && _current != null)
            {
                door = ExitAt(p)?.Door;
                if (door == null) text = ChurchRadios.PromptFor(p) ?? Loot.LootService.Instance?.PromptFor(p);
            }
            else if (!p.Indoors) door = OutsideDoorInReach(p.GlobalPosition);
            if (door != null)
                text = InputHints.Prompt(PlayerInput.InteractMount, _doors.ContainsKey(door) ? "Close the door" : "Open the door");
            // a Battle Royale crate at your feet comes first, as E opens it first (#194)
            if (BattleRoyale.BrCrates.Instance?.PromptFor(p) is { } crate) text = crate;
        }
        _prompt.Visible = text != null;
        if (text != null) _prompt.Text = text;
    }

    // ---- plans: memory, disk, generator --------------------------------------------------------

    /// <summary>
    /// The stored plan for the building behind a door, or a new one generated and stored now.
    /// Every door of a church leads to the church's one plan. Concurrent requests for the same
    /// building share one generation, so two players at one door get one house.
    /// </summary>
    public Task<InteriorLayout?> GetOrCreate(string door)
    {
        // a known plan answers synchronously: offline loot takes a whole container in one loop,
        // and relies on each take completing before the next
        if (_planKeys.TryGetValue(door, out var known) && _cache.TryGetValue(known, out var cached))
            return Task.FromResult<InteriorLayout?>(cached);
        return Resolve(door);
    }

    private async Task<InteriorLayout?> Resolve(string door)
    {
        var source = Source!;
        string key = await Task.Run(() => PlanKey(source, door));
        _planKeys[door] = key;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        if (_pending.TryGetValue(key, out var running)) return await running;
        string dir = _storeDir;
        var task = Task.Run(() => LoadOrGenerate(source, dir, key));
        _pending[key] = task;
        return await Finish(key, task);
    }

    /// <summary>The key a door's interior is stored under: the group's primary building, for a door of a group.</summary>
    public static async Task<string> PlanKey(IChunkSource source, string door)
    {
        if (!BuildingKey.TryParse(door, out var k)) return door;
        var tile = await source.LoadBuildingsAsync(k.Tile);
        if (tile == null || k.Index < 0 || k.Index >= tile.Buildings.Count) return door;
        return BuildingTypes.For(tile).GroupOf(k.Index) is { } g ? new BuildingKey(k.TileE, k.TileN, g.Primary).ToString() : door;
    }

    private async Task<InteriorLayout?> Finish(string key, Task<InteriorLayout?> task)
    {
        try
        {
            var layout = await task;
            if (layout != null)
            {
                _cache[key] = layout;
                _planKeys[layout.Key] = key;
            }
            return layout;
        }
        finally { _pending.Remove(key); }
    }

    public static async Task<InteriorLayout?> LoadOrGenerate(IChunkSource source, string storeDir, string keyText)
    {
        if (!BuildingKey.TryParse(keyText, out var key)) return null;
        var tile = await source.LoadBuildingsAsync(key.Tile);
        if (tile == null || key.Index < 0 || key.Index >= tile.Buildings.Count) return null;
        // a group is planned, and stored, once: under its primary building
        if (BuildingTypes.For(tile).GroupOf(key.Index) is { } group)
            key = new BuildingKey(key.TileE, key.TileN, group.Primary);
        var building = tile.Buildings[key.Index];
        string print = InteriorGenerator.GroupPrint(tile, key.Index);

        string path = Path.Combine(storeDir, $"{key.TileE}_{key.TileN}", $"{key.Index}.json");
        if (File.Exists(path) && InteriorLayout.FromJson(await File.ReadAllTextAsync(path)) is { } stored
            && stored.Matches(building, print))
            return stored;

        var roads = await source.LoadRoadsAsync(key.Tile);
        var grid = await source.LoadChunkAsync(key.Tile);
        var layout = InteriorGenerator.Generate(tile, key.Index, roads, grid);
        if (layout == null) return null;

        var problems = InteriorValidator.Validate(layout);
        if (problems.Count > 0)
            GD.PushWarning($"[interior] {keyText}: {string.Join("; ", problems.Take(3))}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".part";
        await File.WriteAllTextAsync(tmp, layout.ToJson());
        File.Move(tmp, path, overwrite: true);
        return layout;
    }
}

/// <summary>One built interior: its mesh, its collision and its front doors' leaves, placed under its building.</summary>
public partial class InteriorNode : Node3D
{
    private readonly Dictionary<string, DoorLeaf> _leaves = new();
    private readonly Dictionary<string, DoorLeaf> _shutters = new();

    public InteriorLayout Layout { get; private init; } = null!;

    /// <summary>
    /// The interior a point far underground is in: the one whose plan contains it (a metre of
    /// slack), else the nearest. Nearest alone is wrong just outside a doorway, where the
    /// neighbour across the street can be closer.
    /// </summary>
    public static string? PlanAt(IEnumerable<InteriorNode> nodes, Vector3 at)
    {
        string? best = null;
        float bestD = float.MaxValue;
        foreach (var node in nodes)
        {
            if (!node.IsInsideTree()) continue;
            var local = node.ToLocal(at);
            if (Math.Abs(local.X) <= node.Layout.Width / 2 + 1f && Math.Abs(local.Z) <= node.Layout.Depth / 2 + 1f)
                return node.Layout.Key;
            var o = node.GlobalPosition;
            float d = new Vector2(o.X - at.X, o.Z - at.Z).Length();
            if (d < bestD) { bestD = d; best = node.Layout.Key; }
        }
        return best;
    }

    /// <summary>The interior whose plan holds a point (half a metre of slack), if any.</summary>
    public static InteriorNode? Containing(IEnumerable<InteriorNode> nodes, Vector3 at)
    {
        foreach (var node in nodes)
        {
            if (!node.IsInsideTree()) continue;
            var local = node.ToLocal(at);
            if (Math.Abs(local.X) <= node.Layout.Width / 2 + 0.5f && Math.Abs(local.Z) <= node.Layout.Depth / 2 + 0.5f
                && local.Y > node.Layout.FloorY(0) - 1f && local.Y < node.Layout.FloorY(Math.Max(1, node.Layout.Floors.Count)) + 1f)
                return node;
        }
        return null;
    }

    /// <summary>The leaf of the door a given building key names, if this interior has that entrance.</summary>
    public DoorLeaf? Leaf(string door) => _leaves.TryGetValue(door, out var l) ? l : null;
    /// <summary>A barn door's pair as seen from in here (<see cref="DoorLeaf.CreateShutter"/>).</summary>
    public DoorLeaf? Shutter(string door) => _shutters.TryGetValue(door, out var l) ? l : null;

    // ---- gun lockers and safes (#165) ----------------------------------------------------------

    private readonly Dictionary<int, Node3D> _lockDoors = new();
    private readonly HashSet<int> _lockOpen = new();
    private const float LockOpenAngle = 1.9f;

    /// <summary>Whether the door of the locked container at this furniture index is shown open.</summary>
    public bool IsLockOpen(int furniture) => _lockOpen.Contains(furniture);

    /// <summary>Swings a gun locker's or safe's door open (or shut, on a restock); animated or at once.</summary>
    public void SetLockOpen(int furniture, bool open, bool animate)
    {
        if (!_lockDoors.TryGetValue(furniture, out var hinge)) return;
        if (open) _lockOpen.Add(furniture); else _lockOpen.Remove(furniture);
        float to = open ? -LockOpenAngle : 0f;
        if (!animate || !hinge.IsInsideTree()) { hinge.Rotation = new Vector3(0, to, 0); return; }
        var tween = hinge.CreateTween();
        tween.TweenProperty(hinge, "rotation:y", to, 0.9).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    private static void AddLockDoors(InteriorNode node, Material material)
    {
        var l = node.Layout;
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var f = l.Furniture[i];
            if (!Loot.LootTables.IsLocked(f.Type)) continue;
            var data = InteriorMeshBuilder.LockDoor(f);
            using var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
            arrays[(int)Mesh.ArrayType.Color] = data.Colors;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, material);
            // the piece's frame (back to -Z, turned), then its left front edge: the hinge
            var piece = new Transform3D(new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2), new Vector3(f.X, l.FloorY(f.Floor) + f.Lift, f.Z));
            var mount = new Node3D { Name = $"Lock{i}", Transform = piece * new Transform3D(Basis.Identity, new Vector3(-f.W / 2, 0, f.D / 2)) };
            var hinge = new Node3D { Name = "Hinge" };
            hinge.AddChild(new MeshInstance3D { Name = "Door", Mesh = mesh });
            mount.AddChild(hinge);
            node.AddChild(mount);
            node._lockDoors[i] = hinge;
        }
    }

    /// <summary>The interior's visual mesh; safe on a worker thread, like <c>ChunkNode.ToArrayMesh</c>.</summary>
    public static ArrayMesh BuildMesh(InteriorMeshBuilder.MeshData data, Material material)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    /// <summary>
    /// Builds the node. Given a prebuilt <paramref name="mesh"/>, the caller adds the collision
    /// itself (<see cref="AddBody"/>); without one, both are built here.
    /// </summary>
    public static InteriorNode Create(InteriorLayout layout, InteriorMeshBuilder.MeshData data, Material material, Transform3D placement,
        ArrayMesh? mesh = null)
    {
        var node = new InteriorNode { Name = "Interior_" + layout.Key, Transform = placement, Layout = layout };
        node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh ?? BuildMesh(data, material) });
        if (mesh == null) node.AddBody(data.Collision);

        // the front doors, shut: the way out is to open one, not to walk into the void
        foreach (var e in layout.AllEntrances())
        {
            var z = new Vector3(-e.InX, 0, -e.InZ).Normalized();
            var doorway = new Transform3D(new Basis(Vector3.Up.Cross(z), Vector3.Up, z), new Vector3(e.X, 0, e.Z));
            var (width, top) = layout.OpeningOf(e);
            var kind = layout.DressedKind();
            // a barn's pair or a garage's roll-up door moves on the facade, with its link; in here
            // only its shut face
            bool pair = DoorLeaf.OnFacade(kind);
            var leaf = pair ? DoorLeaf.CreateShutter(e.Door, doorway, width, top, kind, material)
                : DoorLeaf.Create(e.Door, doorway, width, top, kind, material);
            node.AddChild(leaf);
            leaf.SetSwing(0);
            (pair ? node._shutters : node._leaves)[e.Door] = leaf;
        }
        AddLockDoors(node, material);
        return node;
    }

    public void AddBody(Vector3[] collision)
    {
        // BackfaceCollision: every wall here is a single face, approached from whichever side the
        // player is on; one-sided, half of them would be walked straight through
        var body = new StaticBody3D { Name = "Body" };
        body.AddChild(new CollisionShape3D
        {
            Shape = new ConcavePolygonShape3D { Data = collision, BackfaceCollision = true },
        });
        AddChild(body);
    }
}
