using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--plantcheck A|B</c> with <c>--connect</c> (driven by <c>tools/plantcheck.sh</c>): the flag ghost, plant and
/// pull-up strokes over loopback. A holds the flag (<c>--hold SwissFlag --view first</c>): ghost valid (aimed at the
/// ground), ghost red (aimed at a steep wall it spawns), then plants through the real item path and screenshots its
/// own view mid-stroke, then pulls it up again. B stands nearby and screenshots the body of A and the spawned flag in
/// frames after the "go" of A (<c>test_output/plant_b_NN.png</c>); it must see exactly one live spawn effect.
/// Scratch inventories.
/// </summary>
public partial class PlantProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--plantcheck");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private string _role = "";
    private int _failures;

    public PlantProbe(ItemController items) => _items = items;
    public PlantProbe() : this(null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor()
                               && PlacedObjects.Instance != null, 150))
        { Fail("no player on the ground"); return; }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        GD.Print(_failures == 0 ? $"[plantcheck {_role}] RESULT: ok" : $"[plantcheck {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.0);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task RunA(FootPlayer me)
    {
        var placed = PlacedObjects.Instance!;
        me.LookYaw = 0f;
        me.LookPitch = -0.75f;
        int slot = SlotOf(ItemId.SwissFlag);
        if (slot < 0) { Fail("no flag"); return; }
        string pos = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"posA {me.GlobalPosition.X:F2} {me.GlobalPosition.Y:F2} {me.GlobalPosition.Z:F2}");
        for (int tries = 0; tries < 60 && !_heard.Any(l => l.Contains("PL B ready")); tries++)
        {
            Say(pos);
            await Seconds(2.5);
        }
        if (!_heard.Any(l => l.Contains("PL B ready"))) { Fail("B never joined"); return; }
        await Seconds(1.0);

        // ghost on the ground: valid, green
        var ghost = _items.GetNode<FlagGhost>("FlagGhost");
        await Until(() => ghost.Showing && ghost.Last.Valid, 25);   // traffic may drive through the spot
        await Seconds(0.3);
        Expect(ghost.Showing && ghost.Last.Kind == FlagAimKind.Plant && ghost.Last.Valid, $"ghost valid on the ground ({ghost.Last.Kind}, {ghost.Last.Reason})");
        Shot("a_ghost_valid");

        // ghost on a steep wall: red
        var wall = new StaticBody3D { Name = "ProbeWall" };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4f, 4f, 0.2f) } });
        wall.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(4f, 4f, 0.2f) } });
        GetParent().AddChild(wall);
        me.LookPitch = -0.35f;
        wall.GlobalPosition = me.Camera.GlobalPosition + new Vector3(0, -0.5f, -3.2f);
        await Seconds(0.4);
        Expect(ghost.Showing && ghost.Last.Kind == FlagAimKind.Plant && !ghost.Last.Valid, $"ghost red on a wall ({ghost.Last.Kind}, {ghost.Last.Reason})");
        Shot("a_ghost_invalid");
        wall.QueueFree();
        me.LookPitch = -0.75f;
        await Seconds(0.4);

        // plant through the real path, mid-stroke shots
        Say("go");
        await Seconds(0.3);
        int flags = CountOf(ItemId.SwissFlag);
        var mine = placed.All.Keys.ToHashSet();
        _items.UseSlot(me, slot);
        await Seconds(0.35);
        Shot("a_raise");
        await Seconds(0.25);
        Shot("a_stab");
        Expect(await Until(() => placed.All.Values.Any(o => !mine.Contains(o.Id) && o.Kind == PlacedKind.Flag), 5), "the flag was planted");
        Expect(CountOf(ItemId.SwissFlag) == flags - 1, "the flag left the pack");
        Expect(FlagFx.Spawns == 1, $"one spawn effect here ({FlagFx.Spawns})");
        await Seconds(0.15);
        Shot("a_planted");
        await Seconds(1.2);
        Expect(ghost.Last.Kind == FlagAimKind.PickUp, "the planted flag is the pick-up target");
        Shot("a_pickup_highlight");
        await Seconds(1.0);
        Say("planted");
        await Heard("B", "done", 60);

        // pull it up again
        _items.UseSlot(me, slot);
        await Seconds(0.25);
        Shot("a_pull");
        Expect(await Until(() => CountOf(ItemId.SwissFlag) == flags, 5), "the flag came back to the pack");
        Expect(placed.All.Values.All(o => mine.Contains(o.Id)), "the planted flag is gone");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Until(() => _heard.Any(l => l.Contains("PL A posA")), 150)) { Fail("A never reported"); return; }
        var parts = _heard.First(l => l.Contains("PL A posA")).Split("posA ")[1].Split(' ');
        var a = new Vector3(Float(parts[0]), Float(parts[1]), Float(parts[2]));
        _items.Inventory.Select(0);
        me.GlobalPosition = a + new Vector3(3.0f, 1.0f, -4.0f);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        await Until(() => me.IsOnFloor(), 10);
        // A is at (0,0), the flag 1.7 m north of it: look west at the pair
        me.LookYaw = Mathf.DegToRad(143f);
        me.LookPitch = -0.15f;
        await Seconds(2.0);
        int stood = PlacedObjects.Instance!.All.Count;
        for (int tries = 0; tries < 30 && !_heard.Any(l => l.Contains("PL A go")); tries++)
        {
            Say("ready");
            await Seconds(1.0);
        }
        for (int i = 0; i < 20; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Shot($"b_{i:D2}");
            await Seconds(0.1);
        }
        Expect(await Until(() => _heard.Any(l => l.Contains("PL A planted")), 20), "A reported the flag planted");
        Expect(PlacedObjects.Instance.All.Count == stood + 1, "the flag arrived here live");
        Expect(FlagFx.Spawns == 1, $"exactly one spawn effect here ({FlagFx.Spawns}): none for the join snapshot");
        Say("done");
    }

    private void Shot(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"plant_{name}.png"));
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
        GD.Print($"[plantcheck {_role}] say {what}");
        Chat?.Send($"PL {_role} {what}");
    }

    private Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"PL {role} {what}")), seconds);

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
        GD.Print($"[plantcheck {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[plantcheck {_role}] RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
