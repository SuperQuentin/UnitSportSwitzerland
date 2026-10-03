using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Vehicles;
using static UnitSport.Avatar.FreighterLayout;

namespace UnitSport.Player;

/// <summary>
/// <c>--freighternet A|B</c> with <c>--connect</c> on <c>--world fixture</c> (driven by
/// <c>tools/freighternetcheck.sh</c>, #420): the military freighter's doors over the network.
/// <list type="bullet">
/// <item>A (admin) takes a freighter and lowers the ramp with G (the crew door with it): B's copy
/// shows both open (the pose bits) and draws the ramp down.</item>
/// <item>A gets out: it stands parked with its ramp down (<c>VehicleState.Flags</c>); B walks up the
/// ramp into the hold from behind and is aboard on its floor.</item>
/// <item>B presses the ramp's button inside: A sees the parked freighter's ramp shut.</item>
/// </list>
/// </summary>
public partial class FreighterNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--freighternet");

    public FreighterNetProbe(ItemController items) : base(items, "freighternet", "FN") { }
    public FreighterNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private const byte RampAndCrew = 1 << RampDoor | 1 << CrewDoor;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    private FootPlayer? Other(FootPlayer me)
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != me && !p.Npc) return p;
        return null;
    }

    private static VehicleBody? Parked() =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.Freighter && !v.Wrecked);

    private async Task RunA(FootPlayer me)
    {
        Chat?.Send("/login test");
        await Seconds(1.5);
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        Expect(me.SetRide(RideKind.Freighter), "A takes a military freighter");
        await Seconds(2);
        if (me.Vehicle is not Airliner jet) { Fail("not an airliner"); return; }
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.CarDoor, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.CarDoor, Pressed = false });
        await Seconds(0.5);
        Expect(jet.DoorsOpen == RampAndCrew, $"G lowered the ramp and opened the crew door (doors {jet.DoorsOpen})");
        Say("ramp open");
        if (!await Heard("B", "seen ramp", 40)) Fail("B never saw the ramp");

        me.ExitVehicle();
        await Seconds(2);
        Expect(me.Ride == RideKind.OnFoot && await Until(() => Parked() is { BusDoors: RampAndCrew }, 10),
            $"A got out, it stands parked with the ramp down (doors {Parked()?.BusDoors})");
        // out of the way, beside the nose
        me.PlaceAt(me.GlobalPosition + new Vector3(0f, 0f, -40f), 0f);
        Say("parked");
        if (!await Heard("B", "pressed", 90)) { Fail("B never pressed the ramp's button"); return; }
        Expect(await Until(() => Parked() is { } v && (v.BusDoors & 1 << RampDoor) == 0, 10),
            $"A sees the ramp shut by B's button (doors {Parked()?.BusDoors})");
        Say("seen shut");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        // out from under the aircraft A is about to take where both spawned
        me.PlaceAt(me.GlobalPosition + new Vector3(70f, 0f, 0f), 0f);
        await Seconds(1.5);
        Say("ready");
        if (!await Heard("A", "ramp open", 40)) { Fail("A never lowered the ramp"); return; }
        FootPlayer? a = null;
        AirlinerRig? Rig() => (a = Other(me))?.GetChildren().OfType<AirlinerRig>().FirstOrDefault();
        bool seen = await Until(() => Rig() != null && a!.Ride == RideKind.Freighter && Airliner.LookOf(a.Anim).Doors == RampAndCrew
            && (DisplayServer.GetName() == "headless" || Rig()!.DoorOpen(RampDoor) >= 1f), 20);
        Expect(seen, $"B sees A's ramp and crew door open (doors {(a != null ? Airliner.LookOf(a.Anim).Doors : 0)}, ramp drawn {Rig()?.DoorOpen(RampDoor):F2})");
        Say("seen ramp");

        if (!await Heard("A", "parked", 60)) { Fail("A never parked"); return; }
        Expect(await Until(() => Parked() is { BusDoors: RampAndCrew }, 10), $"B sees it parked with the ramp down (doors {Parked()?.BusDoors})");
        if (Parked() is not { } parked) { Fail("no parked freighter here"); return; }
        Node3D Frame() => parked.Visual ?? parked;
        Vector3 Spot(float z) => Frame().GlobalTransform * AircraftMeshBuilder.Flip(new Vector3(0f, 0f, z));
        // on the ground behind the ramp's lip, then up it into the hold
        var behind = Spot(RampToeZ - 4f);
        me.PlaceAt(behind with { Y = parked.GlobalPosition.Y + 0.3f }, parked.Rotation.Y);
        await Seconds(2);
        async Task<bool> WalkTo(float x, float z, double seconds)
        {
            Vector3 Target() => Frame().GlobalTransform * AircraftMeshBuilder.Flip(new Vector3(x, 0f, z));
            me.WalkControls = () =>
            {
                var to = (Target() - me.GlobalPosition) with { Y = 0 };
                return (to.Length() < 0.2f ? Vector3.Zero : to.Normalized(), false);
            };
            bool there = await Until(() => ((Target() - me.GlobalPosition) with { Y = 0 }).Length() < 0.45f, seconds);
            me.WalkControls = () => (Vector3.Zero, false);
            await Seconds(0.3);
            return there;
        }
        bool up = await WalkTo(0f, RampHingeZ + 2f, 30);
        var local = AircraftMeshBuilder.Flip(Frame().GlobalTransform.AffineInverse() * me.GlobalPosition);
        Expect(up && me.Aboard && Mathf.Abs(local.Y - FloorY) < 0.3f, $"B walked up the ramp into the hold ({local.X:F2}, {local.Y:F2}, {local.Z:F2}), aboard {me.Aboard}");
        // the ramp's button inside, on the left wall by the hinge
        bool at = await WalkTo(HoldHalfWidth - 0.55f, RampHingeZ + 0.5f, 15);
        var button = me.ButtonInReach();
        Expect(at && button?.Door == RampDoor && me.TryInteract(), $"B pressed the ramp's button (button {button?.Door})");
        Expect(await Until(() => (parked.BusDoors & 1 << RampDoor) == 0, 10), $"the ramp shuts for B too (doors {parked.BusDoors})");
        Say("pressed");
        await Heard("A", "seen shut", 20);
        me.WalkControls = null;
    }
}
