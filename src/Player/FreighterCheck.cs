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
    private static bool Shots => CmdArgs.Value("--freightercheck") == "shots";

    private readonly System.Func<FootPlayer?> _player;
    private int _failures, _shot;

    public FreighterCheck(System.Func<FootPlayer?> player) => _player = player;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[freightercheck] {(ok ? "ok  " : "FAIL")} {what}");
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

    /// <summary>An authored point at height <paramref name="y"/> (+Z forward, +X left) in the world, as the aircraft is drawn now.</summary>
    private Vector3 Spot(float x, float y, float z) => Frame()!.GlobalTransform * AircraftMeshBuilder.Flip(new Vector3(x, y, z));

    /// <summary>The player's spot, authored (x, height, z).</summary>
    private Vector3 Local(FootPlayer me) => AircraftMeshBuilder.Flip(Frame()!.GlobalTransform.AffineInverse() * me.GlobalPosition);

    private string Where(FootPlayer me) { var l = Local(me); return $"({l.X:F2}, {l.Y:F2}, {l.Z:F2})"; }

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
        for (int i = 0; i < 1800 && (_player() is null || !_player()!.IsOnFloor()); i++)
        {
            if (_player() == null && i % 50 == 25) Key(PlayerInput.ToggleMode);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        if (_player() is not { } me || !me.IsOnFloor()) { Finish("no player"); return; }
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

        // in flight below the drop speed: the ramp opens, stand up, walk aft into the hold, carried along
        me.DebugLaunch(me.GlobalPosition + Vector3.Up * 600f, -me.GlobalTransform.Basis.Z * 65f);
        await Seconds(3);
        flying.ToggleDoor(RampDoor);
        Expect((flying.DoorsOpen & 1 << RampDoor) != 0, $"the ramp opens in flight at {flying.State.Ias / 0.5144f:0} kt (a drop)");
        float y0 = me.GlobalPosition.Y;
        bool up = me.TryInteract();
        Expect(up && await Until(() => me.Aboard && me.Ride == RideKind.OnFoot, 5), $"E stood up in flight (aboard {me.Aboard}, ride {me.Ride})");
        bool walked = await WalkTo(me, 0f, HoldFrontZ + 0.6f, 15) && await WalkTo(me, 0f, 0f, 25);
        l = Local(me);
        float speed = me.Vehicle?.Kind == RideKind.Freighter ? 0f : (Parked()?.Velocity.Length() ?? 0f);
        Expect(walked && me.Aboard && Mathf.Abs(l.Y - FloorY) < 0.35f, $"walked into the hold in flight, on the floor {Where(me)}");
        Expect(me.GlobalPosition.Y > y0 - 200f, $"it flew on by itself ({me.GlobalPosition.Y - y0:+0;-0} m, {speed:F0} m/s)");
        await Until(() => !Drawn || Rig()?.DoorOpen(RampDoor) >= 1f, 10);
        await Shot("hold_in_flight_looking_forward");
        // aft, down the hold to the open ramp and the sky behind
        me.TurnView(Mathf.Pi);
        await Seconds(0.5);
        await Shot("hold_in_flight_ramp_open_looking_aft");
        me.TurnView(Mathf.Pi);
        Expect(await ToCockpit(me) && me.TryInteract() && await Until(() => me.Vehicle is Airliner && me.SeatIndex == 0, 6), $"back at the controls in flight ({me.Ride})");
        await Shot("controls_in_flight");
        Finish(null);
    }

    private void Finish(string? why)
    {
        if (why != null) Expect(false, why);
        if (_player() is { } p) p.WalkControls = null;
        GD.Print(_failures == 0 ? "[freightercheck] RESULT: ok" : $"[freightercheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
