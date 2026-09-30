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
/// and L (lights, pop-ups up) through the real key actions, then gets out, leaving the car parked.
/// </para>
///
/// <para>
/// <c>--switchcheck watch</c> (windowed: a headless client draws no parked vehicles) looks at the
/// other player and prints what it sees change. Passes once it has seen the driver's car with its
/// top down and its lights on, and then the parked car still that way, as the spawn data carried
/// it; non-zero exit after two minutes otherwise.
/// </para>
/// </summary>
public partial class CarSwitchCheck : Node
{
    private readonly bool _driver;
    private readonly Func<FootPlayer?> _local;
    private readonly Func<Node?> _players;
    private double _t, _since = -1;
    private int _step;
    private (bool Roof, bool Lights)? _seenDriving, _seenParked;
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
        if (_since < 0) { _since = _t; GD.Print("[switchcheck] driver: the watcher is here"); }
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
                var car = me.Visual as CarRig;
                GD.Print($"[switchcheck] driver: own car roof open {car?.RoofOpen}, lights {car?.Headlights}");
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

    private void Watch()
    {
        if (Other()?.Visual is CarRig driving)
        {
            var now = (driving.RoofOpen, driving.Headlights);
            if (now != _seenDriving) GD.Print($"[switchcheck] watch: the other player's car: roof open {now.Item1}, lights {now.Item2}");
            _seenDriving = now;
            _drivingOk |= now is (true, true);
        }

        foreach (var node in VehicleManager.Instance?.GetChildren() ?? new Godot.Collections.Array<Node>())
            if (node is VehicleBody body && body.GetNodeOrNull<CarRig>("Visual") is { } parked)
            {
                var now = (parked.RoofOpen, parked.Headlights);
                if (now != _seenParked) GD.Print($"[switchcheck] watch: parked car {body.Name}: roof open {now.Item1}, lights {now.Item2}");
                _seenParked = now;
                if (_drivingOk && now is (true, true)) Finish(true, "saw it driven and parked with the top down and the lights on");
            }
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
