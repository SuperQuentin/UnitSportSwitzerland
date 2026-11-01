using System.Collections.Concurrent;
using System.Diagnostics;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Network;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The town ground stage (#1419, <see cref="TownGround"/>): after the pads, the road extraction and the road
/// network stage, the ground between the roads and the buildings is solved as one membrane and written into
/// the <c>.terr</c> heights.
///
/// <para>
/// <b>Inputs</b> per tile, over a window of it and <see cref="TownGround.Margin"/> cells into its eight
/// neighbours: the heights left by the earlier stages, the runtime road blend of each tile's final
/// <c>.road</c> (<see cref="TerrainMeshBuilder.ComputeRoadBlend"/>, the same code the game runs, so the
/// solve and the blend agree on the road cells), the footprints of the <c>.bldg</c> files
/// (<see cref="BuildingPad.FootprintOf"/>) and the urban weight of their walls (<see cref="UrbanField"/>).
/// </para>
///
/// <para>
/// <b>Seams</b>: every tile reads the same inputs for the same cells, the heights a neighbour had before this
/// stage changed it (held in memory column by column), so the shared border vertices come out identical
/// (<see cref="TerrainBuild.VerifySeams"/> runs at the end). <b>Re-runnable</b> like the pads: the changes are
/// kept in <c>&lt;temp&gt;/townground/E_N.grd</c> and <see cref="Restore"/> undoes them first.
/// </para>
/// </summary>
public static class TownGroundStage
{
    internal const string Dir = "townground";

    /// <summary>Metres round a retaining wall's face, a bridge or a tunnel, and a stream where the ground keeps its height.</summary>
    private const double WallKeepM = 5.0, StructureKeepM = 3.0, StreamKeepM = 2.0;

    /// <summary>Undoes an earlier run (before the pads are restored: this stage ran after them).</summary>
    public static int Restore(string outDir, string tempDir, IReadOnlyCollection<TileId> tiles, int jobs, CancellationToken ct = default) =>
        GradeStage.Restore(outDir, tempDir, tiles, jobs, ct, Dir);

    /// <summary>A tile's inputs in global lattice coordinates (x east, y south: row 0 of tile N is y = -(N + 1) * 1000).</summary>
    private sealed class Inputs
    {
        public required ushort[] H0;
        public required int[] RoadCells;      // tile-local indices
        public required float[] RoadFixed;
        public required List<long> Keep, Footprint;   // global keys
    }

    private static long Key(long x, long y) => (y << 32) ^ (x & 0xffffffffL);
    private static (long X, long Y) Unkey(long k) => ((int)(k & 0xffffffffL), k >> 32);

    private static long X0(TileId id) => (long)id.E * 1000;
    private static long Y0(TileId id) => -((long)id.N + 1) * 1000;

    public static int Run(string outDir, string tempDir, IReadOnlyCollection<TileId> tiles, int jobs, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        string manifestPath = Path.Combine(outDir, "manifest.json");
        var manifest = TerrainManifest.FromJson(File.ReadAllText(manifestPath));
        var region = manifest.Tiles.Select(t => t.Id).ToHashSet();
        var scope = GradeStage.ScopeOf(outDir, tiles);

        var inputs = new ConcurrentDictionary<TileId, Lazy<Inputs?>>();
        Inputs? InputsOf(TileId id) => !region.Contains(id) ? null
            : inputs.GetOrAdd(id, k => new Lazy<Inputs?>(() => Load(outDir, tempDir, k))).Value;

        var urbanFields = new ThreadLocal<(UrbanField Field, int Uses)>(() => (new UrbanField(new Facades(outDir)), 0));

        var changedTiles = new ConcurrentBag<ManifestTile>();
        long freeCells = 0, cellsChanged = 0, solveTicks = 0;
        int written = 0, solved = 0;
        float worstResidual = 0;
        object gate = new();
        foreach (var column in scope.GroupBy(t => t.E).OrderBy(g => g.Key))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var key in inputs.Keys)
                if (key.E < column.Key - 1) inputs.TryRemove(key, out _);
            // every tile this column reads, loaded before any of it is written
            var needed = column.SelectMany(id => Enumerable.Range(-1, 3).SelectMany(de => Enumerable.Range(-1, 3)
                .Select(dn => new TileId(id.E + de, id.N + dn)))).Distinct().ToList();
            Parallel.ForEach(needed, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, id => InputsOf(id));

            Parallel.ForEach(column, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, id =>
            {
                var own = InputsOf(id);
                if (own == null) return;
                var tick = Stopwatch.StartNew();
                var (field, uses) = urbanFields.Value;
                if (uses >= 32) field = new UrbanField(new Facades(outDir));   // its density cache grows per tile
                urbanFields.Value = (field, uses >= 32 ? 1 : uses + 1);

                var win = BuildWindow(id, InputsOf, out bool anyRoad);
                ushort[] heights = (ushort[])own.H0.Clone();
                if (anyRoad)
                {
                    int m = TownGround.Margin, w = win.Size;
                    double e0 = id.MinE - m, n0 = id.MaxN + m;
                    var res = TownGround.Solve(win, i =>
                    {
                        int r = i / w, c = i - r * w;
                        return field.Weight(e0 + c, n0 - r);
                    });
                    int n = ChunkFormat.GridSize;
                    for (int r = 0; r < n; r++)
                        for (int c = 0; c < n; c++)
                        {
                            int wi = (r + m) * w + c + m;
                            if (res.Heights[wi] != win.H0[wi]) heights[r * n + c] = ChunkFormat.Quantize(res.Heights[wi]);
                        }
                    Interlocked.Add(ref freeCells, res.Free);
                    Interlocked.Increment(ref solved);
                    lock (gate) worstResidual = Math.Max(worstResidual, res.Residual);
                }
                Interlocked.Add(ref solveTicks, tick.ElapsedTicks);

                var changes = new List<GradeStage.Change>();
                for (int k = 0; k < heights.Length; k++)
                    if (heights[k] != own.H0[k]) changes.Add(new GradeStage.Change(k, own.H0[k], heights[k]));
                GradeStage.WriteSidecar(tempDir, id, changes, Dir);
                Interlocked.Add(ref cellsChanged, changes.Count);

                var current = GradeStage.ReadTile(outDir, id);
                if (!heights.AsSpan().SequenceEqual(current.Heights))
                {
                    var grid = GradeStage.GridOf(id, heights);
                    GradeStage.WriteTile(outDir, grid);
                    changedTiles.Add(new ManifestTile { E = id.E, N = id.N, Min = grid.MinHeight, Max = grid.MaxHeight });
                    Interlocked.Increment(ref written);
                }
            });
        }

        if (!changedTiles.IsEmpty)
        {
            var byId = changedTiles.ToDictionary(t => t.Id);
            foreach (var t in manifest.Tiles)
                if (byId.TryGetValue(t.Id, out var c)) { t.Min = c.Min; t.Max = c.Max; }
            AtomicFile.Write(manifestPath, s => { using var w = new StreamWriter(s); w.Write(manifest.ToJson()); });
            if (HorizonStage.Run(outDir, jobs, ct) is var hrc && hrc != 0) return hrc;
        }
        int seamErrors = TerrainBuild.VerifySeams(outDir, scope, jobs, ct);
        double perTile = solved == 0 ? 0 : solveTicks * 1000.0 / Stopwatch.Frequency / scope.Count;
        Console.WriteLine($"Town ground: {scope.Count} tiles, {solved} solved, {freeCells:N0} free cells, {cellsChanged:N0} cells changed, "
            + $"{written} files written in {clock.Elapsed.TotalSeconds:F1}s ({perTile:F0} ms/tile solve, worst last sweep {worstResidual * 1000:F2} mm)"
            + (seamErrors > 0 ? $", {seamErrors} SEAM ERRORS" : ""));
        return seamErrors > 0 ? 1 : 0;
    }

    /// <summary>The heights before this stage, the road cells of the tile's own blend, and what keeps its height or is a footprint.</summary>
    private static Inputs? Load(string outDir, string tempDir, TileId id)
    {
        string terr = Path.Combine(outDir, ChunkFormat.ChunkFileName(id));
        if (!File.Exists(terr)) return null;
        var heights = (ushort[])GradeStage.ReadTile(outDir, id).Heights.Clone();
        GradeStage.Undo(heights, GradeStage.ReadSidecar(tempDir, id, Dir));

        var roadCells = new List<int>();
        var roadFixed = new List<float>();
        var keep = new List<long>();
        long x0 = X0(id), y0 = Y0(id);
        string roadPath = Path.Combine(outDir, RoadFormat.FileName(id));
        if (File.Exists(roadPath))
        {
            RoadTile roads;
            using (var fs = File.OpenRead(roadPath)) roads = RoadCodec.Decode(fs);
            var blend = TerrainMeshBuilder.ComputeRoadBlend(roads);
            int n = ChunkFormat.GridSize;
            for (int k = 0; k < blend.Cells.Length; k++)
            {
                float lo = blend.Lo[k], hi = blend.Hi[k];
                int cell = blend.Cells[k];
                if (lo == hi && float.IsFinite(lo))
                {
                    roadCells.Add(cell);
                    roadFixed.Add(lo);
                }
                else if (!float.IsFinite(lo) || !float.IsFinite(hi))
                    keep.Add(Key(x0 + cell % n, y0 + cell / n));   // one-sided: over a bore, under an island
            }
            foreach (var prop in roads.LinearProps)
                if (prop.Type is LinearPropType.RetainingWallFill or LinearPropType.RetainingWallCut)
                    Stamp(keep, x0, y0, prop.Points, 4, prop.PointCount, WallKeepM);
            foreach (var seg in roads.Segments)
            {
                if (seg.PointCount < 2) continue;
                bool structure = (seg.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0;
                if (structure) Stamp(keep, x0, y0, seg.Points, 3, seg.PointCount, seg.Width * 0.5 + StructureKeepM);
                else if (RoadFormat.IsWatercourse(seg.Class)) Stamp(keep, x0, y0, seg.Points, 3, seg.PointCount, seg.Width * 0.5 + StreamKeepM);
            }
        }

        var footprint = new List<long>();
        string bldg = Path.Combine(outDir, BuildingFormat.FileName(id));
        if (File.Exists(bldg))
        {
            BuildingTile bt;
            using (var fs = File.OpenRead(bldg)) bt = BuildingCodec.Decode(fs);
            foreach (var b in bt.Buildings)
            {
                if (BuildingPad.FootprintOf(b, id) is not { } f) continue;
                double s = TownGround.FootprintShoulder;
                long gx0 = (long)Math.Floor(f.MinE - s), gx1 = (long)Math.Ceiling(f.MaxE + s);
                long gn0 = (long)Math.Floor(f.MinN - s), gn1 = (long)Math.Ceiling(f.MaxN + s);
                for (long gn = gn0; gn <= gn1; gn++)
                    for (long gx = gx0; gx <= gx1; gx++)
                        if (f.SignedDistance(gx, gn) <= s) footprint.Add(Key(gx, -gn));
            }
        }
        return new Inputs
        {
            H0 = heights, RoadCells = roadCells.ToArray(), RoadFixed = roadFixed.ToArray(),
            Keep = keep, Footprint = footprint,
        };
    }

    /// <summary>Every lattice point within <paramref name="radius"/> of a tile-local polyline (x, ., z per point, <paramref name="stride"/> floats).</summary>
    private static void Stamp(List<long> into, long x0, long y0, float[] p, int stride, int count, double radius)
    {
        for (int i = 0; i + 1 < count; i++)
        {
            double ax = p[i * stride], az = p[i * stride + 2], bx = p[(i + 1) * stride], bz = p[(i + 1) * stride + 2];
            double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            int c0 = (int)Math.Floor(Math.Min(ax, bx) - radius), c1 = (int)Math.Ceiling(Math.Max(ax, bx) + radius);
            int r0 = (int)Math.Floor(Math.Min(az, bz) - radius), r1 = (int)Math.Ceiling(Math.Max(az, bz) + radius);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double t = l2 < 1e-12 ? 0 : Math.Clamp(((c - ax) * dx + (r - az) * dz) / l2, 0, 1);
                    double ex = ax + dx * t - c, ez = az + dz * t - r;
                    if (ex * ex + ez * ez <= radius * radius) into.Add(Key(x0 + c, y0 + r));
                }
        }
    }

    /// <summary>The window of a tile and its margin, from the inputs of the nine tiles round it, in a fixed order.</summary>
    private static TownGround.Window BuildWindow(TileId id, Func<TileId, Inputs?> inputsOf, out bool anyRoad)
    {
        int m = TownGround.Margin, n = ChunkFormat.GridSize, w = n + 2 * m;
        var win = new TownGround.Window(w);
        long wx0 = X0(id) - m, wy0 = Y0(id) - m;
        var kinds = win.Kinds;
        anyRoad = false;

        // heights: the tile that holds a cell (a border vertex is the same in both)
        var near = new List<(TileId Id, Inputs In)>();
        for (int de = -1; de <= 1; de++)
            for (int dn = 1; dn >= -1; dn--)
                if (inputsOf(new TileId(id.E + de, id.N + dn)) is { } inp) near.Add((new TileId(id.E + de, id.N + dn), inp));
        foreach (var (tid, inp) in near)
        {
            long tx0 = X0(tid) - wx0, ty0 = Y0(tid) - wy0;
            for (int r = 0; r < n; r++)
            {
                long wr = ty0 + r;
                if (wr < 0 || wr >= w) continue;
                for (int c = 0; c < n; c++)
                {
                    long wc = tx0 + c;
                    if (wc < 0 || wc >= w) continue;
                    int wi = (int)(wr * w + wc);
                    win.H0[wi] = (float)ChunkFormat.Dequantize(inp.H0[r * n + c]);
                    kinds[wi] = TownGround.Kind.Free;
                }
            }
        }

        void Mark(List<long> keys, TownGround.Kind kind)
        {
            foreach (long k in keys)
            {
                var (x, y) = Unkey(k);
                long wc = x - wx0, wr = y - wy0;
                if (wc < 0 || wr < 0 || wc >= w || wr >= w) continue;
                int wi = (int)(wr * w + wc);
                if (kinds[wi] != TownGround.Kind.Outside) kinds[wi] = kind;
            }
        }
        foreach (var (_, inp) in near) Mark(inp.Keep, TownGround.Kind.Keep);
        foreach (var (_, inp) in near) Mark(inp.Footprint, TownGround.Kind.Footprint);

        // road cells last, but never over a footprint's shoulder: a door reads the ground there, on the pad's level
        // (the runtime blend still holds the road over it); the first tile in the fixed order wins a shared vertex
        var taken = new bool[w * w];
        foreach (var (tid, inp) in near)
        {
            long tx0 = X0(tid) - wx0, ty0 = Y0(tid) - wy0;
            for (int k = 0; k < inp.RoadCells.Length; k++)
            {
                int cell = inp.RoadCells[k];
                long wr = ty0 + cell / n, wc = tx0 + cell % n;
                if (wc < 0 || wr < 0 || wc >= w || wr >= w) continue;
                int wi = (int)(wr * w + wc);
                if (taken[wi] || kinds[wi] is TownGround.Kind.Outside or TownGround.Kind.Footprint) continue;
                taken[wi] = true;
                kinds[wi] = TownGround.Kind.Road;
                win.Fixed[wi] = inp.RoadFixed[k];
                anyRoad = true;
            }
        }
        return win;
    }
}
