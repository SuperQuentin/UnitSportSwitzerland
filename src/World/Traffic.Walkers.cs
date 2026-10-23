using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// Walkers (#1033): pedestrians on sidewalks and paths, crossing at crosswalks. At the lights a walker steps off only on its
/// crossing's pedestrian green (a two-stage crossing's halves each on their own group, #1030: it crosses a half, waits on the
/// island, crosses the other) and never in its last seconds; at a zebra without lights it waits only for a car too close to stop,
/// and the cars stop for it (<see cref="ZebraWait"/>). At a permissive turn the cars wait for the walkers on the crossing through
/// the shared crossers (#1046, <see cref="CrosserComing"/>). The walk graph: <see cref="LaneGraph.BuildWalks"/>. Seams of the shared
/// base (#1060, <c>Traffic.Modes.cs</c>); docs: <c>docs/notes/world/traffic-walkers.md</c>.
/// </summary>
public partial class Traffic
{
    /// <summary>Whether walkers have landed: their density row shows in the settings (Settings → World, the solo screen).</summary>
    public static bool WalkersReady => true;

    /// <summary>A walker: a 0.5 × 1.7 × 0.4 m orange box at 1.35 m/s ± 25 %, spawned 25 m out while filling, then 60-300 m; dropped past 400 m, drawn to 250 m.</summary>
    private static readonly MoverLook WalkerLook = new(new Vector3(0.5f, 1.7f, 0.4f), new Color(0.95f, 0.55f, 0.15f),
        Cruise: 1.35f, Spread: 0.25f, Near: 25f, SpawnMin: 60f, SpawnMax: 300f, Despawn: 400f, Drawn: 250f);

    /// <summary>The walk graph covers this far from the middle of the view's kilometre tile (m): 3 × 3 km, past every walker's 400 m.</summary>
    private const float WalkReach = 1500f;

    /// <summary>
    /// The walk graph (on a worker thread): sidewalks, paths, crosswalks (<c>CPAR</c> walker lines) and zebras, in <c>tiles.Origin</c>'s
    /// frame, 3 × 3 km about the view's tile (the middle of the roads' 5 × 5). Null with none (no walkers spawn).
    /// </summary>
    private static LaneGraph? BuildWalkGraph(ModeTiles tiles)
    {
        if (tiles.Near.Count == 0) return null;
        // the view's tile: the middle of the 5 x 5 the roads were built from
        int e = (tiles.Near.Min(t => t.Id.E) + tiles.Near.Max(t => t.Id.E) + 1) / 2;
        int n = (tiles.Near.Min(t => t.Id.N) + tiles.Near.Max(t => t.Id.N) + 1) / 2;
        var around = tiles.Origin.ToWorld(e * 1000.0 + 500.0, n * 1000.0 + 500.0, 0.0);
        var g = LaneGraph.BuildWalks(tiles.Near, tiles.Origin, tiles.Roads, around, WalkReach);
        GD.Print($"[traffic] walk graph: {g.Edges.Count} edges from {g.WalkSidewalks} sidewalks, {g.WalkPaths} paths, {g.WalkLit} lit crossings "
            + $"({g.WalkIslands} islands), {g.WalkZebras} zebras ({g.ZebraHits?.Count ?? 0} car edges they cross), {g.WalkUnmarked} unmarked, {g.WalkLinks} links; {g.WalkDropped} crossings left out (no walkway at an end)");
        return g.Edges.Count == 0 ? null : g;
    }

    /// <summary>How likely a walker spawns on an edge: never on a crossing or a link, mostly on sidewalks, seldom on a track.</summary>
    private static float WalkerWeight(LaneEdge edge) => edge.Connector ? 0f : edge.Class switch
    {
        RoadClass.Track => 0.1f,
        RoadClass.Path => 0.4f,
        _ => 1f,
    };

    /// <summary>A walker just placed on its route: on its own line across the walkway from the start.</summary>
    private void WalkerSpawned(SlowMover m)
    {
        m.Lateral = WalkerLine(m, m.Route.Edge);
        m.State = null;
    }

    /// <summary>
    /// Where across <paramref name="edge"/> this walker keeps (m right of its line): a little right of the middle, spread by its habit
    /// over the walkway's width (so a crowd at a kerb stands side by side).
    /// </summary>
    private static float WalkerLine(SlowMover m, LaneEdge edge)
    {
        float u = ((m.Habit >> 8) & 0xFF) / 255f - 0.5f;
        float room = Mathf.Max(0f, edge.Width * 0.5f - 0.3f);
        return Mathf.Clamp(0.15f + u * edge.Width * 0.8f, -room, room);
    }

    /// <summary>A walker stands this far short of a crossing it waits at (m): off the kerb's edge.</summary>
    private const float KerbBack = 0.6f;

    /// <summary>A walker never steps off on a lit crossing in its group's last seconds of green (s).</summary>
    private const double LastGreen = 2.5;

    /// <summary>A walker decides about a crossing from this far ahead of it (m).</summary>
    private const float CrossingLook = 4f;

    /// <summary>
    /// One tick of a walker's own rules: at the next crossing on its way (within <see cref="CrossingLook"/> m), a lit one's green
    /// (not its last <see cref="LastGreen"/> s), a zebra's gap (no car too close to stop for it; the cars that can are stopping, as it
    /// stands there to cross); otherwise its pace. Holding (not stuck) while it waits.
    /// </summary>
    private float StepWalker(SlowMover m, float dt)
    {
        var route = m.Route;
        route.Plan(CrossingLook + 6f, m.Next);
        m.Lateral = Mathf.MoveToward(m.Lateral, WalkerLine(m, route.Edge), 0.6f * dt);
        bool wasHolding = m.Holding;
        m.Holding = false;
        float want = m.Cruise;

        // on a crossing: a zebra it is on stays claimed (the cars stop for it); stepping onto a lit one, the check counts its aspect
        var here = LaneGraph.CrossingOf(route.Edge);
        if (here is { Zebra: true }) here.Claimed = _now;
        if (here is { Lit: true } lit && !ReferenceEquals(m.State, route.Edge))
        {
            m.State = route.Edge;
            WalkerLitStarts++;
            // (amber: the clearance after a green it set off on; red is a walker that never waited)
            switch (lit.Site!.Plan.State(lit.Group, SignalLamps.Clock()))
            {
                case SignalAspect.Amber: WalkerAmberStarts++; break;
                case SignalAspect.Red or SignalAspect.RedAmber: WalkerRedStarts++; break;
            }
        }
        else if (here is { Zebra: true } && !ReferenceEquals(m.State, route.Edge))
        {
            m.State = route.Edge;
            WalkerZebraCrossings++;
        }
        else if (here is { Unmarked: true } && !ReferenceEquals(m.State, route.Edge))
        {
            m.State = route.Edge;
            WalkerGapCrossings++;
        }
        else if (here is { TramYield: true } && !ReferenceEquals(m.State, route.Edge))
        {
            m.State = route.Edge;
            WalkerTramGapCrossings++;
            // (a tram within its braking distance of the crossing as the walker steps on: the check's count, #1176)
            if (TramAtCrossing(route.Edge, 0f, braking: true)) WalkerTramCloseSteps++;
        }

        // the next crossing ahead (Wait: 1 while it is clear to go on to it, Group: which one)
        bool crossingAhead = false;
        var legs = route.Legs;
        float ahead = route.Edge.Length - route.Arc;
        for (int i = route.Leg + 1; i < legs.Count && ahead < CrossingLook; i++)
        {
            var edge = legs[i].Edge;
            if (LaneGraph.CrossingOf(edge) is not { Island: false } c)
            {
                ahead += edge.Length;
                continue;
            }
            // stand back from the kerb (on an island, at most half its width)
            float back = here is { Island: true } ? Mathf.Min(KerbBack, route.Edge.Length * 0.5f) : KerbBack;
            float stopIn = ahead - back;
            bool go;
            if (c.Lit)
            {
                double clock = SignalLamps.Clock();
                go = c.Site!.Plan.State(c.Group, clock) == SignalAspect.Green 
                    && c.Site.Plan.UntilChange(c.Group, clock) > LastGreen + ahead / Mathf.Max(m.Speed, 0.7f);
            }
            else if (c.Unmarked)
                // no right of way: a gap long enough to walk over, with a margin
                go = !CarAtZebra(edge, edge.Length / Mathf.Max(m.Cruise, 0.8f) + UnmarkedMargin);
            else if (c.TramYield)
                // the trams have priority (#1176, #1115): a gap in them long enough to walk over, with a margin
                go = !TramAtCrossing(edge, edge.Length / Mathf.Max(m.Cruise, 0.8f) + UnmarkedMargin);
            else
            {
                c.Claimed = _now;
                go = !CarAtZebra(edge);
            }
            // already on its way to it when it was clear, and too close to stop at a walker's pace now: it goes
            int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(edge);
            if (!go && m.Wait > 0f && m.Group == id && m.Speed > 0.5f && stopIn < m.Speed * m.Speed / (2f * 3f)) go = true;
            m.Wait = go ? 1f : 0f;
            m.Group = id;
            crossingAhead = true;
            if (!go)
            {
                want = Mathf.Min(want, Mathf.Sqrt(2f * 2.5f * Mathf.Max(0f, stopIn)));
                if (stopIn < 1f || m.Speed < 0.2f) m.Holding = true;
                if (m.Holding && !wasHolding)
                {
                    if (here is { Island: true }) WalkerIslandWaits++;
                    else if (c.Lit) WalkerLitWaits++;
                    else if (c.Unmarked) WalkerGapWaits++;
                    else if (c.TramYield) WalkerTramWaits++;
                    else WalkerZebraWaits++;
                }
            }
            break;
        }
        if (!crossingAhead) m.Wait = 0f;
        return want;
    }

    /// <summary>Where a walker walks across its edge: its own line (eased over as it changes walkway).</summary>
    private float WalkerLateral(SlowMover m) => m.Lateral;

    /// <summary>A walker's next leg at a junction of the walk graph: any way on at random (back at a dead end).</summary>
    private (LaneEdge, bool)? NextWalk(SlowMover m, (LaneEdge Edge, bool Forward) leg)
    {
        // a walker from an endpoint (#1134): on its shortest way to its destination
        if (m.Goal is not null && _walks is { } g)
        {
            long key = leg.Forward ? leg.Edge.KeyEnd : leg.Edge.KeyStart;
            _nextOptions.Clear();
            foreach (var o in g.Leaving(key))
                if (o.Edge != leg.Edge) _nextOptions.Add(o);
            if (TowardGoal(m, _nextOptions) is { } way) return way;
        }
        return NextAny(_walks, leg);
    }

    // ---- zebras: the cars stop for the walkers, the walkers for a car too close to stop --------------

    /// <summary>A car brakes for a zebra at up to this (m/s²); one that would need more is committed and goes.</summary>
    private const float ZebraBrake = 4f;

    /// <summary>A walker crossing a side road with no marking (#1033) wants the cars this many seconds further off than its walk over.</summary>
    private const float UnmarkedMargin = 2f;

    /// <summary>
    /// Whether a car is on the crossing <paramref name="zebra"/> or coming at it too fast to stop short of it (more than
    /// <see cref="ZebraBrake"/>): the walker at it waits. With <paramref name="gap"/> (s, an unmarked crossing where the cars do not
    /// yield): also any car reaching it sooner, or moving within 8 m of it (one turning in).
    /// </summary>
    /// <summary>
    /// Whether a tram is on the crossing <paramref name="crossing"/> (its body within a tram's half width plus a metre of it) or
    /// reaches it within <paramref name="gap"/> s (#1176: walkers give way to trams at an unlit stage over their tracks);
    /// <paramref name="braking"/>: or could not stop short of it at its service brake.
    /// </summary>
    private const float TramBodyLength = 44f;

    private bool TramAtCrossing(LaneEdge crossing, float gap, bool braking = false)
    {
        var a = crossing.Points[0];
        var b = crossing.Points[^1];
        foreach (var t in _trams)
        {
            if (Mathf.Abs(t.Head.Y - a.Y) > 4f) continue;
            // its body: the head and the points laid back along its track
            float d = DistanceToSegmentFlat(t.Head, a, b);
            for (float back = 4f; back <= TramBodyLength && d > 2.2f; back += 4f) d = Mathf.Min(d, DistanceToSegmentFlat(t.Route.At(-back).Pos, a, b));
            if (d < 2.2f) return true;
            if (t.Speed < 0.5f) continue;
            // coming at it: along its way, how far its head is from the crossing's line
            var dir = MathX.Flat(t.Vel).Normalized();
            if (dir.LengthSquared() < 0.5f) continue;
            float toward = TowardCrossing(t.Head, dir, a, b);
            if (toward < 0f || toward > 120f) continue;
            if (gap > 0f && toward / t.Speed < gap) return true;
            if (braking && t.Speed * t.Speed / (2f * TramBrake) > toward - 1f) return true;
        }
        return false;
    }

    /// <summary>How far along <paramref name="dir"/> from <paramref name="p"/> the line through a-b lies (m), -1 where it never meets it or misses the segment by more than 3 m.</summary>
    private static float TowardCrossing(Vector3 p, Vector3 dir, Vector3 a, Vector3 b)
    {
        var ab = MathX.Flat(b - a);
        var n = new Vector3(-ab.Z, 0f, ab.X);
        float denom = dir.Dot(n);
        if (Mathf.Abs(denom) < 1e-4f) return -1f;
        float t = MathX.Flat(a - p).Dot(n) / denom;
        if (t < 0f) return -1f;
        var hit = MathX.Flat(p + dir * t);
        return DistanceToSegmentFlat(hit, a, b) > 3f ? -1f : t;
    }

    private static float DistanceToSegmentFlat(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = MathX.Flat(b - a);
        var ap = MathX.Flat(p - a);
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-6f ? 0f : Mathf.Clamp(ap.Dot(ab) / len2, 0f, 1f);
        return (ap - ab * t).Length();
    }

    private bool CarAtZebra(LaneEdge zebra, float gap = 0f)
    {
        var a = zebra.Points[0];
        var b = zebra.Points[^1];
        var mid = (a + b) * 0.5f;
        var across = MathX.Flat(b - a);
        float half = across.Length() * 0.5f;
        if (half < 0.1f) return false;
        across /= half * 2f;
        // (the zebra lies across the road: the cars' way runs along its normal)
        var normal = new Vector3(-across.Z, 0f, across.X);
        _xKey.Head.X = mid.X - 60f;
        int from = _byX.BinarySearch(_xKey, ByX);
        for (int o = from < 0 ? ~from : from; o < _byX.Count && _byX[o].Head.X <= mid.X + 60f; o++)
        {
            var car = _byX[o];
            var d = MathX.Flat(mid - car.Head);
            if (Mathf.Abs(mid.Y - car.Head.Y) > 4f) continue;
            if (gap > 0f && car.Speed > 1f && d.Length() < half + 8f) return true;
            // within the zebra's length of its middle, across: on the road it spans
            if (Mathf.Abs(d.Dot(across)) > half + 1f) continue;
            float along = d.Dot(normal);
            var (_, dir) = car.Route.At(0f);
            float toward = dir.Dot(normal) >= 0f ? along : -along;
            // on it (its body over the 3 m band)
            if (Mathf.Abs(toward) < CarFront + 1.5f) return true;
            if (toward < 0f) continue;
            float room = toward - CarFront - 1.5f;
            if (car.Speed > 1f && car.Speed * car.Speed / (2f * ZebraBrake) > room - 0.5f) return true;
            if (gap > 0f && car.Speed > 0.5f && room / car.Speed < gap) return true;
        }
        return false;
    }

    /// <summary>
    /// The speed a car may still do toward a zebra on its way where a walker stands to cross or is crossing (#1033): it stops
    /// short of the zebra's band, unless it is already too close to stop (<see cref="ZebraBrake"/>). +∞ with none.
    /// </summary>
    private float ZebraWait(Vehicle car, float dt)
    {
        if (_walkers.Count == 0 || _walks?.ZebraHits is not { Count: > 0 } hits) return float.MaxValue;
        float result = float.MaxValue;
        float along = -car.Route.Arc;
        var legs = car.Route.Legs;
        for (int i = car.Route.Leg; i < legs.Count && along < 50f; i++)
        {
            var (e, forward) = legs[i];
            if (hits.TryGetValue(e, out var list))
                foreach (var (arc, zebra) in list)
                {
                    if (_now - zebra.Claimed > 0.5) continue;
                    float gap = along + (forward ? arc : e.Length - arc) - CarFront - 1.5f - 0.5f;
                    // its front in the band or past it, or too close to stop: committed
                    if (gap < -0.5f || car.Speed * car.Speed / (2f * ZebraBrake) > gap + 0.5f) continue;
                    result = Mathf.Min(result, StopWithin(gap));
                }
            along += e.Length;
        }
        if (result < float.MaxValue && car.Speed < StartSpeed) ZebraCarSeconds += dt;
        return result;
    }

    // ---- --trafficcheck --mover walker ----------------------------------------------------------

    /// <summary>The walkers' counts for the check (#1033): steps onto a lit crossing (on a red; on the clearance amber, cleared on green), waits at a red kerb, on an island, at a zebra, zebras crossed.</summary>
    public int WalkerLitStarts, WalkerRedStarts, WalkerAmberStarts, WalkerLitWaits, WalkerIslandWaits, WalkerZebraWaits, WalkerZebraCrossings,
        WalkerGapCrossings, WalkerGapWaits, WalkerTramGapCrossings, WalkerTramWaits, WalkerTramCloseSteps;

    /// <summary>Car-seconds stood for a walker at a zebra (#1033).</summary>
    public double ZebraCarSeconds;

    /// <summary>
    /// The cars standing at a permissive turn's conflict point (#1046) with a walker on a crossing of the same lights within 20 m:
    /// the cars waiting for the walkers (the probe, once a tick).
    /// </summary>
    public int CarsWaitingForWalkers()
    {
        int n = 0;
        foreach (var car in _cars)
        {
            if (!car.GapWaiting || car.Speed >= StartSpeed || car.GapPath?.Site is not { } site) continue;
            foreach (var w in _walkers)
                if (LaneGraph.CrossingOf(w.Route.Edge) is { } c && c.Site == site && MathX.FlatDistance(w.Head, car.Head) < 20f)
                {
                    n++;
                    break;
                }
        }
        return n;
    }

    /// <summary>A walker spawned on a walkway <paramref name="minDist"/>-<paramref name="maxDist"/> m from <paramref name="at"/> (the probe's <c>--at</c>: walkers where the junction under test is).</summary>
    public void SpawnWalkerNear(Vector3 at, float minDist, float maxDist)
    {
        if (_walks is not { Edges.Count: > 0 } g) return;
        // the walkways about it, picked from (the whole graph's random spots seldom land that near)
        if (_walksNear.Graph != g || _walksNear.At.DistanceTo(at) > 1f)
        {
            var near = LaneGraph.Empty(g.Frame);
            foreach (var e in g.Edges)
                if (MathX.FlatDistance(e.Points[0], at) < maxDist + 50f || MathX.FlatDistance(e.Points[^1], at) < maxDist + 50f) near.Add(e);
            _walksNear = (g, at, near);
        }
        if (_walksNear.Near is { Edges.Count: > 0 } pick) SpawnMover(MoverKind.Walker, pick, at, minDist, maxDist);
    }

    private (LaneGraph? Graph, Vector3 At, LaneGraph? Near) _walksNear;

    /// <summary>How many walkers wait on purpose now, and how many stand still.</summary>
    public (int Holding, int Standing) WalkersStill()
    {
        int holding = 0, standing = 0;
        foreach (var w in _walkers)
        {
            if (w.Holding) holding++;
            if (w.Speed < 0.1f) standing++;
        }
        return (holding, standing);
    }
}
