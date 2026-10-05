using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Vehicles;
using static UnitSport.Avatar.An124Layout;

namespace UnitSport.Player;

/// <summary>
/// <c>--an124net A|B</c> with <c>--connect</c> on <c>--world fixture</c> (driven by
/// <c>tools/an124netcheck.sh</c>, #419): the AN-124's doors and kneeling over the network.
/// <list type="bullet">
/// <item>A (admin) takes an AN-124, G opens everything and kneels it: B's copy has the four door bits
/// and its frame comes down by the kneeling drop (the published body pose).</item>
/// <item>A gets out: it stands parked, open and knelt (<c>VehicleState.Flags</c>); B's copy is posed
/// knelt; B walks up the nose ramp into the hold.</item>
/// <item>B presses the kneeling button inside: it rises with B on its floor, and A sees it rise; B
/// presses the visor's button: A sees the visor shut.</item>
/// </list>
/// </summary>
public partial class An124NetProbe : ChatProbe
{
    public static string? Role => RoleArg("--an124net");

    public An124NetProbe(ItemController items) : base(items, "an124net", "AN") { }
    public An124NetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private const byte All = 15;

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
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.An124 && !v.Wrecked);

    /// <summary>How far a parked one's frame stands off its body: −0.85 knelt, 0 standing.</summary>
    private static float FrameDrop(VehicleBody v) => v.Visual is { } f ? f.Position.Y : 0f;

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

        Expect(me.SetRide(RideKind.An124), "A takes an AN-124");
        await Seconds(2);
        if (me.Vehicle is not Airliner jet) { Fail("not an airliner"); return; }
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.CarDoor, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.CarDoor, Pressed = false });
        await Seconds(0.5);
        Expect(jet.DoorsOpen == All && await Until(() => jet.KneelShown >= 1f, 15), $"G opened everything and knelt it (doors {jet.DoorsOpen}, kneel {jet.KneelShown:F2})");
        Say("open");
        if (!await Heard("B", "seen open", 40)) Fail("B never saw it open and knelt");

        me.ExitVehicle();
        await Seconds(2);
        Expect(me.Ride == RideKind.OnFoot && await Until(() => Parked() is { BusDoors: All } v && FrameDrop(v) < -KneelDrop + 0.05f, 10),
            $"A got out, it stands parked open and knelt (doors {Parked()?.BusDoors}, frame {(Parked() is { } p ? FrameDrop(p) : 0f):F2})");
        // out of the way, off the left wing (B walks in at the nose)
        me.PlaceAt(me.GlobalPosition + new Vector3(-60f, 0f, 0f), 0f);
        Say("parked");
        if (!await Heard("B", "raised", 90)) { Fail("B never raised it"); return; }
        Expect(await Until(() => Parked() is { } v && (v.BusDoors & 1 << KneelDoor) == 0 && FrameDrop(v) > -0.02f, 15),
            $"A sees it rise by B's button (doors {Parked()?.BusDoors}, frame {(Parked() is { } q ? FrameDrop(q) : 0f):F2})");
        Say("seen raised");
        if (!await Heard("B", "visor", 60)) { Fail("B never shut the visor"); return; }
        Expect(await Until(() => Parked() is { } v && (v.BusDoors & 1 << NoseDoor) == 0, 10), $"A sees the visor shut by B (doors {Parked()?.BusDoors})");
        Say("seen visor");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        me.PlaceAt(me.GlobalPosition + new Vector3(70f, 0f, 0f), 0f);
        await Seconds(1.5);
        Say("ready");
        if (!await Heard("A", "open", 40)) { Fail("A never opened it"); return; }
        FootPlayer? a = null;
        bool seen = await Until(() => (a = Other(me)) != null && a.Ride == RideKind.An124 && Airliner.LookOf(a.Anim).Doors == All
            && a.BodyPose.Origin.Y < -KneelDrop + 0.05f, 25);
        Expect(seen, $"B sees A's AN-124 open and knelt (doors {(a != null ? Airliner.LookOf(a.Anim).Doors : 0)}, frame {a?.BodyPose.Origin.Y:F2})");
        Say("seen open");

        if (!await Heard("A", "parked", 60)) { Fail("A never parked"); return; }
        Expect(await Until(() => Parked() is { BusDoors: All } v && FrameDrop(v) < -KneelDrop + 0.05f, 15),
            $"B sees it parked open and knelt (doors {Parked()?.BusDoors}, frame {(Parked() is { } p0 ? FrameDrop(p0) : 0f):F2})");
        if (Parked() is not { } parked) { Fail("no parked AN-124 here"); return; }
        Node3D Frame() => parked.Visual ?? parked;
        Vector3 Local() => AircraftMeshBuilder.Flip(Frame().GlobalTransform.AffineInverse() * me.GlobalPosition);
        // on the ground before the nose ramp's toes, then up it into the hold
        var before = Frame().GlobalTransform * AircraftMeshBuilder.Flip(new Vector3(0f, 0f, NoseToeZ(true) + 4f));
        me.PlaceAt(before with { Y = parked.GlobalPosition.Y + 0.3f }, parked.Rotation.Y + Mathf.Pi);
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
        bool up = await WalkTo(0f, NoseHingeZ - 3f, 40);
        var l = Local();
        Expect(up && me.Aboard && Mathf.Abs(l.Y - FloorY) < 0.3f, $"B walked up the nose ramp into the hold ({l.X:F2}, {l.Y:F2}, {l.Z:F2}), aboard {me.Aboard}");
        // the kneeling button, ahead of the crew door on the left wall: it rises with B on its floor
        float doorFront = CrewDoorZ + DoorWidth * 0.5f;
        bool at = await WalkTo(HoldHalfWidth - 0.55f, doorFront + An124Deck.KneelButtonAhead + 0.2f, 20);
        var button = me.ButtonInReach();
        Expect(at && button?.Door == KneelDoor && me.TryInteract(), $"B pressed the kneeling button (button {button?.Door})");
        float ground = parked.GlobalPosition.Y;
        Expect(await Until(() => (parked.BusDoors & 1 << KneelDoor) == 0 && FrameDrop(parked) > -0.02f, 15),
            $"it rises for B too (doors {parked.BusDoors}, frame {FrameDrop(parked):F2})");
        await Seconds(1);
        l = Local();
        Expect(me.Aboard && Mathf.Abs(l.Y - FloorY) < 0.3f && Mathf.Abs(me.GlobalPosition.Y - ground - FloorY) < 0.35f,
            $"B rose with the floor ({me.GlobalPosition.Y - ground:F2} m over the ground)");
        Say("raised");
        if (!await Heard("A", "seen raised", 30)) Fail("A never saw it rise");
        // the visor's button on the left wall at the front
        bool atVisor = await WalkTo(HoldHalfWidth - 0.55f, NoseHingeZ - 0.6f, 20);
        button = me.ButtonInReach();
        Expect(atVisor && button?.Door == NoseDoor && me.TryInteract() && await Until(() => (parked.BusDoors & 1 << NoseDoor) == 0, 10),
            $"B shut the visor by its button (button {button?.Door}, doors {parked.BusDoors})");
        Say("visor");
        await Heard("A", "seen visor", 20);
        me.WalkControls = null;
    }
}
