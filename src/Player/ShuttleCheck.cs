using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --headless --fixed-fps 60 --path . -- --shuttlecheck [glide|europe] --world flat</c> (#1492, #1430 C2):
/// the whole mission, flown by <see cref="ShuttlePilot"/> on the shuttle's own controls, judged here:
/// <list type="number">
/// <item>take-off from the runway at the start, a rocket climb into Space (60 km);</item>
/// <item>the cruise drive up to 400 km, then round the Earth (a whole turn of the ground track);</item>
/// <item>the drive off: a circular orbit at 400 km, coasted;</item>
/// <item>a retro burn to a 45 km perigee, half an orbit down, re-entry (the glow), handed to the air below 50 km;</item>
/// <item>back up on the rockets, the drive home, out of it 80 km up the runway's line, and a straight-in glide
/// to the runway it left from: down on its wheels, stopped, within the runway.</item>
/// </list>
/// <c>glide</c>: the landing alone, from where the drive leaves it. <c>europe</c> (#1497): the film's mission on the
/// flat world: a taxi onto the runway first, the drive back over home instead of round the Earth, a steep retro burn.
/// Fails on a crash, a hard landing, damage, a contact in free flight, an orbit that is not circular, no glow, a
/// stage that takes too long, or a landing off the runway.
/// </summary>
public partial class ShuttleCheck : Node
{
    public static bool Requested => CmdArgs.Has("--shuttlecheck");

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private Airliner? _shuttle;
    private ShuttlePilot? _pilot;
    private double _t, _report;
    private bool _done;
    private readonly string _mode = CmdArgs.Value("--shuttlecheck", notFlag: true) ?? "";

    /// <summary>The test world's runway: from the start north, 3.6 km by 60 m, at sea level.</summary>
    private const float RunwayLength = 3600f, RunwayHalfWidth = 30f;

    public ShuttleCheck(WorldOrigin origin) => _origin = origin;

    public override void _Ready()
    {
        // --frame earth (#1630): the earth world's one turning frame
        AddChild(new OriginShifter(_origin, () => _player?.GlobalPosition, () => true) { Earth = _origin.Frame.IsAnchor });
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _t += delta;
        if (_t > 6 * 3600) { Fail("TIMEOUT"); return; }
        if (_pilot == null) { Spawn(); return; }
        var p = _player!;
        var jet = _shuttle!;
        if (p.Vehicle != jet) { Fail($"out of the shuttle at stage {_pilot.Now} (crashed?)"); return; }
        if (p.VehicleHealth < 50f) { Fail($"damaged at stage {_pilot.Now}: health {p.VehicleHealth:F0}"); return; }
        if (jet.Space!.Contacts > 0) { Fail($"{jet.Space.Contacts} contacts in free flight"); return; }
        if (_t >= _report)
        {
            _report = _t + 60;
            Report(p, jet);
        }
        double alt = _origin.Frame.AltitudeOf(p.GlobalPosition);
        if (_pilot.Now == ShuttlePilot.Stage.Glide) PutGround();
        _pilot.Step(p, jet, _origin.Frame, (float)alt, (float)delta);
        Engine.TimeScale = _pilot.WantScale;
        if (_pilot.Now == ShuttlePilot.Stage.Failed) { Fail(_pilot.Failure ?? "the pilot failed"); return; }
        if (_pilot.Now == ShuttlePilot.Stage.Stopped) Judge(p, jet);
    }

    private void Report(FootPlayer p, Airliner jet)
    {
        double alt = _origin.Frame.AltitudeOf(p.GlobalPosition);
        // the speed it has (in the flat Space frame far from Switzerland the world is stretched by LV95's scale)
        GD.Print(FormattableString.Invariant(
            $"[shuttlecheck] t {_t:F0} s {_pilot?.Now}: alt {alt / 1000:F2} km, v {jet.State.Tas:F0} m/s, ias {jet.State.Ias:F0}, pitch {Mathf.RadToDeg(AirlinerFlight.PitchOf(jet.State.Attitude)):F0}°, bank {Mathf.RadToDeg(AirlinerFlight.BankOf(jet.State.Attitude)):F0}°, lever {jet.State.Lever:F2}, heat {jet.Heat:F2}, drive {jet.DriveOn}, home {_pilot?.DistanceHome(_origin.Frame, p) / 1000:F0} km, health {p.VehicleHealth:F0}"));
    }

    private void Spawn()
    {
        if (_player == null)
        {
            _player = new FootPlayer { Name = "Probe", Origin = _origin };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(0, TestWorld.GroundY + 1f, 0);
            _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
            _player.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f, Brake: 1f);
            return;
        }
        if (_t < 1 || !_player.IsOnFloor()) return;
        if (_shuttle == null)
        {
            if (!_player.SetRide(RideKind.Shuttle) || _player.Vehicle is not Airliner { Spec.Spacecraft: true } jet) { Fail("no shuttle"); return; }
            _shuttle = jet;
            return;
        }
        if (_t < 3) return;
        var g = _origin.Frame.ToGlobal(_player.GlobalPosition);
        var runway = new ShuttlePilot.Runway(g.Lv95.E, g.Lv95.N, 0f, RunwayLength, RunwayHalfWidth, 0);
        var plan = _mode == "europe"
            // the film's mission (#1497): a taxi from 80 m east of the line facing west, the drive back over home,
            // a steep retro burn, a short while in the air before back up
            ? new ShuttlePilot.Mission(runway, runway, Start: (g.Lv95.E + 80, g.Lv95.N + 60, 1.5f * Mathf.Pi), RoundTheEarth: false,
                CoastSeconds: 60, PerigeeKm: -3500, AirSeconds: 20, TurnOutKm: 2000)
            : new ShuttlePilot.Mission(runway, runway);
        _pilot = new ShuttlePilot(plan)
        {
            Changed = (from, to) => GD.Print(FormattableString.Invariant($"[shuttlecheck] {from} -> {to} at {_t:F0} s")),
            Log = line => GD.Print("[shuttlecheck] " + line),
        };
        _pilot.Changed += Judged;
        if (plan.Start is { } start)
        {
            // stood at the taxi's start, facing its way
            _player.DebugLaunch(_origin.Frame.ToWorld(start.E, start.N, 0.05), Vector3.Zero);
            // put down, not landed: its first contact is settling onto the wheels
            _shuttle.State.Settle = 2f;
            _shuttle.State.Yaw = -start.Heading;
            _shuttle.State.Attitude = new Basis(Vector3.Up, -start.Heading);
        }
        if (_mode == "glide")
        {
            // the landing alone: where the drive leaves it, 80 km out on the runway's line, 52 km up, 300 m/s north
            var at = _origin.Frame.ToWorld(g.Lv95.E, g.Lv95.N - plan.EntryOut, 52_000);
            _player.DebugLaunch(at, new Vector3(0, -30f, -300f));
            _shuttle.State.Lever = _shuttle.State.Spool = 0f;
            _pilot.SkipToLanding();
        }
    }

    /// <summary>What each stage left behind, judged as it ends.</summary>
    private void Judged(ShuttlePilot.Stage from, ShuttlePilot.Stage to)
    {
        var pilot = _pilot!;
        if (to == ShuttlePilot.Stage.Retro && pilot.OrbitApsides is { } a && a.Apo - a.Peri > 1_000)
        {
            double r0 = CelestialBody.Earth.Shape.A;
            Fail(FormattableString.Invariant($"not circular: {(a.Peri - r0) / 1000:F1} x {(a.Apo - r0) / 1000:F1} km"));
        }
        if (from == ShuttlePilot.Stage.Entry && _shuttle!.HeatPeak < 0.5f)
            Fail(FormattableString.Invariant($"no glow on re-entry: heat peak {_shuttle.HeatPeak:F2}"));
        if (to == ShuttlePilot.Stage.Rollout && pilot.TouchSink > _shuttle!.Spec.HardLanding)
            Fail(FormattableString.Invariant($"a hard landing: {pilot.TouchSink:F2} m/s"));
    }

    private void Judge(FootPlayer p, Airliner jet)
    {
        var (along, across) = _pilot!.StoppedAt;
        GD.Print(FormattableString.Invariant($"[shuttlecheck] stopped {along:F0} m past the threshold, {across:F1} m off the centreline, health {p.VehicleHealth:F0}, heat peak {jet.HeatPeak:F2}, drove round {_pilot.DroveRound}"));
        if (along < 0 || along > RunwayLength || Math.Abs(across) > RunwayHalfWidth) { Fail("stopped off the runway"); return; }
        Finish(0, "ok");
    }

    /// <summary>
    /// The flat world's ground under the runway, in the frame the craft is in now, put back after each shift. The flat
    /// world's Space frames turn only far from home (never an anchor frame on the way down); the earth world's frame turns
    /// everywhere (#1630), so there the plane is laid on the ellipsoid at the runway, its up the ellipsoid's.
    /// </summary>
    private void PutGround()
    {
        var frame = _origin.Frame;
        if (frame.IsAnchor && !EarthFrame || _groundIn != null && frame.SameFrame(_groundIn)) return;
        if (GetParent().GetNodeOrNull<Node3D>("Ground") is not { } ground) return;
        var runway = _pilot!.Plan.To;
        // the runway's middle at sea level in this frame, level: the turned Space frames turned it with the
        // world, and on edge a 20 km plane is no runway
        var (e, n) = runway.At(runway.Length * 0.5);
        var at = frame.ToWorld(e, n, 0);
        var basis = Basis.Identity;
        if (frame.IsAnchor)
        {
            var up = frame.UpAt(at);
            var north = frame.ToWorld(e, n + 100, 0) - at;
            var east = north.Cross(up).Normalized();
            basis = new Basis(east, up, east.Cross(up).Normalized());
        }
        ground.GlobalTransform = new Transform3D(basis, at - basis.Y * 0.5f);
        _groundIn = frame;
    }

    private bool EarthFrame => _origin.Frame.IsAnchor && OriginShifter.Instance?.Earth == true;

    private OriginFrame? _groundIn;

    private void Fail(string why) => Finish(1, "FAILED " + why);

    private void Finish(int code, string result)
    {
        if (_done) return;
        if (_player != null && _shuttle != null) Report(_player, _shuttle);
        Engine.TimeScale = 1.0;
        _done = true;
        GD.Print($"[shuttlecheck] RESULT: {result}");
        GetTree().Quit(code);
    }
}
