using Godot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.World;

/// <summary>
/// The driver of a race NPC (issue #39): a child of an NPC <see cref="FootPlayer"/> that exists
/// only on the NPC's owner. The NPC is a real racer — the same body, car and physics as a player,
/// replicated by the same synchronizer, solid to everyone — simulated on the client that asked for
/// it, and driven by an <see cref="AutoPilot"/> through <see cref="FootPlayer.RideControls"/>.
///
/// <para>
/// The race glue is the two marked methods: <see cref="OnRaceSetup"/> (grid slot, countdown) and
/// <see cref="ReportProgress"/> (checkpoints and the line, reported through
/// <see cref="Checkpoint"/> / <see cref="Crossed"/>, which the race manager wires to its RPCs).
/// </para>
/// </summary>
public partial class RaceNpc : Node
{
    public const string NodeName = "Npc";
    private const float CheckpointEvery = 200f, CheckpointReach = 30f;

    /// <summary>The car it drives (a <see cref="CarCatalog"/> kind).</summary>
    public RideKind Kind { get; set; }

    /// <summary>Race glue: a checkpoint passed, in order.</summary>
    public System.Action<int>? Checkpoint;

    /// <summary>Race glue: the finish line crossed.</summary>
    public System.Action? Crossed;

    private FootPlayer _me = null!;
    private RaceRoute? _route;
    private float _finish;
    private int _next;
    private double _goIn;
    private bool _going, _done;
    private AutoPilot? _pilot;

    private static RideInput Hold() => new(0f, 0f, 0f, false, Handbrake: true);

    public override void _Ready()
    {
        _me = GetParent<FootPlayer>();
        if (!_me.IsMultiplayerAuthority()) { QueueFree(); return; }
        _me.RideControls = Hold;
    }

    public override void _PhysicsProcess(double delta)
    {
        // in its car, and back in it after being thrown out (the mount waits for the ground)
        if (_me.Ride != Kind && _me.IsOnFloor()) _me.SetRide(Kind);
        if (_route == null || _done) return;
        if (!_going)
        {
            _goIn -= delta;
            if (_goIn > 0) return;
            _going = true;
            if (CarCatalog.For(Kind) is { } spec)
            {
                var pilot = _pilot = new AutoPilot(_route, _me, spec);
                _me.RideControls = () => pilot.Drive((float)GetPhysicsProcessDeltaTime(), true, Others(_me));
            }
        }
        ReportProgress();
    }

    // ---- race glue ----

    /// <summary>On the grid at <paramref name="slot"/> of <paramref name="count"/>, handbrake on, GO in <paramref name="countdown"/> s.</summary>
    public void OnRaceSetup(RaceRoute route, float finish, int slot, int count, double countdown)
    {
        _route = route;
        _finish = finish;
        _next = 0;
        _goIn = countdown;
        _going = _done = false;
        _pilot = null;
        var line = route.Line;
        float s = 12f + 15f * (count - 1 - slot);   // same single-file grid as the players'
        var fwd = RaceRoute.Flat(line.PointAt(s + 2f) - line.PointAt(s - 2f)).Normalized();
        _me.GlobalPosition = line.PointAt(s) + Vector3.Up * 1.2f;
        _me.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
        _me.RequestReplacement();   // stopped, and put down on the ground once it is there
        _me.RideControls = Hold;
        GD.Print($"[npc] {_me.Name} on the grid, slot {slot + 1} of {count}, {finish:F0} m to go");
    }

    /// <summary>Checkpoints in order, then the line, from the NPC's position along the route.</summary>
    private void ReportProgress()
    {
        var line = _route!.Line;
        int near = 0;
        float best = float.MaxValue;
        for (int i = 0; i < line.Points.Count; i += 2)
        {
            float d = RaceRoute.Flat(line.Points[i] - _me.GlobalPosition).LengthSquared();
            if (d < best) { best = d; near = i; }
        }
        float arc = line.Arc[near];
        if (Mathf.Sqrt(best) >= CheckpointReach) return;
        while (arc >= (_next + 1) * CheckpointEvery && (_next + 1) * CheckpointEvery <= _finish)
            Checkpoint?.Invoke(_next++);
        if (arc < _finish) return;
        Crossed?.Invoke();
        if (_pilot != null) _pilot.Finished = true;   // brake to a stop past the line
        _done = true;
        GD.Print($"[npc] {_me.Name} crossed the line");
    }

    private static IEnumerable<AutoPilot.Other> Others(FootPlayer me)
    {
        foreach (var node in me.GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != me)
                yield return new AutoPilot.Other(p.GlobalPosition, p.Velocity.Length(), false);
    }
}

/// <summary>
/// Race NPCs on the server, at <c>World/Npcs</c> on both sides (RPCs route by path): spawns them
/// through the player spawner for everyone, caps them, and removes them with their owner.
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

    /// <summary>Client → server: <paramref name="count"/> NPCs in <paramref name="rideKind"/> next to the sender.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestNpc(int count, int rideKind, Vector3 at, float headingYaw)
    {
        if (_spawner != null) Spawn(Multiplayer.GetRemoteSenderId(), count, (RideKind)rideKind, at, headingYaw);
    }

    /// <summary>Server: spawns up to <paramref name="count"/> NPCs for <paramref name="owner"/>; returns their ids.</summary>
    public List<long> Spawn(long owner, int count, RideKind kind, Vector3 at, float yaw)
    {
        var ids = new List<long>();
        // a ground mount the AutoPilot can drive: cars, for now
        if (_spawner == null || !CarCatalog.IsCar(kind)) return ids;
        if (_players!.GetNodeOrNull<Node3D>(owner.ToString()) is not { } me) return ids;
        // the position is the client's word: only next to its own player
        if (me.GlobalPosition.DistanceTo(at) > 60f) at = me.GlobalPosition;
        int owned = _live.Keys.Count(id => PlayerReplication.NpcOwner(id) == owner);
        count = Mathf.Min(count, Mathf.Min(PerOwner - owned, Total - _players.GetChildCount()));
        var back = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));   // behind: forward is -Z turned by yaw
        for (int n = 1; n <= 999 && ids.Count < count; n++)
        {
            long id = PlayerReplication.NpcId(owner, n);
            if (_live.ContainsKey(id)) continue;
            var pos = at + back * (8f * (ids.Count + 1)) + Vector3.Up * 1.5f;
            _live[id] = kind;
            _spawner.Spawn(PlayerReplication.NpcData(owner, n, (int)kind, pos, yaw));
            ids.Add(id);
            GD.Print($"[npc] spawned {PlayerReplication.NodeName(id)} ({Label(id)}) for peer {owner}");
        }
        return ids;
    }

    /// <summary>Server: removes <paramref name="owner"/>'s NPCs, except those in <paramref name="keep"/>.</summary>
    public void ForgetOwner(long owner, ICollection<long>? keep = null)
    {
        foreach (long id in _live.Keys.Where(id => PlayerReplication.NpcOwner(id) == owner && keep?.Contains(id) != true).ToList())
        {
            _live.Remove(id);
            _players?.GetNodeOrNull(PlayerReplication.NodeName(id))?.QueueFree();
            GD.Print($"[npc] removed {PlayerReplication.NodeName(id)}");
        }
    }

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

    /// <summary>The car an NPC drives, as its display name.</summary>
    public string Label(long id) =>
        $"NPC {(_live.TryGetValue(id, out var kind) ? CarCatalog.For(kind)?.Label : null)} #{-id % 1000}";
}
