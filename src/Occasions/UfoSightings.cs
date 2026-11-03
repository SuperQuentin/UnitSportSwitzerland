using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Occasions;

/// <summary>
/// The UFO sightings (#863, docs/notes/occasions/ufo-sightings.md), at <c>World/UfoSightings</c> on
/// the server and every client (RPCs route by path).
///
/// <para>
/// <b>The authority plans</b> (the dedicated server, or the game offline): while the world runs the
/// <c>ufo</c> occasion and the game hour is night (<see cref="SightingPlanner.Night"/>), at each slot
/// of simulated time it rolls every 2 km cell a real player is in (<see cref="SightingPlanner.Roll"/>)
/// and finds the anchor only it can: a cow of a herd near the player for an abduction
/// (<see cref="Farming.Herds.CowNear"/>), a flat meadow for a landing, a valley point for the
/// mothership. The plan (<see cref="Sighting"/>) goes to every peer once (<see cref="Plan"/>, and
/// <see cref="SendTo"/> for a joiner); <b>every peer plays it back</b> from the shared simulation
/// clock (<see cref="SightingPath"/>), so all see the same saucer at the same place.
/// </para>
///
/// <para>
/// <b>The beams are real</b>: each saucer's beam is added to <see cref="TractorBeams"/> on every peer
/// (on a client only if this viewer sees the sightings), so the server lifts its cows and dropped
/// items, and a player standing still under one is lifted by their own body, like under a player's
/// saucer. <b>A landed saucer is a real parked UFO</b> while it stands (<see cref="SightingPath.BoardFrom"/>
/// .. <see cref="SightingPath.BoardTo"/>): the authority places one through
/// <see cref="VehicleManager.Place"/>, so E (pad Y, VR grip) at its ramp boards it like any parked
/// vehicle and the server counts it as the boarder's; once claimed, the sighting has lost that
/// saucer (<see cref="Taken"/>) and nobody draws it any more. Unclaimed, it is taken back at lift-off.
/// </para>
/// </summary>
public partial class UfoSightings : Node3D, IOriginContainer
{
    public const string NodeName = "UfoSightings";

    public static UfoSightings? Instance { get; private set; }

    private readonly ChunkManager? _chunks;
    private readonly WorldOrigin _origin;
    private readonly bool _dedicated;

    private readonly Dictionary<int, Sighting> _plans = new();
    private readonly HashSet<int> _taken = new();
    /// <summary>Authority: the parked UFO standing in for each landed saucer, by sighting id.</summary>
    private readonly Dictionary<int, string> _placed = new();
    private readonly HashSet<(long, long)> _cells = new();
    private readonly List<int> _drop = new();
    private long _slot = long.MinValue;
    private double _checkIn;

    /// <summary>
    /// <c>--sighting kind[,kind…]</c>: one of each, in turn, as soon as there is a player and ground
    /// (the next once the last has ended), night or day, occasion or not.
    /// </summary>
    private readonly List<SightingKind> _forced = new();
    private int _forcedCount;
    private double _forcedUntil;

    public UfoSightings(ChunkManager? chunks, WorldOrigin origin, bool dedicated)
    {
        Name = NodeName;
        _chunks = chunks;
        _origin = origin;
        _dedicated = dedicated;
    }

    public UfoSightings() : this(null, WorldOrigin.SwissDefault(), false) { }

    public static UfoSightings Create(Node world, ChunkManager? chunks, WorldOrigin origin, bool dedicated)
    {
        var s = new UfoSightings(chunks, origin, dedicated);
        world.AddChild(s);
        return s;
    }

    public override void _EnterTree()
    {
        Instance = this;
        TractorBeams.Scripted = AddBeams;
    }

    public override void _ExitTree()
    {
        if (TractorBeams.Scripted == AddBeams) TractorBeams.Scripted = null;
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        foreach (var kind in (CmdArgs.Value("--sighting") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<SightingKind>(kind.Trim(), true, out var k)) _forced.Add(k);
        if (!_dedicated) ReadyDraw();
    }

    // ---- roles and clocks ----------------------------------------------------------------------

    /// <summary>This peer plans: the server, or the game offline.</summary>
    public bool Authority => _dedicated || !NetLink.Online(this);

    /// <summary>The shared simulation clock the plans are played on.</summary>
    public static double Now => SimClock.SimAt(ClockSync.ServerNow);

    /// <summary>The sightings this peer knows (running or about to).</summary>
    public IReadOnlyCollection<Sighting> Plans => _plans.Values;

    /// <summary>Whether a sighting's landed saucer was boarded and flown away.</summary>
    public bool IsTaken(int id) => _taken.Contains(id);

    /// <summary>The parked UFO's name standing in for a landed saucer.</summary>
    public static string ParkedName(int id) => $"ufo_sighting_{id}";

    /// <summary>The same, made once a sighting (it is asked every frame).</summary>
    private readonly Dictionary<int, NodePath> _names = new();
    private NodePath NameOf(int id) => _names.TryGetValue(id, out var n) ? n : _names[id] = new NodePath(ParkedName(id));

    /// <summary>The world point of a craft's pose (origin of the saucer, its legs' feet).</summary>
    public Vector3 WorldOf(Sighting s, in CraftPose pose) => _origin.ToWorld(s.E, s.N, s.Ground) + pose.Offset;

    // ---- the beams -----------------------------------------------------------------------------

    /// <summary>Adds the sightings' saucer beams to the tick's list (<see cref="TractorBeams.Scripted"/>).</summary>
    private void AddBeams(List<TractorBeams.Beam> beams)
    {
        if (_plans.Count == 0 || !SeesSightings) return;
        double now = Now;
        foreach (var s in _plans.Values)
        {
            if (s.Kind == SightingKind.Mothership || now < s.Start || now > s.End) continue;
            for (int c = 0; c < s.Count; c++)
            {
                var pose = SightingPath.Pose(s, c, now);
                if (!pose.Shown || !pose.Beam) continue;
                var apex = WorldOf(s, pose) + UfoHatch;
                beams.Add(new TractorBeams.Beam(null, apex, SightingPath.Velocity(s, c, now)));
            }
        }
    }

    private static readonly Vector3 UfoHatch = Avatar.UfoMeshBuilder.Hatch;

    /// <summary>
    /// This peer shows (and is held by) the sightings: always on the server, which moves the world's
    /// things; on a client only with the occasion's Decorations facet (a player who turned it off sees
    /// no saucer, and no invisible beam lifts them).
    /// </summary>
    private bool SeesSightings => _dedicated
        || OccasionManager.Instance?.IsActive(OccasionIds.Ufo, OccasionFacets.Decorations) == true
        || _forced.Count > 0 || _staged;

    // ---- queries (bots, the occasion's sound) --------------------------------------------------

    /// <summary>The shown craft nearest <paramref name="at"/> (world) within <paramref name="within"/> m: where it is, its beam, its height over its ground.</summary>
    public (Vector3 At, bool Beam, float Height, int Sighting)? Nearest(Vector3 at, float within)
    {
        (Vector3, bool, float, int)? best = null;
        float bestD = within * within;
        double now = Now;
        foreach (var s in _plans.Values)
        {
            if (now < s.Start || now > s.End) continue;
            for (int c = 0; c < s.Count; c++)
            {
                if (Hidden(s, c, now)) continue;
                var pose = SightingPath.Pose(s, c, now);
                if (!pose.Shown) continue;
                var p = WorldOf(s, pose);
                float d = p.DistanceSquaredTo(at);
                if (d >= bestD) continue;
                bestD = d;
                best = (p, pose.Beam, pose.Offset.Y, s.Id);
            }
        }
        return best;
    }

    /// <summary>The nearest shown saucer's world point within <paramref name="within"/> m, or null (the occasion's wail).</summary>
    public Vector3? NearestCraft(Vector3 at, float within) => Nearest(at, within)?.At;

    /// <summary>A craft not drawn now: a landed saucer that is a parked UFO, or one that was flown away.</summary>
    private bool Hidden(Sighting s, int craft, double now) =>
        s.Kind == SightingKind.Landing && craft == 0
        && (_taken.Contains(s.Id) || SightingPath.Boardable(s, now) && VehicleManager.Instance?.GetNodeOrNull(NameOf(s.Id)) != null);

    // ---- the authority's planning ----------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        double now = Now;
        // past ones forgotten on every peer
        _drop.Clear();
        foreach (var (id, s) in _plans) if (now > s.End + 5) _drop.Add(id);
        foreach (var id in _drop) { _plans.Remove(id); _taken.Remove(id); _names.Remove(id); }

        if (!Authority) return;
        _checkIn -= delta;
        if (_checkIn > 0) return;
        _checkIn = 0.5;
        Landings(now);
        PlanSlot(now);
    }

    /// <summary>Puts a parked UFO where a landed saucer stands, sees whether it was claimed, takes it back at lift-off.</summary>
    private void Landings(double now)
    {
        if (VehicleManager.Instance is not { } vehicles) return;
        foreach (var s in _plans.Values)
        {
            if (s.Kind != SightingKind.Landing || _taken.Contains(s.Id)) continue;
            string name = ParkedName(s.Id);
            bool boardable = SightingPath.Boardable(s, now);
            bool placed = _placed.ContainsKey(s.Id);
            var node = vehicles.GetNodeOrNull<VehicleBody>(name);
            if (boardable && !placed)
            {
                var pose = SightingPath.Pose(s, 0, now);
                var ride = Rideable.Create(RideKind.Ufo)!;
                var state = new VehicleState(RideKind.Ufo, new GlobalPos(s.E, s.N, s.Ground), pose.Yaw, Vector3.Zero,
                    ride.MaxHealth, EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0);
                if (vehicles.Place(state, name, settled: true) != null)
                {
                    _placed[s.Id] = name;
                    GD.Print($"[sightings] {s.Id}: landed saucer parked at LV95 {s.E:F1}/{s.N:F1} as {name}");
                }
            }
            else if (placed && node == null && boardable && vehicles.Filed?.Invoke(name) != true)
            {
                // claimed: somebody flies it now
                _placed.Remove(s.Id);
                GD.Print($"[sightings] {s.Id}: the landed saucer was boarded and flown away");
                MarkTaken(s.Id);
            }
            else if (placed && !boardable)
            {
                // lift-off: the parked one goes, the sighting's saucer flies off in its place
                _placed.Remove(s.Id);
                node?.QueueFree();
            }
        }
    }

    private void MarkTaken(int id)
    {
        _taken.Add(id);
        if (NetLink.Online(this) && Multiplayer.IsServer()) Rpc(MethodName.Taken, id);
    }

    /// <summary>A new slot: rolls each cell a player is in, and plans what came up.</summary>
    private void PlanSlot(double now)
    {
        long slot = SightingPlanner.Slot(now);
        bool forcedDue = _forcedCount < _forced.Count && now > _forcedUntil;
        if (slot == _slot && !forcedDue) return;

        bool runs = OccasionManager.Instance?.WorldRuns(OccasionIds.Ufo, OccasionFacets.Decorations) == true;
        bool night = SightingPlanner.Night(World.WorldClock.CurrentHour);
        if (!forcedDue && !(runs && night)) { _slot = slot; return; }

        var players = new List<FootPlayer>();
        foreach (var p in PlayerSnapshot.Of(GetTree()))
            if (!p.Player.Npc && p.Player.Origin != null) players.Add(p.Player);
        if (players.Count == 0) return;   // try again next check: the slot is not spent
        players.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (forcedDue)
        {
            var who = players[0].Global;
            var (ce, cn) = SightingPlanner.Cell(who.E, who.N);
            if (TryPlan(_forced[_forcedCount], ce, cn, slot, (uint)(now * 1000) ^ 0xF0F0u, who, now) is { } forced)
            {
                _forcedCount++;
                _forcedUntil = forced.End + 2;
                Publish(forced);
            }
            return;   // the ground or the herd may not be in yet: again in half a second
        }

        _slot = slot;
        _cells.Clear();
        foreach (var p in players)
        {
            var at = p.Global;
            var (ce, cn) = SightingPlanner.Cell(at.E, at.N);
            if (_cells.Add((ce, cn)) && SightingPlanner.Roll(ce, cn, slot, out uint seed) is { } kind)
            {
                // an abduction with no herd near, or a landing with no meadow, is a flyover
                var plan = TryPlan(kind, ce, cn, slot, seed, at, now) ?? TryPlan(SightingKind.Flyover, ce, cn, slot, seed, at, now);
                if (plan != null) Publish(plan);
            }
            var (me, mn) = SightingPlanner.Cell(at.E, at.N, SightingPlanner.MothershipCell);
            if (_cells.Add((me + 1_000_000, mn)) && SightingPlanner.RollMothership(me, mn, slot, out uint ms)
                && TryPlan(SightingKind.Mothership, me, mn, slot, ms, at, now) is { } mother)
                Publish(mother);
        }
    }

    /// <summary>A plan of <paramref name="kind"/> near <paramref name="near"/>, or null when it has nowhere to go.</summary>
    private Sighting? TryPlan(SightingKind kind, long ce, long cn, long slot, uint seed, GlobalPos near, double now)
    {
        int id = SightingPlanner.Id(ce, cn, slot, kind) ^ (_forcedCount << 20);
        if (_plans.ContainsKey(id)) return null;
        float heading = SightingPlanner.HeadingOf(seed);
        int count = SightingPlanner.CountOf(kind, seed);
        float length = SightingPath.LengthOf(kind);
        double start = now + 2.0;
        float Rand(int salt) => Farming.HerdPlan.Unit(seed, (uint)salt, 0xA11);

        switch (kind)
        {
            case SightingKind.Abduction:
            {
                if (Farming.Herds.Instance?.CowNear(near.E, near.N, 600f, start + SightingPath.AbductBeamOn) is not { } cow) return null;
                return new Sighting(id, kind, start, length, cow.E, cow.N, cow.Ground, heading, count, seed);
            }
            case SightingKind.Landing:
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    float a = Rand(attempt * 2) * Mathf.Tau, d = 70f + Rand(attempt * 2 + 1) * 180f;
                    double e = near.E + Mathf.Sin(a) * d, n = near.N + Mathf.Cos(a) * d;
                    if (MeadowGround(e, n) is { } g) return new Sighting(id, kind, start, length, e, n, g, heading, 1, seed);
                }
                return null;
            }
            case SightingKind.Mothership:
            {
                float a = Rand(40) * Mathf.Tau, d = 900f + Rand(41) * 900f;
                double e = near.E + Mathf.Sin(a) * d, n = near.N + Mathf.Cos(a) * d;
                float g = Ground(e, n) is var h && !float.IsNaN(h) ? h : (float)near.Alt;
                return new Sighting(id, kind, start, length, e, n, g, heading, 1, seed);
            }
            default:
            {
                // across the sky a few hundred metres from the player
                float a = Rand(50) * Mathf.Tau, d = 100f + Rand(51) * 300f;
                double e = near.E + Mathf.Sin(a) * d, n = near.N + Mathf.Cos(a) * d;
                float g = Ground(e, n) is var h && !float.IsNaN(h) ? h : (float)near.Alt;
                return new Sighting(id, SightingKind.Flyover, start, SightingPath.FlyoverLength, e, n, g, heading,
                    kind == SightingKind.Flyover ? count : SightingPlanner.CountOf(SightingKind.Flyover, seed), seed);
            }
        }
    }

    /// <summary>The terrain's height at an LV95 point on this peer, NaN where it is not loaded.</summary>
    private float Ground(double e, double n)
    {
        if (_chunks == null) return float.NaN;
        return _chunks.TryGetHeight(_origin.ToWorld(e, n, 0), out float h) ? h : float.NaN;
    }

    /// <summary>
    /// The ground at a point a saucer may land on, or null: a meadow, pasture or fallow field (or,
    /// where the farm has no data, open land), level within 0.8 m over its 6 m legs, clear of
    /// vehicles.
    /// </summary>
    private float? MeadowGround(double e, double n)
    {
        float g = Ground(e, n);
        if (float.IsNaN(g)) return null;
        if (Farming.FarmField.Instance is { } farm)
        {
            if (farm.CellAtLv95(e, n) is { } cell)
            {
                if (cell.FieldCrop is not (CropKind.Meadow or CropKind.Pasture or CropKind.Fallow)) return null;
            }
            else if (farm.HasFields(TileId.FromLv95(e, n))) return null;   // off the fields where there are fields: a road, a yard
        }
        if (_chunks != null && _chunks.TryGetCover(_origin.ToWorld(e, n, g), out var cover) && cover != CoverClass.Open) return null;
        float lo = g, hi = g;
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.Tau / 6f;
            float h = Ground(e + Mathf.Sin(a) * 6f, n + Mathf.Cos(a) * 6f);
            if (float.IsNaN(h)) return null;
            lo = Mathf.Min(lo, h);
            hi = Mathf.Max(hi, h);
        }
        if (hi - lo > 0.8f) return null;
        if (VehicleManager.Instance?.Nearest(_origin.ToWorld(e, n, g), 12f) != null) return null;
        return lo;
    }

    /// <summary>Authority: a new plan, kept and sent to everyone.</summary>
    private void Publish(Sighting s)
    {
        _plans[s.Id] = s;
        GD.Print($"[sightings] planned {s.Kind} {s.Id}: {s.Count} craft at LV95 {s.E:F1}/{s.N:F1} ground {s.Ground:F1}, "
            + $"from sim {s.Start:F1} for {s.Length:F0} s, heading {Mathf.RadToDeg(s.Heading):F0}");
        if (NetLink.Online(this) && Multiplayer.IsServer())
            Rpc(MethodName.Plan, s.Id, (int)s.Kind, s.Start, s.Length, s.E, s.N, s.Ground, s.Heading, s.Count, (long)s.Seed);
    }

    // ---- the director (#866) and probes ----------------------------------------------------------

    private bool _staged;

    /// <summary>
    /// The trailer director's sighting: <paramref name="kind"/> anchored at an LV95 point and its
    /// ground, starting <paramref name="startIn"/> s from now on the shared clock, coming in on
    /// <paramref name="heading"/> (compass rad). On this peer only (the films run offline); its
    /// beams and its landing behave as a real one's. Returns its id.
    /// </summary>
    public int Stage(SightingKind kind, double e, double n, float ground, float heading = 0f, int count = 1,
        double startIn = 0, uint seed = 1)
    {
        _staged = true;
        int id = 0x40000000 | (int)(Farming.HerdPlan.Hash((uint)_plans.Count, seed, 0x57A6) & 0x0FFFFFFF);
        var s = new Sighting(id, kind, Now + startIn, SightingPath.LengthOf(kind), e, n, ground, heading,
            Math.Max(1, count), seed);
        _plans[id] = s;
        GD.Print($"[sightings] staged {kind} {id} at LV95 {e:F1}/{n:F1}");
        return id;
    }

    /// <summary>Takes a staged sighting away.</summary>
    public void Unstage(int id) => _plans.Remove(id);

    // ---- the wire --------------------------------------------------------------------------------

    /// <summary>Server: a joining peer gets every running plan and what was taken.</summary>
    public void SendTo(long peer)
    {
        if (!NetLink.Online(this) || !Multiplayer.IsServer()) return;
        foreach (var s in _plans.Values)
            RpcId(peer, MethodName.Plan, s.Id, (int)s.Kind, s.Start, s.Length, s.E, s.N, s.Ground, s.Heading, s.Count, (long)s.Seed);
        foreach (int id in _taken) RpcId(peer, MethodName.Taken, id);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Plan(int id, int kind, double start, float length, double e, double n, float ground, float heading, int count, long seed)
    {
        if (Authority) return;
        var s = new Sighting(id, (SightingKind)kind, start, length, e, n, ground, heading, Math.Clamp(count, 1, 8), (uint)seed);
        _plans[id] = s;
        GD.Print($"[sightings] server planned {s.Kind} {id} at LV95 {e:F1}/{n:F1}, in {start - Now:F1} s");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Taken(int id)
    {
        if (Authority) return;
        _taken.Add(id);
        GD.Print($"[sightings] server: the saucer of {id} was flown away");
    }
}
