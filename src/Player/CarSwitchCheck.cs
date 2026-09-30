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

    private CarSwitchCheck(bool driver, Func<FootPlayer?> local, Func<Node?> players)
    {
        Name = "CarSwitchCheck";
        _driver = driver;
        _local = local;
        _players = players;
    }

    public static CarSwitchCheck? Create(Func<FootPlayer?> local, Func<Node?> players)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--switchcheck");
        if (i < 0) return null;
        bool driver = i + 1 < args.Length && args[i + 1] == "driver";
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
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--switchcheck");
            if (i + 2 < args.Length && !args[i + 2].StartsWith("--"))
                GetParent().GetNodeOrNull<Net.ChatManager>("Chat")?.Send($"/login {args[i + 2]}");
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
                _step++;
                break;
            case 2 when t > 6:
                GD.Print($"[switchcheck] driver: preset {CarSetups.For(Preset).Name}: {(me.SetCarSetup(Preset) ? "ok" : "REFUSED")}");
                var car = me.Visual as CarRig;
                GD.Print($"[switchcheck] driver: own car roof open {car?.RoofOpen}, lights {car?.Headlights}, preset {me.CarSetupId}, lift {Lift(car):F2}, doors {me.DoorsOpen}");
                _step++;
                break;
            case 3 when t > 12:
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
        }

        foreach (var node in VehicleManager.Instance?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (node is VehicleBody body && body.GetNodeOrNull<CarRig>("Visual") is { } parked)
            {
                int setup = body.Ride is Car c ? c.Spec.SetupId : -1;
                var now = (parked.RoofOpen, parked.Headlights, setup, MathF.Round(Lift(parked), 2), body.DoorsOpen);
                if (now != _seenParked) GD.Print($"[switchcheck] watch: parked car {body.Name}: roof open {now.Item1}, lights {now.Item2}, preset {now.Item3}, lift {now.Item4:F2}, doors {now.Item5}");
                _seenParked = now;
                _doorSeenOpen |= (body.DoorsOpen & CarRig.DriverDoor) != 0;
                // the garage doors (#56) with a preset on: out through the driver's door, which shuts behind
                if (_drivingOk && now.Item1 && now.Item2 && now.Item3 == Preset && now.Item4 > 0.1f && _doorSeenOpen && body.DoorsOpen == 0)
                {
                    Shot();
                    Finish(true, "saw it driven and parked with the top down, the lights on and the Rally-raid preset, its door opened and shut");
                }
            }
    }

    private void Shot()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--switchcheck");
        if (i + 2 >= args.Length || !args[i + 2].EndsWith(".png")) return;
        GD.Print($"[switchcheck] watch: wrote {args[i + 2]}: {GetViewport().GetTexture().GetImage().SavePng(args[i + 2])}");
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
