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
/// <c>godot --path . -- --frame earth --longdrivecheck [--to E,N] [--car N] [--legm m] [--at E,N]</c> (#1710, G2 D0; plan
/// <c>docs/plans/global-tiling-g2-d.md</c> section 5, the long drive): one car on its <see cref="AutoPilot"/> down the
/// lane graph for tens of km, across many leaves and re-anchors. A <see cref="RaceRoute"/> reads the roads 4 tiles round
/// where it is built, so the drive is a chain of legs: each one toward a point <c>--legm</c> (3 km) on toward the
/// destination (<c>--to</c>, LV95 notation), the next one built from the car's place 1.2 km before the current one ends,
/// its pilot taking over on the move. Fails on:
/// <list type="bullet">
/// <item>a stall: 60 s with less than 50 m won toward the destination (or along the road, with no <c>--to</c>);</item>
/// <item>a fall: the car 1 m under the ground's height for half a second, or out of the car;</item>
/// <item>a jump at a re-anchor, of the car or the chase camera, or of the ground under it (<see cref="ReanchorWatch"/>);</item>
/// <item>no leg toward the destination (no road), or under 3 re-anchors in all.</item>
/// </list>
/// No <c>--to</c> (a fixture course, the quick tier): one leg, the longer way along the road from the start, to its end.
/// <c>tools/longdrivecheck.sh</c> drives Geneva → Nyon on the D: region's leaves. RESULT ok or FAILED.
/// </summary>
public partial class LongDriveCheck : Node
{
    public static bool Requested() => CmdArgs.Has("--longdrivecheck");

    private const float BuildAheadM = 1200f, StallS = 60f, StallM = 50f, ArriveM = 400f;
    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private readonly float _legM = CmdArgs.Float("--legm") is float l && l > 500 ? l : 3000f;
    private readonly int _car = CmdArgs.Int("--car") ?? 4;
    private GlobalPos? _to;
    private FootPlayer? _player;
    private AutoPilot? _pilot;
    private Camera3D _camera = null!;
    private ReanchorWatch _watch = null!;
    private Task<RaceRoute?>? _next;
    private int _legs, _phase;
    private double _t, _report, _stallT, _belowS, _bestToGo = double.MaxValue, _bestArc;
    private double _driven;
    private GlobalPos? _last;
    private ulong _startMs;
    private float _top;

    public LongDriveCheck(IWorldGround ground, WorldOrigin origin)
    {
        _ground = ground;
        _origin = origin;
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
        AddChild(_camera = new Camera3D { Name = "Chase", Far = 30_000, Current = true });
        _camera.MakeCurrent();
        var (se, sn) = SpawnPoint.ParseTarget();
        _camera.GlobalPosition = _origin.ToWorld(se, sn, 0) + Vector3.Up * 20f;
        _ground.AddAnchor(_camera, collision: true);
        _watch = new ReanchorWatch(_origin, _ground, "longdrive") { Shots = DisplayServer.GetName() != "headless", JumpTolM = 0.25f };
        AddChild(_watch);
        Log($"start LV95 {se:F0}/{sn:F0}{(_to is { } to ? Inv($", to {to.Lv95.E:F0}/{to.Lv95.N:F0}, {to.HorizontalDistanceTo(GlobalPos.FromLv95(se, sn, 0)) / 1000:F1} km as the crow flies") : ", along the road")}, "
            + $"car {_car}, legs of {_legM:F0} m, origin {(_origin.Frame.IsAnchor ? "an anchor frame" : "FLAT")}");
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
        if (Now > TimeoutS) { Fault($"timed out after {_driven / 1000:F1} km, leg {_legs}"); Finish(); return; }
        switch (_phase)
        {
            case 0: FirstLeg(); break;
            case 1: Mount(); break;
            case 2: Drive((float)delta); break;
        }
    }

    /// <summary>The next leg from <paramref name="at"/>: toward the point <see cref="_legM"/> on toward the destination, or the whole road.</summary>
    private Task<RaceRoute?> BuildLeg(Vector3 at)
    {
        var source = _ground.Content;
        if (source == null) return Task.FromResult<RaceRoute?>(null);
        Vector3? toward = null;
        if (_to is { } to)
        {
            var here = _origin.ToGlobal(at);
            double left = here.HorizontalDistanceTo(to);
            var (east, north, _) = here.OffsetTo(to);
            double k = Math.Min(1.0, _legM / Math.Max(left, 1.0));
            toward = _origin.ToWorld(here.Moved(east * k, north * k));
        }
        var frame = _origin.Frame;
        return Task.Run(() => RaceRoute.BuildAsync(source, frame, at, toward: toward, smallest: RoadClass.Road));
    }

    private void FirstLeg()
    {
        var at = _camera.GlobalPosition with { Y = 0 };
        if (!_ground.HasCollisionAt(at) || !_ground.TryGetHeight(at, out float h)) return;
        _camera.GlobalPosition = at with { Y = h + 20f };
        _next ??= BuildLeg(at with { Y = h });
        if (!_next.IsCompleted) return;
        var route = _next.IsCompletedSuccessfully ? _next.Result : null;
        _next = null;
        if (route == null) { Fault("no road near the start"); Finish(); return; }
        route.Follow(_origin.Frame);
        var line = route.Line;
        var p = line.PointAt(10f);
        var fwd = RaceRoute.Flat(line.PointAt(12f) - line.PointAt(8f)).Normalized();
        float g = _ground.TryGetHeight(p, out float gh) ? gh : p.Y;
        _player = new FootPlayer { Name = "Driver", Terrain = _ground };
        AddChild(_player);
        _player.GlobalPosition = p with { Y = g + 1.2f };
        _player.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
        _pending = route;
        _phase = 1;
        Log($"leg 1: {route.Length:F0} m ({route.Class}), built at {Now:F1} s");
    }

    private RaceRoute? _pending;

    private void Mount()
    {
        if (_player is not { } p || !p.IsOnFloor()) return;
        var spec = CarCatalog.All[Mathf.Clamp(_car, 0, CarCatalog.All.Count - 1)];
        if (!p.SetRide(spec.Kind)) { Fault($"could not get into car {_car}"); Finish(); return; }
        TakeLeg(_pending!);
        _pending = null;
        _watch.Watch("car", () => _player);
        _watch.Watch("camera", () => _camera);
        _watch.WatchGround(() => _player?.GlobalPosition);
        _last = _origin.ToGlobal(p.GlobalPosition);
        _phase = 2;
        Log($"driving {spec.Label}");
    }

    private void TakeLeg(RaceRoute route)
    {
        _legs++;
        route.Follow(_origin.Frame);
        var pilot = AutoPilot.For(route, _player!);
        if (pilot == null) { Fault("no pilot for the car"); Finish(); return; }
        pilot.Temperament(0.9f, 0f, 1);
        pilot.Go = true;
        _pilot = pilot;
        _bestArc = 0;
        _player!.RideControls = () => pilot.Drive((float)GetPhysicsProcessDeltaTime(), true, Array.Empty<AutoPilot.Other>());
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
        _top = Mathf.Max(_top, speed);
        Chase(p);

        if (p.Ride == RideKind.OnFoot) { Fault($"out of the car at {_driven / 1000:F2} km ({LvAt(p)})"); Finish(); return; }
        // a fall: under the ground's height (through a patch, off the data's edge)
        if (_ground.TryGetHeight(p.GlobalPosition, out float g) && p.GlobalPosition.Y < g - 1f)
        {
            if ((_belowS += dt) > 0.5) { Fault($"fell under the ground ({g - p.GlobalPosition.Y:F1} m) at {_driven / 1000:F2} km ({LvAt(p)})"); Finish(); return; }
        }
        else _belowS = 0;

        // progress: toward the destination, or along the leg's road
        double toGo = _to is { } to ? here.HorizontalDistanceTo(to) : pilot.Route.Length - pilot.Arc;
        bool won = _to != null ? toGo < _bestToGo - StallM : pilot.Arc > _bestArc + StallM;
        if (won || _stallT == 0) { _stallT = _t; _bestToGo = Math.Min(_bestToGo, toGo); _bestArc = Math.Max(_bestArc, pilot.Arc); }
        if (_t - _stallT > StallS) { Fault($"stalled: {StallS:F0} s without {StallM:F0} m won, at {_driven / 1000:F2} km ({LvAt(p)}), {speed * 3.6f:F0} km/h"); Finish(); return; }

        _report += dt;
        if (_report >= 10)
        {
            _report = 0;
            Log(Inv($"t={_t,5:F0} s {_driven / 1000,6:F2} km {speed * 3.6f,4:F0} km/h leg {_legs} at {pilot.Arc:F0}/{pilot.Route.Length:F0} m, {toGo / 1000:F2} km to go, {_watch.Shifts} re-anchors, {LvAt(p)}"));
        }

        if (_to != null && toGo < ArriveM) { Log(Inv($"arrived: {toGo:F0} m from the destination")); Finish(); return; }
        float left = pilot.Route.Length - pilot.Arc;
        // the next leg, built from a little ahead of the car, before this one runs out
        if (_to != null && _next == null && left < BuildAheadM)
            _next = BuildLeg(p.GlobalPosition + RaceRoute.Flat(p.WorldVelocity).Normalized() * Mathf.Max(20f, speed));
        if (_next is { IsCompleted: true } task)
        {
            _next = null;
            var route = task.IsCompletedSuccessfully ? task.Result : null;
            if (route == null || route.Length < 200f)
            {
                if (left < 60f) { Fault($"no road on toward the destination from {LvAt(p)} ({toGo / 1000:F1} km to go)"); Finish(); return; }
            }
            else
            {
                TakeLeg(route);
                Log($"leg {_legs}: {route.Length:F0} m ({route.Class}) from {LvAt(p)}");
            }
        }
        if (_to == null && left < 60f) { Log(Inv($"the road's end after {_driven:F0} m")); Finish(); }
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

    private string LvAt(Node3D n) { var (e, nn) = _origin.ToLv95(n.GlobalPosition); return Inv($"LV95 {e:F0}/{nn:F0}"); }

    private void Finish()
    {
        if (_phase < 0) return;
        _phase = -1;
        if (_player != null) _player.RideControls = null;
        _watch.Judge();
        Log(Inv($"drove {_driven / 1000:F2} km in {_t:F0} s ({(_t > 0 ? _driven / _t * 3.6 : 0):F0} km/h on average, top {_top * 3.6f:F0}) over {_legs} legs"));
        Log(_watch.Summary().Replace("[longdrive] ", ""));
        foreach (var f in _watch.Faults) Fault(f);
        Log(_faults.Count == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_faults.Count}: {_faults[0]})");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
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
