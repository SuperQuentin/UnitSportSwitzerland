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
public partial class ShotgunProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--gunshot");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private readonly List<ItemEvent> _events = new();
    private string _role = "";
    private int _failures;

    public ShotgunProbe(ItemController items) => _items = items;
    public ShotgunProbe() : this(null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ItemEvents.Received += e => { if (!e.Local) _events.Add(e); };
        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor() && ItemEvents.Instance != null, 150))
        { Fail("no player on the ground"); return; }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        GD.Print(_failures == 0 ? $"[gunshot {_role}] RESULT: ok" : $"[gunshot {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.0);
        GetTree().Quit(_failures == 0 ? 0 : 1);
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

    private void Shot(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"gunshot_{name}.png"));
    }

    private static float Float(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id && !_items.Inventory[i].IsEmpty) return i;
        return -1;
    }

    private int CountOf(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private void Say(string what)
    {
        GD.Print($"[gunshot {_role}] say {what}");
        Chat?.Send($"PG {_role} {what}");
    }

    private Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"PG {role} {what}")), seconds);

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[gunshot {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[gunshot {_role}] RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
