using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// Traffic endpoints on this client's traffic (#971, <see cref="TrafficGenerators"/>): each generator makes its cars where it
/// stands, a car that finds no room waiting in its queue, and sends each by its shares (to a destination along the way found
/// to it, or along the road at random); each destination takes the cars driving out past it. The world's own endpoints (#1134,
/// <see cref="WorldEndpoints"/>) make riders, walkers and trams the same way, each on its mode's graph.
/// </summary>
public partial class Traffic
{
    /// <summary>
    /// Movers only from the endpoints (#971, the user: the test world): no car spawned at random, none culled for being one too
    /// many; since #1134 the same for riders, walkers and trams. The real world keeps its random traffic (its endpoints will be
    /// implicit, later).
    /// </summary>
    public bool GeneratorsOnly { get; set; }

    /// <summary>The endpoints' list (every peer's, shared); null runs none.</summary>
    public TrafficGenerators? Generators { get; set; }

    /// <summary>
    /// The world's own endpoints (#1134: the test region's, from its file beside the tiles): every peer reads the same file and runs
    /// them on its own traffic, as it does the shared list's, with ids from <see cref="WorldIdBase"/>. Cars, riders, walkers, trams.
    /// </summary>
    public IReadOnlyList<TrafficEndpoint> WorldEndpoints
    {
        get => _worldEndpoints;
        set
        {
            _worldEndpoints = value;
            _endpointModes = 0;
            foreach (var ep in value)
                if (ep.Mode != MoverKind.Car) _endpointModes |= Bit(ep.Mode);
            _endpointsStale = true;
        }
    }
    private IReadOnlyList<TrafficEndpoint> _worldEndpoints = [];
    /// <summary>The modes beyond cars the world's endpoints make or take (bits of <see cref="MoverKind"/>): their graphs are built.</summary>
    private uint _endpointModes;

    /// <summary>A world endpoint's id is its file's plus this (the shared list's ids stay below).</summary>
    public const int WorldIdBase = 100000;

    /// <summary>The junction and arm (a corner, a tram line) each world endpoint serves, by id (the probe's report).</summary>
    public readonly Dictionary<int, (string Junction, string Arm)> EndpointLabels = new();

    /// <summary>The world's endpoints from its file (#1134), ids moved up by <see cref="WorldIdBase"/>, their labels kept.</summary>
    public void LoadWorldEndpoints(IEnumerable<RegionEndpoint> file)
    {
        var list = new List<TrafficEndpoint>();
        EndpointLabels.Clear();
        foreach (var x in file)
        {
            var mode = x.Mode switch
            {
                EndpointMode.Bike => MoverKind.Rider, EndpointMode.Walker => MoverKind.Walker, EndpointMode.Tram => MoverKind.Tram, _ => MoverKind.Car,
            };
            var shares = x.Shares.Length == 0 ? null : x.Shares.Select(s => new TrafficShare(s.To + WorldIdBase, s.Weight)).ToArray();
            list.Add(new TrafficEndpoint(x.Id + WorldIdBase, x.E, x.N, x.Heading, x.Generates, x.Destination, x.PerMinute, shares, mode));
            EndpointLabels[x.Id + WorldIdBase] = (x.Junction, x.Arm);
        }
        WorldEndpoints = list;
    }

    /// <summary><c>--traffic-rate X</c> (#971): every generator's rate times this.</summary>
    public float GeneratorRate { get; set; } = 1f;

    /// <summary>The most cars the endpoints keep on the roads at once, whatever their rates.</summary>
    private const int MaxGenerated = 400;
    /// <summary>The most riders, walkers and trams the endpoints keep at once (#1134), each.</summary>
    private const int MaxGeneratedRiders = 300, MaxGeneratedWalkers = 300, MaxGeneratedTrams = 16;
    /// <summary>
    /// A generator makes its movers only this close to the view, as a share of its mode's drop distance (#1134): farther they would
    /// be dropped as soon as made (a whole region of them).
    /// </summary>
    private const float GenerateWithin = 0.75f;
    /// <summary>A generator's queue stops growing here: cars it could not start for a long while are given up.</summary>
    private const int MaxQueue = 40;
    /// <summary>How far an endpoint may stand from a road and still be on it (m).</summary>
    private const float SnapReach = 30f;
    /// <summary>Road edges the way to a destination searches before it gives up (a car then goes at random).</summary>
    private const int MaxSearch = 20000;

    /// <summary>An endpoint on this graph: the road edge it stands on, the way its heading runs along it, where (m from the edge's start).</summary>
    private sealed class EndpointState
    {
        public required TrafficEndpoint Ep;
        /// <summary>Its mode's graph (the roads for cars).</summary>
        public LaneGraph? Graph;
        public LaneEdge? Edge;
        public bool Forward;
        public float Pos;
        /// <summary>
        /// Where it takes movers as a destination (#1134): the edge they may drive out on (a divided road's other carriageway, a
        /// one-way's: not the one its movers start on), its heading's way along it, where.
        /// </summary>
        public LaneEdge? OutEdge;
        public bool OutForward;
        public float OutPos;
        /// <summary>Where it stands (world).</summary>
        public Vector3 At;
        public double Due;
        public int Queue;
        public readonly Dictionary<(int To, bool Forward), List<(LaneEdge, bool)>?> Ways = new();
        /// <summary>As a destination of walkers or riders (#1134): from each leg of its graph, the length to the leg out past it.</summary>
        public Dictionary<(LaneEdge, bool), float>? CostTo;
    }

    private readonly Dictionary<int, EndpointState> _endpoints = new();
    private (LaneGraph?, LaneGraph?, LaneGraph?, LaneGraph?) _endpointsOn;
    private bool _endpointsStale = true;

    /// <summary>Cars the endpoints made, took, and those still waiting in their queues (the probe prints them).</summary>
    public int Generated { get; private set; }
    public int Arrived { get; private set; }
    public int Queued => _endpoints.Values.Sum(s => s.Queue);
    /// <summary>Cars sent to a destination with no way found to it (they went at random).</summary>
    public int Unrouted { get; private set; }
    /// <summary>Movers taken by a destination, by the generator that made them (0: one not made by one) and the destination.</summary>
    public readonly Dictionary<(int From, int To), int> ArrivedBy = new();
    /// <summary>Movers each generator made (#1134, the probe's report).</summary>
    public readonly Dictionary<int, int> GeneratedBy = new();
    /// <summary>Walkers or riders a generator did not make: no way on their graph to the destination drawn (#1134).</summary>
    public readonly Dictionary<int, int> NoWayBy = new();
    /// <summary>Movers each generator still has waiting in its queue (#1134, the probe's report).</summary>
    public int QueuedAt(int id) => _endpoints.TryGetValue(id, out var s) ? s.Queue : 0;
    /// <summary>Whether an endpoint found its mode's graph (#1134, the probe's report: one on no road makes nothing).</summary>
    public bool EndpointPlaced(int id) => _endpoints.TryGetValue(id, out var s) && (s.Edge ?? s.OutEdge) is not null;
    private int _generatedLive;

    /// <summary>Called by the list's <see cref="TrafficGenerators.Changed"/>: the endpoints are snapped again on the next tick.</summary>
    public void EndpointsChanged() => _endpointsStale = true;

    /// <summary>The endpoints' tick: their states kept on this graph, their movers made, the arrived ones taken.</summary>
    private void StepEndpoints(float dt, Vector3 focus)
    {
        if (_roads is null || (Generators is null && _worldEndpoints.Count == 0)) return;
        if (_endpointsStale || _endpointsOn != (_roads, _rides, _walks, _tramLines)) SnapEndpoints();
        foreach (var s in _endpoints.Values)
        {
            if (s.Edge is null || !s.Ep.Generates) continue;
            float range = (s.Ep.Mode == MoverKind.Car ? CarDespawn : LookOf(s.Ep.Mode).Despawn) * GenerateWithin;
            if (MathX.Flat(s.At - focus).LengthSquared() > range * range) continue;
            s.Due += s.Ep.PerMinute * GeneratorRate / 60.0 * dt;
            while (s.Due >= 1.0) { s.Due -= 1.0; if (s.Queue < MaxQueue) s.Queue++; }
            // one a tick, as soon as there is room (the user: a car that cannot start waits in the queue)
            if (s.Queue == 0) continue;
            bool started = s.Ep.Mode switch
            {
                MoverKind.Car => _generatedLive < MaxGenerated && Start(s),
                MoverKind.Tram => _trams.Count < MaxGeneratedTrams && StartTram(s),
                MoverKind.Rider => _riders.Count < MaxGeneratedRiders && StartSlow(s),
                MoverKind.Walker => _walkers.Count < MaxGeneratedWalkers && StartSlow(s),
                _ => false,
            };
            if (!started) continue;
            s.Queue--;
            GeneratedBy[s.Ep.Id] = GeneratedBy.GetValueOrDefault(s.Ep.Id) + 1;
        }
        TakeArrived();
        TakeArrivedMovers();
    }

    /// <summary>Every endpoint this traffic runs: the shared list's and the world's own (#1134).</summary>
    private IEnumerable<TrafficEndpoint> AllEndpoints()
    {
        if (Generators is not null)
            foreach (var ep in Generators.All) yield return ep;
        foreach (var ep in _worldEndpoints) yield return ep;
    }

    /// <summary>Puts every endpoint on its mode's graph, keeping what it had queued.</summary>
    private void SnapEndpoints()
    {
        _endpointsStale = false;
        _endpointsOn = (_roads, _rides, _walks, _tramLines);
        var old = new Dictionary<int, EndpointState>(_endpoints);
        _endpoints.Clear();
        foreach (var ep in AllEndpoints())
        {
            // (a new generator's first mover comes at once: a tram every few minutes is not waited for)
            var s = new EndpointState { Ep = ep, Graph = GraphOf(ep.Mode), Due = 1.0 };
            if (old.TryGetValue(ep.Id, out var was)) (s.Due, s.Queue) = (was.Due, was.Queue);
            var at = _origin.ToWorld(ep.E, ep.N, 0);
            var heading = new Vector3(Mathf.Cos(ep.Heading), 0f, -Mathf.Sin(ep.Heading));
            // its movers start on an edge they may drive along its heading, and leave on one they may drive against it
            if (s.Graph is not null && OnGraph(s.Graph, ep.Mode, at, SnapReach, heading) is var (edge, pos, tangent))
            {
                s.Edge = edge;
                s.Pos = pos;
                s.Forward = tangent.Dot(heading) >= 0f;
                s.At = edge.Sample(pos).Pos;
            }
            if (s.Graph is not null && OnGraph(s.Graph, ep.Mode, at, SnapReach, -heading) is var (outEdge, outPos, outTangent))
            {
                s.OutEdge = outEdge;
                // (not at its very end: a mover reaching an edge's end is on its next leg before it is past it; J3's west sidewalks
                // are 19 m pieces that their endpoints snapped to the ends of, and no walker ever arrived)
                s.OutPos = Mathf.Clamp(outPos, Mathf.Min(1f, outEdge.Length * 0.5f), Mathf.Max(outEdge.Length - 1f, outEdge.Length * 0.5f));
                s.OutForward = outTangent.Dot(heading) >= 0f;
            }
            _endpoints[ep.Id] = s;
        }
    }

    /// <summary>The road edge nearest <paramref name="at"/> (plan view) within <paramref name="reach"/> m: where along it, and its direction there.</summary>
    public (LaneEdge Edge, float Pos, Vector3 Tangent)? OnRoad(Vector3 at, float reach) =>
        _roads is null ? null : OnGraph(_roads, MoverKind.Car, at, reach, null);

    /// <summary>
    /// The edge of <paramref name="graph"/> nearest <paramref name="at"/> (plan view) within <paramref name="reach"/> m that a mover
    /// of <paramref name="kind"/> may start on (no link, no junction path; a rider's one it may ride): where along it, its direction.
    /// </summary>
    private static (LaneEdge Edge, float Pos, Vector3 Tangent)? OnGraph(LaneGraph graph, MoverKind kind, Vector3 at, float reach, Vector3? way)
    {
        (LaneEdge, float, Vector3)? best = null;
        float bestD = reach * reach;
        foreach (var e in graph.Edges)
        {
            if (e.Connector || e.Path is not null) continue;
            if (kind == MoverKind.Rider && LaneGraph.RideOf(e) is null) continue;
            var p = e.Points;
            for (int i = 0; i + 1 < p.Length; i++)
            {
                var a = new Vector2(p[i].X, p[i].Z);
                var ab = new Vector2(p[i + 1].X, p[i + 1].Z) - a;
                float len2 = ab.LengthSquared();
                if (len2 < 1e-6f) continue;
                // (one that may be driven <paramref name="way"/> only, where asked)
                if (way is { } w && e.OneWay != 0 && (p[i + 1] - p[i]).Dot(w) * e.OneWay <= 0f) continue;
                float t = Mathf.Clamp((new Vector2(at.X, at.Z) - a).Dot(ab) / len2, 0f, 1f);
                float d = (a + ab * t - new Vector2(at.X, at.Z)).LengthSquared();
                if (d >= bestD) continue;
                bestD = d;
                var tangent = (p[i + 1] - p[i]).Normalized();
                best = (e, e.Cumulative[i] + t * (e.Cumulative[i + 1] - e.Cumulative[i]), tangent);
            }
        }
        return best;
    }

    /// <summary>One car from <paramref name="s"/>, by its shares; false where there is no room at its spot yet.</summary>
    private bool Start(EndpointState s)
    {
        var edge = s.Edge!;
        var share = PickShare(s.Ep);
        bool forward = share.To == TrafficEndpoint.AgainstHeading ? !s.Forward : s.Forward;
        List<(LaneEdge, bool)>? way = null;
        if (share.To > 0)
        {
            way = WayTo(s, share.To, s.Forward);
            if (way is null && (edge.OneWay == 0 || (edge.OneWay == 1) == !s.Forward))
            {
                // a generator in the middle of a road: the other way may be the one that leads there
                way = WayTo(s, share.To, !s.Forward);
                if (way is not null) forward = !s.Forward;
            }
            if (way is null) Unrouted++;
        }
        float arc = forward ? s.Pos : edge.Length - s.Pos;
        var route = new Route(edge, forward, arc);
        var (spot, dir) = route.At(0f);
        // (#1172) where the car will stand, its usual line off the road's centre: on J13's 4+4 artery that is 7-10 m off, so a queue
        // standing at the generator was never "within 8 m" of the centre and cars started 4 m behind its last car, at 25 km/h
        // (#1195) and where its lane puts it at once: on a transition's strip or an approach's lane up to 4 m further over. A queue
        // standing in that lane lay 6.5 m beside the usual line, past RoomFor's 6 m: the car started at 60 km/h 22 m behind it and
        // stopped on its last car (J21's north arm, the generator between J21 and J22)
        uint habit = (uint)_rng.Next();
        spot += new Vector3(-dir.Z, 0f, dir.X).Normalized() * StartLine(route, habit, way);
        if (RoomFor(spot, dir, CruiseSpeed(edge.Class) * 0.8f) is not { } speed) return false;
        AddCar(route);
        var car = _cars[^1];
        car.Habit = habit;
        car.Speed = speed;
        car.Planned = way;
        car.FromEndpoint = true;
        car.FromId = s.Ep.Id;
        car.ToId = way is not null ? share.To : 0;
        _generatedLive++;
        Generated++;
        return true;
    }

    /// <summary>
    /// One walker or rider from <paramref name="s"/> (#1134), by its shares: placed where it stands, heading its way, on its way to its
    /// destination by the lengths from there (<see cref="CostTo"/>; none found: at random). False while another stands on the spot.
    /// </summary>
    private bool StartSlow(EndpointState s)
    {
        var kind = s.Ep.Mode;
        var edge = s.Edge!;
        var share = PickShare(s.Ep);
        bool forward = share.To == TrafficEndpoint.AgainstHeading ? !s.Forward : s.Forward;
        if (edge.OneWay != 0) forward = edge.OneWay == 1;
        var list = MoversOf(kind);
        var look = LookOf(kind);
        float clear = look.Size.Z + 1.5f;
        foreach (var o in list)
            if (MathX.Flat(o.Head - s.At).LengthSquared() < clear * clear) return false;
        Dictionary<(LaneEdge, bool), float>? goal = null;
        if (share.To > 0 && _endpoints.TryGetValue(share.To, out var dest) && dest.Ep.Mode == kind && CostTo(dest) is { } cost)
        {
            // no way there on this mode's graph (a sidewalk with no crossing to the other, no corner round): none made, counted
            if (!cost.ContainsKey((edge, forward)) && !(edge, forward).Equals((dest.OutEdge, !dest.OutForward)))
            {
                NoWayBy[s.Ep.Id] = NoWayBy.GetValueOrDefault(s.Ep.Id) + 1;
                s.Queue--;
                return false;
            }
            goal = cost;
        }
        var m = TakeMover(kind);
        if (goal is not null) (m.ToId, m.Goal) = (share.To, goal);
        m.Route = new Route(edge, forward, forward ? s.Pos : edge.Length - s.Pos);
        m.Cruise = look.Cruise * (1f + look.Spread * (2f * (float)_rng.NextDouble() - 1f));
        m.Speed = m.Cruise;
        m.Lateral = 0f;
        m.Habit = (uint)_rng.Next();
        m.FromId = s.Ep.Id;
        if (kind == MoverKind.Walker) WalkerSpawned(m); else RiderSpawned(m);
        PlaceMover(m);
        m.Vel = Vector3.Zero;
        list.Add(m);
        return true;
    }

    /// <summary>
    /// One tram from <paramref name="s"/> (#1134): its sections on the track behind the endpoint, heading in, standing; false while
    /// another tram is within <see cref="TramSpawnClear"/> m or the track behind is too short for it. Its way on: the track's.
    /// </summary>
    private bool StartTram(EndpointState s)
    {
        var edge = s.Edge!;
        foreach (var t in _trams)
            if (MathX.Flat(t.Head - s.At).Length() < TramSpawnClear) return false;
        var lengths = _rng.Next(2) == 0 ? TramShort : TramLong;
        float length = TramGangway * (lengths.Length - 1);
        foreach (float l in lengths) length += l;
        var route = new Route(edge, s.Forward, s.Forward ? s.Pos : edge.Length - s.Pos);
        if (!BackFill(s.Graph!, route, length + 5f)) return false;
        var tram = AddTram(edge, s.Forward, s.Pos, lengths, TramGangway, TramStreet);
        tram.Route = route;
        tram.Speed = 0f;
        tram.Place(TramLateralFn);
        tram.FromEndpoint = true;
        tram.FromId = s.Ep.Id;
        TramsSpawned++;
        return true;
    }

    private TrafficShare PickShare(TrafficEndpoint ep)
    {
        if (ep.Shares is not { Length: > 0 } shares) return new TrafficShare(TrafficEndpoint.AlongHeading, 1f);
        float at = (float)_rng.NextDouble() * shares.Sum(x => x.Weight);
        foreach (var share in shares)
            if ((at -= share.Weight) < 0f) return share;
        return shares[^1];
    }

    /// <summary>
    /// The legs from the generator's edge (driven <paramref name="forward"/>) to destination <paramref name="to"/>'s edge driven
    /// out past it, the shortest by length (cached per generator); null where none is found.
    /// </summary>
    private List<(LaneEdge, bool)>? WayTo(EndpointState from, int to, bool forward)
    {
        if (from.Ways.TryGetValue((to, forward), out var cached)) return cached;
        List<(LaneEdge, bool)>? way = null;
        if (_endpoints.TryGetValue(to, out var dest) && dest.OutEdge is { } destEdge && dest.Ep.Destination)
        {
            var goal = (destEdge, !dest.OutForward);
            var start = (from.Edge!, forward);
            var dist = new Dictionary<(LaneEdge, bool), float> { [start] = 0f };
            var back = new Dictionary<(LaneEdge, bool), (LaneEdge, bool)>();
            var open = new PriorityQueue<(LaneEdge, bool), float>();
            open.Enqueue(start, 0f);
            int searched = 0;
            while (open.TryDequeue(out var leg, out float d) && searched++ < MaxSearch)
            {
                if (d > dist[leg]) continue;
                if (leg == goal)
                {
                    way = new List<(LaneEdge, bool)> { leg };
                    while (back.TryGetValue(way[^1], out var prev)) way.Add(prev);
                    way.Reverse();
                    break;
                }
                foreach (var next in Onward(leg))
                {
                    float nd = d + next.Edge.Length;
                    if (dist.TryGetValue(next, out float known) && known <= nd) continue;
                    dist[next] = nd;
                    back[next] = leg;
                    open.Enqueue(next, nd);
                }
            }
        }
        from.Ways[(to, forward)] = way;
        return way;
    }

    /// <summary>
    /// For walkers and riders (#1134): from every leg of the destination's graph, the length to its leg out past it (a search back
    /// from it, from each leg's start), cached on the destination. Null for one on no edge.
    /// </summary>
    private Dictionary<(LaneEdge, bool), float>? CostTo(EndpointState dest)
    {
        if (dest.CostTo is not null) return dest.CostTo;
        if (dest.OutEdge is not { } edge || dest.Graph is not { } graph || !dest.Ep.Destination) return null;
        var goal = (edge, !dest.OutForward);
        var dist = new Dictionary<(LaneEdge, bool), float> { [goal] = 0f };
        var open = new PriorityQueue<(LaneEdge Edge, bool Forward), float>();
        open.Enqueue(goal, 0f);
        int searched = 0;
        while (open.TryDequeue(out var leg, out float d) && searched++ < MaxSearch)
        {
            if (d > dist[leg]) continue;
            long start = leg.Forward ? leg.Edge.KeyStart : leg.Edge.KeyEnd;
            // as a mover may go on (NextRide, Onward): a road arriving where junction paths start takes one of them, and a path
            // ends on a road; so no road into a road there, no path into a path
            bool pathsHere = false;
            foreach (var o in graph.Leaving(start))
                if (o.Edge.Path is not null) { pathsHere = true; break; }
            foreach (var before in graph.Entering(start))
            {
                if (before.Edge == leg.Edge) continue;
                bool fromPath = before.Edge.Path is not null, toPath = leg.Edge.Path is not null;
                if (fromPath && toPath || !fromPath && !toPath && pathsHere) continue;
                float nd = d + before.Edge.Length;
                if (dist.TryGetValue(before, out float known) && known <= nd) continue;
                dist[before] = nd;
                open.Enqueue(before, nd);
            }
        }
        return dest.CostTo = dist;
    }

    /// <summary>
    /// Of the legs <paramref name="options"/> a walker or rider may take on, the one on its shortest way to its destination
    /// (#1134); null without a destination or with none of them leading there.
    /// </summary>
    private static (LaneEdge Edge, bool Forward)? TowardGoal(SlowMover m, List<(LaneEdge Edge, bool Forward)> options)
    {
        if (m.Goal is not { } cost) return null;
        (LaneEdge, bool)? best = null;
        float least = float.MaxValue;
        foreach (var o in options)
            if (cost.TryGetValue(o, out float c) && c < least) (least, best) = (c, o);
        return best;
    }

    /// <summary>Where a car may go on from a leg, as <see cref="NextRoad"/> lets it: a road arriving at a junction with paths takes one of them.</summary>
    private IEnumerable<(LaneEdge Edge, bool Forward)> Onward((LaneEdge Edge, bool Forward) leg)
    {
        long key = leg.Forward ? leg.Edge.KeyEnd : leg.Edge.KeyStart;
        var options = _roads!.Leaving(key).Where(o => o.Edge != leg.Edge).ToList();
        if (leg.Edge.Path is not null)
        {
            var roads = options.Where(o => o.Edge.Path is null).ToList();
            if (roads.Count > 0) return roads;
        }
        else if (options.Any(o => o.Edge.Path is not null)) return options.Where(o => o.Edge.Path is not null);
        return options;
    }

    /// <summary>The leg after <paramref name="leg"/> on a car's planned way, if it is on it and the way goes on.</summary>
    private static (LaneEdge, bool)? PlannedAfter(Vehicle car, (LaneEdge Edge, bool Forward) leg)
    {
        if (car.Planned is not { } way) return null;
        int k = way.IndexOf(leg);
        // a path beside the planned one, to the same exit (NextRoad's pick among lanes side by side)
        if (k < 0 && leg.Edge.Path is not null)
            k = way.FindIndex(w => w.Item1.Path is not null && w.Item1.KeyEnd == leg.Edge.KeyEnd && w.Item2 == leg.Forward);
        return k >= 0 && k + 1 < way.Count ? way[k + 1] : null;
    }

    /// <summary>
    /// Room for a car to start at <paramref name="spot"/> going <paramref name="way"/>, and the speed it may start at (at most
    /// <paramref name="cruise"/>): none within a car of the spot, none coming up behind it within <see cref="SpawnClear"/> m, and
    /// for one ahead going its way no faster than it can still brake from. Null where there is no room.
    /// </summary>
    private float? RoomFor(Vector3 spot, Vector3 way, float cruise)
    {
        float speed = cruise;
        foreach (var c in _cars)
        {
            var d = c.Head - spot;
            if (new Vector2(d.X, d.Z).LengthSquared() < 8f * 8f) return null;
            if (c.Route.At(0).Dir.Dot(way) < 0.5f) continue;
            float along = d.Dot(way);
            if (Mathf.Abs(along) > 60f || (d - way * along).Length() > 6f) continue;
            if (along < 0f && along > -SpawnClear) return null;
            if (along > 0f) speed = Mathf.Min(speed, Mathf.Sqrt(c.Speed * c.Speed + 2f * 4f * Mathf.Max(0f, along - 9f)));
        }
        return speed;
    }

    /// <summary>The destination of <paramref name="kind"/> a mover on <paramref name="route"/> drives out past now, its id; 0 none.</summary>
    private int DestinationPassed(MoverKind kind, Route route)
    {
        var edge = route.Edge;
        foreach (var s in _endpoints.Values)
        {
            if (!s.Ep.Destination || s.Ep.Mode != kind || s.OutEdge != edge || route.Forward == s.OutForward) continue;
            // driving out (against the endpoint's heading): past it once its place along the edge is beyond the endpoint's
            float pos = route.Forward ? route.Arc : edge.Length - route.Arc;
            if (route.Forward ? pos >= s.OutPos : pos <= s.OutPos) return s.Ep.Id;
        }
        return 0;
    }

    /// <summary>Takes the cars driving out past a destination: they leave the world there.</summary>
    private void TakeArrived()
    {
        bool any = false;
        foreach (var s in _endpoints.Values) any |= s.Ep.Destination && s.OutEdge is not null;
        if (!any) return;
        for (int i = _cars.Count - 1; i >= 0; i--)
        {
            var car = _cars[i];
            int took = DestinationPassed(MoverKind.Car, car.Route);
            if (took == 0) continue;
            ArrivedBy[(car.FromId, took)] = ArrivedBy.GetValueOrDefault((car.FromId, took)) + 1;
            if (car.FromEndpoint) _generatedLive--;
            foreach (var u in car.Units) _byBody.Remove(u.GetInstanceId());
            car.Free();
            _cars.RemoveAt(i);
            Arrived++;
        }
    }

    /// <summary>Takes the riders, walkers and trams going out past a destination of theirs (#1134).</summary>
    private void TakeArrivedMovers()
    {
        if (_endpointModes == 0) return;
        foreach (var list in (ReadOnlySpan<List<SlowMover>>)[_walkers, _riders])
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var m = list[i];
                int took = DestinationPassed(m.Kind, m.Route);
                if (took == 0) continue;
                ArrivedBy[(m.FromId, took)] = ArrivedBy.GetValueOrDefault((m.FromId, took)) + 1;
                DropMover(m);
                list.RemoveAt(i);
            }
        for (int i = _trams.Count - 1; i >= 0; i--)
        {
            var tram = _trams[i];
            int took = DestinationPassed(MoverKind.Tram, tram.Route);
            if (took == 0) continue;
            ArrivedBy[(tram.FromId, took)] = ArrivedBy.GetValueOrDefault((tram.FromId, took)) + 1;
            foreach (var u in tram.Units) _byBody.Remove(u.GetInstanceId());
            tram.Free();
            _trams.RemoveAt(i);
        }
    }

    /// <summary>
    /// <c>--trafficcheck --at</c> in the test world (#971, the user): where an endpoint goes on each arm of the junction near
    /// <paramref name="at"/>: each signalised approach within 60 m followed back up its road to its far end (a dead end, or
    /// <paramref name="reach"/> m), 15 m short of it; with the heading in, toward the junction (LV95 radians).
    /// </summary>
    public List<(double E, double N, float Heading)> ArmEnds(Vector3 at, float reach = 450f)
    {
        var ends = new List<(double, double, float)>();
        if (_roads is null) return ends;
        foreach (var a in _roads.Approaches.Where(a => a.Site is not null && new Vector2(a.Stop.X - at.X, a.Stop.Z - at.Z).Length() < 60f))
        {
            var e = _roads.Edges.FirstOrDefault(x => x.ApproachAtEnd?.Approach == a || x.ApproachAtStart?.Approach == a);
            if (e is null) continue;
            var leg = (Edge: e, Forward: e.ApproachAtEnd?.Approach == a);
            float behind = leg.Edge.Length;
            var seen = new HashSet<LaneEdge> { e };
            while (behind < reach)
            {
                var (first, forward) = leg;
                var options = _roads.Entering(forward ? first.KeyStart : first.KeyEnd)
                    .Where(o => o.Edge != first && !o.Edge.Connector && o.Edge.Path is null && !seen.Contains(o.Edge)).ToList();
                if (options.Count == 0) break;
                leg = options.MaxBy(o => o.Edge.Length);
                seen.Add(leg.Edge);
                behind += leg.Edge.Length;
            }
            // 15 m in from the leg's far end, driven toward the junction
            float s = leg.Forward ? Mathf.Min(15f, leg.Edge.Length) : Mathf.Max(0f, leg.Edge.Length - 15f);
            var (pos, tangent) = leg.Edge.Sample(s);
            var dir = leg.Forward ? tangent : -tangent;
            var (en, nn) = _origin.ToLv95(pos);
            ends.Add((en, nn, Mathf.Atan2(-dir.Z, dir.X)));
        }
        return ends;
    }
}
