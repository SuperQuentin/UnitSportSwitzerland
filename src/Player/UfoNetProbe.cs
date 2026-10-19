using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// <c>--ufonetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/ufonetcheck.sh</c>): the flying
/// saucer (#861) between two clients and a dedicated server, everything checked on the remote peer.
/// <list type="bullet">
/// <item>A takes a saucer on the ground: B sees a saucer, legs down, ramp open;</item>
/// <item>A hovers over B: B sees the legs folded, the ramp shut, the hum up;</item>
/// <item>A switches the beam on: B sees the beam and is lifted (standing still), A sees B rise;</item>
/// <item>B walks out of the cone: let down gently, unhurt;</item>
/// <item>A switches the beam and the lamps off: B sees both off;</item>
/// <item>A lands: B sees it on its legs with the ramp open; A gets out down the ramp, B sees it parked.</item>
/// </list>
/// Roles talk through chat lines; A logs in as admin (<c>--admin-password test</c>) so it may park the saucer.
/// </summary>
public partial class UfoNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--ufonetcheck");

    public UfoNetProbe(ItemController items) : base(items, "ufonet", "UF") { }
    public UfoNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ProcessPriority = 1000;
        if (!await Joined(150, () => Other() != null))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!);
        else await RunB(Me!);
        await Finish(2);
    }

    /// <summary>The other client's player, as drawn here.</summary>
    private FootPlayer? Other()
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != Me && !p.Npc) return p;
        return null;
    }

    private float _down;

    private async Task RunA(FootPlayer me)
    {
        // an admin may park what it took from the menu (ServerWorld's MayPark): the script's server has a password
        Chat?.Send("/login test");
        await Seconds(1);
        if (!await Heard("B", "ready", 60)) { Fail("B never got ready"); return; }
        var b = Other()!;
        // clear of B: a 9 m saucer is not taken on top of someone
        me.PlaceAt(b.GlobalPosition + new Vector3(16f, 0.5f, 0f), 0f);
        if (!await Until(() => me.IsOnFloor(), 10)) Fail("A never stood on the ground");
        Expect(me.SetRide(RideKind.Ufo), "A takes a saucer");
        if (me.Saucer is not { } ufo) { Fail("not a saucer"); return; }
        me.FlyControls = () => new FlightInput(Vector2.Zero, 0f, _down, 0f, 0f, false, false, 0f);
        Expect(await Until(() => ufo.State.Landed && ufo.State.Ramp > 0.99f, 15), $"on its legs, ramp open ({ufo.State.Ramp:F2})");
        Say("landed1");
        if (!await Heard("B", "seenlanded1", 30)) Fail("B never saw the landed saucer");

        // hovering over B: legs up, ramp shut
        for (int i = 0; i < 30; i++)
        {
            me.DebugLaunch(b.GlobalPosition + Vector3.Up * 16f, Vector3.Zero);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        Expect(await Until(() => ufo.State.Legs < 0.01f && ufo.State.Ramp < 0.01f, 10), "hovering: legs folded, ramp shut");
        Say("hover");
        if (!await Heard("B", "seenhover", 30)) Fail("B never saw it fly");

        ufo.SetBeam(true);
        Say("beam");
        float from = b.GlobalPosition.Y;
        Expect(await Until(() => b.GlobalPosition.Y > from + 5f, 20), $"A sees B rise in the beam ({b.GlobalPosition.Y - from:F1} m)");
        if (!await Heard("B", "lifted", 30)) Fail("B was never lifted");
        if (!await Heard("B", "freed", 40)) Fail("B never got out of the beam");

        ufo.SetBeam(false);
        ufo.SetLights(false);
        Say("off");
        if (!await Heard("B", "seenoff", 30)) Fail("B never saw the beam and lamps off");

        // down onto its legs, the ramp opening by itself
        _down = 1f;
        Expect(await Until(() => ufo.State.Landed && ufo.State.Ramp > 0.99f, 30), "landed again, ramp open");
        _down = 0f;
        Say("landed2");
        if (!await Heard("B", "seenlanded2", 30)) Fail("B never saw it land");

        me.FlyControls = null;
        me.ExitVehicle();
        await Seconds(1);
        Expect(me.Ride == RideKind.OnFoot, "A got out");
        Say("out");
        if (!await Heard("B", "seenparked", 30)) Fail("B never saw it parked");
        Say("done");
    }

    private async Task RunB(FootPlayer me)
    {
        await Seconds(1);
        Say("ready");
        if (!await Heard("A", "landed1", 60)) { Fail("A never took a saucer"); return; }
        var a = Other()!;
        bool drawn = await Until(() => a.Ride == RideKind.Ufo && RigOf(a) != null && Ufo.LookOf(a.Anim).Ramp > 0.99f, 10);
        var look = Ufo.LookOf(a.Anim);
        Expect(drawn && look.Legs > 0.99f && look.Ramp > 0.99f, $"B sees a saucer on its legs, ramp open (legs {look.Legs:F2}, ramp {look.Ramp:F2})");
        Say("seenlanded1");

        if (!await Heard("A", "hover", 40)) { Fail("A never hovered"); return; }
        Expect(await Until(() => Ufo.LookOf(a.Anim) is { Legs: < 0.01f, Ramp: < 0.01f, Hum: > 0.2f }, 10),
            $"B sees it fly: legs folded, ramp shut, hum up ({Ufo.LookOf(a.Anim)})");
        Say("seenhover");

        if (!await Heard("A", "beam", 30)) { Fail("A never switched the beam on"); return; }
        Expect(await Until(() => Ufo.BeamOn(a.Anim) && RigOf(a)?.FindChild("Beam", true, false) is Node3D { Visible: true }, 10),
            "B sees the beam on");
        float from = me.GlobalPosition.Y;
        Expect(await Until(() => me.InBeam && me.GlobalPosition.Y > from + 6f, 20), $"B is lifted ({me.GlobalPosition.Y - from:F1} m)");
        Say("lifted");
        // walk out of the cone: let down gently, unhurt
        Input.ActionPress(PlayerInput.MoveRight);
        await Seconds(6);
        Input.ActionRelease(PlayerInput.MoveRight);
        Expect(await Until(() => !me.InBeam && me.IsOnFloor(), 30), "walking out frees B: let down, on the ground");
        Expect(me.Health >= FootPlayer.MaxHealth, $"B landed unhurt (health {me.Health:F0})");
        Say("freed");

        if (!await Heard("A", "off", 30)) { Fail("A never switched off"); return; }
        Expect(await Until(() => !Ufo.BeamOn(a.Anim) && !Ufo.LookOf(a.Anim).Lights, 10), "B sees the beam and the lamps off");
        Say("seenoff");

        if (!await Heard("A", "landed2", 60)) { Fail("A never landed"); return; }
        Expect(await Until(() => Ufo.LookOf(a.Anim) is { Legs: > 0.99f, Ramp: > 0.99f, Landed: true }, 10),
            $"B sees it landed, ramp open ({Ufo.LookOf(a.Anim)})");
        Say("seenlanded2");

        if (!await Heard("A", "out", 30)) { Fail("A never got out"); return; }
        Expect(await Until(() => ParkedSaucer() != null && a.Ride == RideKind.OnFoot, 15), "B sees A on foot and the saucer parked");
        Say("seenparked");
        await Heard("A", "done", 20);
    }

    /// <summary>The drawn saucer under a player, or null.</summary>
    private static UfoRig? RigOf(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is UfoRig rig) return rig;
            if (RigOf(child) is { } inner) return inner;
        }
        return null;
    }

    private Vehicles.VehicleBody? ParkedSaucer()
    {
        foreach (var n in GetTree().GetNodesInGroup(Vehicles.VehicleBody.Group))
            if (n is Vehicles.VehicleBody { Ride: Ufo } v) return v;
        return null;
    }
}
