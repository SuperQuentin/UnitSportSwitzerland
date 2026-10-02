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
        int i = Leg;
        float s = Arc - back;
        while (s < 0 && i > 0) { i--; s += Legs[i].Edge.Length; }
        while (s > Legs[i].Edge.Length && i < Legs.Count - 1) { s -= Legs[i].Edge.Length; i++; }
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
    public override void _EnterTree() => Current = this;
    public override void _ExitTree() { if (Current == this) Current = null; }

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
    private readonly Random _rng = new(20260929);
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
        foreach (var v in _cars.Concat(_trains))
        {
            // a vehicle may still be on an edge of an older graph
            foreach (var (edge, _) in v.Route.Legs)
                if (done.Add(edge)) edge.Shift(shift);
            v.Head = shift.Point(v.Head);
            v.Vel = shift.Direction(v.Vel);
            for (int i = 0; i < v.Path.Length; i++) v.Path[i] = shift.Point(v.Path[i]);
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
            var near = tiles.Where(t => Math.Abs(t.Id.E - here.E) <= 2 && Math.Abs(t.Id.N - here.N) <= 2);
            var roads = LaneGraph.Build(near, origin, IsCarRoad);
            int deadBefore = roads.DeadEnds;
            roads.JoinTrimmedEnds();
            var rails = LaneGraph.Build(tiles, origin, IsRail);
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
                }
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
            if (_cars.Count < wantCars) SpawnCar(focus, filling ? CarNear : CarSpawnMin, Obstacles?.Invoke());
            if (_trains.Count < wantTrains) SpawnTrain(focus);
        }

        // one list for the traffic's life, refilled each tick (#221)
        var obstacles = _obstacles;
        obstacles.Clear();
        if (Obstacles?.Invoke() is { } seen) obstacles.AddRange(seen);
        _byX.Clear();
        _byX.AddRange(_cars);
        _byX.Sort(ByX);
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
                foreach (var u in v.Units) _byBody.Remove(u.GetInstanceId());
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

    private void SpawnCar(Vector3 focus, float minDist, IEnumerable<(Vector3 Pos, Vector3 Vel)>? obstacles)
    {
        if (_roads!.RandomSpot(_rng, focus, minDist, CarSpawnMax, CarWeight) is not var (edge, arc)) return;
        // never out of thin air beside a player: a car filled in 5 m in front of a racer is a crash. 60 m
        // and the 4 s a fast one covers: at 150 km/h 60 m is 1.4 s, less than a racer needs to see it and stop (#159)
        var (spot, _) = edge.Sample(arc);
        if (obstacles != null && obstacles.Any(o => new Vector2(o.Pos.X - spot.X, o.Pos.Z - spot.Z).Length() < 60f + 4f * new Vector2(o.Vel.X, o.Vel.Z).Length())) return;
        bool forward = edge.OneWay switch { 1 => true, -1 => false, _ => _rng.Next(2) == 0 };
        var route = new Route(edge, forward, forward ? arc : edge.Length - arc);
        bool van = _rng.NextDouble() < 0.18;
        var (body, lamps) = TrafficMeshBuilder.Car(TrafficMeshBuilder.Paints[_rng.Next(TrafficMeshBuilder.Paints.Length)], van);
        var v = new Vehicle(route, CruiseSpeed(edge.Class) * 0.8f, new[] { 0f },
            new[] { Unit(body, lamps, CarDrawn) }) { Reaction = 0.5f + 0.5f * (float)_rng.NextDouble() };
        AddVehicle(v);
        _cars.Add(v);
    }

    /// <summary>Braking a traffic driver plans with, m/s²: the speed from which it stops within <paramref name="gap"/> m.</summary>
    private static float StopWithin(float gap) => Mathf.Sqrt(2f * 4f * Mathf.Max(0f, gap));

    /// <summary>
    /// The cars by east-west position, for the gap to the car ahead: a car looks only at those
    /// within <see cref="GapWindow"/> m of it that way instead of at every car (O(n²) at traffic
    /// 150, #221). The window is the 40 m it looks ahead with room for a tick's travel.
    /// </summary>
    private readonly List<Vehicle> _byX = new();
    private static readonly Comparer<Vehicle> ByX = Comparer<Vehicle>.Create((a, b) => a.Head.X.CompareTo(b.Head.X));
    private const float GapWindow = 45f;
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
        car.Route.Plan(130f, NextRoad);
        var (pos, dir) = car.Route.At(0);
        var edge = car.Route.Edge;
        float cruise = CruiseSpeed(edge.Class);
        float target = cruise;

        // slow for the bend ahead: lateral acceleration kept to ~2.5 m/s²
        var (_, aheadDir) = car.Route.At(-18f);
        float turn = Mathf.Acos(Mathf.Clamp(dir.Dot(aheadDir), -1f, 1f));
        if (turn > 0.05f) target = Mathf.Min(target, Mathf.Sqrt(2.5f * 18f / turn));

        // keep a gap to the car ahead in its lane, going its way. Measured from the centreline, a car
        // coming the other way in the other lane (1.5 m over) counted as "in the lane": two met nose to
        // nose, stopped for each other for good and blocked the whole road — racers stopped behind
        // both (#85)
        // only the cars within reach east-west (sorted at the start of the tick; none moves a metre in one)
        _xKey.Head.X = car.Head.X - GapWindow;
        int from = _byX.BinarySearch(_xKey, ByX);
        for (int o = from < 0 ? ~from : from; o < _byX.Count && _byX[o].Head.X <= car.Head.X + GapWindow; o++)
        {
            var other = _byX[o];
            if (other == car) continue;
            var d = other.Head - car.Head;
            float along = d.Dot(dir);
            if (along < 1f || along > 40f || (d - dir * along).Length() > 1.6f) continue;
            if (other.Route.At(0).Dir.Dot(dir) < 0.3f) continue;
            target = Mathf.Min(target, Mathf.Max(0f, other.Speed + (along - 9f) * 0.6f));
        }

        float half = edge.Width * 0.5f, keep = KeepRight(edge);
        bool twoWay = (edge.Flags & RoadFlags.Divided) == 0 && edge.OneWay == 0;
        // a lane of its own each way is 7.5 m of road; on less, meeting means someone makes room
        bool narrow = twoWay && edge.Width < 7.5f;
        // how far over it can go: its body (0.95 m) on the edge of the tarmac
        float maxPull = twoWay ? Mathf.Max(0f, half - 0.95f - keep) : 0f;
        float mine = keep + car.Pull;   // this car's centre, right of the centreline
        float wantPull = 0f, makeWay = float.MaxValue;
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
                if (Flat(oPos - _roadPos[k]).LengthSquared() < Flat(oPos - _roadPos[best]).LengthSquared()) best = k;
            var t = _roadDir[best];
            var side = new Vector3(-t.Z, 0, t.X).Normalized();
            var r = Flat(oPos - _roadPos[best]);
            float along = 4f * (best - Behind) + r.Dot(t), lat = r.Dot(side);
            float ov = oVel.X * t.X + oVel.Z * t.Z;   // its speed along this car's way
            // on this road (not past either end of what was sampled) and not on a bridge over it
            bool onRoad = r.Length() < half + 2.5f && Mathf.Abs(oPos.Y - _roadPos[best].Y) < 4f;

            // whatever the road says, never drive into a body: anything in front of its own nose, within
            // a car's width of where it actually is (a kinematic box shoves a car it drives into)
            var nose = Flat(oPos - car.Head);
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
            var toJ = Flat(j - oPos);
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
        car.Speed = Mathf.MoveToward(car.Speed, target, accel * dt);
        // waiting for a race is not stuck: a car dropped after 20 s of it vanished in front of the racers
        car.Stuck = car.Speed < 0.3f && !stopFor && !hold ? car.Stuck + dt : 0f;
        car.Holding = hold;
        car.Yield = yield;
        car.Stale = blocked && car.Speed < 0.3f ? car.Stale + dt : 0f;
        if (car.Stale > 5f && twoWay)
        {
            car.Stale = 0f;
            car.Route = new Route(edge, !car.Route.Forward, edge.Length - car.Route.Arc);
        }
        car.Pull = Mathf.MoveToward(car.Pull, Mathf.Min(wantPull, maxPull), (car.Startle > 0f ? 2.2f : 0.8f) * dt);

        var before = car.Head;
        if (!car.Route.Advance(car.Speed * dt, leg => NextRoad(leg))) car.Stuck += 5f;
        car.Route.Trim(90f);   // the road behind it too: whoever is closing from behind is found on it
        float pull = car.Startle > 0f ? Mathf.Clamp(car.Pull + 0.12f * Mathf.Sin(car.Startle * 14f), 0f, maxPull) : car.Pull;
        car.Place(e => KeepRight(e) + pull);
        car.Vel = dt > 0f ? Flat(car.Head - before) / dt : Vector3.Zero;
        // where its lane takes it, 0.5 s apart at this speed: a racer reads it to see it coming out of a side road
        for (int k = 0; k < PathSteps; k++) car.Path[k] = car.Route.At(-car.Speed * 0.5f * (k + 1)).Pos;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

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
        float remaining = edge.Length - car.Route.Arc;
        if (remaining > YieldLookAhead) return float.MaxValue;
        var junction = forward ? edge.Points[^1] : edge.Points[0];

        bool conflict = false;
        foreach (var o in obstacles)
            if (FlatLength(o.Pos - junction) < 8f) { conflict = true; break; }
        foreach (var other in _cars)
        {
            if (conflict) break;
            if (other == car || IsWaiting(other, junction)) continue;
            var to = junction - other.Head;
            float d = FlatLength(to);
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

    /// <summary>A car itself on a yielding approach to that junction (waiting at its own line or about to).</summary>
    private static bool IsWaiting(Vehicle v, Vector3 junction)
    {
        var (edge, forward) = v.Route.Legs[v.Route.Leg];
        if ((edge.Yield & (forward ? RoadAttrFlags.YieldAtEnd : RoadAttrFlags.YieldAtStart)) == 0) return false;
        var end = forward ? edge.Points[^1] : edge.Points[0];
        return FlatLength(end - junction) < 30f;
    }

    private static float FlatLength(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>Cars stopped or slowing at a side road's Wartelinie for main-road traffic right now.</summary>
    public int GivingWayCars => _cars.Count(c => c.GivingWay);

    /// <summary>Right-hand traffic: an undivided road is shared, so each car keeps to its half.</summary>
    private static float KeepRight(LaneEdge e) =>
        e.OneWay != 0 ? e.RightLane
        : (e.Flags & RoadFlags.Divided) != 0 || e.Class == RoadClass.Ramp ? 0f
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
        _trains.Clear();
        _builtAround = null;
    }

    /// <summary>Removes the cars within <paramref name="radius"/> m of a point: a race grid is not lined up in the middle of the traffic.</summary>
    public void ClearAround(Vector3 at, float radius)
    {
        for (int i = _cars.Count - 1; i >= 0; i--)
        {
            if (Flat(_cars[i].Head - at).Length() >= radius) continue;
            foreach (var u in _cars[i].Units) _byBody.Remove(u.GetInstanceId());
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
        public float Lift;
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
