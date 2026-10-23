using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// <c>--ebikenet A|B</c> with <c>--connect</c> (driven by <c>tools/ebikenetcheck.sh</c>, #1167): two players
/// watching each other ride the e-bike over a real server. A rides the Samedi under its motor; B must see
/// A's copy as that e-bike, drawn on its own frame (not the road bike's), moving past 15 km/h with the
/// cranks turning. Then B rides the low frame and A must see B's copy the same way, on the low frame.
/// </summary>
public partial class EBikeNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--ebikenet");

    public EBikeNetProbe(ItemController items) : base(items, "ebikenet", "EB", "ebikenet_") { }
    public EBikeNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    private async Task RunA(FootPlayer me)
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }
        await Ride(me, RideKind.EBike, "riding");
        Expect(await Heard("B", "seen", 30), "B saw A ride the e-bike");
        Park(me);
        Say("your turn");
        if (!await Heard("B", "riding", 30)) { Fail("B never rode"); return; }
        await Watch(RideKind.EBikeOpen, "B");
        Say("seen");
        if (!await Heard("B", "done", 30)) Fail("B never finished");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!await Heard("A", "riding", 30)) { Fail("A never rode"); return; }
        await Watch(RideKind.EBike, "A");
        Say("seen");
        if (!await Heard("A", "your turn", 30)) { Fail("A never handed over"); return; }
        await Ride(me, RideKind.EBikeOpen, "riding");
        Expect(await Heard("A", "seen", 30), "A saw B ride the low frame");
        Park(me);
        Say("done");
    }

    /// <summary>Mounts <paramref name="kind"/> and rides straight on under the motor; says <paramref name="said"/> once going.</summary>
    private async Task Ride(FootPlayer me, RideKind kind, string said)
    {
        Expect(me.SetRide(kind) && me.Vehicle is Bicycle { Spec.Assist: not null } bike && bike.Spec.Kind == kind, $"{_role} mounted the {kind}");
        // the remote up to Sport, through the binding: Tour is where it starts
        XR.XrPad.Tap(PlayerInput.EngineToggle);
        await Seconds(0.3);
        Expect(me.Vehicle is Bicycle { Level: AssistLevel.Sport }, $"{_role}'s remote: Sport");
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        Expect(await Until(() => me.GroundSpeed > 15f / 3.6f, 15), $"{_role} is past 15 km/h ({me.GroundSpeed * 3.6f:F0})");
        Say(said);
    }

    private static void Park(FootPlayer me)
    {
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
    }

    /// <summary>The other's copy here: <paramref name="kind"/>, on its own frame, moving and pedalling.</summary>
    private async Task Watch(RideKind kind, string who)
    {
        Expect(await Until(() => Copy() is { } c && c.Ride == kind, 15), $"{who}'s copy rides the {kind} (sees {Copy()?.Ride})");
        Expect(await Until(() => Copy()?.Visual is Avatar.Cyclist { Frame.Spec.Kind: var k } && k == kind, 10),
            $"{who}'s copy is drawn on the {kind}'s own frame ({(Copy()?.Visual as Avatar.Cyclist)?.Frame.Spec.Name})");
        if (Copy() is not { } copy) return;
        var from = copy.GlobalPosition;
        await Seconds(2.0);
        float speed = (copy.GlobalPosition - from).Length() / 2f;
        Expect(speed > 15f / 3.6f, $"{who}'s copy moves at {speed * 3.6f:F0} km/h");
        Expect(copy.Visual is Avatar.Cyclist { CadenceRpm: > 30f } cyc, $"{who}'s copy pedals ({(copy.Visual as Avatar.Cyclist)?.CadenceRpm:F0} rpm)");
    }

    /// <summary>The other player's copy here: the one player node that is not ours.</summary>
    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority()) return p;
        return null;
    }
}
