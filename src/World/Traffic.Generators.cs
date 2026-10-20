using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// Traffic endpoints on this client's traffic (#971, <see cref="TrafficGenerators"/>): each generator makes its cars where it
/// stands, a car that finds no room waiting in its queue, and sends each by its shares (to a destination along the way found
/// to it, or along the road at random); each destination takes the cars driving out past it.
/// </summary>
public partial class Traffic
{
    /// <summary>
    /// Cars only from the endpoints (#971, the user: the test world): no car spawned at random, none culled for being one too
    /// many. The real world keeps its random cars (its endpoints will be implicit, later).
    /// </summary>
    public bool GeneratorsOnly { get; set; }

    /// <summary>The endpoints' list (every peer's, shared); null runs none.</summary>
    public TrafficGenerators? Generators { get; set; }

    /// <summary><c>--traffic-rate X</c> (#971): every generator's rate times this.</summary>
    public float GeneratorRate { get; set; } = 1f;

    /// <summary>The most cars the endpoints keep on the roads at once, whatever their rates.</summary>
    private const int MaxGenerated = 400;
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
        public LaneEdge? Edge;
        public bool Forward;
        public float Pos;
        public double Due;
        public int Queue;
        public readonly Dictionary<(int To, bool Forward), List<(LaneEdge, bool)>?> Ways = new();
    }

    private readonly Dictionary<int, EndpointState> _endpoints = new();
    private LaneGraph? _endpointsOn;
    private bool _endpointsStale = true;

    /// <summary>Cars the endpoints made, took, and those still waiting in their queues (the probe prints them).</summary>
    public int Generated { get; private set; }
    public int Arrived { get; private set; }
    public int Queued => _endpoints.Values.Sum(s => s.Queue);
    /// <summary>Cars sent to a destination with no way found to it (they went at random).</summary>
    public int Unrouted { get; private set; }
    /// <summary>Cars taken by a destination, by the generator that made them (0: a car not made by one) and the destination.</summary>
    public readonly Dictionary<(int From, int To), int> ArrivedBy = new();
    private int _generatedLive;

    /// <summary>Called by the list's <see cref="TrafficGenerators.Changed"/>: the endpoints are snapped again on the next tick.</summary>
    public void EndpointsChanged() => _endpointsStale = true;

    /// <summary>The endpoints' tick: their states kept on this graph, their cars made, the arrived ones taken.</summary>
    private void StepEndpoints(float dt)
    {
        if (Generators is null || _roads is null) return;
        if (_endpointsStale || _endpointsOn != _roads) SnapEndpoints();
        foreach (var s in _endpoints.Values)
        {
            if (s.Edge is null) continue;
            if (s.Ep.Generates)
            {
                s.Due += s.Ep.PerMinute * GeneratorRate / 60.0 * dt;
                while (s.Due >= 1.0) { s.Due -= 1.0; if (s.Queue < MaxQueue) s.Queue++; }
                // one a tick, as soon as there is room (the user: a car that cannot start waits in the queue)
                if (s.Queue > 0 && _generatedLive < MaxGenerated && Start(s)) s.Queue--;
            }
        }
        TakeArrived();
    }

    /// <summary>Puts every endpoint on its road, keeping what it had queued.</summary>
    private void SnapEndpoints()
    {
        _endpointsStale = false;
        _endpointsOn = _roads;
        var old = new Dictionary<int, EndpointState>(_endpoints);
        _endpoints.Clear();
        foreach (var ep in Generators!.All)
        {
            var s = new EndpointState { Ep = ep };
            if (old.TryGetValue(ep.Id, out var was)) (s.Due, s.Queue) = (was.Due, was.Queue);
            var at = _origin.ToWorld(ep.E, ep.N, 0);
            var heading = new Vector3(Mathf.Cos(ep.Heading), 0f, -Mathf.Sin(ep.Heading));
            if (OnRoad(at, SnapReach) is var (edge, pos, tangent))
            {
                s.Edge = edge;
                s.Pos = pos;
                s.Forward = tangent.Dot(heading) >= 0f;
            }
            _endpoints[ep.Id] = s;
        }
    }

    /// <summary>The road edge nearest <paramref name="at"/> (plan view) within <paramref name="reach"/> m: where along it, and its direction there.</summary>
    public (LaneEdge Edge, float Pos, Vector3 Tangent)? OnRoad(Vector3 at, float reach)
    {
        if (_roads is null) return null;
        (LaneEdge, float, Vector3)? best = null;
        float bestD = reach * reach;
        foreach (var e in _roads.Edges)
        {
            if (e.Connector || e.Path is not null) continue;
            var p = e.Points;
            for (int i = 0; i + 1 < p.Length; i++)
            {
                var a = new Vector2(p[i].X, p[i].Z);
                var ab = new Vector2(p[i + 1].X, p[i + 1].Z) - a;
                float len2 = ab.LengthSquared();
                if (len2 < 1e-6f) continue;
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
        if (RoomFor(spot, dir, CruiseSpeed(edge.Class) * 0.8f) is not { } speed) return false;
        AddCar(route);
        var car = _cars[^1];
        car.Speed = speed;
        car.Planned = way;
        car.FromEndpoint = true;
        car.FromId = s.Ep.Id;
        car.ToId = way is not null ? share.To : 0;
        _generatedLive++;
        Generated++;
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
        if (_endpoints.TryGetValue(to, out var dest) && dest.Edge is { } destEdge && dest.Ep.Destination)
        {
            var goal = (destEdge, !dest.Forward);
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

    /// <summary>Takes the cars driving out past a destination: they leave the world there.</summary>
    private void TakeArrived()
    {
        bool any = false;
        foreach (var s in _endpoints.Values) any |= s.Ep.Destination && s.Edge is not null;
        if (!any) return;
        for (int i = _cars.Count - 1; i >= 0; i--)
        {
            var car = _cars[i];
            var edge = car.Route.Edge;
            int took = 0;
            foreach (var s in _endpoints.Values)
            {
                if (!s.Ep.Destination || s.Edge != edge || car.Route.Forward == s.Forward) continue;
                // driving out (against the endpoint's heading): past it once its place along the edge is beyond the endpoint's
                float pos = car.Route.Forward ? car.Route.Arc : edge.Length - car.Route.Arc;
                if (car.Route.Forward ? pos >= s.Pos : pos <= s.Pos) { took = s.Ep.Id; break; }
            }
            if (took == 0) continue;
            ArrivedBy[(car.FromId, took)] = ArrivedBy.GetValueOrDefault((car.FromId, took)) + 1;
            if (car.FromEndpoint) _generatedLive--;
            foreach (var u in car.Units) _byBody.Remove(u.GetInstanceId());
            car.Free();
            _cars.RemoveAt(i);
            Arrived++;
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
