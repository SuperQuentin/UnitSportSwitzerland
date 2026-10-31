using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// <c>--groundcheck [--chunks dir] [--ground-tiles E,N;E,N]</c> (#1417): where buildings meet the
/// ground, measured on real tiles through the code the chunk build runs. Real data only (no tier):
/// <c>godot --headless --path . -- --groundcheck --chunks D:/.../terrain_chunks --systems ui</c>.
///
/// <para>
/// Walls: every bottom edge on the solid's base, cut into the footing pieces, against the ground the
/// ring draws (the stride-1 grid decimated to the ring's stride): the gap histogram, the pieces and
/// buildings left with no footing over a gap, and the footings whose foot ends over drawn ground
/// (a slot under a footing). Doors: the sill against the ground 0.8 m in front at both jambs
/// (buried over 10 cm, floating over 15 cm after the stoop). Each at the ring strides 1/2/4/10, so
/// the LOD mismatch shows. Output also goes to <c>test_output/ground/groundcheck.txt</c>.
/// The road-to-facade strip (#1419, <see cref="GroundStrip"/>): the walkable ground (the road blend
/// at clearance 0, as the collision floor) between a road ribbon's edge and a facade within 25 m,
/// per ring stride: a slope histogram and the lattice edges stepping over 0.3 m.
/// </para>
/// </summary>
public static class GroundCheck
{
    public static bool Requested => CmdArgs.Has("--groundcheck");

    private static readonly int[] Strides = [1, 2, 4, 10];
    private static readonly string[] Bins = ["<0.05", "0.05-1", "1-3", "3-10", ">10"];

    private static readonly (string Name, int E, int N)[] Default =
        [("Sion", 2597, 1119), ("Lausanne", 2538, 1152), ("Zermatt", 2623, 1095)];

    private static int Bin(float gap) => gap < 0.05f ? 0 : gap < 1f ? 1 : gap < 3f ? 2 : gap < 10f ? 3 : 4;

    public static int Run()
    {
        // numbers print the same on every locale (a French one writes 0,05)
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        string dir = CmdArgs.Value(CmdArgs.All, "--chunks") ?? "terrain_chunks";
        var tiles = new List<(string Name, TileId Id)>();
        if (CmdArgs.Value(CmdArgs.All, "--ground-tiles", 1, true) is { } list)
            foreach (var part in list.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var en = part.Split(',');
                if (en.Length == 2 && int.TryParse(en[0], out int e) && int.TryParse(en[1], out int n)) tiles.Add(($"{e}_{n}", new TileId(e, n)));
            }
        else tiles.AddRange(Default.Select(t => (t.Name, new TileId(t.E, t.N))));

        var src = new LocalChunkSource(dir);
        var o = new StringBuilder();
        void Say(string s) { o.AppendLine(s); GD.Print($"[groundcheck] {s}"); }
        int measured = 0;
        foreach (var (name, id) in tiles)
        {
            var bt = src.LoadBuildingsAsync(id).GetAwaiter().GetResult();
            var full = src.LoadChunkAsync(id).GetAwaiter().GetResult();
            if (bt == null || full == null) { Say($"{name} ({id}): no tile in {dir}"); continue; }
            var roads = src.LoadRoadsAsync(id).GetAwaiter().GetResult();
            measured++;
            Say($"== {name} tile {id.E},{id.N}: {bt.Buildings.Count} buildings");
            // the doors as the build gets them: the stride-1 grid for rings 0-2 (strides 1, 2, 4), none at the coarse ring
            var doorsFine = BuildingFootprint.ComputeDoors(bt, roads, full);
            var doorsCoarse = BuildingFootprint.ComputeDoors(bt, roads, null);
            Strip(bt, roads, full, Say);
            foreach (int s in Strides)
            {
                var drawn = s == 1 ? full : full.Decimate(s);
                Walls(bt, full, drawn, s, Say);
                Doors(bt, s >= ChunkFormat.CoarseStride ? doorsCoarse : doorsFine, drawn, s, Say);
            }
        }
        string outDir = System.IO.Path.Combine("test_output", "ground");
        try { System.IO.Directory.CreateDirectory(outDir); System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "groundcheck.txt"), o.ToString()); }
        catch (Exception e) { GD.PushWarning($"[groundcheck] cannot write the report: {e.Message}"); }
        GD.Print($"[groundcheck] RESULT: {(measured > 0 ? "ok" : "FAILED (no tile measured)")}, {measured} tile(s)");
        return measured > 0 ? 0 : 1;
    }

    private static void Walls(BuildingTile bt, ChunkGrid full, ChunkGrid drawn, int stride, Action<string> say)
    {
        float Ground(ChunkGrid g, Vector3 p) => (float)g.SampleMeshHeight(bt.Id.MinE + p.X, bt.Id.MaxN - p.Z);
        var footings = BuildingMeshBuilder.FootingsForRing(bt, full, stride);
        var byBuilding = footings.GroupBy(f => f.Building).ToDictionary(g => g.Key, g => g.ToList());
        var hist = new int[5];
        var bare = new int[5];
        int pieces = 0, uncovered = 0, bareBuildings = 0, slots = 0, buildingsWithGap = 0, offPieces = 0, offBare = 0;
        float worstSlot = 0, worstDiff = 0, worstBare = 0;
        double worstAtE = 0, worstAtN = 0;
        for (int bi = 0; bi < bt.Buildings.Count; bi++)
        {
            var b = bt.Buildings[bi];
            if (!BuildingMeshBuilder.Drawn(b)) continue;
            bool anyGap = false, anyFooting = byBuilding.ContainsKey(bi);
            foreach (var (p, q, onBase) in BuildingMeshBuilder.BottomEdges(b))
            {
                float len = new Vector2(q.X - p.X, q.Z - p.Z).Length();
                int count = Mathf.Max(1, Mathf.CeilToInt(len / BuildingMeshBuilder.FootingStep));
                for (int i = 0; i < count; i++)
                {
                    var e0 = p.Lerp(q, (float)i / count);
                    var e1 = p.Lerp(q, (float)(i + 1) / count);
                    float gap = Mathf.Max(e0.Y - Ground(drawn, e0), e1.Y - Ground(drawn, e1));
                    bool covered = byBuilding.TryGetValue(bi, out var mine) && mine.Any(f => Near(f, e0, e1));
                    if (!onBase)
                    {
                        // a wall that starts above its base: only the clear cases (under a storey, over the ground)
                        if (gap > 0.05f && gap <= BuildingMeshBuilder.FootingMax) { offPieces++; if (!covered) offBare++; }
                        continue;
                    }
                    worstDiff = Mathf.Max(worstDiff, Mathf.Abs(Ground(full, e0) - Ground(drawn, e0)));
                    pieces++;
                    int bin = Bin(gap);
                    hist[bin]++;
                    if (gap < 0.05f) continue;
                    anyGap = true;
                    if (!covered)
                    {
                        uncovered++;
                        bare[bin]++;
                        if (gap > worstBare) { worstBare = gap; worstAtE = bt.Id.MinE + e0.X; worstAtN = bt.Id.MaxN - e0.Z; }
                    }
                }
            }
            if (anyGap) { buildingsWithGap++; if (!anyFooting) bareBuildings++; }
        }
        // a slot under a footing: the foot ends above the ground the ring draws (ends and middle)
        foreach (var f in footings)
        {
            float worst = 0;
            foreach (float u in new[] { 0f, 0.5f, 1f })
            {
                var at = f.A.Lerp(f.B, u);
                float foot = Mathf.Lerp(f.GroundA, f.GroundB, u) - f.Below;
                worst = Mathf.Max(worst, foot - Ground(drawn, at));
            }
            if (worst > 0.05f) slots++;
            worstSlot = Mathf.Max(worstSlot, worst);
        }
        say($"  walls stride {stride}: {pieces} base pieces; gap {string.Join(" ", Bins.Select((n, i) => $"{n}={hist[i]}"))}; "
            + $"over a gap with NO footing {uncovered} pieces ({string.Join(" ", Bins.Skip(1).Select((n, i) => $"{n}={bare[i + 1]}"))}, worst {worstBare:F1} m"
            + (worstBare > 0 ? $" at --at {worstAtE:F0},{worstAtN:F0}" : "") + $"); {bareBuildings}/{buildingsWithGap} gapped buildings have NO footing at all; "
            + $"{footings.Count} footings, {slots} end above drawn ground (worst {worstSlot:F2} m); drawn vs stride-1 ground at the base worst {worstDiff:F2} m; "
            + $"off-base wall bottoms over a gap (<=3 m): {offPieces} pieces, {offBare} with NO footing");
    }

    private static void Strip(BuildingTile bt, RoadTile? roads, ChunkGrid full, Action<string> say)
    {
        if (roads == null) { say("  strip: no road tile"); return; }
        int n = ChunkFormat.GridSize;
        var ground = TerrainMeshBuilder.GroundHeights(full, TerrainMeshBuilder.ComputeRoadBlend(roads), 0.0);
        var heights = new float[n * n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) heights[r * n + c] = ground(c, r);
        var facades = new List<GroundStrip.Seg>();
        var plans = new List<float[]>();
        foreach (var b in bt.Buildings)
        {
            if (!BuildingMeshBuilder.Drawn(b)) continue;
            foreach (var (p, q, onBase) in BuildingMeshBuilder.BottomEdges(b))
                if (onBase) facades.Add(new GroundStrip.Seg(p.X, p.Z, q.X, q.Z));
            for (int t = 0; t < b.TriangleCount; t++)
            {
                var (a, c, d) = b.Tri(t);
                plans.Add([a.X, a.Z, c.X, c.Z, d.X, d.Z]);
            }
        }
        var (rd, onRoad) = GroundStrip.RoadEdgeDistance(roads);
        var (fd, under) = GroundStrip.FacadeDistance(facades, plans);
        var strip = GroundStrip.Members(rd, onRoad, fd, under);
        foreach (int s in Strides)
        {
            var m = GroundStrip.Of(heights, strip, s);
            say($"  strip stride {s}: {m.Cells} cells; slope deg {string.Join(" ", GroundStrip.SlopeNames.Select((nm, i) => $"{nm}={m.Slope[i]}"))}; "
                + $"lattice steps >0.3 m: {m.Steps} of {m.Edges} edges (worst {m.WorstStep:F2} m)");
        }
    }

    private static bool Near(BuildingMeshBuilder.Footing f, Vector3 a, Vector3 b) =>
        (f.A.DistanceTo(a) < 0.02f && f.B.DistanceTo(b) < 0.02f) || (f.A.DistanceTo(b) < 0.02f && f.B.DistanceTo(a) < 0.02f);

    private static void Doors(BuildingTile bt, DoorSpot[] doors, ChunkGrid drawn, int stride, Action<string> say)
    {
        float Ground(Vector3 p) => (float)drawn.SampleMeshHeight(bt.Id.MinE + p.X, bt.Id.MaxN - p.Z);
        int total = 0, buried = 0, floating = 0, stoops = 0, level = 0;
        var worst = new List<(float Off, string What)>();
        foreach (var d in doors)
        {
            if (d.Width <= 0 || !BuildingMeshBuilder.Drawn(bt.Buildings[d.Index])) continue;
            total++;
            var along = new Vector3(-d.Outward.Z, 0, d.Outward.X);
            float hw = Mathf.Min(d.Width / 2, 1.5f);
            var front = d.Position + d.Outward * 0.8f;
            float gl = Ground(front - along * hw), gr = Ground(front + along * hw), gm = Ground(front);
            float high = Mathf.Max(gm, Mathf.Max(gl, gr)), low = Mathf.Min(gm, Mathf.Min(gl, gr));
            float sill = d.Position.Y;
            // a stoop's steps close the drop by their rise each
            float drop = sill - low - d.Steps * d.Rise;
            if (d.Steps > 0) stoops++;
            string where = $"--at {bt.Id.MinE + d.Position.X:F0},{bt.Id.MaxN - d.Position.Z:F0} slot {d.Slot} sill {sill:F2} front {low:F2}..{high:F2} steps {d.Steps}";
            if (high - sill > 0.1f) { buried++; worst.Add((high - sill, "buried " + where)); }
            else if (drop > 0.15f) { floating++; worst.Add((drop, "floating " + where)); }
            else level++;
        }
        say($"  doors stride {stride}: {total} doors; buried>10cm {buried}, floating>15cm {floating}, with a stoop {stoops}, level {level}"
            + (worst.Count > 0 ? "; worst: " + string.Join(" | ", worst.OrderByDescending(w => w.Off).Take(3).Select(w => $"{w.Off:F2} m {w.What}")) : ""));
    }
}
