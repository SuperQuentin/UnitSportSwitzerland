using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Birds;

/// <summary>
/// <c>--pigeonnetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/pigeonnetcheck.sh</c>): playing the
/// pigeon (#217) between two clients and a dedicated server.
/// <list type="bullet">
/// <item>A turns into a pigeon: no items usable, no hotbar, and its items are all still there;</item>
/// <item>B sees A as a bird (the remote copy draws the pigeon, at pigeon size);</item>
/// <item>A flies over B and lets go: the server picks B as the victim, B gets the splat and A's name,
/// A sees it land on B and scores a hit;</item>
/// <item>A turns back into a person: items usable again, the same items.</item>
/// </list>
/// Roles talk through chat lines; scratch inventory.
/// </summary>
public partial class PigeonNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--pigeonnetcheck");

    public PigeonNetProbe(ItemController items) : base(items, "pigeonnet", "PG") { }
    public PigeonNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);
    private static BirdLife? Life => BirdLife.Instance;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ProcessPriority = 1000;
        if (!await Joined(150, () => Life?.Net != null && Other() != null))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!, Life!);
        else await RunB(Me!, Life!);
        await Finish(2);
    }

    /// <summary>The other client's player, as drawn here.</summary>
    private FootPlayer? Other()
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != Me && !p.Npc) return p;
        return null;
    }

    private async Task RunA(FootPlayer me, BirdLife life)
    {
        _items.Inventory.Add(ItemId.Shells, 7);
        int pack = PackTotal();
        await Seconds(0.5);
        Expect(_items.UsablePlayer == me && _items.Ui.ItemsActive, "on foot: items usable");

        me.SetRide(RideKind.Pigeon);
        await Seconds(0.5);
        Expect(me.Ride == RideKind.Pigeon, "A is a pigeon");
        Expect(_items.UsablePlayer == null && !_items.Ui.ItemsActive && !_items.Ui.IsOpen, "as a pigeon: no items, no hotbar, no inventory screen");
        Expect(PackTotal() == pack, $"items kept ({PackTotal()} of {pack})");
        Say("pigeon");
        if (!await Heard("B", "seenbird", 30)) Fail("B never saw a bird");

        // over B's head, held there a moment so the server has A where it is, then let go
        var b = Other()!;
        for (int i = 0; i < 40; i++)
        {
            me.DebugLaunch(b.GlobalPosition + Vector3.Up * 6f, Vector3.Zero);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        int drops = life.DropsOnOthers;
        Input.ActionPress(PlayerInput.Fire);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Input.ActionRelease(PlayerInput.Fire);
        Expect(me.PigeonDrops == 1, "Fire let one go");
        Expect(await Until(() => life.DropsOnOthers > drops && life.PigeonHits == 1, 10),
            $"A saw it fall on B and scored ({life.DropsOnOthers - drops} seen, {life.PigeonHits} hits, victim {life.LastVictim})");
        if (!await Heard("B", "splat", 20)) Fail("B was not hit");

        // landed and slowed to a walk: a person again
        Expect(await Until(() => me.SetRide(RideKind.OnFoot), 20), "back on foot");
        await Seconds(0.5);
        Expect(_items.UsablePlayer == me && _items.Ui.ItemsActive && PackTotal() == pack, $"a person again: items back ({PackTotal()} of {pack})");
        Say("done");
    }

    private async Task RunB(FootPlayer me, BirdLife life)
    {
        if (!await Heard("A", "pigeon", 60)) { Fail("A never turned into a pigeon"); return; }
        var a = Other()!;
        bool bird = await Until(() => a.Ride == RideKind.Pigeon && a.GetNodeOrNull("Body/Bird") != null, 10);
        var box = bird ? Avatar.MeshBounds.Of(a.GetNode<Node3D>("Body")) : default;
        Expect(bird && box.Size.Length() < 1.2f, $"B sees A as a bird, at bird size ({box.Size})");
        Say("seenbird");

        int splats = life.Splats;
        bool hit = await Until(() => life.Splats > splats, 40);
        Expect(hit && life.LastDropper.Length > 0, $"A's dropping landed on B, by '{life.LastDropper}'");
        Say(hit ? "splat" : "nosplat");
        await Heard("A", "done", 30);
        Expect(await Until(() => a.Ride == RideKind.OnFoot, 5), "B sees A on foot again");
    }
}
