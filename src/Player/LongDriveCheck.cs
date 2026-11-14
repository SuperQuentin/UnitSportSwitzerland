using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --frame earth --longdrivecheck [--to E,N] [--car N] [--corridor m] [--at E,N]</c> (#1710, G2 D0; plan
/// <c>docs/plans/global-tiling-g2-d.md</c> section 5, the long drive): one car on its <see cref="AutoPilot"/> down the
/// lane graph for tens of km, across many leaves and re-anchors. With a destination (<c>--to</c>, LV95 notation) the
/// route is planned once, end to end (#1744, <see cref="RaceRoute.ToAsync"/>): the lane graph of the corridor to it
/// (<c>--corridor</c> m each side, 2 km), the leaves' roads on earth and the LV95 tiles' on flat, the shortest way along
/// it on the rightmost lane of each street; the pilot keeps to each road's speed (<see cref="SpeedOf"/>). Fails on:
/// <list type="bullet">
/// <item>a stall: 60 s with less than 50 m won toward the destination (or along the road, with no <c>--to</c>);</item>
/// <item>a fall: the car 1 m under the ground's height for half a second, or out of the car;</item>
/// <item>a jump at a re-anchor, of the car or the chase camera, or of the ground under it (<see cref="ReanchorWatch"/>);</item>
/// <item>no route to the destination, the route ending short of it, or no re-anchor at all.</item>
/// </list>
/// No <c>--to</c> (a fixture course, the quick tier): one leg, the longer way along the road from the start, to its end.
/// <c>tools/longdrivecheck.sh</c> drives Geneva → Nyon on the D: region's leaves. RESULT ok or FAILED.
/// </summary>
public partial class LongDriveCheck : Node
{
    public static bool Requested() => CmdArgs.Has("--longdrivecheck");

    private const float StallS = 60f, StallM = 50f, ArriveM = 400f;
    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly Func<CubeKey, RoadTile?>? _leafRoads;
    private readonly List<string> _faults = new();
    private readonly double _corridor = CmdArgs.Double("--corridor") is double c && c >= 300 ? c : 2000;
    // --along Motorway: the drive starts and ends on that class of road (the bar's drive on the A1: no junction islands,
    // signal poles or old-town corners, the frame's re-anchors at speed)
    private readonly RoadClass? _along = Enum.TryParse<RoadClass>(CmdArgs.Value("--along"), true, out var cls) ? cls : null;
    private readonly int _car = CmdArgs.Int("--car") ?? 4;
    // a loss of more than this within a quarter second is a hit, not braking (3 m/s² is 0.75 m/s)
    private const float JoltMs = 4f;
    private readonly float[] _speeds = new float[15];
    private int _tick;
    private int _jolts;
    private GlobalPos? _to;
    private FootPlayer? _player;
    private ILongDriveDriver? _pilot;
    private Camera3D _camera = null!;
    private ReanchorWatch _watch = null!;
    private Task<RaceRoute?>? _next;
    private int _startTries;
    private const int StartTries = 6;
    private const float StartStepM = 150f, MinLegM = 200f;
    private int _phase;
    private double _t, _report, _stallT, _belowS, _bestToGo = double.MaxValue, _bestArc;
    private double _driven;
    private GlobalPos? _last;
    private ulong _startMs;
    private float _top;

    /// <param name="leafRoads">The earth world's leaves' roads (<c>EarthLeaves.RoadsOf</c>); null on the flat world.</param>
    public LongDriveCheck(IWorldGround ground, WorldOrigin origin, Func<CubeKey, RoadTile?>? leafRoads = null)
    {
        _ground = ground;
        _origin = origin;
        _leafRoads = leafRoads;
        Name = "LongDriveCheck";
    }

    private double Now => (Time.GetTicksMsec() - _startMs) / 1000.0;

    public override void _Ready()
    {
        _startMs = Time.GetTicksMsec();
        if (CmdArgs.Value("--to")?.Split(',') is [var te, var tn]
            && double.TryParse(te, NumberStyles.Float, CultureInfo.InvariantCulture, out double e)
            && double.TryParse(tn, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            _to = GlobalPos.FromLv95(e, n, 0);
        // places known blocked (--avoid E,N;E,N, LV95 notation): planned round from the start, as one found on the way
        foreach (var place in (CmdArgs.Value("--avoid") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (place.Split(',') is [var ae, var an]
                && double.TryParse(ae, NumberStyles.Float, CultureInfo.InvariantCulture, out double be)
                && double.TryParse(an, NumberStyles.Float, CultureInfo.InvariantCulture, out double bn))
                _blocked.Add(GlobalPos.FromLv95(be, bn, 0));
        _known = _blocked.Count;
        AddChild(_camera = new Camera3D { Name = "Chase", Far = 30_000, Current = true });
        _camera.MakeCurrent();
        var (se, sn) = SpawnPoint.ParseTarget();
        _camera.GlobalPosition = _origin.ToWorld(se, sn, 0) + Vector3.Up * 20f;
        _ground.AddAnchor(_camera, collision: true);
        _watch = new ReanchorWatch(_origin, _ground, "longdrive") { Shots = DisplayServer.GetName() != "headless", JumpTolM = 0.25f };
        AddChild(_watch);
        Log($"start LV95 {se:F0}/{sn:F0}{(_to is { } to ? Inv($", to {Lv(to)}, {to.HorizontalDistanceTo(GlobalPos.FromLv95(se, sn, 0)) / 1000:F1} km as the crow flies") : ", along the road")}, "
            + Inv($"car {_car}, the corridor {_corridor:F0} m each side, roads from {(_leafRoads != null ? "the leaves" : "the LV95 tiles")}, ")
            + $"origin {(_origin.Frame.IsAnchor ? "an anchor frame" : "FLAT")}");
    }

    public override void _ExitTree()
    {
        _ground.RemoveAnchor(_camera);
    }

    // the drive's timeout: 12 m/s on average at worst, plus the start
    private double TimeoutS => 240 + (_to is { } to ? to.HorizontalDistanceTo(Start) * 1.5 / 12.0 : 600);
    private GlobalPos Start { get { var (e, n) = SpawnPoint.ParseTarget(); return GlobalPos.FromLv95(e, n, 0); } }

    public override void _PhysicsProcess(double delta)
    {
        if (_phase < 0) return;
        if (!_camera.Current) _camera.MakeCurrent();
        if (Now > TimeoutS) { Fault($"timed out after {_driven / 1000:F1} km"); Finish(); return; }
        switch (_phase)
        {
            case 0: FirstLeg(); break;
            case 1: Mount(); break;
            case 2: Drive((float)delta); break;
        }
    }

    /// <summary>The route from <paramref name="at"/>: to the destination along the corridor's lane graph, or (none) the longer way along the road.</summary>
    private Task<RaceRoute?> BuildRoute(Vector3 at)
    {
        List<Vector3>? avoid = _blocked.Count == 0 ? null : _blocked.ConvertAll(b => _origin.ToWorld(b));
        var source = _ground.Content;
        if (source == null) return Task.FromResult<RaceRoute?>(null);
        var frame = _origin.Frame;
        if (_to is { } to)
        {
            var target = _origin.ToWorld(to);
            // --routeroads lv95: the LV95 tiles in either frame (the parity runs: one route, flat and earth alike)
            var leafRoads = CmdArgs.Value("--routeroads") == "lv95" ? null : _leafRoads;
            double half = _corridor;
            var along = _along;
            return Task.Run(() => RaceRoute.ToAsync(source, leafRoads, frame, at, target, half, Log, avoid, along));
        }
        return Task.Run(() => RaceRoute.BuildAsync(source, frame, at, smallest: RoadClass.Road));
    }

    private void FirstLeg()
    {
        var at = _camera.GlobalPosition with { Y = 0 };
        if (!_ground.HasCollisionAt(at) || !_ground.TryGetHeight(at, out float h)) return;
        _camera.GlobalPosition = at with { Y = h + 20f };
        _next ??= BuildRoute(at with { Y = h });
        if (!_next.IsCompleted) return;
        var route = _next.IsCompletedSuccessfully ? _next.Result : null;
        _next = null;
        if (route == null || route.Length < MinLegM)
        {
            // no route from here (a street cut off from the rest, a stub): look again a little further on toward the destination
            if (_to is { } dest && ++_startTries < StartTries)
            {
                var g0 = _origin.ToGlobal(at);
                var (de, dn, _) = g0.OffsetTo(dest);
                double d = Math.Max(1.0, g0.HorizontalDistanceTo(dest));
                _camera.GlobalPosition = _origin.ToWorld(g0.Moved(de / d * StartStepM, dn / d * StartStepM)) with { Y = _camera.GlobalPosition.Y };
                Log(Inv($"no route from the start ({(route == null ? "none" : $"{route.Length:F0} m")}): looking again {StartStepM * _startTries:F0} m on"));
                return;
            }
            Fault(route == null ? "no road near the start" : Inv($"only a {route.Length:F0} m road near the start"));
            Finish();
            return;
        }
        route.Follow(_origin.Frame);
        var line = route.Line;
        var p = line.PointAt(10f);
        var fwd = RaceRoute.Flat(line.PointAt(12f) - line.PointAt(8f)).Normalized();
        float g = _ground.TryGetHeight(p, out float gh) ? gh : p.Y;
        // a start on a bridge (#1759): on its deck, not on the ground under it (the A1 at Bellevue is 7 m up: the car was set
        // down under the deck and drove along the ground until the abutment's slope wedged it under the deck)
        if (p.Y > g + 1f) g = p.Y;
        _player = new FootPlayer { Name = "Driver", Terrain = _ground };
        AddChild(_player);
        _player.GlobalPosition = p with { Y = g + 1.2f };
        _player.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
        _pending = route;
        _phase = 1;
        Log(Inv($"route: {route.Length / 1000:F2} km ({route.Class} at the start{ClassesSaid(route)}), built at {Now:F1} s"));
    }

    private RaceRoute? _pending;

    /// <summary>
    /// Places the car was found stuck at (#1744): a road its route took that a car cannot drive (a passage the ground or a
    /// building closes, on flat and earth alike in Geneva). The drive plans again round them, as a driver would, up to
    /// <see cref="MaxReplans"/> times; each is logged ("blocked:") for its own issue, not counted as the frame's fault.
    /// </summary>
    private readonly List<GlobalPos> _blocked = new();
    private int _known;   // how many of them --avoid gave
    private Task<RaceRoute?>? _replan;
    private const int MaxReplans = 8;
    private const float ReplanAfterS = 15f, BlockAheadM = 6f;

    private void Mount()
    {
        if (_player is not { } p || !p.IsOnFloor()) return;
        var spec = CarCatalog.All[Mathf.Clamp(_car, 0, CarCatalog.All.Count - 1)];
        if (!p.SetRide(spec.Kind)) { Fault($"could not get into car {_car}"); Finish(); return; }
        TakeRoute(_pending!);
        _pending = null;
        _watch.Watch("car", () => _player);
        _watch.Watch("camera", () => _camera);
        _watch.WatchGround(() => _player?.GlobalPosition);
        _last = _origin.ToGlobal(p.GlobalPosition);
        _phase = 2;
        Log($"driving {spec.Label}");
    }

    private void TakeRoute(RaceRoute route)
    {
        route.Follow(_origin.Frame);
        ILongDriveDriver driver;
        if (_to != null) driver = new LaneFollower(route, _player!, _origin);   // a drive to a destination: on its lane
        else
        {
            // a fixture course: the racing pilot, flat out
            var racer = AutoPilot.For(route, _player!);
            if (racer == null) { Fault("no pilot for the car"); Finish(); return; }
            racer.Temperament(0.9f, 0f, 1);
            racer.Go = true;
            racer.Log = m => GD.Print("[longdrivecheck] pilot: " + m);
            driver = new RacingDriver(racer);
        }
        _pilot = driver;
        _bestArc = 0;
        _player!.RideControls = () => driver.Drive((float)GetPhysicsProcessDeltaTime());
    }

    private void Drive(float dt)
    {
        var p = _player!;
        var pilot = _pilot!;
        _t += dt;
        var here = _origin.ToGlobal(p.GlobalPosition);
        if (_last is { } last) _driven += here.DistanceTo(last);
        _last = here;
        float speed = p.Motion.Speed;
        // a dead stop (#1759, #1757): more than JoltMs lost within a quarter second, logged every tick while it lasts, with
        // what the body was touching, for the issue
        _speeds[_tick++ % _speeds.Length] = speed;
        float before = _speeds[_tick % _speeds.Length];
        if (before - speed > JoltMs && _jolts < 40)
        {
            _jolts++;
            var contacts = new System.Text.StringBuilder();
            for (int i = 0; i < p.GetSlideCollisionCount(); i++)
            {
                var c = p.GetSlideCollision(i);
                var at = c.GetPosition() - p.GlobalPosition;
                contacts.Append(Inv($"; {(c.GetCollider() as Node)?.GetPath()} at {at.X:+0.00;-0.00},{at.Y:+0.00;-0.00},{at.Z:+0.00;-0.00} normal {c.GetNormal().X:F2},{c.GetNormal().Y:F2},{c.GetNormal().Z:F2}"));
            }
            float over = _ground.TryGetHeight(p.GlobalPosition, out float gh) ? p.GlobalPosition.Y - gh : float.NaN;
            Log(Inv($"jolt: {before * 3.6f:F0} -> {speed * 3.6f:F0} km/h in 0.25 s at {LvAt(p)}, y {p.GlobalPosition.Y:F2}, {over:F2} m over the ground, ")
                + Inv($"heading {Mathf.RadToDeg(p.GlobalRotation.Y):F0}°{contacts}"));
        }
        _top = Mathf.Max(_top, speed);
        Chase(p);

        if (p.Ride == RideKind.OnFoot) { Fault($"out of the car at {_driven / 1000:F2} km ({LvAt(p)})"); Finish(); return; }
        // a fall: under the ground's height (through a patch, off the data's edge); a tunnel is under it by design
        if (!pilot.Route.TunnelAt(pilot.Arc) && _ground.TryGetHeight(p.GlobalPosition, out float g) && p.GlobalPosition.Y < g - 1f)
        {
            if ((_belowS += dt) > 0.5) { Fault($"fell under the ground ({g - p.GlobalPosition.Y:F1} m) at {_driven / 1000:F2} km ({LvAt(p)})"); Finish(); return; }
        }
        else _belowS = 0;

        // a drive, not a race: each road's speed, the lower of here and 80 m on, and its bends taken gently (a fixture
        // course with no destination: flat out, the racing pilot's own)
        if (_to != null)
            pilot.MaxSpeed = Mathf.Min(Mathf.Min(SpeedOf(pilot.Route.ClassAt(pilot.Arc)), SpeedOf(pilot.Route.ClassAt(pilot.Arc + 80f))),
                BendSpeed(pilot.Route, pilot.Arc));

        // progress: toward the destination, or along the road
        double toGo = _to is { } to ? here.HorizontalDistanceTo(to) : pilot.Route.Length - pilot.Arc;
        bool won = pilot.Arc > _bestArc + StallM;   // along the route: a planned way may head away from the destination first
        if (won || _stallT == 0) { _stallT = _t; _bestToGo = Math.Min(_bestToGo, toGo); _bestArc = Math.Max(_bestArc, pilot.Arc); }
        // stuck: plan again round the place, from where the car is (it waits meanwhile)
        if (_replan is { IsCompleted: true } replanned)
        {
            _replan = null;
            var route = replanned.IsCompletedSuccessfully ? replanned.Result : null;
            if (route == null || route.Length < MinLegM) { Fault(Inv($"no way round the place blocked at {LvAt(p)}")); Finish(); return; }
            TakeRoute(route);
            _stallT = _t;
            Log(Inv($"route again: {route.Length / 1000:F2} km round {_blocked.Count} blocked place(s)"));
            return;
        }
        if (_replan != null) { _stallT = _t; return; }
        if (_to != null && _t - _stallT > ReplanAfterS && _blocked.Count - _known < MaxReplans)
        {
            var fwd = (-p.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
            var block = p.GlobalPosition + fwd * BlockAheadM;
            _blocked.Add(_origin.ToGlobal(block));
            Log(Inv($"blocked: stuck {ReplanAfterS:F0} s at {LvAt(p)} on a {pilot.Route.ClassAt(pilot.Arc)}, {Ahead(p)}: planning round it"));
            Log(RouteHere(pilot) + (pilot is LaneFollower lf ? "; the driver: " + lf.Said : ""));
            if (DisplayServer.GetName() != "headless")
            {
                // windowed: the chase camera's picture of the place, for its issue
                string shot = Inv($"test_output/longdrive_blocked_{_blocked.Count - _known}.png");
                GetViewport().GetTexture().GetImage().SavePng(shot);
                Log("picture: " + shot);
            }
            pilot.Go = false;
            _replan = BuildRoute(p.GlobalPosition);
            _stallT = _t;
            return;
        }
        if (_t - _stallT > StallS) { Fault($"stalled: {StallS:F0} s without {StallM:F0} m won, at {_driven / 1000:F2} km ({LvAt(p)}), {speed * 3.6f:F0} km/h, {Ahead(p)}"); Finish(); return; }

        _report += dt;
        if (_report >= 5)
        {
            _report = 0;
            Log(Inv($"t={_t,5:F0} s {_driven / 1000,6:F2} km {speed * 3.6f,4:F0} km/h at {pilot.Arc:F0}/{pilot.Route.Length:F0} m on {pilot.Route.ClassAt(pilot.Arc)}, {toGo / 1000:F2} km to go, {_watch.Shifts} re-anchors, {LvAt(p)}") + (pilot is LaneFollower f ? "; " + f.Said : ""));
        }

        if (_to != null && toGo < ArriveM) { Log(Inv($"arrived: {toGo:F0} m from the destination")); Finish(); return; }
        float left = pilot.Route.Length - pilot.Arc;
        if (left < 60f)
        {
            if (_to != null) Fault(Inv($"the route ended {toGo / 1000:F2} km from the destination ({LvAt(p)})"));
            else Log(Inv($"the road's end after {_driven:F0} m"));
            Finish();
        }
    }

    // the route from 10 m behind the car to 40 m on: its height against the ground's and its road (a block's cause)
    private string RouteHere(ILongDriveDriver pilot)
    {
        var r = pilot.Route;
        var parts = new List<string>();
        for (float d = -10f; d <= 40f; d += 5f)
        {
            float s = pilot.Arc + d;
            var c = r.Centre[r.NearestCentreIndexAt(s)];
            string ground = _ground.TryGetHeight(c, out float g) ? Inv($"{c.Y - g:+0.0;-0.0}") : "?";
            var kind = r.Kinds is { Count: > 0 } k ? k[Mathf.Min(r.NearestCentreIndexAt(s), k.Count - 1)] : default;
            var (ce, cn) = _origin.ToLv95(c);
            parts.Add(Inv($"{d:+0;-0}m: road {ground} m over the ground at {ce:F1}/{cn:F1} y {c.Y:F2}, {kind.Class}{(kind.Flags != 0 ? " " + kind.Flags : "")}"));
        }
        return "the route here: " + string.Join("; ", parts);
    }

    // behind and above the car, looking at it: the origin follows this camera
    private void Chase(FootPlayer p)
    {
        var v = RaceRoute.Flat(p.WorldVelocity);
        var fwd = v.LengthSquared() > 1f ? v.Normalized() : -p.GlobalTransform.Basis.Z with { Y = 0 };
        if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.Forward;
        var want = p.GlobalPosition - fwd.Normalized() * 10f + Vector3.Up * 4f;
        _camera.GlobalPosition = want;
        _camera.LookAt(p.GlobalPosition + Vector3.Up, Vector3.Up);
    }

    /// <summary>Sideways and braking accelerations a drive keeps to in town streets (m/s²): a driver's, not a racer's.</summary>
    private const float BendAccel = 2.5f, BrakeAccel = 3f, BendLookM = 150f;

    /// <summary>
    /// The speed (m/s) from which every bend of the route's own line in the next <see cref="BendLookM"/> m can be reached
    /// braking at <see cref="BrakeAccel"/> and taken at <see cref="BendAccel"/> sideways. The line the car follows, not the
    /// racing line (which smooths a junction's corner away: a turn from a Major road into a side street came at 35 km/h
    /// and went 8 m wide, #1744). The bend at a point: the turn between the 4 m before it and the 4 m after, over 4 m.
    /// </summary>
    private static float BendSpeed(RaceRoute route, float arc)
    {
        var c = route.Centre;
        float best = float.MaxValue;
        for (float d = 0; d <= BendLookM; d += 2f)
        {
            int i = route.NearestCentreIndexAt(arc + d);
            if (i < 2 || i + 2 >= c.Count) continue;
            var a = RaceRoute.Flat(c[i] - c[i - 2]);
            var b = RaceRoute.Flat(c[i + 2] - c[i]);
            if (a.LengthSquared() < 1e-4f || b.LengthSquared() < 1e-4f) continue;
            float k = Mathf.Abs(RaceRoute.SignedAngle(a, b)) / (0.5f * (a.Length() + b.Length()));
            if (k < 1e-4f) continue;
            best = Mathf.Min(best, Mathf.Sqrt(BendAccel / k + 2f * BrakeAccel * d));
        }
        return Mathf.Max(best, 2.5f);
    }

    /// <summary>The speed a drive keeps to on a class of road (m/s): <see cref="RaceRoute.DriveSpeed"/>, which plans by it too.</summary>
    public static float SpeedOf(RoadClass c) => RaceRoute.DriveSpeed(c);

    // the classes a route runs on and their lengths, for the log
    private static string ClassesSaid(RaceRoute r)
    {
        if (r.Kinds is not { Count: > 0 } c) return "";
        var metres = new Dictionary<RoadClass, float>();
        foreach (var (k, _) in c) metres[k] = metres.GetValueOrDefault(k) + 2f;
        var parts = new List<string>();
        foreach (var (k, m) in metres) parts.Add(Inv($"{k} {m / 1000:F1} km"));
        return "; " + string.Join(", ", parts);
    }

    // what is in front of the car (a stall's or a crash's cause): the first body 12 m ahead at bumper height, else at a kerb's
    private string Ahead(FootPlayer p) { var high = Ahead(p, 0.7f); return high.StartsWith("nothing") ? Ahead(p, 0.3f) : high; }

    private string Ahead(FootPlayer p, float height)
    {
        var fwd = (-p.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
        var from = p.GlobalPosition + Vector3.Up * height;
        var q = PhysicsRayQueryParameters3D.Create(from, from + fwd * 12f);
        q.Exclude = new Godot.Collections.Array<Rid> { p.GetRid() };
        var hit = p.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count == 0) return Inv($"nothing 12 m ahead at {height:F1} m");
        var c = hit["collider"].AsGodotObject() as Node;
        float d = from.DistanceTo(hit["position"].AsVector3());
        return Inv($"ahead at {d:F1} m ({height:F1} m up): {c?.GetType().Name} {c?.GetPath()}");
    }

    // LV95 as notation only, for the log
    private static string Lv(GlobalPos p) { var (e, n, _) = p.Lv95; return Inv($"{e:F0}/{n:F0}"); }

    private string LvAt(Node3D n) { var (e, nn) = _origin.ToLv95(n.GlobalPosition); return Inv($"LV95 {e:F0}/{nn:F0}"); }

    private void Finish()
    {
        if (_phase < 0) return;
        _phase = -1;
        if (_player != null) _player.RideControls = null;
        _watch.Judge();
        if (_pilot is LaneFollower backed && backed.BackOffs > 0) Log(Inv($"backed off {backed.BackOffs} time(s) from something it ran into"));
        if (_blocked.Count > _known) Log(Inv($"{_blocked.Count - _known} place(s) found blocked and driven round (see the blocked: lines)"));
        Log(Inv($"drove {_driven / 1000:F2} km in {_t:F0} s ({(_t > 0 ? _driven / _t * 3.6 : 0):F0} km/h on average, top {_top * 3.6f:F0})"));
        Log(_watch.Summary().Replace("[longdrive] ", ""));
        Parity();
        foreach (var f in _watch.Faults) Fault(f);
        Log(_faults.Count == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_faults.Count}: {_faults[0]})");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// The drive's result for the bar's parity (#1744; <c>tools/longdrivecheck.sh</c> runs it on flat, then on earth, and
    /// compares): "parity blocked" at each place found blocked, "parity end" with how and where the drive ended (the
    /// first fault's kind, or "arrived"), and "parity frame" for each re-anchor fault of the frame's own (a jump, the
    /// ground moving), which no flat run can match. Real roads stop the drive at places the frame has nothing to do with
    /// (the Geneva blockers, #1744): earth passes when it stops at the same places, the same way.
    /// </summary>
    private void Parity()
    {
        for (int i = _known; i < _blocked.Count; i++) Log("parity blocked " + Lv(_blocked[i]));
        string end = _faults.Count == 0 ? "arrived" : Kind(_faults[0]);
        Log($"parity end {end} " + (_player != null ? Lv(_origin.ToGlobal(_player.GlobalPosition)) : "-"));
        foreach (var f in _watch.Faults)
            if (f != "no re-anchor happened") Log("parity frame " + f);
    }

    // a fault's kind: its words before where it happened ("stalled", "out of the car", "no way round the place blocked")
    private static string Kind(string fault)
    {
        int cut = fault.Length;
        foreach (var mark in new[] { ":", " at ", " (", " after " })
            if (fault.IndexOf(mark, StringComparison.Ordinal) is int i and >= 0) cut = Math.Min(cut, i);
        for (int i = 0; i < cut; i++)
            if (char.IsDigit(fault[i])) { cut = i; break; }   // "the route ended 0.50 km from ...": the words alone
        return fault[..cut].Trim().Replace(' ', '_');
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    // logged as it happens: a run stopped by the RAM floor or a timeout still says what it found
    private void Fault(string f)
    {
        _faults.Add(f);
        Log($"FAULT: {f}");
    }

    private static void Log(string s) => GD.Print("[longdrivecheck] " + s);
}
