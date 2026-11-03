using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --flycheck wingsuit|glide|paraglider|heli|plane|pigeon|pigeoncrash|a320|freighter[,out.png] [--at E,N] [--world flat] [--airliner arcade|sim]</c>
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
    private float _walk, _peak, _hit;
    private double _splatAt = -1;

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
        if (_wait > (Heavy ? 900 : 150)) { GD.Print("[flycheck] TIMEOUT"); Finish(2); return; }

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
            // the crash sortie is meant to hit: it judges its own impact
            _player.Impacted += lost => { if (lost >= 8 && _kind != "pigeoncrash") _crashed = true; };
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
            case "pigeoncrash":
                p.SetRide(RideKind.Pigeon);
                break;
            case "ufo":
                p.SetRide(RideKind.Ufo);
                break;
            case "plane":
                p.SetRide(RideKind.Plane);
                p.DebugLaunch(p.GlobalPosition + Vector3.Up * 400f, new Vector3(0, 0, -50));
                break;
            case "thermal":
            case "pigeon-thermal":
            case "campfire":
                BeginLift(p);
                break;
            case "a320":
            case "freighter":
            case "an124":
                // a whole circuit on the real keys: take-off, climb, a 180° turn, approach, landing, stop (#414, #420)
                p.SetRide(_kind switch { "freighter" => RideKind.Freighter, "an124" => RideKind.An124, _ => RideKind.A320 });
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
                _circuit = new AirlinerCircuit(_kind);
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
            case "pigeoncrash":
                // #519: climb, then dive forward into the ground past CrashSpeed: a splat, back on foot, unhurt
                Hold(PlayerInput.Jump, t > 0.5 && t < 5);
                Hold(PlayerInput.CrouchSlide, t > 5);
                Hold(PlayerInput.MoveForward, t > 0.5);
                _peak = Mathf.Max(_peak, Agl(_player!.GlobalPosition));
                if (_player.Ride == RideKind.Pigeon) _hit = _player.Flight.Velocity.Length();
                else if (_splatAt < 0) _splatAt = t;
                // a moment later, so a picture catches the feathers
                if (_splatAt >= 0 && t > _splatAt + 0.35)
                {
                    int splats = Birds.BirdLife.Instance?.CrashSplats ?? -1;
                    GD.Print($"[flycheck] pigeon crashed at {_hit:F1} m/s from {_peak:F1} m agl: now {_player.Ride}, health {_player.Health:F0}, splats {splats}");
                    if (_player.Ride != RideKind.OnFoot || _hit < 20f || _player.Health < FootPlayer.MaxHealth || splats == 0) _crashed = true;
                    End("splat");
                }
                if (t > 30) { _crashed = true; End("never crashed"); }
                break;
            case "a320":
            case "freighter":
            case "an124":
                if (_player!.Vehicle is not Airliner jet) { _crashed = true; End("not in an airliner"); break; }
                if (_circuit!.Step(jet, _player, Agl(_player.GlobalPosition), (float)t, Hold) is { } how)
                {
                    if (!how.Ok) _crashed = true;
                    End(how.Text);
                }
                break;
            case "ufo":
                UfoSortie(t, Hold);
                break;
            case "thermal":
            case "pigeon-thermal":
            case "campfire":
                LiftSortie(t, Hold);
                break;
            case "plane":
                Hold(PlayerInput.Sprint, t < 3);                  // throttle lever up
                Hold(PlayerInput.MoveBack, t > 6 && t < 7.5);     // pull up
                Hold(PlayerInput.MoveRight, t > 10 && t < 11);    // roll into a turn
                if (t > 20) End("flying");
                break;
        }
    }

    // ---- lift (#1225): thermals, a campfire's updraft, a pigeon soaring -------------------------

    private Vector3 _liftAt;
    private float _liftFrom, _liftTop, _liftBestVy = float.NegativeInfinity, _glideFrom = float.NaN;
    private float _liftStrength;
    private const long CheckFireKey = 1_225_000_001;
    private static readonly float Stick = CmdArgs.Value("--liftstick") is { } v && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0.7f;

    /// <summary>
    /// The lift sorties start where the air rises: over the strongest thermal near the start (by the
    /// hour of <c>--time</c>, 13 by default), or over a lit campfire's updraft.
    /// </summary>
    private void BeginLift(FootPlayer p)
    {
        float hour = CmdArgs.Value("--time") is { } h && float.TryParse(h, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float hh) ? hh : 13f;
        World.AirLiftField.HourOverride = hour;
        World.AirLiftField.Use(_chunks, _origin);
        var ground = p.GlobalPosition;
        if (_kind == "campfire")
        {
            // a burning fire's updraft (AirLiftField's campfire numbers) 20 m ahead on the flat
            var fire = ground + new Vector3(0, -1f, -20f);
            var g = _origin.ToGlobal(fire);
            World.AirLift.Volumes.Set(CheckFireKey, g.E, g.N, g.Alt, World.AirLiftField.CampfireRadius,
                World.AirLiftField.CampfireTop, World.AirLiftField.CampfireSpeed);
            _liftAt = fire;
            p.SetRide(RideKind.Paraglider);
            p.DebugLaunch(fire + new Vector3(0, 8f, 14f), new Vector3(0, -1, -9));
            GD.Print($"[flycheck] campfire: an updraft 20 m ahead, the glider 8 m over it");
        }
        else
        {
            Span<World.Thermal> found = stackalloc World.Thermal[8];
            int n = World.AirLiftField.ThermalsNear(ground, 1500, found);
            if (n > 0)
            {
                _liftStrength = found[0].Strength;
                _liftAt = World.AirLiftField.ToWorld(found[0]) ?? ground;
            }
            else _liftAt = ground;
            GD.Print($"[flycheck] {_kind} at {hour:F1} h: {n} thermals within 1.5 km, the strongest {_liftStrength:F2} "
                     + $"{MathX.FlatDistance(_liftAt, ground):F0} m away");
            if (_kind == "thermal")
            {
                p.SetRide(RideKind.Paraglider);
                p.DebugLaunch(_liftAt + Vector3.Up * 150f, new Vector3(0, -1, -9));
            }
            else
            {
                p.SetRide(RideKind.Pigeon);
                p.GlobalPosition = _liftAt + Vector3.Up * 1f;
            }
        }
        _liftFrom = p.GlobalPosition.Y;
        _liftTop = _liftFrom;
    }

    private void LiftSortie(double t, System.Action<string, bool> hold)
    {
        var p = _player!;
        float y = p.GlobalPosition.Y;
        _liftTop = Mathf.Max(_liftTop, y);
        if ((int)(t * 60) % 600 == 0)
            GD.Print($"[flycheck]   t {t:F0} s: {y - _liftFrom:+0;-0} m, air {p.AirNow.Y:F1} m/s, {MathX.FlatDistance(p.GlobalPosition, _liftAt):F0} m from the core");
        switch (_kind)
        {
            case "thermal":
            {
                // circle in the core: 0.7 of the stick (a full one banks so hard the canopy sinks 2.5 m/s; 0.35 drifts out of the core)
                Input.ActionPress(PlayerInput.MoveRight, Stick);
                if (t < 90) return;
                float gain = y - _liftFrom;
                bool night = _liftStrength <= 0f;
                GD.Print($"[flycheck] thermal: {gain:F0} m gained in 90 s (air now {p.AirNow.Y:F1} m/s)");
                // the plan hoped for 150 m: a circling canopy sinks ~2.3 m/s, so a 3.5 m/s core nets ~1 m/s (96 m measured)
                if (night ? gain > 5f : gain < 80f) _crashed = true;
                End(night ? "no thermal at night" : "climbed in a thermal");
                break;
            }
            case "campfire":
            {
                // straight over the fire: the updraft carries the canopy up while it is in it
                _liftBestVy = Mathf.Max(_liftBestVy, p.Flight.Velocity.Y);
                if (t < 6) return;
                GD.Print($"[flycheck] campfire: best climb {_liftBestVy:F1} m/s over the fire, {_liftTop - _liftFrom:F1} m gained at most");
                if (_liftBestVy < 2f) _crashed = true;
                End("over a campfire");
                World.AirLift.Volumes.Remove(CheckFireKey);
                break;
            }
            default:   // pigeon-thermal: flap up 4 s circling, then glide on circling in the thermal, never flapping
            {
                hold(PlayerInput.Jump, t > 0.5 && t < 4.5);
                hold(PlayerInput.MoveRight, t > 0.5);   // circling from the take-off: stay in the core
                if (t > 6 && float.IsNaN(_glideFrom)) _glideFrom = y;
                if (t < 26) return;
                float held = y - _glideFrom;
                GD.Print($"[flycheck] pigeon-thermal: {held:+0.0;-0.0} m over 20 s of gliding (air {p.AirNow.Y:F1} m/s)");
                if (held < -1f || p.Ride != RideKind.Pigeon) _crashed = true;
                End("soared");
                break;
            }
        }
    }

    private float _ufoY, _ufoCruise, _ufoDash, _ufoLegsHigh = -1f, _ufoRampStart = -1f;
    private bool _ufoBeamSeen, _ufoPressed;

    /// <summary>A switch pressed as a key would be: an input event, which the player's handler reads (#861).</summary>
    private static void Tap(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    /// <summary>
    /// The saucer (#861): the ramp opens on the ground, climb (legs fold), cruise, boost, let go
    /// (it stops and holds its height), the beam on and off, descend and land on its legs (the
    /// ramp opens again). Each step checked against <see cref="UfoFlight"/>'s numbers.
    /// </summary>
    private void UfoSortie(double t, System.Action<string, bool> hold)
    {
        var p = _player!;
        if (p.Saucer is not { } ufo) { _crashed = true; End($"not in the saucer: {p.Ride}"); return; }
        var s = ufo.State;
        float flat = MathX.FlatLength(p.Flight.Velocity);
        void Check(bool ok, string what) { GD.Print($"[flycheck] ufo {(ok ? "ok" : "FAIL")}: {what}"); if (!ok) _crashed = true; }
        void Once(double at, System.Action act) { if (t >= at && t < at + 0.05 && !_ufoPressed) { _ufoPressed = true; act(); } else if (t >= at + 0.05 && t < at + 0.1) _ufoPressed = false; }

        Once(4.4, () => { _ufoRampStart = s.Ramp; Check(s.Landed && s.Ramp > 0.99f, $"landed, the ramp opened by itself ({s.Ramp:F2})"); });
        hold(PlayerInput.Jump, t > 4.5 && t < 7.5);
        Once(8, () => { _ufoY = p.GlobalPosition.Y; Check(s.Ramp < 0.01f && !s.Landed, $"lifted off, ramp in ({s.Ramp:F2}), {Agl(p.GlobalPosition):F0} m up"); });
        hold(PlayerInput.MoveForward, t > 8.3 && t < 15);
        hold(PlayerInput.Sprint, t > 11.2 && t < 15);
        Once(11, () => { _ufoCruise = flat; _ufoLegsHigh = s.Legs; Check(flat > UfoFlight.Cruise - 1f && flat < UfoFlight.Cruise + 1f, $"cruising {flat:F1} m/s"); });
        Once(14.7, () => { _ufoDash = flat; Check(flat > UfoFlight.Dash - 3f, $"boosted {flat:F1} m/s"); });
        Once(14.8, () => Check(_ufoLegsHigh < 0.01f, $"legs folded up high ({_ufoLegsHigh:F2})"));
        Once(19.5, () => Check(flat < 0.3f && Mathf.Abs(p.GlobalPosition.Y - _ufoY) < 1.5f,
            $"let go: stopped ({flat:F2} m/s), height held ({p.GlobalPosition.Y - _ufoY:+0.0;-0.0} m), tilt {s.Tilt.X:F3}"));
        // windowed with a picture: one braking (the wobble) and one with the beam on, beside it
        Once(16.2, () => Snap("braking"));
        Once(20, () => Tap(PlayerInput.TractorBeam));
        Once(21.4, () => Snap("beam"));
        if (t > 20.5 && t < 21.5 && s.Beam) _ufoBeamSeen = true;
        Once(22, () => Tap(PlayerInput.TractorBeam));
        Once(22.5, () => Check(_ufoBeamSeen && !s.Beam, "beam on, then off"));
        hold(PlayerInput.CrouchSlide, t > 23 && !s.Landed);
        if (t > 23 && s.Landed && s.Ramp > 0.99f)
        {
            Check(s.Legs > 0.99f, $"down on its legs ({s.Legs:F2}), ramp open, {Agl(p.GlobalPosition):F1} m agl");
            End("landed, ramp open");
        }
        if (t > 50) { _crashed = true; End("never landed"); }
    }

    /// <summary>A picture beside the end one, <c>&lt;name&gt;_&lt;what&gt;.png</c>, when the run asked for one.</summary>
    private void Snap(string what)
    {
        if (_shot == null) return;
        string path = _shot.Replace(".png", $"_{what}.png");
        if (GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok) GD.Print($"[flycheck] wrote {path}");
    }

    /// <summary>Wall-clock frame times, ms, while flying: what a windowed run costs (#421, the cockpit's screens with <c>--instruments off</c> to compare).</summary>
    private readonly List<float> _frameMs = new();
    private ulong _lastFrameUs;

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksUsec();
        if (_lastFrameUs != 0 && _player != null && !_done) _frameMs.Add((now - _lastFrameUs) / 1000f);
        _lastFrameUs = now;
    }

    private void End(string how)
    {
        if (_done) return;
        _done = true;
        if (_frameMs.Count > 10)
        {
            _frameMs.Sort();
            float sum = 0f;
            foreach (float f in _frameMs) sum += f;
            GD.Print($"[flycheck] frames {_frameMs.Count}: avg {sum / _frameMs.Count:F2} ms, p50 {_frameMs[_frameMs.Count / 2]:F2}, "
                + $"p95 {_frameMs[_frameMs.Count * 95 / 100]:F2}, max {_frameMs[^1]:F2} (time scale {Engine.TimeScale:F0})");
        }
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

    /// <summary>A heavy aircraft's whole circuit: a long sortie (#414, #420).</summary>
    private bool Heavy => _kind is "a320" or "freighter" or "an124";

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
