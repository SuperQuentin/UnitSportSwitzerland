using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--garagecheck a|b|c</c> on a client connected to a loopback server: the garage parts and the
/// car doors, seen from the OTHER peers.
///
/// <list type="bullet">
/// <item><b>a</b> (the owner) takes a car, tunes it, opens and shuts the driver's door at a stop,
/// gets out (parks it), opens a door on foot, gets back in (the parts must still be there), drives
/// off (doors shut above 20 km/h), changes car (stock again), then wrecks a tuned car;</item>
/// <item><b>b</b> watches: every change to a's player and to parked cars (parts, doors, the rig's
/// door hinges) is printed, it works a door of a's parked car through the server, and takes
/// screenshots into <c>test_output/</c>;</item>
/// <item><b>c</b> joins late and prints what it is shown.</item>
/// </list>
/// Read the <c>[garage]</c> lines; the timeline is a's, in seconds from standing on the ground.
/// </summary>
public partial class GarageProbe : Node
{
    public static string? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--garagecheck");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The parts a puts on: F1 slicks, GT wing, splitter, pink, slammed, blue neon, scissor doors.</summary>
    public static readonly CarTuning Tuned = new CarTuning(0)
        .With(TuneSlot.Tyres, 4).With(TuneSlot.Wing, 5).With(TuneSlot.FrontBumper, 2)
        .With(TuneSlot.Paint, 11).With(TuneSlot.RideHeight, 2).With(TuneSlot.Underglow, 1).With(TuneSlot.Doors, 4);

    private readonly string _role;
    private readonly Func<FootPlayer?> _local;
    private double _t = -1, _snap, _clock;
    private string _last = "";
    private double _sawParked = -1;
    private bool _toggled;
    private int _drive;
    private Camera3D? _eye;

    public GarageProbe(string role, Func<FootPlayer?> local)
    {
        _role = role;
        _local = local;
        Name = "GarageProbe";
    }

    private void Log(string what) => GD.Print($"[garage] {_role} t={_t,5:F1} {what}");

    private static RideKind FirstCar => CarCatalog.All[0].Kind;
    private static RideKind OtherCar => CarCatalog.All[1].Kind;

    public override void _PhysicsProcess(double delta)
    {
        _clock += delta;
        if (_clock > 240) { GD.Print($"[garage] {_role} RESULT: done (timeout)"); GetTree().Quit(); return; }
        var me = _local();
        if (me == null) return;
        if (_t < 0)
        {
            if (!me.IsOnFloor()) return;
            _t = 0;
            Log($"on the ground as {me.Name}");
        }
        double before = _t;
        _t += delta;
        bool At(double s) => before < s && _t >= s;

        if (_role == "a") Act(me, At);
        else Watch(me);
    }

    private void Act(FootPlayer me, Func<double, bool> at)
    {
        if (at(2)) Log($"SetRide {FirstCar}: {me.SetRide(FirstCar)}");
        if (at(3)) { me.SetTuning(Tuned); Log($"tuned: bits {me.TuningBits:X}"); }
        // the menu itself, on the tuned car, for a look
        var garage = GetTree().Root.FindChild("GarageUi", true, false) as GarageUi;
        if (at(4) && garage != null) { garage.Open(me); Log($"garage menu open: {garage.IsOpen}"); }
        if (at(6.5) && DisplayServer.GetName() != "headless")
            GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath("res://test_output/garage_menu_a.png"));
        if (at(7)) garage?.Close();
        if (at(8)) Log($"driver door at a stop: {me.TryToggleCarDoor()} -> doors {me.DoorsOpen}");
        if (at(12)) Log($"driver door again: {me.TryToggleCarDoor()} -> doors {me.DoorsOpen}");
        if (at(15)) { me.ExitVehicle(); Log($"got out (parked); ride {me.Ride}, bits {me.TuningBits:X}"); }
        if (at(38))
        {
            var v = VehicleManager.Instance?.Nearest(me.GlobalPosition, 4f);
            Log($"on foot, G at the car: {me.TryToggleCarDoor()} -> parked doors {v?.DoorsOpen}");
        }
        if (at(70))
        {
            var v = VehicleManager.Instance?.Nearest(me.GlobalPosition, 4f);
            Log($"parked before getting in: doors {v?.DoorsOpen} tune {(v?.Ride as Car)?.Tuning.Bits:X}");
            Log($"get back in: {me.TryInteract()}");
        }
        if (at(73)) Log($"in again: ride {me.Ride}, bits {me.TuningBits:X} (same car: {me.TuningBits == Tuned.Pack()}), doors {me.DoorsOpen}");
        if (at(76)) Log($"door open to drive off: {me.TryToggleCarDoor()} -> doors {me.DoorsOpen}");
        // off down the road past 20 km/h, then stop again
        if (at(77)) { Input.ActionPress(PlayerInput.Throttle); _drive = 1; }
        if (_drive == 1 && me.GroundSpeed > 30f / 3.6f)
        {
            Input.ActionRelease(PlayerInput.Throttle);
            Input.ActionPress(PlayerInput.Brake);
            Log($"at {me.GroundSpeed * 3.6f:F0} km/h: doors {me.DoorsOpen}");
            _drive = 2;
        }
        if (_drive == 2 && me.GroundSpeed < 0.5f)
        {
            Input.ActionRelease(PlayerInput.Brake);
            Log($"stopped at {me.GroundSpeed * 3.6f:F1} km/h");
            _drive = 3;
        }
        if (at(91)) Log($"change car to {OtherCar}: {me.SetRide(OtherCar)} -> bits {me.TuningBits:X}");
        if (at(96)) Log($"back to {FirstCar} (a new one): {me.SetRide(FirstCar)} -> bits {me.TuningBits:X}");
        if (at(98)) { me.SetTuning(Tuned); Log($"tuned the new one: bits {me.TuningBits:X}"); }
        if (at(104)) { me.ShotHit(99999f); Log($"wrecked it: ride {me.Ride}, bits {me.TuningBits:X}"); }
        if (at(110))
        {
            var wreck = VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Wrecked);
            Log($"the wreck: {(wreck == null ? "none" : $"{wreck.Name} tune {(wreck.Ride as Car)?.Tuning.Bits:X}")}; "
                + $"nearest claimable car: {VehicleManager.Instance?.Nearest(me.GlobalPosition, 6f)?.Name ?? "none"}");
        }
        if (at(125)) { GD.Print("[garage] a RESULT: done"); GetTree().Quit(); }
    }

    private void Watch(FootPlayer me)
    {
        _snap += GetPhysicsProcessDeltaTime();
        if (_snap < 0.25) return;
        _snap = 0;

        var lines = new List<string>();
        foreach (var p in GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>())
            if (p != me)
                lines.Add($"player {p.Name}: {p.Ride} bits {p.TuningBits:X} doors {p.DoorsOpen} rig[{Rig(p.GetChildren().OfType<CarRig>().FirstOrDefault(r => !r.IsQueuedForDeletion()))}]");
        VehicleBody? target = null;
        foreach (var v in VehicleManager.Instance?.GetChildren().OfType<VehicleBody>() ?? Enumerable.Empty<VehicleBody>())
        {
            lines.Add($"parked {v.Name}: {v.Kind} bits {(v.Ride as Car)?.Tuning.Bits:X} doors {v.DoorsOpen} wrecked {v.Wrecked} rig[{Rig(v.Rig)}]");
            if (!v.Wrecked && v.Ride is Car { Tuning.Bits: not 0 }) target = v;
        }
        string now = string.Join(" | ", lines);
        if (now != _last) { Log(now); _last = now; }

        // b works a door of a's parked car, as a non-authority peer (through the server)
        if (_role == "b" && target != null && !_toggled)
        {
            if (_sawParked < 0) _sawParked = _t;
            if (_t - _sawParked > 6)
            {
                // beside the side a is NOT on, so a's own door later is the other one
                var side = target.GlobalTransform.Basis.X * 1.6f;
                var owner = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p != me)?.GlobalPosition ?? target.GlobalPosition;
                if (owner.DistanceTo(target.GlobalPosition + side) > owner.DistanceTo(target.GlobalPosition - side)) side = -side;
                me.GlobalPosition = target.GlobalPosition - side + Vector3.Up * 0.2f;
                me.Velocity = Vector3.Zero;
            }
            if (_t - _sawParked > 7.5)
            {
                _toggled = true;
                Log($"b works a door of {target.Name} through the server: {me.TryToggleCarDoor()}");
            }
        }
        if (target != null && target.DoorsOpen == 3) Shoot(target, $"parked_{_role}");
        foreach (var p in GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>())
            if (p != me && p.TuningBits != 0 && p.DoorsOpen != 0 && _t > 1) Shoot(p, $"driving_{_role}");
    }

    private readonly HashSet<string> _shot = new();
    private Node3D? _shootAt;
    private string _shootName = "";
    private double _shootIn;

    public override void _Process(double delta)
    {
        if (_shootAt == null || _eye == null) return;
        if (!IsInstanceValid(_shootAt)) { _shootAt = null; return; }
        // taken back every frame: the player's and the fly camera make themselves current too
        var car = _shootAt.GlobalTransform;
        _eye.LookAtFromPosition(car.Origin - car.Basis.Z * 5f + car.Basis.X * 3.5f + Vector3.Up * 2.2f,
            car.Origin + Vector3.Up * 0.6f, Vector3.Up);
        _eye.Current = true;
        if ((_shootIn -= delta) > 0) return;
        string path = ProjectSettings.GlobalizePath("res://test_output/garage_" + _shootName + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[garage] {_role} screenshot {path}");
        _shootAt = null;
    }

    /// <summary>
    /// <c>--tuningcheck</c>, pure numbers like <c>--driftcheck</c>: the <see cref="CarTuning"/> wire
    /// form, then each tyre set on the first car in Sim — peak cornering on tarmac and on gravel
    /// (60 km/h, a held half lock) and the mean angle of a held Game drift. Non-zero exit if the
    /// wire form breaks or the tyres do not rank the way they are sold.
    /// </summary>
    public static int Check()
    {
        bool ok = CarTuning.Check(out string report);
        GD.Print($"[tuning] wire form: {report}{(ok ? "" : "  FAIL")}");
        var profile = GameSettings.Current.RideProfile;
        GameSettings.Current.RideProfile = RideProfile.Sim;
        var spec = CarCatalog.All[0];
        var results = new (float Tarmac, float Gravel, float Flick)[CarTuning.Options[0].Length];
        for (int i = 0; i < results.Length; i++)
        {
            var tuning = new CarTuning(0).With(TuneSlot.Tyres, i);
            results[i] = (Corner(spec, tuning, Audio.Surface.Asphalt), Corner(spec, tuning, Audio.Surface.Gravel), Flick(spec, tuning));
            GD.Print($"[tuning] {CarTuning.Options[0][i],-10} tarmac {results[i].Tarmac / Rideable.Gravity:F2} g  gravel {results[i].Gravel / Rideable.Gravity:F2} g  "
                + $"drift hold {Mathf.RadToDeg(results[i].Flick):F0}°");
        }
        GameSettings.Current.RideProfile = profile;
        var (stock, drift, rally, race, f1) = (results[0], results[1], results[2], results[3], results[4]);
        bool ranks = f1.Tarmac > race.Tarmac && race.Tarmac > stock.Tarmac && stock.Tarmac > drift.Tarmac
            && rally.Gravel > stock.Gravel && rally.Gravel > f1.Gravel && drift.Flick > stock.Flick && stock.Flick > f1.Flick;
        GD.Print($"[tuning] ranking (tarmac F1 > Race > Stock > Drift, gravel Rally best, the drift tyre slides most): {(ranks ? "ok" : "FAIL")}");
        ok &= ranks;
        GD.Print(ok ? "[tuning] RESULT: ok" : "[tuning] RESULT: FAILED");
        return ok ? 0 : 1;
    }

    private const float Dt = 1f / 60f;

    /// <summary>Peak lateral acceleration, m/s², at 60 km/h with half lock held and the throttle feathered.</summary>
    private static float Corner(CarSpec spec, CarTuning tuning, Audio.Surface surface)
    {
        var car = new Car(spec, tuning);
        var m = new RideMotion { Speed = 60f / 3.6f };
        var ground = new RideGround(true, 0f, surface);
        float peak = 0f;
        for (float t = 0; t < 3f; t += Dt)
        {
            car.Step(new RideInput(0.35f, 0f, -0.5f, false), ground, Dt, ref m);
            peak = Mathf.Max(peak, Mathf.Abs(car.AccelY));
        }
        return peak;
    }

    /// <summary>
    /// Mean drift angle through <c>--driftcheck</c>'s Game hold: a handbrake entry from 60 km/h,
    /// then the turn held on 40% throttle for 3 s: a tyre that lets go easily holds it on less gas.
    /// </summary>
    private static float Flick(CarSpec spec, CarTuning tuning)
    {
        var profile = GameSettings.Current.RideProfile;
        GameSettings.Current.RideProfile = RideProfile.Game;
        var car = new Car(spec, tuning);
        var m = new RideMotion { Speed = 60f / 3.6f };
        var ground = new RideGround(true, 0f);
        for (float t = 0; t < 0.3f; t += Dt) car.Step(new RideInput(0f, 0f, -1f, false, Handbrake: true), ground, Dt, ref m);
        float sum = 0f;
        int n = 0;
        for (float t = 0; t < 3f; t += Dt, n++)
        {
            car.Step(new RideInput(0.4f, 0f, -0.6f, false), ground, Dt, ref m);
            sum += Mathf.Abs(Mathf.Wrap(m.Slip, -Mathf.Pi, Mathf.Pi));
        }
        GameSettings.Current.RideProfile = profile;
        return sum / n;
    }

    /// <summary>The rig's door hinges: name and opening angle, rad; "-" for no rig (headless).</summary>
    private static string Rig(CarRig? rig)
    {
        if (rig == null) return "-";
        var body = rig.GetNodeOrNull<Node3D>("Body");
        if (body == null) return "no body";
        var doors = body.GetChildren().OfType<Node3D>().Where(n => n.Name.ToString().StartsWith("Door"))
            .Select(n => $"{n.Name}:{n.Quaternion.GetAngle():F1}");
        return $"{string.Join(",", doors)}{(body.GetNodeOrNull("Underglow") != null ? " neon" : "")} drop {0.5f - body.Position.Y:F2}";
    }

    /// <summary>A screenshot of a car from a three-quarter front view, into test_output/.</summary>
    private void Shoot(Node3D car, string name)
    {
        if (DisplayServer.GetName() == "headless" || _shootAt != null || !_shot.Add(name)) return;
        _eye ??= new Camera3D { Name = "GarageProbeEye", Fov = 50 };
        if (_eye.GetParent() == null) AddChild(_eye);
        _shootAt = car;
        _shootName = name;
        _shootIn = 1.0;   // the doors finish swinging
    }
}
