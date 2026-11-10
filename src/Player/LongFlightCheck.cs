using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --frame earth --airliner sim --longflightcheck [--at E,N] [--tolatlon lat,lon] [--cruise m]
/// [--flightwarp x] [--flightkm km]</c> (#1710, G2 D0; plan <c>docs/plans/global-tiling-g2-d.md</c> section 5, the flight
/// Geneva → Brest): an A320 put in the air 3 km over the start, on its own autopilot and autothrust
/// (<see cref="AirlinerFlight"/>'s, light sim), climbing to the cruise (10 km) and steered toward Brest's airport
/// (48.4479 N, 4.4185 W) along the great circle, the heading knob turned every second; 220 km out it descends to
/// 1,500 m and the time warp ends. Over the pyramid (<c>cube_global</c>) all the way, it fails on:
/// <list type="bullet">
/// <item>the ground missing under the aircraft (no globe tile or leaf drawn there, <see cref="World.Globe.DrawnLevelAt"/>)
/// for 3 s, or the pyramid holding no height there;</item>
/// <item>the far view missing: one of 8 points round it at 70 % of the horizon (at most 150 km) drawn by nothing for
/// 10 s;</item>
/// <item>under 300 m over the pyramid's ground, a crash, or out of the aircraft;</item>
/// <item>a jump at a re-anchor (<see cref="ReanchorWatch"/>: the aircraft and the chase camera; not the ground, km under it);</item>
/// <item>with the destination's leaves (<c>--leaves</c> on Brest's): none drawn under it once down at 1,500 m.</item>
/// </list>
/// <c>--flightkm</c> stops after that many km (a shorter run); <c>--flightwarp</c> (4) is the engine's time scale for
/// the cruise. <c>tools/longflightcheck.sh</c> runs the whole flight. RESULT ok or FAILED.
/// </summary>
public partial class LongFlightCheck : Node
{
    public static bool Requested() => CmdArgs.Has("--longflightcheck");

    private const float StartAltM = 3000f, DescendM = 1500f, DescendFromKm = 220f, ClearanceM = 300f;
    private const double HoleS = 3, FarHoleS = 10;
    private readonly WorldOrigin _origin;
    private readonly IWorldGround? _ground;
    private readonly List<string> _faults = new();
    private readonly float _cruise = CmdArgs.Float("--cruise") ?? 10_000f;
    private readonly double _warp = CmdArgs.Double("--flightwarp") ?? 4.0;
    private readonly double? _stopKm = CmdArgs.Double("--flightkm");
    private GlobalPos _to;
    private FootPlayer? _player;
    private Airliner? _jet;
    private Camera3D _camera = null!;
    private ReanchorWatch _watch = null!;
    private PyramidGround? _pyramid;
    private Terrain.Leaves.LeafStore? _real;

    private static CubeKey LeafUnder(GlobalPos p) { var g = p.Geodetic; return Terrain.Leaves.LeafFrames.LeafAt(g.LatDeg, g.LonDeg); }
    private int _phase, _samples, _farSamples, _farMissed, _leafSeen = -1;
    private double _t, _steer, _check, _holeS, _worstHoleS, _worstFarHoleS, _lowestAgl = double.MaxValue, _flown, _startKm;
    private readonly double[] _farHoleS = new double[8];
    private GlobalPos? _last;
    private bool _descending, _apOn;
    private ulong _startMs;
    private readonly Dictionary<int, int> _levels = new();

    public LongFlightCheck(IWorldGround? ground, WorldOrigin origin)
    {
        _ground = ground;
        _origin = origin;
        Name = "LongFlightCheck";
    }

    private double Now => (Time.GetTicksMsec() - _startMs) / 1000.0;

    public override void _Ready()
    {
        _startMs = Time.GetTicksMsec();
        double lat = 48.4479, lon = -4.4185;
        if (CmdArgs.Value("--tolatlon")?.Split(',') is [var a, var b]
            && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double la)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo))
            (lat, lon) = (la, lo);
        _to = GlobalPos.FromEcef(Ellipsoid.Wgs84.ToEcef(new Geodetic(lat, lon, 0)));
        if (Core.GameSettings.Current.Airliner != AirlinerHandling.Sim)
        {
            Core.GameSettings.Current.Airliner = AirlinerHandling.Sim;   // the autopilot is the light sim's (a test profile: never saved)
            Log("the airliner set to light sim: its autopilot");
        }
        var (e, n) = SpawnPoint.ParseTarget();
        AddChild(_camera = new Camera3D { Name = "Chase", Far = 600_000, Current = true });
        _camera.MakeCurrent();
        _camera.GlobalPosition = _origin.ToWorld(e, n, StartAltM);
        _watch = new ReanchorWatch(_origin, _ground, "longflight") { Shots = DisplayServer.GetName() != "headless", JumpTolM = 0.5f };
        AddChild(_watch);
        _startKm = GlobalPos.FromLv95(e, n, 0).HorizontalDistanceTo(_to) / 1000;
        Log(Inv($"start LV95 {e:F0}/{n:F0} at {StartAltM:F0} m, to {lat:F4},{lon:F4}: {_startKm:F0} km, cruise {_cruise:F0} m, warp {_warp:F1}x, origin {(_origin.Frame.IsAnchor ? "an anchor frame" : "FLAT")}"));
        if (!_origin.Frame.IsAnchor) Fault("the world is not an anchor frame: run with --frame earth");
        // the destination's own leaves (--leaves): the end needs one of them drawn under it, not a generated fill leaf
        _real = Terrain.Leaves.LeafStore.Open(Core.TerrainPaths.FindLeafDir());
        Log($"{_real.LocalKeys.Count} real leaves in {Core.TerrainPaths.FindLeafDir()}; the destination's leaf {(_real.IsLocal(LeafUnder(_to)) ? "among them" : "NOT among them")}");
        if (World.Globe.DataDir is { } dir) _pyramid = new PyramidGround(dir);
        else Fault("no globe pyramid (cube_global, --globe)");
    }

    public override void _ExitTree() => Engine.TimeScale = 1.0;

    // the whole flight at the warp, the descent at 1x, the start
    private double TimeoutS => 300 + (_stopKm ?? _startKm) * 1000 / 220 / _warp + 900;

    public override void _PhysicsProcess(double delta)
    {
        if (_phase < 0) return;
        if (!_camera.Current) _camera.MakeCurrent();
        if (Now > TimeoutS) { Fault(Inv($"timed out after {_flown / 1000:F0} km")); Finish(); return; }
        if (_faults.Count > 0 && _phase == 0) { Finish(); return; }
        switch (_phase)
        {
            case 0: Wait(); break;
            case 1: Launch(); break;
            case 2: Fly(delta); break;
        }
    }

    // the globe under the start, drawn
    private void Wait()
    {
        if (World.Globe.Current is not { } globe) { if (Now > 60) { Fault("no globe in the world"); Finish(); } return; }
        if (globe.DrawnLevelAt(_origin.ToEcef(_camera.GlobalPosition)) < 0) return;
        Log($"the globe draws under the start at {Now:F1} s ({globe.PatchCount} tiles)");
        _phase = 1;
    }

    // a pilot boards on a floor only (SetRide): a platform under it at the start's altitude, gone once aboard
    private StaticBody3D? _platform;

    private void Launch()
    {
        if (_player == null)
        {
            _platform = new StaticBody3D { Name = "Platform" };
            _platform.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60, 1, 60) } });
            AddChild(_platform);
            _platform.GlobalPosition = _camera.GlobalPosition + Vector3.Down * 0.5f;
            _player = new FootPlayer { Name = "Pilot", Terrain = _ground };
            AddChild(_player);
            _player.GlobalPosition = _camera.GlobalPosition + Vector3.Up * 1.2f;
            _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);   // placed by hand: no terrain to wait for
            return;
        }
        var p = _player;
        if (!p.IsOnFloor()) { if (Now > 90) { Fault("the pilot never stood on the platform"); Finish(); } return; }
        if (!p.SetRide(RideKind.A320) || p.Vehicle is not Airliner jet) { Fault("could not board an A320"); Finish(); return; }
        _platform!.QueueFree();
        _platform = null;
        _jet = jet;
        float yaw = YawToward(p.GlobalPosition);
        p.Rotation = new Vector3(0, yaw, 0);
        jet.State.Yaw = yaw;
        // in the light sim an airliner boards cold (Parked with no engines): put in the air with every engine lit
        jet.State.Battery = true;
        jet.State.Starting = true;
        jet.State.Lit = jet.Spec.Engines;
        p.DebugLaunch(p.GlobalPosition + Vector3.Up * 5f, AirlinerFlight.Heading(yaw) * 150f);
        _ground?.AddAnchor(p, collision: true);
        _watch.Watch("aircraft", () => _player);
        _watch.Watch("camera", () => _camera);
        // not the ground: kilometres under it (the fill's at Geneva moved 3.3 cm at a re-anchor, #1710); the drive and
        // --reanchorcheck ask it on the region's leaves
        _last = _origin.ToGlobal(p.GlobalPosition);
        Engine.TimeScale = _warp;
        _phase = 2;
        Log("in the air, 150 m/s toward the destination");
    }

    // the world yaw toward the destination: the chord's level part at the aircraft is the great circle's start
    private float YawToward(Vector3 at)
    {
        var d = _origin.ToWorld(_to) - at;
        var up = _origin.UpAt(at);
        var flat = d - up * d.Dot(up);
        return Mathf.Atan2(-flat.X, -flat.Z);
    }

    private void Fly(double delta)
    {
        var p = _player!;
        var jet = _jet!;
        _t += delta;
        var here = _origin.ToGlobal(p.GlobalPosition);
        if (_last is { } last) _flown += here.HorizontalDistanceTo(last);
        _last = here;
        double toGo = here.HorizontalDistanceTo(_to);
        Chase(p, jet);
        if (p.Vehicle != jet) { Fault(Inv($"out of the aircraft at {_flown / 1000:F0} km")); Finish(); return; }

        // the autopilot on after 2 s of flight, then the knobs: heading every second, the altitudes
        if (!_apOn && _t > 2)
        {
            jet.Command(AirlinerCommand.Autopilot);
            _apOn = true;
        }
        if (_apOn && jet.State.Autopilot)
        {
            _steer += delta;
            if (_steer >= 1)
            {
                _steer = 0;
                jet.State.ApHeading = Mathf.Wrap(YawToward(p.GlobalPosition), -Mathf.Pi, Mathf.Pi);
                jet.State.ApSpeed = Mathf.Min(140f, jet.Spec.Vmo - 15f);
                jet.State.ApAltitude = _descending ? DescendM : _cruise;
            }
        }
        else if (_apOn && _t > 6) { Fault(Inv($"the autopilot is off at {_flown / 1000:F0} km")); Finish(); return; }

        if (!_descending && toGo < DescendFromKm * 1000)
        {
            _descending = true;
            Log(Inv($"{toGo / 1000:F0} km out: descending to {DescendM:F0} m, warp off"));
        }
        // the warp ends on the way down: the leaves under it stream at 1x
        if (_descending && Engine.TimeScale != 1.0 && here.Geodetic.H < DescendM + 2500) Engine.TimeScale = 1.0;

        _check += delta / Engine.TimeScale;   // real seconds: the streaming's clock
        if (_check >= 0.5) { Check(here, _check); _check = 0; }

        double alt = here.Geodetic.H;
        bool down = _descending && Mathf.Abs((float)(alt - DescendM)) < 150 && toGo < 30_000;
        if (_stopKm is { } km && _flown / 1000 >= km) { Log(Inv($"{km:F0} km flown: stopping (--flightkm)")); Finish(); return; }
        if (down && _t > 0)
        {
            // over the destination, low: one of its real leaves drawn under it (a generated fill leaf does not count)
            if (_real?.IsLocal(LeafUnder(here)) == true && World.Globe.Current?.DrawnLevelAt(here.Ecef) == World.Globe.LeafLevelDrawn)
            {
                _leafSeen = 1;
                Log(Inv($"the destination's leaves are drawn under the aircraft, {toGo / 1000:F1} km out at {alt:F0} m"));
                Finish();
                return;
            }
            if (toGo < 3000) { _leafSeen = 0; Finish(); return; }
        }
    }

    private void Check(GlobalPos here, double dt)
    {
        var globe = World.Globe.Current;
        if (globe == null) { Fault("the globe left the world"); Finish(); return; }
        _samples++;
        int level = globe.DrawnLevelAt(here.Ecef);
        _levels[level] = _levels.GetValueOrDefault(level) + 1;
        if (level < 0)
        {
            _holeS += dt;
            _worstHoleS = Math.Max(_worstHoleS, _holeS);
            if (_holeS > HoleS && _faults.Count < 20) { Fault(Inv($"no ground drawn under the aircraft for {_holeS:F0} s at {_flown / 1000:F0} km")); _holeS = double.NegativeInfinity; }
        }
        else _holeS = 0;

        // the ground's height under it, from the pyramid: the clearance
        double alt = here.Geodetic.H;
        if (_pyramid?.HeightAt(here.Ecef) is double g)
        {
            double agl = alt - g;
            _lowestAgl = Math.Min(_lowestAgl, agl);
            if (agl < ClearanceM && _faults.Count < 20) Fault(Inv($"{agl:F0} m over the ground at {_flown / 1000:F0} km"));
        }
        else if (_faults.Count < 20) Fault(Inv($"the pyramid holds no height under the aircraft at {_flown / 1000:F0} km"));

        // the far view: 8 points round it at 70 % of the horizon, at most 150 km
        double horizon = Math.Sqrt(2 * 6_371_000.0 * Math.Max(alt, 10) + alt * alt);
        double r = Math.Min(150_000, 0.7 * horizon);
        for (int k = 0; k < 8; k++)
        {
            double a = k * Math.PI / 4;
            var far = here.Moved(r * Math.Sin(a), r * Math.Cos(a)).Ecef;
            _farSamples++;
            if (globe.DrawnLevelAt(far) >= 0) { _farHoleS[k] = 0; continue; }
            _farMissed++;
            _farHoleS[k] += dt;
            _worstFarHoleS = Math.Max(_worstFarHoleS, _farHoleS[k]);
            if (_farHoleS[k] > FarHoleS && _faults.Count < 20)
            {
                Fault(Inv($"the far view missing {r / 1000:F0} km out at bearing {k * 45}° for {_farHoleS[k]:F0} s, at {_flown / 1000:F0} km"));
                _farHoleS[k] = double.NegativeInfinity;
            }
        }
        if (_samples % 40 == 0)
            Log(Inv($"t={_t,6:F0} s {_flown / 1000,6:F0} km, {here.HorizontalDistanceTo(_to) / 1000:F0} km to go, {alt:F0} m ({alt - (_pyramid?.HeightAt(here.Ecef) ?? 0):F0} m over the ground), {_player!.Flight.Velocity.Length():F0} m/s, globe level {level} under it, {globe.PatchCount} tiles, {_watch.Shifts} re-anchors"));
    }

    // behind and above the aircraft: the origin follows this camera
    private void Chase(FootPlayer p, Airliner jet)
    {
        var fwd = AirlinerFlight.Heading(jet.State.Yaw);
        _camera.GlobalPosition = p.GlobalPosition - fwd * 60f + Vector3.Up * 15f;
        _camera.LookAt(p.GlobalPosition, Vector3.Up);
    }

    private void Finish()
    {
        if (_phase < 0) return;
        _phase = -1;
        Engine.TimeScale = 1.0;
        _watch.Judge();
        var lv = new List<string>();
        foreach (var (k, v) in _levels) lv.Add(Inv($"{(k == World.Globe.LeafLevelDrawn ? "leaf" : k.ToString(CultureInfo.InvariantCulture))}:{v}"));
        Log(Inv($"flew {_flown / 1000:F0} km in {_t:F0} s of game time; {_samples} checks, the ground under it by level {string.Join(" ", lv)}; worst hole {_worstHoleS:F1} s; far view {_farSamples - _farMissed} of {_farSamples} points drawn, worst gap {_worstFarHoleS:F1} s; lowest {(_lowestAgl < double.MaxValue ? _lowestAgl : double.NaN):F0} m over the ground"));
        Log(_watch.Summary().Replace("[longflight] ", ""));
        foreach (var f in _watch.Faults) Fault(f);
        if (_leafSeen == 0) Fault("no real leaf of the destination drawn under the aircraft over it (its leaves missing, or not in --leaves)");
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

    private static void Log(string s) => GD.Print("[longflightcheck] " + s);
}
