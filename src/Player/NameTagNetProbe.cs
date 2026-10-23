using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// <c>--nametagnet A|B</c> with <c>--connect</c> (driven by <c>tools/nametagnetcheck.sh</c>, #1136): the
/// name over another player's head. A plays as a phone (<c>--mobile</c>); B must see A's copy wear
/// "TaggerA" and "MOBILE", its own body none, and A's tag follow a <c>/name</c>.
/// </summary>
public partial class NameTagNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--nametagnet");

    public NameTagNetProbe(ItemController items) : base(items, "nametagnet", "NT", "nametagnet_") { }
    public NameTagNetProbe() : this(null!) { }

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
        Expect(me.DeviceKind == FootPlayer.DeviceMobile, $"A plays as a phone (device {me.DeviceKind})");
        Expect(me.GetNodeOrNull("NameTag") == null, "A wears no tag of its own");
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }
        Say("look");
        Expect(await Heard("B", "seen", 20), "B saw A's tag");
        Chat?.Send("/name TaggerA2");
        Say("renamed");
        Expect(await Heard("B", "seen renamed", 20), "B saw A's new name");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!await Heard("A", "look", 20)) { Fail("A never asked"); return; }
        Expect(me.GetNodeOrNull("NameTag") == null, "B wears no tag of its own");
        Expect(me.DeviceKind == FootPlayer.DeviceDesktop, "B plays on a computer");
        Expect(await Until(() => Tag() is { Visible: true, Text: "TaggerA\nMOBILE" }, 10),
            $"A's copy wears \"TaggerA / MOBILE\" (shows \"{Tag()?.Text}\")");
        if (Copy() is { } a && Tag() is { } t)
            Expect(t.GlobalPosition.Y > a.GlobalPosition.Y + 1.8f, $"the tag is over A's head ({t.GlobalPosition.Y - a.GlobalPosition.Y:F2} m over the feet)");
        await ShotOfA("B_tag");
        Say("seen");
        if (!await Heard("A", "renamed", 20)) { Fail("A never renamed"); return; }
        Expect(await Until(() => Tag() is { Text: "TaggerA2\nMOBILE" }, 10), $"A's tag follows /name (shows \"{Tag()?.Text}\")");
        Say("seen renamed");
    }

    /// <summary>Windowed (<c>SHOTS=1</c>): step back from A's copy (both spawn side by side), turn to it and save the view.</summary>
    private async Task ShotOfA(string name)
    {
        if (DisplayServer.GetName() == "headless" || Copy() is not { } a || Me is not { } me) return;
        me.GlobalPosition = a.GlobalPosition + new Vector3(0, 0.5f, 5f);
        await Seconds(1.0);
        var to = a.GlobalPosition + Vector3.Up * 1.4f - (me.GlobalPosition + Vector3.Up * 1.68f);
        me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
        me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
        await Seconds(0.5);
        GD.Print($"{Log} shot {Shot(name)}");
    }

    private Label3D? Tag() => Copy()?.GetNodeOrNull<Label3D>("NameTag");

    /// <summary>A's copy here: the one player node that is not ours.</summary>
    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority()) return p;
        return null;
    }
}
