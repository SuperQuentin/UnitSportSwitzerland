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

    public static int? ParseArgs() => CmdArgs.Has("--lootstats") ? CmdArgs.Int("--lootstats") ?? 20 : null;

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
                    var types = BuildingTypes.For(tile);
                    for (int i = 0; i < tile.Buildings.Count; i++)
                    {
                        // a church is one interior, planned from its primary building
                        if (types.GroupOf(i) is { } g && g.Primary != i) continue;
                        if (InteriorGenerator.Generate(tile, i, roads, grid) is { } l) list.Add(l);
                    }
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

        // the locked containers (#165): how many buildings of a kind have one, and what they give
        GD.Print($"[loot] {"kind",-18}{"lockers%",9}{"safes%",8}{"guns/100",10}{"shells/100",11}");
        foreach (var (kind, t) in tallies.OrderByDescending(kv => kv.Value.Buildings))
        {
            double per = (double)t.Buildings * _epochs;
            GD.Print($"[loot] {kind,-18}{100.0 * t.Types.GetValueOrDefault(FurnitureType.GunLocker) / t.Buildings,9:F1}"
                + $"{100.0 * t.Types.GetValueOrDefault(FurnitureType.Safe) / t.Buildings,8:F1}"
                + $"{100.0 * t.Seen.GetValueOrDefault(ItemId.Shotgun) / per,10:F1}{100.0 * t.Seen.GetValueOrDefault(ItemId.Shells) / per,11:F1}");
        }

        bool ok = true;

        // #213: cellars, shelters, the room mix, banks; and any plan the validator rejects
        GD.Print($"[rooms] {"kind",-18}{"cellar%",8}{"shelter%",9}");
        foreach (var g in layouts.GroupBy(l => l.Kind).OrderByDescending(g => g.Count()))
        {
            int count = g.Count();
            GD.Print($"[rooms] {g.Key,-18}{100.0 * g.Count(l => l.Below > 0) / count,8:F1}"
                + $"{100.0 * g.Count(l => l.Floors.Any(f => f.Rooms.Any(r => r.Type == RoomType.Shelter))) / count,9:F1}");
        }
        var roomMix = layouts.SelectMany(l => l.Floors.SelectMany(f => f.Rooms)).GroupBy(r => r.Type)
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        GD.Print("[rooms] mix: " + string.Join(", ", roomMix));
        var banks = layouts.Where(l => l.IsBank).ToList();
        GD.Print($"[rooms] {banks.Count} bank(s); " + string.Join("; ", banks.Select(b =>
            $"{b.Key} {b.Floors.Count} floor(s), counter {b.Furniture.Count(f => f.Type == FurnitureType.TellerDesk)}, "
            + $"vault safes {b.Furniture.Count(f => f.Type == FurnitureType.VaultSafe)} (Simon "
            + string.Join("/", b.Furniture.Select((f, i) => (f, i)).Where(x => x.f.Type == FurnitureType.VaultSafe)
                .Select(x => LootTables.SimonSequence(b, x.i, 0).Length)) + ")")));
        int invalid = 0;
        foreach (var l in layouts)
            if (InteriorValidator.Validate(l) is { Count: > 0 } problems)
            {
                if (invalid++ < 8) GD.Print($"[rooms] invalid {l.Key} ({l.Kind}, {l.Below} below): {string.Join("; ", problems.Take(3))}");
            }
        GD.Print($"[rooms] {invalid} of {layouts.Count} plans fail validation");
        // #433: a home cinema's seats look at its screen (turned to face it, and in front of it)
        int cinemas = 0, facing = 0, seats = 0, seatsFacing = 0;
        foreach (var l in layouts)
            for (int fl = 0; fl < l.Floors.Count; fl++)
                foreach (var r in l.Floors[fl].Rooms.Where(r => r.Type == RoomType.HomeCinema))
                {
                    bool In(FurniturePlan p) => p.Floor == fl && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1;
                    var screen = l.Furniture.FirstOrDefault(p => p.Type == FurnitureType.CinemaScreen && In(p));
                    if (screen == null) continue;
                    cinemas++;
                    var front = new Basis(Vector3.Up, screen.Turns * Mathf.Pi / 2) * Vector3.Back;
                    bool Faces(FurniturePlan p) => p.Turns == ((screen.Turns + 2) & 3)
                        && (p.X - screen.X) * front.X + (p.Z - screen.Z) * front.Z > 1f;
                    var chairs = l.Furniture.Where(p => p.Type is FurnitureType.Sofa or FurnitureType.Armchair && In(p)).ToList();
                    if (chairs.Any(p => p.Type == FurnitureType.Sofa && Faces(p))) facing++;
                    seats += chairs.Count;
                    seatsFacing += chairs.Count(Faces);
                }
        GD.Print($"[rooms] {cinemas} home cinema(s): the sofa faces the screen in {facing}, {seatsFacing} of {seats} seats do");
        if (cinemas > 0 && facing < cinemas * 0.9) { GD.Print("[rooms] FAIL: home cinema sofas not facing their screen"); ok = false; }
        // a few plans to look at: banks, cellars with a shelter, a block of flats
        string svgDir = ProjectSettings.GlobalizePath("res://test_output/rooms");
        Directory.CreateDirectory(svgDir);
        foreach (var l in banks.Concat(layouts.Where(l => l.Kind == BuildingKind.House && l.Below > 0).Take(6))
            .Concat(layouts.Where(l => l.Kind == BuildingKind.Apartment && l.Below > 0).Take(2)))
            File.WriteAllText(Path.Combine(svgDir, $"{(l.IsBank ? "Bank" : l.Kind.ToString())}_{l.Key}.svg"), InteriorValidator.ToSvg(l));
        GD.Print($"[rooms] plans written to {svgDir}");

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
