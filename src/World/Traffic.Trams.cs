using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// Trams (#1057): articulated trams on the street tracks, stopping at tram stops, obeying the tram bar signals, given way to by
/// turning cars. A tram is a <see cref="Vehicle"/> like a train (box sections at fixed offsets on one route, <see cref="AddTram"/>)
/// on the tram graph (<see cref="LaneGraph.BuildTrams"/>: every tram track the way its trams run, the junction paths RoadGen lays
/// through the cores in place of the tracks there). Per tick it takes the speed it may do: its line's (a street, its own bed, a
/// junction), its curves', the tram or car ahead on its track, its light and its stop; it eases to it like a tram, gently.
/// Docs: <c>docs/notes/world/traffic-trams.md</c>; the shared base: <c>traffic-modes</c>.
/// </summary>
public partial class Traffic
{
    /// <summary>Whether trams have landed: their density row shows in the settings (Settings → World, the solo screen).</summary>
    public static bool TramsReady => true;

    /// <summary>A tram section: 2.4 m wide, 3.4 m high, blue (its length per section, <see cref="AddTram"/>); 11 m/s; spawned 300-1500 m out, dropped past 2000 m, drawn to 1200 m.</summary>
    private static readonly MoverLook TramLook = new(new Vector3(2.4f, 3.4f, 9f), new Color(0.2f, 0.45f, 0.9f),
        Cruise: 11f, Spread: 0f, Near: 300f, SpawnMin: 300f, SpawnMax: 1500f, Despawn: 2000f, Drawn: 1200f);

    /// <summary>
    /// The trams' sections (m): a three-section low-floor tram (~26 m, Geneva's Cityrunner) or a five-section one (~42 m, its Tango,
    /// Zurich's Flexity), the sections a little apart (the gangways).
    /// </summary>
    private static readonly float[] TramShort = [9f, 7f, 9f], TramLong = [8.6f, 6.4f, 8.6f, 6.4f, 8.6f];
    private const float TramGangway = 0.6f;

    /// <summary>
    /// Line speeds (m/s): 40 km/h in a street (an embedded track, a town's paved bed), 50 km/h on its own bed, 25 km/h through a
    /// junction core (its switches, tight curves and turns).
    /// </summary>
    private const float TramStreet = 11f, TramOwnBed = 14f, TramThrough = 7f;

    /// <summary>
    /// How a tram drives (m/s²): it accelerates at 1.0, brakes at 1.2 for its lights, stops and curves (a service brake standing
    /// passengers keep their feet in), harder only for what cuts in; it takes curves at 1.0 sideways.
    /// </summary>
    private const float TramAccel = 1.0f, TramBrake = 1.2f, TramHardBrake = 2.8f, TramLateral = 1.0f;

    /// <summary>How far ahead a tram reads its line, its lights and what stands on its track (m).</summary>
    private const float TramSight = 140f;

    /// <summary>It stops this far behind what stands ahead on its track (m); its front this far short of its stop line.</summary>
    private const float TramStandOff = 3f, TramLineMargin = 0.8f;

    /// <summary>Something is on a tram's line within this much of its track's centre (m): the tram's half width and a margin.</summary>
    private const float TramReach = 1.9f;

    /// <summary>Trams spawn no nearer than this to another tram (m).</summary>
    private const float TramSpawnClear = 120f;

    /// <summary>
    /// The tram graph (on a worker thread): every tram track of the 7x7 km around (<see cref="LaneGraph.BuildTrams"/>), each the way its
    /// trams run, joined through the junction cores by RoadGen's tram paths where it laid them.
    /// </summary>
    private static LaneGraph? BuildTramGraph(ModeTiles tiles) => LaneGraph.BuildTrams(tiles.Wide, tiles.Origin);

    /// <summary>A tram spawns on a track of its line, not on a junction path.</summary>
    private static float TramWeight(LaneEdge e) => e.Path is null && e.Length > 4f ? 1f : 0f;

    /// <summary>One more tram somewhere on the tram graph (<see cref="_tramLines"/>, not null here), out of sight of the view.</summary>
    private void SpawnTram(Vector3 focus)
    {
        var graph = _tramLines!;
        if (graph.RandomSpot(_rng, focus, TramLook.SpawnMin, TramLook.SpawnMax, TramWeight) is not var (edge, arc)) return;
        var (spot, _) = edge.Sample(arc);
        foreach (var t in _trams)
            if (MathX.Flat(t.Head - spot).Length() < TramSpawnClear) return;
        bool forward = edge.OneWay switch { 1 => true, -1 => false, _ => _rng.Next(2) == 0 };
        var lengths = _rng.Next(2) == 0 ? TramShort : TramLong;
        float length = TramGangway * (lengths.Length - 1);
        foreach (float l in lengths) length += l;
        // its sections behind it on the track it came along (else they would stand bunched at the start of the head's edge)
        var route = new Route(edge, forward, forward ? arc : edge.Length - arc);
        if (!BackFill(graph, route, length + 5f)) return;
        var tram = AddTram(edge, forward, arc, lengths, TramGangway, TramStreet);
        tram.Route = route;
        tram.Speed = 0f;
        tram.Place(TramLateralFn);
        TramsSpawned++;
    }

    /// <summary>
    /// Grows <paramref name="route"/> backwards until <paramref name="length"/> m lie behind its head: the legs a tram would have come
    /// along (straight on into it). False where the track ends before.
    /// </summary>
    private static bool BackFill(LaneGraph graph, Route route, float length)
    {
        float behind = route.Arc;
        while (behind < length && route.Legs.Count < 64)
        {
            var (e, f) = route.Legs[0];
            long key = f ? e.KeyStart : e.KeyEnd;
            var (_, t) = e.Sample(f ? 0f : e.Length);
            var dir = f ? t : -t;
            (LaneEdge Edge, bool Forward)? best = null;
            float straightest = 0.5f;
            foreach (var o in graph.Entering(key))
            {
                if (o.Edge == e) continue;
                var (_, u) = o.Edge.Sample(o.Forward ? o.Edge.Length : 0f);
                float straight = (o.Forward ? u : -u).Dot(dir);
                if (straight > straightest) (best, straightest) = (o, straight);
            }
            if (best is not { } b) return false;
            route.Legs.Insert(0, b);
            route.Leg++;
            behind += b.Edge.Length;
        }
        return behind >= length;
    }

    /// <summary>
    /// Metres right of its edge's line a tram runs: a line mapped with two tracks has its trams on the right one (as a train); a
    /// single track, a junction path (laid on the track itself), on it.
    /// </summary>
    private static float TramLateralOf(LaneEdge e) => e.Path is null ? RoadFormat.TrackOffset(e.Flags) : 0f;
    private Func<LaneEdge, float>? _tramLateral;
    private Func<LaneEdge, float> TramLateralFn => _tramLateral ??= TramLateralOf;

    /// <summary>
    /// The line speed of a tram's edge: through a junction core, in a street (embedded, or a narrow formation: a town's paved bed),
    /// on its own bed.
    /// </summary>
    private float TramLimit(LaneEdge e) =>
        e.Path is not null ? TramThrough : _tramLines?.InStreet(e) == true || e.Width < 4f ? TramStreet : TramOwnBed;

    /// <summary>Where a tram looks ahead along its track (reused: no allocation per tick), every <see cref="TramAheadStep"/> m.</summary>
    private const float TramAheadStep = 2.5f;
    private readonly Vector3[] _tramAhead = new Vector3[(int)(TramSight / TramAheadStep) + 2];
    private readonly Vector3[] _tramAheadDir = new Vector3[(int)(TramSight / TramAheadStep) + 2];

    /// <summary>One tick of a tram: the speed its line, its curves, the way ahead, its light and its stop allow; on along its route.</summary>
    private void StepTram(Vehicle tram, float dt)
    {
        var route = tram.Route;
        var next = _nextTramLeg ??= NextTramLeg;
        route.Plan(TramSight, next);
        float front = TramFrontOf(tram);

        // the line ahead, as the tram will run it (its track's offset on each leg)
        int n = 0;
        for (float s = 0f; s <= TramSight && n < _tramAhead.Length; s += TramAheadStep, n++)
        {
            var (p, d, leg) = route.AtLeg(-s);
            _tramAhead[n] = p + new Vector3(-d.Z, 0f, d.X).Normalized() * TramLateralOf(route.Legs[leg].Edge);
            _tramAheadDir[n] = d;
        }

        // its line's speed, and the speed each leg ahead asks for, braked to in time; its light
        float target = TramLimit(route.Edge);
        float along = -route.Arc, end = float.MaxValue;
        bool holding = false, atRed = false;
        for (int i = route.Leg; i < route.Legs.Count && along < TramSight; i++)
        {
            var (e, fwd) = route.Legs[i];
            if (i > route.Leg) target = Mathf.Min(target, TramBraked(TramLimit(e), along - front));
            if (fwd && e.Path is { Site: { } site, Group: >= 0 } p && p.Group < site.Plan.Groups.Count)
            {
                float line = TramLight(tram, site, p.Group, along - (_tramLines?.TramStopBack(e) ?? 0f) - front, dt);
                if (line < target) { target = line; holding = true; atRed = true; }
            }
            along += e.Length;
            if (i + 1 == route.Legs.Count) end = along;   // the end of the track it knows: it stops there
        }
        if (end < float.MaxValue) target = Mathf.Min(target, TramStopWithin(end - front - 1f));
        if (!atRed) tram.AtRed = false;

        // its curves: a bend of radius r at 1.0 m/s² sideways, braked to in time
        for (int k = 4; k < n; k += 2)
        {
            float turn = Mathf.Acos(Mathf.Clamp(_tramAheadDir[k - 4].Dot(_tramAheadDir[k]), -1f, 1f));
            if (turn < 0.02f) continue;
            float radius = 4f * TramAheadStep / turn;
            target = Mathf.Min(target, TramBraked(Mathf.Sqrt(TramLateral * radius), (k - 4) * TramAheadStep - front));
        }

        // what stands or moves on its track ahead: a tram, a car, a player
        float follow = TramFollow(tram, n, front);
        if (follow < target) { target = follow; holding |= follow < 1f; }

        target = Mathf.Max(0f, target);
        tram.Speed = Mathf.MoveToward(tram.Speed, target, (target > tram.Speed ? TramAccel : TramHardBrake) * dt);
        var before = tram.Head;
        bool moved = route.Advance(tram.Speed * dt, next);
        route.Trim(tram.Offsets[^1] + 20f);
        tram.Place(TramLateralFn);
        tram.Vel = dt > 0f ? MathX.Flat(tram.Head - before) / dt : Vector3.Zero;
        for (int k = 0; k < PathSteps; k++) tram.Path[k] = route.At(-tram.Speed * 0.5f * (k + 1)).Pos;
        tram.Holding = holding;
        // standing at the end of the track it knows: the end of the line, dropped after a while; held on purpose is not stuck
        tram.Stuck = !moved || end - front < 3f && tram.Speed < 0.1f ? tram.Stuck + dt : 0f;
    }

    /// <summary>
    /// A tram's light (#1057, its tram bar signal: the group of its path through a lit junction) with its line
    /// <paramref name="toLine"/> m ahead of its front: the speed it may do. The bar across (red) or red-and-dot: it stops at the line;
    /// the dot alone ("stop expected", the amber): it stops if it can at its service brake, else it clears (as a car's amber); the
    /// bar upright or slanted: it goes. +∞ where nothing holds it.
    /// </summary>
    private float TramLight(Vehicle tram, SignalSite site, int group, float toLine, float dt)
    {
        if (toLine < -1f) return float.MaxValue;
        var aspect = site.Plan.State(group, SignalLamps.Clock());
        float room = toLine - TramLineMargin;
        bool stop = aspect switch
        {
            SignalAspect.Red or SignalAspect.RedAmber => true,
            SignalAspect.Amber => !tram.ClearingAmber && tram.Speed * tram.Speed / (2f * TramBrake) < room + 1f,
            _ => false,
        };
        if (aspect == SignalAspect.Amber) tram.ClearingAmber |= !stop;
        else tram.ClearingAmber = false;
        // its front over the line: once, counted (on red only while it does not clear the dot: a check, must stay 0)
        if (toLine <= 0f && toLine > -tram.Speed * dt - 0.05f)
        {
            TramLinesCrossed++;
            if (aspect is SignalAspect.Red && !tram.ClearingAmber) TramRedRuns++;
        }
        if (!stop) return float.MaxValue;
        if (!tram.AtRed && toLine < 15f && tram.Speed < 0.5f) { tram.AtRed = true; TramRedStops++; }
        return TramStopWithin(room);
    }

    /// <summary>Its front past its head section's centre (m): that section's half length.</summary>
    private static float TramFrontOf(Vehicle tram) => tram.UnitHalf + 1f;

    /// <summary>The speed from which a tram stops in <paramref name="gap"/> m at its service brake.</summary>
    private static float TramStopWithin(float gap) => Mathf.Sqrt(2f * TramBrake * Mathf.Max(0f, gap));

    /// <summary>The speed a tram may do now to be at <paramref name="limit"/> in <paramref name="distance"/> m at its service brake.</summary>
    private static float TramBraked(float limit, float distance) =>
        distance <= 0f ? limit : Mathf.Sqrt(limit * limit + 2f * TramBrake * distance);

    /// <summary>
    /// The speed a tram may do behind what stands or moves on its track ahead (its line in <see cref="_tramAhead"/>, <paramref name="n"/>
    /// points): another tram's sections, a car whose body is on its track, a player; +∞ with none.
    /// </summary>
    private float TramFollow(Vehicle tram, int n, float front)
    {
        float best = float.MaxValue, reach = TramSight + 10f;
        foreach (var other in _trams)
        {
            if (other == tram || MathX.Flat(other.Head - tram.Head).LengthSquared() > (reach + 50f) * (reach + 50f)) continue;
            for (int i = 0; i < other.Units.Length; i++)
                best = Mathf.Min(best, TramBehind(n, front, other.Units[i].GlobalPosition, other.Vel, other.UnitHalf + 1f));
        }
        // the cars near (the _byX window)
        _xKey.Head.X = tram.Head.X - reach;
        int from = _byX.BinarySearch(_xKey, ByX);
        for (int o = from < 0 ? ~from : from; o < _byX.Count && _byX[o].Head.X <= tram.Head.X + reach; o++)
            best = Mathf.Min(best, TramBehind(n, front, _byX[o].Head, _byX[o].Vel, CarFront));
        foreach (var (pos, vel) in _obstacles) best = Mathf.Min(best, TramBehind(n, front, pos, vel, 0.5f));
        return best;
    }

    /// <summary>The speed a tram may do behind something at <paramref name="pos"/> (half its length <paramref name="half"/>) if it is on its line ahead; +∞ if not.</summary>
    private float TramBehind(int n, float front, Vector3 pos, Vector3 vel, float half)
    {
        for (int k = 0; k < n; k++)
        {
            var d = pos - _tramAhead[k];
            if (Mathf.Abs(d.Y) > 4f) continue;
            var u = _tramAheadDir[k];
            float along = d.X * u.X + d.Z * u.Z;
            if (Mathf.Abs(along) > TramAheadStep * 0.6f || Mathf.Abs(d.X * u.Z - d.Z * u.X) > TramReach) continue;
            float gap = k * TramAheadStep + along - front - half - TramStandOff;
            // beside or behind its front: not ahead of it
            if (k * TramAheadStep + along < front * 0.5f) return float.MaxValue;
            float speed = Mathf.Max(0f, vel.X * u.X + vel.Z * u.Z);
            return Mathf.Sqrt(speed * speed + 2f * TramBrake * Mathf.Max(0f, gap));
        }
        return float.MaxValue;
    }

    /// <summary>A tram's next leg (the trains' rule on the tram graph: only lines that carry straight on).</summary>
    private (LaneEdge, bool)? NextTramLeg((LaneEdge Edge, bool Forward) leg) => _tramLines is { } g ? NextTrack(g, leg) : null;
    private Func<(LaneEdge Edge, bool Forward), (LaneEdge, bool)?>? _nextTramLeg;

    /// <summary>Whether a tram is in a car lane now (street running: an embedded track, a junction path): the cars behind it follow it.</summary>
    private bool TramInLane(Vehicle tram) => tram.Route.Edge.Path is not null || _tramLines?.InStreet(tram.Route.Edge) == true;

    // ---- --trafficcheck --mover tram -----------------------------------------------------------

    /// <summary>Trams spawned; stopped at their light; fronts over a lit line; over it on red (must stay 0).</summary>
    public int TramsSpawned { get; private set; }
    public int TramRedStops { get; private set; }
    public int TramLinesCrossed { get; private set; }
    public int TramRedRuns { get; private set; }

    /// <summary>The trams' top speed now (m/s).</summary>
    public float TramTopSpeed
    {
        get
        {
            float top = 0f;
            foreach (var t in _trams) top = Mathf.Max(top, t.Speed);
            return top;
        }
    }
}
