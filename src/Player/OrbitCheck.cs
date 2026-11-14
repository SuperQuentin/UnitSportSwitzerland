using System;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --headless --fixed-fps 60 --path . -- --orbitcheck [km] --world flat</c> (#1430 H5): the UFO put on
/// a circular orbit by <c>/orbit</c> (400 km by default) coasts one period at eight times speed, the floating
/// origin following it in turned Space frames (<see cref="OriginShifter.SpaceMode"/>), then thrusts down out of
/// the Space regime into the arcade hover, and lands. Passes when the orbit closes within 10 m, its energy drifts less
/// than 10⁻⁶, no contact ever moved the body off it, the world stayed small round the camera, and the craft
/// comes down out of orbit into a hover, in a ground frame again (no turn, a whole-km corner), and lands.
/// </summary>
public partial class OrbitCheck : Node
{
    public static bool Requested => CmdArgs.Has("--orbitcheck");

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private Ufo? _ufo;
    private OrbitState _start;
    private double _period, _t0, _t, _report, _wait, _worst;
    private int _stage, _turns;
    private bool _done;
    private readonly double _km;
    private readonly bool _earth;

    /// <summary>Simulation speed while it coasts: SimClock's fastest.</summary>
    private const double Speed = SimClock.MaxScale;

    public OrbitCheck(WorldOrigin origin)
    {
        _origin = origin;
        _km = CmdArgs.Double("--orbitcheck") is double km && km > 0 ? km : 400;
        _earth = origin.Frame.IsAnchor;
    }

    public override void _Ready()
    {
        // the camera this world has none of: the shifter follows the craft, as it follows the camera in game
        // --frame earth (#1630): the earth world's one turning frame, 20 km steps in Space and 3 km on the ground
        AddChild(new OriginShifter(_origin, () => _player?.GlobalPosition, () => true) { Earth = _origin.Frame.IsAnchor });
        _origin.Shifted += shift => { if (shift.Turns) _turns++; };
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _wait += delta;
        if (_wait > 3 * 3600) { Fail("TIMEOUT"); return; }

        switch (_stage)
        {
            case 0:
                // on the ground in the saucer, nothing held
                _player = new FootPlayer { Name = "Probe", Origin = _origin };
                AddChild(_player);
                _player.GlobalPosition = new Vector3(0, TestWorld.GroundY + 1f, 0);
                _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
                _player.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f);
                _stage = 1;
                return;
            case 1:
                if (_wait < 1 || !_player!.IsOnFloor()) return;
                if (_ufo == null)
                {
                    _player.SetRide(RideKind.Ufo);
                    _ufo = _player.Vehicle as Ufo;
                    if (_ufo == null) Fail("no UFO");
                    return;
                }
                GD.Print($"[orbitcheck] {_player.PutInOrbit(_km)}");
                _stage = 2;
                return;
            case 2:
                // one step taken on the orbit: that is where one period must bring it back to
                if (_ufo!.Orbit is not { } first) { Fail("not in orbit after /orbit"); return; }
                _start = first;
                var (_, _, a) = Orbit.Apsides(CelestialBody.Earth, first);
                _period = Orbit.Period(CelestialBody.Earth, a);
                GD.Print(FormattableString.Invariant($"[orbitcheck] start r {first.R.Length:F1} m, v {first.V.Length:F2} m/s, period {_period:F1} s"));
                Engine.TimeScale = Speed;
                _t0 = _ufo.OrbitSeconds;
                _stage = 3;
                return;
            case 3:
                Coast(delta);
                return;
            case 4:
                Deorbit(delta);
                return;
            case 5:
                Land(delta);
                return;
        }
    }

    /// <summary>One period with nothing held; every step the world must stay small round the craft.</summary>
    private void Coast(double delta)
    {
        var p = _player!;
        var ufo = _ufo!;
        _t = ufo.OrbitSeconds - _t0;
        // horizontally: Y stays the altitude in the Space frames (no vertical origin yet)
        _worst = Math.Max(_worst, new Vector2(p.GlobalPosition.X, p.GlobalPosition.Z).Length());
        if (ufo.Orbit is not { } s) { Fail(FormattableString.Invariant($"fell out of orbit at {_t:F0} s")); return; }
        if (_t >= _report)
        {
            _report += 600;
            var g = _origin.Frame.ToGlobal(p.GlobalPosition);
            GD.Print(FormattableString.Invariant(
                $"[orbitcheck] t {_t:F0} s: alt {g.Alt / 1000:F2} km, |v| {s.V.Length:F2} m/s, world |xz| {new Vector2(p.GlobalPosition.X, p.GlobalPosition.Z).Length():F0} m, {OriginShifter.Instance?.ShiftCount} shifts ({_turns} turned), contacts {ufo.Contacts}"));
        }
        if (_t < _period) return;

        // the last step went past the period: back to it exactly by a Verlet step of the difference
        // (time-reversible), so the miss is the orbit's and not the step's
        var end = Orbit.Step(CelestialBody.Earth, s, DVec3.Zero, _period - _t);
        double miss = (end.R - _start.R).Length;
        double drift = Math.Abs((s.Energy(CelestialBody.Earth) - _start.Energy(CelestialBody.Earth)) / _start.Energy(CelestialBody.Earth));
        GD.Print(FormattableString.Invariant(
            $"[orbitcheck] one period ({_t:F2} s): {miss:F3} m from the start, energy drift {drift:E2}, contacts {ufo.Contacts}, {OriginShifter.Instance?.ShiftCount} shifts ({_turns} turned), world |xz| at most {_worst:F0} m"));
        if (miss > 10) { Fail(FormattableString.Invariant($"the orbit did not close: {miss:F1} m")); return; }
        if (drift > 1e-6) { Fail(FormattableString.Invariant($"energy drift {drift:E2}")); return; }
        if (ufo.Contacts > 0) { Fail($"{ufo.Contacts} contacts in free flight"); return; }
        if (_turns == 0) { Fail("no turned Space shift"); return; }
        // one shift every threshold along the way, give or take: more is the origin missing the camera
        double expected = 2 * Math.PI * _start.R.Length / OriginShifter.SpaceMode.ThresholdM;
        if (_turns > 1.5 * expected) { Fail(FormattableString.Invariant($"{_turns} turned shifts for about {expected:F0} expected")); return; }
        if (_worst > 30_000) { Fail(FormattableString.Invariant($"the world grew to {_worst:F0} m round the craft")); return; }

        // down: boosted thrust toward the ground until the Space regime lets go
        Engine.TimeScale = Speed;
        p.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 1f, 0f, 0f, false, false, 0f, Boost: true);
        _t = 0;
        _stage = 4;
    }

    /// <summary>Thrusting down out of orbit: the arcade hover takes over below 50 km and brings the sink under control.</summary>
    private void Deorbit(double delta)
    {
        var p = _player!;
        _t += delta;
        double alt = _origin.Frame.AltitudeOf(p.GlobalPosition);
        if (_t > 1800) { Fail(FormattableString.Invariant($"still at {alt / 1000:F1} km after half an hour down")); return; }
        if (_ufo!.Orbit != null) return;
        // out of orbit: let go, the hover holds its height
        p.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f);
        float sink = -p.Velocity.Y;
        if (alt < 0) { Fail("below the ground"); return; }
        if (Mathf.Abs(sink) > 0.5f) return;
        GD.Print(FormattableString.Invariant($"[orbitcheck] out of orbit and hovering at {alt / 1000:F1} km after {_t:F0} s"));
        // back below the handover the world is a flat ground frame again, exactly: no turn, a whole-km
        // corner, altitude 0 at world Y 0 (#1480, the coordinator's condition)
        var f = _origin.Frame;
        // the earth world has one frame at every altitude (#1630): out of the Space regime, its anchor on the ground
        // under the craft, up the ellipsoid's there
        if (_earth)
        {
            var up = f.UpAt(p.GlobalPosition);
            double off = Math.Acos(Math.Clamp(up.Dot(Vector3.Up), -1f, 1f)) * 180 / Math.PI;
            if (!f.IsAnchor || OriginShifter.Instance?.InSpace != false || off > 0.5)
            {
                Fail(FormattableString.Invariant($"not back in the ground's turning frame: anchor {f.IsAnchor}, in Space {OriginShifter.Instance?.InSpace}, up {off:F2}° off"));
                return;
            }
        }
        else if (f.IsAnchor || f.Alt0 != 0 || f.E % 1000 != 0 || f.N % 1000 != 0)
        {
            Fail(FormattableString.Invariant($"not back in a ground frame: anchor {f.IsAnchor}, alt0 {f.Alt0}, corner {f.E}/{f.N}"));
            return;
        }
        // down onto the ground: the flat world is everywhere, so its plane is put under the craft
        p.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 1f, 0f, 0f, false, false, 0f, Boost: true);
        _t = 0;
        _stage = 5;
    }

    /// <summary>Down from the hover onto the flat world's plane, on its legs.</summary>
    private void Land(double delta)
    {
        var p = _player!;
        _t += delta;
        double alt = _origin.Frame.AltitudeOf(p.GlobalPosition);
        if (_t > 3600) { Fail(FormattableString.Invariant($"still at {alt:F0} m after an hour down")); return; }
        if (alt < 2_000 && GetParent().GetNodeOrNull<Node3D>("Ground") is { } ground
            && new Vector2(ground.GlobalPosition.X - p.GlobalPosition.X, ground.GlobalPosition.Z - p.GlobalPosition.Z).Length() > 1_000)
            ground.GlobalPosition = new Vector3(p.GlobalPosition.X, TestWorld.GroundY - 0.5f, p.GlobalPosition.Z);
        if (alt < 20) p.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 1f, 0f, 0f, false, false, 0f);
        if (!_ufo!.State.Landed) return;
        var g = _origin.Frame.ToGlobal(p.GlobalPosition);
        GD.Print(FormattableString.Invariant($"[orbitcheck] landed after the orbit at LV95 {g.Lv95.E:F0}/{g.Lv95.N:F0}, {_t:F0} s from the hover; frame {_origin.Frame.E:F0}/{_origin.Frame.N:F0}, no turn"));
        Finish(0, "ok");
    }

    private void Fail(string why) => Finish(1, "FAILED " + why);

    private void Finish(int code, string result)
    {
        Engine.TimeScale = 1.0;
        _done = true;
        GD.Print($"[orbitcheck] RESULT: {result}");
        GetTree().Quit(code);
    }
}
