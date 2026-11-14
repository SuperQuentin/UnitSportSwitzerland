using System;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--shuttlemooncheck</c> (#1851, C3a of docs/plans/planets.md), under <c>--frame earth --chunks fixture:straight</c>:
/// the shuttle from a 400 km Earth orbit to a lunar one, on the pilot's own controls. A tap of the target action (]) picks
/// the Moon; held, it turns the nose onto the tangent of the Moon's 100 km arrival orbit (<see cref="ShuttleFlight.TangentAim"/>);
/// the cruise drive at full lever carries it out there, slowing as the Moon's surface nears, across the Earth's sphere
/// of influence into the Moon's without a jump of its place; switched off where it passes the arrival height, the drive
/// leaves it in a circular lunar orbit, which closes within 10 m after one turn (at 8x). Fails on a frame where the
/// craft's place jumps past its own step, a contact, an orbit not round the Moon or not near 100 km, a stage too long.
/// Windowed, a picture on the way and in lunar orbit (<c>test_output/shuttlemoon/</c>).
/// </summary>
public partial class ShuttleMoonCheck : Node
{
    public static bool Requested => CmdArgs.Has("--shuttlemooncheck");

    private enum Stage { Wait, Board, Orbit, Pick, Align, Drive, Coast, Done }

    private readonly WorldOrigin _origin;
    private readonly Func<FootPlayer?> _local;
    private Stage _stage;
    private double _t, _stageT, _report;
    private Airliner? _jet;
    private (BodyId Body, DVec3 P, SkyMoment Sky, double Step)? _last;
    private double _worstJump, _closest = double.MaxValue, _period, _t0;
    private int _crossings;
    private OrbitState _start;
    private BodyId _bodyWas = BodyId.Earth;

    public ShuttleMoonCheck(WorldOrigin origin, Func<FootPlayer?> local)
    {
        _origin = origin;
        _local = local;
        Name = "ShuttleMoonCheck";
    }

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[shuttlemoon] {what}"));

    private void Next(Stage s)
    {
        Log($"{_stage} -> {s} at {_t:F1} s");
        _stage = s;
        _stageT = 0;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_stage == Stage.Done) return;
        _t += delta;
        _stageT += delta;
        var p = _local();
        if (_stage > Stage.Board && (p == null || p.Vehicle != _jet)) { Finish($"out of the shuttle at {_stage}"); return; }
        if (_jet?.Space is { Contacts: > 0 } s) { Finish($"{s.Contacts} contact(s) in free flight at {_stage}"); return; }
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
                    TapT();
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
                // the commander's hands on nothing: the stick centred, piloted
                p.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f);
                Next(Stage.Board);
                break;
            case Stage.Board when _stageT > 1:
                Log(p!.PutInOrbit(400));
                Next(Stage.Orbit);
                break;
            case Stage.Orbit when _stageT > 2:
                if (!_jet!.InSpace) { Finish("not in Space after /orbit 400"); return; }
                // a tap of the target action: the first target from the Earth is the Moon
                Key(true);
                Key(false);
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
                Align();
                break;
            case Stage.Drive:
                Drive(delta);
                break;
            case Stage.Coast:
                Coast();
                break;
        }
    }

    private void Align()
    {
        var jet = _jet!;
        if (jet.Aim is not { } aim)
        {
            if (_stageT > 2) Finish("the hold set no aim");
            return;
        }
        double off = Mathf.RadToDeg((-jet.State.Attitude.Z).AngleTo(aim));
        if (off > 1)
        {
            if (_stageT > 30) Finish(string.Create(CultureInfo.InvariantCulture, $"still {off:F1}° off the aim after 30 s"));
            return;
        }
        // the Moon below the horizon: the way out runs into the Earth (the drive would slow to it and drop out of Space).
        // A pilot coasts round until it rises: so does the check, at 8x
        if (Blocked(aim))
        {
            if (Engine.TimeScale == 1) Log($"the Moon is behind the Earth: coasting round until it rises");
            Engine.TimeScale = CoastScale;
            if (_stageT > 1200) Finish("the Moon did not rise in 20 min of coasting");
            return;
        }
        Engine.TimeScale = 1;
        Log($"aligned after {_stageT:F1} s, the way clear of the Earth: the drive on, full lever");
        jet.Command(AirlinerCommand.Drive);
        Next(Stage.Drive);
    }

    /// <summary>The line along <paramref name="aim"/> from the craft passes within 200 km of the Earth's surface.</summary>
    private bool Blocked(Vector3 aim)
    {
        if (_origin.Frame.Axes is not { Body: BodyId.Earth } axes || _local() is not { } p) return false;
        var e = axes.ToEcef(p.GlobalPosition.X, p.GlobalPosition.Y, p.GlobalPosition.Z);
        var at = new DVec3(e.X, e.Y, e.Z);
        // the aim's ECEF direction: world axes X, Y, Z are ECEF vectors
        var dir = new DVec3(axes.X.X * aim.X + axes.Y.X * aim.Y + axes.Z.X * aim.Z,
            axes.X.Y * aim.X + axes.Y.Y * aim.Y + axes.Z.Y * aim.Z,
            axes.X.Z * aim.X + axes.Y.Z * aim.Y + axes.Z.Z * aim.Z).Normalized();
        double t = -(at.X * dir.X + at.Y * dir.Y + at.Z * dir.Z);
        if (t <= 0) return false;
        var closest = at + dir * t;
        return closest.Length < CelestialBody.Earth.Shape.A + 200_000;
    }

    /// <summary>The coast round the Earth waiting for the Moon to rise, faster than a player may (the orbit's substeps hold it).</summary>
    private const double CoastScale = 32;

    private void Drive(double delta)
    {
        var jet = _jet!;
        var p = _local()!;
        if (_stageT > 0.2 && !jet.DriveOn) { Finish($"the drive is off at {_stageT:F1} s"); return; }
        if (jet.DriveOn) jet.State.Lever = 1f;
        // continuity: last frame's place counted from this frame's body at last frame's moment, against this one
        if (_origin.Frame.Axes is not { } axes) { Finish("no anchor frame"); return; }
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
            if (moved > allowed) { Finish(string.Create(CultureInfo.InvariantCulture, $"the craft's place jumped {moved / 1000:F1} km in a frame ({allowed / 1000:F1} km its own step), counted from {axes.Body}")); return; }
        }
        _last = (axes.Body, here, sky, step);

        double d = jet.DriveDistance, arrival = ShuttleFlight.ArrivalM(CelestialBody.Moon);
        if (_t >= _report)
        {
            _report = _t + 5;
            var nose = -jet.State.Attitude.Z;
            var upw = _origin.Frame.UpAt(p.GlobalPosition);
            string aimText = jet.Aim is { } am ? string.Create(CultureInfo.InvariantCulture, $"aim {Mathf.RadToDeg(nose.AngleTo(am)):F1}° off the nose, {90 - Mathf.RadToDeg(am.AngleTo(upw)):F1}° over the horizon") : "no aim";
            Log($"{_stageT:F0} s: {d / 1000:F0} km to the nearest surface, {jet.State.Velocity.Length() / 1000:F0} km/s, nose {90 - Mathf.RadToDeg(nose.AngleTo(upw)):F1}° over the horizon, {aimText}, aligning {SpectatorTargets.Instance?.Aligning}, counted from {axes.Body}");
        }
        if (_stageT > 2 && _stageT < 2.1) Shot("drive");
        _closest = Math.Min(_closest, d);
        // where it passes the arrival height (or starts to climb away below twice it): the drive off
        bool there = _origin.Body == BodyId.Moon && (d <= arrival * 1.02 || d < 2 * arrival && d > _closest + 50);
        if (!there)
        {
            if (_stageT > 600) Finish(string.Create(CultureInfo.InvariantCulture, $"still {d / 1000:F0} km out after 10 min in the drive"));
            return;
        }
        Key(false);
        jet.Command(AirlinerCommand.Drive);
        Log($"the drive off {d / 1000:F1} km over the Moon after {_stageT:F1} s, {_crossings} crossing(s), the worst frame {Math.Max(0, _worstJump):F1} m past its own step");
        Next(Stage.Coast);
    }

    private void Coast()
    {
        var jet = _jet!;
        var space = jet.Space!;
        if (_period == 0)
        {
            if (_stageT < 0.5) return;
            if (space.Orbit is not { } first) { Finish("no orbit after the drive off"); return; }
            if (space.Body.Id != BodyId.Moon) { Finish($"in orbit round {space.Body.Name}, not the Moon"); return; }
            var moon = CelestialBody.Moon;
            var (peri, apo, a) = Orbit.Apsides(moon, first);
            double pe = (peri - moon.Shape.A) / 1000, ap = (apo - moon.Shape.A) / 1000;
            Log($"in lunar orbit: {pe:F1} x {ap:F1} km, {first.V.Length:F0} m/s");
            if (pe < 80 || ap > 120) { Finish(string.Create(CultureInfo.InvariantCulture, $"not a 100 km circular orbit: {pe:F1} x {ap:F1} km")); return; }
            Shot("lunar_orbit");
            _start = first;
            _period = Orbit.Period(moon, a);
            _t0 = space.Seconds;
            Engine.TimeScale = SimClock.MaxScale;
            return;
        }
        if (space.Orbit is not { } s) { Finish("fell out of the lunar orbit"); return; }
        double t = space.Seconds - _t0;
        if (t < _period) return;
        var end = Orbit.Step(CelestialBody.Moon, s, DVec3.Zero, _period - t);
        double miss = (end.R - _start.R).Length;
        Log($"one lunar orbit ({_period / 60:F1} min): {miss:F2} m from the start, contacts {space.Contacts}");
        Engine.TimeScale = 1;
        Finish(miss <= 10 ? null : string.Create(CultureInfo.InvariantCulture, $"the lunar orbit did not close: {miss:F1} m"));
    }

    private static void TapT()
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = Godot.Key.T, Keycode = Godot.Key.T, KeyLabel = Godot.Key.T, Pressed = down });
    }

    /// <summary>The target action, as a keyboard's ] (the cue's own input path).</summary>
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
        Input.ParseInputEvent(new InputEventKey { Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = Godot.Key.Bracketright, Keycode = Godot.Key.Bracketright, Pressed = false });
        if (fail == null) GD.Print("[shuttlemoon] RESULT: ok");
        else GD.PrintErr($"[shuttlemoon] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
