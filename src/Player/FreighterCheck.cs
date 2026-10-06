using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;
using static UnitSport.Avatar.FreighterLayout;

namespace UnitSport.Player;

/// <summary>
/// <c>--freightercheck [shots]</c> offline on <c>--world fixture</c> (#420): the military freighter walked,
/// on the ground and in flight, on the real deck mechanism (<c>walk-aboard</c>).
/// <list type="bullet">
/// <item>at the controls, stopped: G lowers the ramp and opens the crew door;</item>
/// <item>E stands the captain up on the flight deck; down the stairs into the hold; E at a troop seat
/// sits (facing across), E stands up;</item>
/// <item>down the open ramp onto the ground, off the aircraft; back up the ramp into the hold;</item>
/// <item>the left para door's button inside opens it, the crew door's button shuts it;</item>
/// <item>up the stairs, E at the captain's seat: the controls; G shuts the ramp;</item>
/// <item>put up at 600 m at 65 m/s: the ramp opens in flight (a drop), E stands up, the walk aft into
/// the hold is carried along on the floor; back at the controls.</item>
/// </list>
/// Windowed with <c>shots</c>: <c>test_output/freighter/*.png</c> at each stage. RESULT line at the end.
/// </summary>
public partial class FreighterCheck : Node
{
    public static bool Requested => CmdArgs.Has("--freightercheck");
    private static bool Shots => CmdArgs.Value("--freightercheck") is "shots" or "carshots";
    /// <summary><c>car</c>: a car driven up the ramp into the hold instead (#418's carrying, the freighter's CargoBay).</summary>
    private static bool CarMode => CmdArgs.Value("--freightercheck") is "car" or "carshots";

    private readonly System.Func<FootPlayer?> _player;
    private int _failures, _shot, _impacts;

    public FreighterCheck(System.Func<FootPlayer?> player) => _player = player;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[freightercheck] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    private async Task Shot(string name)
    {
        if (!Shots) return;
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/freighter");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:00}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[freightercheck] wrote {path}");
    }

    /// <summary>A picture from a camera of its own, at an authored eye looking at an authored point of the aircraft.</summary>
    private async Task ShotAt(string name, Vector3 eye, Vector3 at)
    {
        if (!Shots || !Drawn || Frame() is not { } frame) return;
        var was = GetViewport().GetCamera3D();
        var cam = new Camera3D { Fov = 60f, Far = 4000f };
        AddChild(cam);
        cam.GlobalPosition = frame.GlobalTransform * AircraftMeshBuilder.Flip(eye);
        cam.LookAt(frame.GlobalTransform * AircraftMeshBuilder.Flip(at), Vector3.Up);
        cam.MakeCurrent();
        await Shot(name);
        cam.QueueFree();
        was?.MakeCurrent();
    }

    private static bool Drawn => DisplayServer.GetName() != "headless";

    /// <summary>The drawn freighter, wherever it is (driven: the player's visual; parked: a vehicle's).</summary>
    private AirlinerRig? Rig() => Frame() as AirlinerRig;

    private VehicleBody? Parked() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.Freighter && !v.Wrecked);

    /// <summary>The aircraft's frame: the drawn rig, or headless a parked one's empty posed frame.</summary>
    private Node3D? Frame()
    {
        if (_player() is { } me && me.GetChildren().OfType<AirlinerRig>().FirstOrDefault(r => !r.IsQueuedForDeletion()) is { } own) return own;
        return Parked()?.Visual;
    }

    /// <summary>The doors as the walk sees them: the player's own aircraft, or the parked one.</summary>
    private byte Doors(FootPlayer me) => me.Vehicle is Airliner own ? own.DoorsOpen : Parked()?.BusDoors ?? 0;

    /// <summary>
    /// The aircraft's frame as the walker knows it: aboard, the deck's frame it is carried in (#542);
    /// the aircraft as drawn now leads it by its motion since the last frame, 1.1 m a physics step
    /// at 68 m/s and several in a long frame, and the walk aimed that far ahead of the spot.
    /// </summary>
    private Transform3D Pose() => _player()?.DeckFrame ?? Frame()!.GlobalTransform;

    /// <summary>An authored point at height <paramref name="y"/> (+Z forward, +X left) in the world, as the walker's deck stands now.</summary>
    private Vector3 Spot(float x, float y, float z) => Pose() * AircraftMeshBuilder.Flip(new Vector3(x, y, z));

    /// <summary>The player's spot, authored (x, height, z).</summary>
    private Vector3 Local(FootPlayer me) => AircraftMeshBuilder.Flip(Pose().AffineInverse() * me.GlobalPosition);

    private string Where(FootPlayer me) { var l = Local(me); return $"({l.X:F2}, {l.Y:F2}, {l.Z:F2})"; }

    /// <summary>Turns the view to an authored spot of the aircraft (the shots: forward, aft at the ramp).</summary>
    private void Face(FootPlayer me, float x, float z)
    {
        var d = Spot(x, FloorY, z) - me.GlobalPosition;
        me.LookYaw = Mathf.Atan2(-d.X, -d.Z);
    }

    private async Task<bool> WalkTo(FootPlayer me, float x, float z, double seconds)
    {
        me.WalkControls = () =>
        {
            var target = Spot(x, 0f, z);
            var to = (target - me.GlobalPosition) with { Y = 0 };
            return (to.Length() < 0.2f ? Vector3.Zero : to.Normalized(), false);
        };
        bool there = await Until(() => ((Spot(x, 0f, z) - me.GlobalPosition) with { Y = 0 }).Length() < 0.45f, seconds);
        me.WalkControls = () => (Vector3.Zero, false);
        await Seconds(0.3);
        return there;
    }

    private static void Key(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    private async Task<bool> ToCockpit(FootPlayer me) =>
        await WalkTo(me, 0f, StairFootZ - 0.6f, 30) && await WalkTo(me, 0f, HoldFrontZ + 1.0f, 15)
        && await WalkTo(me, CaptainHip.X, CaptainHip.Z - 0.8f, 10);

    public override async void _Ready()
    {
        await Seconds(2);
        // offline the world starts on the free camera: the player comes with the mode key
        // the world loads on threads: a wall-clock bound, as the runner's --fixed-fps outruns them
        ulong deadline = Time.GetTicksMsec() + 30_000;
        for (int i = 0; Time.GetTicksMsec() < deadline && (_player() is null || !_player()!.IsOnFloor()); i++)
        {
            if (_player() == null && i % 50 == 25) Key(PlayerInput.ToggleMode);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        if (_player() is not { } me || !me.IsOnFloor()) { Finish("no player"); return; }
        if (CarMode) { await CarStage(me); Finish(null); return; }
        me.Announced += (text, good) => GD.Print($"[freightercheck] announce: {text}");
        me.Impacted += lost => { _impacts++; GD.Print($"[freightercheck] impact: lost {lost:F1} m/s, ride {me.Ride}"); };
        Expect(me.SetRide(RideKind.Freighter), "at the controls of the military freighter");
        await Seconds(2);
        if (me.Vehicle is not Airliner jet) { Finish("not an airliner"); return; }
        Expect(jet.Seats.Length == 2 + 2 * TroopRows && jet.Decks[0].CargoBays.Length == 1,
            $"{jet.Seats.Length} seats, {jet.Decks[0].CargoBays.Length} cargo bay");
        await Shot("parked");

        // G at the controls, stopped: the ramp down and the crew door open
        await Until(() => jet.MayOpenDoors, 8);
        GD.Print($"[freightercheck] before G: on the ground {jet.State.OnGround}, gear down {jet.State.GearDown}, may open {jet.MayOpenDoors}, {jet.State.Velocity.Length():F2} m/s");
        Key(PlayerInput.CarDoor);
        await Seconds(0.5);
        GD.Print($"[freightercheck] after G: doors {jet.DoorsOpen}, gear down {jet.State.GearDown}");
        await Until(() => !Drawn || Rig()?.DoorOpen(RampDoor) >= 1f, 12);
        Expect(jet.DoorsOpen == (1 << RampDoor | 1 << CrewDoor) && (!Drawn || Rig()?.DoorOpen(RampDoor) >= 1f && Rig()?.DoorOpen(CrewDoor) >= 1f),
            $"G lowered the ramp and opened the crew door (doors {jet.DoorsOpen})");
        await Shot("ramp_open");
        // from low behind and beside: the sides stop at the skin, only the ramp reaches the ground (#545)
        await ShotAt("ramp_open_rear_quarter", new Vector3(-9f, 2f, -21f), new Vector3(0, 1.6f, -8f));
        await ShotAt("ramp_open_side", new Vector3(-13f, 1.4f, -8.5f), new Vector3(0, 1.6f, -8.5f));

        // up from the captain's seat onto the flight deck, then down the stairs into the hold
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.Ride == RideKind.OnFoot, 5),
            $"E stood the captain up (aboard {me.Aboard}, ride {me.Ride})");
        await Seconds(1);
        var l = Local(me);
        Expect(l.Z > HoldFrontZ && Mathf.Abs(l.Y - FlightDeckY) < 0.3f, $"standing on the flight deck {Where(me)}");
        await Shot("flight_deck");
        bool down = await WalkTo(me, 0f, HoldFrontZ + 0.6f, 15) && await WalkTo(me, 0f, StairFootZ - 0.8f, 15);
        l = Local(me);
        Expect(down && Mathf.Abs(l.Y - FloorY) < 0.3f, $"down the stairs into the hold {Where(me)}");
        await Shot("hold");

        // a troop seat: E sits facing across, E stands up
        var stand = FreighterDeck.StandSpot(2 + 2 * 3)!.Value;
        var standAuth = AircraftMeshBuilder.Flip(stand);
        bool atSeat = await WalkTo(me, standAuth.X, standAuth.Z, 15);
        Expect(atSeat && me.TryInteract() && await Until(() => me.SeatIndex > 1, 5), $"E sat in a troop seat (seat {me.SeatIndex})");
        await Shot("troop_seat");
        await Seconds(1);
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.SeatIndex == 0 && me.Ride == RideKind.OnFoot, 5), "E stood up into the hold");

        // down the open ramp onto the ground, then back up it
        bool outside = await WalkTo(me, 0f, RampHingeZ - 0.3f, 20) && await WalkTo(me, 0f, RampToeZ - 2.5f, 20);
        l = Local(me);
        Expect(outside && !me.Aboard && Mathf.Abs(l.Y) < 0.3f, $"walked down the ramp onto the ground {Where(me)}, aboard {me.Aboard}");
        await Shot("down_the_ramp");
        bool upRamp = await WalkTo(me, 0f, RampHingeZ + 1.5f, 20);
        l = Local(me);
        Expect(upRamp && me.Aboard && Mathf.Abs(l.Y - FloorY) < 0.3f, $"walked up the ramp into the hold {Where(me)}, aboard {me.Aboard}");

        // the left para door's button inside opens it; the crew door's shuts that one
        bool atPara = await WalkTo(me, HoldHalfWidth - 0.55f, ParaDoorZ + DoorWidth * 0.5f + 0.25f, 15);
        var button = me.ButtonInReach();
        bool pressed = me.TryInteract();
        Expect(atPara && button?.Door == ParaDoorL && pressed && await Until(() => (Doors(me) & 1 << ParaDoorL) != 0, 5),
            $"the para door's button opened it (button {button?.Door}, doors {Doors(me)})");
        await Until(() => !Drawn || Rig()?.DoorOpen(ParaDoorL) >= 1f, 5);
        await Shot("para_door");
        bool atCrew = await WalkTo(me, HoldHalfWidth - 0.55f, CrewDoorZ + DoorWidth * 0.5f + 0.25f, 25);
        button = me.ButtonInReach();
        pressed = me.TryInteract();
        Expect(atCrew && button?.Door == CrewDoor && pressed && await Until(() => (Doors(me) & 1 << CrewDoor) == 0, 5),
            $"the crew door's button shut it (button {button?.Door}, doors {Doors(me)})");

        // up the stairs, the controls again
        bool cockpit = await WalkTo(me, 0f, StairFootZ - 0.4f, 15) && await WalkTo(me, 0f, HoldFrontZ + 1.0f, 15)
            && await WalkTo(me, CaptainHip.X, CaptainHip.Z - 0.8f, 10);
        Expect(cockpit, $"walked up the stairs to the captain's seat {Where(me)}");
        bool took = me.TryInteract();
        Expect(took && await Until(() => me.Vehicle is Airliner && me.SeatIndex == 0, 6), $"E at the captain's seat: the controls (ride {me.Ride}, seat {me.SeatIndex})");
        if (me.Vehicle is not Airliner flying) { Finish("not back at the controls"); return; }
        flying.ToggleDoor(ParaDoorL);
        Key(PlayerInput.CarDoor);
        await Until(() => flying.DoorsOpen == 0 && (!Drawn || Rig()?.DoorOpen(RampDoor) <= 0f), 12);
        Expect(flying.DoorsOpen == 0, $"G and the para door's toggle shut everything (doors {flying.DoorsOpen})");
        Expect(me.Vehicle == flying, $"still at the controls once the ramp is shut (ride {me.Ride}, aboard {me.Aboard})");
        if (me.Vehicle != flying)
        {
            // windowed it was seen standing up meanwhile: back at the controls for the flight stage
            me.SetRide(RideKind.Freighter);
            await Seconds(1);
            if (me.Vehicle is not Airliner again) { Finish("no aircraft for the flight stage"); return; }
            flying = again;
        }

        // in flight below the drop speed: the ramp opens, stand up, walk aft into the hold, carried along.
        // Past the settling after taking the controls, as windowed (the ramp's animation takes that long):
        // headless the launch came inside it and never saw the stale floor contact that wrecked it (#456).
        await Until(() => flying.State.Settle <= 0f, 5);
        float y0Launch = me.GlobalPosition.Y, health0 = me.VehicleHealth;
        int impacts0 = _impacts;
        me.DebugLaunch(me.GlobalPosition + Vector3.Up * 600f, -me.GlobalTransform.Basis.Z * 65f);
        await Seconds(3);
        GD.Print($"[freightercheck] launched: ride {me.Ride}, same aircraft {me.Vehicle == flying}, on the ground {flying.State.OnGround}, {flying.State.Ias / 0.5144f:0} kt, {me.GlobalPosition.Y - y0Launch:F0} m up");
        Expect(me.Vehicle == flying && !flying.State.OnGround && me.VehicleHealth >= health0,
            $"launched at 600 m: flying on, unhurt (ride {me.Ride}, health {me.VehicleHealth:F0} of {health0:F0})");
        if (me.Vehicle != flying) { Finish("the aircraft was lost at the launch"); return; }
        flying.ToggleDoor(RampDoor);
        Expect((flying.DoorsOpen & 1 << RampDoor) != 0, $"the ramp opens in flight at {flying.State.Ias / 0.5144f:0} kt (a drop)");
        float y0 = me.GlobalPosition.Y;
        bool up = me.TryInteract();
        Expect(up && await Until(() => me.Aboard && me.Ride == RideKind.OnFoot, 5), $"E stood up in flight (aboard {me.Aboard}, ride {me.Ride})");
        // on the flight deck of the aircraft as it flies on, pitched as it was: not left level, the pilot on its roof (#456)
        await Seconds(1);
        l = Local(me);
        Expect(me.Aboard && Mathf.Abs(l.Y - FlightDeckY) < 0.3f, $"stood up in flight onto the flight deck {Where(me)}, pitch {Mathf.RadToDeg(Frame()!.GlobalRotation.X):F1}°");
        bool walked = await WalkTo(me, 0f, HoldFrontZ + 0.6f, 15) && await WalkTo(me, 0f, 0f, 25);
        l = Local(me);
        float speed = me.Vehicle?.Kind == RideKind.Freighter ? 0f : (Parked()?.Velocity.Length() ?? 0f);
        Expect(walked && me.Aboard && Mathf.Abs(l.Y - FloorY) < 0.35f, $"walked into the hold in flight, on the floor {Where(me)}");
        Expect(me.GlobalPosition.Y > y0 - 200f, $"it flew on by itself ({me.GlobalPosition.Y - y0:+0;-0} m, {speed:F0} m/s)");
        await LongFrames(me);
        await Until(() => !Drawn || Rig()?.DoorOpen(RampDoor) >= 1f, 10);
        Face(me, 0f, HoldFrontZ);
        await Seconds(0.5);
        await Shot("hold_in_flight_looking_forward");
        // aft, down the hold to the open ramp and the sky behind
        Face(me, 0f, RampToeZ);
        await Seconds(0.5);
        await Shot("hold_in_flight_ramp_open_looking_aft");
        // out onto the open ramp: level with the floor in the air (#420), walked on, not a slope down
        bool onRamp = await WalkTo(me, 0f, RampHingeZ - 1.4f, 20);
        l = Local(me);
        Expect(onRamp && me.Aboard && Mathf.Abs(l.Y - FloorY) < 0.15f, $"on the ramp in flight, level with the floor {Where(me)}");
        Face(me, 0f, RampToeZ - 20f);
        await Seconds(0.5);
        await Shot("on_the_level_ramp_in_flight");
        await WalkTo(me, 0f, 0f, 20);
        Expect(await ToCockpit(me) && me.TryInteract() && await Until(() => me.Vehicle is Airliner && me.SeatIndex == 0, 6), $"back at the controls in flight ({me.Ride})");
        await Seconds(1);
        Expect(_impacts == impacts0 && me.VehicleHealth >= health0,
            $"the controls taken back in flight without a knock ({_impacts - impacts0} impacts, health {me.VehicleHealth:F0}, pitch {Mathf.RadToDeg(AirlinerFlight.PitchOf((me.Vehicle as Airliner)?.State.Attitude ?? Basis.Identity)):F1}°)");
        await Shot("controls_in_flight");
        // at the end: a parked one stood up from flies on only over the fixture's ground (a frozen
        // body past its edge), so the walk comes first
        if (Shots)
        {
            // from outside, the chase camera: the ramp level with the hold's floor in the air (#456)
            await Until(() => Rig()?.DoorOpen(RampDoor) >= 1f, 10);
            await Shot("outside_in_flight_ramp_level");
            me.OrbitView(2.4f);
            await Seconds(0.8);
            await Shot("outside_in_flight_ramp_level_quarter");
            me.OrbitView(-2.4f);
        }
        Finish(null);
    }

    /// <summary>
    /// Standing at the aft end of the level ramp in flight through long frames (a tile's build, an
    /// origin shift): several physics steps each, in which the aircraft moves on while its deck is put
    /// where it is drawn only once a frame. Read from where the aircraft was drawn, the walker stood
    /// past the deck's end, stepped off it and fell out (#542). The physics at 16 times its rate (16
    /// steps a frame under the quick tier's <c>--fixed-fps</c>, fewer at a window's frame rate), the
    /// aircraft pushed 1.1 m a step, 68 m/s in real steps: a frame of 70 ms or more each, flown whether
    /// or not the fixture's ground still lies under it (past it a parked aircraft holds still).
    /// </summary>
    private async Task LongFrames(FootPlayer me)
    {
        bool atEnd = await WalkTo(me, 0f, RampHingeZ - RampLength + 0.15f, 15);
        if (Parked() is not { } jet) { Expect(false, "no parked aircraft to stand in"); return; }
        var forward = (Spot(0f, 0f, 1f) - Spot(0f, 0f, 0f)).Normalized();
        var from = jet.GlobalPosition;
        int steps = 0;
        void Push() { jet.GlobalPosition += forward * (68f / 60f); steps++; }
        int ticks = Engine.PhysicsTicksPerSecond, most = Engine.MaxPhysicsStepsPerFrame;
        Engine.MaxPhysicsStepsPerFrame = 16;
        Engine.PhysicsTicksPerSecond = ticks * 16;
        ulong frame0 = Engine.GetProcessFrames();
        GetTree().PhysicsFrame += Push;
        while (steps < 160) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().PhysicsFrame -= Push;
        float perFrame = steps / (float)System.Math.Max(1UL, Engine.GetProcessFrames() - frame0);
        Engine.PhysicsTicksPerSecond = ticks;
        Engine.MaxPhysicsStepsPerFrame = most;
        await Seconds(0.3);
        float moved = jet.GlobalPosition.DistanceTo(from);
        var l = Local(me);
        Expect(atEnd && perFrame >= 4f && me.Aboard && Mathf.Abs(l.Y - FloorY) < 0.15f,
            $"stood at the ramp's aft end through long frames, still aboard {Where(me)}, {perFrame:F1} steps a frame, the aircraft {moved:F0} m on");
    }

    /// <summary>
    /// A parked freighter, its ramp down: a car drives up the ramp into the hold, is carried (its
    /// <c>DeckOn</c> the freighter), tied down with the handbrake, then reverses down the ramp and out.
    /// </summary>
    private async Task CarStage(FootPlayer me)
    {
        if (VehicleManager.Instance is not { } vehicles || me.Origin is not { } origin) { Expect(false, "no vehicles"); return; }
        float yaw = me.Rotation.Y;
        var spot = me.GlobalPosition + new Basis(Vector3.Up, yaw) * Vector3.Forward * 45f;
        var jet = new Airliner(RideKind.Freighter, AirlinerCatalog.Freighter) { DoorsOpen = 1 << RampDoor };
        vehicles.Park(new VehicleState(RideKind.Freighter, origin.ToGlobal(spot), yaw, Vector3.Zero, 400f, false, false, 0f, VehicleState.Now,
            Flags: jet.PackFlags()));
        if (!await Until(() => Parked() is { Asleep: true, Posed: true }, 20)) { Expect(false, "the freighter never came to rest"); return; }
        var carrier = Parked()!;
        string key = FootPlayer.KeyOf(carrier);
        Expect(carrier.BusDoors == 1 << RampDoor, $"a parked freighter, its ramp down (doors {carrier.BusDoors})");
        var car = Rideable.Create((RideKind)CarCatalog.First)!;
        Expect(me.SetRide(car.Kind), $"in a {car.Label}");
        await Seconds(1);
        me.PlaceAt(Spot(0f, 0.3f, RampToeZ - 7f), carrier.Rotation.Y);
        await Seconds(1.5);
        await Shot("car_behind_the_ramp");
        float throttle = 0.9f, brake = 0f;
        bool handbrake = false;
        me.RideControls = () => new RideInput(throttle, brake, 0f, false, handbrake);
        bool inside = await Until(() =>
        {
            if (me.GroundSpeed > 3f) throttle = 0f; else if (me.GroundSpeed < 1.5f) throttle = 0.9f;
            return me.DeckOn == key && Local(me).Z > RampHingeZ + 3f;
        }, 40);
        throttle = 0f;
        brake = 1f;
        await Until(() => me.GroundSpeed < 0.2f, 10);
        var l = Local(me);
        Expect(inside && Mathf.Abs(l.Y - FloorY) < 0.3f, $"drove up the ramp into the hold: carried by '{me.DeckOn}' at {Where(me)}");
        brake = 0f;
        handbrake = true;
        Expect(await Until(() => me.TiedDown, 5), "the handbrake tied it down in the hold");
        handbrake = false;
        await Shot("car_in_the_hold");
        // reverse out: the brake pedal at a standstill is reverse
        brake = 0.4f;
        bool outside = await Until(() =>
        {
            brake = me.GroundSpeed > 3f ? 0f : 0.4f;
            return me.DeckOn == "" && Local(me).Z < RampToeZ - 4f;
        }, 40);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, true);
        await Seconds(2);
        l = Local(me);
        Expect(outside && Mathf.Abs(l.Y) < 0.3f, $"reversed down the ramp onto the ground at {Where(me)}, not carried ('{me.DeckOn}')");
        await Shot("car_out_of_the_hold");
        me.RideControls = null;
    }

    private void Finish(string? why)
    {
        if (why != null) Expect(false, why);
        if (_player() is { } p) p.WalkControls = null;
        GD.Print(_failures == 0 ? "[freightercheck] RESULT: ok" : $"[freightercheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
