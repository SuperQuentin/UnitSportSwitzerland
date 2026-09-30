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

    public (Vector3 Pos, Vector3 Dir) At(float back)
    {
        int i = Leg;
        float s = Arc - back;
        while (s < 0 && i > 0) { i--; s += Legs[i].Edge.Length; }
        s = Mathf.Max(0f, s);
        var (edge, fwd) = Legs[i];
        var (p, t) = edge.Sample(fwd ? s : edge.Length - s);
        return (p, fwd ? t : -t);
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
public partial class Traffic : Node3D
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    /// <summary>Where the player is: traffic lives around this point.</summary>
    public Func<Vector3?>? Focus { get; set; }

    /// <summary>Bodies cars must brake for (the player, their vehicle).</summary>
    public Func<IEnumerable<Vector3>>? Obstacles { get; set; }

    private LaneGraph? _roads, _rails;
    private TileId? _builtAround;
    private bool _building;
    private readonly Random _rng = new(20260929);
    private readonly List<Vehicle> _cars = new(), _trains = new();
    private double _spawnTimer;
    private Material? _bodyMaterial, _lampMaterial;

    private const float CarNear = 70f, CarSpawnMin = 180f, CarSpawnMax = 750f, CarDespawn = 950f;
    private const float TrainSpawnMin = 700f, TrainSpawnMax = 2500f, TrainDespawn = 3200f;

    public Traffic(ChunkManager chunks, WorldOrigin origin)
    {
        Name = "Traffic";
        _chunks = chunks;
        _origin = origin;
    }

    public override void _Ready()
    {
        _bodyMaterial = HumanMeshBuilder.Material();
        _lampMaterial = TrafficMeshBuilder.LampMaterial();
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
        _cars.Clear();
        _trains.Clear();
        _roads = _rails = null;
        _builtAround = null;
        _building = false;
    }

    private void RebuildIfMoved(Vector3 focus)
    {
        if (_building || _chunks.Source is not { } source) return;
        int epoch = _epoch;
        var (e, n) = _origin.ToLv95(focus);
        var here = TileId.FromLv95(e, n);
        if (_builtAround is { } b && b.E == here.E && b.N == here.N) return;
        _building = true;
        var origin = _origin;

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
            var near = tiles.Where(t => Math.Abs(t.Id.E - here.E) <= 2 && Math.Abs(t.Id.N - here.N) <= 2);
            var roads = LaneGraph.Build(near, origin, IsCarRoad);
            var rails = LaneGraph.Build(tiles, origin, IsRail);
            int divided = roads.Edges.Count(x => (x.Flags & RoadFlags.Divided) != 0);
            int oriented = roads.Edges.Count(x => (x.Flags & RoadFlags.Divided) != 0 && x.OneWay != 0);
            GD.Print($"[traffic] around {here}: {roads.Edges.Count} road edges ({oriented}/{divided} divided "
                + $"carriageways oriented), {rails.Edges.Count} rail edges");
            Callable.From(() =>
            {
                if (epoch != _epoch) return;   // built from the world that was replaced
                _roads = roads;
                _rails = rails;
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

        _spawnTimer -= dt;
        if (_spawnTimer <= 0)
        {
            _spawnTimer = 0.25;
            // first fill anywhere out of arm's reach; after that only out of sight range
            bool filling = _cars.Count < wantCars / 2;
            if (_cars.Count < wantCars) SpawnCar(focus, filling ? CarNear : CarSpawnMin);
            if (_trains.Count < wantTrains) SpawnTrain(focus);
        }

        var obstacles = Obstacles?.Invoke().ToList() ?? new List<Vector3>();
        foreach (var car in _cars) StepCar(car, dt, obstacles);
        foreach (var train in _trains) StepTrain(train, dt);

        Cull(_cars, focus, CarDespawn, wantCars);
        Cull(_trains, focus, TrainDespawn, wantTrains);
    }

    private void Cull(List<Vehicle> list, Vector3 focus, float range, int want)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var v = list[i];
            float d = new Vector2(v.Head.X - focus.X, v.Head.Z - focus.Z).Length();
            if (d > range || v.Stuck > 20f || (list.Count > want && d > range * 0.6f))
            {
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
    private static float CarWeight(LaneEdge e) => e.Class switch
    {
        RoadClass.Motorway or RoadClass.Expressway => 1f, RoadClass.Major => 0.9f,
        RoadClass.Road => 0.7f, RoadClass.Ramp => 0.5f, RoadClass.Minor => 0.35f, _ => 0.12f,
    };

    private void SpawnCar(Vector3 focus, float minDist)
    {
        if (_roads!.RandomSpot(_rng, focus, minDist, CarSpawnMax, CarWeight) is not var (edge, arc)) return;
        bool forward = edge.OneWay switch { 1 => true, -1 => false, _ => _rng.Next(2) == 0 };
        var route = new Route(edge, forward, forward ? arc : edge.Length - arc);
        bool van = _rng.NextDouble() < 0.18;
        var (body, lamps) = TrafficMeshBuilder.Car(TrafficMeshBuilder.Paints[_rng.Next(TrafficMeshBuilder.Paints.Length)], van);
        var v = new Vehicle(route, CruiseSpeed(edge.Class) * 0.8f, new[] { 0f },
            new[] { Unit(body, lamps) });
        AddVehicle(v);
        _cars.Add(v);
    }

    private void StepCar(Vehicle car, float dt, List<Vector3> obstacles)
    {
        var (pos, dir) = car.Route.At(0);
        float target = CruiseSpeed(car.Route.Edge.Class);

        // slow for the bend ahead: lateral acceleration kept to ~2.5 m/s²
        var (ahead, aheadDir) = car.Route.At(-18f);
        float turn = Mathf.Acos(Mathf.Clamp(dir.Dot(aheadDir), -1f, 1f));
        if (turn > 0.05f) target = Mathf.Min(target, Mathf.Sqrt(2.5f * 18f / turn));

        // keep a gap to anything in the lane ahead: other cars, and the player
        foreach (var other in _cars)
        {
            if (other == car) continue;
            var d = other.Head - pos;
            float along = d.Dot(dir);
            if (along < 1f || along > 40f || (d - dir * along).Length() > 2.2f) continue;
            target = Mathf.Min(target, Mathf.Max(0f, other.Speed + (along - 9f) * 0.6f));
        }
        foreach (var o in obstacles)
        {
            var d = o - pos;
            float along = d.Dot(dir);
            // in its lane, not merely on the road: yielding to anything within 2.6 m of its lane
            // stopped it for a racer passing on the other half, the racer stopped for it, and the
            // two waited for each other for good (#52)
            if (along < 0.5f || along > 30f || (d - dir * along).Length() > 1.8f) continue;
            target = Mathf.Min(target, Mathf.Max(0f, (along - 6f) * 0.7f));
        }

        float accel = target > car.Speed ? 2.2f : 6f;
        car.Speed = Mathf.MoveToward(car.Speed, target, accel * dt);
        car.Stuck = car.Speed < 0.3f ? car.Stuck + dt : 0f;

        if (!car.Route.Advance(car.Speed * dt, leg => NextRoad(leg))) car.Stuck += 5f;
        car.Route.Trim(10f);
        car.Place(KeepRight);
    }

    /// <summary>Right-hand traffic: an undivided road is shared, so each car keeps to its half.</summary>
    private static float KeepRight(LaneEdge e) =>
        (e.Flags & RoadFlags.Divided) != 0 || e.Class == RoadClass.Ramp ? 0f
        : e.Width < 4.5f ? 0.3f : e.Width * 0.25f;

    private (LaneEdge, bool)? NextRoad((LaneEdge Edge, bool Forward) leg)
    {
        long key = leg.Forward ? leg.Edge.KeyEnd : leg.Edge.KeyStart;
        var options = _roads!.Leaving(key).Where(o => o.Edge != leg.Edge).ToList();
        if (options.Count == 0)
        {
            // a dead end — a real cul-de-sac, or the edge of what is loaded: turn round
            if (leg.Edge.OneWay != 0) return null;
            return (leg.Edge, !leg.Forward);
        }
        // mostly stay on roads of the same size; a motorway rarely turns off into a lane
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

    // ---- trains --------------------------------------------------------------------------

    private void SpawnTrain(Vector3 focus)
    {
        if (_rails!.RandomSpot(_rng, focus, TrainSpawnMin, TrainSpawnMax, _ => 1f) is not var (edge, arc)) return;
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
    }

    private (LaneEdge, bool)? NextRail((LaneEdge Edge, bool Forward) leg)
    {
        long key = leg.Forward ? leg.Edge.KeyEnd : leg.Edge.KeyStart;
        var (_, dir) = leg.Edge.Sample(leg.Forward ? leg.Edge.Length : 0f);
        if (!leg.Forward) dir = -dir;
        // a train cannot take a set of points backwards: only lines that carry straight on
        var options = _rails!.Leaving(key).Where(o => o.Edge != leg.Edge)
            .Select(o =>
            {
                var (_, t) = o.Edge.Sample(o.Forward ? 0f : o.Edge.Length);
                return (o, straight: (o.Forward ? t : -t).Dot(dir));
            })
            .Where(x => x.straight > 0.5f).ToList();
        if (options.Count == 0) return null;
        var pick = options[_rng.Next(options.Count)].o;
        return (pick.Edge, pick.Forward);
    }

    // ---- bodies --------------------------------------------------------------------------

    /// <summary>
    /// One solid unit. Its box is the mesh's own bounds: the hand-typed ones stood 15 cm over a
    /// car, 20 cm over a van and 30 cm short of a carriage roof.
    /// </summary>
    private Node3D Unit(ArrayMesh body, ArrayMesh lamps)
    {
        var node = new AnimatableBody3D { SyncToPhysics = false };
        node.AddChild(new MeshInstance3D { Mesh = body, MaterialOverride = _bodyMaterial });
        node.AddChild(new MeshInstance3D { Mesh = lamps, MaterialOverride = _lampMaterial });
        var box = body.GetAabb().Merge(lamps.GetAabb());
        node.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size }, Position = box.GetCenter() });
        return node;
    }

    private void AddVehicle(Vehicle v)
    {
        foreach (var u in v.Units) AddChild(u);
        v.Place(_ => v.Offset);
    }

    /// <summary>Clears all traffic — after a teleport, where everything is suddenly far away.</summary>
    public void Clear()
    {
        foreach (var v in _cars.Concat(_trains)) v.Free();
        _cars.Clear();
        _trains.Clear();
        _builtAround = null;
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
    public int TrainCount => _trains.Count;

    /// <summary>One car, or one train of several units, riding one route.</summary>
    private sealed class Vehicle
    {
        public readonly Route Route;
        public readonly float Cruise;
        /// <summary>Distance behind the head of each unit's centre.</summary>
        public readonly float[] Offsets;
        public readonly Node3D[] Units;
        public float Speed;
        public float Stuck;
        public float Lift;
        public float Offset;
        public Vector3 Head;

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
                float half = i == 0 && Units.Length == 1 ? 1.4f : 8f;
                var (front, _) = Route.At(Offsets[i] - half);
                var (rear, _) = Route.At(Offsets[i] + half);
                var dir = front - rear;
                if (dir.LengthSquared() < 1e-4f) (_, dir) = Route.At(Offsets[i]);
                dir = dir.Normalized();
                var side = new Vector3(-dir.Z, 0, dir.X).Normalized();
                var at = (front + rear) * 0.5f + side * lateral + Vector3.Up * Lift;
                // MeshScratch turns the meshes to face -Z, the node's forward: point -Z along travel
                var basis = Player.Flyer.Orient(dir, Vector3.Up, Vector3.Forward);
                Units[i].GlobalTransform = new Transform3D(basis, at);
                if (i == 0) Head = at;
            }
        }

        public void Free()
        {
            foreach (var u in Units) u.QueueFree();
        }
    }
}
