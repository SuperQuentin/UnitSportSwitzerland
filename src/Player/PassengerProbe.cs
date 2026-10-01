using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--passengernet a|b|c [password]</c> on three clients of a loopback server (a with the server's
/// <c>--admin-password</c>): passengers (#158), checked on every peer.
///
/// <list type="bullet">
/// <item><b>a</b> takes a four-seat car and waits for two passengers, drives off, and at speed
/// jumps out: then watches the car roll on without it;</item>
/// <item><b>b</b> walks up and gets in first; when a jumps out the car is b's, driverless, b sat
/// where it was; when c takes the wheel b rides on with c; when c gets out b is alone in it, takes
/// the wheel itself, and parks it;</item>
/// <item><b>c</b> gets in second; once the car rolls driverless it takes the wheel from b
/// (a hand-over between two players), drives, stops and gets out.</item>
/// </list>
/// Every peer checks what it sees: who sits where, that a passenger is exactly where its host is,
/// that the seated figures are on the host's rig and the driver drawn only while there is one.
/// Read the <c>[passengers]</c> lines: <c>PASS</c> / <c>FAIL</c>, and a RESULT per role. b (windowed)
/// saves its view from the seat and the car from outside into <c>test_output/</c>.
/// </summary>
public partial class PassengerProbe : Node
{
    public static string? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, "--passengernet");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? Password
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--passengernet");
            return i >= 0 && i + 2 < args.Length && !args[i + 2].StartsWith("--") ? args[i + 2] : null;
        }
    }

    private readonly string _role;
    private readonly System.Func<FootPlayer?> _local;
    private double _t = -1, _clock, _snap;
    private int _failed;
    private string _stage = "start";
    private double _stageAt;
    private string _last = "";
    private Camera3D? _eye;

    public PassengerProbe(string role, System.Func<FootPlayer?> local)
    {
        _role = role;
        _local = local;
        Name = "PassengerProbe";
    }

    private void Log(string what) => GD.Print($"[passengers] {_role} t={_t,5:F1} {what}");

    private void Check(bool ok, string what)
    {
        if (!ok) _failed++;
        Log($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Stage(string next)
    {
        _stage = next;
        _stageAt = _t;
        Log($"-> {next}");
    }

    private double InStage => _t - _stageAt;

    private void Finish()
    {
        GD.Print($"[passengers] {_role} RESULT: {(_failed == 0 ? "ok" : $"FAILED ({_failed})")}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private static RideKind Car => CarCatalog.All[0].Kind;   // the AE86 hatch: two seats in front, a bench behind

    private IEnumerable<FootPlayer> Players => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>();

    /// <summary>Whoever is driving or hosting the car, as this peer sees it.</summary>
    private FootPlayer? CarHost => Players.FirstOrDefault(p => p.Ride == Car);

    private static void Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    public override void _PhysicsProcess(double delta)
    {
        _clock += delta;
        if (_clock > 170) { Log("timed out in " + _stage); _failed++; Finish(); return; }
        var me = _local();
        if (me == null) return;
        if (_t < 0)
        {
            if (!me.IsOnFloor()) return;
            _t = 0;
            Log($"on the ground as {me.Name}");
        }
        _t += delta;
        Watch(me);
        switch (_role)
        {
            case "a": Driver(me); break;
            case "b": First(me); break;
            default: Second(me); break;
        }
    }

    /// <summary>What every peer sees, twice a second, logged when it changes: who sits where, carried exactly, drawn.</summary>
    private void Watch(FootPlayer me)
    {
        _snap += GetPhysicsProcessDeltaTime();
        if (_snap < 0.5) return;
        _snap = 0;
        var lines = new List<string>();
        foreach (var p in Players.OrderBy(p => p.Name.ToString()))
        {
            string what = p.RidingWith != 0 ? $"in {p.RidingWith}'s seat {p.SeatIndex}"
                : p.Ride != RideKind.OnFoot ? $"{(p.SeatIndex == 0 ? "driving" : $"driverless, sat in seat {p.SeatIndex}")} {p.Ride}"
                : "on foot";
            lines.Add($"{p.Name}{(p == me ? "*" : "")}: {what}");
        }
        string now = string.Join(" | ", lines);
        if (now != _last) { Log(now); _last = now; }

        // every passenger exactly where its host is, and drawn on the host's rig
        foreach (var p in Players.Where(p => p.RidingWith != 0))
        {
            if (p.Host is not { } host || host.Visual == null) continue;
            float gap = p.GlobalPosition.DistanceTo(host.GlobalPosition);
            var seated = host.Visual.FindChild($"Seated_{p.Name}", true, false) as Node3D
                ?? host.FindChild($"Seated_{p.Name}", true, false) as Node3D;
            string key = $"{p.Name}-{host.Name}-{p.SeatIndex}";
            if (_seen.Add(key) || (gap > 0.01f && _seen.Add(key + "gap")))
                Check(gap <= 0.01f && seated is { Visible: true } || p == me && seated != null,
                    $"{p.Name} in {host.Name}'s seat {p.SeatIndex}: {gap:F3} m from its host, figure {(seated == null ? "MISSING" : "on the rig")} at {host.WorldVelocity.Length() * 3.6f:F0} km/h");
        }
        // the driver drawn only while somebody is at the wheel
        var car = CarHost;
        if (car?.Visual is Avatar.CarRig rig)
        {
            bool shown = rig.FindChild("Driver", true, false) is Node3D { Visible: true } || rig.View != Avatar.CockpitView.Outside;
            string key = $"driver-{car.Name}-{car.SeatIndex}";
            if (_seen.Add(key))
                Check(shown == (car.SeatIndex == 0) || rig.View != Avatar.CockpitView.Outside,
                    $"{car.Name}'s car: driver {(shown ? "drawn" : "not drawn")} with {(car.SeatIndex == 0 ? "somebody" : "nobody")} at the wheel");
        }
    }

    private readonly HashSet<string> _seen = new();

    // ---- a: drives, then jumps out ----------------------------------------------------------

    private void Driver(FootPlayer me)
    {
        switch (_stage)
        {
            case "start":
                if (_t > 0.5 && Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
                    chat.Send($"/login {pw}");
                Stage("car");
                break;
            case "car":
                if (InStage < 2) return;
                Check(me.SetRide(Car), $"a takes the {Car}");
                Stage("wait");
                break;
            case "wait":
                int aboard = Players.Count(p => p.RidingWith.ToString() == me.Name);
                if (aboard >= 2 && InStage > 1)
                {
                    Check(true, $"two passengers aboard after {InStage:F0} s");
                    Input.ActionPress(PlayerInput.Throttle, 1f);
                    Stage("drive");
                }
                else if (InStage > 60) { Check(false, $"only {aboard} passengers came"); Finish(); }
                break;
            case "drive":
                if (me.GroundSpeed * 3.6f > 45f || InStage > 9)
                {
                    Input.ActionRelease(PlayerInput.Throttle);
                    Log($"jumping out at {me.GroundSpeed * 3.6f:F0} km/h");
                    me.ExitVehicle();
                    Stage("watch");
                }
                break;
            case "watch":
                if (InStage > 1.5 && _seen.Add("after-jump"))
                {
                    var host = CarHost;
                    Check(host != null && host != me && host.SeatIndex > 0,
                        $"the car rolls on driverless with {host?.Name} aboard (seat {host?.SeatIndex}), {host?.WorldVelocity.Length() * 3.6f:F0} km/h");
                    Check(VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Any(v => v.Kind == Car) != true, "and it was not parked");
                }
                if (InStage > 60) Finish();
                break;
        }
    }

    // ---- b: in first, host after a, rides with c, drives it home -----------------------------

    private void Board(FootPlayer me, double delay)
    {
        if (CarHost is not { SeatIndex: 0 } driver || driver == me || InStage < delay) return;
        if (me.RidingWith != 0) return;
        if (_seen.Add("walk"))
        {
            // up to the car's side: on the left for b, the right for c
            me.GlobalPosition = driver.GlobalPosition + driver.GlobalTransform.Basis.X * (_role == "b" ? -2.3f : 2.3f) + Vector3.Up * 0.3f;
            return;
        }
        if (InStage < delay + 1.5 || !_seen.Add("ask")) return;
        Log($"E beside {driver.Name}'s car: {me.TryInteract()}");
        Stage("seated");
    }

    private void First(FootPlayer me)
    {
        switch (_stage)
        {
            case "start": Board(me, 3); break;
            case "seated":
                // a jumped out: the car is b's now
                if (me.Ride == Car) { Stage("host"); return; }
                if (me.RidingWith == 0) { if (InStage > 5) { Check(false, "b got no seat"); Finish(); } return; }
                if (_seen.Add("b-seat")) Check(me.SeatIndex > 0, $"b sits in seat {me.SeatIndex} of {me.RidingWith}'s car");
                if (me.Host is { } h && h.WorldVelocity.Length() * 3.6f > 30f && _seen.Add("shot")) Shoot(me);
                break;
            case "host":
                // the car is b's now, rolling with nobody at the wheel: it slows by itself
                if (_seen.Add("host")) { _speed0 = me.GroundSpeed; Check(me.SeatIndex > 0, $"b hosts the car driverless from seat {me.SeatIndex} at {me.GroundSpeed * 3.6f:F0} km/h"); }
                if (InStage > 1.5 && _seen.Add("rolling"))
                    Check(me.GroundSpeed < _speed0 - 0.3f, $"it rolls on and slows: {_speed0 * 3.6f:F0} -> {me.GroundSpeed * 3.6f:F0} km/h");
                if (me.RidingWith != 0) Stage("riding");
                break;
            case "riding":
                if (_seen.Add("b-rides")) Check(me.Ride == RideKind.OnFoot && me.SeatIndex > 0, $"c took the wheel; b rides on in seat {me.SeatIndex}");
                if (me.Ride == Car && me.SeatIndex > 0) Stage("alone");
                break;
            case "alone":
                // c got out at a stop: b is alone in its driverless car, and takes the wheel itself
                if (InStage > 1 && _seen.Add("wheel")) Press(PlayerInput.TakeWheel);
                if (me.SeatIndex == 0 && InStage > 1.2)
                {
                    Check(true, "b took the wheel of its own car");
                    Stage("park");
                }
                else if (InStage > 10) { Check(false, "b never got the wheel"); Finish(); }
                break;
            case "park":
                if (InStage < 1.5) return;
                me.ExitVehicle();
                Stage("parked");
                break;
            case "parked":
                if (InStage < 2) return;
                Check(VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Any(v => v.Kind == Car) == true, "with nobody left aboard, the car is parked");
                Finish();
                break;
        }
    }

    private float _speed0;

    // ---- c: in second, takes the wheel from b ------------------------------------------------

    private void Second(FootPlayer me)
    {
        switch (_stage)
        {
            case "start": Board(me, 6); break;
            case "seated":
                if (me.RidingWith == 0) { if (InStage > 5) { Check(false, "c got no seat"); Finish(); } return; }
                if (_seen.Add("c-seat")) Check(me.SeatIndex > 0, $"c sits in seat {me.SeatIndex} of {me.RidingWith}'s car");
                // once the car rolls on with b as its host and nobody at the wheel: take it
                if (me.Host is { SeatIndex: > 0 } && _seen.Add("ask-wheel")) { Stage("asked"); _askAt = _t + 2; }
                break;
            case "asked":
                if (_t >= _askAt && _seen.Add("press")) Press(PlayerInput.TakeWheel);
                if (me.Ride == Car && me.SeatIndex == 0)
                {
                    Check(true, $"c took the wheel at {me.GroundSpeed * 3.6f:F0} km/h");
                    Input.ActionPress(PlayerInput.Throttle, 0.4f);
                    Stage("drive");
                }
                else if (InStage > 10) { Check(false, "c never got the wheel"); Finish(); }
                break;
            case "drive":
                if (InStage > 2) { Input.ActionRelease(PlayerInput.Throttle); Input.ActionPress(PlayerInput.Brake); Stage("stop"); }
                break;
            case "stop":
                if (me.GroundSpeed > 0.3f && InStage < 15) return;
                Input.ActionRelease(PlayerInput.Brake);
                me.ExitVehicle();
                Log("c got out at a stop, b still aboard");
                Stage("out");
                break;
            case "out":
                if (InStage > 0.7 && _seen.Add("c-after"))
                    Check(CarHost is { SeatIndex: > 0 } h && h != me, $"the car is {CarHost?.Name}'s, driverless at a stop, not parked");
                if (InStage > 20) Finish();
                break;
        }
    }

    private double _askAt;

    // ---- screenshots (b, windowed) ---------------------------------------------------------

    private void Shoot(FootPlayer me)
    {
        if (DisplayServer.GetName() == "headless") return;
        _shots = 2;
        _shotIn = 0.3;
    }

    private int _shots;
    private double _shotIn;

    public override void _Process(double delta)
    {
        if (_shots == 0 || _local() is not { } me) return;
        if ((_shotIn -= delta) > 0) return;
        string name = _shots == 2 ? "passenger_seat_view" : "passengers_outside";
        if (_shots == 1 && _eye is not { Current: true } && me.Host is { } host)
        {
            // from outside, beside the front wheel: everyone in their seat through the glass
            _eye ??= new Camera3D { Name = "PassengerEye", Far = 3000f, Fov = 50 };
            if (_eye.GetParent() == null) GetTree().Root.AddChild(_eye);
            var t = host.GlobalTransform;
            _eye.LookAtFromPosition(t.Origin + t.Basis.X * -4.5f + t.Basis.Z * -3f + Vector3.Up * 2.2f, t.Origin + Vector3.Up * 0.9f, Vector3.Up);
            _eye.Current = true;
            _shotIn = 0.25;
            return;
        }
        string path = ProjectSettings.GlobalizePath($"res://test_output/{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
        _shots--;
        _shotIn = 0.15;
        if (_shots == 0 && _eye != null) _eye.Current = false;
    }
}
