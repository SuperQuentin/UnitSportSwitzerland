using Godot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.World;

/// <summary>
/// The driver of a race NPC (issue #39): a child of an NPC <see cref="FootPlayer"/> on every peer,
/// active only on the NPC's current simulator (its multiplayer authority). The NPC is a real racer —
/// the same body, car and physics as a player, replicated like one (NetPos from its simulator),
/// solid to everyone — simulated first on the client that asked for it, then on whichever client
/// the server hands it to (<see cref="RaceNpcs"/>, issue #50), and driven by an
/// <see cref="AutoPilot"/> through <see cref="FootPlayer.RideControls"/>.
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
    /// <summary>This peer simulates it (it can start or stop doing so at any time, #50).</summary>
    private bool _active;

    private static RideInput Hold() => new(0f, 0f, 0f, false, Handbrake: true);

    public override void _Ready()
    {
        _me = GetParent<FootPlayer>();
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
        // only its current simulator drives it; a handoff arrives with a fresh NpcSetup (Resume)
        if (!_me.IsMultiplayerAuthority())
        {
            if (_active) { _active = false; _route = null; _pilot = null; _arrival = null; }
            return;
        }
        if (!_active) { _active = true; _me.RideControls = Hold; }
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
        if (_resumeCheckpoint >= 0)
        {
            pilot.D.Near = NearestPast(_route.Line, _me.GlobalPosition, _resumeCheckpoint);
            GD.Print($"[npc] {_me.Name} pilot re-armed at {pilot.Arc:F0} m, {_me.RideSpeed * 3.6f:F0} km/h");
        }
        _resumeCheckpoint = -1;
        _arrival = null;   // handed over at GO
        // its own driver, the same every race: skill 0.8..1, aggression 0..1, from its id
        var rng = new System.Random((int)(-Id % int.MaxValue));
        pilot.Temperament(0.8f + 0.2f * (float)rng.NextDouble(), (float)rng.NextDouble(), (int)(-Id % int.MaxValue));
        pilot.Log = s => GD.Print($"[npc] {_me.Name}: {s}");
        GD.Print($"[npc] {_me.Name} drives, skill {pilot.Skill:F2} aggression {pilot.Aggression:F2}");
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
        _resumeCheckpoint = -1;
        if (g.Resume)
        {
            // handed over (#50): it is where it is, moving; a pilot takes it from there (at GO if
            // the countdown is still on), starting on the line where the car is, not at the grid
            _resumeCheckpoint = g.Next;
            _race!.TrackNpc(Id, () => _me.GlobalPosition);
            GD.Print($"[npc] {_me.Name} resumes race #{g.RaceId} from checkpoint {g.Next}");
            return;
        }
        if (_arrival != null) _arrival.SetSlot(g.At, g.Countdown);   // it drives the rest of the way itself
        else
        {
            _me.PlaceAt(g.At + Vector3.Up * 1.2f, Mathf.Atan2(-g.Forward.X, -g.Forward.Z));
            _me.RideControls = Hold;
        }
        _race!.TrackNpc(Id, () => _me.GlobalPosition);
        GD.Print($"[npc] {_me.Name} on the grid of race #{g.RaceId}");
    }

    private int _resumeCheckpoint = -1;

    /// <summary>
    /// The line point nearest <paramref name="at"/> past checkpoint <paramref name="next"/> - 1: a
    /// pilot searching forward from the grid stops at the first dip in distance, which on a winding
    /// road can be a hairpin behind the car.
    /// </summary>
    private static int NearestPast(RaceLine line, Vector3 at, int next)
    {
        float from = next * RaceCourse.CheckpointEvery - 50f, to = from + RaceCourse.CheckpointEvery + 400f;
        int best = line.IndexAt(Mathf.Max(0f, from));
        float bestD = float.MaxValue;
        for (int i = best; i < line.Points.Count && line.Arc[i] <= to; i++)
        {
            float d = RaceRoute.Flat(line.Points[i] - at).LengthSquared();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
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
/// Race NPCs, at <c>World/Npcs</c> on both sides. The server spawns them through the player
/// spawner for everyone (for <see cref="RaceManager"/>'s <c>/race npc</c>), caps them, decides
/// which client simulates each one, and removes them when their race ends or nobody can run them.
///
/// <para>
/// <b>An NPC belongs to nobody</b> (issue #50): the client that asked for it simulates it first,
/// but the server moves it to another client when that one leaves, crashes or goes too far —
/// the race goes on, and the others keep racing it. A client may simulate an NPC only while its
/// own player is within <see cref="Zone"/> of it: the simulator's client is what gives the NPC a
/// world (terrain collision, trees), so a pilot 5 km up must not be handed a car in a valley.
/// With nobody in the zone the NPC is retired (DNF if it is racing).
/// </para>
/// </summary>
public partial class RaceNpcs : Node
{
    public const string NodeName = "Npcs";
    public const int PerOwner = 8, Total = 32;

    /// <summary>A client simulates at most this many NPCs, whoever asked for them (after handoffs).</summary>
    public const int MaxSimulated = 12;

    /// <summary>
    /// Horizontal metres within which a client may be given an NPC to simulate. A client builds
    /// terrain collision on the 1 km tiles within one tile of each collision anchor
    /// (<c>LodPolicy.CollisionMaxDist</c> = 1), so from anywhere in its own tile it has solid
    /// ground at least 1000 m out in every direction, and full-detail grids with roads four tiles
    /// out (<c>RoadMaxDist</c>). An NPC kept within <see cref="Zone"/> × <see cref="LeaveFactor"/>
    /// = 900 m therefore always drives on ground its simulator already has: the NPC's own anchor
    /// only extends collision onto grids that are loaded anyway, it never streams a new area.
    /// </summary>
    public const float Zone = 600f;

    /// <summary>A simulator keeps its NPC out to this multiple of <see cref="Zone"/> (hysteresis).</summary>
    public const float LeaveFactor = 1.5f;

    /// <summary>An NPC moved for distance stays at least this long with its simulator (no ping-pong).</summary>
    private const double MinHold = 5.0;

    /// <summary>No state from the simulator for this long: it is gone (ENet takes 5-30 s to notice a crash).</summary>
    public const double StaleSeconds = 2.5;

    private const double ReviewPeriod = 1.0;

    private MultiplayerSpawner? _spawner;
    private Node3D? _players;
    private sealed class Live { public RideKind Kind; public double Since; }
    private readonly Dictionary<long, Live> _live = new();
    private double _review;

    public static RaceNpcs CreateServer(MultiplayerSpawner spawner, Node3D players) =>
        new() { Name = NodeName, _spawner = spawner, _players = players };

    public static RaceNpcs CreateClient() => new() { Name = NodeName };

    private RaceManager? Race => GetParent()?.GetNodeOrNull<RaceManager>(RaceManager.NodeName);
    private InterestService? Interest => GetParent()?.GetNodeOrNull<InterestService>(InterestService.NodeName);
    private static double Now => Time.GetTicksMsec() / 1000.0;

    private FootPlayer? Npc(long id) => GetParent()?.GetNodeOrNull<FootPlayer>("Players/" + PlayerReplication.NodeName(id));

    /// <summary>Server: the client simulating this NPC right now (the node's authority).</summary>
    public long SimulatorOf(long id) => Npc(id)?.GetMultiplayerAuthority() ?? PlayerReplication.NpcOwner(id);

    /// <summary>Server: how many NPCs <paramref name="peer"/> simulates.</summary>
    public int SimulatedBy(long peer) => _live.Keys.Count(id => SimulatorOf(id) == peer);

    /// <summary>Server: spawns up to <paramref name="count"/> NPCs for <paramref name="owner"/> behind <paramref name="at"/>; returns their ids.</summary>
    /// <param name="place">Where the i-th one appears and its yaw (<see cref="NpcArrival.Plan"/>); null: behind <paramref name="at"/>.</param>
    public List<long> Spawn(long owner, int count, RideKind kind, Vector3 at, float yaw, System.Func<int, (Vector3 At, float Yaw)>? place = null)
    {
        var ids = new List<long>();
        if (_spawner == null || !AutoPilot.Drives(kind)) return ids;
        int owned = _live.Keys.Count(id => PlayerReplication.NpcOwner(id) == owner);
        count = Mathf.Min(count, Mathf.Min(PerOwner - owned, Total - _players!.GetChildCount()));
        count = Mathf.Min(count, MaxSimulated - SimulatedBy(owner));
        var back = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));   // behind: forward is -Z turned by yaw
        for (int n = 1; n <= 999 && ids.Count < count; n++)
        {
            long id = PlayerReplication.NpcId(owner, n);
            if (_live.ContainsKey(id)) continue;
            var (pos, facing) = place?.Invoke(ids.Count) ?? (at + back * (8f * (ids.Count + 1)) + Vector3.Up * 1.5f, yaw);
            _live[id] = new Live { Kind = kind, Since = Now };
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
            Npc(id)?.QueueFree();
            Interest?.ForgetPeer(id);
            GD.Print($"[npc] removed {PlayerReplication.NodeName(id)}");
        }
    }

    /// <summary>Server: <paramref name="peer"/> left. Its NPCs go to someone near them, or retire.</summary>
    public void PeerLeft(long peer)
    {
        foreach (long id in _live.Keys.Where(id => SimulatorOf(id) == peer).ToList())
            HandOff(id, peer, "its simulator left");
    }

    public override void _Process(double delta)
    {
        if (_spawner == null || (_review += delta) < ReviewPeriod) return;
        _review = 0;
        foreach (long id in _live.Keys.ToList())
        {
            if (Npc(id) is not { } npc) continue;
            long sim = npc.GetMultiplayerAuthority();
            var simNode = _players!.GetNodeOrNull<FootPlayer>(sim.ToString());
            if (simNode == null || Now - npc.LastNetState > StaleSeconds)
            {
                HandOff(id, sim, simNode == null ? "its simulator left" : "its simulator stopped sending");
                continue;
            }
            float d = Flat(npc.GlobalPosition, simNode.GlobalPosition);
            if (d > Zone * LeaveFactor && Now - _live[id].Since >= MinHold)
                HandOff(id, sim, $"its simulator is {d:F0} m away");
        }
    }

    /// <summary>Server: gives the NPC to the best client in its zone other than <paramref name="from"/>, or retires it.</summary>
    private void HandOff(long id, long from, string why)
    {
        if (Npc(id) is not { } npc) return;
        long to = Best(id, npc, from);
        if (to == 0)
        {
            GD.Print($"[npc] {npc.Name}: {why}, nobody within {Zone:F0} m — retired");
            Retire(new[] { id });
            return;
        }
        GD.Print($"[npc] {npc.Name}: {why} — handed from peer {from} to peer {to}");
        npc.SetSimulator((int)to);
        _live[id].Since = Now;
        npc.RefreshNetVisibility(to);   // it must exist there: an NPC always does on its simulator
        Rpc(MethodName.Migrate, id, (int)to);
        npc.RefreshRelays();   // the relays skip the simulator: now another peer
        Race?.ResumeNpc(id);
    }

    /// <summary>
    /// The client that should simulate an NPC: a player within <see cref="Zone"/> of it, still
    /// sending, under <see cref="MaxSimulated"/>; entrants of the NPC's race first, then someone
    /// on the ground over someone flying, then the nearest. 0 when there is none.
    /// </summary>
    private long Best(long id, FootPlayer npc, long exclude)
    {
        int race = Race?.RaceOf(id) ?? 0;
        long best = 0;
        (int, int, float) bestKey = default;
        foreach (var child in _players!.GetChildren())
        {
            if (child is not FootPlayer { Npc: false } p || !long.TryParse(p.Name, out long peer) || peer == exclude) continue;
            float d = Flat(p.GlobalPosition, npc.GlobalPosition);
            if (d > Zone || Now - p.LastNetState > StaleSeconds || SimulatedBy(peer) >= MaxSimulated) continue;
            var key = (race != 0 && Race?.RaceOf(peer) == race ? 0 : 1,
                p.Ride is >= RideKind.Wingsuit and <= RideKind.Plane ? 1 : 0, d);
            if (best == 0 || key.CompareTo(bestKey) < 0) { best = peer; bestKey = key; }
        }
        return best;
    }

    /// <summary>Server: whether <paramref name="peer"/>'s player may take on an NPC at <paramref name="at"/> (the host role uses the same zone).</summary>
    public bool InZone(long peer, Vector3 at) =>
        _players?.GetNodeOrNull<FootPlayer>(peer.ToString()) is { } p && Flat(p.GlobalPosition, at) <= Zone;

    public static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>
    /// Server → everyone: the NPC is simulated by <paramref name="peer"/> from now on. Each peer
    /// that has the NPC moves its authority (node and <c>Sync</c>); the new simulator takes the
    /// body over, the old one keeps a remote copy. A peer without the node ignores it: if it
    /// spawns there later, the spawn state names the current simulator.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Migrate(long id, int peer)
    {
        if (Npc(id) is not { } npc) return;
        npc.SetSimulator(peer);
        if (!npc.IsMultiplayerAuthority()) Race?.ForgetNpc(id);
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
            if (node is not FootPlayer { Npc: true } npc) continue;
            // a 2 m ball where it stands must touch its body (a thin ray misses a body that moved
            // since the last physics step: the space is one step behind the synchronizer)
            var ball = new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = 2f },
                Transform = new Transform3D(Basis.Identity, npc.GlobalPosition + Vector3.Up),
            };
            var hits = space.IntersectShape(ball, 64);   // a terrain body alone returns a hit per face
            bool solid = hits.Any(h => h["collider"].AsGodotObject() == npc);
            GD.Print($"[npccheck] {npc.Name} at {npc.GlobalPosition:F1} ride {npc.Ride} v {npc.WorldVelocity.Length():F1} solid={solid} sim={npc.SimPeer}{(npc.IsMultiplayerAuthority() ? " (here)" : "")}");
        }
    }

    /// <summary>An NPC's display name: "NPC", what it rides, its number.</summary>
    public string Label(long id) =>
        $"NPC {(_live.TryGetValue(id, out var live) ? RaceManager.MountName((int)live.Kind) : null)} #{-id % 1000}";
}
