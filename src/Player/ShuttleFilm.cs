using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --resolution 1280x720 --fixed-fps 30 --path . -- --shuttlefilm --chunks &lt;Geneva's tiles&gt; --at 2496345,1120665
/// --clip-hq --nocapture</c> (#1497, <c>tools/shuttlefilm.sh</c>): the space shuttle's mission at Geneva, flown by
/// <see cref="ShuttlePilot"/> and filmed by <see cref="ClipRecorder"/> from the chase camera in four clips: the taxi
/// onto runway 04, the take-off and the way into space up to 400 km on the drive; the orbit and the retro burn;
/// re-entry; and the way down from space to the landing on 22 over the lake. Kept over Europe (the user's choice): the
/// drive goes 2,000 km out along the take-off's heading and back, so the orbit, the burn and re-entry pass over
/// Switzerland. Between the clips the flight runs at SimClock's fastest. Writes <c>test_output/clips/shuttle-1..4.mp4</c>;
/// the script cuts them into one.
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
    /// <summary>The runway's heading from true north at the threshold (#1504): what the shuttle is set down along.</summary>
    private static readonly float TrueHeading04 = (float)TrueNorth.GridToTrue(SwE, SwN, Heading04);

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
        if (!_landing && _pilot.Now == ShuttlePilot.Stage.Glide && agl < 1_500)
        {
            _landing = true;
            Mark("landing");
        }
        if (_shotStage is { } at && (_shotAt >= 0 || _pilot.Now == at && (Up(at) ? alt > _shotKm * 1000 : alt < _shotKm * 1000))) { DriveShot(alt); return; }
        Engine.TimeScale = Scale(_pilot.Now, alt, agl, jet.Heat);
        if (!_driveShot) Film(Clip(_pilot.Now, alt, agl, jet.Heat));
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
        // down from space: the sky turning blue again, then the long glide faster
        ShuttlePilot.Stage.Fall => 4,
        ShuttlePilot.Stage.Glide => _landing ? 1 : agl > 10_000 ? 8 : 4,
        ShuttlePilot.Stage.Rollout or ShuttlePilot.Stage.Stopped => 1,
        _ => ShuttlePilot.Fast,
    };

    /// <summary>
    /// The clip this moment belongs in, or none. The way into space and the way out of it are each one clip, so
    /// the sky turns black and blue again on film (a new clip waits for the last one's encoding: a cut there lost
    /// the climb from 20 to 60 km); the script captions their halves from the <c>mark</c> lines.
    /// </summary>
    private string? Clip(ShuttlePilot.Stage stage, double alt, float agl, float heat) => stage switch
    {
        ShuttlePilot.Stage.Taxi or ShuttlePilot.Stage.LineUp or ShuttlePilot.Stage.Roll
            or ShuttlePilot.Stage.Climb or ShuttlePilot.Stage.DriveUp => "shuttle-1",
        ShuttlePilot.Stage.Orbit or ShuttlePilot.Stage.Retro => "shuttle-2",
        ShuttlePilot.Stage.Entry when heat > 0.03f || alt < 120_000 => "shuttle-3",
        ShuttlePilot.Stage.Air => "shuttle-3",
        ShuttlePilot.Stage.Fall or ShuttlePilot.Stage.Glide or ShuttlePilot.Stage.Rollout => "shuttle-4",
        ShuttlePilot.Stage.Stopped when _stoppedFor < 4 => "shuttle-4",
        _ => null,
    };

    /// <summary>A moment the script captions inside a clip: the frame drawn now (ClipRecorder logs each clip's first).</summary>
    private static void Mark(string what) => GD.Print($"[shuttlefilm] mark {what} at frame {Engine.GetFramesDrawn()}");

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
            float yaw = TrueNorth.WorldYaw(_origin.Frame, start, Mathf.Pi / 2 - TrueHeading04);
            _player.Rotation = new Vector3(0, yaw, 0);
            jet.State.Yaw = yaw;
            jet.State.Attitude = new Basis(Vector3.Up, yaw);
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
            Changed = (a, b) =>
            {
                GD.Print($"[shuttlefilm] {a} -> {b}");
                if (b == ShuttlePilot.Stage.DriveUp) Mark("drive");
            },
            Log = line => GD.Print("[shuttlefilm] " + line),
        };
        GD.Print(FormattableString.Invariant($"[shuttlefilm] 04 at {sw:F0} m, 22 at {ne:F0} m, heading {Mathf.RadToDeg(Heading04):F1}°: rolling"));
        // ,skip (a picture on the way down): straight to where the drive home leaves it, 80 km out on 22's line,
        // 62 km up, 300 m/s toward the field, as --shuttlecheck glide does: minutes instead of the whole mission
        if (_shotStage != null && Array.IndexOf(CmdArgs.Value("--shuttlefilm", notFlag: true)!.Split(','), "skip") >= 0)
        {
            var (ee, en) = to.At(-_pilot.Plan.EntryOut, 0);
            float h = to.Heading;
            var forward = new Vector3(Mathf.Sin(h), 0, -Mathf.Cos(h));
            _player!.DebugLaunch(_origin.ToWorld(ee, en, 62_000), forward * 300f + Vector3.Down * 30f);
            _shuttle!.State.Yaw = -h;
            _shuttle.State.Attitude = new Basis(Vector3.Up, -h);
            _shuttle.State.Lever = _shuttle.State.Spool = 0f;
            _shuttle.State.Settle = 0f;
            _pilot.SkipToLanding();
        }
    }

    // ---- a picture on the drive up: --shuttlefilm drive[,hide=<type>...] ----

    private bool _driveShot => _shotStage != null;

    /// <summary>
    /// The stage the picture is taken in: <c>drive</c> (the drive up), or <c>shot,&lt;stage&gt;</c> (any of
    /// <see cref="ShuttlePilot.Stage"/>: past <c>at</c> going up in the climb and the drive up, under it in the others).
    /// </summary>
    private readonly ShuttlePilot.Stage? _shotStage = CmdArgs.Value("--shuttlefilm", notFlag: true)?.Split(',') is { } m
        ? m[0] == "drive" ? ShuttlePilot.Stage.DriveUp
        : m[0] == "shot" && m.Length > 1 && Enum.TryParse(m[1], true, out ShuttlePilot.Stage s) ? s : null
        : null;

    private static bool Up(ShuttlePilot.Stage s) => s is ShuttlePilot.Stage.Climb or ShuttlePilot.Stage.DriveUp;

    /// <summary>
    /// <c>--shuttlefilm drive[,hide=&lt;type&gt;...]</c>: the mission flown to 80 km on the drive up, then one picture
    /// (<c>test_output/clips/shuttle-drive[-&lt;types&gt;].png</c>) with those kinds of node hidden: what drew over the
    /// stars round the shuttle there (#1518)?
    /// </summary>
    private void DriveShot(double alt)
    {
        Engine.TimeScale = 1;
        if (_shotAt < 0)
        {
            var hidden = new List<string>();
            foreach (var a in CmdArgs.Value("--shuttlefilm", notFlag: true)!.Split(','))
                if (a.StartsWith("hide="))
                {
                    hidden.Add(a[5..]);
                    foreach (var n in GetTree().Root.FindChildren(a[5..], "", true, false))
                        if (n is Node3D n3) n3.Visible = false;
                        else if (n is CanvasItem c) c.Visible = false;
                }
            var args = CmdArgs.Value("--shuttlefilm", notFlag: true)!.Split(',');
            // ,sky: the space sky magenta (the atmosphere stopped, or it dims it back to black): a black thing or hidden stars?
            if (Array.IndexOf(args, "sky") >= 0 && GetViewport().FindWorld3D()?.Environment is { } env)
            {
                foreach (var n in GetTree().Root.FindChildren("Atmosphere", "", true, false)) n.ProcessMode = ProcessModeEnum.Disabled;
                env.BackgroundMode = Godot.Environment.BGMode.Color;
                env.BackgroundColor = new Color(0.45f, 0f, 0.45f);
                hidden.Add("sky");
            }
            // ,stardist=<share>: the stars nearer than usual (cropped by the far plane or the depth's precision?)
            if (Array.Find(args, a => a.StartsWith("stardist=")) is { } sd)
            {
                World.StarField.DistanceScale = float.Parse(sd[9..], System.Globalization.CultureInfo.InvariantCulture);
                hidden.Add(sd.Replace("=", ""));
            }
            // ,far: the camera's planes and the stars' distance in the log
            var lens = GetViewport().GetCamera3D();
            GD.Print(FormattableString.Invariant($"[shuttlefilm] camera near {lens?.Near}, far {lens?.Far}, at {lens?.GlobalPosition}, horizon {_chunks?.Horizon?.Progress()}, frame anchor {_origin.Frame.IsAnchor}"));
            // ,bigstars: every star 40 times its size, so whatever crops them shows its edge
            if (Array.IndexOf(args, "bigstars") >= 0)
            {
                foreach (var n in GetTree().Root.FindChildren("*", "", true, false))
                    if (n is World.StarField { MaterialOverride: ShaderMaterial stars }) stars.SetShaderParameter("size_scale", 40f);
                hidden.Add("bigstars");
            }
            _shotName = FormattableString.Invariant($"shuttle-{_shotStage!.Value.ToString().ToLowerInvariant()}-{_shotKm:F0}") + (hidden.Count == 0 ? "" : "-" + string.Join("-", hidden).Replace("*", ""));
            _shotAt = _wait;
            return;
        }
        if (_wait - _shotAt < 0.2) return;
        var lensAt = GetViewport().GetCamera3D()?.GlobalPosition;
        // where the air's shell thinks the camera is (World/Atmosphere's own sums): its height over the shell's
        // sphere against the frame's altitude; a misplaced shell clears a disc of air under the camera
        string air = "";
        if (lensAt is { } eye)
        {
            var axes = World.Globe.Axes ?? _origin.EcefAxes;
            var (px, py, pz) = axes.FromEcef(new Terrain.Format.EcefPos(0, 0, 0));
            var g = Terrain.Format.Ellipsoid.Wgs84.ToGeodetic(axes.ToEcef(eye.X, eye.Y, eye.Z));
            var nadir = Terrain.Format.CubeSphere.OnEllipsoid(Terrain.Format.CubeSphere.NormalAt(g.LatDeg, g.LonDeg), 0);
            double r = Math.Sqrt(Core.FrameAxes.Dot(nadir, nadir));
            double d = (eye - new Vector3((float)px, (float)py, (float)pz)).Length();
            air = FormattableString.Invariant($", air: over the sphere {(d - r) / 1000:F1} km against {_origin.Frame.AltitudeOf(eye) / 1000:F1} km, globe axes {World.Globe.Axes != null}");
        }
        GD.Print(FormattableString.Invariant($"[shuttlefilm] {alt / 1000:F1} km in {_pilot!.Now}: {_shotName}.png, horizon {_chunks?.Horizon?.Progress()}, {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draws, {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1e6:F1} M triangles, fill {RenderingServer.GlobalShaderParameterGet("world_fill_hide")}, flat {RenderingServer.GlobalShaderParameterGet("world_flat_hide")}, edge {RenderingServer.GlobalShaderParameterGet("world_flat_edge")} on {RenderingServer.GlobalShaderParameterGet("world_flat_edge_on")}, camera {lensAt}{air}"));
        GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath($"res://test_output/clips/{_shotName}.png"));
        End(0, "drive");
    }

    private double _shotAt = -1;

    /// <summary>,at=&lt;km&gt;: the picture's height on the drive up (80 by default).</summary>
    private readonly double _shotKm = Array.Find(CmdArgs.Value("--shuttlefilm", notFlag: true)?.Split(',') ?? Array.Empty<string>(), a => a.StartsWith("at=")) is { } at
        && double.TryParse(at[3..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double km) ? km : 80;
    private string _shotName = "";

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
        // ,heat: a copy of the plasma, white hot, for the picture
        if (Array.IndexOf(CmdArgs.Value("--shuttlefilm", notFlag: true)!.Split(','), "heat") >= 0
            && _player!.FindChild("Plasma", true, false) is MeshInstance3D plasma && plasma.GetParent().GetNodeOrNull("PlasmaDbg") == null)
        {
            var copy = (MeshInstance3D)plasma.Duplicate();
            copy.Name = "PlasmaDbg";
            copy.Visible = true;
            var mat = (ShaderMaterial)plasma.MaterialOverride.Duplicate();
            mat.SetShaderParameter("heat", 1f);
            copy.MaterialOverride = mat;
            plasma.GetParent().AddChild(copy);
            _debugAt = _wait - 3;
            return;
        }
        // ,hide=<type>: that kind of node hidden for the picture (what draws over the stars?)
        if (Array.Find(CmdArgs.Value("--shuttlefilm", notFlag: true)!.Split(','), a => a.StartsWith("hide=")) is { } hide)
            foreach (var n in GetTree().Root.FindChildren("*", hide[5..], true, false))
                if (n is Node3D n3) n3.Visible = false;
        var cam = GetViewport().GetCamera3D();
        int alt = (int)(_origin.Frame.AltitudeOf(p.GlobalPosition) / 1000);
        GD.Print(FormattableString.Invariant($"[shuttlefilm] {alt} km: body {p.GlobalPosition}, camera at {cam?.GlobalPosition}, near {cam?.Near}, far {cam?.Far}, anchor frame {_origin.Frame.IsAnchor}"));
        GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath($"res://test_output/clips/shuttle-orbit-{alt}{(CmdArgs.Value("--shuttlefilm", notFlag: true)!.Contains("hide=") ? "-hidden" : "")}.png"));
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
