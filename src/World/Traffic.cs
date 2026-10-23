using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// A path through a <see cref="LaneGraph"/>, grown at the front as the vehicle reaches each
/// junction and trimmed at the back once nothing is on the old legs. A car is one body on it; a
/// train is a locomotive and carriages at fixed distances behind the head, which is what keeps a
/// train on one line through a set of points instead of each carriage choosing its own.
/// </summary>
public sealed class Route
{
    public readonly List<(LaneEdge Edge, bool Forward)> Legs = new();
    public int Leg;
    public float Arc;   // along the current leg, in the direction of travel

    public Route(LaneEdge edge, bool forward, float arc)
    {
        Legs.Add((edge, forward));
        Arc = arc;
    }

    /// <summary>Position and direction <paramref name="back"/> m behind the head (negative: ahead, over the legs already chosen).</summary>
    public (Vector3 Pos, Vector3 Dir) At(float back)
    {
        var (p, d, _) = AtLeg(back);
        return (p, d);
    }

    /// <summary><see cref="At"/>, with the leg the point lies on.</summary>
    public (Vector3 Pos, Vector3 Dir, int Leg) AtLeg(float back)
    {
        int i = Leg;
        float s = Arc - back;
        while (s < 0 && i > 0) { i--; s += Legs[i].Edge.Length; }
        while (s > Legs[i].Edge.Length && i < Legs.Count - 1) { s -= Legs[i].Edge.Length; i++; }
        s = Mathf.Max(0f, s);
        var (edge, fwd) = Legs[i];
        var (p, t) = edge.Sample(fwd ? s : edge.Length - s);
        return (p, fwd ? t : -t, i);
    }

    /// <summary>Moves the head on, choosing the next leg with <paramref name="next"/> at each junction.</summary>
    public bool Advance(float distance, Func<(LaneEdge Edge, bool Forward), (LaneEdge, bool)?> next)
    {
        Arc += distance;
        while (Arc > Legs[Leg].Edge.Length)
        {
            Arc -= Legs[Leg].Edge.Length;
            if (Leg + 1 >= Legs.Count)
            {
                var chosen = next(Legs[Leg]);
                if (chosen == null) { Arc = Legs[Leg].Edge.Length; return false; }
                Legs.Add(chosen.Value);
            }
            Leg++;
        }
        return true;
    }

    /// <summary>
    /// Chooses the legs ahead now, so that <paramref name="ahead"/> m of the road to come is known:
    /// a driver looks down the road and knows where it turns off (and so does a racer reading it).
    /// </summary>
    public void Plan(float ahead, Func<(LaneEdge Edge, bool Forward), (LaneEdge, bool)?> next)
    {
        int i = Leg;
        float left = Legs[i].Edge.Length - Arc;
        while (left < ahead)
        {
            if (i + 1 >= Legs.Count)
            {
                if (next(Legs[i]) is not { } chosen) return;
                Legs.Add(chosen);
            }
            i++;
            left += Legs[i].Edge.Length;
        }
    }

    /// <summary>
    /// The first junction (3+ road ends meeting) within <paramref name="within"/> m ahead on the legs chosen: where, how
    /// far, and whether this car gives way there (#159): a road meeting a more important one (a lower
    /// <see cref="RoadClass"/>). ponytail: class only; stored one-way and yield arms (#117 / #121) when the road overhaul lands.
    /// </summary>
    public (Vector3 At, float Distance, bool Yields)? NextJunction(LaneGraph g, float within)
    {
        float d = Legs[Leg].Edge.Length - Arc;
        for (int i = Leg; i < Legs.Count; i++)
        {
            if (i > Leg) d += Legs[i].Edge.Length;
            if (d > within) break;
            var (edge, fwd) = Legs[i];
            long key = fwd ? edge.KeyEnd : edge.KeyStart;
            if (g.Degree(key) >= 3)
                return (fwd ? edge.Points[^1] : edge.Points[0], d, g.GivesWay(key, edge));
        }
        return null;
    }

    /// <summary>Drops legs the tail (<paramref name="length"/> behind the head) has fully left.</summary>
    public void Trim(float length)
    {
        float behind = Arc;
        int keep = Leg;
        while (keep > 0 && behind < length) { keep--; behind += Legs[keep].Edge.Length; }
        if (keep > 0) { Legs.RemoveRange(0, keep); Leg -= keep; }
    }

    public LaneEdge Edge => Legs[Leg].Edge;
    public bool Forward => Legs[Leg].Forward;
}

/// <summary>
/// Cars on the real roads and trains on the real railway, around whoever is playing.
///
/// <para>
/// Cosmetic and local: each client runs its own, spawned in a band around its own player and
/// cleared once far behind, so no bandwidth goes to it and nobody has to agree on where a car
/// is. It is still solid — every vehicle carries a kinematic collision box, so a cyclist who
/// rides into one crashes and a train is not something to stand in front of.
/// </para>
///
/// <para>
/// Everything runs from <see cref="LaneGraph"/>s rebuilt off the thread pool whenever the
/// player moves into a new kilometre tile: the roads within 2 km for cars, 3 km for trains.
/// Density follows the clock — about half the cars at night (<see cref="DayNight"/>).
/// </para>
/// </summary>
public partial class Traffic : Node3D, Core.IOriginContainer, Core.IOriginShiftAware
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    /// <summary>Where the player is: traffic lives around this point.</summary>
    public Func<Vector3?>? Focus { get; set; }

    /// <summary>
    /// Bodies the cars must mind — players, racers, race NPCs — where each is and how it moves: a
    /// car makes way for one closing from behind or coming at it on a narrow road, and waits at a
    /// junction for one about to pass it.
    /// </summary>
    public Func<IEnumerable<(Vector3 Pos, Vector3 Vel)>>? Obstacles { get; set; }
    private readonly List<(Vector3 Pos, Vector3 Vel)> _obstacles = new();

    /// <summary>
    /// The traffic of this client, for the race pilots that drive among it (one world per process).
    /// Local and cosmetic like the traffic itself: a pilot reads it only on the peer simulating it.
    /// </summary>
    public static Traffic? Current { get; private set; }
    public override void _EnterTree()
    {
        Current = this;
        Movie.ReplayWatch.Container(this);   // recorded for the movie studio (#775)
    }
    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        Core.RunSeeds.Reseeded -= OnReseeded;
    }

    /// <summary>
    /// A car as a race pilot sees it: where it is, how it moves, where its lane takes it over the next
    /// <see cref="PathSteps"/> × 0.5 s at its speed (up to the end of the roads it has chosen), and
    /// whether it is waiting for someone (then its path is not where it is going).
    /// </summary>
    public readonly record struct CarView(Vector3 Pos, Vector3 Vel, Vector3[] Path, bool Holding);
    public const int PathSteps = 12;

    public IEnumerable<CarView> CarsNear(Vector3 at, float range)
    {
        foreach (var c in _cars.Concat(_trains))   // a train at a level crossing too
            if (new Vector2(c.Head.X - at.X, c.Head.Z - at.Z).LengthSquared() < range * range)
                yield return new CarView(c.Head, c.Vel, c.Path, c.Holding);
    }

    /// <summary>Whether the traffic car with this body is making way for someone (pulled over, slowing): a racer may pass it anywhere it fits.</summary>
    public bool Yielding(ulong body) => _byBody.TryGetValue(body, out var v) && v.Yield;
    private readonly Dictionary<ulong, Vehicle> _byBody = new();

    /// <summary>A traffic car's state in a few words (for crash logs).</summary>
    public string? Describe(ulong body) => _byBody.TryGetValue(body, out var v)
        ? $"{v.Speed * 3.6f:F0} km/h, {v.Route.Edge.Class} {v.Route.Edge.Width:F1} m, pull {v.Pull:F1}, hold {v.Holding}, yield {v.Yield}, startle {v.Startle > 0f}, alert {v.Alert:F1}, arc {v.Route.Arc:F0}/{v.Route.Edge.Length:F0}, end degree {_roads?.Degree(v.Route.Forward ? v.Route.Edge.KeyEnd : v.Route.Edge.KeyStart)}, age {(Time.GetTicksMsec() - v.Born) / 1000f:F0} s"
        : null;

    private LaneGraph? _roads, _rails;
    private TileId? _builtAround;
    private bool _building;
    private Random _rng = new(20260929);
    private readonly List<Vehicle> _cars = new(), _trains = new();
    private double _spawnTimer;
    private Material? _bodyMaterial, _lampMaterial;

    private const float CarNear = 70f, CarSpawnMin = 180f, CarSpawnMax = 750f, CarDespawn = 950f;
    private const float TrainSpawnMin = 700f, TrainSpawnMax = 2500f, TrainDespawn = 3200f;
    /// <summary>
    /// A car is drawn to here (#221): past it, 4 m of car is a few pixels, and cars live out to
    /// <see cref="CarDespawn"/>. Trains, 50-120 m long, stay drawn as far as they run: seeing one
    /// cross the valley is the point of them.
    /// </summary>
    private const float CarDrawn = 600f;

    public Traffic(ChunkManager chunks, WorldOrigin origin)
    {
        Name = "Traffic";
        _chunks = chunks;
        _origin = origin;
    }

    public override void _Ready()
    {
        Core.RunSeeds.Reseeded += OnReseeded;
        _bodyMaterial = HumanMeshBuilder.Material();
        _lampMaterial = TrafficMeshBuilder.LampMaterial();
    }

    /// <summary>
    /// The origin moved (#185). The car and train nodes are children and have moved; their lanes,
    /// where they are on them and where they are heading are kept here, in world space.
    /// </summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        var done = new HashSet<LaneEdge>();
        _roads?.Shift(shift, _origin.Frame, done);
        _rails?.Shift(shift, _origin.Frame, done);
        ShiftModes(shift, done);
        foreach (var v in _cars.Concat(_trains))
        {
            // a vehicle may still be on an edge of an older graph
            foreach (var (edge, _) in v.Route.Legs)
                if (done.Add(edge)) edge.Shift(shift);
            v.Head = shift.Point(v.Head);
            v.Vel = shift.Direction(v.Vel);
            for (int i = 0; i < v.Path.Length; i++) v.Path[i] = shift.Point(v.Path[i]);
            v.ShiftTrail(shift);
        }
    }

    // ---- roads ---------------------------------------------------------------------------

    private static bool IsCarRoad(RoadSegment s) =>
        s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp or RoadClass.Major
            or RoadClass.Road or RoadClass.Minor or RoadClass.Lane
        && (s.Flags & RoadFlags.Stairs) == 0;

    private static bool IsRail(RoadSegment s) =>
        s.Class == RoadClass.Railway && (s.Flags & (RoadFlags.Disused | RoadFlags.Tramway)) == 0;

    private int _epoch;

    /// <summary>
    /// Removes every car and train and forgets the lane graphs, for when the roads they were
    /// built from are replaced (the generated fallback retiring). They rebuild on the next frame.
    /// </summary>
    public void Forget()
    {
        _epoch++;
        foreach (var v in _cars) v.Free();
        foreach (var v in _trains) v.Free();
        _byBody.Clear();
        _cars.Clear();
        _generatedLive = 0;
        _trains.Clear();
        ClearMovers();
        _roads = _rails = _walks = _rides = _tramLines = null;
        _modesBuilt = 0;
        _builtAround = null;
        _building = false;
    }

    /// <summary>
    /// A film's shot pinned its seeds (#924): every car and train goes, the lane graphs stay, and the
    /// cars come again from the shot's own stream, so its traffic never depends on the shots filmed
    /// before it or on how long its ground took to stream in (<see cref="Forget"/> alone did neither).
    /// </summary>
    private void OnReseeded()
    {
        foreach (var v in _cars) v.Free();
        foreach (var v in _trains) v.Free();
        _byBody.Clear();
        _cars.Clear();
        _trains.Clear();
        ClearMovers();
        _rng = Core.RunSeeds.Rng("traffic");
        _spawnTimer = 0;
        _refill = true;
    }

    /// <summary>After a reseed: the next tick fills the roads at once, as a shot starts (not four cars a second).</summary>
    private bool _refill;

    private void RebuildIfMoved(Vector3 focus)
    {
        if (_building || _chunks.Source is not { } source) return;
        int epoch = _epoch;
        var (e, n) = _origin.ToLv95(focus);
        var here = TileId.FromLv95(e, n);
        // (a mode wanted since the last build, its density raised from 0, rebuilds here too: #1060)
        uint wanted = ModesWanted();
        if (_builtAround is { } b && b.E == here.E && b.N == here.N && (wanted & ~_modesBuilt) == 0) return;
        _building = true;
        // one frame for the whole build: the origin may move while it runs (#185)
        var origin = _origin.Frame;

        Task.Run(async () =>
        {
            var tiles = new List<RoadTile>();
            for (int de = -3; de <= 3; de++)
                for (int dn = -3; dn <= 3; dn++)
                {
                    try
                    {
                        if (await source.LoadRoadsAsync(new TileId(here.E + de, here.N + dn)) is { } t) tiles.Add(t);
                    }
                    catch (Exception) { /* no roads there */ }
                }
            var near = tiles.Where(t => Math.Abs(t.Id.E - here.E) <= 2 && Math.Abs(t.Id.N - here.N) <= 2).ToList();
            var roads = LaneGraph.Build(near, origin, IsCarRoad);
            int deadBefore = roads.DeadEnds;
            roads.JoinTrimmedEnds();
            var rails = LaneGraph.Build(tiles, origin, IsRail, MoverKind.Train);
            // the modes' own graphs (#1060), each from its builder in its own file: none unless its density asks for movers
            var (walks, rides, trams) = wanted == 0 ? default : BuildModeGraphs(new ModeTiles(tiles, near, origin, roads, rails), wanted);
            if (walks is not null || rides is not null || trams is not null)
                GD.Print($"[traffic] around {here}: {walks?.Edges.Count ?? 0} walk edges, {rides?.Edges.Count ?? 0} ride edges, {trams?.Edges.Count ?? 0} tram edges");
            int divided = roads.Edges.Count(x => (x.Flags & RoadFlags.Divided) != 0);
            int oriented = roads.Edges.Count(x => (x.Flags & RoadFlags.Divided) != 0 && x.OneWay != 0);
            int yielding = roads.Edges.Count(x => x.Yield != 0);
            // origin-free fingerprint of every one-way edge's direction, so two peers can be compared
            long directions = roads.Edges.Where(x => x.OneWay != 0).Sum(x => (long)Mathf.RoundToInt(x.Length * 10) * 3 + x.OneWay);
            GD.Print($"[traffic] around {here}: {roads.Edges.Count} road edges ({oriented}/{divided} divided "
                + $"carriageways oriented, {yielding} giving way at an end), {rails.Edges.Count} rail edges, dead ends {deadBefore} -> {roads.DeadEnds} joined across junctions, one-way fingerprint {directions}");
            Callable.From(() =>
            {
                if (epoch != _epoch) return;   // built from the world that was replaced
                if (origin.Epoch != _origin.Epoch)
                {
                    // the origin moved while it was being built: bring it into the current frame
                    var shift = _origin.Since(origin);
                    roads.Shift(shift, _origin.Frame, new());
                    rails.Shift(shift, _origin.Frame, new());
                    // (a mode's graph may hold the roads' or rails' own edges: each moves once)
                    var moved = new HashSet<LaneEdge>(roads.Edges.Concat(rails.Edges));
                    walks?.Shift(shift, _origin.Frame, moved);
                    rides?.Shift(shift, _origin.Frame, moved);
                    trams?.Shift(shift, _origin.Frame, moved);
                }
                _roads = roads;
                _rails = rails;
                (_walks, _rides, _tramLines, _modesBuilt) = (walks, rides, trams, wanted);
                _builtAround = here;
                _building = false;
            }).CallDeferred();
        });
    }

    // ---- the loop ------------------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (Focus?.Invoke() is not { } focus) return;
        RebuildIfMoved(focus);
        if (_roads == null || _rails == null) return;

        var s = GameSettings.Current;
        float night = DayNight.Instance?.Night ?? 0f;
        int wantCars = Mathf.RoundToInt(s.TrafficCars * Mathf.Lerp(1f, 0.45f, night));
        int wantTrains = s.Trains ? 3 : 0;

        if (_refill)
        {
            _refill = false;
            for (int i = 0; i < wantCars && _cars.Count < wantCars; i++) SpawnCar(focus, CarNear, Obstacles?.Invoke());
        }
        _spawnTimer -= dt;
        if (_spawnTimer <= 0)
        {
            _spawnTimer = 0.25;
            // first fill anywhere out of arm's reach; after that only out of sight range
            bool filling = _cars.Count < wantCars / 2;
            if (!GeneratorsOnly && _cars.Count < wantCars) SpawnCar(focus, filling ? CarNear : CarSpawnMin, Obstacles?.Invoke());
            if (_trains.Count < wantTrains) SpawnTrain(focus);
        }

        StepEndpoints(dt, focus);

        // one list for the traffic's life, refilled each tick (#221)
        var obstacles = _obstacles;
        obstacles.Clear();
        if (Obstacles?.Invoke() is { } seen) obstacles.AddRange(seen);
        long tick = System.Diagnostics.Stopwatch.GetTimestamp();
        _byX.Clear();
        _byX.AddRange(_cars);
        _byX.Sort(ByX);
        // the walkers, riders and trams where they are now, for the cars to mind (#1060; none without the modes)
        CollectCrossers();
        DoubledNow = 0;
        _now += dt;
        foreach (var car in _cars) StepCar(car, dt, obstacles);
        DoubledMost = Math.Max(DoubledMost, DoubledNow);
        foreach (var train in _trains) StepTrain(train, dt);
        _tickCars += _cars.Count;
        _tickUs[_ticks++ % _tickUs.Length] = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - tick) * 1e6 / System.Diagnostics.Stopwatch.Frequency);

        Cull(_cars, focus, CarDespawn, GeneratorsOnly ? int.MaxValue : wantCars);
        // the modes beyond cars and trains (#1060): walkers, riders, trams, each on its own graph
        // (the test world's walkers and riders come from its endpoints alone, #1134: no graph to spawn on, none culled for number)
        if (_walks is not null || _walkers.Count > 0)
            StepMovers(MoverKind.Walker, GeneratorsOnly ? null : _walks, focus, dt, GeneratorsOnly ? int.MaxValue : Want(MoverKind.Walker, night));
        if (_rides is not null || _riders.Count > 0)
            StepMovers(MoverKind.Rider, GeneratorsOnly ? null : _rides, focus, dt, GeneratorsOnly ? int.MaxValue : Want(MoverKind.Rider, night));
        if (_tramLines is not null || _trams.Count > 0) StepTrams(focus, dt, Want(MoverKind.Tram, night));
        StepDebugDraw(delta);
        Cull(_trains, focus, TrainDespawn, wantTrains);
    }

    /// <summary>The cost of the last ticks' car and train steps, µs (#353, <c>perf-traffic-tick</c>): a ring, read by <c>--trafficcheck</c>.</summary>
    private readonly float[] _tickUs = new float[8192];
    private int _ticks;
    private long _tickCars;

    /// <summary>Forgets the ticks measured so far (the probe starts measuring once the cars are spawned).</summary>
    public void ResetTickCost() { _ticks = 0; _tickCars = 0; }

    /// <summary>Mean, median and 99th percentile of the measured ticks, µs, how many, and the mean number of cars over them.</summary>
    public (float Mean, float P50, float P99, int Ticks, float Cars) TickCost()
    {
        int n = Math.Min(_ticks, _tickUs.Length);
        if (n == 0) return (0f, 0f, 0f, 0, 0f);
        var sorted = _tickUs[..n];
        Array.Sort(sorted);
        return (sorted.Average(), sorted[n / 2], sorted[Math.Min(n - 1, (int)(n * 0.99f))], n, _tickCars / (float)Math.Max(1, _ticks));
    }

    private void Cull(List<Vehicle> list, Vector3 focus, float range, int want)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var v = list[i];
            float d = new Vector2(v.Head.X - focus.X, v.Head.Z - focus.Z).Length();
            if (d > range || v.Stuck > 20f || (list.Count > want && d > range * 0.6f))
            {
                if (v.Stuck > 20f)
                {
                    int kind = list == _trams ? (int)MoverKind.Tram : list == _trains ? (int)MoverKind.Train : (int)MoverKind.Car;
                    if (StuckDropped[kind]++ < 12 && v.FromEndpoint)
                    {
                        var (se, sn) = _origin.ToLv95(v.Head);
                        GD.Print($"[traffic] dropped a stuck {(MoverKind)kind} from endpoint #{v.FromId} at {se:F0},{sn:F0} ({v.Route.Edge.Class}, to #{v.ToId})");
                    }
                }
                foreach (var u in v.Units) _byBody.Remove(u.GetInstanceId());
                if (v.FromEndpoint) _generatedLive--;
                v.Free();
                list.RemoveAt(i);
            }
        }
    }

    // ---- cars ----------------------------------------------------------------------------

    private static float CruiseSpeed(RoadClass c) => c switch
    {
        RoadClass.Motorway => 33f, RoadClass.Expressway => 27f, RoadClass.Ramp => 17f,
        RoadClass.Major => 21f, RoadClass.Road => 16f, RoadClass.Minor => 12f, _ => 8f,
    };

    /// <summary>Busier roads get more of the cars, as they do.</summary>
    // (never on a junction path: a car out of thin air inside a junction cuts across those with a green light, #854)
    private static float CarWeight(LaneEdge e) => e.Connector ? 0f : e.Class switch
    {
        RoadClass.Motorway or RoadClass.Expressway => 1f, RoadClass.Major => 0.9f,
        RoadClass.Road => 0.7f, RoadClass.Ramp => 0.5f, RoadClass.Minor => 0.35f, _ => 0.12f,
    };

    /// <summary>
    /// <c>--trafficcheck --at</c> (#353): one more car within <paramref name="within"/> m of a point, while there are
    /// fewer than the settings ask, so a junction there sees traffic before the 40 s are up.
    /// </summary>
    /// <summary>The probe's extra cars appear at least this far from its point (m): out of its camera's view (#854).</summary>
    private const float ProbeSpawnMin = 120f;

    /// <summary>A car appears no nearer another than this (m): the 10 m it was put one in front of a car at 50 km/h, too close to stop (#854).</summary>
    private const float SpawnClear = 30f;

    public void SpawnNear(Vector3 at, float within)
    {
        if (_roads is not null && _cars.Count < GameSettings.Current.TrafficCars) SpawnCar(at, ProbeSpawnMin, Obstacles?.Invoke(), within);
    }

    private void SpawnCar(Vector3 focus, float minDist, IEnumerable<(Vector3 Pos, Vector3 Vel)>? obstacles, float maxDist = CarSpawnMax)
    {
        if (_roads!.RandomSpot(_rng, focus, minDist, maxDist, CarWeight) is not var (edge, arc)) return;
        // never out of thin air beside a player: a car filled in 5 m in front of a racer is a crash. 60 m
        // and the 4 s a fast one covers: at 150 km/h 60 m is 1.4 s, less than a racer needs to see it and stop (#159)
        var (spot, _) = edge.Sample(arc);
        if (obstacles != null && obstacles.Any(o => new Vector2(o.Pos.X - spot.X, o.Pos.Z - spot.Z).Length() < 60f + 4f * new Vector2(o.Vel.X, o.Vel.Z).Length())) return;
        // nor on top of another car, or so close in front of one that it cannot stop: two filled in on one spot drove as one (#854)
        foreach (var c in _cars)
            if (new Vector2(c.Head.X - spot.X, c.Head.Z - spot.Z).LengthSquared() < SpawnClear * SpawnClear) return;
        bool forward = edge.OneWay switch { 1 => true, -1 => false, _ => _rng.Next(2) == 0 };
        AddCar(new Route(edge, forward, forward ? arc : edge.Length - arc));
    }

    private void AddCar(Route route)
    {
        bool van = _rng.NextDouble() < 0.18;
        var (body, lamps) = TrafficMeshBuilder.Car(TrafficMeshBuilder.Paints[_rng.Next(TrafficMeshBuilder.Paints.Length)], van);
        var v = new Vehicle(route, CruiseSpeed(route.Edge.Class) * 0.8f, new[] { 0f },
            new[] { Unit(body, lamps, CarDrawn) }) { Reaction = 0.5f + 0.5f * (float)_rng.NextDouble(), Habit = (uint)_rng.Next() };
        v.Trail = new Vector3[24];
        AddVehicle(v);
        _cars.Add(v);
    }

    /// <summary>
    /// <c>--trafficcheck --feed</c> (#353, the queue capture only): one more car on the road into
    /// <paramref name="approach"/>, <paramref name="from"/> to <paramref name="to"/> m before its
    /// line (back over the junctions upstream where its edge is shorter), its route already through
    /// the approach; none while there are as many cars as the settings ask, or a car within <see cref="SpawnClear"/> m of the spot.
    /// With <paramref name="move"/>, the car takes a path with that move at the approach's junction (#854, <c>--feed-turns</c>).
    /// </summary>
    public bool SpawnInto(LaneApproach approach, float from, float to, SignalMoves move = SignalMoves.None)
    {
        if (_roads is null || _cars.Count >= GameSettings.Current.TrafficCars) { FeedFails[0]++; return false; }
        foreach (var e in _roads.Edges)
        {
            bool atEnd = e.ApproachAtEnd?.Approach == approach;
            if (!atEnd && e.ApproachAtStart?.Approach != approach) continue;
            // a few spots tried: the first is often near a car already (#854: 189 of 225 feeds at J10 found none)
            for (int attempt = 0; attempt < FeedTries; attempt++)
            {
            float before = from + (float)_rng.NextDouble() * (to - from);
            // the legs back from the approach's edge, in the order they are driven; the line is
            // `behind` m from the start of the first
            var legs = new List<(LaneEdge Edge, bool Forward)> { (e, atEnd) };
            float behind = e.Length - (atEnd ? e.ApproachAtEnd : e.ApproachAtStart)!.Value.StopBack;
            while (behind < before && legs.Count < 8)
            {
                var (first, forward) = legs[0];
                var options = _roads.Entering(forward ? first.KeyStart : first.KeyEnd).Where(o => o.Edge != first).ToList();
                if (options.Count == 0) break;
                var pick = options[_rng.Next(options.Count)];
                legs.Insert(0, pick);
                behind += pick.Edge.Length;
            }
            if (behind < from) { FeedFails[1]++; continue; }
            var route = new Route(legs[0].Edge, legs[0].Forward, Mathf.Max(0f, behind - before));
            for (int i = 1; i < legs.Count; i++) route.Legs.Add(legs[i]);
            var (spot, way) = route.At(0f);
            // room for it on its way (#854: the 30 m round any car, oncoming and leaving ones too, let few in): none within a car
            // of the spot, none coming up behind it within 30 m, and from one ahead going its way it starts no faster than it can
            // still brake from
            float speed = CruiseSpeed(route.Edge.Class) * 0.8f;
            bool room = true;
            foreach (var c in _cars)
            {
                var d = c.Head - spot;
                if (new Vector2(d.X, d.Z).LengthSquared() < 8f * 8f) { room = false; break; }
                if (c.Route.At(0).Dir.Dot(way) < 0.5f) continue;
                float along = d.Dot(way);
                if (Mathf.Abs(along) > 60f || (d - way * along).Length() > 6f) continue;
                if (along < 0f && along > -SpawnClear) { room = false; break; }
                if (along > 0f) speed = Mathf.Min(speed, Mathf.Sqrt(c.Speed * c.Speed + 2f * 4f * Mathf.Max(0f, along - 9f)));
            }
            if (!room) { FeedFails[2]++; continue; }
            AddCar(route);
            _cars[^1].Speed = speed;
            _cars[^1].Wants = (approach, move);
            return true;
            }
            return false;
        }
        return false;
    }

    /// <summary>Spots <see cref="SpawnInto"/> tries before it gives up.</summary>
    private const int FeedTries = 6;

    /// <summary><c>--trafficcheck</c> (#854): the cars within <paramref name="radius"/> m of a point.</summary>
    public int CarsWithin(Vector3 at, float radius) => _cars.Count(c => MathX.Flat(c.Head - at).Length() < radius);

    /// <summary>Braking a traffic driver plans with, m/s²: the speed from which it stops within <paramref name="gap"/> m.</summary>
    private static float StopWithin(float gap) => Mathf.Sqrt(2f * 4f * Mathf.Max(0f, gap));

    /// <summary>
    /// The cars by east-west position, for the gap to the car ahead: a car looks only at those
    /// within <see cref="GapWindow"/> m of it that way instead of at every car (O(n²) at traffic
    /// 150, #221). The window is the <see cref="FollowLook"/> m it looks ahead with room for a tick's travel.
    /// </summary>
    private readonly List<Vehicle> _byX = new();
    private static readonly Comparer<Vehicle> ByX = Comparer<Vehicle>.Create((a, b) => a.Head.X.CompareTo(b.Head.X));
    private const float GapWindow = FollowLook + 5f;

    /// <summary>How far ahead a car looks for the one it follows (m): 40 was under the 37 m it takes to stop from 75 km/h (#854).</summary>
    private const float FollowLook = 70f;

    /// <summary>How far apart cars stand in a queue, middle to middle (m).</summary>
    private const float QueueSpacing = 7.5f;

    /// <summary>A car further than this sideways from the line ahead is not in the way (m): a body's width and a little.</summary>
    private const float AheadWidth = 2.0f;

    /// <summary>How far a car's front stands before its middle (m): half a van's 5 m.</summary>
    private const float CarFront = 2.5f;

    /// <summary>Two cars' lines further apart than this are in two lanes (m): a body's 1.9 m and a little (#1172, the box rule's lanes).</summary>
    private const float LaneApart = 2.0f;

    /// <summary>Two cars whose middles are closer than this along the way are side by side (m), not one behind the other (#1172).</summary>
    private const float SideBySide = 2.0f;
    private readonly Vehicle _xKey = new(0f);

    /// <summary>This car's road from 80 m behind to 120 m ahead, every 4 m (<see cref="Behind"/> is the index of the car).</summary>
    private readonly Vector3[] _roadPos = new Vector3[51], _roadDir = new Vector3[51];
    private const int Behind = 20;

    /// <summary>
    /// One car, one step: cruise for the road, slow for bends and for the car ahead, and around a
    /// race behave like a driver who has seen it coming (#85) —
    /// <list type="bullet">
    /// <item>something fast closing from behind: pull over to the right edge and slow down, so it can
    /// pass (marked <see cref="Vehicle.Yield"/>: a racer may pass it anywhere it fits);</item>
    /// <item>something coming the other way: pull over; on a narrow road crawl, and stop, tucked in,
    /// if it will be in this car's lane when they meet;</item>
    /// <item>at a junction: stay short of it while someone fast is about to pass through it — never
    /// pull out in front of a racer;</item>
    /// <item>something standing in its lane: stop behind it, pulled over; still there after 5 s (a racer is reset after 10),
    /// turn round and leave (one waits, one goes: nobody waits for the other forever).</item>
    /// </list>
    /// </summary>
    private void StepCar(Vehicle car, float dt, List<(Vector3 Pos, Vector3 Vel)> obstacles)
    {
        car.Route.Plan(ApproachLookAhead + 20f, leg => NextRoad(leg, car));
        var (pos, dir) = car.Route.At(0);
        var edge = car.Route.Edge;
        float cruise = CruiseSpeed(edge.Class);
        float target = cruise;

        // slow for the bend ahead: its radius ~ 18 m over the turn, at the sideways acceleration the lights are timed for (#979)
        var (_, aheadDir) = car.Route.At(-18f);
        float turn = Mathf.Acos(Mathf.Clamp(dir.Dot(aheadDir), -1f, 1f));
        if (turn > 0.05f) target = Mathf.Min(target, (float)Terrain.Format.TurnSpeed.Of(18f / turn));

        // keep a gap to the car ahead in its lane, going its way. Measured from the centreline, a car
        // coming the other way in the other lane (1.5 m over) counted as "in the lane": two met nose to
        // nose, stopped for each other for good and blocked the whole road — racers stopped behind
        // both (#85)
        // only the cars within reach east-west (sorted at the start of the tick; none moves a metre in one)
        _xKey.Head.X = car.Head.X - GapWindow;
        int from = _byX.BinarySearch(_xKey, ByX);
        var rightOf = new Vector3(-dir.Z, 0f, dir.X).Normalized();   // right of its way, as Vehicle.Place offsets the lane
        // a car alongside on the side it is easing over to (#854): it holds its line until there is room, or it slid onto it
        float sideTaken = 0f, easing = car.LaneWanted - car.Lane;
        bool queued = false, overlapping = false;
        // the nearest car ahead in its way that stands or crawls: the tail of the queue it joins (#1172: its lane changes are planned to
        // end there, not at the line; a car only moves over while it rolls, so one that reached the tail first kept its lane)
        float tailAhead = float.MaxValue;
        // the nearest car coming the other way on a narrow carriageway the two pass on, moved over (#1180): how far ahead, and how
        // far right of this car it is (its middle's)
        bool squeezes = Narrow(edge) && NarrowPass.Fits(edge.Width);
        float meetAhead = float.MaxValue, meetAcross = 0f;
        for (int o = from < 0 ? ~from : from; o < _byX.Count && _byX[o].Head.X <= car.Head.X + GapWindow; o++)
        {
            var other = _byX[o];
            if (other == car) continue;
            var d = other.Head - car.Head;
            float along = d.Dot(dir);
            // (until its tail is past: its body beside this one's from 6 m ahead to 6 m behind)
            if (squeezes && along > -6f && along < NarrowPass.Look && along < meetAhead && Mathf.Abs(d.Y) < 3f
                && d.Dot(rightOf) is var meetSide && meetSide < 0.5f && meetSide > -(edge.Width + 2f) && other.Route.At(0).Dir.Dot(dir) < -0.7f)
                (meetAhead, meetAcross) = (along, meetSide);
            // (#1134, the user's two cars at a lane split: also one coming up behind in the lane it moves into, close enough to be
            // driven into: it waits for a gap that holds it, from 8 m behind to 6 m ahead of it)
            if (along > -8f && along < 6f && Mathf.Abs(easing) > 0.05f && d.Dot(rightOf) is var beside && Mathf.Abs(beside) < 3.2f
                && Mathf.Sign(beside) == Mathf.Sign(easing) && Mathf.Abs(beside) > 0.4f && Mathf.Abs(d.Y) < 3f && other.Route.At(0).Dir.Dot(dir) > 0.3f)
                sideTaken = Mathf.Sign(easing);
            // closer than a metre: the one behind (on one spot, the one with the lower habit) waits as if the other were just
            // ahead. Skipping it, as it did, a car that got that close drove on through the queue, or the two as one (#854)
            if (along > -1f && along < 1f && car.Habit < other.Habit && (d - dir * along).Length() < 1f) DoubledNow++;
            // (#1134: two standing body on body, the probe's overlap watch)
            if (car.Speed < 0.3f && other.Speed < 0.3f && Mathf.Abs(d.Dot(dir)) < 3.5f && Mathf.Abs(d.Dot(rightOf)) < 1.7f && Mathf.Abs(d.Y) < 3f)
                (overlapping, car.OverlapWith) = (true, other);
            // (#1172) the first time one rolls onto a car ahead of it, body on body: what each was doing then
            if (car.Speed >= 0.3f && d.Dot(dir) > 0f && d.Dot(dir) < 4f && Mathf.Abs(d.Dot(rightOf)) < 1.7f && Mathf.Abs(d.Y) < 3f && other.Route.At(0).Dir.Dot(dir) > 0.3f
                && !car.ContactLogged && ContactsLogged < 40)
            {
                car.ContactLogged = true;
                ContactsLogged++;
                var (ce, cn) = _origin.ToLv95(car.Head);
                GD.Print($"[traffic] contact at {ce:F0},{cn:F0}: behind v {car.Speed:F1} lane {car.Lane:F2}/{car.LaneWanted:F2}/aim {car.LaneAim:F2} step {car.StepTarget:F2} idx {car.LaneIndex} strip {car.Strip} turn {car.Turn} habit {car.Habit % 1000} edge {car.Route.Edge.GetHashCode() % 10000} arc {car.Route.Arc:F1}"
                    + $"; ahead v {other.Speed:F1} lane {other.Lane:F2}/{other.LaneWanted:F2}/aim {other.LaneAim:F2} idx {other.LaneIndex} strip {other.Strip} turn {other.Turn} habit {other.Habit % 1000} edge {other.Route.Edge.GetHashCode() % 10000} arc {other.Route.Arc:F1}"
                    + $"; along {d.Dot(dir):F2} across {d.Dot(rightOf):F2} dirdot {other.Route.At(0).Dir.Dot(dir):F2} t {Core.GameClock.Now:F1}");
            }
            if (along > -1f && along < 1f && (along > 0f || car.Habit < other.Habit)) along = 1f;
            // in the way: within a body's width of its line now, or of the lane it is heading for at the line. One for a turn pocket
            // eases over only in the last metres and met the car standing in it from the side, on top of it (#854). One coming
            // the other way is not in the way, by its direction below
            // (#1172) or of where its lane takes it by the time it gets there: one easing over onto a lane where a car stands (a pocket
            // not open yet rides the through lane, a transition's strip leads onto it) saw it only once within 2 m of its line, 12 m
            // short of it at 40 km/h, and stopped on top of it; or one ahead on a bend or a junction path, measured across its heading now)
            float across = d.Dot(rightOf), toward = car.LaneAim - car.Lane;
            if (along < 1f || along > FollowLook || Mathf.Abs(d.Y) > 3f || other.Route.At(0).Dir.Dot(dir) < 0.3f
                || Mathf.Abs(across) > AheadWidth && Mathf.Abs(across - toward) > AheadWidth
                   && !OnLineAhead(car, other.Head, d.Length())) continue;
            // side by side, each in the other's way (#1134, the user: two cars at a lane split, one moving over into the other's lane,
            // both stopped for good): only one gives way. The one changing lanes yields to the one keeping its lane; both or neither
            // changing, the lower habit yields. The other drives on as if it were not there
            // (side by side only, in two lanes: in one lane the one behind always waits, else the one ahead was driven into, as at J5b;
            // #1172: and truly beside, middle within SideBySide m of middle: one whose middle is further ahead goes first, the one behind
            // waits. Read up to 5 m, a car ignored one 2-4 m ahead of it as it merged in or two junction paths joined, and drove onto it)
            if (d.Dot(dir) < SideBySide && Mathf.Abs(across) > 1.2f && (-d).Dot(other.Route.At(0).Dir) > -1f)
            {
                bool meMoving = Mathf.Abs(car.LaneWanted - car.Lane) > 0.05f, otherMoving = Mathf.Abs(other.LaneWanted - other.Lane) > 0.05f;
                bool iYield = meMoving != otherMoving ? meMoving : car.Habit < other.Habit;
                if (!iYield) continue;
            }
            // standing 7.5 m apart, middle to middle: about 2.5 m between bumpers, as a queue stands (#971: 9 m left 4.5 m)
            target = Mathf.Min(target, Mathf.Max(0f, other.Speed + (along - QueueSpacing) * 0.6f));
            // and no faster than it can still shed before the gap (4 m/s², under the 6 it brakes at): the line above alone
            // let one come at a queue at 50 km/h from 40 m, too late to stop (#854)
            target = Mathf.Min(target, Mathf.Sqrt(other.Speed * other.Speed + 2f * 4f * Mathf.Max(0f, along - QueueSpacing + 1f)));
            if (other.Speed < 2f) tailAhead = Mathf.Min(tailAhead, along);
            // standing right behind one that waits (a red, a gap, a queue of its own): queued, not stuck (#1134: a queue behind a
            // long red reaches past where the cars read the lights, and its back vanished after 20 s)
            // (truly ahead, not one on the same spot: two on one spot each reading the other as ahead would hold each other
            // queued for good, where being dropped as stuck untangles them)
            if (other.Speed < 0.5f && d.Dot(dir) > 2f && along < QueueSpacing + 6f && (other.AtRed || other.RoomHeld || other.NarrowHeld || other.Queued || other.GapWaiting || other.GapHeld)) queued = true;
        }
        // and behind a rider or tram in its lane (#1060: the modes' crossers; +∞ without them): standing behind one, queued too
        // (#1134: a tram at its stop, a rider at a red)
        float behindCrosser = FollowCrossers(car, dir, rightOf);
        car.Queued = queued || behindCrosser < 0.5f && car.Speed < 0.5f;
        car.Overlap = overlapping ? car.Overlap + dt : 0f;
        if (car.Overlap > OverlapFails && OverlapLongest <= OverlapFails)
        {
            var (oe, on) = _origin.ToLv95(car.Head);
            string Who(Vehicle v) => $"lane {v.Lane:F1}/{v.LaneWanted:F1} idx {v.LaneIndex} strip {v.Strip} turn {v.Turn} red {v.AtRed} q {v.Queued} edge {v.Route.Edge.GetHashCode() % 10000} {(v.Route.Forward ? "f" : "b")} arc {v.Route.Arc:F1}{(v.Route.Edge.Path is not null ? " path" : "")}{(v.Route.Edge.Transition is not null ? " trans" : "")} habit {v.Habit % 1000}";
            var w = car.OverlapWith;
            GD.Print($"[traffic] two cars body on body for {OverlapFails:F0} s at {oe:F0},{on:F0}: A {Who(car)}; B {(w is null ? "-" : Who(w))}; d along {(w is null ? 0 : (w.Head - car.Head).Dot(dir)):F2} across {(w is null ? 0 : (w.Head - car.Head).Dot(rightOf)):F2}");
        }
        OverlapLongest = Mathf.Max(OverlapLongest, car.Overlap);
        target = Mathf.Min(target, behindCrosser);

        float half = edge.Width * 0.5f, keep = KeepRight(edge, car.Route.Forward, car.Habit);
        bool twoWay = (edge.Flags & RoadFlags.Divided) == 0 && edge.OneWay == 0;
        // a lane of its own each way is 7.5 m of road; on less, meeting means someone makes room
        bool narrow = twoWay && edge.Width < 7.5f;
        // how far over it can go: its body (0.95 m) on the edge of the tarmac
        float maxPull = twoWay ? Mathf.Max(0f, half - 0.95f - keep) : 0f;
        float mine = keep + car.Pull;   // this car's centre, right of the centreline
        float wantPull = 0f, makeWay = float.MaxValue;
        // meeting one on a narrow carriageway (#1180, the user): a crawl, over to the right edge (wheels on the verge or the kerb by
        // what it takes) until the two bodies pass with room between; back to its line once its tail is past
        if (meetAhead < float.MaxValue)
        {
            float centre = NarrowPass.Centre(edge.Width, keep, -(mine + meetAcross));
            maxPull = Mathf.Max(maxPull, centre - keep);
            wantPull = centre - keep;
            target = Mathf.Min(target, NarrowPass.Approach(edge.Width, centre, meetAhead));
            if (!car.Squeezing) NarrowPasses++;
            car.Squeezing = true;
        }
        else car.Squeezing = false;
        bool yield = false, hold = false, blocked = false, stopFor = false;
        (Vector3 At, float Distance, bool Yields)? junction = null;
        bool junctionLooked = false;
        // what this driver has noticed: someone in sight (no crest, hillside or building between), and then
        // only after its reaction time. Seen late — little time left — it is startled (see below)
        bool threat = false, threatBrake = false, racerRight = false;
        float threatTtc = float.MaxValue;
        car.SawAgo += dt;
        car.LookIn -= dt;
        bool Noticed(Vector3 oPos, float ttc, bool brake)
        {
            if (!Sees(car, oPos)) return false;
            threat = true;
            if (ttc < threatTtc) { threatTtc = ttc; threatBrake = brake; }
            return car.Alert >= car.Reaction;
        }
        target = Mathf.Min(target, GiveWay(car, obstacles));
        // its lane, and the traffic lights (#353)
        float lights = Approach(car);
        FollowTransition(car);
        // a car just spawned starts on the lane it would be in (#818 phase 5: else one spawned close to a junction reaches its path
        // off its lane); after that it eases
        if (!car.Placed) (car.Lane, car.Placed) = (car.LaneWanted, true);
        target = Mathf.Min(target, lights);
        target = Mathf.Min(target, LitLimit(car, edge));
        // a turn that gives way on its green: in to the conflict point, and wait there for a gap (#1046)
        target = Mathf.Min(target, GapWait(car, dt, obstacles));
        // a zebra without lights a walker stands at or crosses: stop short of it (#1033; +∞ without walkers)
        target = Mathf.Min(target, ZebraWait(car, dt));

        bool sampled = false;
        foreach (var (oPos, oVel) in obstacles)
        {
            var rel = oPos - pos;
            // a racer 9 s from a junction at 50 m/s is 450 m away
            if (new Vector2(rel.X, rel.Z).LengthSquared() > 450f * 450f) continue;
            // the road is sampled only for a car with someone that near: the local player alone is
            // an obstacle, and sampling for every car every tick was most of the traffic's cost (#221)
            if (!sampled)
            {
                for (int k = 0; k < _roadPos.Length; k++) (_roadPos[k], _roadDir[k]) = car.Route.At(4f * (Behind - k));
                sampled = true;
            }
            float oSpeed = new Vector2(oVel.X, oVel.Z).Length();

            // where it is on this car's road (bends and all): metres ahead (- behind), and right of the centreline
            int best = 0;
            for (int k = 1; k < _roadPos.Length; k++)
                if (MathX.Flat(oPos - _roadPos[k]).LengthSquared() < MathX.Flat(oPos - _roadPos[best]).LengthSquared()) best = k;
            var t = _roadDir[best];
            var side = new Vector3(-t.Z, 0, t.X).Normalized();
            var r = MathX.Flat(oPos - _roadPos[best]);
            float along = 4f * (best - Behind) + r.Dot(t), lat = r.Dot(side);
            float ov = oVel.X * t.X + oVel.Z * t.Z;   // its speed along this car's way
            // on this road (not past either end of what was sampled) and not on a bridge over it
            bool onRoad = r.Length() < half + 2.5f && Mathf.Abs(oPos.Y - _roadPos[best].Y) < 4f;

            // whatever the road says, never drive into a body: anything in front of its own nose, within
            // a car's width of where it actually is (a kinematic box shoves a car it drives into)
            var nose = MathX.Flat(oPos - car.Head);
            float noseAhead = nose.Dot(dir), noseSide = Mathf.Abs(nose.X * -dir.Z + nose.Z * dir.X);
            if (noseAhead > 0f && noseAhead < 6f + Mathf.Max(0f, car.Speed - (oVel.X * dir.X + oVel.Z * dir.Z)) && noseSide < 2.1f && Mathf.Abs(rel.Y) < 3f)
            {
                target = 0f;
                wantPull = maxPull;
                blocked |= oSpeed < 0.5f;
            }

            // giving way to someone coming along the road this car is about to turn into (#159): beyond the
            // junction it is "on this road", and the oncoming rule below waited for sight and a reaction time —
            // a car stood in the junction mouth in front of racers at 120-150 km/h. At the line a driver looks
            if (onRoad && ov < -2f)
            {
                if (!junctionLooked) { junction = car.Route.NextJunction(_roads!, 60f); junctionLooked = true; }
                if (junction is var (_, jD, jGives) && jGives && jD >= 5f && along > jD + 2f && (along - jD) / -ov < 9f)
                {
                    hold = true;
                    target = Mathf.Min(target, StopWithin(jD - 12f));
                    continue;
                }
            }
            if (onRoad && along > 0.5f && ov < -2f)
            {
                // coming the other way: they meet in this many seconds
                float meet = along / Mathf.Max(car.Speed - ov, 1f);
                if (meet > 7f || !Noticed(oPos, meet, brake: true)) continue;
                racerRight |= lat > mine + 0.5f && along < 30f;
                wantPull = maxPull;
                yield = true;
                if (!narrow) continue;
                // someone fast on a road too narrow to meet at speed: over to the edge and stop there until
                // it has gone by — a car still rolling, however slowly, is one a racer has to judge (#85)
                if (oSpeed > 8f) { stopFor = true; continue; }
                target = Mathf.Min(target, 4f);
                // where it will be across the road when they meet, its sideways drift carried on
                float across = lat + (oVel.X * side.X + oVel.Z * side.Z) * Mathf.Min(meet, 2f);
                // in this car's lane: wait for it, tucked in
                if (across > mine - 2.1f) target = Mathf.Min(target, StopWithin(along - 12f));
                continue;
            }
            if (onRoad && along > 0.5f)
            {
                // in the lane ahead (a racer, a player), going its way or standing
                if (along > 35f || Mathf.Abs(lat - mine) > 2.0f) continue;
                float follow = Mathf.Max(0f, Mathf.Max(ov, 0f) + (along - 7f) * 0.7f);
                if (follow < target) { target = follow; blocked = oSpeed < 0.5f; }
                if (oSpeed < 0.5f) wantPull = maxPull;
                continue;
            }
            if (onRoad)
            {
                // behind on this road, closing: make way — over to the edge, and slow so the pass is short
                // (on a narrow road also one that has caught up and follows: slowing only until it no longer
                // closed, the car sped up again and the racer sat behind it waiting for a gap)
                float closing = ov - car.Speed;
                bool following = narrow && along > -40f && ov > 1f;
                if (along > 0.5f || (!following && (closing < 2f || -along / closing > 8f))) continue;
                // one bursting up from behind: a start, a swerve, but a lift rather than a stamp on the brakes
                // in front of it
                if (!Noticed(oPos, closing > 0.5f ? -along / closing : 99f, brake: false)) continue;
                racerRight |= lat > mine + 0.5f && along > -30f;
                wantPull = maxPull;
                yield = true;
                // narrow: stop at the edge, a standing car is passed at speed wherever one fits beside it
                if (narrow) stopFor = true;
                else makeWay = Mathf.Min(makeWay, cruise * 0.6f);
                continue;
            }
            // off this road: someone fast about to pass through the junction ahead? wait short of it
            if (oSpeed < 2f) continue;
            if (!junctionLooked) { junction = car.Route.NextJunction(_roads!, 60f); junctionLooked = true; }
            if (junction is not var (j, dj, gives)) continue;
            var toJ = MathX.Flat(j - oPos);
            float dJ = toJ.Length(), towards = dJ > 0.1f ? (oVel.X * toJ.X + oVel.Z * toJ.Z) / dJ : oSpeed;
            // giving way (#159): look left and right over what a racer covers while this car pulls out (from the
            // line to clear the main road: ~3.5 s from standing, and a margin) — 9 s, not 7
            if (dJ > 15f && (towards < 3f || dJ / towards > (gives ? 9f : 7f))) continue;
            // committed (the nose is in it): clear it rather than stop across the road
            if (dj < 5f) continue;
            // a driver at the line has stopped to look: no reaction time, and a crest or a bend hiding the
            // racer (the line of sight rule) is no excuse to pull out in front of it
            if (!gives && !Noticed(oPos, towards > 0.5f ? dJ / towards : 99f, brake: true)) continue;
            hold = true;
            target = Mathf.Min(target, StopWithin(dj - 12f));
        }

        // making way for one behind is a gentle lift (2.5 m/s²): braking at 6 in front of a racer following
        // at a gap planned for 5 m/s² is how one was rear-ended at 63 km/h (#85)
        float accel = target > car.Speed ? 2.2f : 6f;
        if (makeWay < target) { target = makeWay; accel = car.Speed > target ? 2.5f : 2.2f; }
        if (stopFor && target > 0f) { target = 0f; accel = 3.5f; }
        // seen early the reaction is the calm one above; seen with under 2.5 s left (out of a blind bend, over
        // a crest) the driver is startled: brakes hard to a stop for someone coming at it or across its way,
        // swerves for the verge faster (never towards the racer: not if it is on that side), and wobbles a
        // little — a fright, not a plan. Staying on the tarmac, it never goes off a drop
        bool reacted = car.Alert >= car.Reaction;
        car.Alert = threat ? car.Alert + dt : 0f;
        if (threat && !reacted && car.Alert >= car.Reaction && threatTtc < 2.5f) { car.Startle = 1.2f; car.StartleBrake = threatBrake; }
        if (car.Startle > 0f)
        {
            car.Startle -= dt;
            if (car.StartleBrake) { target = 0f; accel = 8f; }
        }
        if (racerRight) wantPull = Mathf.Min(wantPull, car.Pull);
        // a queue starts car by car (#1046): a standing car let go (its light turns green, the car ahead moves off) starts its
        // driver's reaction time later, so a queue stretches out as it discharges (it moved off as one train of cars)
        // one carriageway too narrow for two cars side by side (#1134, the user): not into it while one comes the other way on it or
        // is about to; it waits at the stretch's end (before the junction's core where the stretch starts past one)
        car.NarrowHeld = false;
        if (NarrowAhead(car) is { } narrowStop)
        {
            target = Mathf.Min(target, StopWithin(narrowStop));
            car.NarrowHeld = car.Speed < 0.5f;
        }
        if (car.Speed < StartSpeed)
        {
            if (target <= StartSpeed) car.Starting = 0f;
            else if ((car.Starting += dt) < car.Reaction + StartLag) target = car.Speed;
        }
        car.Speed = Mathf.MoveToward(car.Speed, target, accel * dt);
        // waiting for a race is not stuck: a car dropped after 20 s of it vanished in front of the racers.
        // Nor is waiting at a red (#353): a car 20 s at one vanished from the line (a red always ends;
        // a wait on green for room still counts, the way out of a gridlock), nor in a junction for a gap (#1046: it vanished
        // there; the green it gives way to ends too). A car held by the lights (a red, a wait at the line) in a queue does not
        // turn round either
        bool atLights = lights < float.MaxValue;
        car.Stuck = car.Speed < 0.3f && !stopFor && !hold && !car.AtRed && !car.RoomHeld && !car.NarrowHeld && !car.Queued && !car.GapWaiting && !car.GapHeld ? car.Stuck + dt : 0f;
        car.Holding = hold;
        car.Yield = yield;
        // (#1134, J1: why a car stands at its line through a green) logged a few times, with what holds it
        if (car.Approach is { Site: { } gs } && car.Group >= 0 && car.ToLine < 4f && car.ToLine > -1f && car.Speed < 0.3f
            && gs.Plan.State(car.Group, SignalLamps.Clock()) == SignalAspect.Green)
        {
            car.GreenStand += dt;
            if (car.GreenStand > 12f && !car.GreenStandLogged && GreenStandsLogged++ < 12)
            {
                car.GreenStandLogged = true;
                var (ge, gn) = _origin.ToLv95(car.Head);
                GD.Print($"[traffic] a car stands 12 s at its line on green at {ge:F0},{gn:F0}: turn {car.Turn}, lane {car.LaneIndex}, room-held {car.RoomHeld}, "
                    + $"narrow-held {car.NarrowHeld}, queued {car.Queued}, gap waiting {car.GapWaiting}/{car.GapHeld}, holding {hold}, stop-for {stopFor}, "
                    + $"waited permissive {car.WaitedPermissive}, target {target:F1}, next legs {string.Join(" > ", car.Route.Legs.Skip(car.Route.Leg).Take(4).Select(l => $"{l.Item1.Class}{(l.Item1.Path is not null ? "/path" : l.Item1.Connector ? "/link" : "")} {l.Item1.Length:F0}m w{l.Item1.Width:F1}"))}");
            }
        }
        else car.GreenStand = 0f;
        car.Stale = blocked && car.Speed < 0.3f && !atLights ? car.Stale + dt : 0f;
        if (car.Stale > 5f && twoWay)
        {
            car.Stale = 0f;
            car.Route = new Route(edge, !car.Route.Forward, edge.Length - car.Route.Arc);
            car.ClearTrail();
        }
        // sideways only while it rolls, as a car steers (#854: a standing car slid over)
        float rolling = Mathf.Clamp(car.Speed / 3f, 0f, 1f);
        car.Pull = Mathf.MoveToward(car.Pull, Mathf.Min(wantPull, maxPull), (car.Startle > 0f ? 2.2f : 0.8f) * rolling * dt);

        var before = car.Head;
        var (legEdge, legForward) = (car.Route.Edge, car.Route.Forward);
        // (at the end of its way: dropped this tick, queued or not, #1134)
        if (!car.Route.Advance(car.Speed * dt, leg => NextRoad(leg, car))) car.Stuck += 21f;
        if (car.Route.Edge != legEdge) HandOver(car, legEdge, legForward);
        car.Route.Trim(90f);   // the road behind it too: whoever is closing from behind is found on it
        float pull = car.Startle > 0f ? Mathf.Clamp(car.Pull + 0.12f * Mathf.Sin(car.Startle * 14f), 0f, maxPull) : car.Pull;
        // lane changes (#353): following a taper at its speed, else over ~2.5 s; after the line back to its usual line
        // (#1134, the user: one lane at a time, the changes spaced, planned ahead: LaneChoice.Aim)
        float planTo = Mathf.Min(car.ToLine, tailAhead - QueueSpacing);
        float aim = LaneChoice.Aim(car.Lane, car.LaneWanted, car.StepTarget, car.SinceStep, planTo, out bool steps);
        if (steps)
        {
            if (Mathf.Abs(aim - car.Lane) > LaneChoice.LaneWidth * 1.4f) MultiLaneJumps++;
            (car.StepTarget, car.SinceStep) = (aim, 0f);
            LaneChanges++;
        }
        car.SinceStep += car.Speed * dt;
        if (sideTaken == 0f || Mathf.Sign(aim - car.Lane) != sideTaken)
            car.Lane = Mathf.MoveToward(car.Lane, aim, 0.35f * car.Speed * dt);
        float lane = car.Lane;
        car.Place(e => KeepRight(e, car.Route.Forward, car.Habit) + pull + lane);
        car.Vel = dt > 0f ? MathX.Flat(car.Head - before) / dt : Vector3.Zero;
        // where its lane takes it, 0.5 s apart at this speed: a racer reads it to see it coming out of a side road
        for (int k = 0; k < PathSteps; k++) car.Path[k] = car.Route.At(-car.Speed * 0.5f * (k + 1)).Pos;
    }


    /// <summary>The longest road piece between an approach's end and its junction path that the car looks past for the path (m).</summary>
    private const float PathGap = 12f;

    /// <summary>A car with no lane record to steer by eases onto a junction path's first lane over this many metres before it (#818 phase 5).</summary>
    private const float PathLeadIn = 40f;

    /// <summary>How far a driver notices a car coming, m.</summary>
    private const float SightRange = 250f;

    /// <summary>
    /// Whether this car's driver has <paramref name="at"/> in sight: within <see cref="SightRange"/> and
    /// no terrain or building on the line from the driver's eyes (trees, cars and people do not hide it).
    /// One ray per car every 0.2 s, remembered for 1.5 s.
    /// ponytail: one sight per car, not per racer: a driver who has seen one racer "sees" the others
    /// near it too; per-racer memory if that ever shows.
    /// </summary>
    private bool Sees(Vehicle car, Vector3 at)
    {
        var eye = car.Head + Vector3.Up * 1.2f;
        if (eye.DistanceSquaredTo(at) > SightRange * SightRange) return false;
        if (car.LookIn <= 0f)
        {
            car.LookIn = 0.2f;
            var query = PhysicsRayQueryParameters3D.Create(eye, at + Vector3.Up, ~(TreeColliders.Layer | Player.Hurtbox.Layer));
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
            bool hidden = hit.Count > 0 && hit["collider"].AsGodotObject() is StaticBody3D and not AnimatableBody3D;
            if (!hidden) car.SawAgo = 0f;
        }
        return car.SawAgo < 1.5f;
    }

    // ---- giving way (#121) -----------------------------------------------------------------

    /// <summary>A side road's car starts looking this far before the end of its road.</summary>
    private const float YieldLookAhead = 40f;

    /// <summary>
    /// Pull out only if no main-road car arrives within this many seconds: the gap a driver
    /// accepts before turning in front of oncoming traffic (gap-acceptance studies put the
    /// critical gap for a minor-road manoeuvre at about 6-7 s; 6 s here).
    /// </summary>
    private const float YieldGap = 6f;

    /// <summary>Main-road traffic is watched this far from the junction at most (a 120 km/h gap).</summary>
    private const float YieldWatch = 200f;

    /// <summary>
    /// The speed a car on a side road may still do as it nears the junction it must give way at
    /// (the edge's end flagged by the tile): it slows to look, and stops short of the Wartelinie
    /// while a car on the main road would reach the junction within <see cref="YieldGap"/> s, or
    /// is in it, or a player stands in it. Cars on yielding roads do not wait for one another
    /// (each sees the other stopped at its own line). +∞ when there is nothing to give way to.
    /// </summary>
    private float GiveWay(Vehicle car, List<(Vector3 Pos, Vector3 Vel)> obstacles)
    {
        car.GivingWay = false;
        var (edge, forward) = car.Route.Legs[car.Route.Leg];
        if ((edge.Yield & (forward ? RoadAttrFlags.YieldAtEnd : RoadAttrFlags.YieldAtStart)) == 0) return float.MaxValue;
        // a signalised approach (#353): the lights decide, the yield signs are for when they are off
        if ((forward ? edge.ApproachAtEnd : edge.ApproachAtStart) is { Approach.Site: not null }) return float.MaxValue;
        // and on a short piece past the lights' line into a lit junction's path (the crosswalk's at J2, #971: the yield sat on it,
        // and a car past its line on green gave way to the cross road's cars standing at their red, for good)
        var legs = car.Route.Legs;
        if (car.Route.Leg + 1 < legs.Count && legs[car.Route.Leg + 1].Edge.Path is { Site: not null }) return float.MaxValue;
        float remaining = edge.Length - car.Route.Arc;
        if (remaining > YieldLookAhead) return float.MaxValue;
        var junction = forward ? edge.Points[^1] : edge.Points[0];

        bool conflict = false;
        foreach (var o in obstacles)
            if (MathX.FlatLength(o.Pos - junction) < 8f) { conflict = true; break; }
        foreach (var other in _cars)
        {
            if (conflict) break;
            if (other == car || IsWaiting(other, junction)) continue;
            var to = junction - other.Head;
            float d = MathX.FlatLength(to);
            if (d > YieldWatch) continue;
            var (_, dir) = other.Route.At(0);
            bool coming = new Vector2(dir.X, dir.Z).Dot(new Vector2(to.X, to.Z)) > 0;
            if (d < 10f || coming && d < Mathf.Max(other.Speed, 3f) * YieldGap) conflict = true;
        }
        car.GivingWay = conflict;
        // slow to look; stop 2 m short of the end (the teeth stand at the main road's edge, past it)
        float look = 3f + remaining * 0.35f;
        return conflict ? Mathf.Max(0f, (remaining - 2f) * 0.7f) : look;
    }

    // ---- approaches: lanes and traffic lights (#353) -----------------------------------------

    /// <summary>A car finds the approach its route leads into this far ahead: its lanes start up to ~80 m before the line.</summary>
    private const float ApproachLookAhead = 300f;

    /// <summary>How hard a car eases down to a lit junction's limit on its way to the line (m/s²).</summary>
    private const float LimitEase = 2f;

    /// <summary>
    /// The speed limit a car keeps to at traffic lights (#979, the user): its arm's (the signal plan's, 50 km/h in town) through the
    /// junction, eased down to on the approach so it crosses its line no faster: the lights are timed for a first car entering at
    /// that limit (SN 640 838), and a car arriving on a green does no more. +∞ elsewhere.
    /// </summary>
    private static float LitLimit(Vehicle car, LaneEdge edge)
    {
        if (edge.Path is { Site: { } here, Group: >= 0 } p && p.Group < here.Plan.Groups.Count)
            return here.Plan.Arms[here.Plan.Groups[p.Group].Arm].SpeedKmh / 3.6f;
        if (car.Approach is { Site: { } site } a && a.Arm < site.Plan.Arms.Count && car.ToLine < float.MaxValue)
        {
            float limit = site.Plan.Arms[a.Arm].SpeedKmh / 3.6f;
            return Mathf.Sqrt(limit * limit + 2f * LimitEase * Mathf.Max(0f, car.ToLine));
        }
        return float.MaxValue;
    }

    /// <summary>A car reads the lights this far before the stop line.</summary>
    private const float SignalLookAhead = 80f;

    /// <summary>On yellow a car stops if it can at this deceleration (m/s²), else it clears the junction.</summary>
    private const float AmberBrake = 3f;

    /// <summary>On green a car looks this far before the line whether there is room past the junction for it.</summary>
    private const float RoomLook = 30f;

    /// <summary>
    /// The speed a car may still do toward the approach its route leads into (#353), and its lane
    /// there. Once per approach it takes its next turn (<see cref="TurnOf"/>), the lane for it
    /// (<see cref="LaneApproach.LaneFor"/>) and, at traffic lights, the group for it; then it
    /// moves to that lane as the lanes open (<see cref="LaneApproach.Lateral"/>). At lights it
    /// reads the group on the server clock, so every peer's traffic stops for the same red: red or
    /// red and yellow, it stops half a metre short of its lane's stop line; yellow, it stops if it
    /// can at <see cref="AmberBrake"/>, else goes; green, it goes, unless it turns left across
    /// oncoming traffic from a shared lane, or there is no room for it past the junction. +∞ when
    /// nothing holds it.
    /// </summary>
    private float Approach(Vehicle car)
    {
        car.AtRed = car.RoomHeld = false;
        // the next approach on its route within reach: it may be a short edge after another (a
        // split line, a junction just before), read before the car is on it
        LaneApproach? approach = null;
        float toLine = float.MaxValue, toEnd = 0f, along = 0f;
        int leg = -1;
        var legs = car.Route.Legs;
        for (int i = car.Route.Leg; i < legs.Count && along < ApproachLookAhead; i++)
        {
            var (e, f) = legs[i];
            // (#1134: read from as far as 300 m, so a car settles in its lane early, but not past a junction before it)
            if (i > car.Route.Leg && e.Path is not null) break;
            float rem = i == car.Route.Leg ? e.Length - car.Route.Arc : e.Length;
            if ((f ? e.ApproachAtEnd : e.ApproachAtStart) is var (a, back))
            {
                approach = a; toEnd = along + rem; toLine = toEnd - back; leg = i;
                break;
            }
            along += rem;
        }
        // the path its route takes past this approach (#818 phase 5): planned later than the approach is seen, the lane follows it
        // (past the short road pieces between the approach's end and the path: at J2 a 4 m piece over the crosswalk hid it, so the
        // car guessed its move from the road's line and waited for another group than the one it drove on, #971)
        JunctionPathInfo? pathAhead = null;
        if (approach is not null)
            for (int k = leg + 1; k < legs.Count; k++)
            {
                if (legs[k].Edge.Path is { } p) { pathAhead = p; break; }
                if (legs[k].Edge.Length > PathGap) break;
            }
        bool same = approach is not null && approach == car.Approach;
        bool laneAgain = !same || pathAhead != car.PathAhead;
        car.PathAhead = pathAhead;
        float before = same ? car.ToLine : float.MaxValue;
        car.ToLine = toLine;
        car.LaneWanted = car.LaneAim = 0f;
        if (!same)
        {
            car.Approach = approach;
            car.ClearingAmber = car.WasAtRed = car.WaitedPermissive = car.WaitedRoom = false;
        }
        if (laneAgain)
        {
            if (approach is not null && pathAhead is { } path)
            {
                // a junction path chosen (#818 phase 5): its move, the lane it starts from, the group it goes on
                car.Turn = path.Move;
                car.LaneIndex = approach.LaneNearest(path.FromOffset);
                car.Group = path.Site is not null && path.Site == approach.Site && path.Group >= 0 ? path.Group
                    : approach.Site is { } s ? GroupFor(s, approach.Arm, car.Turn) : -1;
            }
            else if (approach is not null)
            {
                car.Turn = TurnOf(approach, WayPast(legs, leg));
                car.LaneIndex = approach.LaneFor(car.Turn, car.Habit);
                car.Group = approach.Site is { } s ? GroupFor(s, approach.Arm, car.Turn) : -1;
            }
        }
        // (#818 phase 5) the path ahead with no lane record to steer by (an unlit junction, an arm without pockets): the car eases
        // from its usual line onto the path's first lane over the last PathLeadIn metres, so it reaches the path on its lane
        if (approach is null || !approach.MultiLane || car.LaneIndex < 0)
        {
            float toPath = 0f;
            for (int i = car.Route.Leg; i < legs.Count && toPath < PathLeadIn; i++)
            {
                var (e, f) = legs[i];
                if (e.Path is { } next && i > car.Route.Leg)
                {
                    var (road, roadForward) = legs[i - 1];
                    float onto = Mathf.Clamp((PathLeadIn - toPath) / (PathLeadIn - 10f), 0f, 1f);
                    car.LaneAim = next.FromOffset - KeepRight(road, roadForward, car.Habit);
                    car.LaneWanted = car.LaneAim * onto;
                    break;
                }
                if (e.Path is not null) break;   // on a path: its own line
                toPath += i == car.Route.Leg ? e.Length - car.Route.Arc : e.Length;
            }
        }
        if (approach is null) return float.MaxValue;

        // its lane: from its usual line onto the original lane's centre, then where its lane goes
        if (approach.MultiLane && car.LaneIndex >= 0)
        {
            float d = Mathf.Max(0f, toLine);
            float onto = Mathf.Clamp((approach.Reach + 15f - d) / 15f, 0f, 1f);
            float centre = approach.LaneCentre - KeepRight(car.Route.Edge, car.Route.Forward, car.Habit);
            car.LaneWanted = centre * onto + approach.Lateral(car.LaneIndex, d);
            car.LaneAim = centre + approach.Lateral(car.LaneIndex, 0f);
            // (#1172) on a road that already has its lane here (an artery's lanes run on before the record's 24 m of them): straight to
            // it, not to the original lane's centre first. Upstream every car rode its usual line, so a queue longer than the record
            // stood in one or two lanes (J13's west arm: L 0, T 5, T 9, TR 20 at its longest)
            if (HasLane(car.Route.Edge, car.Route.Forward, KeepRight(car.Route.Edge, car.Route.Forward, car.Habit) + car.LaneAim))
                car.LaneWanted = car.LaneAim;
        }
        bool crossed = before > 0f && before < float.MaxValue && toLine <= 0f;
        bool pocketLeft = car.Turn == SignalMoves.Left && car.LaneIndex >= 0 && approach.Parent[car.LaneIndex] >= 0
            && approach.Lanes[car.LaneIndex].Moves == SignalMoves.Left;
        if (crossed && pocketLeft) { approach.PocketLefts++; PocketLefts++; }
        // how far from its lane it crossed the line (the lane change lagging the taper), for the probe
        if (crossed && approach.MultiLane && car.LaneIndex >= 0)
        {
            approach.LaneError = Mathf.Max(approach.LaneError, Mathf.Abs(car.Lane - car.LaneWanted));
            approach.Crossings[car.LaneIndex]++;
            // from the original lane's centre, as the records give the lanes (car.Lane is from its usual line)
            approach.OffsetSum[car.LaneIndex] += car.Lane - (approach.LaneCentre - KeepRight(car.Route.Edge, car.Route.Forward, car.Habit));
        }

        if (approach.Site is not { } site || car.Group < 0)
        {
            if (crossed) { approach.GreenCrossings++; LinesCrossed++; }
            return float.MaxValue;
        }
        if (toLine > SignalLookAhead || toLine < -0.5f) return float.MaxValue;
        var aspect = site.Plan.State(car.Group, SignalLamps.Clock());
        // crossing the line on red, unless it was already clearing a yellow it could not stop for
        if (crossed)
        {
            if (aspect is SignalAspect.Red && !car.ClearingAmber) { RedRuns++; approach.RedRuns++; }
            else if (car.ClearingAmber) { AmberClears++; approach.AmberClears++; if (aspect is SignalAspect.Red or SignalAspect.RedAmber) ClearedOnRed++; }
            else
            {
                LinesCrossed++;
                approach.GreenCrossings++;
                if (car.WasAtRed) Discharged(approach, car.LaneIndex);
            }
        }
        // its lane's line: a bike box in front of it keeps cars back
        // (by its front: the line is where its bumper stops, not its middle, #854)
        float stopAt = toLine - 0.5f - CarFront - (car.LaneIndex >= 0 ? Mathf.Max(0f, approach.Lanes[car.LaneIndex].StopBehind) : 0f);
        if (aspect is SignalAspect.Green or SignalAspect.FlashingAmber or SignalAspect.Off)
        {
            car.ClearingAmber = false;
            if (toLine <= 0f) return float.MaxValue;
            // a left turn from the shared lane gives way to oncoming traffic: it waits at the line, where its path has no points to
            // wait at inside the junction (#1046: an older tile, no junction paths)
            if (car.Turn == SignalMoves.Left && site.Plan.Groups[car.Group].Kind == SignalGroupKind.Car && pathAhead is not { Yields.Length: > 0 }
                && Oncoming(car, site, approach.Arm))
            {
                if (!car.WaitedPermissive) { car.WaitedPermissive = true; approach.PermissiveWaits++; PermissiveWaits++; }
                return StopWithin(stopAt);
            }
            // a queue does not block the junction: no entering it without room past it (only while it
            // can still stop calmly; one already too close goes)
            if (toLine < RoomLook && car.Speed * car.Speed / 8f < Mathf.Max(stopAt, 0f) + 0.5f && !RoomPast(car, approach, leg, toEnd))
            {
                car.RoomHeld = true;
                if (!car.WaitedRoom) { car.WaitedRoom = true; approach.RoomWaits++; RoomWaits++; }
                return StopWithin(stopAt);
            }
            return float.MaxValue;
        }
        // first sight of it too close to stop even hard (a car just spawned or turned in): it clears
        bool firstSight = before >= SignalLookAhead;
        // one clearing goes on only while it still cannot stop even hard: held up by the car ahead before its line, it stops
        // (#971: kept for good, 15 of 28 cars clearing an amber at J2 crossed their line on red, after the car ahead moved on)
        bool cannotStop = car.Speed * car.Speed / (2f * 6f) > stopAt;
        // and on amber only one that reaches its line before the red, with half a second to spare, at the speed it will have
        // (its own, or the bend's ahead if lower: one that decided at 7.7 m/s slowed to 5.8 for its turn and crossed 0.1 s into
        // the red), else it stops if it can (#971: 8 more crossed on red)
        var (_, bendDir) = car.Route.At(-18f);
        float bend = Mathf.Acos(Mathf.Clamp(car.Route.At(0).Dir.Dot(bendDir), -1f, 1f));
        float reaching = bend > 0.05f ? Mathf.Min(car.Speed, (float)Terrain.Format.TurnSpeed.Of(18f / bend)) : car.Speed;
        bool inTime = aspect == SignalAspect.Amber && reaching > 0.5f
            && (toLine - CarFront) / reaching <= site.Plan.UntilChange(car.Group, Terrain.SignalLamps.Clock()) - 0.5;
        if (car.ClearingAmber && cannotStop || firstSight && cannotStop
            || aspect == SignalAspect.Amber && car.Speed * car.Speed / (2f * AmberBrake) > stopAt && (inTime || cannotStop))
        {
            car.ClearingAmber = true;
            return float.MaxValue;
        }
        car.ClearingAmber = false;
        if (!car.WasAtRed) { RedStops++; approach.RedStops++; }
        car.AtRed = car.WasAtRed = true;
        return StopWithin(stopAt);
    }

    /// <summary>
    /// Which way a route goes past a junction: from the end of leg <paramref name="leg"/> to 15 m
    /// into the road it takes (across a connector between trimmed ends, onto the road beyond).
    /// Zero when the legs do not reach that far.
    /// </summary>
    private static Vector3 WayPast(List<(LaneEdge Edge, bool Forward)> legs, int leg)
    {
        var (e, f) = legs[leg];
        var end = f ? e.Points[^1] : e.Points[0];
        for (int i = leg + 1; i < legs.Count; i++)
        {
            var (n, nf) = legs[i];
            if (n.Connector && i + 1 < legs.Count) continue;
            return Into(end, n, nf);
        }
        return Vector3.Zero;
    }

    /// <summary>From a junction point to 15 m into an edge leaving it, flat.</summary>
    private static Vector3 Into(Vector3 from, LaneEdge edge, bool forward)
    {
        float s = Mathf.Min(15f, edge.Length);
        var (p, _) = edge.Sample(forward ? s : edge.Length - s);
        return new Vector3(p.X - from.X, 0f, p.Z - from.Z);
    }

    /// <summary>
    /// The turn a way past the junction makes from an approach: at traffic lights as the plan
    /// names it (the arm it leaves along, <see cref="SignalPlan.Turn"/>); elsewhere within 45
    /// degrees of straight on is through.
    /// </summary>
    private static SignalMoves TurnOf(LaneApproach approach, Vector3 way)
    {
        if (way.LengthSquared() < 1f) return SignalMoves.Through;
        if (approach.Site is { } site)
        {
            var arms = site.Plan.Arms;
            int exit = -1;
            float best = -2f;
            for (int k = 0; k < arms.Count; k++)
            {
                if (k == approach.Arm || !arms[k].Out) continue;
                float d = site.Out[k].Dot(way);
                if (d > best) { best = d; exit = k; }
            }
            return exit < 0 ? SignalMoves.Through : SignalPlan.Turn(arms, approach.Arm, exit);
        }
        var w = way.Normalized();
        var a = -approach.Out;
        if (a.Dot(w) > 0.707f) return SignalMoves.Through;
        return a.Z * w.X - a.X * w.Z > 0f ? SignalMoves.Left : SignalMoves.Right;   // the driver's left is (a.Z, 0, -a.X)
    }

    /// <summary>The group for a turn on an approach: its pocket's arrow, else the main head that carries it.</summary>
    public static int GroupFor(SignalSite site, int arm, SignalMoves turn)
    {
        int any = -1;
        for (int g = 0; g < site.Plan.Groups.Count; g++)
        {
            var group = site.Plan.Groups[g];
            if (group.Arm != arm || (group.Moves & turn) == 0) continue;
            if (group.Kind is SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow) return g;
            if (group.Kind == SignalGroupKind.Car && any < 0) any = g;
        }
        return any;
    }

    /// <summary>
    /// Whether there is room for a car past the junction its leg <paramref name="leg"/> ends at
    /// (<paramref name="toEnd"/> m ahead), the box rule (#1134, <see cref="BoxRule"/>, the user): its exit, from the core up to the
    /// next stop line or junction (as far as its way is planned; <see cref="BoxRule.Reach"/> at most), holds it past every car
    /// on it going its way and every car in the core committed to it. An approach or exit arm flagged <c>AllowBlock</c> lets it go.
    /// </summary>
    private bool RoomPast(Vehicle car, LaneApproach approach, int leg, float toEnd)
    {
        if (approach.AllowBlock) return true;
        // its way planned past the core to the exit's end, so the exit is measured, not guessed free (#1134)
        car.Route.Plan(toEnd + 40f + BoxRule.Reach, l => NextRoad(l, car));
        var legs = car.Route.Legs;
        float across = 0f;
        int i = leg + 1;
        // across the junction: its paths or connectors (#818's cores are paths), and a short road piece before one (the crosswalk's
        // at J2, #971)
        for (; i < legs.Count; i++)
        {
            var e = legs[i].Edge;
            if (!e.Connector && e.Path is null && !(e.Length <= PathGap && i + 1 < legs.Count && (legs[i + 1].Edge.Connector || legs[i + 1].Edge.Path is not null))) break;
            across += e.Length;
        }
        if (i >= legs.Count) return true;
        var (exitAt, exitDir) = car.Route.At(-(toEnd + across + 1f));
        // the exit arm it drives into: the site's arm whose way out is its way
        if (approach.Site is { } site)
        {
            int arm = -1;
            float best = 0.7f;
            for (int k = 0; k < site.Out.Length; k++)
                if (site.Out[k].Dot(exitDir) is var dot && dot > best) (best, arm) = (dot, k);
            if (arm >= 0 && arm < site.Plan.Arms.Count && site.Plan.Arms[arm].AllowBlock) return true;
        }
        // its exit, leg by leg from the core (the user: "look forward through all nodes until it either finds enough space or the
        // stop line"): each leg's road up to the next stop line or junction, less the cars on it going its way (those in the core
        // heading onto it on the first), until one more car fits; a way planned no further than the room found is free beyond
        _exitRoom.Clear();
        float measured = 0f;
        bool open = true;
        LaneApproach? lineAt = null;
        int lineLeg = -1;
        for (int k = i; k < legs.Count && measured < BoxRule.Reach; k++)
        {
            var (e, f) = legs[k];
            if (e.Connector || e.Path is not null) { open = false; break; }
            float room = e.Length;
            bool line = false;
            if ((f ? e.ApproachAtEnd : e.ApproachAtStart) is var (lineApproach, back)) (line, room, lineAt, lineLeg) = (true, Mathf.Max(0f, e.Length - back), lineApproach, k);
            _exitRoom.Add((legs[k], room, 0));
            measured += room;
            if (line) { open = false; break; }
        }
        if (_exitRoom.Count == 0) return open;
        // the lane it will stand in at that line, chosen as Approach will choose it there: the lane its junction path past it starts
        // from, else the one whose arrows show its turn
        int myLane = -1;
        if (lineAt is { MultiLane: true })
        {
            for (int k = lineLeg + 1; k < legs.Count && myLane < 0; k++)
            {
                if (legs[k].Edge.Path is { } p) myLane = lineAt.LaneNearest(p.FromOffset);
                else if (legs[k].Edge.Length > PathGap) break;
            }
            if (myLane < 0) myLane = lineAt.LaneFor(TurnOf(lineAt, WayPast(legs, lineLeg)), car.Habit);
        }
        float reach = measured + 10f;
        foreach (var other in _cars)
        {
            if (other == car || MathX.Flat(other.Head - exitAt).LengthSquared() > reach * reach) continue;
            int at = _exitRoom.FindIndex(x => x.Leg == (other.Route.Edge, other.Route.Forward));
            if (at < 0 && (other.Route.Edge.Connector || other.Route.Edge.Path is not null))
            {
                var olegs = other.Route.Legs;
                for (int k = other.Route.Leg + 1; k < olegs.Count && k <= other.Route.Leg + 4; k++)
                    if (_exitRoom.FindIndex(x => x.Leg == olegs[k]) is >= 0 and var onto) { at = onto; break; }
                if (at > 0) at = 0;   // (committed from the core: it stands at the exit's start first)
            }
            // (#1172) on an exit whose next approach has lanes side by side, only the cars in the lane this car will stand in: a car
            // in the other lane takes none of its room (the strip from J5a to J5b, a left pocket beside the through lane: each car
            // counted 7.5 m of the one lane, the strip "filled" at half its cars)
            if (at >= 0 && myLane >= 0 && other.Approach == lineAt && other.LaneIndex >= 0 && other.Route.Edge.Path is null && !other.Route.Edge.Connector
                && Mathf.Max(0f, other.ToLine) is var od && Mathf.Abs(lineAt!.Lateral(other.LaneIndex, od) - lineAt.Lateral(myLane, od)) > LaneApart) continue;
            if (at >= 0) _exitRoom[at] = _exitRoom[at] with { Cars = _exitRoom[at].Cars + 1 };
        }
        _exitSpans.Clear();
        foreach (var x in _exitRoom) _exitSpans.Add((x.Room, x.Cars));
        return BoxRule.Fits(_exitSpans, open);
    }

    /// <summary>The legs of an exit being measured, each its room and the cars on it (<see cref="RoomPast"/>), one list for the traffic's life.</summary>
    private readonly List<(( LaneEdge, bool) Leg, float Room, int Cars)> _exitRoom = new();
    private readonly List<(float Room, int Cars)> _exitSpans = new();

    /// <summary>
    /// The longest two standing cars have stood body on body (heads under 2.5 m apart, s, #1134): the probe fails over
    /// <see cref="OverlapFails"/> (two cars that block each other for good).
    /// </summary>
    public float OverlapLongest { get; private set; }
    public const float OverlapFails = 15f;

    /// <summary>Lane changes begun, and those that crossed more than one lane at once (#1134: must stay 0).</summary>
    public int LaneChanges { get; private set; }
    public int MultiLaneJumps { get; private set; }

    /// <summary>
    /// Of the paths side by side to one exit with one move (<paramref name="same"/>), the one a car takes (#1134, LaneChoice.Pick): the
    /// lane with the fewest cars already heading for it, biased to the side its next turn after the junction lies on.
    /// </summary>
    private (LaneEdge, bool) PickLane(List<(LaneEdge Edge, bool Forward)> same, Vehicle? car)
    {
        if (same.Count == 1) return same[0];
        var lanes = new List<(float Offset, int Queued)>(same.Count);
        foreach (var o in same)
        {
            int queued = 0;
            foreach (var c in _cars) if (ReferenceEquals(c.PathAhead, o.Edge.Path)) queued++;
            lanes.Add((o.Edge.Path!.FromOffset, queued));
        }
        var next = SignalMoves.None;
        if (car?.Planned is { } way)
        {
            int at = way.FindIndex(w => w.Item1.Path is not null && w.Item1.KeyEnd == same[0].Edge.KeyEnd);
            for (int k = at + 1; at >= 0 && k < way.Count; k++)
                if (way[k].Item1.Path is { } p) { next = p.Move; break; }
        }
        return same[LaneChoice.Pick(lanes, next, car?.Habit ?? 0u)];
    }

    /// <summary>Green stands logged so far (#1134).</summary>
    private int GreenStandsLogged, ContactsLogged;

    /// <summary>Cars that passed one coming the other way on a narrow carriageway, moved over at a crawl (#1180).</summary>
    public int NarrowPasses { get; private set; }

    /// <summary>A carriageway narrower than this holds no two cars side by side (m): two 1.9 m cars and 0.6 m between them (#1134).</summary>
    public const float TwoCarsWide = 4.4f;

    private static bool Narrow(LaneEdge e) =>
        !e.Connector && e.Path is null && e.OneWay == 0 && (e.Flags & RoadFlags.Divided) == 0 && e.Width < TwoCarsWide && e.Length > 6f;

    /// <summary>
    /// The one-carriageway stretch rule (#1134): where a car's way within 60 m leads into a two-way edge too narrow for two cars side
    /// by side (<see cref="TwoCarsWide"/>) and a car is on it the other way, or nearer to it the other way (the lower habit first on a
    /// tie), how far it may still go: to the stretch's start, or to the start of the junction's core before it. Null: it goes. A car
    /// on the stretch or in a core goes on: whoever is on it first has it. Since #1180 only on a road under
    /// <see cref="NarrowPass.MinWidth"/>: on a wider one the two pass each other, moved over at a crawl.
    /// </summary>
    private float? NarrowAhead(Vehicle car)
    {
        var cur = car.Route.Edge;
        if (Narrow(cur) || cur.Path is not null || cur.Connector) return null;
        var legs = car.Route.Legs;
        float along = cur.Length - car.Route.Arc, coreAt = -1f;
        for (int i = car.Route.Leg + 1; i < legs.Count && along < 60f; i++)
        {
            var (e, f) = legs[i];
            if (e.Connector || e.Path is not null) { if (coreAt < 0f) coreAt = along; along += e.Length; continue; }
            if (Narrow(e))
            {
                float entry = coreAt >= 0f ? coreAt : along;
                // (where the two pass each other moved over, #1180, nobody waits: only a road too narrow even for that)
                if (NarrowPass.Fits(e.Width)) return null;
                return NarrowTaken(car, e, f, entry) ? Mathf.Max(0f, entry - 2f) : null;
            }
            coreAt = -1f;
            along += e.Length;
        }
        return null;
    }

    /// <summary>Whether a car is on <paramref name="e"/> against <paramref name="f"/>, or about to enter it that way before <paramref name="car"/> (<paramref name="entry"/> m off).</summary>
    private bool NarrowTaken(Vehicle car, LaneEdge e, bool f, float entry)
    {
        foreach (var other in _cars)
        {
            if (other == car || MathX.Flat(other.Head - car.Head).LengthSquared() > (e.Length + 80f) * (e.Length + 80f)) continue;
            if (other.Route.Edge == e) { if (other.Route.Forward != f) return true; continue; }
            var olegs = other.Route.Legs;
            float d = other.Route.Edge.Length - other.Route.Arc;
            for (int k = other.Route.Leg + 1; k < olegs.Count && k <= other.Route.Leg + 4 && d < 60f; k++)
            {
                if (olegs[k].Item1 == e)
                {
                    if (olegs[k].Item2 != f && (d < entry || d == entry && other.Habit < car.Habit)) return true;
                    break;
                }
                d += olegs[k].Item1.Length;
            }
        }
        return false;
    }



    /// <summary>Whether a car comes the other way through a signalised junction: on the opposite approach, near its line and moving.</summary>
    private bool Oncoming(Vehicle car, SignalSite site, int arm)
    {
        int opposite = -1;
        float most = 0f;
        for (int k = 0; k < site.Out.Length; k++)
        {
            float d = -site.Out[k].Dot(site.Out[arm]);
            if (k != arm && d > most) { most = d; opposite = k; }
        }
        if (opposite < 0 || most < 0.7f) return false;
        foreach (var other in _cars)
            if (other != car && other.Approach is { } oa && oa.Site == site && oa.Arm == opposite && other.ToLine < 40f && other.ToLine > -12f
                && other.Speed > 1f && !other.AtRed)
                return true;
        return false;
    }

    // ---- permissive turns: in to the conflict point, and wait there for a gap (#1046) ---------

    /// <summary>The gap a turning car accepts (s): it goes unless a mover it gives way to reaches the conflict sooner (a permissive left's critical gap is ~4-5 s).</summary>
    private const float AcceptGap = 4.5f;

    /// <summary>Its front stands this far short of the other's band (m).</summary>
    private const float GapMargin = 1f;

    /// <summary>A car looks this far ahead for a junction path with points to give way at, and as far along the movers' ways (m).</summary>
    private const float GapLook = 60f;

    /// <summary>A start (#1046): under this speed (m/s) a car stands; let go, it starts its <see cref="Vehicle.Reaction"/> and this later (s): 0.75-1.25 s.</summary>
    private const float StartSpeed = 0.5f, StartLag = 0.25f;

    /// <summary>
    /// The speed a car may still do toward a point on the junction path ahead where it gives way (#1046): on its green a turn that
    /// yields (<see cref="JunctionPathInfo.Yields"/>) pulls into the junction up to just short of the first such point a mover is
    /// coming to (<see cref="Coming"/>), not into an earlier band it would stand in, and goes once the gap comes; already past its
    /// line, it clears on amber or red. One still before its line waits there unless it would stand fully past it behind the cars
    /// already waiting in the junction. +∞ when nothing holds it.
    /// </summary>
    private float GapWait(Vehicle car, float dt, List<(Vector3 Pos, Vector3 Vel)> obstacles)
    {
        var inside = car.GapWaiting ? car.GapPath : null;
        car.GapWaiting = car.GapHeld = false;
        float result = float.MaxValue;
        var legs = car.Route.Legs;
        float along = -car.Route.Arc;
        for (int i = car.Route.Leg; i < legs.Count && along < GapLook && result == float.MaxValue; i++)
        {
            var (e, forward) = legs[i];
            if (forward && e.Path is { Yields.Length: > 0 } p)
            {
                var yields = p.Yields;
                float stop = float.MaxValue;
                for (int k = 0; k < yields.Length; k++)
                {
                    float wait = along + yields[k].At - CarFront - GapMargin;
                    if (wait < -1.5f || !Coming(car, e, yields[k], obstacles)) continue;
                    // not standing in an earlier band it has not passed yet: it waits short of that one
                    for (int j = 0; j < k; j++)
                    {
                        float earlier = along + yields[j].At - CarFront - GapMargin;
                        if (earlier >= -1.5f && wait - CarFront < along + yields[j].Out) wait = Mathf.Min(wait, earlier);
                    }
                    stop = wait;
                    break;
                }
                // (too close to stop even hard: committed, it goes)
                if (stop < float.MaxValue && car.Speed * car.Speed / 12f <= Mathf.Max(stop, 0f) + 1f)
                {
                    car.GapPath = p;
                    result = StopWithin(stop);
                    // before its line, behind the cars waiting in the junction: it goes in only to stand fully past its line
                    if (car.ToLine <= 0f || car.Approach?.Site != p.Site) car.GapWaiting = true;
                    else if (WaitingOn(car, p) is var ahead and > 0 && stop - ahead * QueueSpacing - CarFront < car.ToLine + 0.5f)
                    {
                        car.GapHeld = true;
                        result = Mathf.Min(result, StopWithin(car.ToLine - 0.5f - CarFront));
                    }
                }
            }
            along += e.Length;
        }
        // --trafficcheck: cars that stood in a junction for a gap, the longest wait, those that left it on amber or red
        if (car.GapWaiting && car.Speed < StartSpeed)
        {
            if (car.GapFor == 0f) InsideWaits++;
            car.GapFor += dt;
        }
        else if (inside is not null && !(car.GapWaiting && car.GapPath == inside) && car.GapFor > 0f)
        {
            LongestInsideWait = Mathf.Max(LongestInsideWait, car.GapFor);
            if (inside.Site is { } site && site.Plan.State(inside.Group, SignalLamps.Clock()) != SignalAspect.Green) ClearedInside++;
            car.GapFor = 0f;
        }
        return result;
    }

    /// <summary>
    /// Whether a mover the car on <paramref name="path"/> gives way to at <paramref name="y"/> is in the conflict or reaches it within
    /// <see cref="AcceptGap"/>: a car whose way goes onto that movement's path (by where its body comes into this car's lane), a
    /// player or a racer at the conflict or closing on it, a walker, rider or tram of the point's kind (<see cref="CrosserComing"/>,
    /// #1060: the modes' movers, from <see cref="_crossers"/>).
    /// </summary>
    private bool Coming(Vehicle car, LaneEdge path, PathYield y, List<(Vector3 Pos, Vector3 Vel)> obstacles)
    {
        var (at, _) = path.Sample((y.At + y.Out) * 0.5f);
        float zone = (y.Out - y.At) * 0.5f + 1f;
        foreach (var (pos, vel) in obstacles)
        {
            var d = MathX.Flat(at - pos);
            float dist = d.Length();
            if (dist < zone) return true;
            float closing = (vel.X * d.X + vel.Z * d.Z) / dist;
            if (dist < GapLook && closing > 0.5f && (dist - zone) / closing < AcceptGap) return true;
        }
        if (CrosserComing(at, zone, y.Mover)) return true;
        if (y.Edge is not { } other) return false;
        _xKey.Head.X = at.X - GapLook - 20f;
        int from = _byX.BinarySearch(_xKey, ByX);
        for (int o = from < 0 ? ~from : from; o < _byX.Count && _byX[o].Head.X <= at.X + GapLook + 20f; o++)
        {
            var c = _byX[o];
            if (c == car) continue;
            var cl = c.Route.Legs;
            float d = -c.Route.Arc;
            int i = c.Route.Leg;
            for (; i < cl.Count && d < GapLook && cl[i].Edge != other; i++) d += cl[i].Edge.Length;
            if (i >= cl.Count || cl[i].Edge != other) continue;
            // one whose light does not let it in (red, or an amber it stops for) and that can still stop short of the path will stop
            // there: not coming (#1046: lefts waited inside a whole green for oncoming cars 90 m out, on their way to their red)
            if (i > c.Route.Leg && other.Path is { Site: { } site, Group: >= 0 } op && op.Group < site.Plan.Groups.Count
                && site.Plan.State(op.Group, SignalLamps.Clock()) is var aspect
                && !(aspect is SignalAspect.Green or SignalAspect.FlashingAmber or SignalAspect.Off || aspect == SignalAspect.Amber && c.ClearingAmber)
                && c.Speed * c.Speed / 12f < d - 1f)
                continue;
            d += y.Enter;
            // in this car's lane now, until it is past by its length and the lane
            if (d <= 0f)
            {
                if (d > -(2f * CarFront + 2f * ConflictPoints.CarBody + 3.5f)) return true;
                continue;
            }
            if (d / Mathf.Max(c.Speed, 0.1f) < AcceptGap) return true;
        }
        return false;
    }

    /// <summary>For <c>--trafficcheck</c>'s gap shot (#1046): a car that has stood over a second in a junction for a gap, its light not red yet, within <paramref name="within"/> m.</summary>
    public Vector3? StandingForGap(Vector3 near, float within)
    {
        foreach (var c in _cars)
            if (c.GapWaiting && c.GapFor > 1f && MathX.Flat(c.Head - near).Length() < within
                && c.GapPath is { Site: { } site, Group: >= 0 } p && site.Plan.State(p.Group, SignalLamps.Clock()) is not (SignalAspect.Red or SignalAspect.RedAmber))
                return c.Head;
        return null;
    }

    /// <summary>Cars standing in the junction for a gap on path <paramref name="p"/> (#1046), near <paramref name="car"/>.</summary>
    private int WaitingOn(Vehicle car, JunctionPathInfo p)
    {
        int n = 0;
        _xKey.Head.X = car.Head.X - GapLook;
        int from = _byX.BinarySearch(_xKey, ByX);
        for (int o = from < 0 ? ~from : from; o < _byX.Count && _byX[o].Head.X <= car.Head.X + GapLook; o++)
        {
            var c = _byX[o];
            if (c != car && c.GapWaiting && ReferenceEquals(c.GapPath, p) && c.Speed < 2f) n++;
        }
        return n;
    }

    /// <summary>Two queued cars crossing their lane's line further apart than this (s) are two queues (#1046).</summary>
    private const double DischargeGap = 6;

    /// <summary>
    /// A car that stood at red crossed its line on green (#1046, <c>--trafficcheck</c>): the time since the car before it in that
    /// lane's queue did, by its place in the queue (the second car's, the third's, ...; the fifth's and later together).
    /// </summary>
    private void Discharged(LaneApproach approach, int lane)
    {
        int i = Math.Clamp(lane, 0, approach.LastDischarge.Length - 1);
        double gap = _now - approach.LastDischarge[i];
        approach.LastDischarge[i] = _now;
        approach.QueuePlace[i] = gap < DischargeGap ? approach.QueuePlace[i] + 1 : 1;
        if (approach.QueuePlace[i] < 2) return;
        int place = Math.Min(approach.QueuePlace[i], HeadwaySum.Length - 1);
        HeadwaySum[place] += gap;
        HeadwayCount[place]++;
    }

    /// <summary>Queue discharge headways (#1046): their sum (s) and count by place in the queue, 2 to 5 (5: the fifth and later).</summary>
    public double[] HeadwaySum { get; } = new double[6];
    public int[] HeadwayCount { get; } = new int[6];
    /// <summary>Cars that stood in a junction for a gap (#1046), the longest such wait (s), and those that left it on amber or red.</summary>
    public int InsideWaits { get; private set; }
    public float LongestInsideWait { get; private set; }
    public int ClearedInside { get; private set; }

    /// <summary>The traffic's own time (s), for the probe's headways.</summary>
    private double _now;

    /// <summary>
    /// For <c>--trafficcheck</c>'s capture (#353): the cars on an approach that read group
    /// <paramref name="group"/> there, from 2 m past its line to <paramref name="within"/> m before
    /// it: standing (under 0.5 m/s), and all of them.
    /// </summary>
    public (int Standing, int All) Queue(LaneApproach approach, int group, float within)
    {
        int standing = 0, all = 0;
        foreach (var c in _cars)
            if (c.Approach == approach && c.Group == group && c.ToLine > -2f && c.ToLine < within)
            {
                all++;
                if (c.Speed < 0.5f) standing++;
            }
        return (standing, all);
    }

    /// <summary>Movers dropped for standing still too long (cars 20 s, walkers and riders 30 s), by <see cref="MoverKind"/> (#1134, the probe).</summary>
    public readonly int[] StuckDropped = new int[5];

    /// <summary>
    /// For the probe (#1134): the cars standing (under 0.5 m/s) on an approach within <paramref name="within"/> m before its line,
    /// whatever their group.
    /// </summary>
    public int Standing(LaneApproach approach, float within)
    {
        int n = 0;
        foreach (var c in _cars)
            if (c.Approach == approach && c.ToLine > -2f && c.ToLine < within && c.Speed < 0.5f) n++;
        return n;
    }

    /// <summary>
    /// <see cref="Standing"/> by the lane each car stands in (#1172, the probe: lanes filling evenly): the lane whose centre where it
    /// stands is nearest its line (before a pocket opens, the lane it branches from; a lane the road has there already, at its place), into <paramref name="counts"/> (one per lane of the approach).
    /// </summary>
    public void StandingByLane(LaneApproach approach, float within, int[] counts)
    {
        Array.Clear(counts);
        foreach (var c in _cars)
        {
            if (c.Approach != approach || c.ToLine <= -2f || c.ToLine >= within || c.Speed >= 0.5f) continue;
            float off = c.Lane - (approach.LaneCentre - KeepRight(c.Route.Edge, c.Route.Forward, c.Habit));
            int best = -1;
            float bestGap = float.MaxValue, d = Mathf.Max(0f, c.ToLine);
            for (int k = 0; k < approach.Lanes.Length; k++)
                if (approach.Lanes[k].Kind == ApproachLaneKind.Car
                    && Mathf.Min(Mathf.Abs(approach.Lateral(k, d) - off),
                        HasLane(c.Route.Edge, c.Route.Forward, approach.LaneCentre + approach.Lanes[k].Offset) ? Mathf.Abs(approach.Lanes[k].Offset - off) : float.MaxValue)
                       + (approach.Parent[k] >= 0 ? 0.01f : 0f) is var gap && gap < bestGap) (best, bestGap) = (k, gap);
            if (best >= 0 && best < counts.Length) counts[best]++;
        }
    }

    /// <summary>Cars waiting at a red light now (#353).</summary>
    public int AtRedCars => _cars.Count(c => c.AtRed);
    /// <summary>Signalised approaches in the road graph (#353).</summary>
    public int SignalApproaches => _roads?.Approaches.Count(a => a.Site is not null) ?? 0;
    /// <summary>Since the start (#353): cars that crossed a stop line on red (must stay 0), came to read red and stopped, cleared a yellow they could not stop for, crossed on green (or at a junction without lights), turned left from a pocket, waited at the line to turn left across oncoming traffic, waited for room past the junction.</summary>
    public int RedRuns { get; private set; }

    /// <summary>Cars onto a junction path (#818 phase 5), and the largest sideways step at the hand-over onto one and off one (m).</summary>
    public int PathEntries { get; private set; }

    /// <summary><c>--feed-turns</c> (#854): the moves fed cars took at their junction (left, straight on, right), and how many not the one they drew.</summary>
    public readonly int[] FedMoves = new int[3];
    public readonly int[] FeedFails = new int[3];
    public int FedMovesMissed { get; private set; }
    /// <summary>Cars that crossed their line on red while clearing an amber they had not stopped for (#971).</summary>
    public int ClearedOnRed { get; private set; }

    /// <summary><c>--trafficcheck</c> (#854): pairs of cars on one spot this tick (within 1 m), and the most in any tick.</summary>
    public int DoubledNow { get; private set; }
    public int DoubledMost { get; private set; }
    public float PathStepIn { get; private set; }
    public float PathStepOut { get; private set; }
    public int PathStepsOver1m { get; private set; }
    /// <summary>Cars that took a transition's lane to follow toward a junction path (#818 phase 5), and the transitions' pieces in the graph.</summary>
    public int TransitionFollows { get; private set; }
    public int TransitionPieces => _roads?.Transitions ?? 0;
    public int RedStops { get; private set; }
    public int AmberClears { get; private set; }
    public int LinesCrossed { get; private set; }
    public int PocketLefts { get; private set; }
    public int PermissiveWaits { get; private set; }
    public int RoomWaits { get; private set; }

    /// <summary>A car itself on a yielding approach to that junction (waiting at its own line or about to).</summary>
    private static bool IsWaiting(Vehicle v, Vector3 junction)
    {
        var (edge, forward) = v.Route.Legs[v.Route.Leg];
        if ((edge.Yield & (forward ? RoadAttrFlags.YieldAtEnd : RoadAttrFlags.YieldAtStart)) == 0) return false;
        var end = forward ? edge.Points[^1] : edge.Points[0];
        return MathX.FlatLength(end - junction) < 30f;
    }


    /// <summary>Cars stopped or slowing at a side road's Wartelinie for main-road traffic right now.</summary>
    public int GivingWayCars => _cars.Count(c => c.GivingWay);

    /// <summary>
    /// Whether edge <paramref name="e"/>, driven <paramref name="forward"/>, has a car lane of that way whose centre lies within 1 m of
    /// <paramref name="right"/> m right of its centreline (#1172).
    /// </summary>
    private static bool HasLane(LaneEdge e, bool forward, float right)
    {
        if (e.Path is not null || e.Connector || e.LaneWidth <= 0f) return false;
        bool oneWay = e.OneWay != 0;
        if ((e.Flags & RoadFlags.Divided) != 0 && !oneWay) return false;
        int lanes = oneWay ? e.OneWayLanes : forward ? e.LanesFwd : e.LanesBwd;
        float first = oneWay ? e.RightLane : forward ? e.RightLaneFwd : e.RightLaneBwd;
        for (int k = 0; k < lanes; k++)
            if (Mathf.Abs(first - k * e.LaneWidth - right) < 1f) return true;
        return false;
    }

    /// <summary>
    /// Right-hand traffic: an undivided road is shared, so each car keeps to the rightmost lane of its
    /// direction (#700: where the road has the lanes and a car's habit takes the next lane in: a third of
    /// them on 2+ lanes), clear of a painted bike lane.
    /// </summary>
    private static float KeepRight(LaneEdge e, bool forward, uint habit)
    {
        if (e.OneWay != 0) return e.OneWayLanes > 1 && !RoadCrossSection.IsHighSpeed(e.Class) && habit % 3 == 0 ? e.RightLane - e.LaneWidth : e.RightLane;
        if ((e.Flags & RoadFlags.Divided) != 0 || e.Class == RoadClass.Ramp) return 0f;
        float right = forward ? e.RightLaneFwd : e.RightLaneBwd;
        int lanes = forward ? e.LanesFwd : e.LanesBwd;
        return lanes > 1 && habit % 3 == 0 ? right - e.LaneWidth : right;
    }

    /// <summary>
    /// A car moved onto another edge: onto a junction path (#818 phase 5) or off one, its line carries over (a path has no lane
    /// offset of its own: it starts on its lane and ends on the exit's), and it eases back to its usual line from there.
    /// </summary>
    private void HandOver(Vehicle car, LaneEdge from, bool fromForward)
    {
        var to = car.Route.Edge;
        if (to.Path is { } onto && from.Path is null)
        {
            car.Lane = KeepRight(from, fromForward, car.Habit) + car.Lane - onto.FromOffset;
            PathEntries++;
            if (car.Wants is var (wantAt, want) && want != SignalMoves.None && (fromForward ? from.ApproachAtEnd : from.ApproachAtStart) is var (here, _) && here == wantAt)
            {
                FedMoves[onto.Move switch { SignalMoves.Left => 0, SignalMoves.Through => 1, _ => 2 }]++;
                if (onto.Move != want) FedMovesMissed++;
            }
            PathStepIn = Mathf.Max(PathStepIn, Mathf.Abs(car.Lane));
            if (Mathf.Abs(car.Lane) > 1f) PathStepsOver1m++;
        }
        else if (from.Path is { } off && to.Path is null)
        {
            // the path ends on the exit lane: -ToOffset right of a driver leaving along the exit (ToOffset is right of one arriving)
            PathStepOut = Mathf.Max(PathStepOut, Mathf.Abs(car.Lane));
            car.Lane = car.Lane - off.ToOffset - KeepRight(to, car.Route.Forward, car.Habit);
            // leaving onto a transition (an exit's hatch, lanes merging): the lane the path ends on, followed along it
            car.Strip = to.StripAt(car.Route.Forward, atTravelEnd: false, -off.ToOffset);
        }
        else if (from.Path is null && to.Path is null && from.Connector == to.Connector)
            // from one piece of a road to the next: it stays where it is (the pieces' usual lines differ with their widths), then
            // eases on from there, steering (#854: it jumped over where a road widens; was only where a transition steers it)
            car.Lane += KeepRight(from, fromForward, car.Habit) - KeepRight(to, car.Route.Forward, car.Habit);
    }

    /// <summary>
    /// Whether <paramref name="at"/> lies on this car's way <paramref name="ahead"/> m on (#1172): its route's line at that distance (round
    /// a bend, along a junction path), and across it anywhere from where the car drives now to the lane it will be in there
    /// (<see cref="LineAhead"/>), with <see cref="AheadWidth"/> either side: its lane lags the one it wants, so it may still be anywhere
    /// between the two when it gets there.
    /// </summary>
    private static bool OnLineAhead(Vehicle car, Vector3 at, float ahead)
    {
        var (p, t, leg) = car.Route.AtLeg(-ahead);
        var side = new Vector3(-t.Z, 0f, t.X).Normalized();
        var r = MathX.Flat(at - p);
        if (Mathf.Abs(r.Dot(t)) > 4f) return false;
        var (edge, forward) = (car.Route.Edge, car.Route.Forward);
        float now = edge.Path is not null ? car.Lane : KeepRight(edge, forward, car.Habit) + car.Pull + car.Lane;
        float then = LineAhead(car, leg, ahead);
        float lat = r.Dot(side);
        return lat >= Mathf.Min(now, then) - AheadWidth && lat <= Mathf.Max(now, then) + AheadWidth;
    }

    /// <summary>
    /// Where a car will drive on leg <paramref name="leg"/> of its route, <paramref name="ahead"/> m on, m right of that leg's line (#1172),
    /// as <see cref="Approach"/> and <see cref="FollowTransition"/> will set its lane there: on a junction path the path's line; on a
    /// transition's piece (this one or one ahead) the strip it follows at that station (a pocket not open yet rides the through lane);
    /// else its approach lane there, or its wanted line on this piece, or the next piece's usual line.
    /// </summary>
    private static float LineAhead(Vehicle car, int leg, float ahead)
    {
        var legs = car.Route.Legs;
        var (e, f) = legs[leg];
        bool here = leg == car.Route.Leg;
        if (e.Path is not null) return here ? car.LaneWanted : 0f;
        if (e.Transition is not null)
        {
            float s = car.Route.Arc + ahead;
            for (int i = car.Route.Leg; i < leg; i++) s -= legs[i].Edge.Length;
            int strip = here ? car.Strip : -1;
            for (int i = leg + 1; strip < 0 && i < legs.Count; i++)
            {
                if (legs[i].Edge.Path is { } path) { strip = legs[i - 1].Edge.StripAt(legs[i - 1].Forward, atTravelEnd: true, path.FromOffset); break; }
                if (legs[i].Edge.Transition is null) break;
            }
            if (strip >= 0 && e.LaneAt(f, Mathf.Clamp(s, 0f, e.Length), strip) is { } lane) return lane;
        }
        var (edge, forward) = (car.Route.Edge, car.Route.Forward);
        if (edge.Path is null && car.Approach is { MultiLane: true } a && car.LaneIndex >= 0 && car.ToLine < float.MaxValue)
        {
            float keep = KeepRight(edge, forward, car.Habit);
            if (HasLane(edge, forward, keep + car.LaneAim)) return keep + car.LaneAim;
            float d = Mathf.Max(0f, car.ToLine - ahead);
            float onto = Mathf.Clamp((a.Reach + 15f - d) / 15f, 0f, 1f);
            return a.LaneCentre * onto + keep * (1f - onto) + a.Lateral(car.LaneIndex, d);
        }
        return here ? KeepRight(e, f, car.Habit) + car.Pull + car.LaneWanted : KeepRight(e, f, car.Habit);
    }

    /// <summary>
    /// On a transition's piece (#818 phase 5): the car follows its own lane from station to station, the one its path ahead
    /// starts from at the mouth (carried back over the transition's pieces before it), or the one the path behind ended on;
    /// while it opens or closes, between it and the lane it branches from or merges into. Overrides the lane record and the lead-in there.
    /// </summary>
    private void FollowTransition(Vehicle car)
    {
        var (edge, forward) = (car.Route.Edge, car.Route.Forward);
        if (edge.Transition is null || edge.Path is not null) { car.Strip = -1; return; }
        var legs = car.Route.Legs;
        for (int i = car.Route.Leg + 1; i < legs.Count; i++)
        {
            var (e, _) = legs[i];
            if (e.Path is { } path)
            {
                var (mouth, mouthForward) = legs[i - 1];
                int strip = mouth.StripAt(mouthForward, atTravelEnd: true, path.FromOffset);
                if (strip >= 0 && car.Strip < 0) TransitionFollows++;
                car.Strip = strip;
                break;
            }
            if (e.Transition is null) break;   // a stretch of street at its normal section before the junction
        }
        if (car.Strip < 0 || edge.LaneAt(forward, car.Route.Arc, car.Strip) is not { } lane) return;
        car.LaneWanted = lane - KeepRight(edge, forward, car.Habit);
    }

    private (LaneEdge, bool)? NextRoad((LaneEdge Edge, bool Forward) leg, Vehicle? car = null)
    {
        long key = leg.Forward ? leg.Edge.KeyEnd : leg.Edge.KeyStart;
        var options = _roads!.Leaving(key).Where(o => o.Edge != leg.Edge).ToList();
        // at a junction with paths (#818 phase 5): a road arriving takes one of them, a path ends on its road (never on another
        // path back into the junction, which starts at the same mouth)
        if (leg.Edge.Path is not null)
        {
            var roads = options.Where(o => o.Edge.Path is null).ToList();
            if (roads.Count > 0) options = roads;
        }
        else if (options.Any(o => o.Edge.Path is not null)) options = options.Where(o => o.Edge.Path is not null).ToList();
        // a generated car on its way to a destination (#971): the next leg of it, where it may go there
        if (car is not null && PlannedAfter(car, leg) is { } planned && options.Contains(planned))
        {
            // (its way was found along one lane's path: where lanes side by side make the same move to the same exit, the car's
            // habit picks one, as LaneFor does, so they all carry cars, #1134)
            if (planned.Item1.Path is not { } p) return planned;
            var same = options.Where(o => o.Edge.Path!.Move == p.Move && o.Edge.KeyEnd == planned.Item1.KeyEnd).OrderBy(o => o.Edge.Path!.FromOffset).ToList();
            return same.Count > 1 ? PickLane(same, car) : planned;
        }
        if (options.Count == 0)
        {
            // a dead end — a real cul-de-sac, or the edge of what is loaded: turn round
            if (leg.Edge.OneWay != 0) return null;
            return (leg.Edge, !leg.Forward);
        }
        // a turn an OSM restriction forbids from this approach (#353) is not taken, if anything else is left
        if ((leg.Forward ? leg.Edge.ApproachAtEnd : leg.Edge.ApproachAtStart) is var (approach, _) && approach.Banned != 0 && options.Count > 1)
        {
            var end = leg.Forward ? leg.Edge.Points[^1] : leg.Edge.Points[0];
            var allowed = options.Where(o => (TurnOf(approach, WayInto(end, o)) & approach.Banned) == 0).ToList();
            if (allowed.Count > 0) options = allowed;
        }
        // mostly stay on roads of the same size; a motorway rarely turns off into a lane. Paths are chosen by their exit (one
        // weight each, however many lanes lead there), then the lane nearest the car's usual line
        if (options.All(o => o.Edge.Path is not null) && options.Count > 0)
        {
            // a fed car that wants a move here takes it, where a path has it (#854, --feed-turns)
            if (car?.Wants is var (wantAt, want) && want != SignalMoves.None
                && (leg.Forward ? leg.Edge.ApproachAtEnd : leg.Edge.ApproachAtStart) is var (here, _) && here == wantAt
                && options.Where(o => o.Edge.Path!.Move == want).ToList() is { Count: > 0 } wanted)
                options = wanted;
            var exits = options.GroupBy(o => o.Edge.KeyEnd).Select(g => g.ToList()).ToList();
            float ExitWeight(List<(LaneEdge Edge, bool)> g) => Mathf.Abs((int)ExitClass(g[0].Edge) - (int)leg.Edge.Class) <= 1 ? 4f : 1f;
            float sum = exits.Sum(ExitWeight), at = (float)_rng.NextDouble() * sum;
            var exit = exits[^1];
            foreach (var g in exits) { at -= ExitWeight(g); if (at <= 0) { exit = g; break; } }
            return PickLane(exit, car);
        }
        float Weight((LaneEdge Edge, bool) o) =>
            Mathf.Abs((int)o.Edge.Class - (int)leg.Edge.Class) <= 1 ? 4f : 1f;
        float total = options.Sum(Weight), pick = (float)_rng.NextDouble() * total;
        foreach (var o in options)
        {
            pick -= Weight(o);
            if (pick <= 0) return o;
        }
        return options[^1];
    }

    /// <summary>The class of the road a junction path leads onto (its own is the junction's): the road leaving its far end.</summary>
    private RoadClass ExitClass(LaneEdge path)
    {
        foreach (var (next, _) in _roads!.Leaving(path.KeyEnd))
            if (next.Path is null && !next.Connector) return next.Class;
        return path.Class;
    }

    /// <summary>
    /// Which way a road leaving a junction takes a car (#353), as <see cref="WayPast"/> does along a
    /// route: across a connector between trimmed ends, onto the road it leads to.
    /// </summary>
    private Vector3 WayInto(Vector3 junction, (LaneEdge Edge, bool Forward) option)
    {
        var (edge, forward) = option;
        if (edge.Connector)
        {
            long far = forward ? edge.KeyEnd : edge.KeyStart;
            foreach (var (next, nf) in _roads!.Leaving(far))
                if (next != edge && !next.Connector) return Into(junction, next, nf);
        }
        return Into(junction, edge, forward);
    }

    // ---- trains --------------------------------------------------------------------------

    private void SpawnTrain(Vector3 focus)
    {
        if (_rails!.RandomSpot(_rng, focus, TrainSpawnMin, TrainSpawnMax, _ => 1f) is not var (edge, arc)) return;
        SpawnTrainOn(edge, arc);
    }

    /// <summary>
    /// <c>--trafficcheck --crossing</c> (#124): a train with its head on the rail nearest
    /// <paramref name="at"/>, so the whole train rolls over that point. False before the rails load.
    /// </summary>
    public bool SpawnTrainAt(Vector3 at)
    {
        if (_rails == null || _rails.Edges.Count == 0) return false;
        var (edge, arc) = _rails.Edges
            .SelectMany(e => Enumerable.Range(0, (int)(e.Length / 0.5f) + 1).Select(k => (e, s: k * 0.5f)))
            .MinBy(x => x.e.Sample(x.s).Pos.DistanceTo(at));
        SpawnTrainOn(edge, arc);
        return true;
    }

    public IEnumerable<Vector3> TrainUnits => _trains.SelectMany(t => t.Units.Select(u => u.GlobalPosition));

    private void SpawnTrainOn(LaneEdge edge, float arc)
    {
        bool narrow = (edge.Flags & RoadFlags.NarrowGauge) != 0;
        bool forward = _rng.Next(2) == 0;
        var route = new Route(edge, forward, forward ? arc : edge.Length - arc);

        // SBB red and white on standard gauge; the mountain railways' red on metre gauge
        var paint = narrow ? new Color(0.72f, 0.12f, 0.12f) : new Color(0.9f, 0.9f, 0.9f);
        var band = narrow ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.78f, 0.1f, 0.1f);
        float length = narrow ? 17f : 24f, gap = length + 0.8f;
        int count = narrow ? 3 : 2 + _rng.Next(4);
        var offsets = new float[count];
        var units = new Node3D[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = length * 0.5f + i * gap;
            var (body, lamps) = TrafficMeshBuilder.Carriage(i == 0 ? new Color(0.78f, 0.1f, 0.1f) : paint,
                i == 0 ? new Color(0.95f, 0.95f, 0.95f) : band, length, narrow, i == 0, i == count - 1);
            units[i] = Unit(body, lamps);
            units[i].Name = $"Train{i}";   // so a knock can tell a train from a car
        }
        float speed = (edge.Flags & RoadFlags.RackRailway) != 0 ? 7f : narrow ? 16f : 30f;
        var v = new Vehicle(route, speed, offsets, units)
        {
            Lift = 0.2f,
            Offset = RoadFormat.TrackOffset(edge.Flags),
        };
        AddVehicle(v);
        _trains.Add(v);
    }

    private void StepTrain(Vehicle train, float dt)
    {
        var (_, dir) = train.Route.At(0);
        var (_, aheadDir) = train.Route.At(-40f);
        float turn = Mathf.Acos(Mathf.Clamp(dir.Dot(aheadDir), -1f, 1f));
        float target = train.Cruise;
        if (turn > 0.05f) target = Mathf.Min(target, Mathf.Sqrt(1.2f * 40f / turn));
        train.Speed = Mathf.MoveToward(train.Speed, target, 0.8f * dt);

        // the end of the loaded railway: the train leaves the world there
        if (!train.Route.Advance(train.Speed * dt, NextRail)) train.Stuck += 99f;
        train.Route.Trim(train.Offsets[^1] + 30f);
        train.Place(_ => train.Offset);
        for (int k = 0; k < PathSteps; k++) train.Path[k] = train.Route.At(-train.Speed * 0.5f * (k + 1)).Pos;
    }

    // a train cannot take a set of points backwards: only lines that carry straight on (the rule on any graph, trams too: #1060)
    private (LaneEdge, bool)? NextRail((LaneEdge Edge, bool Forward) leg) => NextTrack(_rails!, leg);

    // ---- bodies --------------------------------------------------------------------------

    /// <summary>
    /// One solid unit. Its box is the mesh's own bounds: the hand-typed ones stood 15 cm over a
    /// car, 20 cm over a van and 30 cm short of a carriage roof.
    /// </summary>
    private Node3D Unit(ArrayMesh body, ArrayMesh lamps, float drawn = 0f)
    {
        var node = new AnimatableBody3D { SyncToPhysics = false };
        node.AddChild(new MeshInstance3D { Mesh = body, MaterialOverride = _bodyMaterial, VisibilityRangeEnd = drawn });
        node.AddChild(new MeshInstance3D { Mesh = lamps, MaterialOverride = _lampMaterial, VisibilityRangeEnd = drawn });
        var box = body.GetAabb().Merge(lamps.GetAabb());
        node.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size }, Position = box.GetCenter() });
        return node;
    }

    private void AddVehicle(Vehicle v)
    {
        foreach (var u in v.Units) { AddChild(u); _byBody[u.GetInstanceId()] = v; }
        v.Place(_ => v.Offset);
    }

    /// <summary>Clears all traffic — after a teleport, where everything is suddenly far away.</summary>
    public void Clear()
    {
        foreach (var v in _cars.Concat(_trains)) v.Free();
        _byBody.Clear();
        _cars.Clear();
        _generatedLive = 0;
        _trains.Clear();
        ClearMovers();
        _builtAround = null;
    }

    /// <summary>Removes the cars within <paramref name="radius"/> m of a point: a race grid is not lined up in the middle of the traffic.</summary>
    public void ClearAround(Vector3 at, float radius)
    {
        for (int i = _cars.Count - 1; i >= 0; i--)
        {
            if (MathX.Flat(_cars[i].Head - at).Length() >= radius) continue;
            foreach (var u in _cars[i].Units) _byBody.Remove(u.GetInstanceId());
            if (_cars[i].FromEndpoint) _generatedLive--;
            _cars[i].Free();
            _cars.RemoveAt(i);
        }
    }

    public LaneGraph? Roads => _roads;

    /// <summary>Head position and heading of a car on the biggest road there is, or of a train.</summary>
    public (Vector3 Pos, Vector3 Dir)? Watch(bool train)
    {
        var list = train ? _trains : _cars;
        var v = list.OrderBy(c => (int)c.Route.Edge.Class).FirstOrDefault();
        if (v == null) return null;
        var (_, dir) = v.Route.At(0);
        return (v.Head, dir);
    }
    public float AverageCarSpeed => _cars.Count == 0 ? 0f : _cars.Average(c => c.Speed);
    public float AverageTrainSpeed => _trains.Count == 0 ? 0f : _trains.Average(c => c.Speed);
    public int CarCount => _cars.Count;
    /// <summary>Cars driving an edge against its stored or inferred one-way direction (must stay 0).</summary>
    public int WrongWayCars => _cars.Count(c => c.Route.Edge.OneWay != 0 && c.Route.Forward != c.Route.Edge.OneWay > 0);
    public int OneWayEdges => _roads?.Edges.Count(e => e.OneWay != 0) ?? 0;
    public int TrainCount => _trains.Count;

    /// <summary>One car, or one train of several units, riding one route.</summary>
    private sealed class Vehicle
    {
        public Route Route;
        public readonly float Cruise;
        /// <summary>Distance behind the head of each unit's centre.</summary>
        public readonly float[] Offsets;
        public readonly Node3D[] Units;
        public float Speed;
        public float Stuck;
        public bool GivingWay;
        /// <summary>Stopping for a red light (#353), clearing a yellow, has stopped for this red; waited to turn left across oncoming traffic, waited for room past the junction.</summary>
        public bool AtRed, ClearingAmber, WasAtRed, WaitedPermissive, WaitedRoom;
        /// <summary>Waiting at its line on green for room past the junction (#1134, the box rule): not stuck, and the cars behind it queue.</summary>
        public bool RoomHeld;
        /// <summary>Waiting before a one-carriageway stretch while one comes the other way (#1134): not stuck, the cars behind queue.</summary>
        public bool NarrowHeld;
        /// <summary>Passing one coming the other way on a narrow carriageway, moved over at a crawl (#1180).</summary>
        public bool Squeezing;
        /// <summary>How long it has stood body on body with another standing car (s, #1134).</summary>
        public float Overlap;
        public Vehicle? OverlapWith;
        /// <summary>The lane change under way (m right of its line; NaN none) and the metres driven since the last began (#1134).</summary>
        public float StepTarget = float.NaN, SinceStep = 1000f;
        /// <summary>How long it has stood at its line on green, and whether that was logged (#1134, the J1 diagnosis).</summary>
        public float GreenStand;
        public bool GreenStandLogged, ContactLogged;
        /// <summary>
        /// Held for a gap on a junction path (#1046): past its line, short of a point in the junction; at its line behind the cars
        /// waiting there; the path, and how long it has stood in the junction (s). How long it has been let go standing (s).
        /// </summary>
        public bool GapWaiting, GapHeld;
        public JunctionPathInfo? GapPath;
        public float GapFor, Starting;
        /// <summary>
        /// The approach its route leads into (#353), metres to its stop line (+∞ none), the turn it makes
        /// there, its lane and signal group (-1 none); and where it drives: metres right of its usual line, and the target.
        /// </summary>
        public LaneApproach? Approach;
        public float ToLine = float.MaxValue;
        public SignalMoves Turn;
        public int LaneIndex = -1, Group = -1;
        public JunctionPathInfo? PathAhead;
        /// <summary>The transition lane it follows (#818 phase 5: the strip's id, stable along the arm), -1 none.</summary>
        public int Strip = -1;
        public float Lane, LaneWanted;
        /// <summary>The lane it is heading for at the line, from its usual line (#854): <see cref="LaneWanted"/> before it eases over.</summary>
        public float LaneAim;
        /// <summary>A generated car's way to its destination (#971): the legs it takes, in order; null picks at random.</summary>
        public List<(LaneEdge, bool)>? Planned;
        /// <summary>Made by a traffic endpoint (#971): counted against <see cref="Traffic.MaxGenerated"/>.</summary>
        public bool FromEndpoint;
        /// <summary>Standing right behind a car that waits (at a red, for a gap, queued itself): not stuck (#1134).</summary>
        public bool Queued;
        public int FromId;
        /// <summary>The destination a generated car is on its way to (#971), 0 none.</summary>
        public int ToId;
        /// <summary>A fed car's move at one approach (#854, <c>--feed-turns</c>): the path it takes there; none, as the traffic picks.</summary>
        public (LaneApproach At, SignalMoves Move)? Wants;
        /// <summary>Placed on its lane at its first step.</summary>
        public bool Placed;
        public float Lift;
        /// <summary>Half the distance between a unit's front and rear bogies (m); 0: a car's 1.4, a carriage's 8 (#1060: a tram section's own).</summary>
        public float UnitHalf;
        public float Offset;
        public Vector3 Head;
        /// <summary>World velocity of the head (from where it was last step), for a racer's sensing.</summary>
        public Vector3 Vel;
        /// <summary>How far it has pulled over past its usual keep-right, m.</summary>
        public float Pull;
        /// <summary>Seconds standing behind something standing in its lane.</summary>
        public float Stale;
        /// <summary>Waiting short of a junction for someone to pass; making way for someone (see <see cref="Traffic.Yielding"/>).</summary>
        public bool Holding, Yield;
        public readonly Vector3[] Path = new Vector3[PathSteps];
        /// <summary>The driver: reaction time (s), how long it has had someone in view, when it last saw
        /// them, when it looks again, and a fright (s left, and whether it brakes in it).</summary>
        public float Reaction = 0.75f, Alert, SawAgo = 99f, LookIn, Startle;
        /// <summary>When it was spawned (ms): a car met just after it appeared (#159 logs).</summary>
        public readonly ulong Born = Time.GetTicksMsec();
        /// <summary>The car's own roll: which lane it takes on a road with several in its direction (#700).</summary>
        public uint Habit;
        public bool StartleBrake;

        /// <summary>A search key for <see cref="Traffic._byX"/>: a vehicle that is only a position east-west.</summary>
        public Vehicle(float x)
        {
            Route = null!;
            Offsets = Array.Empty<float>();
            Units = Array.Empty<Node3D>();
            Head = new Vector3(x, 0, 0);
        }

        public Vehicle(Route route, float cruise, float[] offsets, Node3D[] units)
        {
            Route = route;
            Cruise = cruise;
            Speed = cruise;
            Offsets = offsets;
            Units = units;
        }

        /// <summary>
        /// Puts every unit on the route, square to its own stretch of it (front and rear
        /// bogies, so a carriage in a curve is a chord, not a tangent) and pushed right of the
        /// centreline by <paramref name="right"/>.
        /// </summary>
        public void Place(Func<LaneEdge, float> right)
        {
            float lateral = right(Route.Edge);
            for (int i = 0; i < Units.Length; i++)
            {
                float half = UnitHalf > 0f ? UnitHalf : i == 0 && Units.Length == 1 ? 1.4f : 8f;
                // each end on its own leg's line, at the offset that line has there: a junction path's line starts and ends on a
                // lane, the road's is its centre, so across the join the offset changes by that lane's (#854: a car leaving a
                // path turned across the exit while its front was on the road and its rear still on the path)
                var (front, frontDir, frontLeg) = Route.AtLeg(Offsets[i] - half);
                var (rear, rearDir, rearLeg) = Route.AtLeg(Offsets[i] + half);
                front += Side(frontDir) * OffsetOn(frontLeg, lateral);
                rear += Side(rearDir) * OffsetOn(rearLeg, lateral);
                // a car's rear follows where its front went, as its rear wheels do: a lane change or a road widening turns it
                // instead of sliding it sideways (#854, the user)
                if (Trail is not null && i == 0 && FollowTrail(front, 2f * half) is { } behind) rear = behind;
                var dir = front - rear;
                if (dir.LengthSquared() < 1e-4f) (_, dir) = Route.At(Offsets[i]);
                dir = dir.Normalized();
                var at = (front + rear) * 0.5f + Vector3.Up * Lift;
                // MeshScratch turns the meshes to face -Z, the node's forward: point -Z along travel
                var basis = Player.Flyer.Orient(dir, Vector3.Up, Vector3.Forward);
                Units[i].GlobalTransform = new Transform3D(basis, at);
                if (i == 0) Head = at;
            }
        }

        private static Vector3 Side(Vector3 dir) => new Vector3(-dir.Z, 0, dir.X).Normalized();

        /// <summary>A car's front, every <see cref="TrailStep"/> m of travel, newest last (a ring); null for a train.</summary>
        public Vector3[]? Trail;
        private int _trailCount, _trailNext;
        private const float TrailStep = 0.25f;

        /// <summary>Forgets the trail: the car was put somewhere new (turned round, spawned).</summary>
        public void ClearTrail() => _trailCount = 0;

        /// <summary>Moves the trail with the world origin.</summary>
        public void ShiftTrail(Core.OriginShift shift)
        {
            if (Trail is null) return;
            for (int k = 0; k < Trail.Length; k++) Trail[k] = shift.Point(Trail[k]);
        }

        /// <summary>
        /// Notes <paramref name="front"/> on the trail and returns the point <paramref name="length"/> m back along it, where the
        /// rear goes; null while the trail is shorter (just spawned or turned round: the rear goes by the route).
        /// </summary>
        private Vector3? FollowTrail(Vector3 front, float length)
        {
            var trail = Trail!;
            int last = (_trailNext - 1 + trail.Length) % trail.Length;
            // a jump (put somewhere new, a step across a join) starts it again
            if (_trailCount > 0 && (front - trail[last]).LengthSquared() > 3f * 3f) _trailCount = 0;
            if (_trailCount == 0 || (front - trail[last]).LengthSquared() >= TrailStep * TrailStep)
            {
                trail[_trailNext] = front;
                _trailNext = (_trailNext + 1) % trail.Length;
                _trailCount = Math.Min(_trailCount + 1, trail.Length);
            }
            var at = front;
            float left = length;
            for (int k = 1; k <= _trailCount; k++)
            {
                var p = trail[(_trailNext - k + 2 * trail.Length) % trail.Length];
                float d = (p - at).Length();
                if (d >= left && d > 1e-4f) return at + (p - at) * (left / d);
                left -= d;
                at = p;
            }
            return null;
        }

        /// <summary>
        /// The car's offset (<paramref name="lateral"/>, from the current leg's line) on leg <paramref name="leg"/>, next to it: a path
        /// starts <see cref="JunctionPathInfo.FromOffset"/> right of the road's line arriving, and ends on the exit at
        /// -<see cref="JunctionPathInfo.ToOffset"/> right of a driver leaving.
        /// </summary>
        private float OffsetOn(int leg, float lateral)
        {
            if (leg == Route.Leg) return lateral;
            var here = Route.Legs[Route.Leg].Edge;
            var there = Route.Legs[leg].Edge;
            if (leg < Route.Leg)
            {
                // behind: the road before a path, or the path before a road
                if (here.Path is { } onto && there.Path is null) return lateral + onto.FromOffset;
                if (there.Path is { } off && here.Path is null) return lateral + off.ToOffset;
            }
            else
            {
                // ahead: the path after a road, or the road after a path
                if (there.Path is { } onto && here.Path is null) return lateral - onto.FromOffset;
                if (here.Path is { } off && there.Path is null) return lateral - off.ToOffset;
            }
            return lateral;
        }

        public void Free()
        {
            foreach (var u in Units) u.QueueFree();
        }
    }
}
