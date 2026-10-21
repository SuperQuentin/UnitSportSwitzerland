using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The shared base of the traffic modes beyond cars and trains (#1060, tracking #1058): walkers (#1033), riders (#1056) and trams
/// (#1057). Each mode has its own lane graph, built off-thread with the roads and rails whenever the view enters a new tile (a
/// builder seam in its own file: <c>Traffic.Walkers.cs</c>, <c>Traffic.Riders.cs</c>, <c>Traffic.Trams.cs</c>), and its own
/// density (<see cref="GameSettings.TrafficWalkers"/>, <see cref="GameSettings.TrafficRiders"/>, <see cref="GameSettings.Trams"/>,
/// 0 until the mode lands). Walkers and riders are <see cref="SlowMover"/>s, one solid coloured box each on a <see cref="Route"/> over
/// their graph, spawned in a ring around <see cref="Focus"/> and pooled; trams are <see cref="Vehicle"/>s like trains (sections at
/// fixed offsets) on the tram graph. Every live one goes into <see cref="_crossers"/> once a tick: the cars' <see cref="Coming"/>
/// (the conflict points of #1046) and their following read it, so a mode only puts its movers there and cars wait for them.
/// Docs: <c>docs/notes/world/traffic-modes.md</c>.
/// </summary>
public partial class Traffic
{
    // ---- graphs ------------------------------------------------------------------------------

    /// <summary>
    /// What a rebuild read, for the modes' graph builders (#1060): the road tiles 7×7 km around the view (<see cref="Wide"/>, the
    /// rails') and 5×5 km (<see cref="Near"/>, the roads'), the one origin frame the build is in, and the road and rail graphs
    /// just built from them (a mode may tie its edges to theirs, e.g. a tram track embedded in a road). Read on a worker thread:
    /// no Godot node, no live origin.
    /// </summary>
    public sealed record ModeTiles(IReadOnlyList<RoadTile> Wide, IReadOnlyList<RoadTile> Near, OriginFrame Origin, LaneGraph Roads, LaneGraph Rails);

    /// <summary>The walkers', riders' and trams' graphs (#1060); null until their mode is wanted and its builder returns one.</summary>
    private LaneGraph? _walks, _rides, _tramLines;

    /// <summary>The modes the graphs were last built for (bits of <see cref="MoverKind"/>): one wanted since rebuilds them.</summary>
    private uint _modesBuilt;

    private static uint Bit(MoverKind kind) => 1u << (int)kind;

    /// <summary>A mode's graph (#1060; the roads for cars, the rails for trains).</summary>
    public LaneGraph? GraphOf(MoverKind kind) => kind switch
    {
        MoverKind.Car => _roads, MoverKind.Train => _rails, MoverKind.Walker => _walks, MoverKind.Rider => _rides,
        MoverKind.Tram => _tramLines, _ => null,
    };

    /// <summary>The modes whose density asks for movers (bits): their graphs are built.</summary>
    private static uint ModesWanted()
    {
        var s = GameSettings.Current;
        return (s.TrafficWalkers > 0 ? Bit(MoverKind.Walker) : 0u) | (s.TrafficRiders > 0 ? Bit(MoverKind.Rider) : 0u)
            | (s.Trams > 0 ? Bit(MoverKind.Tram) : 0u);
    }

    /// <summary>The modes' graphs for one rebuild (on the worker thread): each wanted mode's builder, the others none.</summary>
    private static (LaneGraph? Walks, LaneGraph? Rides, LaneGraph? Trams) BuildModeGraphs(ModeTiles tiles, uint wanted) => (
        (wanted & Bit(MoverKind.Walker)) != 0 ? BuildWalkGraph(tiles) : null,
        (wanted & Bit(MoverKind.Rider)) != 0 ? BuildRideGraph(tiles) : null,
        (wanted & Bit(MoverKind.Tram)) != 0 ? BuildTramGraph(tiles) : null);

    /// <summary>How many movers of a mode the settings ask for now (walkers and riders thin out at night, like the cars).</summary>
    private static int Want(MoverKind kind, float night)
    {
        var s = GameSettings.Current;
        return kind switch
        {
            MoverKind.Walker => Mathf.RoundToInt(s.TrafficWalkers * Mathf.Lerp(1f, 0.3f, night)),
            MoverKind.Rider => Mathf.RoundToInt(s.TrafficRiders * Mathf.Lerp(1f, 0.3f, night)),
            MoverKind.Tram => s.Trams,
            _ => 0,
        };
    }

    // ---- the slow movers: walkers and riders --------------------------------------------------

    /// <summary>
    /// How a mode's movers look and go (#1060): their box (width, height, length, m), colour, cruising speed (m/s) and how much
    /// one mover's differs (a fraction either way), where they spawn (from <see cref="Near"/> m while filling, then
    /// <see cref="SpawnMin"/>-<see cref="SpawnMax"/> m from the view) and are dropped (<see cref="Despawn"/>), and how far they are drawn.
    /// </summary>
    private readonly record struct MoverLook(Vector3 Size, Color Colour, float Cruise, float Spread, float Near, float SpawnMin,
        float SpawnMax, float Despawn, float Drawn);

    /// <summary>
    /// A walker or rider (#1060): one solid box on a <see cref="Route"/> over its mode's graph. Pooled: its body is hidden and kept
    /// when it is dropped, and given to the next one spawned. The mode's own state goes in <see cref="State"/> (or the few plain
    /// fields here); <see cref="Holding"/> says it waits on purpose (a red, a gap), so it is not culled as stuck.
    /// </summary>
    private sealed class SlowMover
    {
        public readonly MoverKind Kind;
        public readonly AnimatableBody3D Body;
        public readonly CollisionShape3D Shape;
        /// <summary>Its next leg at a junction (one delegate for the mover's life, not one per step).</summary>
        public readonly Func<(LaneEdge Edge, bool Forward), (LaneEdge, bool)?> Next;
        public Route Route = null!;
        public float Speed, Cruise;
        /// <summary>Metres right of its edge's line, in its direction of travel (a sidewalk's, a bike lane's offset).</summary>
        public float Lateral;
        public Vector3 Head, Vel;
        public float Stuck;
        /// <summary>Waiting on purpose (a red, a gap, a crossing): not stuck.</summary>
        public bool Holding;
        /// <summary>In a car lane (a rider on a road with no bike lane): the cars behind it follow it (<see cref="FollowCrossers"/>).</summary>
        public bool InLane;
        /// <summary>Its own roll, as a car's <see cref="Vehicle.Habit"/>.</summary>
        public uint Habit;
        /// <summary>Free for the mode: a signal group, a wait, an approach, anything it keeps per mover.</summary>
        public int Group = -1;
        public float Wait;
        public object? State;

        public SlowMover(MoverKind kind, AnimatableBody3D body, CollisionShape3D shape, Func<SlowMover, (LaneEdge, bool), (LaneEdge, bool)?> next)
        {
            Kind = kind;
            Body = body;
            Shape = shape;
            Next = leg => next(this, leg);
        }
    }

    private readonly List<SlowMover> _walkers = new(), _riders = new();
    private readonly Stack<SlowMover> _walkerPool = new(), _riderPool = new();
    private double _walkerTimer, _riderTimer;
    private readonly Dictionary<MoverKind, (BoxMesh Mesh, StandardMaterial3D Material)> _boxLooks = new();

    private List<SlowMover> MoversOf(MoverKind kind) => kind == MoverKind.Walker ? _walkers : _riders;

    private static MoverLook LookOf(MoverKind kind) => kind switch
    {
        MoverKind.Walker => WalkerLook, MoverKind.Rider => RiderLook, _ => TramLook,
    };

    private static readonly StringName DisabledName = "disabled";

    /// <summary>
    /// A solid box for a mode's unit (#1060, every new mover is a box for now): its mesh and material shared per kind, its collision
    /// box the same size, standing on the ground (its origin at the bottom, -Z forward like the cars). For a tram, one per section.
    /// </summary>
    private AnimatableBody3D BoxUnit(MoverKind kind, Vector3 size, float drawn, out CollisionShape3D shape)
    {
        var look = LookOf(kind);
        if (!_boxLooks.TryGetValue(kind, out var shared))
            _boxLooks[kind] = shared = (new BoxMesh { Size = Vector3.One },
                new StandardMaterial3D { AlbedoColor = look.Colour, Roughness = 0.9f });
        var node = new AnimatableBody3D { SyncToPhysics = false, Name = kind.ToString() };
        // (one shared unit cube, scaled per unit: a tram's sections differ in length)
        node.AddChild(new MeshInstance3D
        {
            Mesh = shared.Mesh, MaterialOverride = shared.Material, VisibilityRangeEnd = drawn,
            Scale = new Vector3(size.X, size.Y, size.Z), Position = new Vector3(0f, size.Y * 0.5f, 0f),
        });
        shape = new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = new Vector3(0f, size.Y * 0.5f, 0f) };
        node.AddChild(shape);
        return node;
    }

    /// <summary>A mover of <paramref name="kind"/> from the pool (or a new one), its body shown and solid again.</summary>
    private SlowMover TakeMover(MoverKind kind)
    {
        var pool = kind == MoverKind.Walker ? _walkerPool : _riderPool;
        if (pool.TryPop(out var m))
        {
            m.Body.Visible = true;
            m.Shape.SetDeferred(DisabledName, false);
        }
        else
        {
            var look = LookOf(kind);
            var body = BoxUnit(kind, look.Size, look.Drawn, out var shape);
            AddChild(body);
            m = new SlowMover(kind, body, shape, NextMover);
        }
        m.Speed = m.Stuck = m.Wait = 0f;
        m.Holding = m.InLane = false;
        m.Group = -1;
        m.State = null;
        m.Vel = Vector3.Zero;
        return m;
    }

    /// <summary>Back to the pool: hidden and not solid.</summary>
    private void DropMover(SlowMover m)
    {
        m.Body.Visible = false;
        m.Shape.SetDeferred(DisabledName, true);
        (m.Kind == MoverKind.Walker ? _walkerPool : _riderPool).Push(m);
    }

    /// <summary>
    /// One more mover of <paramref name="kind"/> on its graph, <paramref name="minDist"/>-<paramref name="maxDist"/> m from
    /// <paramref name="focus"/>, on an edge its mode weighs (<c>WalkerWeight</c>, <c>RiderWeight</c>), then handed to the mode
    /// (<c>WalkerSpawned</c>, <c>RiderSpawned</c>) to set its line, speed and anything of its own. Not within 2 m of another.
    /// </summary>
    private void SpawnMover(MoverKind kind, LaneGraph graph, Vector3 focus, float minDist, float maxDist)
    {
        Func<LaneEdge, float> weight = kind == MoverKind.Walker ? WalkerWeight : RiderWeight;
        if (graph.RandomSpot(_rng, focus, minDist, maxDist, weight) is not var (edge, arc)) return;
        var (spot, _) = edge.Sample(arc);
        var list = MoversOf(kind);
        foreach (var o in list)
            if (MathX.Flat(o.Head - spot).LengthSquared() < 4f) return;
        bool forward = edge.OneWay switch { 1 => true, -1 => false, _ => _rng.Next(2) == 0 };
        var m = TakeMover(kind);
        var look = LookOf(kind);
        m.Route = new Route(edge, forward, forward ? arc : edge.Length - arc);
        m.Cruise = look.Cruise * (1f + look.Spread * (2f * (float)_rng.NextDouble() - 1f));
        m.Speed = m.Cruise;
        m.Lateral = 0f;
        m.Habit = (uint)_rng.Next();
        if (kind == MoverKind.Walker) WalkerSpawned(m); else RiderSpawned(m);
        PlaceMover(m);
        m.Vel = Vector3.Zero;
        list.Add(m);
    }

    /// <summary>The box on its route: at the head, its line's offset to the right, square to its way.</summary>
    private void PlaceMover(SlowMover m)
    {
        var (pos, dir) = m.Route.At(0f);
        float lateral = m.Kind == MoverKind.Walker ? WalkerLateral(m) : RiderLateral(m);
        var side = new Vector3(-dir.Z, 0f, dir.X).Normalized();
        var at = pos + side * lateral;
        var flat = new Vector3(dir.X, 0f, dir.Z);
        var basis = flat.LengthSquared() > 1e-6f ? Player.Flyer.Orient(flat.Normalized(), Vector3.Up, Vector3.Forward) : m.Body.GlobalBasis;
        m.Body.GlobalTransform = new Transform3D(basis, at);
        m.Head = at;
    }

    /// <summary>The next leg at a junction, as the mode routes it (<c>NextWalk</c>, <c>NextRide</c>).</summary>
    private (LaneEdge, bool)? NextMover(SlowMover m, (LaneEdge Edge, bool Forward) leg) =>
        m.Kind == MoverKind.Walker ? NextWalk(m, leg) : NextRide(m, leg);

    private readonly List<(LaneEdge Edge, bool Forward)> _nextOptions = new();

    /// <summary>
    /// The default way on at a junction of <paramref name="graph"/> (#1060): any edge leaving it but the one it came on, at random;
    /// at a dead end back the way it came (if that edge may be taken that way), else none (it stops: culled as stuck).
    /// </summary>
    private (LaneEdge, bool)? NextAny(LaneGraph? graph, (LaneEdge Edge, bool Forward) leg)
    {
        if (graph is null) return null;
        long key = leg.Forward ? leg.Edge.KeyEnd : leg.Edge.KeyStart;
        _nextOptions.Clear();
        foreach (var o in graph.Leaving(key))
            if (o.Edge != leg.Edge) _nextOptions.Add(o);
        if (_nextOptions.Count > 0) return _nextOptions[_rng.Next(_nextOptions.Count)];
        if (leg.Edge.OneWay != 0) return null;
        return (leg.Edge, !leg.Forward);
    }

    /// <summary>
    /// One tick of one slow mode (#1060): its spawns (four times a second, a burst while filling), each mover's step (the mode's
    /// <c>StepWalker</c> / <c>StepRider</c> gives the speed it wants; it eases to it, moves on, picks its legs and stands its box on
    /// its line), and the cull (out of range, stuck 30 s, or one too many).
    /// </summary>
    private void StepMovers(MoverKind kind, LaneGraph? graph, Vector3 focus, float dt, int want)
    {
        var list = MoversOf(kind);
        var look = LookOf(kind);
        ref double timer = ref kind == MoverKind.Walker ? ref _walkerTimer : ref _riderTimer;
        if (graph is not null && graph.Edges.Count > 0 && (timer -= dt) <= 0)
        {
            timer = 0.25;
            bool filling = list.Count < want / 2;
            int burst = filling ? Math.Max(1, want / 8) : 1;
            for (int i = 0; i < burst && list.Count < want; i++)
                SpawnMover(kind, graph, focus, filling ? look.Near : look.SpawnMin, look.SpawnMax);
        }
        foreach (var m in list)
        {
            float target = kind == MoverKind.Walker ? StepWalker(m, dt) : StepRider(m, dt);
            m.Speed = Mathf.MoveToward(m.Speed, Mathf.Max(0f, target), (target > m.Speed ? 1.5f : 3f) * dt);
            var before = m.Head;
            bool ended = !m.Route.Advance(m.Speed * dt, m.Next);
            m.Route.Trim(look.Size.Z + 10f);
            PlaceMover(m);
            m.Vel = dt > 0f ? MathX.Flat(m.Head - before) / dt : Vector3.Zero;
            // at the end of its way (no leg on): gone in a few ticks; standing without waiting on purpose: stuck
            m.Stuck = ended ? m.Stuck + 5f : m.Speed < 0.1f && !m.Holding ? m.Stuck + dt : 0f;
        }
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var m = list[i];
            float d = MathX.Flat(m.Head - focus).Length();
            if (d > look.Despawn || m.Stuck > 30f || (list.Count > want && d > look.Despawn * 0.6f) || want == 0)
            {
                DropMover(m);
                list.RemoveAt(i);
            }
        }
    }

    /// <summary>Every walker and rider back to the pool (a reseed, a teleport, a world replaced).</summary>
    private void ClearMovers()
    {
        foreach (var m in _walkers) DropMover(m);
        foreach (var m in _riders) DropMover(m);
        _walkers.Clear();
        _riders.Clear();
        foreach (var t in _trams) t.Free();
        foreach (var t in _trams)
            foreach (var u in t.Units) _byBody.Remove(u.GetInstanceId());
        _trams.Clear();
        _crossers.Clear();
    }

    // ---- trams: trains on the tram graph -------------------------------------------------------

    private readonly List<Vehicle> _trams = new();
    private double _tramTimer;

    /// <summary>
    /// A tram of <paramref name="lengths"/>.Length box sections (#1060, each <see cref="TramLook"/>'s width and height, its own
    /// length, <paramref name="gap"/> m between them) with its head <paramref name="arc"/> m along <paramref name="edge"/>, going
    /// <paramref name="forward"/>, at <paramref name="cruise"/> m/s: a <see cref="Vehicle"/> like a train, its sections at fixed
    /// offsets on one route. Added to the trams.
    /// </summary>
    private Vehicle AddTram(LaneEdge edge, bool forward, float arc, float[] lengths, float gap, float cruise)
    {
        var offsets = new float[lengths.Length];
        var units = new Node3D[lengths.Length];
        float back = 0f;
        for (int i = 0; i < lengths.Length; i++)
        {
            offsets[i] = back + lengths[i] * 0.5f;
            back += lengths[i] + gap;
            var size = new Vector3(TramLook.Size.X, TramLook.Size.Y, lengths[i]);
            units[i] = BoxUnit(MoverKind.Tram, size, TramLook.Drawn, out _);
            units[i].Name = $"Tram{i}";
        }
        var route = new Route(edge, forward, forward ? arc : edge.Length - arc);
        var v = new Vehicle(route, cruise, offsets, units) { UnitHalf = lengths[0] * 0.5f - 1f };
        AddVehicle(v);
        _trams.Add(v);
        return v;
    }

    /// <summary>The trams' tick (#1060): spawns (<c>SpawnTram</c>, four times a second while there are fewer than wanted), steps (<c>StepTram</c>), the cull.</summary>
    private void StepTrams(Vector3 focus, float dt, int want)
    {
        if (_tramLines is { Edges.Count: > 0 } && (_tramTimer -= dt) <= 0)
        {
            _tramTimer = 0.25;
            if (_trams.Count < want) SpawnTram(focus);
        }
        foreach (var t in _trams) StepTram(t, dt);
        Cull(_trams, focus, TramLook.Despawn, want);
    }

    /// <summary>
    /// The way on for a train-like vehicle at a junction of <paramref name="graph"/>: only lines that carry straight on (a train
    /// cannot take a set of points backwards), at random; none at the end of the line. The trains' rule on any graph (#1060).
    /// </summary>
    private (LaneEdge, bool)? NextTrack(LaneGraph graph, (LaneEdge Edge, bool Forward) leg)
    {
        long key = leg.Forward ? leg.Edge.KeyEnd : leg.Edge.KeyStart;
        var (_, dir) = leg.Edge.Sample(leg.Forward ? leg.Edge.Length : 0f);
        if (!leg.Forward) dir = -dir;
        _nextOptions.Clear();
        foreach (var o in graph.Leaving(key))
        {
            if (o.Edge == leg.Edge) continue;
            var (_, t) = o.Edge.Sample(o.Forward ? 0f : o.Edge.Length);
            if ((o.Forward ? t : -t).Dot(dir) > 0.5f) _nextOptions.Add(o);
        }
        if (_nextOptions.Count == 0) return null;
        return _nextOptions[_rng.Next(_nextOptions.Count)];
    }

    // ---- what the cars mind: the crossers ------------------------------------------------------

    /// <summary>
    /// A walker, rider or tram section as the cars see it (#1060): where it is and how it moves, who it is at a conflict point
    /// (<see cref="ConflictMover"/>), whether it is in a car lane (cars behind follow it) and half its length along its way.
    /// </summary>
    private readonly record struct Crosser(Vector3 Pos, Vector3 Vel, MoverKind Kind, ConflictMover Who, bool InLane, float Half);

    /// <summary>
    /// Every live walker, rider and tram section, refilled at the start of each tick and sorted by X (#1060, like
    /// <see cref="_byX"/>): what <see cref="Coming"/> and <see cref="FollowCrossers"/> read. Empty without the modes: the cars
    /// then run exactly as before.
    /// </summary>
    private readonly List<Crosser> _crossers = new();
    private static readonly Comparer<Crosser> CrosserByX = Comparer<Crosser>.Create((a, b) => a.Pos.X.CompareTo(b.Pos.X));
    private static readonly Crosser[] CrosserKey = new Crosser[1];

    /// <summary>Fills <see cref="_crossers"/> from the modes' movers (no allocation once the list has grown).</summary>
    private void CollectCrossers()
    {
        _crossers.Clear();
        foreach (var m in _walkers) _crossers.Add(new Crosser(m.Head, m.Vel, MoverKind.Walker, ConflictMover.Walker, m.InLane, WalkerLook.Size.Z * 0.5f));
        foreach (var m in _riders) _crossers.Add(new Crosser(m.Head, m.Vel, MoverKind.Rider, ConflictMover.Bike, m.InLane, RiderLook.Size.Z * 0.5f));
        foreach (var t in _trams)
        {
            bool inLane = TramInLane(t);
            for (int i = 0; i < t.Units.Length; i++)
                _crossers.Add(new Crosser(t.Units[i].GlobalPosition, t.Vel, MoverKind.Tram, ConflictMover.Tram, inLane, t.UnitHalf + 1f));
        }
        if (_crossers.Count > 1) _crossers.Sort(CrosserByX);
    }

    /// <summary>The first crosser at or east of <paramref name="x"/>.</summary>
    private int CrosserFrom(float x)
    {
        CrosserKey[0] = new Crosser(new Vector3(x, 0f, 0f), default, default, default, false, 0f);
        int i = _crossers.BinarySearch(CrosserKey[0], CrosserByX);
        return i < 0 ? ~i : i;
    }

    /// <summary>
    /// For a car's conflict point (#1046) at <paramref name="at"/> (its band <paramref name="zone"/> m about it) that gives way to
    /// <paramref name="who"/>: a crosser of that kind is in it, or reaches it within <see cref="AcceptGap"/> (the rule the players
    /// and racers get in <see cref="Coming"/>, its body's half length added).
    /// </summary>
    private bool CrosserComing(Vector3 at, float zone, ConflictMover who)
    {
        if (_crossers.Count == 0) return false;
        for (int o = CrosserFrom(at.X - GapLook - 20f); o < _crossers.Count && _crossers[o].Pos.X <= at.X + GapLook + 20f; o++)
        {
            var c = _crossers[o];
            if (c.Who != who) continue;
            var d = MathX.Flat(at - c.Pos);
            float dist = d.Length() - c.Half;
            if (dist < zone) return true;
            float closing = (c.Vel.X * d.X + c.Vel.Z * d.Z) / Mathf.Max(d.Length(), 0.01f);
            if (dist < GapLook && closing > 0.5f && (dist - zone) / closing < AcceptGap) return true;
        }
        return false;
    }

    /// <summary>
    /// The speed a car may still do behind the crossers in its lane ahead (#1060: a rider on a road with no bike lane, a tram
    /// sharing it): the car-following rule of <see cref="StepCar"/> with the crosser's own length. +∞ with none.
    /// </summary>
    private float FollowCrossers(Vehicle car, Vector3 dir, Vector3 rightOf)
    {
        if (_crossers.Count == 0) return float.MaxValue;
        float target = float.MaxValue;
        for (int o = CrosserFrom(car.Head.X - GapWindow); o < _crossers.Count && _crossers[o].Pos.X <= car.Head.X + GapWindow; o++)
        {
            var c = _crossers[o];
            if (!c.InLane) continue;
            var d = c.Pos - car.Head;
            float along = d.Dot(dir);
            if (along < 0.5f || along > FollowLook || Mathf.Abs(d.Y) > 3f || Mathf.Abs(d.Dot(rightOf)) > AheadWidth) continue;
            float speed = Mathf.Max(0f, c.Vel.Dot(dir));
            float gap = along - CarFront - c.Half - 1.5f;
            target = Mathf.Min(target, Mathf.Max(0f, speed + gap * 0.6f));
            target = Mathf.Min(target, Mathf.Sqrt(speed * speed + 2f * 4f * Mathf.Max(0f, gap)));
        }
        return target;
    }

    // ---- --trafficcheck and the debug view ------------------------------------------------------

    /// <summary>How many of a kind are live (#1060; cars and trains too, for <c>--trafficcheck --mover</c>).</summary>
    public int CountOf(MoverKind kind) => kind switch
    {
        MoverKind.Car => _cars.Count, MoverKind.Train => _trains.Count, MoverKind.Tram => _trams.Count,
        MoverKind.Walker => _walkers.Count, MoverKind.Rider => _riders.Count, _ => 0,
    };

    /// <summary>Their average speed (m/s).</summary>
    public float AverageSpeedOf(MoverKind kind)
    {
        float sum = 0f;
        int n = 0;
        switch (kind)
        {
            case MoverKind.Walker or MoverKind.Rider:
                foreach (var m in MoversOf(kind)) { sum += m.Speed; n++; }
                break;
            default:
                foreach (var v in kind switch { MoverKind.Car => _cars, MoverKind.Train => _trains, _ => _trams }) { sum += v.Speed; n++; }
                break;
        }
        return n == 0 ? 0f : sum / n;
    }

    /// <summary>Head position and heading of one of a kind, for the probe's chase camera: the fastest walker or rider, the first tram.</summary>
    public (Vector3 Pos, Vector3 Dir)? WatchKind(MoverKind kind)
    {
        if (kind is MoverKind.Car or MoverKind.Train) return Watch(kind == MoverKind.Train);
        if (kind == MoverKind.Tram)
            return _trams.Count == 0 ? null : (_trams[0].Head, _trams[0].Route.At(0).Dir);
        var list = MoversOf(kind);
        if (list.Count == 0) return null;
        var best = list[0];
        foreach (var m in list) if (m.Speed > best.Speed) best = m;
        return (best.Head, best.Route.At(0).Dir);
    }

    /// <summary>The live crossers' count of a kind seen by the cars (the probe).</summary>
    public int CrossersOf(MoverKind kind)
    {
        int n = 0;
        foreach (var c in _crossers) if (c.Kind == kind) n++;
        return n;
    }

    /// <summary>Draws the modes' graphs (#1060, debug menu Mode graphs): each edge within reach in its mode's colour.</summary>
    public bool ShowModeGraphs { get; set; }

    /// <summary>How far from the view the modes' graphs are drawn (m).</summary>
    private const float ModeGraphDrawn = 300f;

    /// <summary>The modes' part of the debug drawing: their graphs (<see cref="ShowModeGraphs"/>) and, with the paths, each mover's way ahead.</summary>
    private int DrawModes(ImmediateMesh mesh, Vector3 focus)
    {
        int n = 0;
        var up = Vector3.Up * DrawLift;
        if (ShowModeGraphs)
            foreach (var kind in MoverKinds.Modes)
            {
                if (GraphOf(kind) is not { } g) continue;
                var c = LookOf(kind).Colour;
                c.A = 0.7f;
                foreach (var e in g.Edges)
                {
                    if (MathX.Flat(e.Points[0] - focus).Length() > ModeGraphDrawn && MathX.Flat(e.Points[^1] - focus).Length() > ModeGraphDrawn) continue;
                    for (int i = 0; i + 1 < e.Points.Length; i++, n++) Ribbon(mesh, e.Points[i] + up, e.Points[i + 1] + up, c, 0.25f);
                }
            }
        if (ShowPaths)
        {
            foreach (var m in _walkers) n += DrawRouteAhead(mesh, m.Route, m.Head, LookOf(MoverKind.Walker).Colour, 0.25f, 30f);
            foreach (var m in _riders) n += DrawRouteAhead(mesh, m.Route, m.Head, LookOf(MoverKind.Rider).Colour, 0.35f, 60f);
            foreach (var t in _trams) n += DrawRouteAhead(mesh, t.Route, t.Head, LookOf(MoverKind.Tram).Colour, 0.8f, 200f);
        }
        return n;
    }

    /// <summary>A route's legs ahead of its head, on their lines (no lane offset), <paramref name="ahead"/> m of them.</summary>
    private static int DrawRouteAhead(ImmediateMesh mesh, Route route, Vector3 head, Color colour, float width, float ahead)
    {
        var up = Vector3.Up * DrawLift;
        var last = head + up;
        int n = 0;
        for (float s = 2f; s <= ahead; s += 2f, n++)
        {
            var (p, _) = route.At(-s);
            Ribbon(mesh, last, p + up, colour, width);
            last = p + up;
        }
        return n;
    }

    // ---- the origin --------------------------------------------------------------------------

    /// <summary>The origin moved (#185): the modes' graphs, their movers' routes and where they are.</summary>
    private void ShiftModes(OriginShift shift, HashSet<LaneEdge> done)
    {
        _walks?.Shift(shift, _origin.Frame, done);
        _rides?.Shift(shift, _origin.Frame, done);
        _tramLines?.Shift(shift, _origin.Frame, done);
        foreach (var list in (ReadOnlySpan<List<SlowMover>>)[_walkers, _riders])
            foreach (var m in list)
            {
                foreach (var (edge, _) in m.Route.Legs)
                    if (done.Add(edge)) edge.Shift(shift);
                m.Head = shift.Point(m.Head);
                m.Vel = shift.Direction(m.Vel);
            }
        foreach (var t in _trams)
        {
            foreach (var (edge, _) in t.Route.Legs)
                if (done.Add(edge)) edge.Shift(shift);
            t.Head = shift.Point(t.Head);
            t.Vel = shift.Direction(t.Vel);
            for (int i = 0; i < t.Path.Length; i++) t.Path[i] = shift.Point(t.Path[i]);
        }
        for (int i = 0; i < _crossers.Count; i++)
            _crossers[i] = _crossers[i] with { Pos = shift.Point(_crossers[i].Pos), Vel = shift.Direction(_crossers[i].Vel) };
    }
}
