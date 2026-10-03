using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --flycheck wingsuit|glide|paraglider|heli|plane|pigeon|a320[,out.png] [--at E,N] [--world flat] [--airliner arcade|sim]</c>
///
/// <para>
/// Flies one craft through a scripted sortie with the real input actions and prints what the
/// model did — speed, sink, glide ratio, climb, how it ended — every second. Flight is the one
/// thing no screenshot can judge: a glide ratio, a stall or a canopy that never lands are all
/// numbers. Exits non-zero if the craft crashed or the pilot ended below the terrain.
/// </para>
/// </summary>
public partial class FlightCheckProbe : Node
{
    /// <summary>Null on <c>--world flat</c> (TestWorld): the ground is the plane.</summary>
    private readonly ChunkManager? _chunks;
    private readonly WorldOrigin _origin;
    private readonly string _kind;
    private readonly string? _shot;
    private FootPlayer? _player;
    private double _t, _report, _wait;
    private bool _started, _crashed, _done;
    /// <summary>Where the sortie started, kept in LV95: the origin may move under it (#185).</summary>
    private GlobalPos _from;
    private string _last = "";
    private float _walk, _peak;

    public FlightCheckProbe(ChunkManager? chunks, WorldOrigin origin, string kind, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _kind = kind;
        _shot = shot;
    }

    public static (string Kind, string? Shot)? ParseArgs()
    {
        if (CmdArgs.Value("--flycheck") is not { } value) return null;
        var parts = value.Split(',');
        return (parts[0].ToLowerInvariant(), parts.Length > 1 ? parts[1] : null);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _wait += delta;
        if (_wait > (_kind == "a320" ? 900 : 150)) { GD.Print("[flycheck] TIMEOUT"); Finish(2); return; }

        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);

        if (_player == null)
        {
            if (!TestWorld.TryGround(_chunks, at, out float g)) return;
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);
            if (_chunks == null) _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);   // flat world: no terrain to wait for
            _player.Announced += (text, good) => { _last = text; GD.Print($"[flycheck] announce: {text}"); };
            _player.Impacted += lost => { if (lost >= 8) _crashed = true; };
            return;
        }
        if (!_started)
        {
            if (_chunks != null && !_chunks.HasCollisionAt(at) || !_player.IsOnFloor()) return;
            Begin();
            _started = true;
            return;
        }

        _t += delta;
        Script(_t);

        _report += delta;
        if (_report >= 1.0)
        {
            _report = 0;
            var p = _player.GlobalPosition;
            var v = _player.IsFlying ? _player.Flight.Velocity : _player.Velocity;
            float flat = new Vector2(v.X, v.Z).Length();
            GD.Print($"[flycheck] t={_t,5:F1} {_player.Ride,-10} speed {v.Length() * 3.6f,5:F0} km/h  "
                + $"horiz {flat,5:F1}  vert {v.Y,+6:F1} m/s  glide {(v.Y < -0.3f ? flat / -v.Y : 0),4:F1}  "
                + $"agl {Agl(p),6:F0} m");
        }
    }

    private float Agl(Vector3 p) => TestWorld.TryGround(_chunks, p, out float g) ? p.Y - g : float.NaN;

    /// <summary>Puts the pilot where the sortie starts.</summary>
    private void Begin()
    {
        var p = _player!;
        switch (_kind)
        {
            case "wingsuit":
            case "glide":
                // dropped off an imaginary cliff: 450 m of air under the feet
                p.GlobalPosition += Vector3.Up * 450f;
                break;
            case "paraglider":
                p.SetRide(RideKind.Paraglider);
                p.DebugLaunch(p.GlobalPosition + Vector3.Up * 300f, new Vector3(0, -1, -10));
                break;
            case "heli":
                p.SetRide(RideKind.Helicopter);
                break;
            case "pigeon":
                p.SetRide(RideKind.Pigeon);
                break;
            case "plane":
                p.SetRide(RideKind.Plane);
                p.DebugLaunch(p.GlobalPosition + Vector3.Up * 400f, new Vector3(0, 0, -50));
                break;
            case "a320":
                // a whole circuit on the real keys: take-off, climb, a 180° turn, approach, landing, stop (#414)
                p.SetRide(RideKind.A320);
                // --heading deg (true, 0 north, 90 east): lined up on a real runway (GVA 05 is 46°)
                if (CmdArgs.Value("--heading") is { } h && float.TryParse(h, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float deg)
                    && p.Vehicle is Airliner lined)
                {
                    float yaw = -Mathf.DegToRad(deg);
                    p.Rotation = new Vector3(0, yaw, 0);
                    lined.State.Yaw = yaw;
                    lined.State.Attitude = new Basis(Vector3.Up, yaw);
                }
                Engine.TimeScale = 4.0;
                _circuit = new AirlinerCircuit();
                // windowed with a picture: one per phase, <name>_<phase>.png beside it
                if (_shot != null)
                    _circuit.Snap = phase =>
                    {
                        string path = _shot.Replace(".png", $"_{phase}.png");
                        if (GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok) GD.Print($"[flycheck] wrote {path}");
                    };
                break;
        }
        _from = _origin.ToGlobal(p.GlobalPosition);
        GD.Print($"[flycheck] {_kind}: start agl {Agl(p.GlobalPosition):F0} m");
    }

    /// <summary>The sortie: which buttons are held when.</summary>
    private void Script(double t)
    {
        void Hold(string action, bool on) { if (on) Input.ActionPress(action); else Input.ActionRelease(action); }

        switch (_kind)
        {
            case "wingsuit":
                // fall a second, deploy, glide, dive a little, open the canopy, land
                Hold(PlayerInput.Jump, (t > 1.0 && t < 1.1) || (t > 16.0 && t < 16.1));
                Hold(PlayerInput.MoveForward, t > 9 && t < 11);
                if (t > 17 && _player!.Ride == RideKind.OnFoot) End("landed on foot");
                if (t > 120) End("still airborne");
                break;
            case "glide":
                // the wingsuit sortie cut off mid-flight, for a picture of it flying
                Hold(PlayerInput.Jump, t > 1.0 && t < 1.1);
                Hold(PlayerInput.MoveRight, t > 11 && t < 12.5);
                if (t > 13) End("gliding");
                break;
            case "paraglider":
                Hold(PlayerInput.MoveRight, t > 12 && t < 16);   // a turn
                if (t > 25) End("glided");
                break;
            case "heli":
                Hold(PlayerInput.Jump, t > 1 && t < 5);           // climb ~36 m
                Hold(PlayerInput.MoveForward, t > 6 && t < 16);   // cruise
                if (t > 22) End("hovering");
                break;
            case "pigeon":
                // walk, take off and climb flapping, glide, dive, let go to land (#217)
                Hold(PlayerInput.MoveForward, t < 2);
                Hold(PlayerInput.Jump, t > 2.5 && t < 7);
                Hold(PlayerInput.CrouchSlide, t > 10 && t < 10.6);
                if (t > 1.8 && t < 1.9) _walk = MathX.FlatLength(_player!.Flight.Velocity);
                _peak = Mathf.Max(_peak, Agl(_player!.GlobalPosition));
                if (t > 11 && _player.IsOnFloor() && _player.Ride == RideKind.Pigeon && Pigeon.ModeOf(_player.Flight) == PigeonFlight.Mode.Ground)
                {
                    // in VR (--xrsim) the camera is the bird's eye, level: no roll on the head
                    var cam = _player.Camera.GlobalTransform;
                    float eye = cam.Origin.DistanceTo(_player.GlobalPosition), roll = Mathf.Abs(cam.Basis.X.Y);
                    GD.Print($"[flycheck] pigeon walked {_walk:F2} m/s, peak {_peak:F1} m agl, camera {eye:F2} m from the body, roll {roll:F3}{(XR.XrSession.Active ? " (VR)" : "")}");
                    if (_walk is < 0.8f or > 1.5f || _peak < 8f || XR.XrSession.Active && (eye > 0.5f || roll > 0.01f)) _crashed = true;
                    End("landed, walking");
                }
                if (t > 40) End("still airborne");
                break;
            case "a320":
                if (_player!.Vehicle is not Airliner jet) { _crashed = true; End("not in an airliner"); break; }
                if (_circuit!.Step(jet, _player, Agl(_player.GlobalPosition), (float)t, Hold) is { } how)
                {
                    if (!how.Ok) _crashed = true;
                    End(how.Text);
                }
                break;
            case "plane":
                Hold(PlayerInput.Sprint, t < 3);                  // throttle lever up
                Hold(PlayerInput.MoveBack, t > 6 && t < 7.5);     // pull up
                Hold(PlayerInput.MoveRight, t > 10 && t < 11);    // roll into a turn
                if (t > 20) End("flying");
                break;
        }
    }

    private void End(string how)
    {
        if (_done) return;
        _done = true;
        var p = _player!.GlobalPosition;
        float dist = (float)_origin.ToGlobal(p).HorizontalDistanceTo(_from);
        float drop = (float)_from.Alt - p.Y;
        bool under = TestWorld.TryGround(_chunks, p, out float g) && p.Y < g - 1.5f;
        GD.Print($"[flycheck] END {how}: {dist:F0} m flown, {drop:F0} m lost"
            + (drop > 1 ? $", overall glide {dist / drop:F1}" : "") + $", now {_player.Ride}");
        bool ok = !_crashed && !under;
        GD.Print(ok ? "[flycheck] RESULT: ok" : $"[flycheck] RESULT: FAILED ({(under ? "under terrain" : "crashed")})");

        if (_shot != null)
        {
            var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(_shot) == Error.Ok) GD.Print($"[flycheck] wrote {_shot}");
        }
        Finish(ok ? 0 : 1);
    }

    private AirlinerCircuit? _circuit;

    private void Finish(int code)
    {
        Engine.TimeScale = 1.0;
        foreach (var a in new[] { PlayerInput.Jump, PlayerInput.MoveForward, PlayerInput.MoveBack,
                     PlayerInput.MoveRight, PlayerInput.MoveLeft, PlayerInput.Sprint, PlayerInput.CrouchSlide })
            Input.ActionRelease(a);
        _done = true;
        GetTree().Quit(code);
    }
}
