using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The spectator from the Earth to the Moon, and onto it (#1850, H7d of docs/plans/planets.md section 13):
/// <c>--mooncheck</c>, headless, under <c>--frame earth</c>. The spectator camera starts 400 km over the point under the
/// Moon and flies straight for the point 100 km over the Moon on its way, at the spectator's own speed rule
/// (<see cref="Player.SpectatorSpeed.SurfaceCap"/>: half the distance to the nearest surface of any body, a second), so the
/// frame shifts, turns and, at 66,000 km from the Moon, is counted from the Moon (<see cref="WorldOrigin.Rebase"/>).
///
/// <para>
/// With the Moon's data (<c>--bodies DIR</c>) it goes on: down to 150 m over the ground there, T into the body once the
/// near ground (<see cref="BodyGround"/>) is under it, standing on it, sprinting 120 m (the ground moves under the walker
/// at least once), and a jump that must reach about six times the Earth's (<c>JumpVelocity</c>² / 2g at the Moon's
/// 1.63 m/s²: 5.4 m against 0.9).
/// </para>
///
/// <para>
/// It fails on: no crossing, or more than one; a shift on the crossing's frame (a crossing moves nothing); the camera's
/// place, counted from the anchor's body, jumping more than its own motion and a metre between two frames (past 1,000 km
/// up the origin follows the camera in 3D, <see cref="OriginShifter.DeepM"/>, or the float alone is a metre off); arriving
/// counted from any body but the Moon; taking more than <see cref="MaxSeconds"/> of game time to arrive; on the ground,
/// the walker not standing within a metre and a half of the ground's height, sinking a metre under it, not walking
/// 120 m in a minute, no new ground on the way, or a jump off by more than a quarter. A windowed run saves pictures
/// (<c>test_output/moon/arrival.png</c>, <c>ground.png</c>, <c>apex.png</c>).
/// </para>
/// </summary>
public partial class MoonCheck : Node
{
    public static bool Requested => CmdArgs.Has("--mooncheck");

    /// <summary>The trip must take less than this, game seconds (the plan: about a minute).</summary>
    public const double MaxSeconds = 120;

    /// <summary>How far the walker sprints, metres: past <see cref="BodyGround.MoveM"/>, so a new square comes under it.</summary>
    public const double WalkM = 120;

    private enum Stage { Wait, Fly, Descend, Ground, StepIn, Stand, Walk, Settle, Jump, Done }

    private readonly WorldOrigin _origin;
    private readonly System.Func<Player.FootPlayer?> _local;
    private readonly System.Func<BodyGround?> _ground;
    private Camera3D? _cam;
    private Stage _stage;
    private int _crossings, _crossedShifts = -1, _shiftsAtCross, _builtAtWalk;
    private double _t, _flying, _worstJump, _stageT, _lowest = double.MaxValue, _apex, _jumpFrom;
    private (BodyId Body, DVec3 P, SkyMoment Sky, double Step)? _last;
    private DVec3 _target, _walkFrom;
    private bool _left;
    private string? _fail;

    public MoonCheck(WorldOrigin origin, System.Func<Player.FootPlayer?> local, System.Func<BodyGround?> ground)
    {
        _origin = origin;
        _local = local;
        _ground = ground;
        Name = "MoonCheck";
        _origin.BodyChanged += (from, to) =>
        {
            _crossings++;
            _shiftsAtCross = OriginShifter.Instance?.ShiftCount ?? 0;
            GD.Print($"[mooncheck] crossed from {from} to {to} after {_flying:F1} s");
        };
    }

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[mooncheck] {what}"));

    private void Next(Stage stage)
    {
        _stage = stage;
        _stageT = 0;
    }

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        _stageT += delta;
        switch (_stage)
        {
            case Stage.Wait when _t > 3:
                Start();
                break;
            case Stage.Fly:
                Fly(delta);
                break;
            case Stage.Descend:
                Descend(delta);
                break;
            case Stage.Ground:
                AwaitGround();
                break;
            case Stage.StepIn:
                // T let go the frame after
                Key(Godot.Key.T, false);
                Next(Stage.Stand);
                break;
            case Stage.Stand:
                Stand();
                break;
            case Stage.Walk:
                Walk();
                break;
            case Stage.Settle when _stageT > 1.5:
                _jumpFrom = Over(_local()!);
                _apex = _jumpFrom;
                Input.ActionPress(PlayerInput.Jump);
                Next(Stage.Jump);
                break;
            case Stage.Jump:
                Jump();
                break;
        }
    }

    private void Start()
    {
        _cam = GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as Camera3D;
        if (_cam == null || OriginShifter.Instance is not { } shifter) { Finish("no spectator camera or shifter"); return; }
        // 400 km over the point right under the Moon now: the way out is straight up, never through the Earth
        var moon = Frames.Convert(BodyId.Moon, DVec3.Zero, BodyId.Earth, SkyClock.Now);
        var under = Ellipsoid.Wgs84.ToGeodetic(moon.AsEcef);
        var at = Ellipsoid.Wgs84.ToEcef(under with { H = 400_000 });
        Log($"the Moon is over {under.LatDeg:F1}, {under.LonDeg:F1}");
        shifter.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(at));
        _cam.MakeCurrent();
        _cam.GlobalPosition = Vector3.Zero;
        Next(Stage.Fly);
        Log("off from 400 km up, the Moon overhead");
    }

    /// <summary>The camera's place, body-fixed in the anchor's body.</summary>
    private DVec3 Here(FrameAxes axes, Vector3 world)
    {
        var e = axes.ToEcef(world.X, world.Y, world.Z);
        return new DVec3(e.X, e.Y, e.Z);
    }

    /// <summary>A body-fixed direction in world axes.</summary>
    private static Vector3 World(FrameAxes axes, DVec3 d) => new((float)FrameAxes.Dot(d.AsEcef, axes.X),
        (float)FrameAxes.Dot(d.AsEcef, axes.Y), (float)FrameAxes.Dot(d.AsEcef, axes.Z));

    private void Fly(double delta)
    {
        if (_cam == null || _origin.Frame.Axes is not { } axes) { Finish("no anchor frame"); return; }
        _flying += delta;
        var sky = SkyClock.Now;
        var p = Here(axes, _cam.GlobalPosition);

        if (_crossings > 0 && _crossedShifts < 0)
        {
            _crossedShifts = (OriginShifter.Instance?.ShiftCount ?? 0) - _shiftsAtCross;
        }

        // where it heads: 100 km over the Moon on the line from its centre to the camera
        var moonHere = Frames.Convert(BodyId.Moon, DVec3.Zero, axes.Body, sky);
        var away = (p - moonHere).Normalized();
        var target = moonHere + away * (CelestialBody.Moon.Shape.A + 100_000);
        var toTarget = target - p;
        double left = toTarget.Length;
        var (nearest, distance) = Frames.NearestSurface(axes.Body, p, sky);
        double speed = Player.SpectatorSpeed.SurfaceCap(distance);
        double step = Math.Min(speed * delta, left);
        // continuity in the anchor body's own frame: last frame's place, counted from this frame's body at last frame's
        // moment (across the crossing: the Earth's count of it into the Moon's), against this one. Only the camera's own
        // step may lie between them, and a metre
        if (_last is { } last)
        {
            var before = Frames.Convert(last.Body, last.P, axes.Body, last.Sky);
            double moved = (p - before).Length;
            double allowed = last.Step + 1;
            _worstJump = Math.Max(_worstJump, moved - allowed);
            if (moved > allowed) _fail ??= string.Create(CultureInfo.InvariantCulture,
                $"the camera's place jumped {moved:F1} m in a frame ({allowed:F1} m its own step) at {_flying:F1} s, counted from {axes.Body}");
        }
        _last = (axes.Body, p, sky, step);
        if ((int)(_flying / 5) != (int)((_flying - delta) / 5))
            Log($"{_flying:F0} s: {left / 1000:F0} km to go, {distance / 1000:F0} km over {nearest}, {speed / 1000:F0} km/s, step {step / 1000:F1} km, camera at {_cam.GlobalPosition}, counted from {axes.Body}");

        if (left < 1000)
        {
            Arrive(axes.Body, nearest, distance);
            return;
        }
        _cam.GlobalPosition += World(axes, toTarget / left) * (float)step;
        if (_flying > MaxSeconds) Finish(string.Create(CultureInfo.InvariantCulture,
            $"still {left / 1000:F0} km from lunar orbit after {_flying:F0} s"));
    }

    private void Arrive(BodyId body, BodyId nearest, double distance)
    {
        Log($"arrived after {_flying:F1} s: counted from {body}, {distance / 1000:F0} km over {nearest}, {_crossings} crossing(s), {_crossedShifts} shift(s) on the crossing's frame, the worst frame {Math.Max(0, _worstJump):F1} m past its own motion");
        if (_fail != null) { Finish(_fail); return; }
        if (_crossings != 1) { Finish($"{_crossings} crossings (one wanted)"); return; }
        if (body != BodyId.Moon) { Finish($"counted from {body} at the Moon"); return; }
        if (_crossedShifts > 0) { Finish($"{_crossedShifts} shift(s) came with the crossing"); return; }
        Shot("arrival");
        if (_ground() is not { } ground || _cam == null || _origin.Frame.Axes is not { } axes)
        {
            Log("no ground data (--bodies): the trip alone");
            Finish(null);
            return;
        }
        // 150 m over the ground under where it arrived
        var n = ground.Heights.NormalUnder(Here(axes, _cam.GlobalPosition).AsEcef);
        var g = ground.Heights.Ground(n);
        _target = new DVec3(g.X + n.X * 150, g.Y + n.Y * 150, g.Z + n.Z * 150);
        var geo = CelestialBody.Moon.Shape.ToGeodetic(g);
        Log($"down to 150 m over the ground at {geo.LatDeg:F2}, {geo.LonDeg:F2} ({geo.H:F0} m)");
        Next(Stage.Descend);
    }

    private void Descend(double delta)
    {
        if (_cam == null || _origin.Frame.Axes is not { } axes) { Finish("no anchor frame"); return; }
        var to = _target - Here(axes, _cam.GlobalPosition);
        double left = to.Length;
        if (left < 1)
        {
            Log($"150 m over the ground after {_stageT:F1} s; the near ground: {(_ground()?.Built ?? 0)} square(s)");
            Next(Stage.Ground);
            return;
        }
        double step = Math.Min(Player.SpectatorSpeed.SurfaceCap(left + 150) * delta, left);
        _cam.GlobalPosition += World(axes, to / left) * (float)step;
        if (_stageT > 60) Finish($"still {left / 1000:F1} km over the landing after 60 s");
    }

    private void AwaitGround()
    {
        if (_cam == null || _origin.Frame.Axes is not { } axes || _ground() is not { } ground) { Finish("no ground"); return; }
        if (ground.Frame is { } f)
        {
            var (x, _, z) = f.ToLocal(Here(axes, _cam.GlobalPosition).AsEcef);
            if (x * x + z * z < BodyGround.MoveM * BodyGround.MoveM)
            {
                Log($"the ground is under the camera after {_stageT:F1} s: T");
                Key(Godot.Key.T, true);
                Next(Stage.StepIn);
                return;
            }
        }
        if (_stageT > 20) Finish("no near ground under the camera after 20 s");
    }

    /// <summary>The walker's height over the ground's height under it, metres.</summary>
    private double Over(Player.FootPlayer me)
    {
        var ground = _ground()!;
        var p = Here(_origin.Frame.Axes!, me.GlobalPosition).AsEcef;
        var n = ground.Heights.NormalUnder(p);
        return CelestialBody.Moon.Shape.ToGeodetic(p).H - ground.Heights.At(n);
    }

    private void Stand()
    {
        var me = _local();
        if (me != null && (int)_stageT != (int)(_stageT - 1.0 / 60))
            Log($"{_stageT:F0} s after T: {Over(me):F2} m over the ground's height, at {me.GlobalPosition}, velocity {me.Velocity}, on floor {me.IsOnFloor()}, processing {me.CanProcess()}, visible {me.Visible}");
        if (me != null && me.Camera.Current && me.IsOnFloor() && _stageT > 1)
        {
            double over = Over(me);
            Log($"standing on the Moon after {_stageT:F1} s, {over:F2} m over the ground's height, gravity {BodyGravity.Ground:F2} m/s²");
            if (Math.Abs(over) > 1.5) { Finish(string.Create(CultureInfo.InvariantCulture, $"standing {over:F2} m off the ground's height")); return; }
            Shot("ground");
            _walkFrom = Here(_origin.Frame.Axes!, me.GlobalPosition);
            _builtAtWalk = _ground()!.Built;
            Input.ActionPress(PlayerInput.Sprint);
            Input.ActionPress(PlayerInput.MoveForward);
            Next(Stage.Walk);
            return;
        }
        if (_stageT > 15) Finish($"not standing 15 s after T (body {(me == null ? "none" : $"current {me.Camera.Current}, on floor {me.IsOnFloor()}")})");
    }

    private void Walk()
    {
        var me = _local()!;
        double over = Over(me);
        _lowest = Math.Min(_lowest, over);
        if (over < -1) { Release(); Finish(string.Create(CultureInfo.InvariantCulture, $"sank {-over:F1} m under the ground walking")); return; }
        double walked = (Here(_origin.Frame.Axes!, me.GlobalPosition) - _walkFrom).Length;
        if (walked >= WalkM)
        {
            Release();
            int squares = _ground()!.Built - _builtAtWalk;
            Log($"walked {walked:F0} m in {_stageT:F1} s, at worst {_lowest:F2} m over the ground's height, {squares} new square(s) on the way");
            if (squares < 1) { Finish("no new ground came under the walker"); return; }
            Next(Stage.Settle);
            return;
        }
        if (_stageT > 60) { Release(); Finish(string.Create(CultureInfo.InvariantCulture, $"walked only {walked:F0} m in 60 s")); }
    }

    private void Jump()
    {
        var me = _local()!;
        if (_stageT > 0.1 && Input.IsActionPressed(PlayerInput.Jump)) Input.ActionRelease(PlayerInput.Jump);
        double over = Over(me);
        _apex = Math.Max(_apex, over);
        if (!_left && _apex - _jumpFrom > 2) { _left = true; }
        if (_left && over - _jumpFrom > _apex - _jumpFrom - 0.05 && _apex - _jumpFrom > 4) Shot("apex");
        if (_stageT > 0.5 && me.IsOnFloor())
        {
            double rise = _apex - _jumpFrom;
            double want = me.JumpVelocity * me.JumpVelocity / (2 * BodyGravity.Ground);
            Log($"jumped {rise:F2} m (the Moon's {want:F2} m, the Earth's {me.JumpVelocity * me.JumpVelocity / (2 * CelestialBody.Earth.GroundGravity):F2} m), down after {_stageT:F1} s");
            Finish(Math.Abs(rise - want) <= 0.25 * want ? null : string.Create(CultureInfo.InvariantCulture, $"the jump rose {rise:F2} m, not {want:F2} m"));
            return;
        }
        if (_stageT > 15) Finish("still in the air 15 s after the jump");
    }

    private static void Release()
    {
        Input.ActionRelease(PlayerInput.MoveForward);
        Input.ActionRelease(PlayerInput.Sprint);
    }

    private static void Key(Key key, bool down) => Input.ParseInputEvent(new InputEventKey
    {
        Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = key, Keycode = key, KeyLabel = key, Pressed = down,
    });

    private readonly System.Collections.Generic.HashSet<string> _shots = new();

    /// <summary>A windowed run's picture, once a name.</summary>
    private void Shot(string name)
    {
        if (DisplayServer.GetName() == "headless" || !_shots.Add(name)) return;
        var path = ProjectSettings.GlobalizePath($"res://test_output/moon/{name}.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"saved {path}");
    }

    private void Finish(string? fail)
    {
        _stage = Stage.Done;
        if (fail == null) GD.Print("[mooncheck] RESULT: ok");
        else GD.PrintErr($"[mooncheck] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
