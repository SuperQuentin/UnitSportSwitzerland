using Godot;
using UnitSport.Build;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>
/// <c>--prefabcheck [shots]</c> (offline, <c>--systems ui,physics,build</c>): every Battle Royale prefab
/// (#276) put up through <see cref="Structures.SpawnPrefab"/>, the way a match does, on a row across the
/// flat fixture, with its gadgets owned by the match. Each must be drawn whole and stand; breaking the
/// tower's ground wall leaves rubble with planks in it (<see cref="Structures.Rubble"/>); breaking a
/// footbridge's end brings down the four cells wood cannot reach from the other bank. Everything is
/// cleared at the end (structures removed, the match's gadgets by owner). With <c>shots</c> (windowed)
/// it writes <c>test_output/prefab_*.png</c>.
/// </summary>
public partial class PrefabProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--prefabcheck") >= 0;
    private static bool Shots => OS.GetCmdlineUserArgs().Contains("shots");

    private int _failures;
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        await Seconds(2.0);
        if (Me == null) GetParent<Core.ClientWorld>().ToggleMode();
        if (!await Until(() => Me is { } m && m.IsOnFloor() && Structures.Instance != null && PlacedObjects.Instance != null, 120))
        {
            GD.Print("[prefabcheck] RESULT: FAILED - no player on the ground");
            GetTree().Quit(1);
            return;
        }
        await Seconds(1.0);
        try { await Run(Me!, Structures.Instance!, PlacedObjects.Instance!); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        GD.Print(_failures == 0 ? "[prefabcheck] RESULT: ok" : $"[prefabcheck] RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Run(FootPlayer me, Structures structures, PlacedObjects placed)
    {
        var origin = structures.Origin;
        var home = me.GlobalPosition;
        var before = structures.All.Keys.ToHashSet();
        var rubble = new List<(Vector3 At, List<ItemStack> Stacks)>();
        structures.Rubble = (at, stacks) => rubble.Add((at, stacks));

        // a row 15 m in front, 22 m apart, everything facing the player (yaw 0: local −Z away from it)
        var spawned = new Dictionary<string, Structure>();
        int i = 0;
        foreach (var prefab in BrPrefabs.All)
        {
            var at = home + new Vector3(-70 + i++ * 22f, 0, -15f);
            var (e, n) = origin.ToLv95(at);
            var s = structures.SpawnPrefab(prefab.Pieces, e, n, at.Y, 0f);
            spawned[prefab.Name] = s;
            var frame = s.WorldTransform(origin);
            foreach (var g in prefab.Gadgets)
            {
                var spot = frame * new Transform3D(new Basis(Vector3.Up, Structures.DirYaw(g.Dir)), new Vector3(g.X, g.Y, g.Z));
                switch (g.Kind)
                {
                    case PrefabGadget.Zipline:
                    {
                        var (he, hn) = origin.ToLv95(spot.Origin);
                        var low = spot.Origin + new Vector3(0, -spot.Origin.Y + home.Y, 60f);
                        placed.ServerPlace(PlacedKind.Zipline, new Transform3D(Basis.Identity, low), Gadgets.ZipPayload(he, hn, spot.Origin.Y), PlacedObjects.MatchOwner);
                        break;
                    }
                    case PrefabGadget.RopeLadder:
                        placed.ServerPlace(PlacedKind.RopeLadder, spot, Gadgets.LadderPayload(g.Length), PlacedObjects.MatchOwner);
                        break;
                    default:
                        placed.ServerPlace(g.Kind switch
                        {
                            PrefabGadget.Trampoline => PlacedKind.Trampoline, PrefabGadget.LaunchPad => PlacedKind.LaunchPad, _ => PlacedKind.CamoNet,
                        }, spot, "", PlacedObjects.MatchOwner);
                        break;
                }
            }
        }
        await Seconds(0.5);

        foreach (var prefab in BrPrefabs.All)
        {
            var s = spawned[prefab.Name];
            var root = GetTree().Root.FindChild($"S{s.Id}", true, false) as Node3D;
            Expect(s.Match && s.Pieces.Count == prefab.Pieces.Length && root?.GetChildCount() == prefab.Pieces.Length,
                $"{prefab.Name}: {s.Pieces.Count} pieces kept, {root?.GetChildCount()} drawn of {prefab.Pieces.Length}");
            Expect(BuildGrid.Fallen(s.Grid).Count == 0, $"{prefab.Name}: stands");
        }
        int gadgets = placed.All.Values.Count(o => o.Owner == PlacedObjects.MatchOwner);
        Expect(gadgets == BrPrefabs.All.Sum(p => p.Gadgets.Length), $"{gadgets} gadgets owned by the match");

        if (Shots)
        {
            me.GlobalPosition = home + new Vector3(0, 30, 45);
            me.LookYaw = 0;
            me.LookPitch = -0.35f;
            await Seconds(1.5);
            Shot("row");
            me.GlobalPosition = home;
            await Seconds(0.5);
        }

        // the tower's ground-floor window wall broken: rubble with some of its planks
        var tower = spawned[BrPrefabs.LookoutTower.Name];
        var wall = Slot.Edge(0, 0, 0, 1);
        for (int k = 0; k < 10 && tower.Pieces.ContainsKey(wall); k++)
            structures.SendHit(tower.Id, wall, 120f, ItemId.Shotgun);
        Expect(!tower.Pieces.ContainsKey(wall), "the tower's ground wall broke");
        Expect(rubble.Count == 1 && rubble[0].Stacks.Any(st => st.Id == ItemId.WoodPlanks && st.Count > 0),
            $"it left rubble ({string.Join(", ", rubble.SelectMany(r => r.Stacks).Select(st => $"{st.Count} {st.Id}"))})");

        // a footbridge's end broken: the four nearest cells fall, the rest hangs from the far bank
        var bridge = spawned[BrPrefabs.SuspensionBridge.Name];
        int floors = bridge.Pieces.Values.Count(p => p.Piece.Kind == PieceKind.Floor);
        for (int k = 0; k < 10 && bridge.Pieces.ContainsKey(Slot.Floor(0, 0, 0)); k++)
            structures.SendHit(bridge.Id, Slot.Floor(0, 0, 0), 120f, ItemId.Shotgun);
        int left = bridge.Pieces.Values.Count(p => p.Piece.Kind == PieceKind.Floor);
        Expect(floors - left == 5, $"the bridge end broke and 4 cells fell with it ({floors - left} gone)");
        if (Shots) { await Seconds(0.5); Shot("broken"); }

        // the match is over: everything goes
        structures.ClearMatch();
        placed.ClearOwner(PlacedObjects.MatchOwner);
        await Seconds(0.3);
        Expect(structures.All.Keys.All(before.Contains), "the prefabs are gone");
        Expect(!placed.All.Values.Any(o => o.Owner == PlacedObjects.MatchOwner), "the match's gadgets are gone");
    }

    private void Shot(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"prefab_{name}.png"));
    }

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
        GD.Print($"[prefabcheck] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
