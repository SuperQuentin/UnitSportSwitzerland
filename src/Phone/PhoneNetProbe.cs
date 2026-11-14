using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Phone;

/// <summary>
/// <c>--phonenet A|B</c> with <c>--connect</c> (driven by <c>tools/phonenetcheck.sh</c>, #889): what
/// another peer sees of a phone. A takes it out, opens it, puts it away; B checks A's copy each
/// time: <see cref="FootPlayer.PhoneState"/>, both arms in the phone pose, the phone drawn in the
/// hands, opened to its hinge's end, then gone with the arms off it. Windowed (<c>SHOTS=1</c>),
/// B saves its view of A to <c>test_output/phonenet_B_*.png</c>.
/// </summary>
public partial class PhoneNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--phonenet");

    public PhoneNetProbe(ItemController items) : base(items, "phonenet", "PH", "phonenet_") { }
    public PhoneNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(); else await RunB(Me!);
        await Finish(2.0);
    }

    private async Task RunA()
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready || PhoneController.Instance is not { } phone) { Fail("B never got ready, or no phone"); return; }
        Expect(phone.TakeOut(), "A takes the phone out");
        await Seconds(1.0);
        Say("out");
        Expect(await Heard("B", "seen out", 12), "B saw the phone out");
        // the body walks with the phone folded (#1825): B sees the copy's legs go on while the arms hold the phone
        await WalkAround("walk-folded", "walked-folded", new Vector3(0, 0, -1));
        Expect(await Heard("B", "seen walk-folded", 15), "B saw the copy walk with the folded phone");
        await Seconds(ChatCalm);
        phone.Open();
        await Until(() => phone.IsCovering, 3);
        Expect(phone.IsCovering, "A's screen covers the view");
        Say("open");
        Expect(await Heard("B", "seen open", 12), "B saw the phone open");
        await WalkAround("walk-open", "walked-open", new Vector3(0, 0, 1));
        Expect(await Heard("B", "seen walk-open", 15), "B saw the copy walk with the open phone");
        await Seconds(ChatCalm);
        phone.PutAway();
        await Until(() => !phone.IsOut, 4);
        Expect(!phone.IsOut, "A put it away");
        Say("away");
        Expect(await Heard("B", "seen away", 12), "B saw it put away");

        // the holster (#1369): the selection stays on A, B's copy of A has an empty hand, then the item again
        var inv = _items.Inventory;
        for (int i = 0; i < Inventory.HotbarSize && inv.Held.IsEmpty; i++) inv.Select(i);
        await Seconds(1.0);
        Say("drawn");
        Expect(await Heard("B", "seen drawn", 12), "B saw A's item in hand");
        inv.SetHolstered(true);
        Say("holstered");
        Expect(await Heard("B", "seen holstered", 12), "B saw A's hand empty after the holster");
        inv.SetHolstered(false);
        Say("redrawn");
        Expect(await Heard("B", "seen redrawn", 12), "B saw the item back in A's hand");
    }

    /// <summary>A pause that lets the chat's flood gate (5 lines, 0.6 a second) refill between the walks' lines and the holster's.</summary>
    private const double ChatCalm = 3.0;

    /// <summary>A walks 3 m the way given through the walk intent (the body's own seam), saying when it starts and stops.</summary>
    private async Task WalkAround(string start, string end, Vector3 way)
    {
        var me = Me!;
        var from = me.GlobalPosition;
        Say(start);
        await Seconds(0.3);
        bool there = await WalkUntil(me, way, () => me.GlobalPosition.DistanceTo(from) > 3f, 6, 0.2);
        Expect(there, $"A walked with the phone out ({me.GlobalPosition.DistanceTo(from):F1} m, state {me.PhoneState})");
        Say(end);
    }

    /// <summary>
    /// B watches A's copy from A's start to its end message: the phone stays in the hands the whole way (state, arms,
    /// model), the copy's position advances, and no frame jumps it.
    /// </summary>
    private async Task WatchWalk(string start, string end, int state, Avatar.ItemArmPose pose)
    {
        if (!await Heard("A", start, 20)) { Fail($"A never said {start}"); return; }
        var from = Copy()?.GlobalPosition ?? Vector3.Zero;
        var last = from;
        bool held = true;
        float maxStep = 0f;
        var done = Heard("A", end, 20);
        while (!done.IsCompleted)
        {
            if (Copy() is { } c)
            {
                held &= c.PhoneState == state && c.DrawnArmPose == pose && c.PhoneModel is { Visible: true };
                maxStep = Mathf.Max(maxStep, c.GlobalPosition.DistanceTo(last));
                last = c.GlobalPosition;
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        float moved = Copy()?.GlobalPosition.DistanceTo(from) ?? 0f;
        Expect(held, $"A's copy held the phone in both hands (state {state}, arms {pose}) the whole walk");
        Expect(moved > 1.5f, $"while its position advanced ({moved:F1} m)");
        Expect(maxStep < 1.0f, $"and nothing snapped (largest step {maxStep:F2} m in a frame)");
        Say("seen " + start);
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");

        if (!await Heard("A", "out", 20)) { Fail("A never took it out"); return; }
        Expect(await Until(() => Copy() is { PhoneState: FootPlayer.PhoneFoldedState, DrawnArmPose: Avatar.ItemArmPose.Phone, PhoneModel.Visible: true }, 4),
            $"A's copy holds the folded phone in both hands (state {Copy()?.PhoneState}, arms {Copy()?.DrawnArmPose})");
        ShotOfA("B_folded");
        Say("seen out");
        await WatchWalk("walk-folded", "walked-folded", FootPlayer.PhoneFoldedState, Avatar.ItemArmPose.Phone);

        if (!await Heard("A", "open", 20)) { Fail("A never opened it"); return; }
        Expect(await Until(() => Copy() is { PhoneState: FootPlayer.PhoneOpenState, DrawnArmPose: Avatar.ItemArmPose.PhoneOpen }
                                 && Copy()!.PhoneModel is { Visible: true, Fold: > 0.99f }, 4),
            $"A's copy holds it open (state {Copy()?.PhoneState}, hinge {Copy()?.PhoneModel?.Fold:F2})");
        ShotOfA("B_open");
        Say("seen open");
        await WatchWalk("walk-open", "walked-open", FootPlayer.PhoneOpenState, Avatar.ItemArmPose.PhoneOpen);

        if (!await Heard("A", "away", 20)) { Fail("A never put it away"); return; }
        // the arms go back to whatever the hotbar holds (Hold for an item, None for an empty hand)
        Expect(await Until(() => Copy() is { PhoneState: FootPlayer.PhonePocketState } c
                                 && c.DrawnArmPose is not (Avatar.ItemArmPose.Phone or Avatar.ItemArmPose.PhoneOpen)
                                 && c.PhoneModel is not { Visible: true }, 4),
            $"A's copy has put it away, its arms off the phone (arms {Copy()?.DrawnArmPose})");
        Say("seen away");

        if (!await Heard("A", "drawn", 20)) { Fail("A never drew an item"); return; }
        Expect(await Until(() => Copy() is { HeldItemId: not 0 }, 4), $"A's copy has an item in hand ({Copy()?.HeldItemId})");
        Say("seen drawn");
        if (!await Heard("A", "holstered", 20)) { Fail("A never holstered"); return; }
        Expect(await Until(() => Copy() is { HeldItemId: 0 }, 4), $"A's copy has an empty hand once holstered ({Copy()?.HeldItemId})");
        Say("seen holstered");
        if (!await Heard("A", "redrawn", 20)) { Fail("A never drew again"); return; }
        Expect(await Until(() => Copy() is { HeldItemId: not 0 }, 4), $"A's copy has the item back ({Copy()?.HeldItemId})");
        Say("seen redrawn");
    }

    /// <summary>Windowed: turn to A's copy and save the view.</summary>
    private void ShotOfA(string name)
    {
        if (DisplayServer.GetName() == "headless" || Copy() is not { } a || Me is not { } me) return;
        var to = a.GlobalPosition + Vector3.Up - (me.GlobalPosition + Vector3.Up * 1.68f);
        me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
        me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
        GD.Print($"{Log} shot {Shot(name)}");
    }

    /// <summary>A's copy here: the one player node that is not ours.</summary>
    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority()) return p;
        return null;
    }
}
