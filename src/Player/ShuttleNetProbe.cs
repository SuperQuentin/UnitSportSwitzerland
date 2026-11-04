using System;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// <c>--shuttlenetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/shuttlenetcheck.sh</c>, #1492): two
/// admins each take a space shuttle, 30 m apart, and ask <c>/orbit 100</c> together, so they fly side by side;
/// everything is checked on the remote peer.
/// <list type="bullet">
/// <item>B sees A's copy as a shuttle, drawn with the shuttle's rig;</item>
/// <item>A fires its rockets: B sees the spool in A's pose and the plumes on A's copy;</item>
/// <item>A switches the cruise drive on (it carries on at the speed it had, so the two stay close): B sees the
/// drive in the pose and the drive's glow; A switches it off (a circular orbit): B sees it go;</item>
/// <item>at 100 km and 7.8 km/s the air is thin but there: B sees re-entry's first glow on A's copy.</item>
/// </list>
/// </summary>
public partial class ShuttleNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--shuttlenetcheck");

    public ShuttleNetProbe(ItemController items) : base(items, "shuttlenet", "SH") { }
    public ShuttleNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private float _lever;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ProcessPriority = 1000;
        if (!await Joined(150, () => Other() != null))
        {
            await Finish(0);
            return;
        }
        await Run(Me!);
        await Finish(2);
    }

    /// <summary>The other client's player, as drawn here.</summary>
    private FootPlayer? Other()
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != Me && !p.Npc) return p;
        return null;
    }

    /// <summary>The other's shuttle as its pose says, or null when it is not in one.</summary>
    private AirlinerLook? OtherLook() =>
        Other() is { Ride: RideKind.Shuttle } o && IsInstanceValid(o) ? Airliner.ShuttleLook(Airliner.LookOf(o.Anim)) : null;

    /// <summary>The drawn aircraft under a player, or null.</summary>
    private static AirlinerRig? RigOf(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is AirlinerRig rig) return rig;
            if (RigOf(child) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>Whether the other's drawn shuttle shows the named effect (a plume, the drive's glow, the plasma).</summary>
    private bool Shows(string node) =>
        Other() is { } o && RigOf(o)?.FindChild(node, true, false) is Node3D n && n.IsVisibleInTree();

    private async Task Run(FootPlayer me)
    {
        if (!await LogIn("test")) return;
        string other = _role == "A" ? "B" : "A";
        if (_role == "B")
        {
            bool answered = false;
            for (int i = 0; i < 30 && !answered; i++)
            {
                Say("ready");
                answered = await Heard("A", "here", 2);
            }
            if (!answered) { Fail("A never got ready"); return; }
            // 30 m east of A, both facing north: /orbit flies the way it heads, so the two orbits are one
            me.PlaceAt(Other()!.GlobalPosition + new Vector3(30f, 0.5f, 0f), 0f);
        }
        else
        {
            if (!await Heard("B", "ready", 60)) { Fail("B never got ready"); return; }
            me.PlaceAt(me.GlobalPosition + Vector3.Up * 0.5f, 0f);
            Say("here");
        }
        if (!await Until(() => me.IsOnFloor(), 10)) Fail("never stood on the ground");
        Expect(me.SetRide(RideKind.Shuttle), "takes a shuttle");
        if (me.Vehicle is not Airliner { Space: { } space } shuttle) { Fail("not a shuttle"); return; }
        me.FlyControls = () =>
        {
            float now = shuttle.State.Lever;
            return new FlightInput(Vector2.Zero, 0f, 0f, Mathf.Clamp((_lever - now) * 6f, 0f, 1f), Mathf.Clamp((now - _lever) * 6f, 0f, 1f),
                false, false, 0f, Brake: shuttle.State.OnGround ? 1f : 0f);
        };
        Say("aboard");
        if (!await Heard(other, "aboard", 60)) { Fail($"{other} never took a shuttle"); return; }
        // the shuttle's rig: an airliner's with the shuttle's own parts (its drive switch)
        Expect(await Until(() => OtherLook() != null && RigOf(Other()!)?.FindChild("DriveSwitch", true, false) != null, 20),
            $"{other}'s copy is a shuttle, drawn as one");

        if (_role == "A") Say("go");
        if (!await Heard("A", "go", 30)) { Fail("A never said go"); return; }
        Chat!.Send("/orbit 100");
        Expect(await Until(() => space.Orbit != null, 10), "the server's /orbit put this shuttle in orbit");
        Say("orbit");
        if (!await Heard(other, "orbit", 30)) { Fail($"{other} never got to orbit"); return; }
        if (!await Until(() => Other() is { } o && o.GlobalPosition.DistanceTo(me.GlobalPosition) < 5_000, 15))
        {
            Fail("the other shuttle is not beside this one");
            return;
        }

        if (_role == "A")
        {
            // the rockets, a short push
            _lever = 0.4f;
            await Until(() => shuttle.State.Spool > 0.3f, 10);
            Say("rockets");
            await Heard("B", "seen", 20);
            _lever = 0f;
            await Until(() => shuttle.State.Spool < 0.01f, 10);
            // the drive on: it carries on at its speed, beside B
            // the switch is an edge the next step takes
            Expect(me.ShuttleDrive(shuttle) && await Until(() => shuttle.DriveOn, 2), "the drive switches on in orbit");
            Expect(shuttle.State.Lever > 0.5f, $"the drive takes over at the orbit's speed (lever {shuttle.State.Lever:F2})");
            _lever = shuttle.State.Lever;
            Say("drive");
            await Heard("B", "seen", 20);
            Expect(me.ShuttleDrive(shuttle) && await Until(() => !shuttle.DriveOn && space.Orbit != null, 2), "the drive switches off onto an orbit");
            _lever = 0f;
            Say("off");
            await Heard("B", "seen", 20);
            GD.Print(FormattableString.Invariant($"{Log} A at 100 km: heat {shuttle.Heat:F3}, contacts {space.Contacts}"));
            Expect(space.Contacts == 0, $"no contact moved the shuttle off its orbit ({space.Contacts})");
        }
        else
        {
            if (!await Heard("A", "rockets", 30)) { Fail("A never fired its rockets"); return; }
            Expect(await Until(() => OtherLook() is { Spool: > 0.25f } && Shows("Plume0"), 5), "B sees A's rockets: the spool and the plumes");
            Say("seen");
            if (!await Heard("A", "drive", 30)) { Fail("A never switched the drive on"); return; }
            Expect(await Until(() => OtherLook() is { Drive: true } && Shows("DriveGlow") && !Shows("Plume0"), 5), "B sees A's drive: its glow, no plumes");
            float apart = Other() is { } a ? a.GlobalPosition.DistanceTo(me.GlobalPosition) : float.PositiveInfinity;
            Expect(apart < 5_000, $"A's copy stays beside B in the drive ({apart:F0} m)");
            Say("seen");
            if (!await Heard("A", "off", 30)) { Fail("A never switched the drive off"); return; }
            Expect(await Until(() => OtherLook() is { Drive: false }, 5), "B sees A's drive go off");
            Say("seen");
            // 100 km up at 7.8 km/s: re-entry's first glow, on A's copy here
            Expect(await Until(() => OtherLook() is { Heat: > 0.02f } && Shows("Plasma"), 10),
                $"B sees re-entry's first glow on A (heat {OtherLook()?.Heat:F3})");
        }
        Say("done");
        await Heard(other, "done", 30);
        me.FlyControls = null;
    }
}
