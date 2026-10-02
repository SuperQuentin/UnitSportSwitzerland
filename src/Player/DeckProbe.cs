using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--decknet a|b [password]</c> on two clients of a loopback server (a with the server's
/// <c>--admin-password</c>): walking about in a bus (#162), checked on both peers.
///
/// <list type="bullet">
/// <item><b>a</b> drives a city bus, opens its doors and waits; once b is sat it shuts them and
/// drives, stops; once b stands it drives again with b on its feet, brakes hard, stops and opens
/// the doors for b to walk out;</item>
/// <item><b>b</b> walks in through the middle door (boarded by walking, no key), down the aisle,
/// sits, stands up when the bus stops, stays on its feet while it drives (carried, never through the
/// floor, every inertia mode allowed), then walks out of the door.</item>
/// </list>
/// Read the <c>[deck]</c> lines: <c>PASS</c> / <c>FAIL</c> and a RESULT per role. b (windowed) saves
/// its view standing in the moving bus into <c>test_output/deck_standing_view.png</c>.
/// </summary>
public partial class DeckProbe : Node
{
    public static string? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, "--decknet");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? Password
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--decknet");
            return i >= 0 && i + 2 < args.Length && !args[i + 2].StartsWith("--") ? args[i + 2] : null;
        }
    }

    private readonly string _role;
    private readonly System.Func<FootPlayer?> _local;
    private double _t = -1, _clock, _snap;
    private int _failed;
    private string _stage = "start";
    private double _stageAt;
    private readonly HashSet<string> _seen = new();

    public DeckProbe(string role, System.Func<FootPlayer?> local)
    {
        _role = role;
        _local = local;
        Name = "DeckProbe";
    }

    private void Log(string what) => GD.Print($"[deck] {_role} t={_t,5:F1} {what}");

    private void Check(bool ok, string what)
    {
        if (!ok) _failed++;
        Log($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Stage(string next) { _stage = next; _stageAt = _t; Log($"-> {next}"); }
    private double InStage => _t - _stageAt;

    private void Finish()
    {
        GD.Print($"[deck] {_role} RESULT: {(_failed == 0 ? "ok" : $"FAILED ({_failed})")}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private static RideKind Bus => HeavyCatalog.All[2].Kind;   // the Citaro, three double doors
    private static HeavySpec Spec => HeavyCatalog.All[2];

    private IEnumerable<FootPlayer> Players => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>();
    private FootPlayer? Driver => Players.FirstOrDefault(p => p.Ride == Bus);
    private FootPlayer? Walker => Players.FirstOrDefault(p => p.Ride == RideKind.OnFoot && (p.DeckOn != "" || p.RidingWith != 0)) ;

    private static void Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    /// <summary>A point of the bus in its cab's frame: across (+ right), up, and metres behind its front.</summary>
    private static Vector3 OnBus(FootPlayer bus, float across, float up, float at) => OnBus(bus.Visual!, across, up, at);

    private static Vector3 OnBus(Node3D frame, float across, float up, float at)
    {
        float cg = Avatar.HeavyMesh.Cg(Spec.Sections[0], 0.5f);
        return frame.GlobalTransform * new Vector3(across, up, -(cg - at));
    }

    // ---- solo: offline, a parked bus (--decknet solo) ----------------------------------------

    private string _busName = "";

    /// <summary>The bus as it is now: parked (a vehicle in the world) or this player's own.</summary>
    private Node3D? SoloFrame(FootPlayer me) => me.Ride == Bus ? me.Visual
        : VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == Bus) is { } parked ? parked.Visual ?? parked : null;

    private void Trace(FootPlayer me, Node3D frame, string what) =>
        Log($"  {what}: at {frame.GlobalTransform.AffineInverse() * me.GlobalPosition}, on '{me.DeckOn}', vel {me.Velocity}, floor {me.IsOnFloor()}, ride {me.Ride} seat {me.SeatIndex}, {me.WalkState}");

    private void SoloWalk(FootPlayer me) => me.WalkControls ??= () =>
    {
        var to = (_target - me.GlobalPosition) with { Y = 0 };
        return (to.Length() < 0.15f ? Vector3.Zero : to.Normalized(), false);
    };

    private void Solo(FootPlayer me)
    {
        var frame = SoloFrame(me);
        switch (_stage)
        {
            case "start":
                if (Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
                    chat.Send($"/login {pw}");
                Stage("bus");
                break;
            case "bus":
                if (InStage < 2) return;
                Check(me.SetRide(Bus), $"takes the {Spec.Label}");
                Input.ActionPress(PlayerInput.Throttle, 0.5f);
                Stage("away");
                break;
            case "away":
                if (InStage < 4) return;
                Input.ActionRelease(PlayerInput.Throttle);
                Input.ActionPress(PlayerInput.Brake);
                Stage("park");
                break;
            case "park":
                if (me.GroundSpeed > 0.3f && InStage < 15) return;
                Input.ActionRelease(PlayerInput.Brake);
                if (me.Heavy is { DoorsOpen: 0 }) Press(PlayerInput.CarDoor);
                Stage("leave");
                break;
            case "leave":
                // up from the wheel: into the bus, which is parked (nobody else aboard)
                if (InStage < 2) return;
                if (_seen.Add("leave")) { me.TryInteract(); Log("E at the wheel"); }
                if (me.DeckOn.StartsWith("v:") && InStage > 3.5)
                {
                    Check(me.IsOnFloor() && me.Ride == RideKind.OnFoot, $"left the wheel into the bus, parked now: on '{me.DeckOn}'");
                    Shoot("deck_aisle", 0.2);
                    Stage("out");
                }
                else if (InStage > 10) { if (frame != null) Trace(me, frame, "stuck"); Check(false, "never stood up from the wheel aboard"); Finish(); }
                break;
            case "out":
                if (frame == null) return;
                var door = OnBus(frame, 0.2f, 0.3f, 5.8f);
                if ((me.GlobalPosition - door).Length() < 0.5f) _seen.Add("at-door");
                _target = me.DeckOn != "" && !_seen.Contains("at-door") ? door : OnBus(frame, 1.275f + 3f, 0.3f, 5.8f);
                SoloWalk(me);
                if (me.DeckOn == "" && InStage > 1)
                {
                    Check(me.IsOnFloor(), "walked out of the middle door onto the road");
                    me.WalkControls = null;
                    Stage("button");
                }
                else if (InStage > 15) { Trace(me, frame, "stuck"); Check(false, "never got out"); Finish(); }
                break;
            case "button":
                // to the middle door's outside button, shut it, open it again (anyone may)
                if (frame == null) return;
                _target = OnBus(frame, 1.275f + 0.45f, 0.3f, 5.06f);
                SoloWalk(me);
                if (InStage > 3 && _seen.Add("press-shut"))
                {
                    me.WalkControls = null;
                    Log($"button in reach: {me.ButtonInReach()?.Door}, G: {me.TryToggleCarDoor()}");
                }
                if (InStage > 4.5 && _seen.Add("shut"))
                {
                    var parked = VehicleManager.Instance!.GetChildren().OfType<VehicleBody>().First(v => v.Kind == Bus);
                    Check((parked.BusDoors & 2) == 0, $"pressed the outside button: the middle door shut (doors {parked.BusDoors})");
                    Log($"E: {me.TryInteract()}");
                }
                if (InStage > 6 && _seen.Add("opened"))
                {
                    var parked = VehicleManager.Instance!.GetChildren().OfType<VehicleBody>().First(v => v.Kind == Bus);
                    Check((parked.BusDoors & 2) != 0, $"pressed it again: open (doors {parked.BusDoors})");
                    Stage("in");
                }
                break;
            case "in":
                if (frame == null || InStage < 1) return;
                if (_seen.Add("outside-shot")) Shoot("deck_outside_door", 0.05);
                _target = OnBus(frame, 0.1f, 0.3f, 5.8f);
                SoloWalk(me);
                if (Mathf.PosMod(InStage, 1.0) < GetPhysicsProcessDeltaTime()) Trace(me, frame, "walking in");
                if (me.DeckOn != "")
                {
                    Check(me.DeckOn.StartsWith("v:"), $"walked back in through the door: on '{me.DeckOn}'");
                    Shoot("deck_walked_in", 0.3);
                    me.WalkControls = null;
                    Stage("to-seat");
                }
                else if (InStage > 12) { Trace(me, frame, "stuck"); Check(false, "never got back in"); Finish(); }
                break;
            case "to-seat":
                if (frame == null) return;
                if (!_seen.Contains("sit")) { _target = OnBus(frame, 0f, 0.3f, 3.6f); SoloWalk(me); }
                if (InStage > 4 && _seen.Add("sit")) { me.WalkControls = null; Log($"E in the aisle: {me.TryInteract()}"); }
                if (me.Ride == Bus) { Check(me.SeatIndex > 0, $"sat down in the parked bus: hosts it, driverless, from seat {me.SeatIndex}"); Stage("sitting"); }
                else if (InStage > 9) { Trace(me, frame, "stuck"); Check(false, "could not sit down"); Finish(); }
                break;
            case "sitting":
                if (InStage < 1.5) return;
                if (_seen.Add("stand")) { me.TryInteract(); Log("E to stand up"); }
                if (me.DeckOn.StartsWith("v:") && me.Ride == RideKind.OnFoot && InStage > 3.5)
                {
                    Check(me.IsOnFloor(), $"stood up into the aisle (on '{me.DeckOn}')");
                    Stage("to-wheel");
                }
                else if (InStage > 10) { if (frame != null) Trace(me, frame, "stuck"); Check(false, "never stood up aboard"); Finish(); }
                break;
            case "to-wheel":
                if (frame == null) return;
                // by the driver's seat, past the driver's corner: at the front door
                if (!_seen.Contains("wheel")) { _target = OnBus(frame, -0.1f, 0.3f, 1.4f); SoloWalk(me); }
                if (InStage > 4 && _seen.Add("wheel")) { me.WalkControls = null; Log($"seats near: {me.SeatsNearHere()}; E at the driver's seat: {me.TryInteract()}"); }
                if (me.Ride == Bus && me.SeatIndex == 0)
                {
                    Check(true, "walked to the wheel and took it");
                    Input.ActionPress(PlayerInput.Throttle, 0.5f);
                    Stage("drive");
                }
                else if (InStage > 10) { Trace(me, frame, "stuck"); Check(false, "never got to the wheel"); Finish(); }
                break;
            case "drive":
                if (InStage > 3) { Input.ActionRelease(PlayerInput.Throttle); Input.ActionPress(PlayerInput.Brake); Stage("stop"); }
                break;
            case "stop":
                if (me.GroundSpeed > 0.3f && InStage < 15) return;
                Input.ActionRelease(PlayerInput.Brake);
                Check(true, "drove it off and stopped");
                Finish();
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _clock += delta;
        if (_clock > 280) { Log("timed out in " + _stage); _failed++; Finish(); return; }
        var me = _local();
        if (me == null) return;
        if (_t < 0)
        {
            if (!me.IsOnFloor()) return;
            _t = 0;
            Log($"on the ground as {me.Name}, inertia {PassengerService.Inertia}");
        }
        _t += delta;
        if (_role == "a") Drive(me); else if (_role == "solo") Solo(me); else Walk(me);
        Watch(me);
    }

    // ---- a: the bus -------------------------------------------------------------------------

    private void Doors(FootPlayer me, bool open)
    {
        if (me.Heavy is { } bus && (bus.DoorsOpen != 0) != open) Press(PlayerInput.CarDoor);
    }

    private void Drive(FootPlayer me)
    {
        var b = Players.FirstOrDefault(p => p != me);
        switch (_stage)
        {
            case "start":
                if (Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
                    chat.Send($"/login {pw}");
                Stage("bus");
                break;
            case "bus":
                if (InStage < 2) return;
                Check(me.SetRide(Bus), $"a takes the {Spec.Label}");
                // away from where everyone spawns, so b is not standing where the bus is
                Input.ActionPress(PlayerInput.Throttle, 0.5f);
                Stage("away");
                break;
            case "away":
                if (InStage < 4) return;
                Input.ActionRelease(PlayerInput.Throttle);
                Input.ActionPress(PlayerInput.Brake);
                Stage("park");
                break;
            case "park":
                if (me.GroundSpeed > 0.3f && InStage < 15) return;
                Input.ActionRelease(PlayerInput.Brake);
                Stage("open");
                break;
            case "open":
                if (InStage < 1) return;
                Doors(me, true);
                Stage("wait-sit");
                break;
            case "wait-sit":
                if (b is { RidingWith: not 0 } && InStage > 1)
                {
                    Check(b.RidingWith.ToString() == me.Name, $"b sat down in seat {b.SeatIndex} (it walked in: on deck before)");
                    Doors(me, false);
                    Input.ActionPress(PlayerInput.Throttle, 0.6f);
                    Stage("drive1");
                }
                else if (InStage > 70) { Check(false, "b never sat down"); Finish(); }
                break;
            case "drive1":
                if (InStage > 7) { Input.ActionRelease(PlayerInput.Throttle); Input.ActionPress(PlayerInput.Brake); Stage("stop1"); }
                break;
            case "stop1":
                if (me.GroundSpeed > 0.3f && InStage < 20) return;
                Input.ActionRelease(PlayerInput.Brake);
                Stage("wait-stand");
                break;
            case "wait-stand":
                if (b is { DeckOn: not "", RidingWith: 0 } && InStage > 1.5)
                {
                    Log("b stands: driving off with it on its feet");
                    Input.ActionPress(PlayerInput.Throttle, 0.7f);
                    Stage("drive2");
                }
                else if (InStage > 30) { Check(false, "b never stood up"); Finish(); }
                break;
            case "drive2":
                if (me.Heavy is { } own && (own.DoorsOpen & 2) != 0 && _seen.Add("opened-moving"))
                    Check(true, $"b opened the middle door from inside while driving, at {me.GroundSpeed * 3.6f:F0} km/h");
                if (InStage > 8)
                {
                    // out of the cab at speed, b on its feet in the aisle: the bus rolls on by itself
                    if (!_seen.Contains("opened-moving")) Check(false, "b's button press never opened the middle door");
                    _jumpedAt = me.GlobalPosition;
                    Log($"jumping out of the cab at {me.GroundSpeed * 3.6f:F0} km/h");
                    Input.ActionRelease(PlayerInput.Throttle);
                    me.ExitVehicle();
                    Stage("jumped");
                }
                break;
            case "jumped":
                if (VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == Bus) is not { } rolling) return;
                float kmh = rolling.Velocity.Length() * 3.6f;
                if (_seen.Add("rolls") ) Log($"the bus rolls on by itself at {kmh:F0} km/h");
                if (InStage > 2 && _seen.Add("rolling-2s")) Check(kmh > 3f, $"two seconds later it still rolls, slowing on its own: {kmh:F0} km/h");
                if (kmh < 0.5f && InStage > 3 && _seen.Add("rolled"))
                {
                    Check(true, $"it rolled {rolling.GlobalPosition.DistanceTo(_jumpedAt):F0} m from where a jumped and stopped");
                    Stage("wait-out");
                }
                else if (InStage > 120) { Check(false, "the bus never stopped"); Finish(); }
                break;
            case "wait-out":
                if (b is { DeckOn: "", RidingWith: 0 } && InStage > 2) { Check(true, "b walked out"); Stage("done"); }
                else if (InStage > 60) { Check(false, "b never got out"); Finish(); }
                break;
            case "done":
                if (InStage > 3) Finish();
                break;
        }
    }

    // ---- b: the passenger on foot -------------------------------------------------------------

    private Vector3 _target;
    private Vector3 _jumpedAt;
    private double _offDeck;

    /// <summary>The bus standing in the world (parked, or rolling with nobody at the wheel).</summary>
    private VehicleBody? ParkedBus => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == Bus);
    private Vector3 _lastBusPos;
    private double _stillFor;
    private float _localZ0;

    private void WalkTo(FootPlayer me, Vector3 target, float speed = 1f)
    {
        _target = target;
        me.WalkControls = () =>
        {
            var to = (_target - me.GlobalPosition) with { Y = 0 };
            return (to.Length() < 0.15f ? Vector3.Zero : to.Normalized() * speed, false);
        };
    }

    private void Walk(FootPlayer me)
    {
        var bus = Driver;
        switch (_stage)
        {
            case "start":
                // out of the way first: both clients spawn on one spot, where a's bus appears
                if (_seen.Add("aside")) me.GlobalPosition += me.GlobalTransform.Basis.X * 12f + Vector3.Up;
                // the bus parked: drawn in one place for a second and a half, doors open
                if (bus?.Visual == null || bus.BusDoors == 0) { _stillFor = 0; return; }
                _stillFor = bus.Visual.GlobalPosition.DistanceTo(_lastBusPos) < 0.01f ? _stillFor + GetPhysicsProcessDeltaTime() : 0;
                _lastBusPos = bus.Visual.GlobalPosition;
                if (_stillFor < 1.5) return;
                // outside the middle door, 2.5 m off the bus's right side, dropped onto the road
                me.GlobalPosition = OnBus(bus, 1.275f + 2.5f, 1.0f, 5.8f);
                Stage("to-door");
                break;
            case "to-door":
                // standing on the road, solid under it, before walking up
                if (!_seen.Contains("ready"))
                {
                    if (InStage < 1 || !me.IsOnFloor() || me.Terrain?.HasCollisionAt(me.GlobalPosition) != true) return;
                    _seen.Add("ready");
                    _stageAt = _t;
                }
                WalkTo(me, OnBus(bus!, 0.1f, 0.3f, 5.8f));
                if (Mathf.PosMod(InStage, 1.0) < GetPhysicsProcessDeltaTime())
                    Log($"  walking in: at {bus!.Visual!.GlobalTransform.AffineInverse() * me.GlobalPosition}, on '{me.DeckOn}', floor {me.IsOnFloor()}");
                if (me.DeckOn != "")
                {
                    Check(me.DeckOn == bus!.Name, $"b boarded by walking in through the door: on {me.DeckOn}'s deck at {me.DeckPos}");
                    Stage("to-seat");
                }
                else if (InStage > 12) { Check(false, $"b never got aboard, stuck at {bus!.Visual!.GlobalTransform.AffineInverse() * me.GlobalPosition}"); Finish(); }
                break;
            case "to-seat":
                // forward up the aisle, then E at the nearest seat
                if (!_seen.Contains("sit")) WalkTo(me, OnBus(bus!, 0f, 0.3f, 3.6f));
                if (InStage > 4 && _seen.Add("sit"))
                {
                    me.WalkControls = null;
                    Log($"E in the aisle at {me.DeckPos}: {me.TryInteract()}");
                }
                if (me.RidingWith != 0) { me.WalkControls = null; Check(me.SeatIndex > 0, $"b sits in seat {me.SeatIndex}"); Stage("seated"); }
                else if (InStage > 10) { Check(false, "b could not sit down"); Finish(); }
                break;
            case "seated":
                // once the bus has driven and stopped again: up into the aisle
                if (bus!.WorldVelocity.Length() * 3.6f > 20f) _seen.Add("moved");
                if (_seen.Contains("moved") && bus.WorldVelocity.Length() < 0.3f && _seen.Add("stand"))
                {
                    Log($"E to stand up: {me.TryInteract()}");
                    Stage("standing");
                }
                break;
            case "standing":
                // to the middle door's inside button, to press it once the bus moves
                if (_seen.Contains("stood")) { _target = OnBus(bus!, 1.195f - 0.45f, 0.3f, 5.06f); if (me.WalkControls == null) WalkTo(me, _target); }
                if (InStage > 1.5 && _seen.Add("stood"))
                {
                    Check(me.DeckOn == bus!.Name && me.RidingWith == 0 && me.IsOnFloor(),
                        $"b stood up into the aisle: on {me.DeckOn}'s deck at {me.DeckPos}, on its feet {me.IsOnFloor()}");
                    _localZ0 = me.DeckPos.Z;
                }
                if (bus!.WorldVelocity.Length() * 3.6f > 15f && _seen.Add("moving")) { me.WalkControls = null; Stage("riding"); Shoot(); me.MaxDeckAccel = 0; _stuns = 0; }
                break;
            case "riding":
                // pressing the middle door's inside button as the bus drives: it opens, at our risk
                if (InStage > 2 && InStage < 2.1 && _seen.Add("press-moving"))
                    Log($"button in reach: {me.ButtonInReach()?.Door}, E: {me.TryInteract()}");
                if (Mathf.PosMod(InStage, 1.0) < GetPhysicsProcessDeltaTime())
                    Log($"  riding: at {me.DeckPos}, on '{me.DeckOn}', floor {me.IsOnFloor()}");
                // on its feet through the drive and the driver jumping out: aboard, never through the floor
                // off a deck only for the moment the bus changes hands (the driver jumped: it is parked now)
                _offDeck = me.DeckOn == "" ? _offDeck + GetPhysicsProcessDeltaTime() : 0;
                if (_offDeck > 3) { Check(false, $"b fell off the deck ({me.GlobalPosition})"); Finish(); return; }
                if (me.DeckPos.Y < -0.5f) { Check(false, $"b is under the floor: {me.DeckPos}"); Finish(); return; }
                if (me.DeckOn.StartsWith("v:") && ParkedBus is { } stopped && stopped.Velocity.Length() < 0.3f && InStage > 6)
                {
                    Check(true, $"b stayed aboard as the driver jumped out and the bus rolled to a stop: on '{me.DeckOn}' at {me.DeckPos}, stunned {_stuns} time(s), hardest {me.MaxDeckAccel:F1} m/s²");
                    Stage("to-button");
                }
                else if (InStage > 130) { Check(false, "the bus never stopped with b aboard"); Finish(); }
                break;
            case "to-button":
                // the parked bus's middle door from inside: shut as it pulled away? press it open
                if (ParkedBus is not { } parked || parked.Visual is not { } pframe) return;
                if (!_seen.Contains("pressed"))
                {
                    _target = OnBus(pframe, 1.195f - 0.45f, 0.3f, 5.06f);
                    if (me.WalkControls == null) WalkTo(me, _target);
                }
                if (InStage > 4 && _seen.Add("pressed"))
                {
                    me.WalkControls = null;
                    if ((parked.BusDoors & 2) == 0) Log($"button in reach: {me.ButtonInReach()?.Door}, E: {me.TryInteract()}");
                }
                if (InStage > 6 && (parked.BusDoors & 2) != 0) { Check(true, $"the middle door of the parked bus is open (doors {parked.BusDoors})"); Stage("to-exit"); }
                else if (InStage > 12) { Check(false, $"the parked bus's door never opened (doors {parked.BusDoors})"); Finish(); }
                break;
            case "to-exit":
                if (ParkedBus is not { } at || at.Visual is not { } xframe) return;
                var door = OnBus(xframe, 0.2f, 0.3f, 5.8f);
                var outside = OnBus(xframe, 1.275f + 3f, 0.3f, 5.8f);
                WalkTo(me, me.DeckOn != "" && (me.GlobalPosition - door).Length() > 0.5f && !_seen.Contains("at-door") ? door : outside);
                if ((me.GlobalPosition - door).Length() < 0.5f) _seen.Add("at-door");
                if (me.DeckOn == "" && InStage > 1 && _seen.Add("out"))
                {
                    Check(me.RidingWith == 0, $"b walked out of the door: off the deck at {xframe.GlobalTransform.AffineInverse() * me.GlobalPosition}");
                    Stage("outside");
                }
                else if (InStage > 15) { Check(false, $"b could not get out, at {me.DeckPos}"); Finish(); }
                break;
            case "outside":
                if (InStage > 2) { me.WalkControls = null; Check(me.IsOnFloor(), "b stands on the road outside"); Finish(); }
                break;
        }
    }

    private int _stuns;
    private bool _wasStunned;

    /// <summary>
    /// Both peers, twice a second: where b is drawn against where the bus says it stands (a sees b's
    /// copy), and on b's own peer whether it is lying down (knocked over by a hard brake).
    /// </summary>
    private void Watch(FootPlayer me)
    {
        if (_role == "b")
        {
            bool stunned = me.Stunned;
            if (stunned && !_wasStunned) { _stuns++; Log($"knocked over at {Driver?.WorldVelocity.Length() * 3.6f:F0} km/h"); }
            _wasStunned = stunned;
        }
        _snap += GetPhysicsProcessDeltaTime();
        if (_snap < 0.5) return;
        _snap = 0;
        if (_role != "a" || Walker is not { DeckOn: not "" } b || Driver is not { Visual: { } rig } bus) return;
        // b's copy here, drawn on the bus: its spot in the bus's frame is what b published
        var local = rig.GlobalTransform.AffineInverse() * b.GlobalPosition;
        float off = local.DistanceTo(b.DeckPos);
        string key = $"drawn-{(bus.WorldVelocity.Length() > 5f ? "moving" : "still")}";
        // a walking b is drawn a step behind (eased, and its spot comes 30 times a second)
        float slack = b.WorldVelocity.Length() - bus.WorldVelocity.Length() > 0.5f ? 0.6f : 0.3f;
        if (_seen.Add(key) || off > slack && _seen.Add(key + "-off"))
            Check(off < slack, $"b's copy drawn on the bus {off:F2} m from the spot it published, at {bus.WorldVelocity.Length() * 3.6f:F0} km/h");
    }

    // ---- b's view, standing in the moving bus --------------------------------------------------

    private double _shotIn = -1;
    private string _shotName = "deck_standing_view";

    private void Shoot(string name = "deck_standing_view", double after = 1.5)
    {
        if (DisplayServer.GetName() == "headless") return;
        _shotName = name;
        _shotIn = after;
    }

    public override void _Process(double delta)
    {
        if (_shotIn < 0 || (_shotIn -= delta) > 0) return;
        _shotIn = -1;
        string path = ProjectSettings.GlobalizePath($"res://test_output/{_shotName}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
    }
}
