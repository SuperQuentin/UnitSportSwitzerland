using System;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--shuttlemooncheck [--fromorbit]</c> (#1851, C3a of docs/plans/planets.md), under <c>--frame earth --chunks
/// fixture:straight</c>: the shuttle to the Moon and back, on the pilot's own controls.
/// <list type="number">
/// <item>From a 400 km Earth orbit a tap of the target action (]) picks the Moon; held, it turns the nose onto the tangent
/// of the Moon's 100 km arrival orbit (<see cref="ShuttleFlight.TangentAim"/>); the cruise drive at full lever carries it
/// there (coasting first until the Moon has risen over the Earth), across the Earth's sphere of influence into the Moon's
/// without a jump of its place; switched off where it passes 100 km, the drive leaves it in a circular lunar orbit, which
/// closes within 10 m after one turn (at 8x).</item>
/// <item>With the Moon's ground (<c>--bodies DIR</c>): the drive down at 40° and a slow lever until it drops out at the
/// Space exit (8 km); the belly jets bring it down on its gear (under 2 m/s, upright); the pilot steps out, walks 50 m,
/// jumps (the Moon's height for the walker's push), and gets back in; the belly jets lift it into Space, the drive takes it
/// up to 100 km and off into a lunar orbit; the Earth is targeted, and the drive home leaves it in an Earth orbit.</item>
/// </list>
/// <c>--fromorbit</c> starts in the 100 km lunar orbit (the shuttle boarded on the Earth, the world moved to the Moon).
/// Fails on a frame where the craft's place jumps past its own step, a contact in free flight, an orbit not round the
/// body or not near its height, a hard or tilted touchdown, a short walk or jump, a stage too long. Windowed, pictures on
/// the way (<c>test_output/shuttlemoon/</c>).
/// </summary>
public partial class ShuttleMoonCheck : Node
{
    public static bool Requested => CmdArgs.Has("--shuttlemooncheck");

    private static bool FromOrbit => CmdArgs.Has("--fromorbit");

    private enum Stage
    {
        Wait, Board, Orbit, Pick, Align, Drive, Coast, Descend, Lander, Out, Walk, Settle, Jump, BoardAgain, Climb, Circle,
        PickHome, AlignHome, Home, Judging, Done,
    }

    private readonly WorldOrigin _origin;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<BodyGround?> _ground;
    private Stage _stage;
    private double _t, _stageT, _report;
    private Airliner? _jet;
    private (BodyId Body, DVec3 P, SkyMoment Sky, double Step)? _last;
    private double _worstJump, _closest = double.MaxValue, _period, _t0, _apex, _jumpFrom;
    private int _crossings;
    private OrbitState _start;
    private BodyId _bodyWas = BodyId.Earth;
    private DVec3 _exitAt, _walkFrom;
    private Vector2 _stick;
    private bool _left, _upDone, _atHatch;

    /// <summary>The walk on the Moon, metres.</summary>
    private const double WalkM = 50;

    /// <summary>The coast round a body waiting for the target to rise, faster than a player may (the orbit's substeps hold it).</summary>
    private const double CoastScale = 32;

    public ShuttleMoonCheck(WorldOrigin origin, Func<FootPlayer?> local, Func<BodyGround?> ground)
    {
        _origin = origin;
        _local = local;
        _ground = ground;
        Name = "ShuttleMoonCheck";
    }

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[shuttlemoon] {what}"));

    private void Next(Stage s)
    {
        Log($"{_stage} -> {s} at {_t:F1} s");
        _stage = s;
        _stageT = 0;
        _report = 0;
    }

    /// <summary>The stages on foot: the walker is out of the shuttle.</summary>
    private bool OnFoot => _stage is Stage.Out or Stage.Walk or Stage.Settle or Stage.Jump or Stage.BoardAgain;

    public override void _PhysicsProcess(double delta)
    {
        if (_stage == Stage.Done) return;
        _t += delta;
        _stageT += delta;
        var p = _local();
        if (_stage > Stage.Board && !OnFoot && (p == null || p.Vehicle != _jet)) { Finish($"out of the shuttle at {_stage}"); return; }
        if (_jet?.Space is { Contacts: > 0 } s && !OnFoot && _stage != Stage.Lander && _stage != Stage.Climb) { Finish($"{s.Contacts} contact(s) in free flight at {_stage}"); return; }
        if (p != null && p.Vehicle == _jet && p.VehicleHealth < 50f) { Finish($"damaged at {_stage}: health {p.VehicleHealth:F0}"); return; }
        if (_origin.Body != _bodyWas)
        {
            _crossings++;
            Log($"crossed from {_bodyWas} to {_origin.Body} at {_t:F1} s");
            _bodyWas = _origin.Body;
        }
        switch (_stage)
        {
            case Stage.Wait when _t > 3 && p == null:
                // the world starts with the spectator: T steps into a walker under it
                if (_stageT > 1)
                {
                    Tap(Godot.Key.T);
                    _stageT = 0;
                }
                if (_t > 30) Finish("no walker 30 s after T");
                break;
            case Stage.Wait when _t > 3 && p != null:
                // a ride is taken standing on the ground
                if (!p.IsOnFloor() || !p.SetRide(RideKind.Shuttle) || p.Vehicle is not Airliner { Spec.Spacecraft: true } jet)
                {
                    if (_t > 40) Finish("no shuttle 40 s in (the walker on the ground?)");
                    return;
                }
                _jet = jet;
                // the commander's hands: the stick the check's (centred unless it flies the belly jets), piloted
                p.FlyControls = () => new FlightInput(_stick, 0f, 0f, 0f, 0f, false, false, 0f);
                Next(Stage.Board);
                break;
            case Stage.Board when _stageT > 1:
                if (FromOrbit) { ToLunarOrbit(p!); return; }
                Log(p!.PutInOrbit(400));
                Next(Stage.Orbit);
                break;
            case Stage.Orbit when _stageT > 2:
                if (!_jet!.InSpace) { Finish("not in Space after /orbit 400"); return; }
                // a tap of the target action: the first target from the Earth is the Moon
                Tap(Godot.Key.Bracketright);
                Next(Stage.Pick);
                break;
            case Stage.Pick:
                if (SpectatorTargets.Instance?.Target == BodyId.Moon)
                {
                    Log("the Moon targeted by a tap");
                    Key(true);   // and held: the nose turns onto the arrival orbit's tangent
                    Next(Stage.Align);
                }
                else if (_stageT > 3) Finish($"the tap targeted {SpectatorTargets.Instance?.Target?.ToString() ?? "nothing"}, not the Moon");
                break;
            case Stage.Align:
                if (Aligned("the Moon")) Next(Stage.Drive);
                break;
            case Stage.Drive:
                if (Drive(delta, BodyId.Moon)) Next(Stage.Coast);
                break;
            case Stage.Coast:
                Coast();
                break;
            case Stage.Descend:
                Descend();
                break;
            case Stage.Lander:
                Land(delta);
                break;
            case Stage.Out when _stageT > 2:
                StepOut(p!);
                break;
            case Stage.Walk:
                Walk(p!);
                break;
            case Stage.Settle when _stageT > 1.5:
                _jumpFrom = Over(p!.GlobalPosition);
                _apex = _jumpFrom;
                Input.ActionPress(PlayerInput.Jump);
                Next(Stage.Jump);
                break;
            case Stage.Jump:
                Jump(p!);
                break;
            case Stage.BoardAgain:
                BoardAgain(p!);
                break;
            case Stage.Climb:
                Climb();
                break;
            case Stage.Circle:
                Circle();
                break;
            case Stage.PickHome:
                PickHome();
                break;
            case Stage.AlignHome:
                if (Aligned("the Earth")) Next(Stage.Home);
                break;
            case Stage.Home:
                if (Drive(delta, BodyId.Earth)) Judge();
                break;
        }
    }

    /// <summary>
    /// <c>--fromorbit</c>: the world moved to the Moon with the shuttle in it (the crossing's relabelling, then a shift to the
    /// tangent frame there), and <c>/orbit 100</c> round it.
    /// </summary>
    private void ToLunarOrbit(FootPlayer p)
    {
        if (OriginShifter.Instance is not { } shifter) { Finish("no origin shifter"); return; }
        var sky = SkyClock.Now;
        var moon = CelestialBody.Moon;
        // over the point 45° west of the one under the sun: in daylight
        var toSun = Frames.Convert(BodyId.Sun, DVec3.Zero, BodyId.Moon, sky);
        double lat = Math.Atan2(toSun.Z, Math.Sqrt(toSun.X * toSun.X + toSun.Y * toSun.Y)) * 180 / Math.PI;
        double lon = Math.Atan2(toSun.Y, toSun.X) * 180 / Math.PI - 45;
        var place = new Geodetic(lat, Math.IEEERemainder(lon, 360), 100_000);
        _origin.Rebase(BodyId.Moon, sky);
        shifter.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(moon.Shape.ToEcef(place with { H = 0 }), BodyId.Moon));
        p.GlobalPosition = new Vector3(0, 100_000, 0);
        Log($"from orbit: at {place.LatDeg:F1}, {place.LonDeg:F1} on the Moon; {p.PutInOrbit(100)}");
        _bodyWas = _origin.Body;
        _period = -1;   // no closing turn to fly: straight down
        Next(Stage.Coast);
    }

    /// <summary>The hold's aim reached (within 1°) and the way clear of the body it is counted from: the drive on.</summary>
    private bool Aligned(string what)
    {
        var jet = _jet!;
        if (jet.Aim is not { } aim)
        {
            if (_stageT > 2) Finish("the hold set no aim");
            return false;
        }
        double off = Mathf.RadToDeg((-jet.State.Attitude.Z).AngleTo(aim));
        if (off > 1)
        {
            if (_stageT > 30) Finish(string.Create(CultureInfo.InvariantCulture, $"still {off:F1}° off the aim after 30 s"));
            return false;
        }
        // the target below the horizon: the way out runs into the body it is round (the drive would slow to it and drop out
        // of Space). A pilot coasts round until it rises: so does the check, faster
        if (Blocked(aim))
        {
            if (Engine.TimeScale == 1) Log($"{what} is behind {_origin.Body}: coasting round until it rises");
            Engine.TimeScale = CoastScale;
            if (_stageT > 1500) Finish($"{what} did not rise in 25 min of coasting");
            return false;
        }
        Engine.TimeScale = 1;
        Log($"aligned on {what} after {_stageT:F1} s, the way clear: the drive on, full lever");
        jet.Command(AirlinerCommand.Drive);
        return true;
    }

    /// <summary>The line along <paramref name="aim"/> from the craft passes within 200 km of the surface of the body it is counted from.</summary>
    private bool Blocked(Vector3 aim)
    {
        if (_origin.Frame.Axes is not { } axes || _local() is not { } p) return false;
        var e = axes.ToEcef(p.GlobalPosition.X, p.GlobalPosition.Y, p.GlobalPosition.Z);
        var at = new DVec3(e.X, e.Y, e.Z);
        // the aim's body-fixed direction: the world axes X, Y, Z are body-fixed vectors
        var dir = new DVec3(axes.X.X * aim.X + axes.Y.X * aim.Y + axes.Z.X * aim.Z,
            axes.X.Y * aim.X + axes.Y.Y * aim.Y + axes.Z.Y * aim.Z,
            axes.X.Z * aim.X + axes.Y.Z * aim.Y + axes.Z.Z * aim.Z).Normalized();
        double t = -(at.X * dir.X + at.Y * dir.Y + at.Z * dir.Z);
        if (t <= 0) return false;
        var closest = at + dir * t;
        return closest.Length < CelestialBody.Of(axes.Body).Shape.A + 200_000;
    }

    /// <summary>The drive to <paramref name="target"/>'s arrival orbit; true once switched off there.</summary>
    private bool Drive(double delta, BodyId target)
    {
        var jet = _jet!;
        var p = _local()!;
        if (_stageT > 0.2 && !jet.DriveOn) { Finish($"the drive is off at {_stageT:F1} s"); return false; }
        if (jet.DriveOn) jet.State.Lever = 1f;
        // continuity: last frame's place counted from this frame's body at last frame's moment, against this one
        if (_origin.Frame.Axes is not { } axes) { Finish("no anchor frame"); return false; }
        var sky = SkyClock.Now;
        var e = axes.ToEcef(p.GlobalPosition.X, p.GlobalPosition.Y, p.GlobalPosition.Z);
        var here = new DVec3(e.X, e.Y, e.Z);
        double step = jet.State.Velocity.Length() * delta;
        if (_last is { } last)
        {
            var before = Frames.Convert(last.Body, last.P, axes.Body, last.Sky);
            double moved = (here - before).Length;
            double allowed = Math.Max(last.Step, step) * 1.05 + 1;
            _worstJump = Math.Max(_worstJump, moved - allowed);
            if (moved > allowed) { Finish(string.Create(CultureInfo.InvariantCulture, $"the craft's place jumped {moved / 1000:F1} km in a frame ({allowed / 1000:F1} km its own step), counted from {axes.Body}")); return false; }
        }
        _last = (axes.Body, here, sky, step);

        double d = jet.DriveDistance, arrival = ShuttleFlight.ArrivalM(CelestialBody.Of(target));
        if (_t >= _report)
        {
            _report = _t + 10;
            Log($"{_stageT:F0} s: {d / 1000:F0} km to the nearest surface, {jet.State.Velocity.Length() / 1000:F0} km/s, counted from {axes.Body}");
        }
        if (_stageT > 2 && _stageT < 2.1) Shot(target == BodyId.Moon ? "drive" : "drive_home");
        // the closest pass inside the target's sphere only: leaving the Moon at 101 km is no pass of the Earth
        if (_origin.Body == target) _closest = Math.Min(_closest, d);
        // where it passes the arrival height (or starts to climb away below twice it): the drive off
        bool there = _origin.Body == target && (d <= arrival * 1.02 || d < 2 * arrival && d > _closest + 50);
        if (!there)
        {
            if (_stageT > 900) Finish(string.Create(CultureInfo.InvariantCulture, $"still {d / 1000:F0} km out after 15 min in the drive"));
            return false;
        }
        Key(false);
        jet.Command(AirlinerCommand.Drive);
        Log($"the drive off {d / 1000:F1} km over {target} after {_stageT:F1} s, {_crossings} crossing(s) so far, the worst frame {Math.Max(0, _worstJump):F1} m past its own step");
        _last = null;
        _closest = double.MaxValue;
        return true;
    }

    private void Coast()
    {
        var jet = _jet!;
        var space = jet.Space!;
        var moon = CelestialBody.Moon;
        if (_period <= 0)
        {
            if (_stageT < 0.5) return;
            if (space.Orbit is not { } first) { Finish("no orbit after the drive off"); return; }
            if (space.Body.Id != BodyId.Moon) { Finish($"in orbit round {space.Body.Name}, not the Moon"); return; }
            var (peri, apo, a) = Orbit.Apsides(moon, first);
            double pe = (peri - moon.Shape.A) / 1000, ap = (apo - moon.Shape.A) / 1000;
            Log($"in lunar orbit: {pe:F1} x {ap:F1} km, {first.V.Length:F0} m/s");
            if (pe < 80 || ap > 120) { Finish(string.Create(CultureInfo.InvariantCulture, $"not a 100 km circular orbit: {pe:F1} x {ap:F1} km")); return; }
            Shot("lunar_orbit");
            if (_period < 0) { Down(); return; }
            _start = first;
            _period = Orbit.Period(moon, a);
            _t0 = space.Seconds;
            Engine.TimeScale = SimClock.MaxScale;
            return;
        }
        if (space.Orbit is not { } s) { Finish("fell out of the lunar orbit"); return; }
        double t = space.Seconds - _t0;
        if (t < _period) return;
        var end = Orbit.Step(moon, s, DVec3.Zero, _period - t);
        double miss = (end.R - _start.R).Length;
        Log($"one lunar orbit ({_period / 60:F1} min): {miss:F2} m from the start, contacts {space.Contacts}");
        Engine.TimeScale = 1;
        if (miss > 10) { Finish(string.Create(CultureInfo.InvariantCulture, $"the lunar orbit did not close: {miss:F1} m")); return; }
        Down();
    }

    /// <summary>Down to the ground, or done: the Moon's ground needs its data (<c>--bodies</c>).</summary>
    private void Down()
    {
        if (_ground() == null) { Log("no ground data (--bodies): the trip and the orbit alone"); Finish(null); return; }
        Next(Stage.Descend);
    }

    /// <summary>The nose 40° under the horizon along its heading, the drive on at a slow lever, until it drops out at the Space exit.</summary>
    private void Descend()
    {
        var jet = _jet!;
        var p = _local()!;
        var up = _origin.Frame.UpAt(p.GlobalPosition);
        var nose = -jet.State.Attitude.Z;
        var ahead = (nose - up * nose.Dot(up)).Normalized();
        jet.Aim = (ahead * Mathf.Cos(Mathf.DegToRad(40)) - up * Mathf.Sin(Mathf.DegToRad(40))).Normalized();
        if (!jet.DriveOn)
        {
            if (jet.Lander) { jet.Aim = null; Next(Stage.Lander); return; }
            if (Mathf.RadToDeg(nose.AngleTo(jet.Aim.Value)) < 1)
            {
                Log("the nose 40° down: the drive on at 15 %");
                jet.Command(AirlinerCommand.Drive);
            }
            else if (_stageT > 40) Finish("could not turn the nose down in 40 s");
            return;
        }
        jet.State.Lever = 0.15f;
        if (_t >= _report)
        {
            _report = _t + 10;
            Log($"down: {_origin.AltitudeOf(p.GlobalPosition) / 1000:F1} km, {jet.State.Velocity.Length():F0} m/s");
        }
        if (_stageT > 600) Finish("still in the drive 10 min after turning down");
    }

    /// <summary>The craft's height over the Moon's ground under it, metres (its centre).</summary>
    private double Over(Vector3 world)
    {
        if (_ground() is not { } ground || _origin.Frame.Axes is not { } axes) return double.NaN;
        var e = axes.ToEcef(world.X, world.Y, world.Z);
        var n = ground.Heights.NormalUnder(e);
        return CelestialBody.Moon.Shape.ToGeodetic(e).H - ground.Heights.At(n);
    }

    /// <summary>The belly jets down: a sink rate by the height, the slide stopped by the stick, the gear down below 3 km.</summary>
    private void Land(double delta)
    {
        var jet = _jet!;
        var p = _local()!;
        if (!jet.Lander) { Finish("off the belly jets on the way down"); return; }
        double h = Over(p.GlobalPosition);
        var up = _origin.Frame.UpAt(p.GlobalPosition);
        var v = jet.State.Velocity;
        float vs = v.Dot(up);
        var moon = CelestialBody.Moon;
        double r = moon.Shape.A + _origin.AltitudeOf(p.GlobalPosition);
        float g = (float)(moon.GM / (r * r));
        // a sink of a tenth of the height, at least 1.2 m/s, at most 80
        float want = -Mathf.Clamp((float)h / 10f, 1.2f, 80f);
        float accel = (want - vs) * 1.2f;
        jet.State.Lever = Mathf.Clamp((g + accel) / ShuttleFlight.BellyAccel, 0f, 1f);
        // the slide: the stick against it, in the craft's own axes
        var nose = -jet.State.Attitude.Z;
        var ahead = (nose - up * nose.Dot(up)).Normalized();
        var right = ahead.Cross(up).Normalized();
        _stick = new Vector2(Mathf.Clamp(-v.Dot(right) / 4f, -1f, 1f), Mathf.Clamp(v.Dot(ahead) / 4f, -1f, 1f));
        if (h < 3000 && !jet.State.GearDown) jet.Command(AirlinerCommand.Gear);
        if (_t >= _report)
        {
            _report = _t + 5;
            float across = (v - up * vs).Length();
            Log($"belly jets: {h:F0} m over the ground, V/S {vs:F1} m/s, slide {across:F1} m/s, lever {jet.State.Lever:F2}, gear {jet.State.Gear:F1}");
        }
        if (h < 300 && h > 200) Shot("descent");
        if (jet.State.OnGround)
        {
            _stick = Vector2.Zero;
            jet.State.Lever = 0f;
            double tilt = Mathf.RadToDeg(jet.State.Attitude.Y.Normalized().AngleTo(up));
            Log($"down on the Moon after {_stageT:F0} s on the jets: {jet.TouchSink:F2} m/s, {tilt:F1}° off upright, health {p.VehicleHealth:F0}");
            if (!(jet.TouchSink <= 2f)) { Finish(string.Create(CultureInfo.InvariantCulture, $"a hard touchdown: {jet.TouchSink:F2} m/s")); return; }
            if (tilt > 5) { Finish(string.Create(CultureInfo.InvariantCulture, $"down {tilt:F1}° off upright")); return; }
            Next(Stage.Out);
            return;
        }
        if (_stageT > 600) Finish(string.Create(CultureInfo.InvariantCulture, $"still {h:F0} m up after 10 min on the jets"));
    }

    private DVec3 Here(Vector3 world)
    {
        var e = _origin.Frame.Axes!.ToEcef(world.X, world.Y, world.Z);
        return new DVec3(e.X, e.Y, e.Z);
    }

    private void StepOut(FootPlayer p)
    {
        Shot("landed");
        p.ExitVehicle();
        p.FlyControls = null;
        Log("out of the shuttle");
        Next(Stage.Walk);
    }

    private void Walk(FootPlayer p)
    {
        if (p.Vehicle != null) { Finish("still aboard after stepping out"); return; }
        if (_exitAt == default)
        {
            if (_stageT < 1 || !p.IsOnFloor()) { if (_stageT > 30) Finish("not standing 30 s after stepping out"); return; }
            _exitAt = Here(p.GlobalPosition);
            _walkFrom = _exitAt;
            Input.ActionPress(PlayerInput.Sprint);
            Input.ActionPress(PlayerInput.MoveForward);
            return;
        }
        double walked = (Here(p.GlobalPosition) - _walkFrom).Length;
        if (walked >= WalkM)
        {
            Input.ActionRelease(PlayerInput.MoveForward);
            Input.ActionRelease(PlayerInput.Sprint);
            Log($"walked {walked:F0} m on the Moon in {_stageT:F1} s");
            Shot("walk");
            Next(Stage.Settle);
            return;
        }
        if (_stageT > 90) { Input.ActionRelease(PlayerInput.MoveForward); Input.ActionRelease(PlayerInput.Sprint); Finish(string.Create(CultureInfo.InvariantCulture, $"walked only {walked:F0} m in 90 s")); }
    }

    private void Jump(FootPlayer p)
    {
        if (_stageT > 0.1 && Input.IsActionPressed(PlayerInput.Jump)) Input.ActionRelease(PlayerInput.Jump);
        double over = Over(p.GlobalPosition);
        _apex = Math.Max(_apex, over);
        if (!_left && _apex - _jumpFrom > 1) _left = true;
        if (_stageT > 0.5 && p.IsOnFloor())
        {
            double rise = _apex - _jumpFrom, want = p.JumpVelocity * p.JumpVelocity / (2 * BodyGravity.Ground);
            Log($"jumped {rise:F2} m on the Moon ({want:F2} m wanted), down after {_stageT:F1} s");
            if (Math.Abs(rise - want) > 0.25 * want) { Finish(string.Create(CultureInfo.InvariantCulture, $"the jump rose {rise:F2} m, not {want:F2} m")); return; }
            Next(Stage.BoardAgain);
            return;
        }
        if (_stageT > 20) Finish("still in the air 20 s after the jump");
    }

    /// <summary>Back where it stepped out, and in again (E at the shuttle).</summary>
    private void BoardAgain(FootPlayer p)
    {
        if (p.Vehicle is Airliner { Spec.Spacecraft: true } jet)
        {
            _jet = jet;
            p.FlyControls = () => new FlightInput(_stick, 0f, 0f, 0f, 0f, false, false, 0f);
            Log($"aboard again after {_stageT:F1} s");
            Next(Stage.Climb);
            return;
        }
        if (!_atHatch)
        {
            _atHatch = true;
            // back to the hatch on its left side (stepping out puts the walker past the wingtip, out of E's reach of it)
            Vehicles.VehicleBody? shuttle = null;
            float best = float.MaxValue;
            if (Vehicles.VehicleManager.Instance is { } vehicles)
                foreach (var node in vehicles.GetChildren())
                    if (node is Vehicles.VehicleBody { Ride: Airliner { Spec.Spacecraft: true } } v && v.GlobalPosition.DistanceTo(p.GlobalPosition) < best)
                    {
                        shuttle = v;
                        best = v.GlobalPosition.DistanceTo(p.GlobalPosition);
                    }
            if (shuttle == null) { Finish("the parked shuttle is gone"); return; }
            var hatch = shuttle.ToGlobal(shuttle.Ride.EntryPoint);
            var up = _origin.Frame.UpAt(hatch);
            var outward = hatch - shuttle.GlobalPosition;
            outward = (outward - up * outward.Dot(up)).Normalized();
            p.DebugLaunch(hatch + outward * 0.8f + up * 0.3f, Vector3.Zero);
            Log(string.Create(CultureInfo.InvariantCulture, $"walked back to the hatch, {best:F0} m from the shuttle"));
            return;
        }
        if (_stageT > 1 && (int)(_stageT * 2) != (int)((_stageT - 1.0 / 60) * 2)) p.TryGetIn();
        if (_stageT > 20) Finish("could not get back into the shuttle in 20 s");
    }

    /// <summary>The belly jets up into Space, the nose up, the drive to 100 km, the nose level and the drive off: a lunar orbit.</summary>
    private void Climb()
    {
        var jet = _jet!;
        var p = _local()!;
        var up = _origin.Frame.UpAt(p.GlobalPosition);
        double alt = _origin.AltitudeOf(p.GlobalPosition);
        if (jet.Lander)
        {
            _stick = Vector2.Zero;
            jet.State.Lever = 0.8f;
            if (Over(p.GlobalPosition) > 200 && jet.State.GearDown) jet.Command(AirlinerCommand.Gear);
            if (_t >= _report)
            {
                _report = _t + 5;
                Log($"up on the belly jets: {alt / 1000:F1} km, V/S {jet.VerticalSpeed:F0} m/s");
            }
            if (_stageT > 300) Finish("still on the belly jets 5 min after lifting off");
            return;
        }
        if (!jet.InSpace) return;
        var nose = -jet.State.Attitude.Z;
        if (!jet.DriveOn && !_upDone)
        {
            // straight up first
            jet.Aim = up;
            if (Mathf.RadToDeg(nose.AngleTo(up)) < 2)
            {
                Log($"in Space at {alt / 1000:F1} km, the nose up: the drive on at 30 %");
                jet.Command(AirlinerCommand.Drive);
                _upDone = true;
            }
            return;
        }
        if (jet.DriveOn && alt < 100_000)
        {
            jet.Aim = up;
            jet.State.Lever = 0.3f;
            return;
        }
        // 100 km: level, then off into an orbit
        var ahead = (nose - up * nose.Dot(up)).Normalized();
        jet.Aim = ahead;
        if (jet.DriveOn) jet.State.Lever = 0.02f;
        if (Mathf.RadToDeg(nose.AngleTo(ahead)) < 1 && jet.DriveOn)
        {
            jet.Command(AirlinerCommand.Drive);
            jet.Aim = null;
            Log($"level at {alt / 1000:F1} km: the drive off");
            Next(Stage.Circle);
        }
        if (_stageT > 600) Finish("not back in orbit 10 min after lifting off");
    }

    private void Circle()
    {
        if (_stageT < 1) return;
        var space = _jet!.Space!;
        if (space.Orbit is not { } o || space.Body.Id != BodyId.Moon) { Finish($"no lunar orbit after the climb ({space.Body.Name})"); return; }
        var moon = CelestialBody.Moon;
        var (peri, apo, _) = Orbit.Apsides(moon, o);
        Log($"back in lunar orbit: {(peri - moon.Shape.A) / 1000:F1} x {(apo - moon.Shape.A) / 1000:F1} km");
        if (peri - moon.Shape.A < 20_000) { Finish("the orbit after the climb dips under 20 km"); return; }
        Next(Stage.PickHome);
    }

    /// <summary>The Earth targeted (taps until it is), and held.</summary>
    private void PickHome()
    {
        if (SpectatorTargets.Instance is not { } targets) { Finish("no target cue"); return; }
        for (int i = 0; i < 40 && targets.Target != BodyId.Earth; i++) targets.Cycle();
        if (targets.Target != BodyId.Earth) { Finish("the Earth is not in the target list from the Moon"); return; }
        Log("the Earth targeted: held");
        Key(true);
        Next(Stage.AlignHome);
    }

    /// <summary>Home: in an orbit round the Earth near the arrival height.</summary>
    private void Judge()
    {
        Next(Stage.Judging);
        var space = _jet!.Space!;
        GetTree().CreateTimer(1.0, processAlways: true, processInPhysics: true).Timeout += () =>
        {
            if (space.Orbit is not { } o || space.Body.Id != BodyId.Earth) { Finish($"no Earth orbit after the drive home ({space.Body.Name})"); return; }
            var earth = CelestialBody.Earth;
            var (peri, apo, _) = Orbit.Apsides(earth, o);
            double pe = (peri - earth.Shape.A) / 1000, ap = (apo - earth.Shape.A) / 1000;
            Log($"home in an Earth orbit: {pe:F1} x {ap:F1} km, {_crossings} crossing(s) in all");
            Shot("home");
            Finish(pe > 75 && ap < 140 ? null : string.Create(CultureInfo.InvariantCulture, $"not near a 100 km Earth orbit: {pe:F1} x {ap:F1} km"));
        };
    }

    private static void Tap(Godot.Key key)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = key, Keycode = key, KeyLabel = key, Pressed = down });
    }

    /// <summary>The target action held or let go, as a keyboard's ] (the cue's own input path).</summary>
    private static void Key(bool down) => Input.ParseInputEvent(new InputEventKey
    {
        Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = Godot.Key.Bracketright, Keycode = Godot.Key.Bracketright,
        KeyLabel = Godot.Key.Bracketright, Pressed = down,
    });

    private void Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        var path = ProjectSettings.GlobalizePath($"res://test_output/shuttlemoon/{name}.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"saved {path}");
    }

    private void Finish(string? fail)
    {
        if (_stage == Stage.Done) return;
        _stage = Stage.Done;
        Engine.TimeScale = 1;
        Key(false);
        Input.ActionRelease(PlayerInput.MoveForward);
        Input.ActionRelease(PlayerInput.Sprint);
        if (fail == null) GD.Print("[shuttlemoon] RESULT: ok");
        else GD.PrintErr($"[shuttlemoon] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
