using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Build;

/// <summary>
/// <c>--buildnet A|B</c> with <c>--connect</c> (driven by <c>tools/buildnetcheck.sh</c>): building over
/// loopback. A builds a floor through the hammer's real path, then two walls and a floor resting on
/// them. B joins only after that, so it must get them from the join snapshot; with PvP off it may
/// neither damage A's wall nor take it, so the wall's damage stays 0 everywhere. A then breaks its own
/// wall: B must see it go, and the upper floor fall with it (its other wall still holds it: only the
/// broken one goes). A takes everything down at the end; B must see the structure gone.
/// Both use scratch inventories (<see cref="BuildProbe.Stock"/>).
/// </summary>
public partial class BuildNetProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--buildnet");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private string _role = "";
    private int _failures;

    public BuildNetProbe(ItemController items) => _items = items;
    public BuildNetProbe() : this(null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    private static readonly Slot Floor0 = Slot.Floor(0, 0, 0), WallA = Slot.Edge(0, 0, 0, 0), WallB = Slot.Edge(0, 0, 0, 2),
        Upper = Slot.Floor(0, 1, 0);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Until(() => Chat != null && Permissions.Online && Me is { } m && m.IsOnFloor() && Structures.Instance != null, 150))
        {
            GD.Print($"[buildnet {_role}] RESULT: FAILED - no player on the ground");
            GetTree().Quit(1);
            return;
        }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        _items.BuildTool.AlwaysShow = true;
        await Seconds(1.5);
        try
        {
            if (_role == "A") await RunA(Me!, Structures.Instance!);
            else await RunB(Structures.Instance!);
        }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        GD.Print(_failures == 0 ? $"[buildnet {_role}] RESULT: ok" : $"[buildnet {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.0);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task RunA(FootPlayer me, Structures structures)
    {
        var tool = _items.BuildTool;
        // a run that died half way left its pieces on the server: take them down first
        foreach (var old in structures.All.Values.ToList())
            foreach (var (slot, p) in old.Pieces.ToList())
                if (p.Owner == "BuilderA") structures.RequestRemove(old.Id, slot, _ => { });
        await Until(() => structures.All.Values.All(x => x.Pieces.Values.All(p => p.Owner != "BuilderA")), 5);
        var before = structures.All.Keys.ToHashSet();
        _items.Inventory.Select(Inventory.HotbarSize - 1);
        me.LookYaw = 0f;
        me.LookPitch = -0.45f;
        tool.Select(PieceKind.Floor, BuildMaterial.Wood);
        Expect(await Until(() => tool.Last.Valid, 10), $"the ghost is valid ({tool.Last.Reason})");
        _items.UseSlot(me, Inventory.HotbarSize - 1);
        Expect(await Until(() => structures.All.Keys.Any(k => !before.Contains(k)), 5), "the server built the floor");
        var s = structures.All.Values.First(x => !before.Contains(x.Id));
        await Build(structures, s, new Piece(WallA, PieceKind.Wall, 0, BuildMaterial.Metal, true));
        await Build(structures, s, new Piece(WallB, PieceKind.Wall, 0, BuildMaterial.Stone, true));
        await Build(structures, s, new Piece(Upper, PieceKind.Floor, 0, BuildMaterial.Wood, false));
        Expect(s.Pieces.Count == 4, $"4 pieces here ({s.Pieces.Count})");
        await Seconds(BuildGrid.Spec(BuildMaterial.Metal).Seconds + 0.5);   // grown before B tries anything
        // chat is not replayed to a late joiner: say it until B answers
        for (int i = 0; i < 60 && !_heard.Any(l => l.Contains("BN B seen")); i++)
        {
            Say($"built {s.Id}");
            await Heard("B", "seen", 2.5);
        }
        if (!_heard.Any(l => l.Contains("BN B seen"))) { Fail("B never reported"); return; }
        Expect(s.Pieces.TryGetValue(WallA, out var w) && w.Damage == 0, "B's shot did nothing to my wall (PvP off)");

        // break my own metal wall: 5 shotgun hits of 120 once grown
        for (int i = 0; i < 6 && s.Pieces.ContainsKey(WallA); i++)
        {
            structures.SendHit(s.Id, WallA, 120f, ItemId.Shotgun);
            await Seconds(0.25);
        }
        Expect(await Until(() => !s.Pieces.ContainsKey(WallA), 5), "my wall broke");
        Expect(s.Pieces.ContainsKey(Upper), "the upper floor still rests on the other wall");
        Say("broke");
        await Heard("B", "done", 60);

        foreach (var slot in s.Pieces.Keys.ToList()) structures.RequestRemove(s.Id, slot, _ => { });
        Expect(await Until(() => !structures.All.ContainsKey(s.Id), 5), "all taken down");
        Say("clean");
        await Heard("B", "gone", 30);
    }

    private async Task RunB(Structures structures)
    {
        if (!await Heard("A", "built", 150)) { Fail("A never built"); return; }
        long id = long.Parse(_heard.Last(l => l.Contains("BN A built")).Split(' ').Last());
        Expect(await Until(() => structures.All.TryGetValue(id, out var x) && x.Pieces.Count == 4, 10), "the join snapshot has A's 4 pieces");
        if (!structures.All.TryGetValue(id, out var s)) { Fail("no structure"); return; }
        Expect(GetTree().Root.FindChild($"S{id}", true, false) is Node3D root && root.GetChildCount() == 4, "drawn: 4 piece bodies");

        // not mine, and PvP is off: a hit is ignored, a take is refused
        structures.SendHit(id, WallA, 100f, ItemId.Shotgun);
        string? refused = "waiting";
        structures.RequestRemove(id, WallA, r => refused = r);
        Expect(await Until(() => refused != "waiting", 5) && refused?.Contains("not yours") == true, $"taking A's wall refused ({refused})");
        await Seconds(1.0);
        Expect(s.Pieces.TryGetValue(WallA, out var w) && w.Damage == 0, "my hit on A's wall was ignored");

        // interest (#359): 1.6 km away the structure is taken back, nearby again it comes back whole
        if (Me is { } me)
        {
            var home = me.GlobalPosition;
            me.GlobalPosition = home + new Vector3(1600f, 400f, 0);
            Expect(await Until(() => !structures.All.ContainsKey(id), 8), "far away, A's structure is taken back here");
            me.GlobalPosition = home + Vector3.Up * 2f;
            Expect(await Until(() => structures.All.TryGetValue(id, out var back) && back.Pieces.Count == 4, 8),
                "back again, it is sent whole");
            s = structures.All[id];
            await Until(() => me.IsOnFloor(), 5);
        }
        Say("seen");

        if (!await Heard("A", "broke", 60)) { Fail("A never broke its wall"); return; }
        Expect(await Until(() => !s.Pieces.ContainsKey(WallA), 5), "A's broken wall is gone here too");
        Expect(s.Pieces.ContainsKey(Upper), "the upper floor still stands here");
        Say("done");

        if (!await Heard("A", "clean", 60)) { Fail("A never cleaned up"); return; }
        Expect(await Until(() => !structures.All.ContainsKey(id), 5), "the structure is gone here too");
        Say("gone");
    }

    private async Task Build(Structures structures, Structure s, Piece piece)
    {
        string? answer = "waiting";
        structures.RequestBuild(s.Id, default, piece, r => answer = r);
        await Until(() => answer != "waiting", 5);
        Expect(answer == null, $"{BuildGrid.Name(piece.Kind)} at {piece.Slot}: built ({answer ?? "ok"})");
    }

    private void Say(string what)
    {
        GD.Print($"[buildnet {_role}] say {what}");
        Chat?.Send($"BN {_role} {what}");
    }

    private Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"BN {role} {what}")), seconds);

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[buildnet {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    /// <summary>A step that cannot go on: counted, so the run ends with RESULT: FAILED.</summary>
    private void Fail(string why) => Expect(false, why);
}
