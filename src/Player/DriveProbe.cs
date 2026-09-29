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
/// <c>godot --path . -- --drivecheck[,out_prefix] [--car N] [--seconds S] [--record out.gpx] [--trace]
/// [--at E,N] [--traffic 0]</c>
///
/// <para>
/// Drives a car along the real road from the spawn — the main road, straight on at every junction
/// — with a scripted driver, filmed by a cinematic trackside camera. It grips through ordinary
/// bends and drifts a corner only where a drift is <b>actually feasible</b>: before each corner it
/// runs the car's own model forward on a copy of its state (<see cref="Car.Clone"/>) through a
/// handbrake entry and a held drift, at the current speed and after braking to 85% and 70%, and
/// commits to the fastest entry whose simulated line stays on the tarmac without spinning. That is
/// the difference between drifting a hairpin and drifting into the trees on a fast sweeper.
/// </para>
///
/// <para>
/// Prints every plan and every corner, time off the road and impacts; <c>--record</c> writes the
/// run as a GPX (with the car's yaw per fix) for the replay and Absolute Cinema. Non-zero exit if
/// it covered under 500 m, never held a planned drift, or wrecked the car; time off the road is
/// reported (and driven back from gently), not failed.
/// </para>
/// </summary>
public partial class DriveProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shotPrefix;
    private readonly int _car;
    private readonly double _seconds;
    private static readonly bool Trace = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--trace") >= 0;

    private FootPlayer? _player;
    private Car? _ride;
    private bool _mounted, _done, _pathRequested;
    private double _t, _wait;

    // the road to drive: one polyline, arc length at each point, width at each point
    private readonly List<Vector3> _path = new();
    private readonly List<float> _arc = new();
    private readonly List<float> _width = new();

    /// <summary>A narrow Jura pass is not a motorway: the scripted driver tops out here.</summary>
    private const float MaxSpeed = 30f;
    /// <summary>Only corners tighter than this radius, m, are considered for a drift.</summary>
    private const float DriftRadius = 160f;
    private const float PlanSeconds = 3.5f, SimDt = 1f / 60f;

    /// <summary>Everything the driver decides from, so the same policy drives the car and its simulations.</summary>
    private struct Driver
    {
        public int Near;
        public bool Drifting;
        public float Handbrake;
        public float Bend;
        public float Cap;
        public float PeakSlip, DriftTime;
        /// <summary>A drift the planner chose; a slide that happened by itself is only caught, never held.</summary>
        public bool Planned;
        /// <summary>Off the tarmac: ease back onto it slowly, no drifting until back.</summary>
        public bool Recovering;
    }

    private Driver _live = new() { Bend = 1f, Cap = MaxSpeed };
    private float _planTimer, _cooldown, _gripUntil;

    // results
    private int _drifts, _plans, _feasible, _shots, _impacts;
    private float _bestDrift, _offRoadTime, _worstOff, _topSpeed;
    private readonly List<string> _log = new();
    private bool _shotThisDrift;

    // cinematic camera
    private Camera3D? _cine;
    private bool _cinePlaced;
    private int _cineCorner = -1;

    // recording
    private readonly string? _record = ArgAfter("--record");
    private readonly StringBuilder _gpx = new();
    private double _sinceFix;

    public DriveProbe(ChunkManager chunks, WorldOrigin origin, string? shotPrefix, int car, double seconds)
    {
        _chunks = chunks;
        _origin = origin;
        _shotPrefix = shotPrefix;
        _car = car;
        _seconds = seconds;
    }

    public static (bool Requested, string? Shot, int Car, double Seconds) ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        bool requested = false;
        string? shot = null;
        int car = 0;
        double seconds = 90;
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
        if (_wait > _seconds + 120) { GD.Print("[drive] TIMEOUT"); Finish(2); return; }

        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);

        if (!_pathRequested)
        {
            if (_chunks.Source == null) return;
            _pathRequested = true;
            _ = BuildPath(at);
            return;
        }
        if (_path.Count < 2) return;

        if (_player == null)
        {
            // the driver is what asks for collision here, so it cannot wait for it
            if (!_chunks.TryGetHeight(_path[0], out float g)) return;
            _player = new FootPlayer { Name = "Driver", Terrain = _chunks };
            AddChild(_player);
            var dir = Flat(_path[Mathf.Min(8, _path.Count - 1)] - _path[0]);
            _player.GlobalPosition = _path[0] with { Y = g + 1.2f };
            _player.Rotation = new Vector3(0, Mathf.Atan2(-dir.X, -dir.Z), 0);
            _player.Impacted += lost => { if (lost > 2f) { _impacts++; _log.Add($"impact -{lost * 3.6f:F0} km/h at {_arc[_live.Near]:F0} m"); } };
            _player.Announced += (text, _) => _log.Add($"announce: {text}");
            return;
        }

        if (!_mounted)
        {
            if (!_player.IsOnFloor()) return;
            var kind = (RideKind)(CarCatalog.First + Mathf.Clamp(_car, 0, CarCatalog.All.Count - 1));
            _mounted = _player.SetRide(kind);
            _ride = _player.Vehicle as Car;
            GD.Print(_mounted ? $"[drive] driving {_ride!.Label} along {_arc[^1]:F0} m of road" : "[drive] MOUNT REFUSED");
            if (!_mounted) { Finish(1); return; }
            _player.RideControls = Drive;
            BeginGpx();
            return;
        }

        _t += delta;
        Measure(dt);
        Cinematic(dt);
        RecordFix(delta);
        if (Trace && (int)(_t * 4) != (int)((_t - delta) * 4))
        {
            var m = _player.Motion;
            GD.Print($"[drive]   t={_t,5:F2} s={_arc[_live.Near],6:F0} v={m.Speed * 3.6f,4:F0} slip={Mathf.RadToDeg(Wrap(m.Slip)),4:F0} "
                + $"off={Off(_player.GlobalPosition, _live.Near),5:F1} air={!_player.IsOnFloor()} drift={_live.Drifting} cap={_live.Cap * 3.6f:F0} in={_player.LastRideInput}");
        }
        if (_t >= _seconds || _live.Near >= _path.Count - 3 || _player.Ride != _ride!.Kind) End();
    }

    // ------------------------------------------------------------------------------------
    // the road
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The main road from the spawn: the nearest drivable edge, then at every junction the edge of
    /// the same class or better that carries on straightest — both ways from the start, keeping
    /// the longer run.
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
            GD.Print($"[drive] route: {best.Class}, {s:F0} m, {pts.Count} points");
            if (Trace)
                for (float at2 = 0; at2 < s; at2 += 20f)
                    GD.Print($"[drive]   road s={at2,5:F0} k={Curvature(at2) * 1000f,6:F1}/km  R={1f / Mathf.Max(Curvature(at2), 1e-4f),6:F0} m  w={_width[_arc.BinarySearch(at2) is var bi && bi < 0 ? ~bi - 1 : bi]:F1}");
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

    private RideInput Drive()
    {
        var p = _player!;
        float dt = (float)GetPhysicsProcessDeltaTime();
        var motion = p.Motion;
        bool wasDrifting = _live.Drifting, wasPlanned = _live.Planned;
        var input = Policy(ref _live, p.GlobalPosition, motion, dt);
        if (wasDrifting && !_live.Drifting) { _live.Planned = wasPlanned; EndDrift(); _live.Planned = false; }
        Plan(p.GlobalPosition, motion, dt);
        return input;
    }

    private RideInput Policy(ref Driver d, Vector3 pos, in RideMotion m, float dt)
    {
        float v = m.Speed;
        float slip = Wrap(m.Slip);
        while (d.Near < _path.Count - 2 && Flat(_path[d.Near + 1] - pos).Length() < Flat(_path[d.Near] - pos).Length()) d.Near++;
        float s0 = _arc[d.Near];

        // steer the TRAVEL toward a point ahead: in a drift the nose points elsewhere
        float look = Mathf.Clamp(v * 0.75f, 7f, 28f);
        var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward;
        float angle = SignedAngle(Flat(travel), Flat(PointAt(s0 + look) - pos));   // + = target to the left
        // gentler hands at speed: full lock at 90 km/h to fix a metre of line is what starts a slide
        float steer = Mathf.Clamp(-angle * 2.6f * Mathf.Clamp(15f / Mathf.Max(v, 1f), 0.4f, 1f), -1f, 1f);

        // speed from the curvature ahead: what the tyres hold round the tightest bend in braking reach
        float want = Mathf.Min(MaxSpeed, d.Cap);
        for (float a = 0; a < 120f; a += 4f)
        {
            float corner = Mathf.Sqrt(7.5f / Mathf.Max(Curvature(s0 + a), 1e-4f));
            // over a crest the road drops away faster than gravity can follow above √(g·R)
            corner = Mathf.Min(corner, Mathf.Sqrt(Rideable.Gravity * 0.9f / Mathf.Max(Crest(s0 + a), 1e-4f)));
            want = Mathf.Min(want, Mathf.Sqrt(corner * corner + 2f * 6f * a));
        }

        // Off the tarmac: everyone goes off sometimes. Come back to it gently — slow, soft hands,
        // no drifting — rather than yanking the wheel at 90 km/h, which is what starts the next slide.
        float past = Off(pos, d.Near) - _width[d.Near] * 0.5f;
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
            // which way the road bends here (+ left); hold the nose 30° into it while it lasts
            float bend = SignedAngle(Flat(PointAt(s0 + 5f) - PointAt(s0 - 5f)), Flat(PointAt(s0 + 25f) - PointAt(s0 + 5f)));
            if (Mathf.Abs(bend) > 0.04f) d.Bend = Mathf.Sign(bend);
            float soon = MaxCurvature(s0, 0f, 24f);
            float hold = d.Planned && soon > 1f / (DriftRadius * 1.6f) ? -d.Bend * 0.52f : 0f;
            if (handbrake) steer = -d.Bend * 0.9f;
            else
            {
                // the wheel holds the ANGLE (minus what the Game assist already counter-steers)...
                float wheel = slip + 1.5f * (slip - hold) - 0.12f * m.YawRate - (Rideable.Arcade ? 0.45f * slip : 0f);
                steer = Mathf.Clamp(-wheel / _ride!.Spec.MaxSteer, -1f, 1f);
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
    /// feasible entry wins; if none stays on the road, the corner is taken on grip.
    /// </summary>
    private void Plan(Vector3 pos, in RideMotion m, float dt)
    {
        _planTimer -= dt;
        _cooldown -= dt;
        float s0 = _arc[_live.Near];
        if (_live.Drifting || _live.Recovering || _planTimer > 0 || _cooldown > 0 || s0 < _gripUntil) return;
        _planTimer = 0.2f;
        if (m.Speed < 12f || MaxCurvature(s0, 10f, 40f) < 1f / DriftRadius) { _live.Cap = MaxSpeed; return; }

        _plans++;
        string tried = "";
        foreach (float factor in new[] { 1f, 0.85f, 0.7f })
        {
            var (ok, off, peak, entered) = Simulate(pos, m, m.Speed * factor);
            tried += $" {factor * 100:F0}%: off {off:F1} m, peak {Mathf.RadToDeg(peak):F0}°{(entered ? "" : " (no entry)")};";
            if (!ok) continue;
            _feasible++;
            if (factor == 1f)
            {
                _live.Drifting = true;
                _live.Planned = true;
                _live.Handbrake = 0.28f;
                _log.Add($"plan at {s0:F0} m: DRIFT now at {m.Speed * 3.6f:F0} km/h (sim peak {Mathf.RadToDeg(peak):F0}°, off {off:F1} m)");
            }
            else
            {
                // brake to the speed the simulation drifted at; the next plan, slower, commits
                _live.Cap = m.Speed * factor;
                _log.Add($"plan at {s0:F0} m: brake to {_live.Cap * 3.6f:F0} km/h, then drift");
            }
            return;
        }
        _log.Add($"plan at {s0:F0} m: GRIP, no feasible drift:{tried}");
        _live.Cap = MaxSpeed;
        _gripUntil = s0 + 60f;
    }

    private (bool Ok, float Off, float Peak, bool Entered) Simulate(Vector3 pos, RideMotion m, float cap)
    {
        var car = _ride!.Clone();
        var d = _live;
        d.Cap = cap;
        d.PeakSlip = 0;
        d.DriftTime = 0;
        bool entered = false;
        float maxOff = -100f, entryWait = 0;   // metres past the edge: negative is inside it
        for (float t = 0; t < PlanSeconds; t += SimDt)
        {
            if (!entered && m.Speed <= cap + 0.5f) { entered = true; d.Drifting = true; d.Planned = true; d.Handbrake = 0.28f; }
            if (!entered && (entryWait += SimDt) > 2f) break;
            var input = Policy(ref d, pos, m, SimDt);
            float s = _arc[d.Near];
            float grade = (PointAt(s + 3f).Y - PointAt(s - 3f).Y) / 6f;
            car.Step(input, new RideGround(true, grade), SimDt, ref m);
            var travel = new Basis(Vector3.Up, m.Yaw + m.Slip) * Vector3.Forward;
            pos += travel * m.Speed * SimDt;
            if (entered) maxOff = Mathf.Max(maxOff, Off(pos, d.Near) - _width[d.Near] * 0.5f);
            if (Mathf.Abs(Wrap(m.Slip)) > 1.35f) return (false, maxOff, d.PeakSlip, entered);
            if (entered && !d.Drifting && t > 1f) break;   // caught and straight: the drift is done
        }
        // half a metre INSIDE the edge: the plan runs on the road's centreline heights, and the real
        // camber and bumps have been measured to put the car 3-6 m wider than a plan allowed 0.8 m past it
        bool ok = entered && maxOff < -0.5f && d.PeakSlip > 0.35f;
        return (ok, maxOff, d.PeakSlip, entered);
    }

    private void EndDrift()
    {
        float deg = Mathf.RadToDeg(_live.PeakSlip);
        _log.Add($"{(_live.Planned ? "DRIFT" : "caught a slide")} at {_arc[_live.Near]:F0} m: peak {deg:F0}°, {_live.DriftTime:F1} s past 15°");
        if (_live.Planned && deg > 20f && _live.DriftTime > 0.5f) _drifts++;
        _bestDrift = Mathf.Max(_bestDrift, deg);
        _live.PeakSlip = 0;
        _live.DriftTime = 0;
        _live.Cap = MaxSpeed;
        _cooldown = 1.2f;
    }

    // ------------------------------------------------------------------------------------
    // measuring, filming, recording
    // ------------------------------------------------------------------------------------

    private void Measure(float dt)
    {
        var p = _player!;
        _topSpeed = Mathf.Max(_topSpeed, p.Motion.Speed);
        float over = Off(p.GlobalPosition, _live.Near) - _width[_live.Near] * 0.5f;
        _worstOff = Mathf.Max(_worstOff, over);
        if (over > 1.5f) _offRoadTime += dt;

        // a picture at the peak of the first few drifts, from the cinematic camera
        float slip = Mathf.Abs(Wrap(p.Motion.Slip));
        if (_shotPrefix != null && _live.Drifting && _shots < 4 && slip > 0.45f && _live.DriftTime > 0.3f && !_shotThisDrift)
        {
            _shotThisDrift = true;
            string file = $"{_shotPrefix}_drift{++_shots}.png";
            var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(file) == Error.Ok) GD.Print($"[drive] wrote {file} ({Mathf.RadToDeg(slip):F0}° at {p.Motion.Speed * 3.6f:F0} km/h)");
        }
        if (!_live.Drifting) _shotThisDrift = false;
    }

    /// <summary>
    /// The test is filmed: for each drifted corner a camera stands on the outside of the bend a
    /// little ahead and pans with the car through it; between corners it hangs back and above at
    /// three-quarters. It is placed on its first frame (never swept in from the world origin), never
    /// sits under the ground, and dissolves the trees between itself and the car with the same
    /// sightline cut the replay cameras use, so a forest never blocks the shot.
    /// </summary>
    private void Cinematic(float dt)
    {
        var p = _player!;
        if (_cine == null)
        {
            _cine = new Camera3D { Name = "Cinematic", Fov = 55f, Far = 20000f };
            AddChild(_cine);
        }
        var car = p.GlobalPosition + Vector3.Up * 0.8f;
        int corner = (int)(_arc[_live.Near] / 60f);
        if (_live.Drifting && _live.Handbrake > 0.2f && corner != _cineCorner)
        {
            // outside of the bend, 25 m on: the side the road turns away from
            _cineCorner = corner;
            float s = _arc[_live.Near] + 25f;
            var a = PointAt(s - 4f); var b = PointAt(s + 4f); var c = PointAt(s + 12f);
            var right = Flat(b - a).Normalized().Cross(Vector3.Up);
            float side = SignedAngle(Flat(b - a), Flat(c - b)) > 0 ? 1f : -1f;
            _cine.GlobalPosition = b + right * side * (_width[_live.Near] * 0.5f + 7f) + Vector3.Up * 2.5f;
        }
        else if (!_live.Drifting)
        {
            // three-quarter chase, high: behind, to the left and above
            var want = car + new Basis(Vector3.Up, p.Motion.Yaw + p.Motion.Slip) * new Vector3(-4f, 5f, 9f);
            _cine.GlobalPosition = _cinePlaced ? _cine.GlobalPosition.Lerp(want, 1f - Mathf.Exp(-3f * dt)) : want;
        }
        _cinePlaced = true;

        // never under the ground: at least 1.5 m of air under the lens
        var cp = _cine.GlobalPosition;
        if (_chunks.TryGetHeight(cp, out float ground) && cp.Y < ground + 1.5f) _cine.GlobalPosition = cp with { Y = ground + 1.5f };

        var look = car - _cine.GlobalPosition;
        if (look.LengthSquared() > 0.25f && Mathf.Abs(look.Normalized().Y) < 0.98f)
            _cine.GlobalBasis = Basis.LookingAt(look.Normalized(), Vector3.Up);
        _chunks.SetSightlineCut(_cine.GlobalPosition, car, 3f);
        _cine.Current = true;
    }

    private void BeginGpx()
    {
        if (_record == null) return;
        _gpx.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<gpx version=\"1.1\" creator=\"UnitSportSwitzerland drivecheck\" xmlns=\"http://www.topografix.com/GPX/1/1\" ")
            .Append("xmlns:us=\"https://github.com/SuperQuentin/UnitSportSwitzerland\">\n")
            .Append($"<trk><name>{_ride!.Label} over the pass</name><type>car:{(int)_ride.Kind - CarCatalog.First}</type><trkseg>\n");
    }

    /// <summary>
    /// One fix every 0.2 s. The yaw (the NOSE, not the direction of travel) rides in an extension:
    /// a track only knows where the car went, and a drift is exactly where the two differ.
    /// </summary>
    private void RecordFix(double delta)
    {
        if (_record == null) return;
        _sinceFix += delta;
        if (_sinceFix < 0.2) return;
        _sinceFix = 0;
        var p = _player!;
        var (e, n) = _origin.ToLv95(p.GlobalPosition);
        var (lat, lon) = SwissProjection.ToWgs84(e, n);
        var ic = CultureInfo.InvariantCulture;
        var time = new System.DateTime(2026, 9, 30, 12, 0, 0, System.DateTimeKind.Utc).AddSeconds(_t);
        _gpx.Append(ic, $"<trkpt lat=\"{lat:F7}\" lon=\"{lon:F7}\"><ele>{p.GlobalPosition.Y:F1}</ele>")
            .Append(ic, $"<time>{time:yyyy-MM-ddTHH:mm:ss.fffZ}</time>")
            .Append(ic, $"<extensions><us:yaw>{p.Motion.Yaw:F4}</us:yaw></extensions></trkpt>\n");
    }

    private void End()
    {
        if (_live.PeakSlip > 0) EndDrift();
        var p = _player!;
        bool wrecked = p.Ride != _ride!.Kind;
        float covered = _arc[_live.Near];
        foreach (var line in _log) GD.Print($"[drive]   {line}");
        GD.Print($"[drive] {_ride.Label}: {covered:F0} m in {_t:F0} s (avg {covered / Mathf.Max((float)_t, 1f) * 3.6f:F0} km/h, "
            + $"top {_topSpeed * 3.6f:F0} km/h), {_plans} corners planned, {_feasible} drift plans feasible, "
            + $"{_drifts} held drifts, best {_bestDrift:F0}°, off road {_offRoadTime:F1} s (worst {_worstOff:F1} m past the edge), "
            + $"{_impacts} impacts, car {(wrecked ? "WRECKED" : $"{p.VehicleHealth:F0}/{_ride.MaxHealth:F0} HP")}");
        // Going off happens to everyone: it is reported, not failed. What must hold is that the car
        // got down the pass, drifted where the plan said it could, and came back in one piece.
        bool ok = covered > 500f && _drifts > 0 && !wrecked;
        GD.Print(ok ? "[drive] RESULT: drove the pass and drifted the corners it could" : "[drive] RESULT: FAILED");
        if (_shotPrefix != null)
        {
            var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng($"{_shotPrefix}_end.png") == Error.Ok) GD.Print($"[drive] wrote {_shotPrefix}_end.png");
        }
        if (_record != null)
        {
            _gpx.Append("</trkseg></trk></gpx>\n");
            System.IO.File.WriteAllText(_record, _gpx.ToString());
            GD.Print($"[drive] recorded {_record}");
        }
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

    private Vector3 PointAt(float s)
    {
        if (s <= 0) return _path[0];
        if (s >= _arc[^1]) return _path[^1];
        int i = _arc.BinarySearch(s);
        if (i < 0) i = ~i - 1;
        float span = _arc[i + 1] - _arc[i];
        return _path[i].Lerp(_path[i + 1], span > 1e-4f ? (s - _arc[i]) / span : 0f);
    }

    /// <summary>Heading change per metre over ±8 m around arc length s.</summary>
    private float Curvature(float s)
    {
        var a = Flat(PointAt(s) - PointAt(s - 8f));
        var b = Flat(PointAt(s + 8f) - PointAt(s));
        if (a.LengthSquared() < 1f || b.LengthSquared() < 1f) return 0f;
        return Mathf.Abs(SignedAngle(a, b)) / 16f;
    }

    /// <summary>Vertical curvature of a crest (1/m, 0 in a dip) over ±10 m around s.</summary>
    private float Crest(float s)
    {
        float y0 = PointAt(s - 10f).Y, y1 = PointAt(s).Y, y2 = PointAt(s + 10f).Y;
        return Mathf.Max(0f, (2f * y1 - y0 - y2) / 100f);
    }

    private float MaxCurvature(float s, float from, float to)
    {
        float k = 0;
        for (float d = from; d <= to; d += 4f) k = Mathf.Max(k, Curvature(s + d));
        return k;
    }

    /// <summary>Distance from the road centreline near index <paramref name="near"/>.</summary>
    private float Off(Vector3 pos, int near)
    {
        float off = float.MaxValue;
        for (int i = Mathf.Max(0, near - 6); i < Mathf.Min(_path.Count - 1, near + 6); i++)
            off = Mathf.Min(off, DistanceToSegment(Flat(pos), Flat(_path[i]), Flat(_path[i + 1])));
        return off;
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
