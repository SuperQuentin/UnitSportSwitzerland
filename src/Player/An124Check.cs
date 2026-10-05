using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;
using static UnitSport.Avatar.An124Layout;

namespace UnitSport.Player;

/// <summary>
/// <c>--an124check [shots]</c> offline on <c>--world fixture</c> (#419): the AN-124 walked and loaded on
/// the real deck mechanism (<c>walk-aboard</c>, <c>vehicles-in-holds</c>).
/// <list type="bullet">
/// <item>at the controls, stopped: G opens the crew door, the visor and nose ramp, the rear ramp, and
/// kneels; the thrust levers do not move it while knelt; the crew door's sill comes down to 2.45 m;</item>
/// <item>E stands the pilot up in the cockpit; the flight engineer's seat and a cabin seat; aft through
/// the upper deck, down the ladder into the hold;</item>
/// <item>down the nose ramp onto the ground and back, down the rear ramp and back;</item>
/// <item>the kneeling button inside raises it (the floor back at 3.3 m) and kneels it again;</item>
/// <item>up the ladder, the controls, G shuts everything and it rises.</item>
/// </list>
/// <c>car</c> / <c>bus</c>: a parked, knelt one with both ends open; a car (or a Citaro) drives up the nose
/// ramp, through the hold (carried, tied down with the handbrake, let go), down the rear ramp and out.
/// Windowed with <c>shots</c> (<c>carshots</c>, <c>busshots</c>): <c>test_output/an124/*.png</c> from a
/// camera of its own outside. RESULT line at the end.
/// </summary>
public partial class An124Check : Node
{
    private static string Mode => CmdArgs.Value("--an124check") ?? "";
    public static bool Requested => CmdArgs.Has("--an124check");
    private static bool Shots => Mode.EndsWith("shots");
    private static string Load => Mode.StartsWith("bus") ? "bus" : Mode.StartsWith("car") ? "car" : "";

    private readonly System.Func<FootPlayer?> _player;
    private int _failures, _shot;

    public An124Check(System.Func<FootPlayer?> player) => _player = player;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[an124check] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(System.Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    private static bool Drawn => DisplayServer.GetName() != "headless";

    /// <summary>A picture from a camera of its own, at an authored eye looking at an authored point of the aircraft.</summary>
    private async Task Shot(string name, Vector3 eye, Vector3 at)
    {
        if (Frame() is { } frame) await ShotWorld(name, frame.GlobalTransform * AircraftMeshBuilder.Flip(eye), frame.GlobalTransform * AircraftMeshBuilder.Flip(at));
    }

    private async Task ShotWorld(string name, Vector3 eye, Vector3 at)
    {
        if (!Shots || !Drawn) return;
        var was = GetViewport().GetCamera3D();
        var cam = new Camera3D { Fov = 60f, Far = 4000f };
        AddChild(cam);
        cam.GlobalPosition = eye;
        cam.LookAt(at, Vector3.Up);
        cam.MakeCurrent();
        for (int i = 0; i < 8; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/an124");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{Load}{++_shot:00}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[an124check] wrote {path}");
        cam.QueueFree();
        was?.MakeCurrent();
    }

    private VehicleBody? Parked() => VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.An124 && !v.Wrecked);

    private Node3D? Frame()
    {
        if (_player() is { } me && me.GetChildren().OfType<AirlinerRig>().FirstOrDefault(r => !r.IsQueuedForDeletion()) is { } own) return own;
        return Parked()?.Visual;
    }

    private Airliner? Jet(FootPlayer me) => me.Vehicle as Airliner ?? Parked()?.Ride as Airliner;
    private byte Doors(FootPlayer me) => me.Vehicle is Airliner own ? own.DoorsOpen : Parked()?.BusDoors ?? 0;

    private Vector3 Spot(float x, float y, float z) => Frame()!.GlobalTransform * AircraftMeshBuilder.Flip(new Vector3(x, y, z));
    private Vector3 Local(FootPlayer me) => AircraftMeshBuilder.Flip(Frame()!.GlobalTransform.AffineInverse() * me.GlobalPosition);
    private string Where(FootPlayer me) { var l = Local(me); return $"({l.X:F2}, {l.Y:F2}, {l.Z:F2})"; }

    private async Task<bool> WalkTo(FootPlayer me, float x, float z, double seconds)
    {
        me.WalkControls = () =>
        {
            var to = (Spot(x, 0f, z) - me.GlobalPosition) with { Y = 0 };
            return (to.Length() < 0.2f ? Vector3.Zero : to.Normalized(), false);
        };
        bool there = await Until(() => ((Spot(x, 0f, z) - me.GlobalPosition) with { Y = 0 }).Length() < 0.45f, seconds);
        me.WalkControls = () => (Vector3.Zero, false);
        await Seconds(0.3);
        return there;
    }

    private async Task<bool> Path(FootPlayer me, params (float X, float Z)[] points)
    {
        foreach (var (x, z) in points)
            if (!await WalkTo(me, x, z, 25)) { GD.Print($"[an124check] stuck short of ({x}, {z}) at {Where(me)}"); return false; }
        return true;
    }

    private static void Key(string action, bool pressed = true)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = pressed });
        if (pressed) Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    /// <summary>The ground under the aircraft, frame space: kneeling lowers the frame onto it.</summary>
    private float GroundY(FootPlayer me) => Jet(me)?.KneelShown * KneelDrop ?? 0f;

    public override async void _Ready()
    {
        await Seconds(2);
        for (int i = 0; i < 1800 && (_player() is null || !_player()!.IsOnFloor()); i++)
        {
            if (_player() == null && i % 50 == 25) Key(PlayerInput.ToggleMode);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        if (_player() is not { } me || !me.IsOnFloor()) { Finish("no player"); return; }
        if (Load != "") { await LoadStage(me); Finish(null); return; }
        await Walk(me);
        Finish(null);
    }

    private async Task Walk(FootPlayer me)
    {
        Expect(me.SetRide(RideKind.An124), "at the controls of the AN-124");
        await Seconds(2);
        if (me.Vehicle is not Airliner jet) { Expect(false, "not an airliner"); return; }
        Expect(jet.Seats.Length == 3 + CabinRows * CabinSeatX.Length && jet.Decks[0].CargoBays.Length == 1,
            $"{jet.Seats.Length} seats, {jet.Decks[0].CargoBays.Length} cargo bay");
        await Shot("parked", new Vector3(34f, 9f, 52f), new Vector3(0, 6f, 2f));
        await Shot("parked_side", new Vector3(64f, 5f, -2f), new Vector3(0, 5.5f, -2f));
        var sill = AirstairsDock.LocalSill(jet.Decks[0], CrewDoor);
        float sillStand = sill is { } s0 ? (Frame()!.GlobalTransform * s0.Edge).Y - me.GlobalPosition.Y : -1f;

        // G at the controls, stopped: every door open and it kneels
        await Until(() => jet.MayOpenDoors, 8);
        Key(PlayerInput.CarDoor);
        await Until(() => jet.KneelShown >= 1f && (!Drawn || (Frame() as AirlinerRig)?.DoorOpen(NoseDoor) >= 1f), 15);
        Expect(jet.DoorsOpen == 15 && jet.KneelShown >= 1f, $"G opened the doors and knelt (doors {jet.DoorsOpen}, kneel {jet.KneelShown:F2})");
        float sillKnelt = sill is { } s1 ? (Frame()!.GlobalTransform * s1.Edge).Y - me.GlobalPosition.Y : -1f;
        Expect(Mathf.Abs(sillStand - FloorY) < 0.15f && Mathf.Abs(sillKnelt - (FloorY - KneelDrop)) < 0.15f,
            $"the crew door's sill {sillStand:F2} m standing, {sillKnelt:F2} m knelt (airstairs from {AirstairsLayout.MinHeight} m)");
        await Shot("visor_up_nose_ramp_down", new Vector3(16f, 7f, 50f), new Vector3(0, 4f, 22f));
        await Shot("rear_ramp_down", new Vector3(-18f, 7f, -48f), new Vector3(0, 4f, -16f));
        await Shot("kneeling_side", new Vector3(30f, 2.5f, 20f), new Vector3(0, 2f, 14f));
        // knelt, the levers do not move it
        var from = me.GlobalPosition;
        Input.ActionPress(PlayerInput.Sprint);
        await Seconds(3);
        Input.ActionRelease(PlayerInput.Sprint);
        Expect(me.GlobalPosition.DistanceTo(from) < 0.5f, $"knelt it holds its brakes under thrust (moved {me.GlobalPosition.DistanceTo(from):F2} m)");

        // up from the pilot's seat in the cockpit
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.Ride == RideKind.OnFoot, 5), $"E stood the pilot up (aboard {me.Aboard})");
        await Seconds(1);
        var l = Local(me);
        Expect(l.Z > CabinFrontZ && Mathf.Abs(l.Y - UpperFloorY) < 0.3f, $"standing in the cockpit {Where(me)}");
        await Shot("cockpit", new Vector3(0.9f, UpperFloorY + 1.75f, 23.6f), new Vector3(0, UpperFloorY + 0.9f, 28f));
        // the flight engineer's seat
        var eng = AircraftMeshBuilder.Flip(An124Deck.StandSpot(2)!.Value);
        Expect(await WalkTo(me, eng.X, eng.Z, 10) && me.TryInteract() && await Until(() => me.SeatIndex == 2, 5), $"E sat in the flight engineer's seat (seat {me.SeatIndex})");
        await Seconds(0.5);
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.SeatIndex == 0 && me.Ride == RideKind.OnFoot, 5), "E stood up");
        // aft into the cabin: a seat on the upper deck
        int cabinSeat = 3 + 2 * CabinSeatX.Length + 1;
        var spot = AircraftMeshBuilder.Flip(An124Deck.StandSpot(cabinSeat)!.Value);
        Expect(await Path(me, (0f, 23.6f), (0f, 22.4f), (spot.X, spot.Z)) && me.TryInteract() && await Until(() => (me.SeatIndex - 3) / CabinSeatX.Length == (cabinSeat - 3) / CabinSeatX.Length, 5),
            $"E sat in a seat on the upper deck, row {(cabinSeat - 3) / CabinSeatX.Length} (seat {me.SeatIndex})");
        await Shot("upper_deck", new Vector3(-0.3f, UpperFloorY + 1.7f, 15.6f), new Vector3(0, UpperFloorY + 0.8f, 23f));
        await Shot("upper_deck_windows", new Vector3(-0.9f, UpperFloorY + 1.45f, 13.4f), new Vector3(2.6f, UpperFloorY + 1.0f, 16.6f));
        Expect(me.TryInteract() && await Until(() => me.Aboard && me.SeatIndex == 0 && me.Ride == RideKind.OnFoot, 5), "E stood up into the aisle");

        // down the ladder into the hold
        bool down = await Path(me, (0f, 16.2f), (LadderX, 15.7f), (LadderX, LadderFootZ - 0.8f));
        l = Local(me);
        Expect(down && Mathf.Abs(l.Y - FloorY) < 0.3f, $"down the ladder into the hold {Where(me)}");
        await Shot("hold", new Vector3(1.5f, FloorY + 1.7f, -12.5f), new Vector3(0, FloorY + 1.2f, 20f));

        // down the nose ramp onto the ground, back up; then the same at the rear
        bool nose = await Path(me, (0f, 21f), (0f, NoseToeZ(true) + 2.5f));
        l = Local(me);
        Expect(nose && !me.Aboard && Mathf.Abs(l.Y - GroundY(me)) < 0.3f, $"walked down the nose ramp onto the ground {Where(me)}, aboard {me.Aboard}");
        bool back = await WalkTo(me, 0f, 20f, 25);
        Expect(back && me.Aboard && Mathf.Abs(Local(me).Y - FloorY) < 0.3f, $"back up the nose ramp {Where(me)}");
        bool rear = await Path(me, (0f, -12f), (0f, RearToeZ(true) - 2.5f));
        l = Local(me);
        Expect(rear && !me.Aboard && Mathf.Abs(l.Y - GroundY(me)) < 0.3f, $"walked the hold and down the rear ramp {Where(me)}, aboard {me.Aboard}");
        back = await WalkTo(me, 0f, -10f, 25);
        Expect(back && me.Aboard && Mathf.Abs(Local(me).Y - FloorY) < 0.3f, $"back up the rear ramp {Where(me)}");

        // the kneeling button by the crew door: it rises, then kneels again
        bool atButton = await Path(me, (1.5f, 10f), (HoldHalfWidth - 0.55f, CrewDoorZ + DoorWidth * 0.5f + An124Deck.KneelButtonAhead + 0.2f));
        var button = me.ButtonInReach();
        // stood up, the aircraft is parked: its own ride object now, not the one flown
        float Kneel() => Jet(me)?.KneelShown ?? -1f;
        Expect(atButton && button?.Door == KneelDoor && me.TryInteract() && await Until(() => Kneel() <= 0f, 12),
            $"the kneeling button raised it (button {button?.Door}, kneel {Kneel():F2})");
        Expect(Mathf.Abs(Local(me).Y - FloorY) < 0.3f && Mathf.Abs(me.GlobalPosition.Y - Spot(0, 0, 0).Y - FloorY) < 0.3f,
            $"carried up on the floor as it rose {Where(me)}");
        Expect(me.TryInteract() && await Until(() => Kneel() >= 1f, 12), $"and knelt it again (kneel {Kneel():F2})");

        // up the ladder, the controls, G shuts everything and it stands up
        bool up = await Path(me, (LadderX, LadderFootZ - 1.0f), (LadderX, UpperRearZ + 1.0f), (0f, 16.4f), (0f, 22.4f), (0f, 23.6f), (PilotHip.X, PilotHip.Z - 0.85f));
        Expect(up && Mathf.Abs(Local(me).Y - UpperFloorY) < 0.3f, $"up the ladder to the pilot's seat {Where(me)}");
        Expect(me.TryInteract() && await Until(() => me.Vehicle is Airliner && me.SeatIndex == 0, 6), $"E at the pilot's seat: the controls ({me.Ride})");
        if (me.Vehicle is not Airliner flying) return;
        Key(PlayerInput.CarDoor);
        await Until(() => flying.DoorsOpen == 0 && flying.KneelShown <= 0f && (!Drawn || (Frame() as AirlinerRig)?.DoorOpen(RearDoor) <= 0f), 15);
        Expect(flying.DoorsOpen == 0 && flying.KneelShown <= 0f, $"G shut everything and it stood up (doors {flying.DoorsOpen}, kneel {flying.KneelShown:F2})");
        await Shot("ready_to_taxi", new Vector3(30f, 9f, 45f), new Vector3(0, 6f, 0f));
    }

    /// <summary>A parked, knelt AN-124 with both ends open: a car or a bus drives in at the nose, through the hold, and out at the tail.</summary>
    private async Task LoadStage(FootPlayer me)
    {
        if (VehicleManager.Instance is not { } vehicles || me.Origin is not { } origin) { Expect(false, "no vehicles"); return; }
        float yaw = me.Rotation.Y;
        var at = me.GlobalPosition + new Basis(Vector3.Up, yaw) * Vector3.Forward * 70f;
        var jet = new Airliner(RideKind.An124, AirlinerCatalog.An124) { DoorsOpen = 1 << NoseDoor | 1 << RearDoor | 1 << KneelDoor };
        vehicles.Park(new VehicleState(RideKind.An124, origin.ToGlobal(at), yaw, Vector3.Zero, 400f, false, false, 0f, VehicleState.Now,
            Flags: jet.PackFlags()));
        if (!await Until(() => Parked() is { Posed: true, Ride: Airliner { KneelShown: >= 1f } }, 30)) { Expect(false, "the AN-124 never knelt at rest"); return; }
        var carrier = Parked()!;
        string key = FootPlayer.KeyOf(carrier);
        Expect(carrier.BusDoors == jet.DoorsOpen, $"a parked AN-124, knelt, both ends open (doors {carrier.BusDoors})");
        var kind = Load == "bus" ? (RideKind)98 : (RideKind)CarCatalog.First;
        var ride = Rideable.Create(kind)!;
        Expect(me.SetRide(kind), $"in a {ride.Label}");
        await Seconds(1);
        // facing aft, in front of the nose ramp
        me.PlaceAt(Spot(0f, KneelDrop + 0.3f, NoseToeZ(true) + 9f), carrier.Rotation.Y + Mathf.Pi);
        await Seconds(2);
        await Shot("before_the_nose_ramp", new Vector3(16f, 15f, NoseToeZ(true) + 24f), new Vector3(0, 3f, NoseHingeZ));
        float throttle = 0.9f, brake = 0f;
        bool handbrake = false;
        me.RideControls = () => new RideInput(throttle, brake, 0f, false, handbrake);
        void Cruise() { if (me.GroundSpeed > 3f) throttle = 0f; else if (me.GroundSpeed < 1.5f) throttle = Load == "bus" ? 0.6f : 0.9f; }
        // a chase picture beside it as it climbs the ramp
        await Until(() => { Cruise(); return Local(me).Z < NoseHingeZ + 4f; }, 60);
        if (Shots)
        {
            var paused = me.RideControls;
            me.RideControls = () => new RideInput(0f, 1f, 0f, false, false);
            var side = me.GlobalTransform.Basis;
            await ShotWorld("driving_in", me.GlobalPosition + side.X * 9f + side.Z * 13f + Vector3.Up * 6f, me.GlobalPosition + Vector3.Up * 2f - side.Z * 4f);
            me.RideControls = paused;
        }
        bool inside = await Until(() => { Cruise(); return me.DeckOn == key && Local(me).Z < NoseHingeZ - 9f; }, 60);
        Expect(inside && Mathf.Abs(Local(me).Y - FloorY) < 0.35f, $"drove up the nose ramp into the hold: carried by '{me.DeckOn}' at {Where(me)}");
        bool mid = await Until(() => { Cruise(); return Local(me).Z < 2f; }, 40);
        throttle = 0f;
        brake = 1f;
        await Until(() => me.GroundSpeed < 0.2f, 10);
        brake = 0f;
        handbrake = true;
        Expect(mid && await Until(() => me.TiedDown, 5), $"stopped mid-hold, tied down with the handbrake {Where(me)}");
        handbrake = false;
        await Shot("in_the_hold", new Vector3(-1.0f, FloorY + 2.0f, -12f), new Vector3(0, FloorY + 1.5f, 6f));
        // the throttle lets it go; on through the hold and down the rear ramp
        throttle = 0.6f;
        bool outside = await Until(() => { Cruise(); return me.DeckOn == "" && Local(me).Z < RearToeZ(true) - 7f; }, 60);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false, true);
        await Seconds(2);
        var l = Local(me);
        // off the ramp, on the ground (a real apron is not level with the aircraft's frame: below the floor is enough)
        Expect(outside && l.Y < FloorY - 1.5f && me.IsOnFloor(), $"drove down the rear ramp onto the ground at {Where(me)}, not carried ('{me.DeckOn}')");
        await Shot("out_at_the_rear", new Vector3(-20f, 6f, RearToeZ(true) - 20f), new Vector3(0, 3f, -16f));
        me.RideControls = null;
    }

    private void Finish(string? why)
    {
        if (why != null) Expect(false, why);
        if (_player() is { } p) { p.WalkControls = null; p.RideControls = null; }
        GD.Print(_failures == 0 ? "[an124check] RESULT: ok" : $"[an124check] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
