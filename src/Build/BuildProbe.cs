using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Build;

/// <summary>
/// <c>--buildcheck [shots]</c> (offline, <c>--systems ui,physics</c>: the flat fixture): the hammer's
/// real aim and build path for a first grounded floor, then a hut around it (window, door, stone and
/// metal walls, a roof), stairs up to a landing, a sandbag half wall; a metal wall carrying a row of
/// wooden floors out to wood's span (one more is refused), shot down so the row falls with it; a real
/// shot trace chipping a wall; taking a piece back with its refund. Everything it built is taken
/// down at the end, so <c>user://structures/offline.json</c> is left as it was. With <c>shots</c> it
/// writes <c>test_output/build_*.png</c> (windowed).
/// </summary>
public partial class BuildProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--buildcheck") >= 0;
    private static bool Shots => OS.GetCmdlineUserArgs().Contains("shots");

    private readonly ItemController _items;
    private int _failures;
    private Vector3? _back;

    public BuildProbe(ItemController items) => _items = items;
    public BuildProbe() : this(null!) { }

    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    /// <summary>What the scratch pack starts with: the hammer on the hotbar and plenty of every material.</summary>
    public static void Stock(Inventory inv)
    {
        inv.Put(Inventory.HotbarSize - 1, new ItemStack(ItemId.Hammer, 1));
        inv.Add(ItemId.WoodPlanks, 90);
        inv.Add(ItemId.Stone, 40);
        inv.Add(ItemId.ScrapMetal, 20);
        inv.Add(ItemId.Screws, 20);
        inv.Add(ItemId.SandBag, 9);
    }

    public override async void _Ready()
    {
        // offline starts as the spectator high up: wait for the ground, then walk (T)
        await Until(() => Structures.Instance?.GroundAt?.Invoke(GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero) != null, 60);
        if (Me == null) GetParent<Core.ClientWorld>().ToggleMode();
        if (!await Until(() => Me is { } m && m.IsOnFloor() && Structures.Instance != null && Structures.Instance.GroundAt?.Invoke(m.GlobalPosition) != null, 120))
        { Fail("no player on the ground"); return; }
        await Seconds(1.0);
        _items.BuildTool.AlwaysShow = true;   // the ghost only shows to a captured pointer, and headless has none
        try { await Run(Me!, Structures.Instance!); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        GD.Print(_failures == 0 ? "[buildcheck] RESULT: ok" : $"[buildcheck] RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Run(FootPlayer me, Structures structures)
    {
        var inv = _items.Inventory;
        var tool = _items.BuildTool;
        var before = structures.All.Keys.ToHashSet();
        inv.Select(Inventory.HotbarSize - 1);
        me.LookYaw = 0f;
        me.LookPitch = -0.45f;

        // 1. the real path: aim, ghost, Use, materials spent
        tool.Select(PieceKind.Floor, BuildMaterial.Wood);
        await Until(() => tool.Last.Valid, 5);
        Expect(tool.Last.Valid && tool.Last.Piece.Grounded && tool.Last.Structure == 0,
            $"the ghost: a new grounded floor ({tool.Last.Reason})");
        if (Shots) Shot("ghost");
        int planks = inv.CountPlain(ItemId.WoodPlanks);
        _items.UseSlot(me, Inventory.HotbarSize - 1);
        Expect(await Until(() => structures.All.Keys.Any(k => !before.Contains(k)), 3), "a structure went up");
        var s = structures.All.Values.First(x => !before.Contains(x.Id));
        Expect(s.Pieces.Count == 1 && inv.CountPlain(ItemId.WoodPlanks) == planks - 5, $"one floor, 5 planks spent ({s.Pieces.Count}, {planks - inv.CountPlain(ItemId.WoodPlanks)})");

        // 2. a hut behind it (further along −Z), stairs, a landing, sandbags
        await Build(s, new Piece(Slot.Floor(0, 0, -1), PieceKind.Floor, 0, BuildMaterial.Wood, true), null);
        await Build(s, new Piece(Slot.Edge(0, 0, -1, 0), PieceKind.WindowWall, 0, BuildMaterial.Wood, false), null);
        await Build(s, new Piece(Slot.Edge(0, 0, -1, 1), PieceKind.Wall, 0, BuildMaterial.Stone, false), null);
        await Build(s, new Piece(Slot.Edge(0, 0, -1, 3), PieceKind.Wall, 0, BuildMaterial.Metal, false), null);
        await Build(s, new Piece(Slot.Edge(0, 0, -1, 2), PieceKind.DoorWall, 0, BuildMaterial.Wood, false), null);
        await Build(s, new Piece(Slot.Volume(0, 1, -1), PieceKind.Roof, 2, BuildMaterial.Metal, false), null);
        await Build(s, new Piece(Slot.Volume(1, 0, 0), PieceKind.Stairs, 0, BuildMaterial.Wood, true), null);
        await Build(s, new Piece(Slot.Floor(1, 1, -1), PieceKind.Floor, 0, BuildMaterial.Wood, false), null);
        await Build(s, new Piece(Slot.Edge(-1, 0, 0, 0), PieceKind.HalfWall, 0, BuildMaterial.Sandbag, true), null);
        await Build(s, new Piece(Slot.Volume(-1, 0, -1), PieceKind.Pillar, 0, BuildMaterial.Stone, true), null);
        Expect(s.Pieces.Count == 11, $"the hut stands: 11 pieces ({s.Pieces.Count})");
        await Build(s, new Piece(Slot.Floor(0, 0, -1), PieceKind.Floor, 0, BuildMaterial.Wood, true), "already");
        await Build(s, new Piece(Slot.Floor(-2, 3, 2), PieceKind.Floor, 0, BuildMaterial.Wood, false), "holds");
        // pieces grow to full strength over their build time (metal is slowest)
        await Seconds(BuildGrid.Spec(BuildMaterial.Metal).Seconds + 0.3);
        if (Shots)
        {
            // step back and to the side for the wide shots, then come back for the shooting
            var stood = me.GlobalPosition;
            me.GlobalPosition = stood + new Vector3(9f, 1.5f, 9f);
            me.LookPitch = -0.2f;
            me.LookYaw = Mathf.Pi / 4;
            await Seconds(0.6);
            Shot("hut");
            _back = stood;
        }

        // 3. a metal wall carrying wood floors out to wood's span; one more is refused. The wall stands
        // on the edge between cells z 0 and z -1, so both of those floors rest on it.
        int span = BuildGrid.Spec(BuildMaterial.Wood).Span;
        await Build(s, new Piece(Slot.Edge(4, 0, 0, 0), PieceKind.Wall, 0, BuildMaterial.Metal, true), null);
        for (int z = 0; z >= -(span + 1); z--)
            await Build(s, new Piece(Slot.Floor(4, 1, z), PieceKind.Floor, 0, BuildMaterial.Wood, false), null);
        await Build(s, new Piece(Slot.Floor(4, 1, -(span + 2)), PieceKind.Floor, 0, BuildMaterial.Wood, false), "holds");
        int count = s.Pieces.Count;
        if (Shots)
        {
            await Seconds(BuildGrid.Spec(BuildMaterial.Wood).Seconds + 0.3);
            Shot("span");
        }

        // 4. shoot the wall down: the row falls with it (metal: 500 once grown, a shotgun hit: up to 121.5)
        var wall = Slot.Edge(4, 0, 0, 0);
        await Seconds(BuildGrid.Spec(BuildMaterial.Metal).Seconds + 0.3);
        structures.SendHit(s.Id, wall, 1000f, ItemId.Shotgun);
        Expect(s.Pieces.TryGetValue(wall, out var w) && w.Damage == 0, "a hit over the weapon's cap is ignored");
        int hits = 0;
        for (; hits < 12 && s.Pieces.ContainsKey(wall); hits++)
            structures.SendHit(s.Id, wall, 120f, ItemId.Shotgun);
        Expect(!s.Pieces.ContainsKey(wall) && hits == 5, $"the grown wall broke on the 5th hit ({hits})");
        Expect(s.Pieces.Count == count - (span + 3), $"the {span + 2} floors it carried fell ({count - s.Pieces.Count - 1})");
        await Seconds(0.4);
        if (Shots) Shot("collapse");
        if (_back is { } back)
        {
            me.GlobalPosition = back;
            me.LookYaw = 0f;
            await Seconds(0.3);
        }

        // 5. a real shot trace chips the door wall facing the player (grown: 150, a close shot: about 65)
        var door = Slot.Edge(0, 0, 0, 0);
        // at the solid post beside the doorway, not through the opening
        var target = s.WorldTransform(structures.Origin) * Structures.LocalTransform(s.Pieces[door].Piece) * new Vector3(0.9f, 1.2f, 0);
        var eye = me.Camera.GlobalPosition;
        var shotgun = Weapons.Get(ItemId.Shotgun)!;
        bool hit = BuildTool.TryHit(me, eye, (target - eye).Normalized(), shotgun);
        Expect(hit && s.Pieces.TryGetValue(door, out var d) && d.Damage > 0, $"a shot at the door wall chipped it ({hit})");

        // 6. take a piece back: refunded
        planks = inv.CountPlain(ItemId.WoodPlanks);
        string? refused = "waiting";
        structures.RequestRemove(s.Id, Slot.Floor(1, 1, -1), r => refused = r);
        await Until(() => refused != "waiting", 3);
        Expect(refused == null && !s.Pieces.ContainsKey(Slot.Floor(1, 1, -1)), $"the landing was taken back ({refused})");

        // 7. tidy up: everything this probe built comes down
        foreach (var built in structures.All.Values.Where(x => !before.Contains(x.Id)).ToList())
            foreach (var slot in built.Pieces.Keys.ToList())
                if (built.Pieces.ContainsKey(slot)) structures.RequestRemove(built.Id, slot, _ => { });
        Expect(await Until(() => structures.All.Keys.All(before.Contains), 3), "the probe's structures are gone");
    }

    /// <summary>Builds one piece on <paramref name="s"/> (no materials: the real path is step 1) and checks the answer.</summary>
    private async Task Build(Structure s, Piece piece, string? refusedContaining)
    {
        string? answer = "waiting";
        Structures.Instance!.RequestBuild(s.Id, default, piece, r => answer = r);
        await Until(() => answer != "waiting", 3);
        bool ok = refusedContaining == null ? answer == null : answer?.Contains(refusedContaining, StringComparison.OrdinalIgnoreCase) == true;
        Expect(ok, $"{BuildGrid.Name(piece.Kind)} {piece.Material} at {piece.Slot}: {(refusedContaining == null ? "built" : "refused")} ({answer ?? "built"})");
    }

    private void Shot(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"build_{name}.png"));
    }

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
        GD.Print($"[buildcheck] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[buildcheck] RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
