using System;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--ufomooncheck</c> (#1851, C3a of docs/plans/planets.md: "two craft can go"), under <c>--frame earth --chunks
/// fixture:straight [--bodies DIR]</c>: the saucer from a 400 km Earth orbit to the Moon on its own cruise drive. A tap of
/// the target action picks the Moon; the drive on at full lever; the target held turns the drive's way onto the tangent of
/// the Moon's 100 km arrival orbit (coasting first until the Moon has risen); across the Moon's sphere without a jump of its
/// place; the drive off where it passes 100 km: a circular lunar orbit. With the Moon's ground: the drive down at 40° and a
/// slow lever until it drops out at the Space exit, and the hover sets it down on its legs. Fails on a jump, a contact, an
/// orbit not round the Moon or not near 100 km, a stage too long.
/// </summary>
public partial class UfoMoonCheck : Node
{
    public static bool Requested => CmdArgs.Has("--ufomooncheck");

    private enum Stage { Wait, Board, Orbit, Pick, Drive, Coast, Descend, Hover, Done }

    private readonly WorldOrigin _origin;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<BodyGround?> _ground;
    private Stage _stage;
    private double _t, _stageT, _report, _closest = double.MaxValue, _worstJump;
    private Ufo? _ufo;
    private (BodyId Body, DVec3 P, SkyMoment Sky, double Step)? _last;
    private float _down;
    private bool _boost;
    private BodyId _bodyWas = BodyId.Earth;
    private int _crossings;
    private bool _driving;

    public UfoMoonCheck(WorldOrigin origin, Func<FootPlayer?> local, Func<BodyGround?> ground)
    {
        _origin = origin;
        _local = local;
        _ground = ground;
        Name = "UfoMoonCheck";
    }

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ufomoon] {what}"));

    private void Next(Stage s)
    {
        Log($"{_stage} -> {s} at {_t:F1} s");
        _stage = s;
        _stageT = 0;
        _report = 0;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_stage == Stage.Done) return;
        _t += delta;
        _stageT += delta;
        var p = _local();
        if (_stage > Stage.Board && (p == null || p.Vehicle != _ufo)) { Finish($"out of the saucer at {_stage}"); return; }
        if (_ufo?.Space is { Contacts: > 0 } s && _stage < Stage.Hover) { Finish($"{s.Contacts} contact(s) in free flight at {_stage}"); return; }
        if (_origin.Body != _bodyWas)
        {
            _crossings++;
            Log($"crossed from {_bodyWas} to {_origin.Body} at {_t:F1} s");
            _bodyWas = _origin.Body;
        }
        switch (_stage)
        {
            case Stage.Wait when _t > 3 && p == null:
                if (_stageT > 1) { Tap(Godot.Key.T); _stageT = 0; }
                if (_t > 30) Finish("no walker 30 s after T");
                break;
            case Stage.Wait when _t > 3 && p != null:
                if (!p.IsOnFloor() || !p.SetRide(RideKind.Ufo) || p.Vehicle is not Ufo ufo)
                {
                    if (_t > 40) Finish("no saucer 40 s in");
                    return;
                }
                _ufo = ufo;
                p.FlyControls = () => new FlightInput(Vector2.Zero, 0f, _down, 0f, 0f, false, false, 0f, Boost: _boost);
                Next(Stage.Board);
                break;
            case Stage.Board when _stageT > 1:
                Log(p!.PutInOrbit(400));
                Next(Stage.Orbit);
                break;
            case Stage.Orbit when _stageT > 2:
                if (!_ufo!.InSpace) { Finish("not in Space after /orbit 400"); return; }
                Tap(Godot.Key.Bracketright);
                Next(Stage.Pick);
                break;
            case Stage.Pick:
                if (SpectatorTargets.Instance?.Target == BodyId.Moon)
                {
                    Log("the Moon targeted by a tap, the target held");
                    Key(true);
                    Next(Stage.Drive);
                }
                else if (_stageT > 3) Finish($"the tap targeted {SpectatorTargets.Instance?.Target?.ToString() ?? "nothing"}, not the Moon");
                break;
            case Stage.Drive:
                Drive(delta);
                break;
            case Stage.Coast:
                Coast();
                break;
            case Stage.Descend:
                Descend();
                break;
            case Stage.Hover:
                Hover();
                break;
        }
    }

    private void Drive(double delta)
    {
        var ufo = _ufo!;
        var p = _local()!;
        if (ufo.Aim is not { } aim) { if (_stageT > 3) Finish("the hold set no aim"); return; }
        // the Moon under the horizon: round the Earth on the orbit until it rises (the way out would run into the Earth)
        if (!_driving)
        {
            if (Blocked(aim))
            {
                if (Engine.TimeScale == 1) Log("the Moon is behind the Earth: coasting round until it rises");
                Engine.TimeScale = 32;
                if (_stageT > 9000) Finish("the Moon did not rise");
                return;
            }
            Engine.TimeScale = 1;
            _driving = true;
            ufo.ToggleDrive();
            Log($"the way clear after {_stageT:F0} s: the drive on, slow until its way is on the aim");
            return;
        }
        if (_stageT > 0.5 && !ufo.DriveOn) { Finish($"the drive is off at {_stageT:F1} s"); return; }
        // slow while its way turns onto the aim, then full
        ufo.SetDriveLever(Mathf.RadToDeg(ufo.Nose.AngleTo(aim)) > 1 ? 0.02f : 1f);
        if (_origin.Frame.Axes is not { } axes) { Finish("no anchor frame"); return; }
        var e = axes.ToEcef(p.GlobalPosition.X, p.GlobalPosition.Y, p.GlobalPosition.Z);
        var here = new DVec3(e.X, e.Y, e.Z);
        double step = ufo.State.Velocity.Length() * delta;
        if (_last is { } last)
        {
            double moved = (here - Frames.Convert(last.Body, last.P, axes.Body, last.Sky)).Length;
            double allowed = Math.Max(last.Step, step) * 1.05 + 1;
            _worstJump = Math.Max(_worstJump, moved - allowed);
            if (moved > allowed) { Finish(string.Create(CultureInfo.InvariantCulture, $"the saucer's place jumped {moved / 1000:F1} km in a frame")); return; }
        }
        _last = (axes.Body, here, SkyClock.Now, step);
        double d = ufo.DriveDistance, arrival = ShuttleFlight.ArrivalM(CelestialBody.Moon);
        if (_t >= _report)
        {
            _report = _t + 10;
            Log($"{_stageT:F0} s: {d / 1000:F0} km to the nearest surface, {ufo.State.Velocity.Length() / 1000:F0} km/s, counted from {axes.Body}");
        }
        if (_origin.Body == BodyId.Moon) _closest = Math.Min(_closest, d);
        bool there = _origin.Body == BodyId.Moon && (d <= arrival * 1.02 || d < 2 * arrival && d > _closest + 50);
        if (!there) { if (_stageT > 9900) Finish("still out after the drive's time"); return; }
        Key(false);
        ufo.ToggleDrive();
        Log($"the drive off {d / 1000:F1} km over the Moon, {_crossings} crossing(s), the worst frame {Math.Max(0, _worstJump):F1} m past its own step");
        Next(Stage.Coast);
    }

    private bool Blocked(Vector3 aim)
    {
        if (_origin.Frame.Axes is not { } axes || _local() is not { } p) return false;
        var e = axes.ToEcef(p.GlobalPosition.X, p.GlobalPosition.Y, p.GlobalPosition.Z);
        var at = new DVec3(e.X, e.Y, e.Z);
        var dir = new DVec3(axes.X.X * aim.X + axes.Y.X * aim.Y + axes.Z.X * aim.Z,
            axes.X.Y * aim.X + axes.Y.Y * aim.Y + axes.Z.Y * aim.Z,
            axes.X.Z * aim.X + axes.Y.Z * aim.Y + axes.Z.Z * aim.Z).Normalized();
        double t = -(at.X * dir.X + at.Y * dir.Y + at.Z * dir.Z);
        return t > 0 && (at + dir * t).Length < CelestialBody.Of(axes.Body).Shape.A + 200_000;
    }

    private void Coast()
    {
        if (_stageT < 0.5) return;
        var space = _ufo!.Space;
        if (space.Orbit is not { } o || space.Body.Id != BodyId.Moon) { Finish($"no lunar orbit after the drive off ({space.Body.Name})"); return; }
        var moon = CelestialBody.Moon;
        var (peri, apo, _) = Orbit.Apsides(moon, o);
        double pe = (peri - moon.Shape.A) / 1000, ap = (apo - moon.Shape.A) / 1000;
        Log($"in lunar orbit: {pe:F1} x {ap:F1} km, {o.V.Length:F0} m/s");
        if (pe < 80 || ap > 120) { Finish(string.Create(CultureInfo.InvariantCulture, $"not a 100 km circular orbit: {pe:F1} x {ap:F1} km")); return; }
        if (_ground() == null) { Log("no ground data (--bodies): the trip alone"); Finish(null); return; }
        Next(Stage.Descend);
    }

    /// <summary>The drive with its way 40° under the horizon at 15 %, until it drops out at the Space exit.</summary>
    private void Descend()
    {
        var ufo = _ufo!;
        var p = _local()!;
        var up = _origin.Frame.UpAt(p.GlobalPosition);
        var nose = ufo.Nose;
        var ahead = (nose - up * nose.Dot(up)).Normalized();
        if (!ufo.DriveOn && _stageT < 0.1) { ufo.ToggleDrive(); return; }
        if (!ufo.DriveOn)
        {
            ufo.Aim = null;
            Log($"out of the drive at {_origin.AltitudeOf(p.GlobalPosition) / 1000:F1} km: the hover down");
            Next(Stage.Hover);
            return;
        }
        ufo.Aim = (ahead * Mathf.Cos(Mathf.DegToRad(40)) - up * Mathf.Sin(Mathf.DegToRad(40))).Normalized();
        ufo.SetDriveLever(0.15f);
        if (_t >= _report)
        {
            _report = _t + 10;
            Log($"down: {_origin.AltitudeOf(p.GlobalPosition) / 1000:F1} km, {ufo.State.Velocity.Length():F0} m/s");
        }
        if (_stageT > 900) Finish("still in the drive 15 min after turning down");
    }

    /// <summary>Down on the hover (the descend key held) onto the legs.</summary>
    private void Hover()
    {
        var ufo = _ufo!;
        var p = _local()!;
        _down = 1f;
        // boosted while the ground is far (the hover sinks at 10 m/s, three times that boosted)
        _boost = p.Clearance > 800f;
        if (_t >= _report)
        {
            _report = _t + 10;
            Log($"hover: {_origin.AltitudeOf(p.GlobalPosition) / 1000:F2} km, {ufo.State.Velocity.Length():F0} m/s, clearance {p.Clearance:F0} m");
        }
        if (ufo.State.Landed)
        {
            _down = 0f;
            _boost = false;
            Log($"landed on the Moon after {_stageT:F0} s on the hover, {_crossings} crossing(s) in all");
            Shot("ufo_landed");
            Finish(null);
            return;
        }
        if (_stageT > 900) Finish("not down 15 min after leaving the drive");
    }

    private static void Tap(Godot.Key key)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Device = (int)InputEvent.DeviceIdKeyboard, PhysicalKeycode = key, Keycode = key, KeyLabel = key, Pressed = down });
    }

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
        if (fail == null) GD.Print("[ufomoon] RESULT: ok");
        else GD.PrintErr($"[ufomoon] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
