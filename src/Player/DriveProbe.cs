using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --drivecheck[,out_prefix] [--cars 0,1,3,4 | --car N] [--seconds S] [--finish M]
/// [--record prefix] [--trace] [--at E,N] [--traffic 0]</c>
///
/// <para>
/// A race down the real road from the spawn (the main road, straight on at every junction): every
/// listed car on a grid, all at once, in the same world — they touch, and a hard enough touch puts
/// one out. Each is driven by its own scripted driver working from its own car:
/// </para>
/// <list type="bullet">
/// <item>the <see cref="RaceLine"/> — outside, apex, outside, inside the tarmac — not the surveyed centreline;</item>
/// <item>its own speed profile from its grip, power, mass and brakes (<see cref="RaceLine.SpeedProfile"/>);</item>
/// <item>drift cars drift a tight corner only when a forward simulation of THEIR car (<see cref="Car.Clone"/>)
/// through a handbrake entry stays on the road; grip cars (<see cref="DriveStyle.Grip"/>) never drift;</item>
/// <item>a slide nobody planned is caught, never held; off the tarmac the driver eases back on slowly;</item>
/// <item>a faster car closing on a slower one moves across to pass if there is room, else lifts.</item>
/// </list>
/// <para>
/// Filmed live by a cinematic camera on the leader. <c>--record prefix</c> writes one GPX per car
/// (nose yaw included) for the replay and Absolute Cinema. Prints the classification. Non-zero exit
/// if nobody reached the finish or no drift car held a planned drift.
/// </para>
/// </summary>
public partial class DriveProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shotPrefix;
    private readonly int[] _cars;
    private readonly double _seconds;
    private static readonly bool Trace = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--trace") >= 0;

    private bool _done, _pathRequested, _started;
    private double _t, _wait, _countdown = 2.5;

    // the road: its centreline (for "on the tarmac"), and the racing line on it
    private readonly List<Vector3> _path = new();
    private readonly List<float> _arc = new();
    private readonly List<float> _width = new();
    private RaceLine? _line;
    private float _finish;

    /// <summary>A narrow Jura pass is not a motorway: no scripted driver goes faster than this.</summary>
    private const float MaxSpeed = 36f;
    /// <summary>Only corners tighter than this radius, m, are considered for a drift.</summary>
    private const float DriftRadius = 90f;
    private const float PlanSeconds = 3.5f, SimDt = 1f / 60f;

    private readonly List<Pilot> _pilots = new();
    private readonly List<string> _log = new();

    // cinematic camera
    private Camera3D? _cine;
    private bool _cinePlaced;
    private int _cineCorner = -1;
    private int _shots;
    private bool _shotThisDrift;

    private readonly string? _record = ArgAfter("--record");

    /// <summary>Everything a driver decides from, so the same policy drives the car and its simulations.</summary>
    private struct Driver
    {
        public int Near;
        public bool Drifting, Planned, Recovering;
        public float Handbrake, Bend, Cap, Lateral;
        public float PeakSlip, DriftTime;
    }

    /// <summary>One car in the race and the driver in it.</summary>
    private sealed class Pilot
    {
        public int Slot;
        public CarSpec Spec = null!;
        public FootPlayer Player = null!;
        public Car Car = null!;
        public float[] Profile = null!;
        public Driver D = new() { Bend = 1f, Cap = MaxSpeed };
        public float PlanTimer, Cooldown, GripUntil;
        public bool Mounted, Out;
        public double FinishTime = -1;
        public int Drifts, Plans, Feasible, Impacts, Contacts;
        public float BestDrift, OffRoad, Top;
        public readonly StringBuilder Gpx = new();
        public double SinceFix;
        public float Arc;
    }

    public DriveProbe(ChunkManager chunks, WorldOrigin origin, string? shotPrefix, int car, double seconds)
    {
        _chunks = chunks;
        _origin = origin;
        _shotPrefix = shotPrefix;
        _seconds = seconds;
        var list = ArgAfter("--cars");
        _cars = list != null
            ? list.Split(',').Select(x => int.TryParse(x, out int i) ? i : 0).ToArray()
            : new[] { car };
        _finish = float.TryParse(ArgAfter("--finish"), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 2000f;
    }

    public static (bool Requested, string? Shot, int Car, double Seconds) ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        bool requested = false;
        string? shot = null;
        int car = 0;
        double seconds = 120;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--drivecheck"))
            {
                requested = true;
                var parts = args[i].Split(',');
                if (parts.Length > 1) shot = parts[1];
            }
            else if (args[i] == "--car" && i + 1 < args.Length) int.TryParse(args[i + 1], out car);
            else if (args[i] == "--seconds" && i + 1 < args.Length)
                double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        }
        return (requested, shot, car, seconds);
    }

    private static string? ArgAfter(string flag)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        float dt = (float)delta;
        _wait += delta;
        if (_wait > _seconds + 150) { GD.Print("[drive] TIMEOUT"); End(); return; }

        if (!_pathRequested)
        {
            if (_chunks.Source == null) return;
            _pathRequested = true;
            var (e, n) = SpawnPoint.ParseTarget();
            _ = BuildPath(_origin.ToWorld(e, n, 0));
            return;
        }
        if (_line == null) return;

        // the grid: two abreast, 9 m between rows, on the racing line near the start
        if (_pilots.Count == 0)
        {
            if (!_chunks.TryGetHeight(_line.Points[0], out _)) return;
            for (int k = 0; k < _cars.Length; k++)
            {
                var spec = CarCatalog.All[Mathf.Clamp(_cars[k], 0, CarCatalog.All.Count - 1)];
                float s = 12f + 9f * (_cars.Length / 2 - k / 2);
                int i = _line.IndexAt(s);
                var fwd = Flat(_line.PointAt(s + 2f) - _line.PointAt(s - 2f)).Normalized();
                var left = new Vector3(fwd.Z, 0, -fwd.X);
                float side = _cars.Length == 1 ? 0f : (k % 2 == 0 ? 1f : -1f) * Mathf.Min(1.6f, _line.Room[i] + 0.5f);
                var at = _line.Points[i] + left * side;
                float g = _chunks.TryGetHeight(at, out float gh) ? gh : at.Y;
                var player = new FootPlayer { Name = $"Driver{k}", Terrain = _chunks };
                AddChild(player);
                player.GlobalPosition = at with { Y = g + 1.2f };
                player.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
                var pilot = new Pilot { Slot = k, Spec = spec, Player = player };
                pilot.D.Lateral = side;
                player.Impacted += lost =>
                {
                    if (lost < 2f) return;
                    pilot.Impacts++;
                    _log.Add($"{_t,5:F1}s {spec.Label}: impact -{lost * 3.6f:F0} km/h at {pilot.Arc:F0} m");
                };
                player.Announced += (text, _) => _log.Add($"{_t,5:F1}s {spec.Label}: {text}");
                _pilots.Add(pilot);
            }
            return;
        }

        // mount everyone once they are on the ground, then a countdown
        if (!_started)
        {
            foreach (var p in _pilots)
            {
                if (p.Mounted || !p.Player.IsOnFloor()) continue;
                p.Mounted = p.Player.SetRide(p.Spec.Kind);
                p.Car = (p.Player.Vehicle as Car)!;
                p.Profile = _line.SpeedProfile(p.Spec, Rideable.Arcade, p.Spec.Style == DriveStyle.Grip ? 0.9f : 0.84f, MaxSpeed);
                var pilot = p;
                p.Player.RideControls = () => Drive(pilot);
                BeginGpx(p);
            }
            if (_pilots.Any(p => !p.Mounted)) return;
            _countdown -= delta;
            if (_countdown > 0) return;
            _started = true;
            GD.Print($"[drive] GO: {string.Join(", ", _pilots.Select(p => p.Spec.Label + (p.Spec.Style == DriveStyle.Grip ? " (grip)" : "")))} "
                + $"over {_finish:F0} m of {_line.Length:F0} m");
            return;
        }

        _t += delta;
        foreach (var p in _pilots)
        {
            if (p.Out) continue;
            p.Arc = _line.Arc[p.D.Near];
            p.Top = Mathf.Max(p.Top, p.Player.Motion.Speed);
            if (Off(p.Player.GlobalPosition) - HalfWidthAt(p.Player.GlobalPosition) > 1.5f) p.OffRoad += dt;
            if (p.FinishTime < 0 && p.Arc >= _finish) { p.FinishTime = _t; _log.Add($"{_t,5:F1}s {p.Spec.Label} FINISHES"); }
            if (p.Player.Ride != p.Spec.Kind) { p.Out = true; _log.Add($"{_t,5:F1}s {p.Spec.Label} is OUT (crashed at {p.Arc:F0} m)"); }
            RecordFix(p, delta);
            // two cars closer than their bodies are touching
            foreach (var q in _pilots)
                if (q.Slot > p.Slot && !q.Out && Flat(q.Player.GlobalPosition - p.Player.GlobalPosition).Length() < 2.1f)
                {
                    p.Contacts++; q.Contacts++;
                }
        }
        Cinematic(dt);
        if (Trace && (int)(_t * 2) != (int)((_t - delta) * 2))
            foreach (var p in _pilots.Where(p => !p.Out))
            {
                var m = p.Player.Motion;
                GD.Print($"[drive]   t={_t,5:F1} {p.Spec.Label,-12} s={p.Arc,6:F0} v={m.Speed * 3.6f,4:F0}/{p.Profile[p.D.Near] * 3.6f,4:F0} "
                    + $"slip={Mathf.RadToDeg(Wrap(m.Slip)),4:F0} off={Off(p.Player.GlobalPosition),4:F1} drift={p.D.Drifting}");
            }
        bool allDone = _pilots.All(p => p.Out || p.FinishTime >= 0);
        if (_t >= _seconds || allDone) End();
    }

    // ------------------------------------------------------------------------------------
    // the road
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The main road from the spawn: the nearest drivable edge, then at every junction the edge of
    /// the same class or better that carries on straightest — both ways from the start, keeping
    /// the longer run. Then the racing line on it.
    /// </summary>
    private async Task BuildPath(Vector3 at)
    {
        var (e, n) = _origin.ToLv95(at);
        var here = TileId.FromLv95(e, n);
        var tiles = new List<RoadTile>();
        for (int de = -4; de <= 4; de++)
            for (int dn = -4; dn <= 4; dn++)
            {
                try { if (await _chunks.Source!.LoadRoadsAsync(new TileId(here.E + de, here.N + dn)) is { } t) tiles.Add(t); }
                catch (System.Exception) { /* no roads there */ }
            }
        var graph = LaneGraph.Build(tiles, _origin, s =>
            s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Major or RoadClass.Road or RoadClass.Minor
            && (s.Flags & (RoadFlags.Stairs | RoadFlags.Tunnel)) == 0);

        LaneEdge? best = null;
        float bestD = float.MaxValue, bestS = 0;
        foreach (var edge in graph.Edges)
            for (int i = 0; i < edge.Points.Length; i++)
            {
                // prefer the bigger road when two are close: a farm track beside the pass is not the pass
                float d = Flat(edge.Points[i] - at).Length() + (int)edge.Class * 4f;
                if (d < bestD) { bestD = d; best = edge; bestS = edge.Cumulative[i]; }
            }
        if (best == null) { GD.Print("[drive] no road near the spawn"); CallDeferred(MethodName.Finish, 1); return; }

        var a = Walk(graph, best, true, bestS);
        var b = Walk(graph, best, false, best.Length - bestS);
        var pts = a.Count >= b.Count ? a : b;
        var line = RaceLine.Build(pts.Select(p => p.P).ToList(), pts.Select(p => p.W).ToList(), 0.9f);
        Callable.From(() =>
        {
            float s = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) s += pts[i].P.DistanceTo(pts[i - 1].P);
                _path.Add(pts[i].P);
                _arc.Add(s);
                _width.Add(pts[i].W);
            }
            _line = line;
            _finish = Mathf.Min(_finish, line.Length - 40f);
            GD.Print($"[drive] route: {best.Class}, {s:F0} m, racing line {line.Length:F0} m "
                + $"(max {line.Room.DefaultIfEmpty(0).Max():F1} m of room either side)");
        }).CallDeferred();
    }

    private static List<(Vector3 P, float W)> Walk(LaneGraph graph, LaneEdge edge, bool forward, float from)
    {
        var pts = new List<(Vector3, float)>();
        const float Step = 2f, MaxLength = 8000f;
        float total = 0;
        var visited = new HashSet<LaneEdge>();
        while (total < MaxLength && visited.Add(edge))
        {
            for (float s = from; s <= edge.Length; s += Step)
            {
                var (p, _) = edge.Sample(forward ? s : edge.Length - s);
                pts.Add((p, edge.Width));
                total += Step;
            }
            // leave the far end: the next edge of this class or better that turns least
            var (endP, endT) = edge.Sample(forward ? edge.Length : 0);
            var dirIn = forward ? endT : -endT;
            long key = forward ? edge.KeyEnd : edge.KeyStart;
            LaneEdge? next = null;
            bool nextFwd = true;
            float bestScore = -2f;
            // RoadGen --rewrite trims every road back from its junction polygon, so after it the
            // roads meeting at a junction no longer share an endpoint: take any edge that starts
            // or ends within a junction's reach of where this one stopped
            foreach (var (cand, candFwd) in graph.Leaving(key).Concat(NearbyStarts(graph, endP)))
            {
                if (cand == edge || visited.Contains(cand) || cand.Class > edge.Class) continue;
                var (_, t) = cand.Sample(candFwd ? 1f : cand.Length - 1f);
                float score = (candFwd ? t : -t).Dot(dirIn);
                if (score > bestScore) { bestScore = score; next = cand; nextFwd = candFwd; }
            }
            if (next == null || bestScore < -0.2f) break;
            edge = next;
            forward = nextFwd;
            from = 0;
        }
        return pts;
    }

    private static IEnumerable<(LaneEdge, bool)> NearbyStarts(LaneGraph graph, Vector3 at)
    {
        const float Reach = 18f;
        foreach (var e in graph.Edges)
        {
            if (e.OneWay != -1 && Flat(e.Points[0] - at).Length() < Reach) yield return (e, true);
            if (e.OneWay != 1 && Flat(e.Points[^1] - at).Length() < Reach) yield return (e, false);
        }
    }

    // ------------------------------------------------------------------------------------
    // the driver: one policy, for the car and for its simulated futures
    // ------------------------------------------------------------------------------------

    private RideInput Drive(Pilot p)
    {
        float dt = (float)GetPhysicsProcessDeltaTime();
        // On the grid: the handbrake, not the brake — at a standstill the brake pedal selects
        // reverse and then drives it, and the whole grid reversed off the line during the countdown.
        if (!_started || p.Out) return new RideInput(0f, 0f, 0f, false, Handbrake: true);
        var motion = p.Player.Motion;
        bool wasDrifting = p.D.Drifting, wasPlanned = p.D.Planned;
        Traffic(p);
        var input = Policy(p, ref p.D, p.Player.GlobalPosition, motion, dt);
        if (wasDrifting && !p.D.Drifting) EndDrift(p, wasPlanned);
        Plan(p, p.Player.GlobalPosition, motion, dt);
        return input;
    }

    /// <summary>
    /// Racecraft: closing on a car ahead, move to the side of the line it is not on if the road has
    /// room there, otherwise lift to its speed. Keeps a clean race clean; contact still happens.
    /// </summary>
    private void Traffic(Pilot p)
    {
        float want = 0f;
        p.D.Cap = MaxSpeed;
        var me = p.Player.GlobalPosition;
        var fwd = new Basis(Vector3.Up, p.Player.Motion.Yaw + p.Player.Motion.Slip) * Vector3.Forward;
        foreach (var q in _pilots)
        {
            if (q == p || q.Out) continue;
            var rel = Flat(q.Player.GlobalPosition - me);
            float ahead = rel.Dot(Flat(fwd));
            if (ahead < 2f || ahead > 22f) continue;
            var left = new Vector3(fwd.Z, 0, -fwd.X);
            float lat = rel.Dot(left);
            if (Mathf.Abs(lat) > 2.6f) continue;
            float room = _line!.Room[p.D.Near] + 1f;
            // go round on the side away from it, if the tarmac allows; else sit behind it
            float target = lat > 0 ? -room : room;
            if (room > 1.8f) want = target;
            else p.D.Cap = Mathf.Min(p.D.Cap, q.Player.Motion.Speed + (ahead - 8f) * 0.5f);
        }
        p.D.Lateral = Mathf.MoveToward(p.D.Lateral, want, 1.2f * (float)GetPhysicsProcessDeltaTime());
    }

    private RideInput Policy(Pilot p, ref Driver d, Vector3 pos, in RideMotion m, float dt)
    {
        var line = _line!;
        float v = m.Speed;
        float slip = Wrap(m.Slip);
        while (d.Near < line.Points.Count - 2 && Flat(line.Points[d.Near + 1] - pos).Length() < Flat(line.Points[d.Near] - pos).Length()) d.Near++;
        float s0 = line.Arc[d.Near];

        // steer the TRAVEL toward a point ahead on the line (plus any overtaking offset)
        float look = Mathf.Clamp(v * 0.7f, 7f, 30f);
        var ahead = line.PointAt(s0 + look);
        if (d.Lateral != 0f)
        {
            var t = Flat(line.PointAt(s0 + look + 2f) - line.PointAt(s0 + look - 2f)).Normalized();
            ahead += new Vector3(t.Z, 0, -t.X) * d.Lateral;
        }
        var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward;
        float angle = SignedAngle(Flat(travel), Flat(ahead - pos));   // + = target to the left
        // gentler hands at speed: full lock at 90 km/h to fix a metre of line is what starts a slide
        float steer = Mathf.Clamp(-angle * 2.6f * Mathf.Clamp(15f / Mathf.Max(v, 1f), 0.4f, 1f), -1f, 1f);

        // this car's own speed along its own profile, a beat ahead
        float want = Mathf.Min(d.Cap, Mathf.Min(p.Profile[d.Near], p.Profile[line.IndexAt(s0 + v * 0.3f)]));

        // off the tarmac: come back to it gently — slow, soft hands, no drifting
        float past = Off(pos) - HalfWidthAt(pos);
        if (past > 1f) d.Recovering = true;
        else if (past < 0.2f) d.Recovering = false;
        if (d.Recovering)
        {
            want = Mathf.Min(want, 14f);
            steer = Mathf.Clamp(steer, -0.5f, 0.5f);
        }

        // a slide that started on its own is caught (brake in it and the car spins), never held
        if (!d.Drifting && Mathf.Abs(slip) > 0.25f) { d.Drifting = true; d.Planned = false; d.Handbrake = 0f; }

        float throttle, brake = 0f;
        bool handbrake = false;
        if (d.Drifting)
        {
            d.Handbrake -= dt;
            handbrake = d.Handbrake > 0;
            float k = line.Curvature[d.Near];
            if (Mathf.Abs(k) > 0.002f) d.Bend = Mathf.Sign(k);
            float soon = MaxCurvature(s0, 0f, 24f);
            float hold = d.Planned && soon > 1f / (DriftRadius * 1.6f) ? -d.Bend * 0.52f : 0f;
            if (handbrake) steer = -d.Bend * 0.9f;
            else
            {
                // the wheel holds the ANGLE (minus what the Game assist already counter-steers)...
                float wheel = slip + 1.5f * (slip - hold) - 0.12f * m.YawRate - (Rideable.Arcade ? 0.45f * slip : 0f);
                steer = Mathf.Clamp(-wheel / p.Spec.MaxSteer, -1f, 1f);
            }
            // ...and the gas holds the LINE: more gas slides wide, less lets the rears bite
            throttle = handbrake ? 0f : Mathf.Clamp(0.75f - 2.5f * angle * d.Bend + (want - v) * 0.03f, 0.15f, 1f);
            if (hold == 0f) throttle = Mathf.Min(throttle, 0.4f);
            d.PeakSlip = Mathf.Max(d.PeakSlip, Mathf.Abs(slip));
            if (Mathf.Abs(slip) > 0.26f) d.DriftTime += dt;
            if (d.Handbrake < -0.4f && Mathf.Abs(slip) < 0.08f) { d.Drifting = false; d.Planned = false; }
        }
        else
        {
            throttle = Mathf.Clamp((want - v) * 0.35f, 0f, 1f);
            // trail off the brake as the wheel turns in: braking hard in a bend unloads the rear
            brake = Mathf.Clamp((v - want) * 0.3f, 0f, 1f) * (1f - 0.7f * Mathf.Abs(steer));
        }
        return new RideInput(throttle, brake, steer, false, handbrake);
    }

    /// <summary>
    /// Decides whether the corner ahead gets drifted, by trying it: the car's own model on a copy of
    /// its state, driven by the same policy through a handbrake entry and a held drift. Fastest
    /// feasible entry wins; if none stays on the road, the corner is taken on grip. Grip cars never
    /// ask.
    /// </summary>
    private void Plan(Pilot p, Vector3 pos, in RideMotion m, float dt)
    {
        if (p.Spec.Style == DriveStyle.Grip) return;
        p.PlanTimer -= dt;
        p.Cooldown -= dt;
        float s0 = _line!.Arc[p.D.Near];
        if (p.D.Drifting || p.D.Recovering || p.PlanTimer > 0 || p.Cooldown > 0 || s0 < p.GripUntil) return;
        p.PlanTimer = 0.2f;
        if (m.Speed < 12f || MaxCurvature(s0, 10f, 40f) < 1f / DriftRadius) return;

        p.Plans++;
        string tried = "";
        foreach (float factor in new[] { 1f, 0.85f, 0.7f })
        {
            var (ok, off, peak, entered) = Simulate(p, pos, m, m.Speed * factor);
            tried += $" {factor * 100:F0}%: off {off:F1} m, peak {Mathf.RadToDeg(peak):F0}°{(entered ? "" : " (no entry)")};";
            if (!ok) continue;
            p.Feasible++;
            if (factor == 1f)
            {
                p.D.Drifting = true;
                p.D.Planned = true;
                p.D.Handbrake = 0.28f;
                _log.Add($"{_t,5:F1}s {p.Spec.Label}: DRIFT at {s0:F0} m, {m.Speed * 3.6f:F0} km/h (sim peak {Mathf.RadToDeg(peak):F0}°)");
            }
            else p.D.Cap = m.Speed * factor;   // brake to the speed the simulation drifted at
            return;
        }
        if (Trace) _log.Add($"{_t,5:F1}s {p.Spec.Label}: grip at {s0:F0} m:{tried}");
        p.GripUntil = s0 + 60f;
    }

    private (bool Ok, float Off, float Peak, bool Entered) Simulate(Pilot p, Vector3 pos, RideMotion m, float cap)
    {
        var car = p.Car.Clone();
        var d = p.D;
        d.Cap = cap;
        d.PeakSlip = 0;
        d.DriftTime = 0;
        bool entered = false;
        float maxOff = -100f, entryWait = 0;   // metres past the edge: negative is inside it
        for (float t = 0; t < PlanSeconds; t += SimDt)
        {
            if (!entered && m.Speed <= cap + 0.5f) { entered = true; d.Drifting = true; d.Planned = true; d.Handbrake = 0.28f; }
            if (!entered && (entryWait += SimDt) > 2f) break;
            var input = Policy(p, ref d, pos, m, SimDt);
            float s = _line!.Arc[d.Near];
            float grade = (_line.PointAt(s + 3f).Y - _line.PointAt(s - 3f).Y) / 6f;
            car.Step(input, new RideGround(true, grade), SimDt, ref m);
            var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward;
            pos += travel * m.Speed * SimDt;
            if (entered) maxOff = Mathf.Max(maxOff, Off(pos) - HalfWidthAt(pos));
            if (Mathf.Abs(Wrap(m.Slip)) > 1.35f) return (false, maxOff, d.PeakSlip, entered);
            if (entered && !d.Drifting && t > 1f) break;   // caught and straight: the drift is done
        }
        // half a metre INSIDE the edge: the plan runs on the line's heights, and the real camber
        // and bumps put the car wider than a plan that allowed itself the whole road
        bool ok = entered && maxOff < -0.5f && d.PeakSlip > 0.35f;
        return (ok, maxOff, d.PeakSlip, entered);
    }

    private void EndDrift(Pilot p, bool planned)
    {
        float deg = Mathf.RadToDeg(p.D.PeakSlip);
        if (planned && deg > 20f && p.D.DriftTime > 0.5f) p.Drifts++;
        if (planned) _log.Add($"{_t,5:F1}s {p.Spec.Label}: drift done, peak {deg:F0}°, {p.D.DriftTime:F1} s past 15°");
        p.BestDrift = Mathf.Max(p.BestDrift, planned ? deg : 0f);
        p.D.PeakSlip = 0;
        p.D.DriftTime = 0;
        p.Cooldown = 1.2f;
    }

    // ------------------------------------------------------------------------------------
    // filming, recording, results
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The race is filmed on its leader: for each drifted corner a camera stands on the outside of
    /// the bend a little ahead and pans with the car through it; otherwise it hangs back and above
    /// at three-quarters. Placed on its first frame, never under the ground, and it dissolves the
    /// trees in its sightline, so a forest never blocks the shot.
    /// </summary>
    private void Cinematic(float dt)
    {
        var lead = _pilots.Where(p => !p.Out).OrderByDescending(p => p.Arc).FirstOrDefault();
        if (lead == null) return;
        if (_cine == null)
        {
            _cine = new Camera3D { Name = "Cinematic", Fov = 55f, Far = 20000f };
            AddChild(_cine);
        }
        var p = lead.Player;
        var car = p.GlobalPosition + Vector3.Up * 0.8f;
        int corner = (int)(lead.Arc / 60f);
        if (lead.D.Drifting && lead.D.Handbrake > 0.2f && corner != _cineCorner)
        {
            _cineCorner = corner;
            float s = lead.Arc + 25f;
            var a = _line!.PointAt(s - 4f); var b = _line.PointAt(s + 4f); var c = _line.PointAt(s + 12f);
            var right = Flat(b - a).Normalized().Cross(Vector3.Up);
            float side = SignedAngle(Flat(b - a), Flat(c - b)) > 0 ? 1f : -1f;
            _cine.GlobalPosition = b + right * side * (HalfWidthAt(b) + 7f) + Vector3.Up * 2.5f;
        }
        else if (!lead.D.Drifting)
        {
            var want = car + new Basis(Vector3.Up, p.Motion.Yaw + p.Motion.Slip) * new Vector3(-4f, 5f, 11f);
            _cine.GlobalPosition = _cinePlaced ? _cine.GlobalPosition.Lerp(want, 1f - Mathf.Exp(-3f * dt)) : want;
        }
        _cinePlaced = true;
        var cp = _cine.GlobalPosition;
        if (_chunks.TryGetHeight(cp, out float ground) && cp.Y < ground + 1.5f) _cine.GlobalPosition = cp with { Y = ground + 1.5f };
        var look = car - _cine.GlobalPosition;
        if (look.LengthSquared() > 0.25f && Mathf.Abs(look.Normalized().Y) < 0.98f)
            _cine.GlobalBasis = Basis.LookingAt(look.Normalized(), Vector3.Up);
        _chunks.SetSightlineCut(_cine.GlobalPosition, car, 3f);
        _cine.Current = true;

        float slip = Mathf.Abs(Wrap(p.Motion.Slip));
        if (_shotPrefix != null && lead.D.Drifting && lead.D.Planned && _shots < 4 && slip > 0.45f && lead.D.DriftTime > 0.3f && !_shotThisDrift)
        {
            _shotThisDrift = true;
            string file = $"{_shotPrefix}_drift{++_shots}.png";
            if (GetViewport().GetTexture().GetImage().SavePng(file) == Error.Ok)
                GD.Print($"[drive] wrote {file} ({lead.Spec.Label}, {Mathf.RadToDeg(slip):F0}° at {p.Motion.Speed * 3.6f:F0} km/h)");
        }
        if (!lead.D.Drifting) _shotThisDrift = false;
    }

    private void BeginGpx(Pilot p)
    {
        if (_record == null) return;
        p.Gpx.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<gpx version=\"1.1\" creator=\"UnitSportSwitzerland drivecheck\" xmlns=\"http://www.topografix.com/GPX/1/1\" ")
            .Append("xmlns:us=\"https://github.com/SuperQuentin/UnitSportSwitzerland\">\n")
            .Append($"<trk><name>{p.Spec.Label}</name><type>car:{(int)p.Spec.Kind - CarCatalog.First}</type><trkseg>\n");
    }

    /// <summary>
    /// One fix every 0.2 s. The yaw (the NOSE, not the direction of travel) rides in an extension:
    /// a track only knows where the car went, and a drift is exactly where the two differ.
    /// </summary>
    private void RecordFix(Pilot p, double delta)
    {
        if (_record == null) return;
        p.SinceFix += delta;
        if (p.SinceFix < 0.2) return;
        p.SinceFix = 0;
        var (e, n) = _origin.ToLv95(p.Player.GlobalPosition);
        var (lat, lon) = SwissProjection.ToWgs84(e, n);
        var ic = CultureInfo.InvariantCulture;
        var time = new System.DateTime(2026, 9, 30, 12, 0, 0, System.DateTimeKind.Utc).AddSeconds(_t);
        p.Gpx.Append(ic, $"<trkpt lat=\"{lat:F7}\" lon=\"{lon:F7}\"><ele>{p.Player.GlobalPosition.Y:F1}</ele>")
            .Append(ic, $"<time>{time:yyyy-MM-ddTHH:mm:ss.fffZ}</time>")
            .Append(ic, $"<extensions><us:yaw>{p.Player.Motion.Yaw:F4}</us:yaw></extensions></trkpt>\n");
    }

    private void End()
    {
        if (_done) return;
        foreach (var line in _log) GD.Print($"[drive]   {line}");
        GD.Print("[drive] classification:");
        int pos = 0;
        foreach (var p in _pilots.OrderBy(p => p.FinishTime < 0 ? 1e9 - p.Arc : p.FinishTime))
        {
            pos++;
            string result = p.FinishTime >= 0 ? $"{p.FinishTime:F1} s" : p.Out ? $"OUT at {p.Arc:F0} m" : $"{p.Arc:F0} m";
            GD.Print($"[drive]   {pos}. {p.Spec.Label,-14} {(p.Spec.Style == DriveStyle.Grip ? "grip " : "drift")} {result,-14} "
                + $"avg {p.Arc / Mathf.Max((float)(p.FinishTime >= 0 ? p.FinishTime : _t), 1f) * 3.6f:F0} km/h, top {p.Top * 3.6f:F0}, "
                + $"{p.Drifts} held drifts (best {p.BestDrift:F0}°), {p.Plans} corners planned / {p.Feasible} feasible, "
                + $"off road {p.OffRoad:F1} s, {p.Impacts} impacts, {p.Contacts / 60f:F1} s in contact");
            if (_record != null)
            {
                p.Gpx.Append("</trkseg></trk></gpx>\n");
                string file = $"{_record}_{(int)p.Spec.Kind - CarCatalog.First}.gpx";
                System.IO.File.WriteAllText(file, p.Gpx.ToString());
                GD.Print($"[drive]      recorded {file}");
            }
        }
        bool anyFinish = _pilots.Any(p => p.FinishTime >= 0);
        bool driftOk = _pilots.All(p => p.Spec.Style == DriveStyle.Grip) || _pilots.Any(p => p.Drifts > 0);
        bool ok = anyFinish && driftOk;
        GD.Print(ok ? "[drive] RESULT: raced to the finish, drifting where it fit" : "[drive] RESULT: FAILED");
        if (_shotPrefix != null && GetViewport().GetTexture().GetImage().SavePng($"{_shotPrefix}_end.png") == Error.Ok)
            GD.Print($"[drive] wrote {_shotPrefix}_end.png");
        Finish(ok ? 0 : 1);
    }

    private void Finish(int code)
    {
        _done = true;
        GetTree().Quit(code);
    }

    // ------------------------------------------------------------------------------------
    // geometry
    // ------------------------------------------------------------------------------------

    private float MaxCurvature(float s, float from, float to)
    {
        float k = 0;
        for (float d = from; d <= to; d += 4f) k = Mathf.Max(k, Mathf.Abs(_line!.Curvature[_line.IndexAt(s + d)]));
        return k;
    }

    /// <summary>Distance from the road centreline, searched near the closest point.</summary>
    private float Off(Vector3 pos)
    {
        int near = NearestCentre(pos);
        float off = float.MaxValue;
        for (int i = Mathf.Max(0, near - 3); i < Mathf.Min(_path.Count - 1, near + 3); i++)
            off = Mathf.Min(off, DistanceToSegment(Flat(pos), Flat(_path[i]), Flat(_path[i + 1])));
        return off;
    }

    private float HalfWidthAt(Vector3 pos) => _width[NearestCentre(pos)] * 0.5f;

    /// <summary>Nearest centreline point: a coarse stride over the 2 m polyline, then a fine search around it.</summary>
    private int NearestCentre(Vector3 pos)
    {
        int i = 0;
        float best = float.MaxValue;
        for (int k = 0; k < _path.Count; k += 16)
        {
            float d = Flat(_path[k] - pos).LengthSquared();
            if (d < best) { best = d; i = k; }
        }
        int c = i;
        for (int k = Mathf.Max(0, c - 16); k < Mathf.Min(_path.Count, c + 16); k++)
        {
            float d = Flat(_path[k] - pos).LengthSquared();
            if (d < best) { best = d; i = k; }
        }
        return i;
    }

    private static float Wrap(float a) => Mathf.Wrap(a, -Mathf.Pi, Mathf.Pi);

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Angle from a to b about +Y, radians; + is anticlockwise from above (to the left).</summary>
    private static float SignedAngle(Vector3 a, Vector3 b) =>
        Mathf.Atan2(a.Z * b.X - a.X * b.Z, a.X * b.X + a.Z * b.Z);

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 1e-6f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return p.DistanceTo(a + ab * t);
    }
}
