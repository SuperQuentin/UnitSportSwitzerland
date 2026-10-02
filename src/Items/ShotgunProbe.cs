using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--gunshot A|B</c> with <c>--connect</c> (driven by <c>tools/gunshotcheck.sh</c>): the shotgun's feel over loopback.
/// A holds the shotgun aimed (<c>--hold Shotgun --aim --view first</c>), fires through the real item path (a second
/// trigger pull at once must be refused by the pump) and screenshots its own view before and right after a shot.
/// B stands a few metres away, sees A's shouldered body (screenshots before the shot, at the kick and after the
/// pump) and must receive exactly two Shot events. Scratch inventories; outputs in <c>test_output/</c>.
/// </summary>
public partial class ShotgunProbe : ChatProbe
{
    public static string? Role => RoleArg("--gunshot");

    private readonly List<ItemEvent> _events = new();

    public ShotgunProbe(ItemController items) : base(items, "gunshot", "PG", "gunshot_") { }
    public ShotgunProbe() : this(null!) { }

    protected override string Dash => "-";

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ItemEvents.Received += e => { if (!e.Local) _events.Add(e); };
        if (!await Joined(150, () => ItemEvents.Instance != null)) return;
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(1.0);
    }

    private async Task RunA(FootPlayer me)
    {
        me.LookYaw = 0f;
        me.LookPitch = 0.02f;
        int gun = SlotOf(ItemId.Shotgun);
        if (gun < 0) { Fail("no shotgun"); return; }
        string pos = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"posA {me.GlobalPosition.X:F2} {me.GlobalPosition.Y:F2} {me.GlobalPosition.Z:F2}");
        for (int tries = 0; tries < 60 && !_heard.Any(l => l.Contains("PG B ready")); tries++)
        {
            Say(pos);   // B may join after the first line: repeat it until B answers
            await Seconds(2.5);
        }
        if (!_heard.Any(l => l.Contains("PG B ready"))) { Fail("B never joined"); return; }
        await Seconds(1.5);
        Shot("a_aimed");
        int shells = CountOf(ItemId.Shells);
        _items.UseSlot(me, gun);
        _items.UseSlot(me, gun);   // inside the pump: refused
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Shot("a_kick");
        Expect(CountOf(ItemId.Shells) == shells - 1, $"one shell spent, the second trigger pull refused while pumping ({shells} -> {CountOf(ItemId.Shells)})");
        await Seconds(0.45);
        Shot("a_pump");
        await Seconds(1.2);
        _items.UseSlot(me, gun);
        Expect(CountOf(ItemId.Shells) == shells - 2, "the next shell fires once the action has cycled");
        Say("fired");
        await Heard("B", "done", 60);
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Until(() => _heard.Any(l => l.Contains("PG A posA")), 150)) { Fail("A never reported"); return; }
        var parts = _heard.First(l => l.Contains("PG A posA")).Split("posA ")[1].Split(' ');
        var a = new Vector3(Float(parts[0]), Float(parts[1]), Float(parts[2]));
        // in front of A and to its right (+X when A faces north), looking back at it
        bool side = Array.IndexOf(OS.GetCmdlineUserArgs(), "--gunside") >= 0;   // "--gunside": A's right, looking along its shoulder line
        _items.Inventory.Select(0);   // B keeps its hands empty: nothing in the way of the picture
        me.GlobalPosition = a + (side ? new Vector3(1.9f, 1.0f, -0.35f) : new Vector3(2.3f, 1.0f, -2.6f));
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        await Until(() => me.IsOnFloor(), 10);
        me.LookYaw = Mathf.DegToRad(side ? 90f : 139f);
        me.LookPitch = -0.12f;
        await Seconds(2.0);
        for (int tries = 0; tries < 30 && !_heard.Any(l => l.Contains("PG A fired")) && _events.Count == 0; tries++)
        {
            Say("ready");
            await Seconds(1.0);
        }
        // the first Shot event: catch the jolt, then the pump
        await Until(() => _events.Count > 0, 40);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Shot(side ? "s_kick" : "b_kick");
        await Seconds(0.30);
        Shot(side ? "s_pump" : "b_pump");
        await Seconds(0.5);
        Shot(side ? "s_after" : "b_after");
        await Until(() => _events.Count >= 2, 10);
        await Seconds(0.5);
        Expect(_events.Count(e => e.Kind == ItemEventKind.Shot) == 2, $"two Shot events arrived ({_events.Count(e => e.Kind == ItemEventKind.Shot)})");
        Say("done");
    }
}
