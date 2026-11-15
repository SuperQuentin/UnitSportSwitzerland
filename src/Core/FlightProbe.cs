using Godot;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Streaming smoothness check: flies the spectator camera in a straight line at a fixed speed
/// and prints the frame-time distribution, then quits.
///
///   godot --path . -- --fly x,y,z,yawDeg,speedMps,seconds [--flyinput [boost]] [--flyshot file.png]
///
/// <para>
/// <c>--flyinput</c> (#1796) flies the camera by its own controls instead: the speed asked for is set one notch under
/// <c>speedMps</c>, a <c>spectator_faster</c> press brings it there (and shows the readout), forward is held (and boost
/// with <c>boost</c>), so the altitude's cap applies; the report adds the speed flown. <c>--flyshot</c> saves the
/// window 1 s into the flight, the speed readout showing.
/// </para>
/// <para>
/// The thing this measures cannot be seen in a screenshot and is not what the settle report
/// says either: a world that loads completely can still hitch every time a ring boundary
/// crosses a tile. Frames over 20 and 33 ms are counted separately because a p99 of 12 ms
/// hides a dozen 80 ms stalls that a player feels one by one.
/// </para>
/// </summary>
public partial class FlightProbe : Node
{
    private readonly SpectatorCamera _camera;
    private readonly ChunkManager _chunks;
    private readonly Vector3 _velocity;
    private readonly double _warmup;
    private readonly double _seconds;
    private readonly List<double> _frames = new();
    private double _elapsed;
    private bool _done;
    // the origin's re-anchors during the flight: how many, and what they cost (#1796: a fast flight is a shift storm first)
    private int _shiftsSeen = -1, _shifts;
    private double _shiftMs, _shiftMaxMs;

    /// <summary>The earth world's streaming state, printed at the end (null on the flat world).</summary>
    public Func<string?>? Status { get; init; }

    private readonly bool _byInput = CmdArgs.Has("--flyinput"), _boost = CmdArgs.Value("--flyinput") == "boost";
    private readonly string? _shot = CmdArgs.Value("--flyshot");
    private readonly float _askedSpeed;
    // --flyterminator (#1798): the globe's sun (world_true_sun) taken back to the Earth through the axes its tiles are
    // placed in, every frame: how far the terminator moved on the ground between frames, and how far it is from the
    // ephemeris' sun. A sun left in the scene's axes across a re-anchor moves with the globe, 20 km a turn in Space.
    private readonly bool _terminator = CmdArgs.Has("--flyterminator");
    // --flyterminator FILE.csv: every frame's offset off the ephemeris, for a chart (a probe's own file, test_output/)
    private readonly System.Text.StringBuilder? _sunCsv = CmdArgs.Value("--flyterminator") is { } f && f.EndsWith(".csv") ? new("t_s,off_km\n") : null;
    private EcefPos _sunLast, _truthLast;
    private bool _sunHave;
    private int _sunFrames, _sunJumps;
    private double _sunStepWorstKm, _sunOffWorstKm, _sunOwnWorstKm;

    /// <summary>The terminator may move this far on the ground between two frames, or be this far off the ephemeris, km.</summary>
    public const double TerminatorStepKm = 0.5, TerminatorOffKm = 2;

    private double _flown, _startAltitude = double.NaN;
    private bool _started, _shotTaken;

    public FlightProbe(SpectatorCamera camera, ChunkManager chunks, Vector3 start, float yawDeg,
        float speed, double seconds)
    {
        _camera = camera;
        _chunks = chunks;
        _seconds = seconds;
        _warmup = Math.Min(4, seconds / 3);
        camera.Position = start;
        camera.Rotation = new Vector3(Mathf.DegToRad(-6), Mathf.DegToRad(yawDeg), 0);
        var forward = -camera.GlobalTransform.Basis.Z with { Y = 0 };
        _velocity = forward.Normalized() * speed;
        _askedSpeed = speed;
        // the terminator is read once the frame's shift, globe and atmosphere have run: last of the frame
        if (_terminator) ProcessPriority = int.MaxValue;
    }

    /// <summary>The globe's sun this frame against the last one and the ephemeris, on the ground (<c>--flyterminator</c>).</summary>
    private void WatchTerminator()
    {
        if (World.Globe.Axes is not { } axes || World.DayNight.Instance?.Sky is not { } sky) return;
        var w = World.Atmosphere.TrueSunWritten;
        if (w == Vector3.Zero) return;
        // world axes to the Earth: the axes' columns are the world's X, Y, Z as Earth directions
        var sun = Unit(new EcefPos(
            axes.X.X * w.X + axes.Y.X * w.Y + axes.Z.X * w.Z,
            axes.X.Y * w.X + axes.Y.Y * w.Y + axes.Z.Y * w.Z,
            axes.X.Z * w.X + axes.Y.Z * w.Y + axes.Z.Z * w.Z));
        var truth = Unit(Ephemeris.Ecef(SkyBody.Sun, sky.Moment));
        double r = Ellipsoid.Wgs84.A / 1000;
        double off = Angle(sun, truth) * r;
        _sunOffWorstKm = Math.Max(_sunOffWorstKm, off);
        _sunCsv?.Append(System.Globalization.CultureInfo.InvariantCulture, $"{_elapsed - _warmup:F3},{off:F3}\n");
        if (_sunHave)
        {
            // past the sun's own motion: the game's day runs faster than the wall clock, kilometres a frame
            double step = Math.Abs(Angle(sun, _sunLast) - Angle(truth, _truthLast)) * r;
            _sunStepWorstKm = Math.Max(_sunStepWorstKm, step);
            _sunOwnWorstKm = Math.Max(_sunOwnWorstKm, Angle(truth, _truthLast) * r);
            if (step > TerminatorStepKm) _sunJumps++;
        }
        (_sunLast, _truthLast, _sunHave) = (sun, truth, true);
        _sunFrames++;
    }

    private static EcefPos Unit(EcefPos v)
    {
        double l = v.Length;
        return new EcefPos(v.X / l, v.Y / l, v.Z / l);
    }

    private static double Angle(EcefPos a, EcefPos b) =>
        Math.Acos(Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1, 1));

    /// <summary>Parses "--fly x,y,z,yaw,speed,seconds".</summary>
    public static string[]? ParseArgs() => CmdArgs.Value("--fly")?.Split(',') is { Length: 6 } parts ? parts : null;

    public override void _Process(double delta)
    {
        if (_done) return;
        _camera.Current = true;
        _elapsed += delta;

        // let the first arrival settle before moving, or the report is the teleport, not the flight
        if (_elapsed < _warmup) return;
        if (_terminator) WatchTerminator();
        if (!_byInput) _camera.Position += _velocity * (float)delta;
        else if (!_started)
        {
            _started = true;
            _camera.Speed = _askedSpeed / Player.SpectatorSpeed.Notch;
            Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.SpectatorFaster, Pressed = true });
            Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.SpectatorFaster, Pressed = false });
            Input.ActionPress(PlayerInput.MoveForward);
            if (_boost) Input.ActionPress(PlayerInput.FlyBoost);
        }
        else _flown += _camera.FlownSpeed * (_boost ? _camera.BoostMultiplier : 1f) * delta;
        if (double.IsNaN(_startAltitude)) _startAltitude = OriginShifter.Instance?.Origin.AltitudeOf(_camera.Position) ?? _camera.Position.Y;
        if (_shot != null && !_shotTaken && _elapsed > _warmup + 1.0)
        {
            _shotTaken = true;
            GD.Print($"[fly] wrote {_shot}: {GetViewport().GetTexture().GetImage().SavePng(_shot)}");
        }
        _frames.Add(delta * 1000);
        if (OriginShifter.Instance is { } shifter)
        {
            if (_shiftsSeen >= 0 && shifter.ShiftCount != _shiftsSeen)
            {
                _shifts += shifter.ShiftCount - _shiftsSeen;
                _shiftMs += shifter.LastShiftMs;
                _shiftMaxMs = Math.Max(_shiftMaxMs, shifter.LastShiftMs);
            }
            _shiftsSeen = shifter.ShiftCount;
        }

        if (_elapsed < _warmup + _seconds) return;
        _done = true;

        _frames.Sort();
        int n = _frames.Count;
        int over20 = _frames.Count(f => f > 20), over33 = _frames.Count(f => f > 33);
        double worst = _frames[^1];
        GD.Print($"[fly] {n} frames over {_seconds:F0} s at {_velocity.Length():F0} m/s: "
            + $"p50 {_frames[n / 2]:F1} ms, p95 {_frames[(int)(n * 0.95)]:F1}, p99 {_frames[(int)(n * 0.99)]:F1}, "
            + $"max {worst:F1}; >20 ms: {over20}, >33 ms: {over33}");
        var shifterNow = OriginShifter.Instance;
        double altitude = shifterNow?.Origin.AltitudeOf(_camera.Position) ?? _camera.Position.Y;
        GD.Print($"[fly] origin shifts {_shifts} ({_shifts / _seconds:F1}/s), {_shiftMs:F0} ms in all, worst {_shiftMaxMs:F1} ms; "
            + $"ends {altitude / 1000:F1} km up, {_camera.Position.Length():F0} m from the origin");
        if (_byInput)
            GD.Print($"[fly] by its controls: asked {_camera.Speed:F0} m/s{(_boost ? ", boosted" : "")}, flown {_flown / 1000:F0} km "
                + $"({_flown / _seconds:F0} m/s on average), from {_startAltitude / 1000:F1} km up");
        if (Status?.Invoke() is { } status) GD.Print($"[fly] streaming: {status}");
        GD.Print($"[fly] {_chunks.SettleReport(_camera.GlobalPosition)}");
        GD.Print($"[fly] worker time: {_chunks.BuildTimeReport()}");
        GD.Print($"[fly] mem={Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:F0}MB "
            + $"prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)}");
        if (_terminator)
        {
            if (_sunCsv != null) System.IO.File.WriteAllText(CmdArgs.Value("--flyterminator")!, _sunCsv.ToString());
            bool held = _sunFrames > 0 && _sunJumps == 0 && _sunOffWorstKm <= TerminatorOffKm;
            GD.Print($"RESULT {(held ? "PASS" : "FAIL")} flyterminator: {_sunFrames} frames, the terminator moved up to "
                + $"{_sunStepWorstKm:F2} km on the ground between frames past the sun's own motion (up to {_sunOwnWorstKm:F2} km at the "
                + $"game's day speed; {_sunJumps} over {TerminatorStepKm} km), "
                + $"up to {_sunOffWorstKm:F2} km off the ephemeris (limit {TerminatorOffKm} km)");
            GetTree().Quit(held ? 0 : 1);
            return;
        }
        // a stall a player can feel is a failure, not a statistic
        GetTree().Quit(over33 > 0 ? 1 : 0);
    }
}
