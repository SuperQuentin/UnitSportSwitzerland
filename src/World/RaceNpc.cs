using Godot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.World;

/// <summary>
/// The driver of a race NPC (issue #39): a child of an NPC <see cref="FootPlayer"/> that exists
/// only on the NPC's owner. The NPC is a real racer — the same body, car and physics as a player,
/// replicated like one (NetPos from its owner), solid to everyone — simulated on the client that
/// asked for it, and driven by an <see cref="AutoPilot"/> through <see cref="FootPlayer.RideControls"/>.
///
/// <para>
/// The race is <see cref="RaceManager"/>'s: it hands this NPC its grid slot
/// (<see cref="RaceManager.NpcSetup"/>), reports its checkpoints from the position given to
/// <see cref="RaceManager.TrackNpc"/>, and says when it finished or was dropped.
/// </para>
/// </summary>
public partial class RaceNpc : Node
{
    public const string NodeName = "Npc";

    /// <summary>Its entrant id, <c>-(owner * 1000 + n)</c>.</summary>
    public long Id { get; set; }

    /// <summary>What it rides (a class <see cref="AutoPilot.Drives"/>).</summary>
    public RideKind Kind { get; set; }

    private FootPlayer _me = null!;
    private RaceManager? _race;
    private RaceRoute? _route;
    private double _goIn;
    private AutoPilot? _pilot;
    /// <summary>Driving in to its slot before GO (#51), and which race that is for.</summary>
    private NpcArrival? _arrival;
    private int _arrivalRace;
    private bool _reported;

    private static RideInput Hold() => new(0f, 0f, 0f, false, Handbrake: true);

    public override void _Ready()
    {
        _me = GetParent<FootPlayer>();
        if (!_me.IsMultiplayerAuthority()) { QueueFree(); return; }
        _me.RideControls = Hold;
        _race = _me.GetParent()?.GetParent()?.GetNodeOrNull<RaceManager>(RaceManager.NodeName);
        if (_race == null) return;
        _race.NpcSetup += OnSetup;
        _race.NpcFinished += OnFinished;
        _race.NpcDropped += OnDropped;
    }

    public override void _ExitTree()
    {
        if (_race == null) return;
        _race.NpcSetup -= OnSetup;
        _race.NpcFinished -= OnFinished;
        _race.NpcDropped -= OnDropped;
    }

    public override void _PhysicsProcess(double delta)
    {
        // on its mount, and back on it after being thrown off (the mount waits for the ground)
        if (_me.Ride != Kind && _me.IsOnFloor()) _me.SetRide(Kind);
        if (_arrival == null && _pilot == null && _race?.TakeArrival(Id) is { } a) Arrive(a);
        if (_arrival is { Staged: true } && !_reported)
        {
            _reported = true;
            _race!.ReportStaged(_arrivalRace, Id);
        }
        if (_route == null || _pilot != null || (_goIn -= delta) > 0) return;
        _pilot = AutoPilot.For(_route, _me);   // null until it is on its mount: tried again next step
        if (_pilot is not { } pilot) return;
        _arrival = null;   // handed over at GO
        _me.RideControls = () => pilot.Drive((float)GetPhysicsProcessDeltaTime(), true, RaceManager.Others(_me));
    }

    /// <summary>Appeared out of sight on the race road: drive in to the (provisional) slot.</summary>
    private void Arrive(RaceManager.Arrival a)
    {
        _arrivalRace = a.RaceId;
        var arrival = _arrival = new NpcArrival(_me, a.Lane, a.Zero, a.Style, a.Variant, a.Slot, a.Count)
        {
            Log = line => GD.Print($"[npc] {_me.Name}: {line}"),
        };
        _me.RideControls = () => arrival.Drive((float)GetPhysicsProcessDeltaTime(), Bodies());
        GD.Print($"[npc] {_me.Name} arriving {a.Style} for slot {a.Slot + 1} of {a.Count}");
    }

    /// <summary>Everyone else, for driving in: players, NPCs, parked or moving.</summary>
    private List<NpcArrival.Body> Bodies()
    {
        var list = new List<NpcArrival.Body>();
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != _me) list.Add(new NpcArrival.Body(p.GlobalPosition, p.WorldVelocity, p.Npc));
        return list;
    }

    private void OnSetup(RaceManager.NpcGrid g)
    {
        if (g.NpcId != Id || g.Course.Route is not { } route) return;
        _route = route;
        _goIn = g.Countdown;
        _pilot = null;
        if (_arrival != null) _arrival.SetSlot(g.At, g.Countdown);   // it drives the rest of the way itself
        else
        {
            _me.PlaceAt(g.At + Vector3.Up * 1.2f, Mathf.Atan2(-g.Forward.X, -g.Forward.Z));
            _me.RideControls = Hold;
        }
        _race!.TrackNpc(Id, () => _me.GlobalPosition);
        GD.Print($"[npc] {_me.Name} on the grid of race #{g.RaceId}");
    }

    private void OnFinished(long id, int position, double time)
    {
        if (id != Id) return;
        if (_pilot != null) _pilot.Finished = true;   // brake to a stop past the line
        GD.Print($"[npc] {_me.Name} finished P{position}");
    }

    private void OnDropped(long id)
    {
        if (id != Id) return;
        _route = null;
        _pilot = null;
        _arrival = null;
        _me.RideControls = Hold;
    }
}

/// <summary>
/// Race NPCs on the server, at <c>World/Npcs</c> on both sides: spawns them through the player
/// spawner for everyone (for <see cref="RaceManager"/>'s <c>/race npc</c>), caps them, and removes
/// them when their race ends or their owner leaves.
/// </summary>
public partial class RaceNpcs : Node
{
    public const string NodeName = "Npcs";
    public const int PerOwner = 8, Total = 32;

    private MultiplayerSpawner? _spawner;
    private Node3D? _players;
    private readonly Dictionary<long, RideKind> _live = new();

    public static RaceNpcs CreateServer(MultiplayerSpawner spawner, Node3D players) =>
        new() { Name = NodeName, _spawner = spawner, _players = players };

    public static RaceNpcs CreateClient() => new() { Name = NodeName };

    /// <summary>Server: spawns up to <paramref name="count"/> NPCs for <paramref name="owner"/> behind <paramref name="at"/>; returns their ids.</summary>
    /// <param name="place">Where the i-th one appears and its yaw (<see cref="NpcArrival.Plan"/>); null: behind <paramref name="at"/>.</param>
    public List<long> Spawn(long owner, int count, RideKind kind, Vector3 at, float yaw, System.Func<int, (Vector3 At, float Yaw)>? place = null)
    {
        var ids = new List<long>();
        if (_spawner == null || !AutoPilot.Drives(kind)) return ids;
        int owned = _live.Keys.Count(id => PlayerReplication.NpcOwner(id) == owner);
        count = Mathf.Min(count, Mathf.Min(PerOwner - owned, Total - _players!.GetChildCount()));
        var back = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));   // behind: forward is -Z turned by yaw
        for (int n = 1; n <= 999 && ids.Count < count; n++)
        {
            long id = PlayerReplication.NpcId(owner, n);
            if (_live.ContainsKey(id)) continue;
            var (pos, facing) = place?.Invoke(ids.Count) ?? (at + back * (8f * (ids.Count + 1)) + Vector3.Up * 1.5f, yaw);
            _live[id] = kind;
            _spawner.Spawn(PlayerReplication.NpcData(owner, n, (int)kind, pos, facing));
            ids.Add(id);
            GD.Print($"[npc] spawned {PlayerReplication.NodeName(id)} ({Label(id)}) for peer {owner}");
        }
        return ids;
    }

    /// <summary>Server: removes these NPCs for everyone.</summary>
    public void Retire(IEnumerable<long> ids)
    {
        foreach (long id in ids.ToList())
        {
            if (!_live.Remove(id)) continue;
            _players?.GetNodeOrNull(PlayerReplication.NodeName(id))?.QueueFree();
            GD.Print($"[npc] removed {PlayerReplication.NodeName(id)}");
        }
    }

    /// <summary>Server: removes every NPC of <paramref name="owner"/>.</summary>
    public void ForgetOwner(long owner) => Retire(_live.Keys.Where(id => PlayerReplication.NpcOwner(id) == owner));

    // ---- --npccheck (loopback test, on a client that does not own the NPCs): are they solid here? ----
    private readonly bool _check = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--npccheck") >= 0;
    private double _sinceCheck;

    public override void _PhysicsProcess(double delta)
    {
        if (!_check || _spawner != null || (_sinceCheck += delta) < 5.0) return;
        _sinceCheck = 0;
        var space = GetViewport().World3D.DirectSpaceState;
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
        {
            if (node is not FootPlayer { Npc: true } npc || npc.IsMultiplayerAuthority()) continue;
            // a 2 m ball where it stands must touch its body (a thin ray misses a body that moved
            // since the last physics step: the space is one step behind the synchronizer)
            var ball = new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = 2f },
                Transform = new Transform3D(Basis.Identity, npc.GlobalPosition + Vector3.Up),
            };
            var hits = space.IntersectShape(ball, 64);   // a terrain body alone returns a hit per face
            bool solid = hits.Any(h => h["collider"].AsGodotObject() == npc);
            GD.Print($"[npccheck] {npc.Name} at {npc.GlobalPosition:F1} ride {npc.Ride} solid={solid}");
        }
    }

    /// <summary>An NPC's display name: "NPC", what it rides, its number.</summary>
    public string Label(long id) =>
        $"NPC {(_live.TryGetValue(id, out var kind) ? RaceManager.MountName((int)kind) : null)} #{-id % 1000}";
}
