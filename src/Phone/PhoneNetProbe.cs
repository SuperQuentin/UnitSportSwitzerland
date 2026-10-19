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
/// hands, opened to its hinge's end, then gone with the arms back down. Windowed (<c>SHOTS=1</c>),
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
        phone.Open();
        await Seconds(1.2);
        Expect(phone.IsCovering, "A's screen covers the view");
        Say("open");
        Expect(await Heard("B", "seen open", 12), "B saw the phone open");
        phone.PutAway();
        for (int i = 0; i < 40 && phone.IsOut; i++) await Seconds(0.1);
        Expect(!phone.IsOut, "A put it away");
        Say("away");
        Expect(await Heard("B", "seen away", 12), "B saw it put away");
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

        if (!await Heard("A", "open", 20)) { Fail("A never opened it"); return; }
        Expect(await Until(() => Copy() is { PhoneState: FootPlayer.PhoneOpenState, DrawnArmPose: Avatar.ItemArmPose.PhoneOpen }
                                 && Copy()!.PhoneModel is { Visible: true, Fold: > 0.99f }, 4),
            $"A's copy holds it open (state {Copy()?.PhoneState}, hinge {Copy()?.PhoneModel?.Fold:F2})");
        ShotOfA("B_open");
        Say("seen open");

        if (!await Heard("A", "away", 20)) { Fail("A never put it away"); return; }
        Expect(await Until(() => Copy() is { PhoneState: FootPlayer.PhonePocketState, DrawnArmPose: Avatar.ItemArmPose.None }
                                 && Copy()!.PhoneModel is not { Visible: true }, 4),
            $"A's copy has put it away, arms down (arms {Copy()?.DrawnArmPose})");
        Say("seen away");
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
