using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--garagecheck a|b|c</c> on a client connected to a loopback server (a with the server's
/// <c>--admin-password</c> after its role, so it may park the car it conjured): the garage parts and the
/// car doors, seen from the OTHER peers.
///
/// <list type="bullet">
/// <item><b>a</b> (the owner) takes a car, tunes it, tries a door from the seat (refused: in a car they are shut),
/// gets out (parks it), opens a door on foot, gets back in (the parts must still be there), drives
/// off (doors shut above 20 km/h), changes car (stock again), then wrecks a tuned car;</item>
/// <item><b>b</b> watches: every change to a's player and to parked cars (parts, doors, the rig's
/// door hinges) is printed, it works a door of a's parked car through the server, and takes
/// screenshots into <c>test_output/</c>;</item>
/// <item><b>c</b> joins late and prints what it is shown.</item>
/// <item><b>drive</b> / <b>watch</b>: one drives a car into a garage through its door portal (and back out),
/// the other screenshots it from the street.</item>
/// </list>
/// Read the <c>[garage]</c> lines; the timeline is a's, in seconds from standing on the ground.
/// </summary>
public partial class GarageProbe : Node
{
    public static string? ParseArgs() => CmdArgs.Value("--garagecheck");

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
        else if (_role == "drive") DriveIn(me, At);
        else if (_role == "watch") WatchGarage(me);
        else Watch(me);
    }

    /// <summary>
    /// <c>--garagecheck a &lt;password&gt;</c>: the server's <c>--admin-password</c>. Online only an
    /// admin may leave a conjured car in the world (the <c>admin-only-spawning</c> note), and a
    /// parks it to work its doors on foot and get back in.
    /// </summary>
    private static string? Password => CmdArgs.Value("--garagecheck", 2, notFlag: true);

    private void Act(FootPlayer me, Func<double, bool> at)
    {
        if (at(0.5) && Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
            chat.Send($"/login {pw}");
        if (at(1.8)) Log($"admin: {Permissions.IsAdmin}");
        if (at(2)) Log($"SetRide {FirstCar}: {me.SetRide(FirstCar)}");
        // --setup <preset> (#40): the garage parts go on over a car preset, and both travel with the car
        if (at(2.5) && CmdArgs.Value("--setup") is { } setup && CarSetups.Parse(setup) is { } preset)
            Log($"preset {preset.Name}: {me.SetCarSetup(preset.Id)} -> {me.CarSetupId}");
        if (at(3)) { me.SetTuning(Tuned); Log($"tuned: bits {me.TuningBits:X}, preset {me.CarSetupId}"); }
        // the menu itself, on the tuned car, for a look
        var garage = GetTree().Root.FindChild("GarageUi", true, false) as GarageUi;
        if (at(4) && garage != null) { garage.Open(me); Log($"garage menu open: {garage.IsOpen}"); }
        if (at(6.5) && DisplayServer.GetName() != "headless")
            GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath("res://test_output/garage_menu_a.png"));
        if (at(7)) garage?.Close();
        if (at(8)) Log($"door from the seat (must be refused): {me.TryToggleCarDoor()} -> doors {me.DoorsOpen}");
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
            Log($"get back in: {me.TryGetIn()}");
        }
        if (at(70.3)) Log($"getting in: doors {me.DoorsOpen}");
        if (at(73)) Log($"in again: ride {me.Ride}, bits {me.TuningBits:X} (same car: {me.TuningBits == Tuned.Pack()}), preset {me.CarSetupId}, doors {me.DoorsOpen} (all shut: {me.DoorsOpen == 0})");
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
                lines.Add($"player {p.Name}: {p.Ride} preset {p.CarSetupId} bits {p.TuningBits:X} doors {p.DoorsOpen} rig[{Rig(p.GetChildren().OfType<CarRig>().FirstOrDefault(r => !r.IsQueuedForDeletion()))}]");
        VehicleBody? target = null;
        foreach (var v in VehicleManager.Instance?.GetChildren().OfType<VehicleBody>() ?? Enumerable.Empty<VehicleBody>())
        {
            lines.Add($"parked {v.Name}: {v.Kind} preset {(v.Ride as Car)?.Spec.SetupId} bits {(v.Ride as Car)?.Tuning.Bits:X} doors {v.DoorsOpen} wrecked {v.Wrecked} rig[{Rig(v.Rig)}]");
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
        if (_eyeFrom is { } from) _eye.LookAtFromPosition(from, _eyeTo, Vector3.Up);
        else
            _eye.LookAtFromPosition(car.Origin - car.Basis.Z * 5f + car.Basis.X * 3.5f + Vector3.Up * 2.2f,
                car.Origin + Vector3.Up * 0.6f, Vector3.Up);
        _eye.Current = true;
        if ((_shootIn -= delta) > 0) return;
        string path = ProjectSettings.GlobalizePath("res://test_output/garage_" + _shootName + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[garage] {_role} screenshot {path}");
        _shootAt = null;
        _eyeFrom = null;
    }

    // ---- "drive" / "watch": a car driven into a drive-in garage, seen from a remote peer -------

    private Vector3? _eyeFrom;
    private Vector3 _eyeTo;
    private Vector3 _from;
    private double _stillFor;

    /// <summary>
    /// Inside a building or out. From position, so it holds for a remote copy too: the interior it
    /// is in (built here, as its door is open and near) is named when this peer has it.
    /// </summary>
    private static string Where(FootPlayer p)
    {
        if (!Interiors.InteriorManager.InInteriorSpace(p.GlobalPosition)) return "outside";
        return Interiors.InteriorManager.Instance?.LayoutAt(p.GlobalPosition) is { } l ? $"inside {l.DressedKind()} {l.Key}" : "inside (not built here)";
    }

    /// <summary><c>--doorkind Agricultural</c> drives into the nearest barn instead of a garage.</summary>
    private static Terrain.Format.BuildingKind TargetKind => Interiors.InteriorProbe.DoorKindArg() ?? Terrain.Format.BuildingKind.Garage;

    private static bool InsideTarget(FootPlayer p) =>
        Interiors.InteriorManager.InInteriorSpace(p.GlobalPosition)
        && Interiors.InteriorManager.Instance?.LayoutAt(p.GlobalPosition)?.DressedKind() == TargetKind;

    /// <summary>With no <c>--heading</c>, the probes pick the garage nearest where they spawned (a generated world has no fixed one).</summary>
    private static bool AutoGarage => !CmdArgs.Has("--heading");

    /// <summary>
    /// The garage nearest <paramref name="me"/>, and <paramref name="me"/> stood in front of its door:
    /// <paramref name="outward"/> metres out and <paramref name="aside"/> along the wall, facing it.
    /// </summary>
    private Interiors.DoorIndex.Entry? StandAtGarage(FootPlayer me, float outward, float aside)
    {
        // a door to DRIVE through: on a works that is a loading bay and never the office door (#531)
        if (Interiors.DoorIndex.NearestOfKind(me.GlobalPosition, Interiors.DoorSearch.KindReach, TargetKind,
                vehicleOnly: true) is not { } door) return null;
        var o = door.Outward;
        var at = door.World + o * outward + new Vector3(-o.Z, 0, o.X) * aside;
        if (me.Terrain != null && me.Terrain.TryGetHeight(at, out float g)) at.Y = g + 0.3f;
        var look = door.World - at;
        me.PlaceAt(at, Mathf.Atan2(-look.X, -look.Z));
        var origin = me.Terrain?.Origin;
        string lv95 = origin == null ? "" : $" (door at LV95 {origin.E + door.World.X:F1},{origin.N - door.World.Z:F1}, "
            + $"facing bearing {Mathf.RadToDeg(Mathf.Atan2(o.X, -o.Z)):F1})";
        Log($"at {TargetKind} {door.Key}: door {door.Width:F1} x {door.Height:F2} m, standing {outward:F0} m out{lv95}");
        return door;
    }

    /// <summary>
    /// <c>--garagecheck drive &lt;password&gt; [--at E,N --heading deg] [--drive-m m] [--brake-m m] [--drive-at s] [--drive-end s]</c>:
    /// takes a car, faces the bearing (with no <c>--heading</c>: lined up 12 m in front of the nearest
    /// garage), holds the throttle for <c>--drive-m</c> metres, coasts, brakes from <c>--brake-m</c> to a
    /// stop; then gets out and back in (a car parked inside), and reverses out through the door.
    /// <c>--drive-at</c> starts the throttle at that time instead of 7 s after lining up; <c>--drive-end</c>
    /// quits there with the car's position (a road check driving at something other than a garage).
    /// </summary>
    private void DriveIn(FootPlayer me, Func<double, bool> at)
    {
        if (at(0.5) && Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
            chat.Send($"/login {pw}");
        // lined up at the door once it is found: with no --heading, the nearest door is looked for
        // every second, as the tiles around draw theirs, for up to 25 s
        if (_readyAt < 0 && _t >= 3 && (_searchIn -= GetPhysicsProcessDeltaTime()) <= 0)
        {
            _searchIn = 1;
            if (!AutoGarage)
            {
                me.PlaceAt(me.GlobalPosition, -Mathf.DegToRad(CmdArgs.Float("--heading") ?? 0f));
                _target = Interiors.DoorIndex.Nearest(me.GlobalPosition, 20f, TargetKind);
                _readyAt = _t;
            }
            else if ((_target = StandAtGarage(me, 12f, 0f)) != null) _readyAt = _t;
            else if (_t > Interiors.DoorSearch.GiveUp)
            {
                // what is drawn and why none of it served, and somewhere to look instead
                Interiors.DoorSearch.Explain("garage", me.GlobalPosition, TargetKind, me.Terrain?.Origin);
                Log($"RESULT: FAILED (no {TargetKind} within {Interiors.DoorSearch.KindReach:F0} m)");
                GetTree().Quit();
                return;
            }
        }
        if (_readyAt < 0) return;
        if (at(_readyAt + 3)) Log($"SetRide {FirstCar}: {me.SetRide(FirstCar)}");
        // the watcher needs a moment to see the car before it moves
        if (at(CmdArgs.Float("--drive-at") ?? _readyAt + 7)) { _from = me.GlobalPosition; Input.ActionPress(PlayerInput.Throttle); _drive = 1; Log($"throttle, {Where(me)}"); }
        float gone = new Vector2(me.GlobalPosition.X - _from.X, me.GlobalPosition.Z - _from.Z).Length();
        if (_target is { } tg && (_drive is 1 or 2 or 6 || _t < _readyAt + 7) && (_trace -= GetPhysicsProcessDeltaTime()) <= 0)
        {
            // where the car is against the door: metres out of the facade, along it, and its nose's bearing
            _trace = 0.25;
            var rel = me.GlobalPosition - tg.World;
            var fwd = -me.GlobalTransform.Basis.Z;
            var link = Interiors.InteriorManager.Instance?.Links.GetValueOrDefault(tg.Key.ToString());
            Log($"  {Where(me)} out {rel.X * tg.Outward.X + rel.Z * tg.Outward.Z:F2} along {rel.X * -tg.Outward.Z + rel.Z * tg.Outward.X:F2} "
                + $"y {rel.Y:F2} vel.out {me.Velocity.X * tg.Outward.X + me.Velocity.Z * tg.Outward.Z:F1} vel.along {me.Velocity.X * -tg.Outward.Z + me.Velocity.Z * tg.Outward.X:F1} nose.in {-(fwd.X * tg.Outward.X + fwd.Z * tg.Outward.Z):F2} v {me.GroundSpeed * 3.6f:F0} km/h "
                + $"door {(Interiors.InteriorManager.Instance?.IsOpen(tg.Key.ToString()) == true ? "open" : "shut")} link {(link == null ? "none" : $"swing {link.Swing:F2}")}"
                + string.Concat(Enumerable.Range(0, me.GetSlideCollisionCount()).Select(i => me.GetSlideCollision(i))
                    .Where(c => c.GetNormal().Y < 0.7f)
                    .Select(c => $" hit {(c.GetCollider() as Node)?.GetPath().ToString().Split('/').LastOrDefault() ?? "?"} n({c.GetNormal().X:F2},{c.GetNormal().Y:F2},{c.GetNormal().Z:F2})")));
        }
        float throttleM = CmdArgs.Float("--drive-m") ?? (AutoGarage ? 5f : 15f), brakeM = CmdArgs.Float("--brake-m") ?? (AutoGarage ? 12.5f : throttleM);
        if (_drive == 1 && gone >= throttleM && Input.IsActionPressed(PlayerInput.Throttle))
        {
            Input.ActionRelease(PlayerInput.Throttle);
            Log($"off the throttle after {gone:F1} m at {me.GroundSpeed * 3.6f:F0} km/h, {Where(me)}");
            _stepAt = _t;
        }
        // stopped short (at a shut door): the same stop, and the result says where
        if (_drive == 1 && (gone >= brakeM || !Input.IsActionPressed(PlayerInput.Throttle) && me.GroundSpeed < 0.2f && _t - _stepAt > 2))
        {
            Input.ActionPress(PlayerInput.Brake);
            Log($"brake after {gone:F1} m at {me.GroundSpeed * 3.6f:F0} km/h, {Where(me)}");
            _drive = 2;
        }
        if (_drive == 2 && me.GroundSpeed < 0.3f)
        {
            Input.ActionRelease(PlayerInput.Brake);
            Log($"stopped after {gone:F1} m, {Where(me)}, garage near: {GarageUi.GarageNear?.Invoke(me.GlobalPosition)}");
            _parkedIn = InsideTarget(me);
            _drive = 3;
            _stepAt = _t;
        }
        if (CmdArgs.Float("--drive-end") is { } end && at(end)) { Log($"RESULT: {Where(me)} at {me.GlobalPosition}"); GetTree().Quit(); return; }
        // parked inside: out on foot, and back in
        if (_drive == 3 && _t - _stepAt > 3)
        {
            me.ExitVehicle();
            Log($"got out: ride {me.Ride}, {Where(me)}, parked car {VehicleManager.Instance?.Nearest(me.GlobalPosition, 6f)?.GlobalPosition}");
            _drive = 4;
            _stepAt = _t;
        }
        if (_drive == 4 && _t - _stepAt > 4)
        {
            var parked = VehicleManager.Instance?.Nearest(me.GlobalPosition, 6f);
            Log($"parked car still inside: {parked != null && Interiors.InteriorManager.InInteriorSpace(parked.GlobalPosition)}; get back in: {me.TryGetIn()}");
            _drive = 5;
            _stepAt = _t;
        }
        // and out again, backwards
        if (_drive == 5 && _t - _stepAt > 3)
        {
            Log($"in again: ride {me.Ride}, {Where(me)}; reversing out");
            _from = me.GlobalPosition;
            Input.ActionPress(PlayerInput.Brake);   // held from standstill, the brake is reverse
            _drive = 6;
        }
        bool outside = !Interiors.InteriorManager.InInteriorSpace(me.GlobalPosition);
        if (_drive == 6 && (outside || _t - _stepAt > 15))
        {
            _outAgain = outside;
            _drive = 7;
            _stepAt = _t;
        }
        if (_drive == 7 && _t - _stepAt > 1.5)
        {
            Input.ActionRelease(PlayerInput.Brake);
            Log($"RESULT: {(_parkedIn && _outAgain ? "ok" : "FAILED")} (parked inside a garage: {_parkedIn}, reversed out: {_outAgain}), {Where(me)} at {me.GlobalPosition}");
            GetTree().Quit();
        }
        if (at(90)) { Log($"RESULT: FAILED (timeout at step {_drive}), {Where(me)} at {me.GlobalPosition}"); GetTree().Quit(); }
    }

    private double _stepAt;
    private bool _parkedIn, _outAgain;
    private Interiors.DoorIndex.Entry? _watchedDoor;
    private bool _standing;
    private Interiors.DoorIndex.Entry? _target;
    private double _readyAt = -1, _searchIn;
    private double _trace;

    /// <summary>
    /// <c>--garagecheck watch</c>: the other player's ride, garage and the door it drives at, and two
    /// screenshots from outside that door: the car going in, and parked inside.
    /// </summary>
    private void WatchGarage(FootPlayer me)
    {
        _snap += GetPhysicsProcessDeltaTime();
        if (_snap < 0.25) return;
        _snap = 0;
        if (_clock > 150) { Log("RESULT: done"); GetTree().Quit(); return; }
        var other = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p != me);
        if (other == null) return;
        // with no --at, stand beside the garage the driver will pick (the nearest to the same spawn)
        if (AutoGarage && !_standing && _clock > 3) { _standing = true; _watchedDoor = StandAtGarage(me, 9f, 3.5f); }
        // the garage it drives at; once inside, it is 3 km under that door
        if (!AutoGarage && !Interiors.InteriorManager.InInteriorSpace(other.GlobalPosition)
            && Interiors.DoorIndex.Nearest(other.GlobalPosition, 15f, TargetKind) is { } near)
            _watchedDoor = near;
        var door = _watchedDoor;
        string leaf = "no garage";
        if (door is { } d)
            leaf = Interiors.InteriorManager.Instance?.Links.GetValueOrDefault(d.Key.ToString()) is { } link
                ? $"door {d.Key} open {link.Open} swing {link.Swing:F2} leaf {(link.Leaf?.Outward == true ? "on the facade" : "none")}"
                : $"door {d.Key} (no link)";
        string now = $"{other.Name}: {other.Ride} {Where(other)}, {leaf}";
        if (now != _last) { Log(now); _last = now; }

        _stillFor = other.GroundSpeed < 0.3f ? _stillFor + 0.25 : 0;
        if (door is not { } e) return;
        var o = e.Outward;
        var tangent = new Vector3(-o.Z, 0, o.X);
        var rel = other.GlobalPosition - e.World;
        float outward = rel.X * o.X + rel.Z * o.Z;
        // from the street, a little to one side, looking into the doorway
        var from = e.World + o * 9f + tangent * 2.5f + Vector3.Up * 2.4f;
        if (CarCatalog.IsCar(other.Ride) && outward is > -1f and < 2.5f && _shot.Add("entering"))
            ShootView(other, "bay_entering_" + _role, from, e.World + Vector3.Up * 1.2f, 0.05);
        if (CarCatalog.IsCar(other.Ride) && InsideTarget(other) && _stillFor > 1.5 && _shot.Add("inside"))
        {
            // it is 3 km down: aim at where it shows through the doorway, carried up by the door's map
            var seen = Interiors.InteriorManager.Instance?.Links.GetValueOrDefault(e.Key.ToString()) is { } link
                ? link.ToOutside * other.GlobalPosition : e.World;
            ShootView(other, "bay_inside_" + _role, from, seen + Vector3.Up * 0.8f, 0.3);
        }
    }

    private void ShootView(Node3D target, string name, Vector3 from, Vector3 to, double wait)
    {
        if (DisplayServer.GetName() == "headless" || _shootAt != null) return;
        _eye ??= new Camera3D { Name = "GarageProbeEye", Fov = 50 };
        if (_eye.GetParent() == null) AddChild(_eye);
        _shootAt = target;
        _shootName = name;
        _shootIn = wait;
        _eyeFrom = from;
        _eyeTo = to;
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
            sum += Mathf.Abs(MathX.WrapAngle(m.Slip));
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
