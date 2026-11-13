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
/// <c>godot --path . -- --frame earth --reanchorcamcheck --chunks fixture:straight --traffic 0</c> (#1763): the local
/// player's own camera across the earth world's ground re-anchor, on foot and in a car. Each re-anchor there turns the
/// world 0.027° and lowers it ~0.7 m at the camera (3 km out on the old anchor's tangent plane); a camera state eased in
/// world space and not carried through the shift (the on-foot pivot height) jumps up by it and eases back down.
///
/// <para>
/// To meet full-size re-anchors on a 3 km course in a few seconds, the check first moves the anchor itself
/// (<see cref="OriginShifter.ShiftTo"/>) <see cref="BehindM"/> short of the threshold behind the player along the road:
/// that shift is a full-size turn already, and walking (then driving) on crosses the threshold for a real one. Every
/// frame (after the player's camera has been placed) the camera's offset from the body, in the body's own axes, is
/// sampled; a re-anchor in the interval may not step it by more than <see cref="StepTolM"/> past the 95th percentile of
/// the steps elsewhere. The body and the camera are also watched in Earth positions (<see cref="ReanchorWatch"/>: a body
/// popped by ground collision a frame late would show there). No <c>--originshift</c>: a smaller threshold makes the
/// drop too small to see (it grows with the square of the distance). RESULT ok or FAILED.
/// </para>
/// </summary>
public partial class ReanchorCamCheck : Node
{
    public static bool Requested() => CmdArgs.Has("--reanchorcamcheck");

    /// <summary>A step of the camera's offset from the body at a re-anchor past this (plus the noise) fails.</summary>
    public const float StepTolM = 0.05f;
    /// <summary>The anchor is put this far short of the threshold behind the player: what is walked (driven) to cross it.</summary>
    private const float WalkBehindM = 30f, DriveBehindM = 80f;
    private float BehindM => _phase <= 3 ? WalkBehindM : DriveBehindM;
    // wall clock: the leaves stream on threads, and a loaded machine read them in 53 s
    private const double TimeoutS = 540;   // under the test runner's 600 s, so it reports first

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private Camera3D _start = null!;
    private ReanchorWatch _watch = null!;
    private Task<RaceRoute?>? _routeTask;
    private RaceRoute? _route;
    private FootPlayer? _player;
    private AutoPilot? _pilot;
    private Vector3 _fwd;
    private int _phase, _shifts, _phaseShifts;
    private double _t, _phaseT, _settle, _said;
    private ulong _startMs;

    // the camera's offset from the body, last frame's, and every step with no re-anchor in it
    private Vector3? _lastRel;
    private bool _shiftSinceSample;
    private readonly List<float> _steps = new();
    private readonly List<(string Mode, int Shift, float Step, Vector3 Delta)> _atShift = new();

    public ReanchorCamCheck(IWorldGround ground, WorldOrigin origin)
    {
        _ground = ground;
        _origin = origin;
        Name = "ReanchorCamCheck";
        // after the shifter (int.MinValue) and the player's _Process, which places its camera
        ProcessPriority = int.MaxValue;
    }

    private double Now => (Time.GetTicksMsec() - _startMs) / 1000.0;

    public override void _Ready()
    {
        _startMs = Time.GetTicksMsec();
        AddChild(_start = new Camera3D { Name = "Start", Far = 30_000, Current = true });
        var (se, sn) = SpawnPoint.ParseTarget();
        _start.GlobalPosition = _origin.ToWorld(se, sn, 0) + Vector3.Up * 20f;
        _ground.AddAnchor(_start, collision: true);
        _watch = new ReanchorWatch(_origin, _ground, "reanchorcam") { JumpTolM = 0.25f };
        AddChild(_watch);
        _origin.Shifted += OnShifted;
        if (OriginShifter.ParseThreshold() != null || OriginShifter.ParseStress() != null)
            Log("WARNING: --originshift / --originstress make the re-anchor's drop too small to see");
        Log($"start LV95 {se:F0}/{sn:F0}, origin {(_origin.Frame.IsAnchor ? "an anchor frame" : "FLAT (this check wants --frame earth)")}");
    }

    public override void _ExitTree()
    {
        _origin.Shifted -= OnShifted;
        _ground.RemoveAnchor(_start);
    }

    private void OnShifted(OriginShift shift)
    {
        _shifts++;
        _shiftSinceSample = true;
        _fwd = shift.Direction(_fwd);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_phase < 0) return;
        float dt = (float)delta;
        _t += dt;
        _phaseT += dt;
        if (Now > TimeoutS) { Fault($"timed out in phase {_phase}"); Finish(); return; }
        switch (_phase)
        {
            case 0: FindRoad(); break;
            case 1: Spawn(); break;
            case 2: Settle(dt, next: 3); break;
            case 3: Cross("on foot", next: 4); break;
            case 4: Mount(); break;
            case 5: Settle(dt, next: 6); break;
            case 6: Cross("in the car", next: 7); break;
            case 7: Finish(); break;
        }
    }

    private void FindRoad()
    {
        var at = _start.GlobalPosition with { Y = 0 };
        if (_phaseT > _said + 15) { _said = _phaseT; Log(Inv($"waiting {_phaseT:F0} s for the road: collision {_ground.HasCollisionAt(at)}, route {(_routeTask == null ? "not asked" : _routeTask.Status.ToString())}")); }
        if (!_ground.HasCollisionAt(at) || !_ground.TryGetHeight(at, out float h)) return;
        _start.GlobalPosition = at with { Y = h + 20f };
        if (_ground.Content is not { } source) return;
        _routeTask ??= Task.Run(() => RaceRoute.BuildAsync(source, _origin.Frame, at with { Y = h }, smallest: RoadClass.Road));
        if (!_routeTask.IsCompleted) return;
        _route = _routeTask.IsCompletedSuccessfully ? _routeTask.Result : null;
        if (_route == null || _route.Length < 400f) { Fault("no road of 400 m near the start"); Finish(); return; }
        _phase = 1;
        Log(Inv($"road: {_route.Length:F0} m"));
    }

    private void Spawn()
    {
        var route = _route!;
        route.Follow(_origin.Frame);
        var line = route.Line;
        var p = line.PointAt(10f);
        _fwd = RaceRoute.Flat(line.PointAt(14f) - line.PointAt(6f)).Normalized();
        float g = _ground.TryGetHeight(p, out float gh) ? gh : p.Y;
        _player = new FootPlayer { Name = "Walker", Terrain = _ground };
        AddChild(_player);
        _player.GlobalPosition = p with { Y = g + 1.2f };
        _player.Rotation = new Vector3(0, Mathf.Atan2(-_fwd.X, -_fwd.Z), 0);
        _watch.Watch("body", () => _player);
        _watch.Watch("camera", () => _player?.CameraOrNull);
        _phase = 2;
        _phaseT = 0;
    }

    /// <summary>On the ground for a second, its camera current (the shifter follows it), then the anchor goes behind it.</summary>
    private void Settle(float dt, int next)
    {
        var p = _player!;
        if (p.CameraOrNull is { } own && !own.Current) own.MakeCurrent();
        _settle = p.IsOnFloor() ? _settle + dt : 0;
        if (_settle < 1.0) { if (_phaseT > 30) { Fault("the player never stood on the road"); Finish(); } return; }
        _settle = 0;
        // the anchor this far behind the camera along the road: the shifter re-anchors once it is crossed
        double threshold = OriginShifter.Instance is { } s && (OriginShifter.ParseThreshold() != null || OriginShifter.ParseStress() != null)
            ? s.ThresholdM : OriginShifter.GroundMode.ThresholdM;
        var cam = p.CameraOrNull ?? (Node3D)p;
        var behind = cam.GlobalPosition - _fwd * (float)(threshold - BehindM);
        var (e, n) = _origin.ToLv95(behind);
        int before = _shifts;
        if (OriginShifter.Instance?.ShiftTo(e, n) != true || _shifts == before) { Fault("the anchor could not be moved behind the player"); Finish(); return; }
        _phaseShifts = _shifts;
        Log(Inv($"anchor moved {threshold - BehindM:F0} m behind the camera (shift {_shifts}); {(next == 3 ? "walking" : "driving")} on"));
        _phase = next;
        _phaseT = 0;
        if (next == 6) _pilot!.Go = true;
    }

    /// <summary>Walking (or driving) on until a re-anchor has come, then a second more.</summary>
    private void Cross(string mode, int next)
    {
        var p = _player!;
        if (_shifts > _phaseShifts)
        {
            if ((_settle += GetPhysicsProcessDeltaTime()) > 1.0)
            {
                _settle = 0;
                Log(Inv($"{mode}: crossed a re-anchor (shift {_shifts}) after {_phaseT:F1} s"));
                if (next == 4) p.WalkControls = null;
                _phase = next;
                _phaseT = 0;
            }
        }
        else if (_phaseT > 60) { Fault($"{mode}: no re-anchor within 60 s"); Finish(); }
    }

    private void Mount()
    {
        var p = _player!;
        if (!p.IsOnFloor()) return;
        var spec = CarCatalog.All[Mathf.Clamp(4, 0, CarCatalog.All.Count - 1)];
        if (!p.SetRide(spec.Kind)) { Fault("could not get into the car"); Finish(); return; }
        _route!.Follow(_origin.Frame);
        _pilot = AutoPilot.For(_route, p);
        if (_pilot == null) { Fault("no pilot for the car"); Finish(); return; }
        _pilot.Temperament(0.9f, 0f, 1);
        _pilot.MaxSpeed = 20f;
        _pilot.Go = false;
        var pilot = _pilot;
        p.RideControls = () => pilot.Drive((float)GetPhysicsProcessDeltaTime(), true, Array.Empty<AutoPilot.Other>());
        // the next sample starts a new series: getting in moves the camera by metres
        _lastRel = null;
        _phase = 5;
        _phaseT = 0;
        Log($"in the {spec.Label}");
    }

    public override void _Process(double delta)
    {
        if (_player is not { } p || p.CameraOrNull is not { } cam) return;
        // walking on along the road (its direction, turned with each shift)
        if (_phase == 3) p.WalkControls ??= () => (_fwd, false);
        if (_phase is not (2 or 3 or 5 or 6)) return;
        var body = p.GlobalTransform;
        var rel = body.Basis.Inverse() * (cam.GlobalPosition - body.Origin);
        bool shifted = _shiftSinceSample;
        _shiftSinceSample = false;
        if (_lastRel is { } last)
        {
            var d = rel - last;
            float step = d.Length();
            if (shifted) _atShift.Add((_phase <= 3 ? "on foot" : "in the car", _shifts, step, d));
            else _steps.Add(step);
        }
        _lastRel = rel;
    }

    private void Finish()
    {
        if (_phase < 0) return;
        _phase = -1;
        if (_player != null) { _player.RideControls = null; _player.WalkControls = null; }
        _watch.Judge();
        _steps.Sort();
        float noise = _steps.Count == 0 ? 0 : _steps[(int)(0.95 * (_steps.Count - 1))];
        Log(Inv($"the camera's offset from the body: {_steps.Count} steps with no re-anchor, 95th percentile {noise * 100:F1} cm, worst {(_steps.Count > 0 ? _steps[^1] * 100 : 0):F1} cm"));
        foreach (var (mode, shift, step, d) in _atShift)
        {
            Log(Inv($"re-anchor {shift} {mode}: the camera's offset from the body stepped {step * 100:F1} cm ({d.X * 100:F1}, {d.Y * 100:F1}, {d.Z * 100:F1}) cm"));
            if (step > StepTolM + noise) Fault(Inv($"{mode}: the camera stepped {step * 100:F1} cm from the body at re-anchor {shift} (tolerance {(StepTolM + noise) * 100:F1} cm)"));
        }
        bool foot = _atShift.Exists(a => a.Mode == "on foot"), car = _atShift.Exists(a => a.Mode == "in the car");
        if (!foot) Fault("no re-anchor sampled on foot");
        if (!car) Fault("no re-anchor sampled in the car");
        Log(_watch.Summary().Replace("[reanchorcam] ", ""));
        foreach (var f in _watch.Faults) Fault(f);
        Log(_faults.Count == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_faults.Count}: {_faults[0]})");
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }

    private void Fault(string f)
    {
        _faults.Add(f);
        Log($"FAULT: {f}");
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    private static void Log(string s) => GD.Print("[reanchorcamcheck] " + s);
}
