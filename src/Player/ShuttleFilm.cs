using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --resolution 1280x720 --fixed-fps 30 --path . -- --shuttlefilm --chunks &lt;Geneva's tiles&gt; --at 2496345,1120665
/// --clip-hq --nocapture</c> (#1497, <c>tools/shuttlefilm.sh</c>): the space shuttle's mission at Geneva, flown by
/// <see cref="ShuttlePilot"/> and filmed by <see cref="ClipRecorder"/> from the chase camera in five clips: the taxi
/// onto runway 04 and the take-off, the drive up to 400 km, the orbit and the retro burn, re-entry, and the
/// approach and landing on 22 over the lake. Kept over Europe (the user's choice): the drive goes 2,000 km out along
/// the take-off's heading and back, so the orbit, the burn and re-entry pass over Switzerland. Between the clips the
/// flight runs at SimClock's fastest. Writes <c>test_output/clips/shuttle-1..5.mp4</c>; the script cuts them into one.
/// </summary>
public partial class ShuttleFilm : Node
{
    public static bool Requested => CmdArgs.Has("--shuttlefilm");

    private readonly ChunkManager? _chunks;
    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private Airliner? _shuttle;
    private ShuttlePilot? _pilot;
    private double _wait, _stoppedFor;
    private bool _done, _landing;
    private string? _filming;
    private readonly HashSet<string> _filmed = new();

    /// <summary>
    /// Geneva's runway 04/22 (airports.json, OSM's centreline): 04's threshold at the south-west end, 3,583 m to
    /// the north-east one, heading 46.5°, 50 m wide.
    /// </summary>
    private const double SwE = 2496258.4, SwN = 1120489.3, NeE = 2498857.9, NeN = 1122955.1;
    private const float Length = 3583f, HalfWidth = 25f;
    private static readonly float Heading04 = (float)Math.Atan2(NeE - SwE, NeN - SwN);

    /// <summary>The field's height when its tiles are not there to say, m.</summary>
    private const double FieldElevation = 420;

    public ShuttleFilm(ChunkManager? chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    private double Ground(double e, double n) =>
        TestWorld.TryGround(_chunks, _origin.ToWorld(e, n, 0), out float y) ? _origin.Frame.AltitudeOf(new Vector3(0, y, 0)) : FieldElevation;

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _wait += delta;
        if (_pilot == null) { Spawn(); return; }
        var p = _player!;
        var jet = _shuttle!;
        double alt = _origin.Frame.AltitudeOf(p.GlobalPosition);
        float agl = TestWorld.TryGround(_chunks, p.GlobalPosition, out float g) && _chunks != null ? p.GlobalPosition.Y - g
            : (float)(alt - _pilot.Plan.To.Elevation);
        if (p.Vehicle != jet) { End(1, $"out of the shuttle at {_pilot.Now} (crashed?)"); return; }
        _pilot.Step(p, jet, _origin.Frame, agl, (float)delta);
        if (_pilot.Now == ShuttlePilot.Stage.Failed) { End(1, _pilot.Failure ?? "the pilot failed"); return; }
        if (_pilot.Now == ShuttlePilot.Stage.Stopped) _stoppedFor += delta;
        // the landing, once begun, is filmed to the end: the height over tiles still streaming in flickers
        _landing |= _pilot.Now == ShuttlePilot.Stage.Glide && agl < 1_500;
        Engine.TimeScale = Scale(_pilot.Now, alt, agl, jet.Heat);
        Film(Clip(_pilot.Now, alt, agl, jet.Heat));
        if (_stoppedFor > 4 && _filming == null && !ClipRecorder.Busy) End(0, "ok");
    }

    /// <summary>How fast the film runs each part: real time on the ground, faster in the air, fastest between the clips.</summary>
    private double Scale(ShuttlePilot.Stage stage, double alt, float agl, float heat) => stage switch
    {
        ShuttlePilot.Stage.Taxi or ShuttlePilot.Stage.LineUp or ShuttlePilot.Stage.Roll => 1,
        ShuttlePilot.Stage.Climb => alt < 4_000 ? 1 : 4,
        ShuttlePilot.Stage.DriveUp => 4,
        ShuttlePilot.Stage.Orbit => 2,
        ShuttlePilot.Stage.Entry => heat > 0.03f || alt < 120_000 ? 2 : ShuttlePilot.Fast,
        ShuttlePilot.Stage.Air => 2,
        ShuttlePilot.Stage.Glide => _landing ? 1 : ShuttlePilot.Fast,
        ShuttlePilot.Stage.Rollout or ShuttlePilot.Stage.Stopped => 1,
        _ => ShuttlePilot.Fast,
    };

    /// <summary>The clip this moment belongs in, or none.</summary>
    private string? Clip(ShuttlePilot.Stage stage, double alt, float agl, float heat) => stage switch
    {
        ShuttlePilot.Stage.Taxi or ShuttlePilot.Stage.LineUp or ShuttlePilot.Stage.Roll => "shuttle-1",
        ShuttlePilot.Stage.Climb when alt < 20_000 => "shuttle-1",
        ShuttlePilot.Stage.DriveUp => "shuttle-2",
        ShuttlePilot.Stage.Orbit or ShuttlePilot.Stage.Retro => "shuttle-3",
        ShuttlePilot.Stage.Entry when heat > 0.03f || alt < 120_000 => "shuttle-4",
        ShuttlePilot.Stage.Air => "shuttle-4",
        ShuttlePilot.Stage.Glide when _landing => "shuttle-5",
        ShuttlePilot.Stage.Rollout => "shuttle-5",
        ShuttlePilot.Stage.Stopped when _stoppedFor < 4 => "shuttle-5",
        _ => null,
    };

    /// <summary>Starts or cuts the clips: each filmed once, the next as soon as the last one has encoded.</summary>
    private void Film(string? want)
    {
        if (want == _filming) return;
        if (_filming != null)
        {
            ClipRecorder.Cut();
            GD.Print($"[shuttlefilm] cut {_filming} at {_pilot!.Now}");
            _filming = null;
        }
        if (want == null || _filmed.Contains(want) || ClipRecorder.Busy) return;
        if (ClipRecorder.Film(GetTree(), want) is { } error) { GD.PrintErr($"[shuttlefilm] {want}: {error}"); return; }
        GD.Print($"[shuttlefilm] filming {want} at {_pilot!.Now}");
        _filming = want;
        _filmed.Add(want);
    }

    private void Spawn()
    {
        if (_wait > 300) { End(2, "TIMEOUT waiting for the airfield's tiles"); return; }
        var r04 = new ShuttlePilot.Runway(SwE, SwN, Heading04, Length, HalfWidth, FieldElevation);
        // the taxi's start: 80 m right of 04's centreline, 150 m past its threshold, facing it
        var (se, sn) = r04.At(150, 80);
        var start = _origin.ToWorld(se, sn, 0);
        if (_player == null)
        {
            if (!TestWorld.TryGround(_chunks, start, out float ground)) return;
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(start.X, ground + 1f, start.Z);
            return;
        }
        if (_debugAt >= 0) { DebugOrbit(); return; }
        if (_chunks != null && !_chunks.HasCollisionAt(start) || !_player.IsOnFloor()) return;
        if (_shuttle == null)
        {
            if (!_player.SetRide(RideKind.Shuttle) || _player.Vehicle is not Airliner { Spec.Spacecraft: true } jet) { End(1, "no shuttle"); return; }
            _shuttle = jet;
            float heading = Heading04 - Mathf.Pi / 2;
            _player.Rotation = new Vector3(0, -heading, 0);
            jet.State.Yaw = -heading;
            jet.State.Attitude = new Basis(Vector3.Up, -heading);
            jet.State.Settle = 2f;
            _wait = 0;
            return;
        }
        // a few seconds for the tiles round it to come in before the camera rolls
        if (_wait < 5) return;
        if (CmdArgs.Value("--shuttlefilm", notFlag: true) is { } mode && mode.StartsWith("orbit")) { DebugOrbit(); return; }
        double sw = Ground(SwE, SwN), ne = Ground(NeE, NeN);
        var from = r04 with { Elevation = sw };
        var to = new ShuttlePilot.Runway(NeE, NeN, Heading04 + Mathf.Pi, Length, HalfWidth, ne);
        _pilot = new ShuttlePilot(new ShuttlePilot.Mission(from, to, Start: (se, sn, Heading04 - Mathf.Pi / 2),
            RoundTheEarth: false, CoastSeconds: 60, PerigeeKm: -3500, AirSeconds: 20, TurnOutKm: 2000))
        {
            Changed = (a, b) => GD.Print($"[shuttlefilm] {a} -> {b}"),
            Log = line => GD.Print("[shuttlefilm] " + line),
        };
        GD.Print(FormattableString.Invariant($"[shuttlefilm] 04 at {sw:F0} m, 22 at {ne:F0} m, heading {Mathf.RadToDeg(Heading04):F1}°: rolling"));
    }

    // ---- a picture in orbit: --shuttlefilm orbit[,km] ----

    private double _debugAt = -1;

    /// <summary>
    /// <c>--shuttlefilm orbit[,km]</c>: put on an orbit there (400 km by default) and one picture from the chase camera,
    /// <c>test_output/clips/shuttle-orbit-&lt;km&gt;.png</c>, with where the camera is and its planes (#1497: above 250 km
    /// nothing drew).
    /// </summary>
    private void DebugOrbit()
    {
        var p = _player!;
        if (_debugAt < 0)
        {
            var arg = CmdArgs.Value("--shuttlefilm", notFlag: true)!.Split(',');
            double km = arg.Length > 1 && double.TryParse(arg[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double k) ? k : 400;
            GD.Print("[shuttlefilm] " + p.PutInOrbit(km));
            p.FlyControls = () => new FlightInput(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f);
            _debugAt = _wait;
            return;
        }
        if (_wait - _debugAt < 4) return;
        var cam = GetViewport().GetCamera3D();
        int alt = (int)(_origin.Frame.AltitudeOf(p.GlobalPosition) / 1000);
        GD.Print(FormattableString.Invariant($"[shuttlefilm] {alt} km: body {p.GlobalPosition}, camera at {cam?.GlobalPosition}, near {cam?.Near}, far {cam?.Far}, anchor frame {_origin.Frame.IsAnchor}"));
        GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath($"res://test_output/clips/shuttle-orbit-{alt}.png"));
        End(0, "orbit");
    }

    private void End(int code, string result)
    {
        if (_done) return;
        if (_filming != null) ClipRecorder.Cut();
        _done = true;
        Engine.TimeScale = 1;
        GD.Print($"[shuttlefilm] RESULT: {(code == 0 ? "ok" : "FAILED " + result)}");
        // the last clip's files first
        _ = Quit(code);
    }

    private async System.Threading.Tasks.Task Quit(int code)
    {
        for (int i = 0; i < 600 && ClipRecorder.Busy; i++) await ToSignal(GetTree().CreateTimer(0.5, true, false, true), SceneTreeTimer.SignalName.Timeout);
        GetTree().Quit(code);
    }
}
