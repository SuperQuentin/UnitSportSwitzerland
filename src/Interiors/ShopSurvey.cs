using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --shopsurvey[,out.txt] --chunks D:/.../terrain_chunks [--tiles N]</c> (#1292):
/// the shop units and dining venues of real towns, before each gets its own street door. On the tiles round Geneva
/// and Lausanne it plans every block of flats with shops under it and every building holding a dining venue as the
/// game does, and counts: a mixed block's street front, its stairwells and front doors, its shop units and how wide
/// each is along the street; a venue's building (kind and plan), whether it was drawn, its guest room's width along
/// the facade and its area. Writes the table to the outfile (default <c>test_output/shopsurvey.txt</c>). No window, no world.
/// </summary>
public static class ShopSurvey
{
    public static bool Requested => Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith("--shopsurvey", StringComparison.Ordinal));

    private static readonly (string Name, double E, double N)[] Towns =
    [
        ("Geneva", 2500300, 1118450), ("Lausanne", 2538000, 1152500),
    ];

    private static int MaxTiles => int.TryParse(CmdArgs.Value("--tiles"), out int n) ? n : int.MaxValue;

    public static int Run()
    {
        var args = OS.GetCmdlineUserArgs();
        string arg = args.First(a => a.StartsWith("--shopsurvey", StringComparison.Ordinal));
        string outPath = arg.Contains(',') ? arg[(arg.IndexOf(',') + 1)..] : "test_output/shopsurvey.txt";
        var src = new LocalChunkSource(CmdArgs.Value("--chunks") ?? "terrain_chunks");
        var sb = new StringBuilder();
        var fronts = new List<float>();
        var shopW = new List<float>();
        var perFront = new List<(float Run, int Shops, int Wells, int FrontDoors)>();
        var venueRows = new Dictionary<string, (int Venues, int Drawn, List<float> Widths, List<float> Areas)>();
        int mixed = 0, tiles = 0, multi = 0;
        // #1292: every door of its own, whether it opens into its unit, and how its room's street wall compares with its front
        int ownDoors = 0, ownVenue = 0, ownLocked = 0, ownWrongRoom = 0, glazed = 0, narrower = 0;
        var shortBy = new List<float>();
        var lockedWhy = new Dictionary<string, int>();
        var doorsMs = new List<double>();
        var unitsMs = new List<double>();
        double planMs = 0;
        int plans = 0;
        foreach (var town in Towns)
        {
            var c = TileId.FromLv95(town.E, town.N);
            int townTiles = 0;
            for (int de = -1; de <= 1; de++)
                for (int dn = -1; dn <= 1; dn++)
                {
                    var id = new TileId(c.E + de, c.N + dn);
                    var tile = src.LoadBuildingsAsync(id).GetAwaiter().GetResult();
                    if (tile == null || ++townTiles > MaxTiles) continue;
                    tiles++;
                    var roads = src.LoadRoadsAsync(id).GetAwaiter().GetResult();
                    var map = BuildingTypes.For(tile);
                    long units0 = BuildingFootprint.StreetUnitsTicks, doors0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    BuildingFootprint.ComputeDoors(tile, roads, null);
                    doorsMs.Add((System.Diagnostics.Stopwatch.GetTimestamp() - doors0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    unitsMs.Add((BuildingFootprint.StreetUnitsTicks - units0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    for (int index = 0; index < tile.Buildings.Count; index++)
                    {
                        var b = tile.Buildings[index];
                        var dining = map.DiningOf(index);
                        // a mixed block is the key's roll: only plan the ones that may be one, or hold a venue
                        if (dining.Count == 0 && b.Kind != BuildingKind.Commercial) continue;
                        if (BuildingFootprint.Compute(tile, index, roads, null) is not { } fp || fp.Door.Width <= 0) continue;
                        InteriorLayout l;
                        InteriorGenerator.OutlineFailure = null;
                        InteriorGenerator.WingFailure = null;
                        InteriorGenerator.VestibuleWhy.Clear();
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        try { l = InteriorGenerator.Generate(tile, index, roads, null); }
                        catch (Exception ex) { GD.PrintErr($"[shopsurvey] {id} #{index}: {ex.Message}"); continue; }
                        planMs += watch.Elapsed.TotalMilliseconds;
                        plans++;
                        var ground = l.GroundFloor;
                        foreach (var own in fp.Doors.Where(x => x.Unit))
                        {
                            ownDoors++;
                            if (own.Venue > 0) ownVenue++;
                            var e = l.EntranceOf(new DoorKey(fp.Key, own.Slot).ToString());
                            int? ri = e == null ? null : InteriorGenerator.EntryRoom(ground.Rooms, e);
                            if (ri is not int k)
                            {
                                ownLocked++;
                                string why = $"{l.Type}{(e == null ? "" : " entrance but no room")} wings: {InteriorGenerator.WingFailure ?? "planned"} vestibules: {(InteriorGenerator.VestibuleWhy.Count == 0 ? "none tried" : string.Join("; ", InteriorGenerator.VestibuleWhy.Keys))}{(InteriorGenerator.OutlineFailure is { } f ? " outline: " + f : "")}";
                                lockedWhy[why[..Math.Min(170, why.Length)]] = lockedWhy.GetValueOrDefault(why[..Math.Min(170, why.Length)]) + 1;
                                if (lockedWhy.Values.Sum() <= 12) sb.AppendLine($"  locked: {id} #{index} slot {own.Slot} {(own.Venue > 0 ? "venue" : "shop")} {why}");
                                continue;
                            }
                            var room = ground.Rooms[k];
                            bool right = own.Venue > 0 ? InteriorGenerator.DiningRoom(room.Type) : room.Type == RoomType.Shop;
                            if (!right) ownWrongRoom++;
                            if (!VenueFront.Has(own)) continue;
                            glazed++;
                            var entry = room.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Entry && o.Edge == null);
                            float wall = entry == null ? 0 : entry.Side is Side.Front or Side.Back ? room.X1 - room.X0 : room.Z1 - room.Z0;
                            if (entry != null && wall < own.Span - 1f) { narrower++; shortBy.Add(own.Span - wall); }
                        }
                        float hd = l.Depth / 2;
                        if (l.Type == BuildingType.MixedUse)
                        {
                            mixed++;
                            fronts.Add(fp.Door.Run);
                            var shops = ground.Rooms.Where(r => r.Type == RoomType.Shop && r.Z0 <= -hd + 0.05f).ToList();
                            foreach (var s in shops) shopW.Add(s.X1 - s.X0);
                            int wells = ground.Rooms.Count(r => r.Type == RoomType.Lobby);
                            int frontDoors = fp.Doors.Count(d => !d.Vehicle && new Vector2(d.Outward.X, d.Outward.Z).Dot(fp.AxisV) < -0.8f);
                            perFront.Add((fp.Door.Run, shops.Count, wells, frontDoors));
                            sb.AppendLine($"  mixed {id} #{index}: plan {l.Width:F1} x {l.Depth:F1}, front run {fp.Door.Run:F1}, {wells} stairwell(s), {frontDoors} front door(s), shops [{string.Join(", ", shops.OrderBy(s => s.X0).Select(s => $"{s.X1 - s.X0:F1}"))}]");
                        }
                        if (dining.Count == 0) continue;
                        if (dining.Count > 1) multi++;
                        string key = $"{b.Kind}/{(l.Type == BuildingType.None ? "plain" : l.Type.ToString())}";
                        var row = venueRows.TryGetValue(key, out var x) ? x : (Venues: 0, Drawn: 0, Widths: new List<float>(), Areas: new List<float>());
                        var guests = ground.Rooms.Where(r => InteriorGenerator.DiningRoom(r.Type)).ToList();
                        row.Venues += dining.Count;
                        row.Drawn += Math.Min(dining.Count, guests.Count);
                        foreach (var g in guests)
                        {
                            // its widest wall on the plan box's edge: the facade it could have glass on
                            float w = 0;
                            if (g.Z0 <= -hd + 0.05f || g.Z1 >= hd - 0.05f) w = Math.Max(w, g.X1 - g.X0);
                            if (g.X0 <= -l.Width / 2 + 0.05f || g.X1 >= l.Width / 2 - 0.05f) w = Math.Max(w, g.Z1 - g.Z0);
                            row.Widths.Add(w);
                            row.Areas.Add(g.Area);
                        }
                        venueRows[key] = row;
                    }
                    GD.Print($"[shopsurvey] {town.Name} {id}: done");
                }
        }

        string Hist(List<float> xs, float step, float top)
        {
            var s = new StringBuilder();
            for (float lo = 0; lo < top; lo += step)
            {
                int n = xs.Count(v => v >= lo && (v < lo + step || lo + step >= top));
                s.Append($"    {lo,4:F0}{(lo + step >= top ? "+   " : $"-{lo + step,-3:F0}")} {n,5}  {new string('#', Math.Min(60, n))}\n");
            }
            return s.ToString();
        }
        static string Q(List<float> xs) => xs.Count == 0 ? "-" : $"median {xs.OrderBy(v => v).ElementAt(xs.Count / 2):F1}, p10 {xs.OrderBy(v => v).ElementAt(xs.Count / 10):F1}, p90 {xs.OrderBy(v => v).ElementAt(xs.Count * 9 / 10):F1}";

        var head = new StringBuilder();
        head.AppendLine($"shop survey, {tiles} tiles round {string.Join(", ", Towns.Select(t => t.Name))}");
        head.AppendLine($"mixed blocks: {mixed}; front run {Q(fronts)}");
        head.AppendLine($"  shop units on the street: {shopW.Count} ({(mixed > 0 ? shopW.Count / (float)mixed : 0):F1} a block), width {Q(shopW)}");
        head.Append(Hist(shopW, 2f, 20f));
        head.AppendLine($"  street front per shop unit: {Q(perFront.Where(p => p.Shops > 0).Select(p => p.Run / p.Shops).ToList())}");
        head.AppendLine($"  blocks with no shop on the street: {perFront.Count(p => p.Shops == 0)}");
        head.AppendLine($"doors of their own (#1292): {ownDoors} ({ownVenue} a venue's); no doorway inside {ownLocked}; into a room not theirs {ownWrongRoom}");
        foreach (var (w, n) in lockedWhy.OrderByDescending(x => x.Value).Take(8)) head.AppendLine($"    locked {n,5}: {w}");
        head.AppendLine($"  glazed: {glazed}; the room's street wall more than 1 m narrower than its front: {narrower}, short by {Q(shortBy)}");
        head.AppendLine($"  {(plans > 0 ? planMs / plans : 0):F0} ms a plan over {plans} plans");
        head.AppendLine($"  a tile's doors: {(doorsMs.Count > 0 ? doorsMs.Average() : 0):F0} ms on average, worst {(doorsMs.Count > 0 ? doorsMs.Max() : 0):F0} ms; of which the blocks' units {(unitsMs.Count > 0 ? unitsMs.Average() : 0):F0} ms, worst {(unitsMs.Count > 0 ? unitsMs.Max() : 0):F0} ms");
        head.AppendLine($"dining venues: {venueRows.Values.Sum(r => r.Venues)} ({multi} buildings hold several)");
        head.AppendLine("  building (kind/plan)          venues  drawn  facade width of the guest room          area");
        foreach (var (k, r) in venueRows.OrderByDescending(v => v.Value.Venues))
            head.AppendLine($"  {k,-30}{r.Venues,6}{r.Drawn,7}  {Q(r.Widths),-40}{Q(r.Areas)}");
        var all = venueRows.Values.SelectMany(r => r.Widths).ToList();
        head.AppendLine("  guest room facade width, all:");
        head.Append(Hist(all, 2f, 20f));
        head.AppendLine();
        head.Append(sb);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!);
        System.IO.File.WriteAllText(outPath, head.ToString());
        GD.Print(head.ToString().Split('\n').Take(60).Aggregate((a, b2) => a + "\n" + b2));
        GD.Print($"[shopsurvey] wrote {outPath}");
        GD.Print($"[shopsurvey] RESULT: {(tiles > 0 ? "ok" : "FAILED (no tiles under --chunks?)")}");
        return tiles > 0 ? 0 : 1;
    }
}
