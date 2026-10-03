using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--airlinernet A|B</c> with <c>--connect</c> on <c>--world fixture</c> (driven by
/// <c>tools/airlinernetcheck.sh</c>, #414): does another peer see an airliner as its pilot flies it?
/// <list type="bullet">
/// <item>A (admin) takes an A320, sets flaps 3, the speedbrake half, the parking brake, rolls the
/// stick hard over and spools up; B's copy shows the same levers, the stick's sign and the spool.</item>
/// <item>A stops it with flaps 2 and the parking brake and gets out: the parked aircraft keeps them
/// (<c>VehicleState.Flags</c>), and B, getting in, finds them as they were left.</item>
/// <item>A takes another one up (<c>DebugLaunch</c>) and raises the gear: B's copy folds it up.</item>
/// </list>
/// </summary>
public partial class AirlinerNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--airlinernet");

    public AirlinerNetProbe(ItemController items) : base(items, "airlinernet", "AN") { }
    public AirlinerNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

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

        Expect(me.SetRide(RideKind.A320), "A takes an A320");
        await Seconds(1);
        if (me.Vehicle is not Airliner jet) { Fail("not an airliner"); return; }
        for (int i = 0; i < 3; i++) jet.Command(AirlinerCommand.FlapsDown);
        jet.Command(AirlinerCommand.Speedbrake);
        jet.Command(AirlinerCommand.ParkingBrake);
        Input.ActionPress(PlayerInput.MoveRight);
        Input.ActionPress(PlayerInput.Sprint);
        await Seconds(6);
        Input.ActionRelease(PlayerInput.Sprint);
        Expect(jet.State.FlapLever == 3 && jet.State.SpeedBrake == 1 && jet.State.ParkingBrake, "levers set");
        Say($"levers {jet.State.Spool:F2}");
        await Heard("B", "seen levers", 20);
        Input.ActionRelease(PlayerInput.MoveRight);

        // stopped, flaps 2, levers back and the parking brake on: parked as left
        jet.Command(AirlinerCommand.FlapsUp);
        Input.ActionPress(PlayerInput.CrouchSlide);
        await Until(() => jet.State.FlapLever == 2 && jet.State.Lever <= 0f, 10);
        Input.ActionRelease(PlayerInput.CrouchSlide);
        Expect(jet.State.ParkingBrake && jet.State.FlapLever == 2 && me.GroundSpeed < 0.5f,
            $"stopped with flaps 2 and the parking brake ({jet.State.FlapLever}, {jet.State.ParkingBrake}, {me.GroundSpeed:F1} m/s)");
        me.ExitVehicle();
        await Seconds(2);
        Expect(me.Ride == RideKind.OnFoot, "A got out");
        Say("parked");
        if (!await Heard("B", "claimed", 60)) Fail("B never got in");

        // a fresh one in the air, away from the parked one: the gear comes up
        me.PlaceAt(me.GlobalPosition + new Vector3(-70f, 0f, 0f), 0f);
        await Seconds(1.5);
        Expect(me.SetRide(RideKind.A320), "A takes another A320");
        await Seconds(1);
        if (me.Vehicle is not Airliner air) { Fail("not an airliner"); return; }
        me.DebugLaunch(me.GlobalPosition + Vector3.Up * 400f, -me.GlobalTransform.Basis.Z * 95f);
        air.State.GearDown = true;
        air.State.Gear = 1f;
        await Seconds(0.5);
        air.Command(AirlinerCommand.Gear);
        await Seconds(1);
        Expect(!air.State.GearDown && !air.State.OnGround, "gear lever up in the air");
        Say("gear up");
        await Heard("B", "seen gear", 30);
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        // out from under the airliner A is about to take where both spawned: a parked one shoved by a
        // player standing inside it blows up
        me.PlaceAt(me.GlobalPosition + new Vector3(70f, 0f, 0f), 0f);
        await Seconds(1.5);
        Say("ready");
        FootPlayer? a = null;
        // the visual is named Body, but a node it replaces may still hold the name for a frame
        AirlinerRig? Rig() => (a = Other(me))?.GetChildren().OfType<AirlinerRig>().FirstOrDefault();
        if (!await Heard("A", "levers", 40)) { Fail("A never set the levers"); return; }
        bool seen = await Until(() => Rig() != null && a!.Ride == RideKind.A320 && Airliner.LookOf(a.Anim) is var l
            && l.Flaps == 3 && l.Spoilers > 0.4f && l.Stick.X > 0.3f && l.Spool > 0.5f, 15);
        var look = a != null ? Airliner.LookOf(a.Anim) : default;
        Expect(seen, $"B sees A's flaps 3, speedbrake, stick right, spool up ({look.Flaps}, {look.Spoilers:F2}, {look.Stick.X:F2}, {look.Spool:F2})");
        Say("seen levers");

        if (!await Heard("A", "parked", 60)) { Fail("A never parked"); return; }
        VehicleBody? Parked()
        {
            if (VehicleManager.Instance is not { } vehicles) return null;
            foreach (var node in vehicles.GetChildren())
                if (node is VehicleBody { Kind: RideKind.A320 } v) return v;
            return null;
        }
        Expect(await Until(() => Parked() != null, 10), "the A320 stands parked");
        if (Parked() is { } parked)
        {
            var left = (Airliner)parked.Ride;
            Expect(left.State.FlapLever == 2 && left.State.ParkingBrake && left.State.GearDown,
                $"parked with flaps 2, parking brake, gear down ({left.State.FlapLever}, {left.State.ParkingBrake}, {left.State.GearDown})");
            // claim it: B at its controls finds the levers where A left them
            // at its entry point (the front left door), facing it
            var door = parked.GlobalTransform * (left.EntryPoint + new Vector3(-1.2f, 0f, 0f));
            me.PlaceAt(door with { Y = parked.GlobalPosition.Y + 0.2f }, parked.Rotation.Y - Mathf.Pi / 2f);
            await Seconds(1.5);
            var entry = parked.ToGlobal(left.EntryPoint);
            GD.Print($"{Log} at the door: {new Vector2(entry.X - me.GlobalPosition.X, entry.Z - me.GlobalPosition.Z).Length():F2} m from the entry, dy {entry.Y - me.GlobalPosition.Y:F2}, reach finds {VehicleReach.Find(me)?.Vehicle?.Name ?? "nothing"}, enterable {VehicleManager.Instance?.Enterable(parked)}, hull {VehicleReach.HullDistance(parked, left.ParkedBox, me.GlobalPosition + Vector3.Up):F2}, wrecked {parked.Wrecked}, trailer {parked.Trailer != null}");
            bool claimed = false;
            for (int i = 0; i < 10 && !claimed; i++)
            {
                me.TryGetIn();
                claimed = await Until(() => me.Vehicle is Airliner, 2);
            }
            Expect(claimed && me.Vehicle is Airliner { State.FlapLever: 2, State.ParkingBrake: true },
                $"B got in and found flaps 2 and the parking brake ({(me.Vehicle as Airliner)?.State.FlapLever}, {(me.Vehicle as Airliner)?.State.ParkingBrake})");
        }
        Say("claimed");

        if (!await Heard("A", "gear up", 40)) { Fail("A never raised the gear"); return; }
        Node3D? Gear() => Rig()?.FindChild("GearNose", true, false) as Node3D;
        float down = Gear()?.Rotation.X ?? 0f;
        bool travelled = await Until(() => Airliner.LookOf(a!.Anim).Gear == 0f && Mathf.Abs((Gear()?.Rotation.X ?? 0f) - down) > 1.5f, 20);
        Expect(travelled, $"B's copy folds the nose gear up ({down:F2} -> {Gear()?.Rotation.X ?? 0f:F2} rad)");
        Say("seen gear");

    }
}
