using Godot;
using System.Text;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --rampfirst[,outfile] --chunks D:/.../terrain_chunks</c> (#694): a survey,
/// not a check. Over every real building tile it asks of each block of flats (and shops under flats) with
/// the gating the garage has today (a whole-box plan, not a bank, a street in reach through
/// <see cref="GarageLink"/>), what share a RAMP-FIRST rule would qualify: a ramp square to the front wall
/// (today's <see cref="RampProfile"/>, needs depth) or one running ALONG the facade (needs width), with a
/// single stairwell beside it and a car park of at least a few bays under the block. Why the rest fail,
/// and how many have one front door against two or more. Writes a table to the outfile (default
/// <c>test_output/rampfirst.txt</c>) and the per-block rows beside it as <c>.csv</c>.
/// No ground is loaded (the stub's hump is not judged), no window, no server.
/// </summary>
public partial class RampFirstProbe : Node
{
    private readonly string _out;

    public RampFirstProbe(string? shot) => _out = string.IsNullOrEmpty(shot) ? "test_output/rampfirst.txt" : shot;

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--rampfirst");

    // ---- the geometry a ramp-first block needs, m (the numbers of the issue, one place to change) ----
    /// <summary>Gross floor per bay in a car park: a 2.5 x 5 m bay plus its share of the aisle and the pillars.</summary>
    private const float BayGross = 25f;
    /// <summary>A block smaller than this (plan box area) is a house, not a development with a garage.</summary>
    private const float MinArea = 120f;
    /// <summary>Along the facade the lane turns 90 degrees off the door: its depth from the front wall.</summary>
    private const float AlongDepth = 5.0f;
    /// <summary>The stairwell's column beside the ramp: its width and the walk to it.</summary>
    private const float StairCol = GarageRule.StairWidth + GarageRule.WalkWidth;
    /// <summary>Wall left each end of the door's column.</summary>
    private const float Margin = 0.6f;

    private sealed class Row
    {
        public string Key = "", Type = "";
        public float W, D, H;
        public int Above, FrontDoors, Budget;
        public bool Today, Wing, Road, TooSmall, Rolls;
        // per orientation: pass flags with the stairwell, and the exclusive failure reason
        public string WhyA = "", WhyB = "", WhyAny = "";
        public bool A, B, ANoStair, BNoStair, A8, B8;
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
        var rows = new List<Row>();
        int garages = 0, buildings = 0, tilesDone = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 4 }, id =>
        {
            var tile = src.LoadBuildingsAsync(id).GetAwaiter().GetResult();
            if (tile == null) return;
            var roads = src.LoadRoadsAsync(id).GetAwaiter().GetResult();
            var streets = BuildingFootprint.StreetRoads(roads);
            var map = BuildingTypes.For(tile);
            var mine = new List<Row>();
            int g = 0;
            for (int i = 0; i < tile.Buildings.Count; i++)
            {
                try
                {
                    var fp = BuildingFootprint.Compute(tile, i, roads, null);
                    if (fp == null || map.Boxes[i] is not { } box) continue;
                    if (fp.Door.Width <= 0) continue;
                    var b = tile.Buildings[i];
                    var (storeyH, above) = InteriorGenerator.Storeys(b);
                    string key = fp.Key.ToString();
                    var type = GarageRule.BlockType(key, box.Width * box.Depth, fp.Kind, above, false);
                    if (BuildingFootprint.IsBank(key, fp.Kind, Mathf.Clamp(box.Width, BuildingFootprint.MinSide, BuildingFootprint.MaxSide),
                        Mathf.Clamp(box.Depth, BuildingFootprint.MinSide, BuildingFootprint.MaxSide))) type = BuildingType.None;
                    if (fp.Extra != null && fp.Extra.Any(d => d.Vehicle && d.Link.Any && fp.Kind != BuildingKind.Garage)) g++;
                    if (type is not (BuildingType.Apartments or BuildingType.MixedUse)) continue;
                    var r = new Row { Key = key, Type = type.ToString(), W = fp.Width, D = fp.Depth, H = storeyH, Above = above };
                    r.Today = fp.Extra != null && fp.Extra.Any(d => d.Vehicle && d.Link.Any);
                    r.Budget = DoorBudget.Total(fp.Kind, box.Width, box.Depth);
                    r.FrontDoors = Math.Min(DoorBudget.AlongRun(fp.Width, DoorBudget.ServiceWidth, DoorBudget.MaxPerWall).Length, r.Budget - 1);
                    r.Rolls = GarageRule.Rolls(key, type == BuildingType.MixedUse);
                    var outward = new Vector2(fp.Door.Outward.X, fp.Door.Outward.Z);
                    r.Wing = !BuildingFootprint.PlannedAsBox(b, box.Center, box.AxisU, outward, box.Width, box.Depth);
                    // a road in reach of a door somewhere along the front wall (plan frame: AxisV is the back)
                    var back = new Vector2(-fp.AxisU.Y, fp.AxisU.X);
                    var wallMid = fp.Center - back * (fp.Depth / 2);
                    var normal = -back;
                    for (float s = -fp.Width / 2 + 2.4f; s <= fp.Width / 2 - 2.4f && !r.Road; s += 1.5f)
                        if (GarageLink.Choose(wallMid + fp.AxisU * s + normal * 0.03f, normal, streets).Any) r.Road = true;
                    Judge(r);
                    mine.Add(r);
                }
                catch (Exception e) { GD.Print($"[rampfirst] {id} #{i}: {e.GetType().Name} {e.Message}"); }
            }
            lock (rows)
            {
                rows.AddRange(mine); garages += g; buildings += tile.Buildings.Count; tilesDone++;
                if (tilesDone % 50 == 0) GD.Print($"[rampfirst] {tilesDone}/{ids.Count} tiles, {rows.Count} blocks, {sw.Elapsed.TotalSeconds:F0} s");
            }
        });
        Report(rows, ids.Count, buildings, garages);
        GD.Print("[rampfirst] RESULT: ok");
        GetTree().Quit(0);
    }

    /// <summary>The whole-gate and each orientation's verdict for one block (see the class summary).</summary>
    private static void Judge(Row r)
    {
        float h = r.H, L = RampProfile.Length(h);
        float stairDepth = GarageRule.FrontLanding + 0.28f * (int)MathF.Ceiling(h / 2 / GarageRule.StairRiser)
            + GarageRule.MidLanding + GarageRule.CorridorWidth;
        float area = r.W * r.D;
        r.TooSmall = area < MinArea;

        // (a) square to the front wall: depth for apron + run + turn, width for the lane (and a stairwell and a flat)
        float dA = GarageRule.RampDepth(h);
        float wA0 = GarageRule.RampWidth + 2 * Margin, wA = wA0 + StairCol + GarageRule.MinFlatSide;
        float parkA0 = area - GarageRule.RampWidth * GarageRule.RampFoot(h);
        float parkA = parkA0 - StairCol * stairDepth;
        // (b) along the facade: the door's column, the run, the turn at the foot, one lane deep from the wall
        float wB0 = GarageRule.RampWidth + L + GarageRule.RampTurn + 2 * Margin, wB = wB0 + StairCol;
        float dB = Math.Max(AlongDepth + 3f, 0f);
        float parkB0 = area - AlongDepth * wB0;
        float parkB = parkB0 - StairCol * stairDepth;
        float dBs = Math.Max(dB, stairDepth);

        string Why(bool depthOk, bool widthOk, bool parkOk) =>
            !depthOk && !widthOk ? "both too shallow and too narrow" : !depthOk ? "too shallow" : !widthOk ? "too narrow" : !parkOk ? "car park under 4 bays" : "";

        r.ANoStair = r.D >= dA && r.W >= wA0 && parkA0 >= 4 * BayGross;
        r.A = r.D >= dA && r.W >= wA && parkA >= 4 * BayGross;
        r.A8 = r.D >= dA && r.W >= wA && parkA >= 8 * BayGross;
        r.BNoStair = r.D >= dB && r.W >= wB0 && parkB0 >= 4 * BayGross;
        r.B = r.D >= dBs && r.W >= wB && parkB >= 4 * BayGross;
        r.B8 = r.D >= dBs && r.W >= wB && parkB >= 8 * BayGross;
        r.WhyA = Why(r.D >= dA, r.W >= wA, parkA >= 4 * BayGross);
        r.WhyB = Why(r.D >= dBs, r.W >= wB, parkB >= 4 * BayGross);
        r.WhyAny = r.A || r.B ? "" : r.WhyA == r.WhyB ? r.WhyA : "mixed (" + r.WhyA + " | " + r.WhyB + ")";
    }

    private static string Pct(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:F1} %";

    private void Report(List<Row> rows, int tiles, int buildings, int garages)
    {
        var sb = new StringBuilder();
        void P(string s) { sb.AppendLine(s); GD.Print("[rampfirst] " + s); }
        int N = rows.Count;
        var whole = rows.Where(r => !r.Wing).ToList();
        var gated = whole.Where(r => r.Road && !r.TooSmall).ToList();
        P($"Real tiles: {tiles} building tiles, {buildings} buildings; {garages} real garage doors today (any kind).");
        P($"Blocks of flats or shops under flats (not banks): {N}  (flats {rows.Count(r => r.Type == "Apartments")}, mixed {rows.Count(r => r.Type == "MixedUse")})");
        P($"  garage door today (the current rule, roll included): {rows.Count(r => r.Today)} = {Pct(rows.Count(r => r.Today), N)}");
        P("");
        P("Gate funnel (same gating as today, exclusive, in order):");
        P($"  wing-planned (L, U, courtyard): {rows.Count(r => r.Wing)} = {Pct(rows.Count(r => r.Wing), N)}");
        P($"  whole box, no street within GarageLink reach: {whole.Count(r => !r.Road)} = {Pct(whole.Count(r => !r.Road), N)}");
        P($"  whole box, road ok, area under {MinArea:F0} m2 (too small): {whole.Count(r => r.Road && r.TooSmall)} = {Pct(whole.Count(r => r.Road && r.TooSmall), N)}");
        P($"  pass the gate (whole, road, size): {gated.Count} = {Pct(gated.Count, N)}");
        P("");
        P("Front doors today (whole boxes): " + string.Join(", ", whole.GroupBy(r => Math.Min(r.FrontDoors, 3)).OrderBy(g => g.Key)
            .Select(g => $"{(g.Key >= 3 ? "3+" : g.Key.ToString())} door(s) {g.Count()} = {Pct(g.Count(), whole.Count)}")));
        P($"  1 front door {Pct(whole.Count(r => r.FrontDoors <= 1), whole.Count)}, 2+ front doors {Pct(whole.Count(r => r.FrontDoors >= 2), whole.Count)}");
        P($"  (all blocks: 1 door {Pct(rows.Count(r => r.FrontDoors <= 1), N)}, 2+ {Pct(rows.Count(r => r.FrontDoors >= 2), N)})");
        P("");
        P("Median plan box of the gated blocks (W along the front x D): "
            + $"{Median(gated.Select(r => r.W)):F1} x {Median(gated.Select(r => r.D)):F1} m, storey {Median(gated.Select(r => r.H)):F2} m");
        float h = Median(gated.Select(r => r.H));
        P($"Needs (storey {h:F2} m): ramp run {RampProfile.Length(h):F1} m; (a) square: depth {GarageRule.RampDepth(h):F1}, width "
            + $"{GarageRule.RampWidth + 2 * Margin:F1} ramp only, +{StairCol + GarageRule.MinFlatSide:F1} with stairwell and a flat; "
            + $"(b) along: width {GarageRule.RampWidth + RampProfile.Length(h) + GarageRule.RampTurn + 2 * Margin:F1} (+{StairCol:F1} stairwell), depth {AlongDepth + 3:F0}+; car park of 4 bays = {4 * BayGross:F0} m2");
        P("");
        P("Qualifies, share of ALL blocks / of gate-passing blocks   [count]:");
        P("  variant                               geometry only (gate ignored)  gate-passing blocks   expected garages (gate + the roll)");
        void Line(string name, Func<Row, bool> f)
        {
            int all = rows.Count(f), gate = gated.Count(f);
            int rolled = gated.Count(r => f(r) && r.Rolls);
            P($"  {name,-38}{all,5} {Pct(all, N),8}  {gate,6} {Pct(gate, gated.Count),8}   {rolled,5} = {Pct(rolled, N)} of all");
        }
        Line("(a) square, ramp only, 4 bays", r => r.ANoStair);
        Line("(a) square + stairwell, 4 bays", r => r.A);
        Line("(a) square + stairwell, 8 bays", r => r.A8);
        Line("(b) along the facade, ramp only, 4 bays", r => r.BNoStair);
        Line("(b) along + stairwell, 4 bays", r => r.B);
        Line("(b) along + stairwell, 8 bays", r => r.B8);
        Line("(a) or (b) + stairwell, 4 bays", r => r.A || r.B);
        Line("(a) or (b) + stairwell, 8 bays", r => r.A8 || r.B8);
        Line("(b) only (not (a)), + stairwell", r => r.B && !r.A);
        P("");
        P("Same, by door count (gate-passing, (a) or (b) + stairwell, 4 bays): "
            + $"1 front door {gated.Count(r => r.FrontDoors <= 1 && (r.A || r.B))}/{gated.Count(r => r.FrontDoors <= 1)}, "
            + $"2+ front doors {gated.Count(r => r.FrontDoors >= 2 && (r.A || r.B))}/{gated.Count(r => r.FrontDoors >= 2)}");
        P("");
        foreach (var (name, sel) in new (string, Func<Row, string>)[] { ("(a) square + stairwell", r => r.WhyA), ("(b) along + stairwell", r => r.WhyB), ("(a) and (b) both fail", r => r.WhyAny) })
        {
            P($"Why gate-passing blocks fail, {name}:");
            var fails = gated.Where(r => sel(r).Length > 0).ToList();
            foreach (var g in fails.GroupBy(sel).OrderByDescending(g => g.Count()))
                P($"  {g.Key,-60}{g.Count(),5} = {Pct(g.Count(), gated.Count)} of gate-passing");
            P($"  (passes: {gated.Count - fails.Count} = {Pct(gated.Count - fails.Count, gated.Count)})");
        }
        P("");
        P("Depth and width of the gate-passing blocks (count in each band):");
        P("  depth m:  " + Hist(gated.Select(r => r.D), 8, 10, 12, 14, 16, 18, 20, 24, 30));
        P("  width m:  " + Hist(gated.Select(r => r.W), 8, 10, 12, 15, 18, 22, 26, 30, 40, 60));
        P("  storeys above: " + string.Join(", ", gated.GroupBy(r => Math.Min(r.Above, 8)).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_out))!);
        File.WriteAllText(_out, sb.ToString());
        var csv = new StringBuilder("key,type,w,d,storeyH,above,frontDoors,today,wing,road,tooSmall,rolls,a,b,whyA,whyB\n");
        foreach (var r in rows)
            csv.AppendLine(string.Join(",", r.Key, r.Type, F(r.W), F(r.D), F(r.H), r.Above, r.FrontDoors, r.Today ? 1 : 0, r.Wing ? 1 : 0,
                r.Road ? 1 : 0, r.TooSmall ? 1 : 0, r.Rolls ? 1 : 0, r.A ? 1 : 0, r.B ? 1 : 0, r.WhyA, r.WhyB));
        File.WriteAllText(Path.ChangeExtension(_out, ".csv"), csv.ToString());
        GD.Print($"[rampfirst] wrote {_out} and its .csv");
    }

    private static string F(float v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static float Median(IEnumerable<float> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l.Count == 0 ? 0 : l[l.Count / 2];
    }

    private static string Hist(IEnumerable<float> xs, params float[] edges)
    {
        var l = xs.ToList();
        var parts = new List<string>();
        for (int i = 0; i <= edges.Length; i++)
        {
            float lo = i == 0 ? float.MinValue : edges[i - 1], hi = i == edges.Length ? float.MaxValue : edges[i];
            parts.Add($"{(i == 0 ? "<" + edges[0] : i == edges.Length ? ">=" + edges[^1] : edges[i - 1] + "-" + edges[i])}:{l.Count(x => x >= lo && x < hi)}");
        }
        return string.Join("  ", parts);
    }
}
