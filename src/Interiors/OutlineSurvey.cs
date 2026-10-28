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
        // the street doors of the plans whose outline is not their box: how many, with no doorway inside,
        // with one more than a metre off the real door, and turned more than 10° from it
        public int Doors, Locked, Off, Turned;
        public double Used, Outside, Area, Ms, MaxMs;
        public readonly Dictionary<string, int> Why = new();
    }

    /// <summary>Locked street doors (#1304), by the plan's type, the wall they are on and the room behind them.</summary>
    private static readonly Dictionary<string, int> LockedBy = new();

    /// <summary>Why a plan wing by wing was not taken (#1304), by kind: its failure, or "not better" when it planned.</summary>
    private static readonly Dictionary<string, int> WingWhy = new();

    /// <summary>
    /// Why a street door has no doorway, in words that count together: the plan's type, which wall of the plan box
    /// the door is on (front: the main door's), and the ground-floor room 0.6 m in from it ("none": no room there).
    /// </summary>
    private static string LockedKey(InteriorLayout l, DoorSpot d)
    {
        var u = new Vector2(MathF.Cos(l.Yaw), -MathF.Sin(l.Yaw));
        var v = new Vector2(-u.Y, u.X);
        var rel = new Vector2(d.Position.X - l.CenterX, d.Position.Z - l.CenterZ);
        var o = new Vector2(d.Outward.X, d.Outward.Z);
        var at = new Vector2(rel.Dot(u), rel.Dot(v));
        var outward = new Vector2(o.Dot(u), o.Dot(v)).Normalized();
        string wall = outward.Y < -0.7f ? "front" : outward.Y > 0.7f ? "back" : "side";
        var p = at - outward * 0.6f;
        var room = l.GroundFloor.Rooms.FirstOrDefault(r => r.Holds(p.X, p.Y));
        if (room == null) return $"{l.Type,-12} {wall,-5} none";
        // how far the room's own wall stands from the door, and whether it took a shape: a door a room is
        // behind but whose wall is far off (a rectangle kept inside the outline) is a fitting problem, not a planning one
        float gap = float.MaxValue;
        var cs = room.Corners();
        for (int i = 0; i < cs.Length; i++) gap = Math.Min(gap, SegDist(at, cs[i], cs[(i + 1) % cs.Length]));
        string far = gap < 0.6f ? "<0.6" : gap < 2f ? "<2" : ">2";
        return $"{l.Type,-12} {wall,-5} {room.Type} {(room.Shape != null ? "shaped" : "rect")} wall{far}m";
    }

    /// <summary>A door's place on the facade in the plan's frame.</summary>
    private static Vector2 InPlan(InteriorLayout l, DoorSpot d)
    {
        var u = new Vector2(MathF.Cos(l.Yaw), -MathF.Sin(l.Yaw));
        var v = new Vector2(-u.Y, u.X);
        var rel = new Vector2(d.Position.X - l.CenterX, d.Position.Z - l.CenterZ);
        return new Vector2(rel.Dot(u), rel.Dot(v));
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-9f ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return p.DistanceTo(a + ab * t);
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
        InteriorGenerator.PlanWings = !CmdArgs.Has("--no-wings");
        // --svgkeys E_N_I,...: those buildings' plans drawn to test_output/survey-plans/, outline and locked doors in
        var svgKeys = (CmdArgs.Value("--svgkeys") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var winged = new List<string>();
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
                        InteriorGenerator.WingFailure = "one wing";
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
                        var entrances = l.AllEntrances().Where(e => e.Level == 0).ToList();
                        if (InteriorGenerator.Winged) winged.Add($"{fp.Key} {b.Kind}");
                        // the first dozen planned wing by wing, drawn both ways: as planned, and as they were before #1304
                        if (InteriorGenerator.Winged && (winged.Count <= 12 || svgKeys.Contains(fp.Key.ToString())))
                        {
                            svgKeys.Add(fp.Key.ToString());
                            InteriorGenerator.PlanWings = false;
                            var old = InteriorGenerator.Generate(tile, index, roads, null);
                            InteriorGenerator.PlanWings = true;
                            if (old != null)
                            {
                                System.IO.Directory.CreateDirectory("test_output/survey-plans");
                                var og = PlanOutline.FineGrid(b, fp.Center, fp.AxisU, fp.Width, fp.Depth);
                                var oe = old.AllEntrances().Where(e => e.Level == 0).ToList();
                                var oshut = fp.Doors.Where(d => d.Level == 0 && d.Width > 0 && !oe.Any(e => e.Door == new DoorKey(fp.Key, d.Slot).ToString()))
                                    .Select(d => InPlan(old, d)).ToList();
                                System.IO.File.WriteAllText($"test_output/survey-plans/{fp.Key}_before.svg",
                                    InteriorValidator.ToSvg(old, og == null ? null : InteriorGenerator.RingsFor(old, og), oshut));
                            }
                        }
                        if (svgKeys.Contains(fp.Key.ToString()))
                        {
                            System.IO.Directory.CreateDirectory("test_output/survey-plans");
                            var g = PlanOutline.FineGrid(b, fp.Center, fp.AxisU, fp.Width, fp.Depth);
                            var shut = fp.Doors.Where(d => d.Level == 0 && d.Width > 0 && !entrances.Any(e => e.Door == new DoorKey(fp.Key, d.Slot).ToString()))
                                .Select(d => InPlan(l, d)).ToList();
                            System.IO.File.WriteAllText($"test_output/survey-plans/{fp.Key}{(InteriorGenerator.PlanWings ? "" : "_before")}.svg",
                                InteriorValidator.ToSvg(l, g == null ? null : InteriorGenerator.RingsFor(l, g), shut));
                        }
                        if (InteriorGenerator.WingFailure != "one wing" && InteriorGenerator.WingFailure?.StartsWith("not for it") != true && !(InteriorGenerator.WingFailure == null && l.Type is BuildingType.Apartments or BuildingType.MixedUse))
                        {
                            string ww = InteriorGenerator.WingFailure == null ? "planned (taken, or not better)"
                                : System.Text.RegularExpressions.Regex.Replace(InteriorGenerator.WingFailure, @"[-\d.,]+|\(.*?\)", "#");
                            string wk = $"{b.Kind,-12} {(ww.Length > 80 ? ww[..80] : ww)}";
                            WingWhy[wk] = WingWhy.GetValueOrDefault(wk) + 1;
                        }
                        // --key E_N_I: that building's doors and doorways in the plan's frame, to see why one locks
                        if (CmdArgs.Value("--key") == fp.Key.ToString())
                        {
                            GD.Print($"[outlinesurvey] {fp.Key} {b.Kind} {l.Type} {l.Width:F1}x{l.Depth:F1} m, entry x {l.EntryX:F1}, wings: {InteriorGenerator.WingFailure ?? "planned"}");
                            // the roof on a 2 m grid, the back at the top: # roof, . open, D a street door
                            if (PlanOutline.Grid(b, fp.Center, fp.AxisU, fp.Width, fp.Depth, 2f) is { } map)
                                for (int j = map.GetLength(1) - 1; j >= 0; j--)
                                {
                                    var mapRow = new StringBuilder("[outlinesurvey]   ");
                                    for (int i = 0; i < map.GetLength(0); i++)
                                    {
                                        bool isDoor = fp.Doors.Any(d =>
                                        {
                                            var q = new Vector2(d.Position.X - fp.Center.X, d.Position.Z - fp.Center.Y);
                                            return (int)((q.Dot(fp.AxisU) + fp.Width / 2) / 2f) == i && (int)((q.Dot(fp.AxisV) + fp.Depth / 2) / 2f) == j;
                                        });
                                        mapRow.Append(isDoor ? 'D' : map[i, j] ? '#' : '.');
                                    }
                                    GD.Print(mapRow.ToString());
                                }
                            foreach (var wr in PlanOutline.Wings(b, fp.Center, fp.AxisU, fp.Width, fp.Depth) ?? [])
                                GD.Print($"[outlinesurvey]   wing x {wr.X0:F1}..{wr.X1:F1} z {wr.Z0:F1}..{wr.Z1:F1}");
                            foreach (var rm in l.GroundFloor.Rooms)
                                GD.Print($"[outlinesurvey]   room {rm.Type} x {rm.X0:F1}..{rm.X1:F1} z {rm.Z0:F1}..{rm.Z1:F1}{(rm.Shape != null ? " shaped" : "")}");
                            foreach (var wr in PlanOutline.Pieces(b, fp.Center, fp.AxisU, fp.Width, fp.Depth) ?? [])
                                GD.Print($"[outlinesurvey]   piece x {wr.X0:F1}..{wr.X1:F1} z {wr.Z0:F1}..{wr.Z1:F1}");
                            foreach (var d in fp.Doors.Where(d => d.Level == 0))
                            {
                                var at = InPlan(l, d);
                                var o = new Vector2(d.Outward.X, d.Outward.Z);
                                var u = new Vector2(MathF.Cos(l.Yaw), -MathF.Sin(l.Yaw));
                                var ol = new Vector2(o.Dot(u), o.Dot(new Vector2(-u.Y, u.X)));
                                string door = new DoorKey(fp.Key, d.Slot).ToString();
                                var e = entrances.FirstOrDefault(e => e.Door == door);
                                GD.Print($"[outlinesurvey]   slot {d.Slot}: at ({at.X:F1}, {at.Y:F1}) out ({ol.X:F2}, {ol.Y:F2}) w {d.Width:F1}{(d.Vehicle ? " vehicle" : "")}"
                                    + (e == null ? " LOCKED" : $" -> doorway ({e.X:F1}, {e.Z:F1})"));
                            }
                            foreach (var rm in l.GroundFloor.Rooms.Where(q => q.Type is RoomType.Lobby or RoomType.Landing or RoomType.Stairwell))
                                GD.Print($"[outlinesurvey]   {rm.Type} x {rm.X0:F1}..{rm.X1:F1} z {rm.Z0:F1}..{rm.Z1:F1}");
                        }
                        int doorCount = fp.Doors.Count(d => d.Level == 0);
                        foreach (var d in fp.Doors.Where(d => d.Level == 0 && d.Width > 0))
                        {
                            string door = new DoorKey(fp.Key, d.Slot).ToString();
                            if (entrances.Any(e => e.Door == door)) continue;
                            string k = $"{b.Kind,-12} " + LockedKey(l, d);
                            LockedBy[k] = LockedBy.GetValueOrDefault(k) + 1;
                            if (LockedBy[k] <= 2)
                            {
                                // the plan with its outline, red circles at its doors: test_output/survey-locked/
                                System.IO.Directory.CreateDirectory("test_output/survey-locked");
                                var g = PlanOutline.FineGrid(b, fp.Center, fp.AxisU, fp.Width, fp.Depth);
                                string name = System.Text.RegularExpressions.Regex.Replace(k.Trim(), @"[^A-Za-z0-9<>]+", "_").Replace("<", "lt").Replace(">", "gt");
                                System.IO.File.WriteAllText($"test_output/survey-locked/{name}__{fp.Key}_{d.Slot}.svg",
                                    InteriorValidator.ToSvg(l, g == null ? null : InteriorGenerator.RingsFor(l, g), [InPlan(l, d)]));
                                GD.Print($"[outlinesurvey]   locked: {fp.Key} slot {d.Slot}: {k}; why the plan kept rectangles: {InteriorGenerator.OutlineFailure ?? "-"}");
                            }
                        }
                        int off = 0, turned = 0;
                        foreach (var e in entrances)
                        {
                            var (gap, turn) = InteriorGenerator.DoorGap(l, e);
                            if (gap > 1) off++;
                            if (turn > 10) turned++;
                        }
                        foreach (var x in new[] { row, total })
                        {
                            x.Doors += doorCount;
                            x.Locked += Math.Max(0, doorCount - entrances.Count);
                            x.Off += off;
                            x.Turned += turned;
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
        sb.AppendLine("kind           plans  boxlike  shaped  fallback  invalid   fill  outside   ms avg  ms max   doors  locked  off>1m  turned");
        void Line(string name, Row x) => sb.AppendLine(
            $"{name,-14}{x.Plans,6}{x.Boxlike,9}{x.Shaped,8}{x.Fallback,10}{x.Invalid,9}{(x.Area > 0 ? x.Used / x.Area : 0),7:P0}{(x.Area > 0 ? x.Outside / x.Area : 0),9:P0}{(x.Plans > 0 ? x.Ms / x.Plans : 0),9:F1}{x.MaxMs,8:F0}"
            + $"{x.Doors,8}{x.Locked,8}{x.Off,8}{x.Turned,8}");
        foreach (var (name, x) in rows) Line(name, x);
        Line("all", total);
        sb.AppendLine("fill and outside: of the outline's area, over the plans whose outline is not their box");
        sb.AppendLine("doors: their ground-floor street doors; locked: no doorway inside; off>1m: the doorway inside more than 1 m from the real door; turned: more than 10° from it");
        sb.AppendLine();
        sb.AppendLine("locked doors by kind, plan type, wall (front: the main door's) and the room behind (#1304):");
        foreach (var (k, n) in LockedBy.OrderByDescending(w => w.Value).Take(40))
            sb.AppendLine($"  {n,5}  {k}");
        sb.AppendLine();
        sb.AppendLine($"planned wing by wing (#1304): {winged.Count}: {string.Join(", ", winged.Take(60))}");
        sb.AppendLine("wing by wing (#1304), by kind: why not, or planned:");
        foreach (var (k, n) in WingWhy.OrderByDescending(w => w.Value).Take(30))
            sb.AppendLine($"  {n,5}  {k}");
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
