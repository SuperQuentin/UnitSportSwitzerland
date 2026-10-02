using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>tools/switchcheck.sh</c>: the car switches (#48) over a real connection. Two clients of one
/// dedicated server, on loopback:
///
/// <para>
/// <c>--switchcheck driver</c> gets in the NA6CE once another player is there, presses O (top down)
/// and L (lights, pop-ups up) through the real key actions, puts the Rally-raid preset on it (#40),
/// then gets out, leaving the car parked.
/// </para>
///
/// <para>
/// <c>--switchcheck watch</c> (windowed: a headless client draws no parked vehicles) looks at the
/// other player and prints what it sees change. Passes once it has seen the driver's car with its
/// top down and its lights on, and then the parked car still that way, as the spawn data carried
/// it — both times in the Rally-raid preset, raised on its big wheels; non-zero exit after two
/// minutes otherwise. <c>--switchcheck watch out.png</c> saves what the watcher sees at the end.
/// </para>
///
/// <para>
/// The cockpit (#69): the driver weaves the wheel while it sits in the car. The watcher must see a
/// driver in the other player's car, drawn whole (their view from the seat is theirs alone), with
/// the steering wheel turned by the replicated front-wheel angle times the steering ratio, and
/// turned well off centre at some point; it saves a close-up through the driver's window as
/// <c>out_cockpit.png</c>.
/// </para>
/// </summary>
public partial class CarSwitchCheck : Node
{
    private readonly bool _driver;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<Node?> _players;
    private double _t, _since = -1;
    private int _step;
    private (bool Roof, bool Lights, int Setup, float Lift, byte Doors)? _seenDriving, _seenParked;
    /// <summary>The parked car's driver door was seen open (getting out), so its shutting is a real check.</summary>
    private bool _doorSeenOpen;
    private static int Preset => CarSetups.Parse("rally-raid")!.Id;
    private bool _drivingOk;
    private bool _driverSeen;
    private float _turnSeen;
    private int _turnMismatch;
    private Camera3D? _shotCamera, _restore;
    private int _shotFrames = -1;

    private CarSwitchCheck(bool driver, Func<FootPlayer?> local, Func<Node?> players)
    {
        Name = "CarSwitchCheck";
        _driver = driver;
        _local = local;
        _players = players;
    }

    public static CarSwitchCheck? Create(Func<FootPlayer?> local, Func<Node?> players)
    {
        if (!CmdArgs.Has("--switchcheck")) return null;
        bool driver = CmdArgs.Value("--switchcheck") == "driver";
        GD.Print($"[switchcheck] role {(driver ? "driver" : "watch")}");
        return new CarSwitchCheck(driver, local, players);
    }

    private FootPlayer? Other()
    {
        var own = Multiplayer.GetUniqueId().ToString();
        foreach (var child in _players()?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (child is FootPlayer p && p.Name != own) return p;
        return null;
    }

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t > 120) Finish(false, "timed out");
        else if (_driver) Drive();
        else Watch();
    }

    private void Drive()
    {
        if (_local() is not { } me || Other() == null || !me.IsOnFloor()) return;
        if (_since < 0)
        {
            _since = _t;
            GD.Print("[switchcheck] driver: the watcher is here");
            // --switchcheck driver <password>: an admin, whose conjured car the server lets it park
            if (CmdArgs.Value("--switchcheck", 2, notFlag: true) is { } password)
                GetParent().GetNodeOrNull<Net.ChatManager>("Chat")?.Send($"/login {password}");
        }
        double t = _t - _since;

        switch (_step)
        {
            case 0 when t > 3:
                var kind = CarCatalog.All.First(c => c.Label == "NA6CE Roadster").Kind;
                GD.Print($"[switchcheck] driver: get in {kind}: {(me.SetRide(kind) ? "ok" : "REFUSED")}");
                _step++;
                break;
            case 1 when t > 5:
                Press(PlayerInput.RoofToggle);
                Press(PlayerInput.LightsToggle);
                // standing, the wheel from lock to lock: the watcher sees it turn, and the hands with it
                me.RideControls = () => new RideInput(0f, 0f, Mathf.Sin((float)_t * 1.5f), false);
                _step++;
                break;
            case 2 when t > 6:
                GD.Print($"[switchcheck] driver: preset {CarSetups.For(Preset).Name}: {(me.SetCarSetup(Preset) ? "ok" : "REFUSED")}");
                var car = me.Visual as CarRig;
                GD.Print($"[switchcheck] driver: own car roof open {car?.RoofOpen}, lights {car?.Headlights}, preset {me.CarSetupId}, lift {Lift(car):F2}, doors {me.DoorsOpen}");
                _step++;
                break;
            case 3 when t > 12:
                me.RideControls = null;
                GD.Print($"[switchcheck] driver: get out: {me.TryInteract()}");
                _step++;
                break;
            case 4 when t > 40:
                Finish(true, "driver done");
                break;
        }
    }

    private static void Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    /// <summary>How far the rig's body is raised over stock (0.5 m is its pivot): the preset, as drawn.</summary>
    private static float Lift(CarRig? rig) => (rig?.GetNodeOrNull<Node3D>("Body")?.Position.Y ?? 0.5f) - 0.5f;

    private void Watch()
    {
        if (Other() is { Visual: CarRig driving } other)
        {
            var now = (driving.RoofOpen, driving.Headlights, other.CarSetupId, MathF.Round(Lift(driving), 2), other.DoorsOpen);
            if (now != _seenDriving) GD.Print($"[switchcheck] watch: the other player's car: roof open {now.Item1}, lights {now.Item2}, preset {now.Item3}, lift {now.Item4:F2}, doors {now.Item5}");
            _seenDriving = now;
            _drivingOk |= now.Item1 && now.Item2 && now.Item3 == Preset && now.Item4 > 0.1f;
            WatchCockpit(other, driving);
        }
        TakeCockpitShot();

        foreach (var node in VehicleManager.Instance?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (node is VehicleBody body && body.GetNodeOrNull<CarRig>("Visual") is { } parked)
            {
                int setup = body.Ride is Car c ? c.Spec.SetupId : -1;
                var now = (parked.RoofOpen, parked.Headlights, setup, MathF.Round(Lift(parked), 2), body.DoorsOpen);
                if (now != _seenParked) GD.Print($"[switchcheck] watch: parked car {body.Name}: roof open {now.Item1}, lights {now.Item2}, preset {now.Item3}, lift {now.Item4:F2}, doors {now.Item5}");
                _seenParked = now;
                _doorSeenOpen |= (body.DoorsOpen & CarRig.DriverDoor) != 0;
                // the garage doors (#56) with a preset on: out through the driver's door, which shuts behind
                if (_drivingOk && now.Item1 && now.Item2 && now.Item3 == Preset && now.Item4 > 0.1f && _doorSeenOpen && body.DoorsOpen == 0
                    && _shotCamera == null)
                {
                    GD.Print($"[switchcheck] watch: cockpit: driver seen {_driverSeen}, wheel turned up to {Mathf.RadToDeg(_turnSeen):F0}°, "
                        + $"frames where it disagreed with the front wheels: {_turnMismatch} (must be 0)");
                    if (!_driverSeen || _turnSeen < 0.5f || _turnMismatch > 0)
                        Finish(false, "the driver or their steering wheel was not seen right");
                    Shot();
                    Finish(true, "saw it driven and parked with the top down, the lights on and the Rally-raid preset, its door opened and shut");
                }
            }
    }

    /// <summary>The other player's cockpit as this peer draws it: a whole driver, the wheel at the front wheels' angle times the ratio.</summary>
    private void WatchCockpit(FootPlayer other, CarRig rig)
    {
        _driverSeen |= rig.GetNodeOrNull<Node3D>("Body/Driver") is { Visible: true } && rig.GetNodeOrNull<Node3D>("Body/DriverHead") is { Visible: true };
        if (CarCatalog.For(other.Ride) is not { } spec) return;
        if (Mathf.Abs(rig.WheelTurn - rig.SteerAngle * spec.SteerRatio) > 1e-3f) _turnMismatch++;
        _turnSeen = Mathf.Max(_turnSeen, Mathf.Abs(rig.WheelTurn));
        if (_shotFrames < 0 && Mathf.Abs(rig.WheelTurn) > 1.2f && _shotCamera == null)
        {
            // from outside the driver's door, a little ahead, at eye height: the driver, the wheel, the hands
            var eye = rig.GlobalTransform * rig.EyeFrame.Origin;
            var basis = rig.GlobalTransform.Basis;
            _restore = GetViewport().GetCamera3D();
            _shotCamera = new Camera3D { Fov = 40, Near = 0.05f };
            GetTree().Root.AddChild(_shotCamera);
            _shotCamera.GlobalPosition = eye + basis.X * 2.4f - basis.Z * 0.9f + Vector3.Up * 0.3f;
            _shotCamera.LookAt(eye - Vector3.Up * 0.25f - basis.Z * 0.2f, Vector3.Up);
            _shotCamera.MakeCurrent();
            // our own figure is not in the way of the lens
            if (_local()?.Visual is { } own) own.Visible = false;
            _shotFrames = 0;
        }
    }

    /// <summary>A couple of frames after the close-up camera went in (it has to have drawn), save and put the view back.</summary>
    private void TakeCockpitShot()
    {
        if (_shotCamera == null || ++_shotFrames < 3) return;
        if (CmdArgs.Value("--switchcheck", 2) is { } png && png.EndsWith(".png"))
        {
            var path = png[..^4] + "_cockpit.png";
            GD.Print($"[switchcheck] watch: wrote {path}: {GetViewport().GetTexture().GetImage().SavePng(path)}");
        }
        _restore?.MakeCurrent();
        if (_local()?.Visual is { } own) own.Visible = true;
        _shotCamera.QueueFree();
        _shotCamera = null;
    }

    private void Shot()
    {
        if (CmdArgs.Value("--switchcheck", 2) is not { } png || !png.EndsWith(".png")) return;
        GD.Print($"[switchcheck] watch: wrote {png}: {GetViewport().GetTexture().GetImage().SavePng(png)}");
    }

    private void Finish(bool ok, string why)
    {
        if (!ok || !_driver)
            GD.Print($"[switchcheck] RESULT: {(ok ? "ok" : "FAILED")} ({why})");
        else GD.Print($"[switchcheck] {why}");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
