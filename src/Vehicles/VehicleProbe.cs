using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Vehicles;

/// <summary>
/// <c>godot --path . -- --vehiclecheck[,out.png] [--at E,N]</c>
///
/// <para>
/// The vehicles-as-world-objects contract, checked end to end with a scripted player:
/// </para>
/// <list type="number">
/// <item>fly a helicopter up, <b>get out in mid-air</b>: the player keeps its momentum, the
/// wingsuit and then the canopy open on Jump, and the empty helicopter carries on alone, falls and
/// <b>explodes</b>, leaving a wreck;</item>
/// <item>ride a bike, get off: it <b>stays parked</b>; walk back to it and <b>get back in</b>;</item>
/// <item>fly a plane, <b>engine off</b>: it glides, no thrust; engine on: thrust returns;</item>
/// <item><b>crash</b> the plane with the player in it: a wreck, the player thrown clear and hurt but
/// alive.</item>
/// </list>
/// <para>Prints what happened and exits non-zero if any of it did not.</para>
/// </summary>
public partial class VehicleProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shot;
    private FootPlayer? _player;
    private int _step;
    private double _t, _total, _report;
    private bool _ok = true;
    private VehicleBody? _tracked;
    private float _mark;
    private int _wrecksBefore;

    public VehicleProbe(ChunkManager chunks, WorldOrigin origin, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--vehiclecheck"))
            {
                var parts = a.Split(',');
                return (true, parts.Length > 1 ? parts[1] : null);
            }
        return (false, null);
    }

    private VehicleManager Vehicles => VehicleManager.Instance!;
    private IEnumerable<VehicleBody> All => Vehicles.GetChildren().OfType<VehicleBody>();
    private float Agl(Vector3 p) => _chunks.TryGetHeight(p, out float g) ? p.Y - g : float.NaN;

    private void Check(bool condition, string what)
    {
        GD.Print($"[vehicle] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    private void Next(string title)
    {
        _step++;
        _t = 0;
        GD.Print($"[vehicle] --- step {_step}: {title}");
    }

    private static void Hold(string action, bool on)
    {
        if (on) Input.ActionPress(action); else Input.ActionRelease(action);
    }

    private bool _pulse;

    /// <summary>
    /// A jump press the player's polling sees: held for one physics step, released the next.
    /// <see cref="Tap"/> presses and releases inside one call, which an event handler catches but
    /// a controller that polls <c>IsActionPressed</c> never does.
    /// </summary>
    private void Pulse(string action, bool want)
    {
        _pulse = want && !_pulse;
        Hold(action, _pulse);
    }

    private static void Tap(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        _total += delta;
        if (_total > 400) { Check(false, $"timed out in step {_step}"); Finish(); return; }

        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);
        var p = _player;

        _report += delta;
        if (p != null && _report >= 1.0)
        {
            _report = 0;
            string veh = _tracked != null && IsInstanceValid(_tracked)
                ? $"  tracked {_tracked.Kind} agl {Agl(_tracked.GlobalPosition):F0} v {_tracked.Velocity.Length():F1} wrecked={_tracked.Wrecked}"
                : "";
            GD.Print($"[vehicle] t={_t,5:F1} {p.Ride,-10} agl {Agl(p.GlobalPosition),5:F0}  v {p.GroundSpeed,5:F1}  hp {p.Health,3:F0}{veh}");
        }

        switch (_step)
        {
            case 0:
                if (p == null)
                {
                    if (!_chunks.TryGetHeight(at, out float g)) return;
                    _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
                    AddChild(_player);
                    _player.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);
                    return;
                }
                if (!_chunks.HasCollisionAt(at) || !p.IsOnFloor()) return;
                Check(p.SetRide(RideKind.Helicopter), "mounted a helicopter");
                Next("climb");
                break;

            case 1:
                Hold(PlayerInput.Jump, true);
                if (Agl(p.GlobalPosition) > 150 || _t > 30)
                {
                    Hold(PlayerInput.Jump, false);
                    Check(Agl(p.GlobalPosition) > 100, $"helicopter climbed ({Agl(p.GlobalPosition):F0} m)");
                    Next("get out in mid-air");
                }
                break;

            case 2:
                if (_t < 1) return;
                p.TryInteract();
                _tracked = All.FirstOrDefault(v => v.Kind == RideKind.Helicopter);
                Check(p.Ride == RideKind.OnFoot, "player is on foot after getting out");
                Check(_tracked != null, "the helicopter stayed in the world");
                Check(!p.IsOnFloor(), "player is in the air");
                Next("fall, wingsuit, canopy; the helicopter falls on its own");
                break;

            case 3:
                // Jump once falling: the base-jump deploy; again two seconds later: the canopy
                if (p.Ride == RideKind.Wingsuit && _mark == 0) { _mark = (float)_t; GD.Print("[vehicle] wingsuit open"); }
                Pulse(PlayerInput.Jump, (p.Ride == RideKind.OnFoot && p.Velocity.Y < -4f && _mark == 0 && _t < 10)
                    || (p.Ride == RideKind.Wingsuit && _t - _mark > 2.5));
                if (p.Ride == RideKind.Parachute && _mark > 0) { _mark = -1; GD.Print("[vehicle] canopy open"); }

                bool wrecked = _tracked != null && IsInstanceValid(_tracked) && _tracked.Wrecked;
                bool landed = p.Ride == RideKind.OnFoot && p.IsOnFloor() && _mark < 0;
                if ((wrecked && landed) || _t > 80)
                {
                    Check(_mark < 0, "wingsuit then canopy opened");
                    Check(wrecked, "the empty helicopter crashed and exploded");
                    Check(p.Health > 0, $"player alive after landing (hp {p.Health:F0})");
                    Next("bike: ride, park, walk away, get back in");
                    _mark = 0;
                }
                break;

            case 4:
                // back to the spawn point: wherever the canopy came down may be too steep to ride
                if (_mark == 0 && _t < 0.1 && _chunks.TryGetHeight(at, out float g4))
                    p.GlobalPosition = new Vector3(at.X, g4 + 0.5f, at.Z);
                if (!p.IsOnFloor() || p.GroundSpeed > 1f) return;
                if (_t > 3 && p.Ride == RideKind.OnFoot && _mark == 0)
                {
                    Check(p.SetRide(RideKind.RoadBike), "mounted a bike");
                    p.RideControls = () => new RideInput(1f, 0f, 0f, false);
                    _mark = 1;
                    _t = 0;
                }
                break;

            case 5:
                break;
        }

        if (_step == 4 && _mark == 1 && _t > 3)
        {
            p!.RideControls = null;
            p.TryInteract();
            _tracked = All.FirstOrDefault(v => v.Kind == RideKind.RoadBike);
            Check(p.Ride == RideKind.OnFoot && _tracked != null, "got off and the bike is parked in the world");
            _mark = 2;
            _t = 0;
        }
        else if (_step == 4 && _mark == 2 && _t > 4)
        {
            bool stayed = _tracked != null && IsInstanceValid(_tracked);
            Check(stayed, "the bike is still there 4 s later");
            if (stayed)
            {
                // walk back to it: stand beside it and press E
                p!.GlobalPosition = _tracked!.GlobalPosition + Vector3.Right * 1.5f + Vector3.Up * 0.3f;
                p.Velocity = Vector3.Zero;
            }
            _mark = 3;
            _t = 0;
        }
        else if (_step == 4 && _mark == 3 && _t > 1)
        {
            GD.Print($"[vehicle] bike at {_tracked!.GlobalPosition}, player at {p!.GlobalPosition}, "
                + $"nearest {(Vehicles.Nearest(p.GlobalPosition, 3.5f)?.Name ?? "none")}");
            p.TryInteract();
            Check(p.Ride == RideKind.RoadBike, "got back on the parked bike");
            Check(!All.Any(v => v.Kind == RideKind.RoadBike && !v.IsQueuedForDeletion()), "the parked bike left the world when ridden");
            p.TryInteract();
            _mark = 4;
            _t = 0;
        }
        else if (_step == 4 && _mark == 4 && _t > 1)
        {
            Next("plane: engine off glides, engine on pulls");
            p!.TryInteract();   // stand next to the bike again: this gets back on, so step off
            if (p.Ride != RideKind.OnFoot) p.TryInteract();
            _mark = 0;
        }
        else if (_step == 5) StepPlane(p!);
        else if (_step == 6) StepCrash(p!);
    }

    private float _speedA;

    private int _groundExit;
    private int _frameCount;
    private readonly RideKind[] _groundKinds = { RideKind.Helicopter, RideKind.Plane };

    /// <summary>
    /// Getting out of a helicopter and a plane standing on the ground, and out of a plane in
    /// flight: none of these may blow the vehicle up. (They did: reported from play, where most
    /// exits are on the ground — the mid-air exit above never showed it.)
    /// </summary>
    private bool StepGroundExits(FootPlayer p)
    {
        if (_groundExit >= _groundKinds.Length * 2) return true;
        var kind = _groundKinds[_groundExit / 2];
        if (_groundExit % 2 == 0)
        {
            if (!p.IsOnFloor() || p.Ride != RideKind.OnFoot || _t < 1) return false;
            p.SetRide(kind);
            _mark = -1;
            _t = 0;
            _groundExit++;
            return false;
        }
        if (_mark == -1 && _t > 2)
        {
            p.TryInteract();
            _tracked = All.Where(v => v.Kind == kind && !v.Wrecked).OrderBy(v => v.GlobalPosition.DistanceTo(p.GlobalPosition)).FirstOrDefault();
            _mark = -2;
            _t = 0;
        }
        if (_mark == -2 && _t > 3)
        {
            Check(_tracked != null && IsInstanceValid(_tracked) && !_tracked.Wrecked,
                $"left a {kind} on the ground and it did not explode"
                + (_tracked == null ? " (none tracked: " + string.Join(",", All.Select(v => $"{v.Kind}/{v.Wrecked}")) + ")" : ""));
            // step away from it for the next one
            p.GlobalPosition += Vector3.Right * 14f + Vector3.Up * 1f;
            _mark = 0;
            _t = 0;
            _groundExit++;
        }
        return false;
    }

    private void StepPlane(FootPlayer p)
    {
        if (!StepGroundExits(p)) return;
        if (_mark == 0)
        {
            if (!p.IsOnFloor() || p.Ride != RideKind.OnFoot) return;
            Check(p.SetRide(RideKind.Plane), "mounted a plane");
            // along the nose, whichever way the player happens to be facing by now
            var nose = -p.GlobalTransform.Basis.Z with { Y = 0 };
            p.DebugLaunch(p.GlobalPosition + Vector3.Up * 400f, nose.Normalized() * 50f);
            Tap(PlayerInput.EngineToggle);
            _mark = 1;
            _t = 0;
            return;
        }
        if (_mark == 1 && _t > 1)
        {
            // the key is an input event, handled on the next frame, not inside Tap
            Check(!p.EngineOn, "engine switched off");
            _speedA = p.GroundSpeed;
            _mark = 2;
            _t = 0;
        }
        if (_mark == 2 && _t > 6)
        {
            Check(p.Flight.Spool < 0.2f, $"engine spooled down (spool {p.Flight.Spool:F2})");
            Check(p.GroundSpeed < _speedA - 1f || p.Flight.Velocity.Y < -1f,
                $"no thrust: speed {_speedA:F1} -> {p.GroundSpeed:F1} m/s, sink {p.Flight.Velocity.Y:F1}");
            Tap(PlayerInput.EngineToggle);
            _speedA = p.GroundSpeed;
            _mark = 3;
            _t = 0;
        }
        Hold(PlayerInput.Sprint, _mark == 3 && _t < 3);
        if (_mark == 3 && _t > 6)
        {
            Check(p.EngineOn, "engine switched back on");
            Check(p.Flight.Spool > 0.7f, $"engine spooled up (spool {p.Flight.Spool:F2})");
            p.TryInteract();
            _tracked = All.FirstOrDefault(v => v.Kind == RideKind.Plane && !v.Wrecked && v.GlobalPosition.DistanceTo(p.GlobalPosition) < 30);
            Check(p.Ride == RideKind.OnFoot && _tracked != null, "jumped out of the plane in flight");
            _mark = 4;
            _t = 0;
        }
        if (_mark == 4 && _t > 2)
        {
            Check(_tracked != null && IsInstanceValid(_tracked) && !_tracked.Wrecked,
                "the abandoned plane flies on instead of exploding");
            // back into a plane on the ground for the crash test
            if (_chunks.TryGetHeight(p.GlobalPosition, out float g)) p.GlobalPosition = p.GlobalPosition with { Y = g + 0.5f };
            p.RequestReplacement();   // the teleport path, which forgives the fall in progress
            _mark = 5;
            _t = 0;
        }
        if (_mark == 5 && _t > 2 && p.IsOnFloor())
        {
            p.SetRide(RideKind.Plane);
            Next("crash the plane with the player in it");
            _wrecksBefore = All.Count(v => v.Wrecked);
            _mark = 0;
        }
    }

    private void StepCrash(FootPlayer p)
    {
        if (_mark == 0)
        {
            if (_t < 1.5) return;   // past the mount's settling second, or the crash is not counted
            // straight at the ground from 30 m
            // up first, and the dive a step later: a plane still touching the ground runs its
            // rolling branch once more, and that levels whatever attitude it was given
            p.DebugLaunch(p.GlobalPosition with { Y = p.GlobalPosition.Y - Agl(p.GlobalPosition) + 30f }, Vector3.Zero);
            _mark = 6;
            return;
        }
        if (_mark == 6)
        {
            var nose = (-p.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
            p.DebugLaunch(p.GlobalPosition, nose * 35f + Vector3.Down * 35f);
            _mark = 1;
            _t = 0;
            return;
        }
        if (_mark == 1 && (p.Ride == RideKind.OnFoot || _t > 10))
        {
            _mark = 2;
            _t = 0;
        }
        if (_mark == 2 && _t > 0.4 && _t < 0.45 && _shot != null)
        {
            var blast = GetViewport().GetTexture().GetImage();
            string path = _shot.Replace(".png", "_blast.png");
            if (blast.SavePng(path) == Error.Ok) GD.Print($"[vehicle] wrote {path}");
        }
        if (_mark == 2 && _t > 1.5)
        {
            Check(p.Ride == RideKind.OnFoot, "thrown clear of the plane");
            Check(All.Count(v => v.Wrecked) > _wrecksBefore, "the plane is a wreck in the world");
            Check(p.Health < FootPlayer.MaxHealth, $"the blast hurt the player (hp {p.Health:F0})");
            Check(p.Health > 0, "and the player survived it");
            if (_shot != null)
            {
                var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(_shot) == Error.Ok) GD.Print($"[vehicle] wrote {_shot}");
            }
            Finish();
        }
    }

    private void Finish()
    {
        foreach (var a in new[] { PlayerInput.Jump, PlayerInput.Sprint })
            Input.ActionRelease(a);
        GD.Print(_ok ? "[vehicle] RESULT: ok" : "[vehicle] RESULT: FAILED");
        SetPhysicsProcess(false);
        GetTree().Quit(_ok ? 0 : 1);
    }
}
