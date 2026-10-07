using Godot;
using System.Text;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --rampfirst[,outfile] --chunks D:/.../terrain_chunks</c> (#694): a survey
/// of the ramp-first garage rule on the real tiles. Over every block of flats (and shops under flats) it runs the real
/// footprint with every roll passing (<see cref="GarageRule.AlwaysRolls"/>): how many boxes take a square ramp or one along
/// the facade, how many of those the footprint finds a door for (a street in reach, a wall to stand on), and how many of the
/// doors the generator plans a ramp behind, validated (the rest read as locked: a bug). The share of all blocks at the
/// configured roll is then the qualifiers times <see cref="GarageRule.Share"/>. Writes the table to the outfile (default
/// <c>test_output/rampfirst.txt</c>) and the per-block rows beside it as <c>.csv</c>.
/// No ground is loaded (the stub's hump is not judged), no window, no server.
/// </summary>
public partial class RampFirstProbe : Node
{
    private readonly string _out;
    private string _svgDir = "";
    private readonly StringBuilder _dump = new();

    /// <summary>The rooms of the unit that holds the first bad room of a validator message, for the probe's log.</summary>
    private static string Dump(InteriorLayout plan, string message)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"floor (\d+) room (\d+)");
        if (!m.Success) return "";
        var fl = plan.Floors[int.Parse(m.Groups[1].Value)];
        int bad = int.Parse(m.Groups[2].Value);
        int unit = fl.Rooms[bad].Unit;
        return string.Join(" | ", fl.Rooms.Select((r, i) => (r, i)).Where(x => x.r.Unit == unit)
            .Select(x => $"{x.i}:{x.r.Type}[{x.r.X0:F1},{x.r.Z0:F1}..{x.r.X1:F1},{x.r.Z1:F1}]"));
    }
    private readonly int[] _svgs = new int[8];

    public RampFirstProbe(string? shot) => _out = string.IsNullOrEmpty(shot) ? "test_output/rampfirst.txt" : shot;

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--rampfirst");

    /// <summary>A block smaller than this (plan box area) is a house, not a development with a garage.</summary>
    private const float MinArea = 120f;

    private sealed class Row
    {
        public string Key = "", Type = "", Plan = "";
        public float W, D, H;
        public int Above, FrontDoors;
        public bool Lone, Wing, Road, TooSmall, Door, Locked, Valid, PlainBad, PlainChecked;
        public GarageRule.RampKind Kind;
        public TileId Tile;
        public int Index;
        public float DoorX;
        public int Bays;
        /// <summary>For a wing-planned block: how many wings, and the largest one's sides (plan frame X, Z), m; and whether either way round it takes a ramp.</summary>
        public int WingCount;
        public float WingW, WingD;
        public GarageRule.RampKind WingKind;
    }

    public override void _Ready()
    {
        string dir = CmdArgs.Value(CmdArgs.All, "--chunks") ?? "terrain_chunks";
        var src = new LocalChunkSource(dir);
        var ids = new List<TileId>();
        foreach (var f in Directory.GetFiles(dir, "buildings_*.bldg"))
        {
            var parts = Path.GetFileNameWithoutExtension(f).Split('_');
            if (parts.Length == 3 && int.TryParse(parts[1], out int e) && int.TryParse(parts[2], out int n)) ids.Add(new TileId(e, n));
        }
        GD.Print($"[rampfirst] {ids.Count} building tiles in {dir}");
        GarageRule.AlwaysRolls = true;
        _svgDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_out))!, "rampfirst");
        Directory.CreateDirectory(_svgDir);
        var rows = new List<Row>();
        int buildings = 0, tilesDone = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 4 }, id =>
        {
            var tile = src.LoadBuildingsAsync(id).GetAwaiter().GetResult();
            if (tile == null) return;
            var roads = src.LoadRoadsAsync(id).GetAwaiter().GetResult();
            var streets = BuildingFootprint.StreetRoads(roads);
            var map = BuildingTypes.For(tile);
            var mine = new List<Row>();
            for (int i = 0; i < tile.Buildings.Count; i++)
            {
                try
                {
                    var fp = BuildingFootprint.Compute(tile, i, roads, null);
                    if (fp == null || map.Boxes[i] is not { } box || fp.Door.Width <= 0) continue;
                    var b = tile.Buildings[i];
                    var (storeyH, above) = InteriorGenerator.Storeys(b);
                    string key = fp.Key.ToString();
                    var type = GarageRule.BlockType(key, box.Width * box.Depth, fp.Kind, above, false);
                    if (BuildingFootprint.IsBank(key, fp.Kind, Mathf.Clamp(box.Width, BuildingFootprint.MinSide, BuildingFootprint.MaxSide),
                        Mathf.Clamp(box.Depth, BuildingFootprint.MinSide, BuildingFootprint.MaxSide))) type = BuildingType.None;
                    if (type is not (BuildingType.Apartments or BuildingType.MixedUse)) continue;
                    var r = new Row { Key = key, Type = type.ToString(), W = fp.Width, D = fp.Depth, H = storeyH, Above = above, Tile = id, Index = i };
                    r.FrontDoors = Math.Min(DoorBudget.AlongRun(fp.Width, DoorBudget.ServiceWidth, DoorBudget.MaxPerWall).Length, DoorBudget.Total(fp.Kind, box.Width, box.Depth) - 1);
                    var outward = new Vector2(fp.Door.Outward.X, fp.Door.Outward.Z);
                    r.Wing = !BuildingFootprint.PlannedAsBox(b, box.Center, box.AxisU, outward, box.Width, box.Depth);
                    r.TooSmall = fp.Width * fp.Depth < MinArea;
                    if (r.Wing && PlanOutline.Wings(b, fp.Center, fp.AxisU, Mathf.Clamp(fp.Width, BuildingFootprint.MinSide, BuildingFootprint.MaxSide), Mathf.Clamp(fp.Depth, BuildingFootprint.MinSide, BuildingFootprint.MaxSide)) is { Count: >= 1 } wl)
                    {
                        r.WingCount = wl.Count;
                        var big = wl.OrderByDescending(x => (x.X1 - x.X0) * (x.Z1 - x.Z0)).First();
                        r.WingW = big.X1 - big.X0; r.WingD = big.Z1 - big.Z0;
                        var k1 = GarageRule.KindOf(above, r.WingW, r.WingD, storeyH); var k2 = GarageRule.KindOf(above, r.WingD, r.WingW, storeyH);
                        r.WingKind = k1 != GarageRule.RampKind.None ? k1 : k2;
                    }
                    var grect = BuildingFootprint.GarageRect(b, box.Center, box.AxisU, outward, box.Width, box.Depth, new Vector2(fp.Door.Position.X, fp.Door.Position.Z));
                    r.Lone = r.Wing && grect != null;
                    r.Kind = grect is { } gr ? GarageRule.KindOf(above, gr.X1 - gr.X0, gr.Z1 - gr.Z0, storeyH) : GarageRule.RampKind.None;
                    var back = new Vector2(-fp.AxisU.Y, fp.AxisU.X);
                    var wallMid = fp.Center - back * (fp.Depth / 2);
                    var normal = -back;
                    for (float s = -fp.Width / 2 + 2.4f; s <= fp.Width / 2 - 2.4f && !r.Road; s += 1.5f)
                        if (GarageLink.Choose(wallMid + fp.AxisU * s + normal * 0.03f, normal, streets).Any) r.Road = true;
                    var gd = fp.Extra.FirstOrDefault(d => d.Vehicle && d.Link.Any);
                    r.Door = fp.Extra.Any(d => d.Vehicle && d.Link.Any);
                    if (!r.Door && r.Kind != GarageRule.RampKind.None && !r.Wing) r.Plan = "no door: " + (BuildingFootprint.GarageWhyNot ?? "not attempted");
                    if (!r.Door && !r.Wing && CmdArgs.Has("--rampfirst-all") && i % 5 == 0)
                    {
                        // a block with no garage, for the baseline: how many of every plan fail the validator (kitchens 1 m wide and the like)
                        bool saved = GarageRule.AlwaysRolls; GarageRule.AlwaysRolls = false;
                        if (InteriorGenerator.Generate(tile, i, roads, null) is { } plain && InteriorValidator.Validate(plain) is { Count: > 0 } bad)
                        { r.Plan = "plain invalid: " + bad[0]; r.PlainBad = true; }
                        GarageRule.AlwaysRolls = saved;
                        r.PlainChecked = true;
                    }
                    if (r.Door)
                    {
                        r.DoorX = new Vector2(gd.Position.X - fp.Center.X, gd.Position.Z - fp.Center.Y).Dot(fp.AxisU);
                        var plan = InteriorGenerator.Generate(tile, i, roads, null);
                        if (plan == null) { r.Plan = "no plan"; r.Locked = true; }
                        else
                        {
                            bool ramp = plan.Entrances.Any(en => en.Vehicle) && plan.Floors.Any(f => f.AllFlights().Any(x => x.Ramp));
                            r.Locked = !ramp;
                            r.Bays = plan.Furniture.Count(p => p.Type == FurnitureType.FloorMarking && plan.RoomOf(p)?.Type == RoomType.CarPark);
                            if (!ramp) r.Plan = (InteriorGenerator.RampWhy ?? "locked") + " / wings: " + (InteriorGenerator.WingFailure ?? "ok");
                            var problems = InteriorValidator.Validate(plan);
                            r.Valid = problems.Count == 0;
                            if (!r.Valid)
                            {
                                r.Plan = "invalid: " + problems[0];
                                lock (_dump) _dump.AppendLine($"{r.Key} {problems[0]} (plan {plan.Width:F1} x {plan.Depth:F1}); " + Dump(plan, problems[0]));
                            }
                            if (Interlocked.Increment(ref _svgs[(int)r.Kind * 2 + (r.Valid ? 0 : 1)]) <= 8)
                                File.WriteAllText(Path.Combine(_svgDir, $"{r.Kind}_{(r.Valid ? "ok" : "bad")}_{r.Key}.svg"), InteriorValidator.ToSvg(plan));
                        }
                    }
                    mine.Add(r);
                }
                catch (Exception e) { GD.Print($"[rampfirst] {id} #{i}: {e.GetType().Name} {e.Message}"); }
            }
            lock (rows)
            {
                rows.AddRange(mine); buildings += tile.Buildings.Count; tilesDone++;
                if (tilesDone % 100 == 0) GD.Print($"[rampfirst] {tilesDone}/{ids.Count} tiles, {rows.Count} blocks, {sw.Elapsed.TotalSeconds:F0} s");
            }
        });
        GarageRule.AlwaysRolls = false;
        Report(rows, ids.Count, buildings);
        GD.Print("[rampfirst] RESULT: ok");
        GetTree().Quit(0);
    }

    private static string Pct(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:F1} %";

    private void Report(List<Row> rows, int tiles, int buildings)
    {
        var sb = new StringBuilder();
        void P(string s) { sb.AppendLine(s); GD.Print("[rampfirst] " + s); }
        int N = rows.Count;
        var whole = rows.Where(r => !r.Wing || r.Lone).ToList();
        var gated = whole.Where(r => r.Road && !r.TooSmall).ToList();
        var doors = rows.Where(r => r.Door).ToList();
        P($"Real tiles: {tiles} building tiles, {buildings} buildings.");
        P($"Blocks of flats or shops under flats (not banks): {N}  (flats {rows.Count(r => r.Type == "Apartments")}, mixed {rows.Count(r => r.Type == "MixedUse")})");
        P($"Gate: wing-planned {rows.Count(r => r.Wing)} ({Pct(rows.Count(r => r.Wing), N)}; of them one wing planned alone {rows.Count(r => r.Lone)}), whole box or lone wing without a street {whole.Count(r => !r.Road)}, under {MinArea:F0} m2 {whole.Count(r => r.Road && r.TooSmall)}, pass {gated.Count} ({Pct(gated.Count, N)})");
        P($"Front doors today, whole boxes: 1 door {Pct(whole.Count(r => r.FrontDoors <= 1), whole.Count)}, 2+ {Pct(whole.Count(r => r.FrontDoors >= 2), whole.Count)}");
        P("");
        P("The box takes (gate-passing, any roll):");
        foreach (var k in new[] { GarageRule.RampKind.Square, GarageRule.RampKind.Along, GarageRule.RampKind.None })
            P($"  {k,-8}{gated.Count(r => r.Kind == k),6} = {Pct(gated.Count(r => r.Kind == k), gated.Count)} of gate-passing, {Pct(gated.Count(r => r.Kind == k), N)} of all");
        P("");
        var wingRows = rows.Where(r => r.Wing).ToList();
        P($"Wing-planned blocks: {wingRows.Count}; by wings: " + string.Join(", ", wingRows.GroupBy(r => Math.Min(r.WingCount, 4)).OrderBy(g => g.Key).Select(g => $"{(g.Key >= 4 ? "4+" : g.Key.ToString())} wing(s) {g.Count()}")));
        var wingOk = wingRows.Where(r => r.WingKind != GarageRule.RampKind.None).ToList();
        P($"  whose largest wing would take a ramp (either way round, any road, any roll): {wingOk.Count} = {Pct(wingOk.Count, N)} of all blocks (Square {wingOk.Count(r => r.WingKind == GarageRule.RampKind.Square)}, Along {wingOk.Count(r => r.WingKind == GarageRule.RampKind.Along)}); single wing {wingOk.Count(r => r.WingCount == 1)}, several {wingOk.Count(r => r.WingCount > 1)}");
        P($"Doors the footprint places with every roll passing: {doors.Count} = {Pct(doors.Count, N)} of all blocks");
        foreach (var k in new[] { GarageRule.RampKind.Square, GarageRule.RampKind.Along })
            P($"  {k,-8}{doors.Count(r => r.Kind == k),6}  (the box takes it: {gated.Count(r => r.Kind == k)}; the rest: no wall for the door, no link)");
        foreach (var g in gated.Where(r => r.Kind != GarageRule.RampKind.None && !r.Door).GroupBy(r => r.Kind + " " + (r.Plan.Length > 100 ? r.Plan[..100] : r.Plan)).OrderByDescending(g => g.Count()).Take(40))
            P($"    no door {g.Count(),5}  {g.Key}");
        P($"  ramp planned and valid {doors.Count(r => !r.Locked && r.Valid)}, locked {doors.Count(r => r.Locked)}, invalid {doors.Count(r => !r.Locked && !r.Valid)}");
        foreach (var g in doors.Where(r => r.Plan.Length > 0).GroupBy(r => r.Plan.Length > 70 ? r.Plan[..70] : r.Plan).OrderByDescending(g => g.Count()).Take(12))
            P($"    {g.Count(),5}  {g.Key}");
        P("");
        double exp = doors.Count(r => r.Type == "Apartments") * GarageRule.Share + doors.Count(r => r.Type == "MixedUse") * GarageRule.MixedRollShare;
        foreach (var k in new[] { GarageRule.RampKind.Square, GarageRule.RampKind.Along })
        {
            var bays = doors.Where(r => r.Kind == k && !r.Locked).Select(r => r.Bays).OrderBy(x => x).ToList();
            if (bays.Count > 0) P($"  {k} car park bays: min {bays[0]}, median {bays[bays.Count / 2]}, max {bays[^1]}, under 4: {bays.Count(x => x < 4)}");
        }
        if (rows.Any(r => r.PlainChecked)) P($"Plans without a garage, validator: {rows.Count(r => r.PlainBad)} invalid of {rows.Count(r => r.PlainChecked)} checked (every fifth block)");
        P($"Roll share {GarageRule.Share:F2} (mixed {GarageRule.MixedRollShare:F2}): expected garage doors {exp:F0} = {100.0 * exp / N:F2} % of all blocks");
        P($"Roll share that gives 4 % of all blocks: {0.04 * N / Math.Max(1, doors.Count):F2}");
        P("Why gate-passing blocks take no ramp: "
            + $"too shallow for the square one {gated.Count(r => r.Kind == GarageRule.RampKind.None && r.D < GarageRule.RampDepth(r.H))}, narrower than the along ramp needs "
            + $"{gated.Count(r => r.Kind == GarageRule.RampKind.None && r.W < GarageRule.AlongWidth(r.H, GarageRule.LiftIn(r.Above)))}");
        P($"Median gate-passing box (W x D): {Median(gated.Select(r => r.W)):F1} x {Median(gated.Select(r => r.D)):F1} m");
        P($"Doors by the front doors the block has today: 1 door {doors.Count(r => r.FrontDoors <= 1)}, 2+ {doors.Count(r => r.FrontDoors >= 2)}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_out))!);
        File.WriteAllText(_out, sb.ToString());
        if (_dump.Length > 0) File.WriteAllText(Path.ChangeExtension(_out, ".invalid.txt"), _dump.ToString());
        var csv = new StringBuilder("key,tile,index,type,w,d,storeyH,above,frontDoors,wing,road,tooSmall,kind,door,doorX,locked,valid,plan\n");
        foreach (var r in rows)
            csv.AppendLine(string.Join(",", r.Key, r.Tile, r.Index, r.Type, F(r.W), F(r.D), F(r.H), r.Above, r.FrontDoors, r.Wing ? 1 : 0,
                r.Road ? 1 : 0, r.TooSmall ? 1 : 0, r.Kind, r.Door ? 1 : 0, F(r.DoorX), r.Locked ? 1 : 0, r.Valid ? 1 : 0, r.Plan.Replace(',', ';')));
        File.WriteAllText(Path.ChangeExtension(_out, ".csv"), csv.ToString());
        GD.Print($"[rampfirst] wrote {_out} and its .csv");
    }

    private static string F(float v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static float Median(IEnumerable<float> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l.Count == 0 ? 0 : l[l.Count / 2];
    }
}
