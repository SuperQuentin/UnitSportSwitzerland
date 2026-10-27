using System.Diagnostics;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --outlinesurvey[,out.txt] --chunks D:/.../terrain_chunks [--no-outlines]</c> (#1290):
/// how real buildings' interiors follow their outlines. On the tiles round a few towns (Geneva, Lausanne, Sion,
/// Fribourg) it plans every building with a door as the game does and counts, per building kind: how many outlines
/// are not their box, how many plans then took shapes, why the others went back to rectangles
/// (<see cref="InteriorGenerator.OutlineFailure"/>), how much of the outline the ground floor fills and how much of it
/// stands outside, validator errors, and the time a plan takes. <c>--no-outlines</c> plans as before #1290, for the
/// comparison. Writes the table to the outfile (default <c>test_output/outlinesurvey.txt</c>). No window, no world.
/// </summary>
public static class OutlineSurvey
{
    public static bool Requested => Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith("--outlinesurvey", StringComparison.Ordinal));

    /// <summary>The towns surveyed: a 3 x 3 block of tiles round each.</summary>
    private static readonly (string Name, double E, double N)[] Towns =
    [
        ("Geneva", 2500300, 1118450), ("Lausanne", 2538000, 1152500), ("Sion", 2594000, 1120000), ("Fribourg", 2578500, 1183800),
    ];

    private sealed class Row
    {
        public int Plans, Shaped, Fallback, Invalid, Boxlike;
        public double Used, Outside, Area, Ms, MaxMs;
        public readonly Dictionary<string, int> Why = new();
    }

    /// <summary>At most this many tiles a town (<c>--tiles N</c>), for a quicker look.</summary>
    private static int MaxTiles => int.TryParse(CmdArgs.Value("--tiles"), out int n) ? n : int.MaxValue;

    public static int Run()
    {
        var args = OS.GetCmdlineUserArgs();
        string arg = args.First(a => a.StartsWith("--outlinesurvey", StringComparison.Ordinal));
        string outPath = arg.Contains(',') ? arg[(arg.IndexOf(',') + 1)..] : "test_output/outlinesurvey.txt";
        var src = new LocalChunkSource(CmdArgs.Value("--chunks") ?? "terrain_chunks");
        InteriorGenerator.FollowOutlines = !CmdArgs.Has("--no-outlines");
        var rows = new SortedDictionary<string, Row>();
        var total = new Row();
        int tiles = 0;
        foreach (var town in Towns)
        {
            var c = TileId.FromLv95(town.E, town.N);
            int townTiles = 0;
            for (int de = -1; de <= 1; de++)
                for (int dn = -1; dn <= 1; dn++)
                {
                    var id = new TileId(c.E + de, c.N + dn);
                    var tile = src.LoadBuildingsAsync(id).GetAwaiter().GetResult();
                    if (tile == null) continue;
                    if (++townTiles > MaxTiles) continue;
                    tiles++;
                    var roads = src.LoadRoadsAsync(id).GetAwaiter().GetResult();
                    var doors = BuildingFootprint.ComputeDoors(tile, roads, null);
                    var tileWatch = Stopwatch.StartNew();
                    int tilePlans = 0;
                    (double Ms, int Index, BuildingKind Kind) slowest = default;
                    foreach (int index in doors.Where(d => d.Width > 0).Select(d => d.Index).Distinct())
                    {
                        var b = tile.Buildings[index];
                        if (BuildingFootprint.Compute(tile, index, roads, null) is not { } fp) continue;
                        var row = rows.TryGetValue(b.Kind.ToString(), out var r) ? r : rows[b.Kind.ToString()] = new Row();
                        InteriorGenerator.OutlineFailure = null;
                        var watch = Stopwatch.StartNew();
                        InteriorLayout? l;
                        try { l = InteriorGenerator.Generate(tile, index, roads, null); }
                        catch (Exception ex) { GD.PrintErr($"[outlinesurvey] {id} #{index}: {ex.Message}"); continue; }
                        double ms = watch.Elapsed.TotalMilliseconds;
                        tilePlans++;
                        if (ms > slowest.Ms) slowest = (ms, index, b.Kind);
                        if (ms > 2000) GD.Print($"[outlinesurvey]   slow: {id} #{index} {b.Kind} {fp.Width:F0}x{fp.Depth:F0} m, {ms:F0} ms");
                        if (l == null) continue;
                        foreach (var x in new[] { row, total })
                        {
                            x.Plans++;
                            x.Ms += ms;
                            x.MaxMs = Math.Max(x.MaxMs, ms);
                        }
                        if (InteriorValidator.Validate(l) is { Count: > 0 } bad)
                        {
                            row.Invalid++;
                            total.Invalid++;
                            GD.Print($"[outlinesurvey]   invalid: {id} #{index} {b.Kind} {l.Type}: {string.Join("; ", bad.Take(3))}");
                        }
                        var grid = PlanOutline.FineGrid(b, fp.Center, fp.AxisU, fp.Width, fp.Depth);
                        if (grid == null) { row.Boxlike++; total.Boxlike++; continue; }
                        bool shaped = l.Floors.Any(f => f.Rooms.Any(q => q.Shape != null));
                        if (shaped) { row.Shaped++; total.Shaped++; }
                        else if (InteriorGenerator.OutlineFailure is { } why)
                        {
                            row.Fallback++;
                            total.Fallback++;
                            // the reason without its numbers, so alike ones count together
                            string key = System.Text.RegularExpressions.Regex.Replace(why, @"floor \d+|room \d+|[-\d.,]+ ?m²?", "#");
                            key = key.Length > 90 ? key[..90] : key;
                            row.Why[key] = row.Why.GetValueOrDefault(key) + 1;
                        }
                        var rings = PlanOutline.Rings(grid, fp.Width, fp.Depth);
                        var (used, outside, area) = Cover(l, rings);
                        foreach (var x in new[] { row, total }) { x.Used += used; x.Outside += outside; x.Area += area; }
                    }
                    GD.Print($"[outlinesurvey] {town.Name} {id}: {tilePlans} plans in {tileWatch.Elapsed.TotalSeconds:F1} s, slowest #{slowest.Index} {slowest.Kind} {slowest.Ms:F0} ms");
                }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"outline survey ({(InteriorGenerator.FollowOutlines ? "outlines followed" : "--no-outlines: as before #1290")}), {tiles} tiles round {string.Join(", ", Towns.Select(t => t.Name))}");
        sb.AppendLine("kind           plans  boxlike  shaped  fallback  invalid   fill  outside   ms avg  ms max");
        void Line(string name, Row x) => sb.AppendLine(
            $"{name,-14}{x.Plans,6}{x.Boxlike,9}{x.Shaped,8}{x.Fallback,10}{x.Invalid,9}{(x.Area > 0 ? x.Used / x.Area : 0),7:P0}{(x.Area > 0 ? x.Outside / x.Area : 0),9:P0}{(x.Plans > 0 ? x.Ms / x.Plans : 0),9:F1}{x.MaxMs,8:F0}");
        foreach (var (name, x) in rows) Line(name, x);
        Line("all", total);
        sb.AppendLine("fill and outside: of the outline's area, over the plans whose outline is not their box");
        sb.AppendLine();
        sb.AppendLine("why a plan went back to rectangles:");
        foreach (var (name, x) in rows)
            foreach (var (why, n) in x.Why.OrderByDescending(w => w.Value).Take(8))
                sb.AppendLine($"  {name,-12} {n,5}  {why}");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!);
        System.IO.File.WriteAllText(outPath, sb.ToString());
        GD.Print(sb.ToString());
        GD.Print($"[outlinesurvey] wrote {outPath}");
        GD.Print($"[outlinesurvey] RESULT: {(total.Plans > 0 ? "ok" : "FAILED (no plans: no tiles under --chunks?)")}");
        return total.Plans > 0 ? 0 : 1;
    }

    /// <summary>The ground floor on a 0.5 m grid: m² of the outline it fills, m² outside it, the outline's m².</summary>
    private static (double Used, double Outside, double Area) Cover(InteriorLayout l, List<Vector2[]> rings)
    {
        double used = 0, outside = 0, area = 0;
        for (float x = -l.Width / 2 + 0.25f; x < l.Width / 2; x += 0.5f)
            for (float z = -l.Depth / 2 + 0.25f; z < l.Depth / 2; z += 0.5f)
            {
                bool inside = RoomShapes.Contains(rings, new Vector2(x, z));
                if (inside) area += 0.25;
                if (!l.GroundFloor.Rooms.Any(r => r.Holds(x, z))) continue;
                if (inside) used += 0.25;
                else outside += 0.25;
            }
        return (used, outside, area);
    }
}
