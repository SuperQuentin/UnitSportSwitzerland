using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Loot;

/// <summary>
/// <c>godot --path . -- --lootstats [epochs] [--at E,N]</c>
///
/// <para>
/// Plans every real building in the 3x3 tiles around the spawn (the same generator the game uses)
/// and rolls its furniture for <c>epochs</c> restock periods (default 20). Prints what an average
/// building of each kind holds per category, and how often each item turns up in houses. Exits
/// non-zero if an average house strays far from what <see cref="LootTables"/> is meant to give —
/// so tuning the tables is checked, not guessed.
/// </para>
/// </summary>
public partial class LootProbe : Node
{
    private readonly IChunkSource _source;
    private readonly ChunkManager _chunks;
    private readonly int _epochs;

    public LootProbe(IChunkSource source, ChunkManager chunks, int epochs)
    {
        _source = source;
        _chunks = chunks;
        _epochs = epochs;
    }

    public static int? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--lootstats");
        if (i < 0) return null;
        return i + 1 < args.Length && int.TryParse(args[i + 1], out int n) ? n : 20;
    }

    private sealed class Tally
    {
        public int Buildings, Containers;
        public readonly Dictionary<ItemCategory, double> Items = new();
        public double Francs;
        public readonly Dictionary<ItemId, int> Seen = new();
        public readonly Dictionary<FurnitureType, int> Types = new();
    }

    public override async void _Ready()
    {
        var (e, n) = SpawnPoint.ParseTarget();
        var centre = TileId.FromLv95(e, n);
        var layouts = new List<InteriorLayout>();
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var id = new TileId(centre.E + dx, centre.N + dy);
                var tile = await _source.LoadBuildingsAsync(id);
                if (tile == null) continue;
                var roads = await _source.LoadRoadsAsync(id);
                var grid = await _source.LoadChunkAsync(id);
                layouts.AddRange(await Task.Run(() =>
                {
                    var list = new List<InteriorLayout>();
                    for (int i = 0; i < tile.Buildings.Count; i++)
                        if (BuildingFootprint.Compute(tile, i, roads, grid) is { } fp)
                            list.Add(InteriorGenerator.Generate(fp, tile.Buildings[i]));
                    return list;
                }));
            }

        var tallies = new Dictionary<BuildingKind, Tally>();
        foreach (var l in layouts)
        {
            var t = tallies.TryGetValue(l.Kind, out var x) ? x : tallies[l.Kind] = new Tally();
            t.Buildings++;
            foreach (var f in l.Furniture.Where(f => LootTables.IsLootable(f.Type)))
            {
                t.Containers++;
                t.Types[f.Type] = t.Types.GetValueOrDefault(f.Type) + 1;
            }
            for (int epoch = 0; epoch < _epochs; epoch++)
                for (int f = 0; f < l.Furniture.Count; f++)
                    foreach (var s in LootTables.ContentsOf(l, f, epoch))
                    {
                        var cat = ItemDefs.Get(s.Id)!.Category;
                        if (cat == ItemCategory.Money) t.Francs += s.Count;
                        else t.Items[cat] = t.Items.GetValueOrDefault(cat) + s.Count;
                        t.Seen[s.Id] = t.Seen.GetValueOrDefault(s.Id) + 1;
                    }
        }

        var cats = new[] { ItemCategory.Food, ItemCategory.Water, ItemCategory.Medical, ItemCategory.Scrap,
            ItemCategory.Mineral, ItemCategory.Part };
        GD.Print($"[loot] {layouts.Count} buildings with interiors, {_epochs} restocks each");
        GD.Print($"[loot] {"kind",-18}{"n",5}{"cont",6}" + string.Concat(cats.Select(c => $"{c,9}")) + $"{"CHF",8}");
        foreach (var (kind, t) in tallies.OrderByDescending(kv => kv.Value.Buildings))
        {
            double per = (double)t.Buildings * _epochs;
            GD.Print($"[loot] {kind,-18}{t.Buildings,5}{(double)t.Containers / t.Buildings,6:F1}"
                + string.Concat(cats.Select(c => $"{t.Items.GetValueOrDefault(c) / per,9:F2}"))
                + $"{t.Francs / per,8:F1}");
        }

        bool ok = true;
        if (tallies.TryGetValue(BuildingKind.House, out var house))
        {
            double per = (double)house.Buildings * _epochs;
            GD.Print("[loot] house furniture: " + string.Join(", ", house.Types.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} {(double)kv.Value / house.Buildings:F1}")));
            GD.Print("[loot] houses: stacks found per hundred houses");
            foreach (var (id, count) in house.Seen.OrderByDescending(kv => kv.Value))
                GD.Print($"[loot]   {ItemDefs.Get(id)!.Name,-16}{100.0 * count / per,6:F1}");

            // the targets the tables were written for, with generous room either side
            void Expect(string what, double value, double lo, double hi)
            {
                bool pass = value >= lo && value <= hi;
                ok &= pass;
                GD.Print($"[loot] {(pass ? "ok  " : "FAIL")} house {what} {value:F2} (want {lo}–{hi})");
            }
            Expect("food", house.Items.GetValueOrDefault(ItemCategory.Food) / per, 1.5, 6);
            Expect("water", house.Items.GetValueOrDefault(ItemCategory.Water) / per, 0.5, 3);
            Expect("scrap", house.Items.GetValueOrDefault(ItemCategory.Scrap) / per, 3, 12);
            Expect("minerals", house.Items.GetValueOrDefault(ItemCategory.Mineral) / per, 0.5, 4);
            Expect("parts", house.Items.GetValueOrDefault(ItemCategory.Part) / per, 0.1, 1.5);
            Expect("CHF", house.Francs / per, 10, 60);
        }
        else
        {
            GD.Print("[loot] FAIL no houses near the spawn");
            ok = false;
        }
        // let any tile build started before the anchor went finish, or quitting races it
        for (int i = 0; i < 600 && !_chunks.Settled; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().Quit(ok ? 0 : 1);
    }
}
