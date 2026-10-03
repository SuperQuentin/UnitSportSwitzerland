using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// <c>godot --path . -- --gathercheck[,out.png] [--at E,N]</c>
///
/// <para>
/// Finds a real lake or river shore, a patch of rock or scree, and a tree in the tiles around the
/// spawn; stands a player at each, facing it, and checks <see cref="Gathering"/> offers the right
/// thing and that a harvest lands in the pack. The screenshot is taken at the tree, prompt showing.
/// Non-zero exit on any failure; a resource the area simply does not have is skipped, not failed.
/// </para>
/// </summary>
public partial class GatherProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly Gathering _gathering;
    private readonly ItemController _items;
    private readonly string? _shot;
    private FootPlayer? _player;
    private bool _ok = true;
    private int _tested;

    public GatherProbe(ChunkManager chunks, WorldOrigin origin, Gathering gathering, ItemController items, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        _gathering = gathering;
        _items = items;
        _shot = shot;
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--gathercheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[gather] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override async void _Ready()
    {
        var (e, n) = SpawnPoint.ParseTarget();
        var centre = TileId.FromLv95(e, n);
        var source = _chunks.Source!;

        // look for one of each over the 5x5 tiles around the spawn, nearest tiles first
        // kept in LV95: the origin moves when the player is put down near them (#185)
        (GlobalPos Stand, GlobalPos Face)? water = null, stone = null, tree = null;
        var tiles = new List<TileId>();
        for (int r = 0; r <= 2; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r) tiles.Add(new TileId(centre.E + dx, centre.N + dy));

        foreach (var id in tiles)
        {
            if (water != null && stone != null && tree != null) break;
            var cover = await source.LoadCoverAsync(id);
            var grid = await source.LoadChunkAsync(id);
            if (cover == null || grid == null) continue;
            const int size = ChunkFormat.GridSize;
            // a tree within reach outranks the ground, so the rock spot must be clear of trees
            var trees = await source.LoadTreesAsync(id) ?? new();
            var wooded = trees.Select(t => ((int)(t.X / 5), (int)(t.Z / 5))).ToHashSet();
            bool Clear(int col, int row)
            {
                for (int a = -1; a <= 1; a++)
                    for (int b = -1; b <= 1; b++)
                        if (wooded.Contains((col / 5 + a, row / 5 + b))) return false;
                return true;
            }
            for (int row = 20; row < size - 20 && (water == null || stone == null); row += 7)
                for (int col = 20; col < size - 20; col += 7)
                {
                    var c = (CoverClass)cover[row * size + col];
                    // a shore: dry ground with open water two metres east
                    if (water == null && c is CoverClass.Open or CoverClass.Wetland
                        && (CoverClass)cover[row * size + col + 2] == CoverClass.Water
                        && (CoverClass)cover[row * size + col + 4] == CoverClass.Water)
                        // in it, wading: water is only collected standing in it (#380)
                        water = (World(id, grid, col + 3, row), World(id, grid, col + 5, row));
                    // rock in the middle of a rock field, so the step ahead is rock too
                    if (stone == null && c is CoverClass.Scree or CoverClass.Rock or CoverClass.LooseScree or CoverClass.Quarry
                        && (CoverClass)cover[row * size + col + 3] == c && (CoverClass)cover[(row + 3) * size + col] == c
                        && Flat(grid, col, row)
                        && Clear(col, row) && Clear(col + 3, row))
                        stone = (World(id, grid, col, row), World(id, grid, col + 3, row));
                }
            if (tree == null && trees.Count > 0)
            {
                var t = trees.FirstOrDefault(t => t.Kind == 0 || t.Kind == 3);
                var at = _origin.ToWorld(id.MinE + t.X, id.MaxN - t.Z, t.Y);
                tree = (_origin.ToGlobal(at + new Vector3(1.4f, 0, 0)), _origin.ToGlobal(at));
            }
        }

        _player = new FootPlayer { Name = "GatherProbe", Terrain = _chunks };
        AddChild(_player);
        _gathering.PlayerOverride = () => _player;

        await Visit("water", water, Gathering.Resource.Water, ItemId.WaterBottle);
        await Visit("stone", stone, Gathering.Resource.Stone, ItemId.Stone, ItemId.SandBag);
        await Visit("tree", tree, Gathering.Resource.TreeWood, ItemId.Firewood);

        Check(_tested > 0, $"found {_tested} kind(s) of resource near the spawn");
        GD.Print(_ok ? "[gather] RESULT: ok" : "[gather] RESULT: FAILED");
        _chunks.RemoveAnchor(_player);
        for (int i = 0; i < 600 && !_chunks.Settled; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().Quit(_ok ? 0 : 1);
    }

    /// <summary>Gentle enough to stand on: steep scree slides a player metres away before the check.</summary>
    private static bool Flat(ChunkGrid g, int col, int row)
    {
        double h = g.HeightMetersAt(col, row);
        foreach (var (dc, dr) in new[] { (4, 0), (-4, 0), (0, 4), (0, -4) })
            if (Math.Abs(g.HeightMetersAt(col + dc, row + dr) - h) > 1.2) return false;
        return true;
    }

    private static GlobalPos World(TileId id, ChunkGrid grid, int col, int row) =>
        new(id.MinE + col * ChunkFormat.SpacingM, id.MaxN - row * ChunkFormat.SpacingM, grid.HeightMetersAt(col, row));

    private async Task Visit(string what, (GlobalPos Stand, GlobalPos Face)? spot, Gathering.Resource expected, params ItemId[] gives)
    {
        if (spot is not { } s)
        {
            GD.Print($"[gather] (no {what} near the spawn: not tested)");
            return;
        }
        _tested++;
        GD.Print($"[gather] {what}: standing at {s.Stand}");
        var stand = _origin.ToWorld(s.Stand);
        var face = _origin.ToWorld(s.Face) - stand;
        float yaw = Mathf.Atan2(-face.X, -face.Z);
        _player!.LeaveInterior(stand + Vector3.Up * 1.5f, yaw);
        _player.Velocity = Vector3.Zero;

        // wait for collision under the new spot, then for the gatherer to notice
        double t = 0;
        while (t < 30 && !(_player.IsOnFloor() && _gathering.Target == expected))
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            t += GetProcessDeltaTime();
            if (t > 20 && !_player.IsOnFloor()) break;
        }
        Check(_gathering.Target == expected, $"{what}: offered {_gathering.Target} (want {expected}) at {_player.GlobalPosition:F1}, moved {_origin.ToGlobal(_player.GlobalPosition).HorizontalDistanceTo(s.Stand):F1} m");
        if (_gathering.Target != expected) return;

        if (_shot != null && expected == Gathering.Resource.TreeWood)
        {
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
            GetViewport().GetTexture().GetImage().SavePng(_shot);
            GD.Print($"[gather] wrote {_shot}");
        }

        int before = gives.Sum(Count);
        Check(_gathering.DebugHarvest(_player), $"{what}: harvested");
        int gained = gives.Sum(Count) - before;
        Check(gained > 0, $"{what}: pack gained {gained}");
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }
}
