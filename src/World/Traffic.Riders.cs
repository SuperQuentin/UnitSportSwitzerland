using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// Riders (#1056): bikes on bike lanes, bike paths and the overlay's cycleways (contraflow where OSM maps it), and in the car lane
/// of a street with none (cars follow them there), through junctions on their own paths (<c>JPTH</c> bike and path ways, #971)
/// and bike groups: the lead green and the bike group's own aspect, right on red where signal 5.18 stands (#711), the left turn
/// from the left-turn bike lane, the advanced stop line or the bike box (#351). Cars give way to them at their conflict points
/// (#1046, <see cref="CrosserComing"/>) with no change of theirs. Seams of the shared base (#1060, <c>Traffic.Modes.cs</c>); the
/// graph is <see cref="LaneGraph.BuildRides"/> (<c>LaneGraph.Riders.cs</c>). Docs: <c>docs/notes/world/traffic-riders.md</c>.
/// </summary>
public partial class Traffic
{
    /// <summary>Whether riders have landed: their density row shows in the settings (Settings → World, the solo screen).</summary>
    public static bool RidersReady => true;

    /// <summary>A rider: a 0.6 × 1.7 × 1.8 m green box at 5 m/s ± 25 %, spawned 40 m out while filling, then 100-600 m; dropped past 800 m, drawn to 400 m.</summary>
    private static readonly MoverLook RiderLook = new(new Vector3(0.6f, 1.7f, 1.8f), new Color(0.25f, 0.75f, 0.3f),
        Cruise: 5f, Spread: 0.25f, Near: 40f, SpawnMin: 100f, SpawnMax: 600f, Despawn: 800f, Drawn: 400f);

    /// <summary>A rider's own state (<see cref="SlowMover.State"/>): the leg it was placed on, the approach it reads.</summary>
    private sealed class RiderState
    {
        public LaneEdge? Edge;
        public bool Forward;
        public LaneApproach? Approach;
        public float ToLine = float.MaxValue;
        public bool WasAtRed, Clearing;
        /// <summary>How far it has ridden on its leg (m), for the probe's lateral check.</summary>
        public float OnLeg;
    }

    /// <summary>What riders did, for <c>--trafficcheck --mover rider</c> (#1056).</summary>
    public sealed class RiderCounts
    {
        public int RedStops, RedRuns, AmberClears, GreenCrossings, RightOnRed, Leads, BikeLefts, CarsWaited;
        /// <summary>Rider ticks per where they rode (<see cref="RideSide"/>: road, lane, track, way; then riders' and cars' junction paths).</summary>
        public readonly long[] On = new long[6];
        /// <summary>The furthest a settled rider was from its line (m), and how many ticks over 1 m.</summary>
        public float LateralOff;
        public int LateralOver;
    }

    public RiderCounts Riders { get; } = new();

    /// <summary>A rider decelerates this hard for a red (m/s²), and judges an amber by it.</summary>
    private const float RideBrake = 2.5f;

    /// <summary>A rider chooses its legs this far ahead (m), and reads the lights this far before its line.</summary>
    private const float RidePlan = 60f, RideLook = 50f;

    /// <summary>A rider moves over to its junction path's first lane (a left-turn bike lane, the kerbside car lane) over these last metres.</summary>
    private const float RideLeadIn = 30f;

    /// <summary>
    /// The ride graph (on a worker thread): bike lanes, bike paths, cycleways, <c>JPTH</c> bike paths, roads riders share, in
    /// <c>tiles.Origin</c>'s frame (<see cref="LaneGraph.BuildRides"/>).
    /// </summary>
    private static LaneGraph? BuildRideGraph(ModeTiles tiles) => LaneGraph.BuildRides(tiles.Near, tiles.Origin, tiles.Roads);

    /// <summary>How likely a rider spawns on an edge: its bike lanes and paths most, the tracks less, a street with none little, junctions never.</summary>
    private static float RiderWeight(LaneEdge edge)
    {
        if (edge.Connector || LaneGraph.RideOf(edge) is not { } w) return 0f;
        static float Of(RideSide s) => s switch { RideSide.Lane or RideSide.Track => 1f, RideSide.Way => 0.4f, RideSide.Road => 0.15f, _ => 0f };
        return Mathf.Max(Of(w.Fwd), Of(w.Bwd));
    }

    /// <summary>A rider just placed on its route: on its line at once, its state fresh.</summary>
    private void RiderSpawned(SlowMover m)
    {
        // a side nobody rides that way (a one-way street's): the other way
        if (LaneGraph.RideOf(m.Route.Edge)?.Side(m.Route.Forward) == RideSide.None)
            m.Route = new Route(m.Route.Edge, !m.Route.Forward, m.Route.Edge.Length - m.Route.Arc);
        m.State = new RiderState { Edge = m.Route.Edge, Forward = m.Route.Forward };
        m.Lateral = RideTarget(m);
    }

    /// <summary>
    /// One tick of a rider's rules: its legs chosen ahead, a slower rider or (in the car lane) a car ahead followed, bends taken
    /// gently, the lights of its approach read (<see cref="RideLights"/>), its line eased toward (<see cref="RideTarget"/>).
    /// </summary>
    private float StepRider(SlowMover m, float dt)
    {
        var st = (RiderState)m.State!;
        m.Holding = false;
        m.Route.Plan(RidePlan, m.Next);
        var (_, dir) = m.Route.At(0f);
        float target = m.Cruise;
        // a bend ahead: no more than ~2.5 m/s² sideways over the next 10 m
        var (_, aheadDir) = m.Route.At(-10f);
        float bend = Mathf.Acos(Mathf.Clamp(dir.Dot(aheadDir), -1f, 1f));
        if (bend > 0.1f) target = Mathf.Min(target, Mathf.Max(2.5f, Mathf.Sqrt(2.5f * 10f / bend)));
        target = Mathf.Min(target, RideLights(m, st));
        var edge = m.Route.Edge;
        var way = LaneGraph.RideOf(edge);
        var side = way?.Side(m.Route.Forward) ?? RideSide.Road;
        m.InLane = edge.Path is null ? side == RideSide.Road && way?.Segment is not null : way is { Bike: null };
        float follow = RideFollow(m, dir);
        // standing behind one who waits (a red, a queue of cars): waiting too, not stuck
        if (follow < 0.5f && target > 0.5f) m.Holding = true;
        target = Mathf.Min(target, follow);

        float want = RideTarget(m);
        m.Lateral = Mathf.MoveToward(m.Lateral, want, (0.4f + 0.25f * m.Speed) * dt);
        st.OnLeg += m.Speed * dt;
        // --trafficcheck: where it rides, and how far from its line once settled on a street
        int on = edge.Path is not null ? (way?.Bike is not null ? 4 : 5) : side == RideSide.None ? 0 : (int)side;
        Riders.On[Math.Clamp(on, 0, 5)]++;
        if (edge.Path is null && st.OnLeg > 25f && m.Speed > 1f)
        {
            float off = Mathf.Abs(m.Lateral - want);
            Riders.LateralOff = Mathf.Max(Riders.LateralOff, off);
            if (off > 1f) Riders.LateralOver++;
        }
        return target;
    }

    /// <summary>
    /// Where a rider wants to ride across its leg (m right of its line): its lane, path or the car lane's right part
    /// (<see cref="LaneGraph.RideLateral"/>), moving over in the last <see cref="RideLeadIn"/> m to where its junction path starts
    /// (the left-turn bike lane for a left, #351); a junction path's own line.
    /// </summary>
    private float RideTarget(SlowMover m)
    {
        var (edge, forward) = (m.Route.Edge, m.Route.Forward);
        var way = LaneGraph.RideOf(edge);
        // the path ahead, and how far
        var legs = m.Route.Legs;
        JunctionPathInfo? path = null;
        float to = edge.Length - m.Route.Arc;
        if (edge.Path is null)
            for (int i = m.Route.Leg + 1; i < legs.Count && to < RideLeadIn + 15f; i++)
            {
                if (legs[i].Edge.Path is { } p) { path = p; break; }
                if (legs[i].Edge.Connector) break;
                to += legs[i].Edge.Length;
            }
        float lateral = LaneGraph.RideLateral(edge, way, forward, m.Route.Arc, path?.Move ?? SignalMoves.None);
        if (path is not null && to < RideLeadIn)
            lateral = Mathf.Lerp(lateral, path.FromOffset, Mathf.Clamp((RideLeadIn - to) / (RideLeadIn - 5f), 0f, 1f));
        return lateral;
    }

    /// <summary>
    /// Where a rider rides across its edge: <see cref="SlowMover.Lateral"/>, eased in <see cref="StepRider"/>. On a new leg it
    /// carries on from where it is (a path starts on its lane, a road's lane lies where the path ended), then eases to its line.
    /// </summary>
    private float RiderLateral(SlowMover m)
    {
        if (m.State is RiderState st && (m.Route.Edge != st.Edge || m.Route.Forward != st.Forward))
        {
            // (m.Head is still where it was placed last tick)
            var (pos, dir) = m.Route.At(0f);
            var right = new Vector3(-dir.Z, 0f, dir.X).Normalized();
            m.Lateral = Mathf.Clamp(MathX.Flat(m.Head - pos).Dot(right), -8f, 8f);
            (st.Edge, st.Forward, st.OnLeg) = (m.Route.Edge, m.Route.Forward, 0f);
        }
        return m.Lateral;
    }

    /// <summary>
    /// The speed a rider may still do behind the rider ahead on its line, and in the car lane behind the car ahead (+∞ with none).
    /// </summary>
    private float RideFollow(SlowMover m, Vector3 dir)
    {
        float best = float.MaxValue;
        var right = new Vector3(-dir.Z, 0f, dir.X);
        foreach (var o in _riders)
        {
            if (o == m) continue;
            var d = o.Head - m.Head;
            if (Mathf.Abs(d.X) > 14f || Mathf.Abs(d.Z) > 14f || Mathf.Abs(d.Y) > 3f) continue;
            float along = d.Dot(dir);
            if (along < 0.3f || along > 12f || Mathf.Abs(d.Dot(right)) > 0.9f || o.Vel.Dot(dir) < -0.5f) continue;
            float gap = along - RiderLook.Size.Z - 0.8f;
            best = Mathf.Min(best, Mathf.Max(0f, o.Speed + gap * 0.8f));
        }
        if (!m.InLane || _byX.Count == 0) return best;
        _xKey.Head.X = m.Head.X - 25f;
        int from = _byX.BinarySearch(_xKey, ByX);
        for (int o = from < 0 ? ~from : from; o < _byX.Count && _byX[o].Head.X <= m.Head.X + 25f; o++)
        {
            var c = _byX[o];
            var d = c.Head - m.Head;
            float along = d.Dot(dir);
            if (along < 0.5f || along > 25f || Mathf.Abs(d.Y) > 3f || Mathf.Abs(d.Dot(right)) > 1.6f || c.Vel.Dot(dir) < -0.5f) continue;
            float gap = along - CarFront - RiderLook.Size.Z * 0.5f - 1.5f;
            best = Mathf.Min(best, Mathf.Max(0f, Mathf.Max(0f, c.Vel.Dot(dir)) + gap * 0.6f));
        }
        return best;
    }

    /// <summary>
    /// The traffic lights of the approach a rider's route leads into (#353, read on the server clock like the cars'): its bike
    /// group where its path or approach has one (their lead green, a left-turn bike lane's own phase, #979), else the cars' group of
    /// its move. Red, it stops at its line: the bike lane's (an advanced stop line ahead of the cars', #351), else the stop line (a
    /// bike box lies in front of the cars). A right turn on red goes where signal 5.18 stands (#711). Amber: it stops if it can at
    /// <see cref="RideBrake"/>. +∞ when nothing holds it.
    /// </summary>
    private float RideLights(SlowMover m, RiderState st)
    {
        var legs = m.Route.Legs;
        LaneApproach? approach = null;
        float toLine = float.MaxValue, along = 0f;
        int leg = -1;
        for (int i = m.Route.Leg; i < legs.Count && along < RideLook; i++)
        {
            var (e, f) = legs[i];
            float rem = i == m.Route.Leg ? e.Length - m.Route.Arc : e.Length;
            if (e.Path is null && (f ? e.ApproachAtEnd : e.ApproachAtStart) is var (a, back))
            {
                approach = a; toLine = along + rem - back; leg = i;
                break;
            }
            along += rem;
        }
        bool same = approach is not null && approach == st.Approach;
        float before = same ? st.ToLine : float.MaxValue;
        if (!same) (st.Approach, st.WasAtRed, st.Clearing) = (approach, false, false);
        st.ToLine = toLine;
        if (approach?.Site is not { } site || approach.Arm >= site.Plan.Arms.Count) return float.MaxValue;

        // its move and group: the path it takes past the line (a short piece over the crosswalk may lie between)
        JunctionPathInfo? path = null;
        for (int k = leg + 1; k < legs.Count; k++)
        {
            if (legs[k].Edge.Path is { } p) { path = p; break; }
            if (legs[k].Edge.Length > PathGap) break;
        }
        var turn = path?.Move ?? TurnOf(approach, WayPast(legs, leg));
        var plan = site.Plan;
        int group = path is { Site: { } ps, Group: >= 0 } && ps == site ? path.Group : BikeGroupFor(site, approach.Arm, turn);
        if (group < 0 || group >= plan.Groups.Count) return float.MaxValue;
        double now = SignalLamps.Clock();
        var aspect = plan.State(group, now);
        bool red = aspect is SignalAspect.Red or SignalAspect.RedAmber;
        bool rightOnRed = turn == SignalMoves.Right && plan.Arms[approach.Arm].BikeRightOnRed;
        if (before > 0f && before < float.MaxValue && toLine <= 0f)
        {
            if (red && rightOnRed) Riders.RightOnRed++;
            else if (st.Clearing && (red || aspect == SignalAspect.Amber)) Riders.AmberClears++;
            else if (red) Riders.RedRuns++;
            else
            {
                Riders.GreenCrossings++;
                // the bike group's lead green: its riders away while the cars beside them still stand
                int cars = GroupFor(site, approach.Arm, SignalMoves.Through);
                if (plan.Groups[group].Kind == SignalGroupKind.Bike && cars >= 0 && plan.State(cars, now) is SignalAspect.Red or SignalAspect.RedAmber)
                    Riders.Leads++;
                if (turn == SignalMoves.Left && plan.Groups[group].Kind == SignalGroupKind.Bike) Riders.BikeLefts++;
            }
        }
        if (toLine > RideLook || toLine < -0.5f) return float.MaxValue;
        if (!red && aspect != SignalAspect.Amber) { st.Clearing = false; return float.MaxValue; }
        // right on red (5.18): slowly round the corner, on its own way
        if (red && rightOnRed) return 3f;
        float stopAt = toLine - StopBehindFor(approach, turn) - RiderLook.Size.Z * 0.5f - 0.3f;
        bool cannotStop = m.Speed * m.Speed / (2f * RideBrake * 1.6f) > stopAt;
        if (st.Clearing && cannotStop || before >= RideLook && cannotStop
            || aspect == SignalAspect.Amber && m.Speed * m.Speed / (2f * RideBrake) > stopAt)
        {
            st.Clearing = true;
            return float.MaxValue;
        }
        st.Clearing = false;
        if (!st.WasAtRed) { st.WasAtRed = true; Riders.RedStops++; }
        m.Holding = true;
        return Mathf.Sqrt(2f * RideBrake * Mathf.Max(0f, stopAt));
    }

    /// <summary>The group riders of a move go on (as RoadGen's <c>PathGroup</c>): the arm's bike group with it, else the cars'.</summary>
    private static int BikeGroupFor(SignalSite site, int arm, SignalMoves turn)
    {
        var groups = site.Plan.Groups;
        for (int g = 0; g < groups.Count; g++)
            if (groups[g].Arm == arm && groups[g].Kind == SignalGroupKind.Bike && (groups[g].Moves & turn) != 0) return g;
        return GroupFor(site, arm, turn);
    }

    /// <summary>How far behind the stop line riders of <paramref name="turn"/> stop: their bike lane's (negative: an advanced line ahead), else on it.</summary>
    private static float StopBehindFor(LaneApproach approach, SignalMoves turn)
    {
        float any = 0f;
        bool found = false;
        foreach (var l in approach.Lanes)
        {
            if (l.Kind != ApproachLaneKind.Bike) continue;
            if ((l.Moves & turn) != 0) return l.StopBehind;
            if (!found) (any, found) = (l.StopBehind, true);
        }
        return any;
    }

    private readonly List<(LaneEdge Edge, bool Forward)> _rideOptions = new();

    /// <summary>
    /// A rider's next leg at a junction of the ride graph. Arriving on a road where junction paths start, one of them: the riders'
    /// own of its kind (a lane's bike path, a path's way round the core), else any of theirs, else the cars' (from the lane nearest
    /// its line), one exit at random, straight on likelier. Off a path, its road. Elsewhere the ways on at random, bike lanes and
    /// paths likelier; never straight back unless it is a dead end.
    /// </summary>
    private (LaneEdge, bool)? NextRide(SlowMover m, (LaneEdge Edge, bool Forward) leg)
    {
        if (_rides is not { } g) return null;
        long key = leg.Forward ? leg.Edge.KeyEnd : leg.Edge.KeyStart;
        var (_, inDir) = leg.Edge.Sample(leg.Forward ? leg.Edge.Length : 0f);
        if (!leg.Forward) inDir = -inDir;
        var options = _rideOptions;
        options.Clear();
        bool anyPath = false;
        foreach (var o in g.Leaving(key))
        {
            if (o.Edge == leg.Edge) continue;
            if (leg.Edge.Path is not null && o.Edge.Path is not null) continue;   // a path ends on its road
            if (LaneGraph.RideOf(o.Edge)?.Side(o.Forward) == RideSide.None) continue;
            var (_, outDir) = o.Edge.Sample(o.Forward ? 0f : o.Edge.Length);
            if (!o.Forward) outDir = -outDir;
            if (MathX.Flat(outDir).Normalized().Dot(MathX.Flat(inDir).Normalized()) < -0.8f) continue;   // not straight back
            options.Add(o);
            anyPath |= o.Edge.Path is not null;
        }
        if (options.Count == 0)
            return leg.Edge.OneWay != 0 || LaneGraph.RideOf(leg.Edge)?.Side(!leg.Forward) is RideSide.None ? null : (leg.Edge, !leg.Forward);
        if (anyPath && leg.Edge.Path is null)
        {
            // which paths: the kind its side leads into (a lane's bike path, a path's way), else any riders' own, else the cars'
            var side = LaneGraph.RideOf(leg.Edge)?.Side(leg.Forward) ?? RideSide.Road;
            var kind = side == RideSide.Track ? JunctionPathKind.Path : JunctionPathKind.Bike;
            int tier = 3;
            foreach (var o in options)
                if (o.Edge.Path is not null) tier = Math.Min(tier, PathTier(o.Edge, kind, side));
            for (int i = options.Count - 1; i >= 0; i--)
                if (options[i].Edge.Path is null || PathTier(options[i].Edge, kind, side) != tier) options.RemoveAt(i);
            // one exit at random (straight on twice as likely), then the lane nearest the rider's line
            float total = 0f;
            foreach (var o in options) total += ExitWeight(o.Edge);
            float pick = (float)_rng.NextDouble() * total;
            long exit = options[^1].Edge.KeyEnd;
            foreach (var o in options) if ((pick -= ExitWeight(o.Edge)) <= 0f) { exit = o.Edge.KeyEnd; break; }
            (LaneEdge, bool)? best = null;
            float bestGap = float.MaxValue;
            foreach (var o in options)
            {
                if (o.Edge.KeyEnd != exit) continue;
                float gap = Mathf.Abs(o.Edge.Path!.FromOffset - m.Lateral);
                if (gap < bestGap) (best, bestGap) = (o, gap);
            }
            return best;
        }
        if (leg.Edge.Path is not null)
            for (int i = options.Count - 1; i >= 0 && options.Count > 1; i--)
                if (options[i].Edge.Path is not null) options.RemoveAt(i);
        float sum = 0f;
        foreach (var o in options) sum += RoadWeight(o);
        float at = (float)_rng.NextDouble() * sum;
        foreach (var o in options) if ((at -= RoadWeight(o)) <= 0f) return o;
        return options[^1];

        // 0 the best: for a rider on a lane or a path the riders' own of its kind, then any of theirs, then the cars'; for one in
        // the car lane the cars' (it rides with them), then the riders'
        static int PathTier(LaneEdge e, JunctionPathKind kind, RideSide side)
        {
            var bike = LaneGraph.RideOf(e)?.Bike;
            if (side == RideSide.Road) return bike is null ? 0 : 1;
            return bike == kind ? 0 : bike is not null ? 1 : 2;
        }
        static float ExitWeight(LaneEdge e) => e.Path!.Move == SignalMoves.Through ? 2f : 1f;
        static float RoadWeight((LaneEdge Edge, bool Forward) o) =>
            LaneGraph.RideOf(o.Edge)?.Side(o.Forward) is RideSide.Lane or RideSide.Track or RideSide.Way ? 3f : 1f;
    }

    // ---- --trafficcheck ------------------------------------------------------------------------

    private readonly HashSet<Vehicle> _waitedForRider = new();

    /// <summary>
    /// For the probe, each frame (#1056): counts the cars standing at a conflict point of their junction path for a rider coming
    /// (a turn giving way to a bike way, #1046), each wait once.
    /// </summary>
    public void RiderProbeTick()
    {
        if (_riders.Count == 0) return;
        foreach (var car in _cars)
        {
            if (!car.GapWaiting || car.GapPath is not { } p || car.Speed > 0.5f) { _waitedForRider.Remove(car); continue; }
            if (_waitedForRider.Contains(car)) continue;
            LaneEdge? edge = null;
            foreach (var (e, _) in car.Route.Legs) if (ReferenceEquals(e.Path, p)) { edge = e; break; }
            if (edge is null) continue;
            foreach (var y in p.Yields)
            {
                if (y.Mover != ConflictMover.Bike) continue;
                var (at, _) = edge.Sample((y.At + y.Out) * 0.5f);
                if (!CrosserComing(at, (y.Out - y.At) * 0.5f + 1f, ConflictMover.Bike)) continue;
                _waitedForRider.Add(car);
                Riders.CarsWaited++;
                break;
            }
        }
    }

    /// <summary>The ride graph's make-up (#1056, the probe): street pieces with a lane, with a path, with neither; ways; riders' and cars' junction paths.</summary>
    public (int Lanes, int Tracks, int Roads, int Ways, int BikePaths, int CarPaths) RideGraphMakeUp()
    {
        int lanes = 0, tracks = 0, roads = 0, ways = 0, bikePaths = 0, carPaths = 0;
        if (_rides is null) return default;
        foreach (var e in _rides.Edges)
        {
            var w = LaneGraph.RideOf(e);
            if (e.Path is not null) { if (w?.Bike is not null) bikePaths++; else carPaths++; continue; }
            if (w is null || e.Connector) continue;
            if (w.Fwd == RideSide.Way) ways++;
            else if (w.Fwd == RideSide.Lane || w.Bwd == RideSide.Lane) lanes++;
            else if (w.Fwd == RideSide.Track || w.Bwd == RideSide.Track) tracks++;
            else roads++;
        }
        return (lanes, tracks, roads, ways, bikePaths, carPaths);
    }
}
