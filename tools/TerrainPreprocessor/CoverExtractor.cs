using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Rasterises every swissTLM3D area layer onto each tile's vertex lattice, then populates
/// it: random scatter in the wooded classes, a planting grid in orchards and nurseries,
/// and the surveyed positions of TLM's individual trees.
/// </summary>
public sealed class CoverExtractor
{
    private readonly string _gpkgPath;

    public CoverExtractor(string gpkgPath) => _gpkgPath = gpkgPath;

    public Dictionary<TileId, byte[]> Cover { get; } = new();
    public Dictionary<TileId, List<TreeInstance>> Trees { get; } = new();
    public int PolygonCount { get; private set; }
    public int TrafficAreaCount { get; private set; }

    /// <summary>Rings rasterised per source layer, for the run summary.</summary>
    public Dictionary<string, int> LayerRings { get; } = new();

    public int SurveyedTrees { get; private set; }
    public int PlantedTrees { get; private set; }
    public int ScatteredTrees { get; private set; }

    public readonly record struct TreeInstance(float X, float Y, float Z, float Height, byte Kind);

    /// <summary>
    /// Tree appearance, shared with <c>ChunkNode.SetTrees</c>: 0 conifer, 1 shrub,
    /// 2 fruit tree (orchard rows), 3 solitary broadleaf (TLM's surveyed single trees).
    /// </summary>
    private const byte KindConifer = 0, KindShrub = 1, KindFruit = 2, KindSolitary = 3;

    /// <summary>
    /// Per-tile mask of cells occupied by a road, rail or portal corridor. Trees are not
    /// planted there — TLM's forest polygons cover the whole wood including the road cut
    /// through it, so without this a tunnel mouth ends up behind a wall of trees growing
    /// in the carriageway.
    /// </summary>
    public Dictionary<TileId, bool[]> RoadMask { get; } = new();

    public void Extract(IReadOnlyCollection<TileId> tiles, Func<double, double, double?> heightOf)
    {
        foreach (var t in tiles)
            Cover[t] = new byte[CoverFormat.Size * CoverFormat.Size];

        // no margin: an area or a tree that touches no wanted tile draws on none
        var region = new TileRegion(tiles);

        using var conn = GeoPackageReader.Open(_gpkgPath);

        // Layers are rasterised in order of increasing specificity: what a human would
        // name the ground wins over what grows on it. A car park stays last so it beats
        // everything, and the pitch polygon beats the sports ground that contains it.
        PolygonCount = Rasterise(conn, "tlm_bb_bodenbedeckung", CoverFormat.Parse, region);
        Rasterise(conn, "tlm_areale_nutzungsareal", CoverFormat.ParseLandUse, region);
        Rasterise(conn, "tlm_areale_freizeitareal", CoverFormat.ParseLeisure, region);
        Rasterise(conn, "tlm_bauten_sportbaute_ply", CoverFormat.ParseStructureArea, region);
        Rasterise(conn, "tlm_bauten_verkehrsbaute_ply", CoverFormat.ParseStructureArea, region);
        TrafficAreaCount = Rasterise(conn, "tlm_areale_verkehrsareal", CoverFormat.ParseTrafficArea, region);
        // hand-traced ground TLM does not map (docs/data/cover_overrides.json), last so it beats
        // every TLM layer; before the trees so an orchard it paves over is not planted
        if (OverridesPath != null) StampOverrides(OverridesPath);
        PaintPending();

        ScatterTrees(tiles, heightOf);
        PlantRows(tiles, heightOf);
        AddSurveyedTrees(conn, heightOf, region);

        // the three passes each ask for a list up front; drop the tiles that stayed bare
        foreach (var id in Trees.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            Trees.Remove(id);
    }

    /// <summary>JSON file of hand-traced cover polygons, or null for none.</summary>
    public string? OverridesPath { get; init; }

    /// <summary>
    /// Adds one tree to a tile after <see cref="Extract"/> has run: the car park planters (#499) are
    /// only known once the network stage has written its islands, which happens after this stage's
    /// own passes. The tile may have had no trees at all, so the list is created on demand.
    /// </summary>
    public void AddTree(TileId id, TreeInstance tree)
    {
        if (!Trees.TryGetValue(id, out var list)) Trees[id] = list = new List<TreeInstance>();
        list.Add(tree);
        PlantedTrees++;
    }

    /// <summary>
    /// Stamps the override polygons: <c>{"polygons":[{"cover":"ParkingPrivate","ring":[[E,N],...]}]}</c>
    /// in LV95. Rings off the batch's tiles fall away in <see cref="PaintPending"/>; <see cref="PaintRing"/> keeps Water.
    /// </summary>
    private void StampOverrides(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        int rings = 0;
        foreach (var poly in doc.RootElement.GetProperty("polygons").EnumerateArray())
        {
            var cls = Enum.Parse<CoverClass>(poly.GetProperty("cover").GetString()!);
            var xyz = new List<double>();
            foreach (var p in poly.GetProperty("ring").EnumerateArray())
                xyz.AddRange(new[] { p[0].GetDouble(), p[1].GetDouble(), 0.0 });
            // close it: the scanline walks edges i -> i+1
            if (xyz[0] != xyz[^3] || xyz[1] != xyz[^2]) xyz.AddRange(new[] { xyz[0], xyz[1], 0.0 });
            Rasterise(new GeoPackageReader.Ring(xyz.ToArray()), cls);
            rings++;
        }
        LayerRings["cover_overrides"] = rings;
    }

    /// <summary>
    /// Rasterises one polygonal layer, mapping its <c>objektart</c> through the supplied
    /// classifier. Returns the number of rings drawn.
    /// </summary>
    private int Rasterise(Microsoft.Data.Sqlite.SqliteConnection conn, string layer,
        Func<string?, CoverClass> classify, TileRegion region)
    {
        int rings = 0;
        foreach (var reader in GeoPackageReader.TileRows(conn, layer, new[] { "objektart" }, region))
        {
            var cls = classify(reader.IsDBNull(0) ? null : reader.GetString(0));
            if (cls == CoverClass.Open || reader.IsDBNull(1)) continue;

            foreach (var ring in GeoPackageReader.ParsePolygons((byte[])reader.GetValue(1)))
            {
                rings++;
                Rasterise(ring, cls);
            }
        }

        LayerRings[layer] = LayerRings.GetValueOrDefault(layer) + rings;
        return rings;
    }

    // The rings waiting to be painted, in source order, and what they hold: rings are read first and
    // painted a tile at a time. Bounded so a batch of big polygons cannot grow without end.
    private readonly record struct PendingRing(double[] Xyz, CoverClass Cls, double MinE, double MaxE, double MinN, double MaxN);
    private readonly List<PendingRing> _pending = new();
    private long _pendingCoords;
    private const long PendingCoordsLimit = 1L << 25;   // 256 MB of doubles

    /// <summary>
    /// Queues one ring (map coordinates, closed) for <see cref="PaintPending"/>. Every sample point is a
    /// terrain vertex, so cover and height line up index-for-index.
    /// </summary>
    private void Rasterise(GeoPackageReader.Ring ring, CoverClass cls)
    {
        int n = ring.Count;
        if (n < 4) return;

        double minE = double.MaxValue, maxE = double.MinValue;
        double minN = double.MaxValue, maxN = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double x = ring.Xyz[i * 3], y = ring.Xyz[i * 3 + 1];
            if (x < minE) minE = x;
            if (x > maxE) maxE = x;
            if (y < minN) minN = y;
            if (y > maxN) maxN = y;
        }
        _pending.Add(new PendingRing(ring.Xyz, cls, minE, maxE, minN, maxN));
        _pendingCoords += ring.Xyz.Length;
        if (_pendingCoords >= PendingCoordsLimit) PaintPending();
    }

    /// <summary>
    /// Paints the queued rings onto the tiles they touch. What a cell ends up as depends only on the
    /// order of the rings that paint it (<see cref="PaintRing"/>), and a tile's rings keep source
    /// order, so the tiles do not depend on each other.
    /// </summary>
    private void PaintPending()
    {
        var rings = new Dictionary<TileId, List<int>>();
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            // a tile holds its edges: its lattice runs from MinE to MinE + TileSizeM, both included
            int e0 = (int)Math.Ceiling(p.MinE / ChunkFormat.TileSizeM) - 1, e1 = (int)Math.Floor(p.MaxE / ChunkFormat.TileSizeM);
            int n0 = (int)Math.Ceiling(p.MinN / ChunkFormat.TileSizeM) - 1, n1 = (int)Math.Floor(p.MaxN / ChunkFormat.TileSizeM);
            for (int e = e0; e <= e1; e++)
                for (int n = n0; n <= n1; n++)
                {
                    var id = new TileId(e, n);
                    if (!Cover.ContainsKey(id)) continue;
                    if (!rings.TryGetValue(id, out var list)) rings[id] = list = new List<int>();
                    list.Add(i);
                }
        }
        var scratch = new RingScratch();
        foreach (var (id, list) in rings)
        {
            var cells = Cover[id];
            foreach (int i in list) PaintRing(_pending[i], id, cells, scratch);
        }
        _pending.Clear();
        _pendingCoords = 0;
    }

    /// <summary>One worker's arrays for <see cref="PaintRing"/>.</summary>
    private sealed class RingScratch
    {
        public readonly int[] RowStart = new int[CoverFormat.Size + 2];
        public int[] Edges = new int[256];
        public double[] Crossings = new double[1024];
    }

    /// <summary>
    /// Scanline fill of one ring on one tile's lattice, in whole metres: a sample row is a whole N and a
    /// sample column a whole E (<see cref="ChunkFormat.SpacingM"/> is 1), and the vertices on a tile's
    /// edge belong to both tiles, which is what keeps a river's water surface whole across a seam.
    /// Each edge is taken once, for the rows of this tile it crosses (half-open in N, so a shared
    /// vertex counts once); the crossings of a row come out of a counting sort by row.
    /// </summary>
    private static void PaintRing(in PendingRing ring, TileId tile, byte[] cells, RingScratch sc)
    {
        const int Last = CoverFormat.Size - 1;
        int tileW = tile.E * (int)ChunkFormat.TileSizeM, tileN = (tile.N + 1) * (int)ChunkFormat.TileSizeM, tileS = tileN - Last;
        var xyz = ring.Xyz;
        int edgeCount = xyz.Length / 3 - 1;
        var ends = sc.RowStart;   // row r's crossings end at ends[r + 1]
        Array.Clear(ends);

        int used = 0, total = 0;
        for (int i = 0; i < edgeCount; i++)
        {
            double y0 = xyz[i * 3 + 1], y1 = xyz[i * 3 + 4];
            if (y0 == y1) continue;
            double lo = Math.Min(y0, y1), hi = Math.Max(y0, y1);
            if (hi <= tileS || lo > tileN) continue;
            int nLo = Math.Max((int)Math.Ceiling(lo), tileS), nHi = Math.Min((int)Math.Ceiling(hi) - 1, tileN);
            if (nLo > nHi) continue;
            if (used == sc.Edges.Length) Array.Resize(ref sc.Edges, used * 2);
            sc.Edges[used++] = i;
            ends[tileN - nHi]++;       // row 0 is the north edge
            ends[tileN - nLo + 1]--;
            total += nHi - nLo + 1;
        }
        if (total < 2) return;
        if (total > sc.Crossings.Length) sc.Crossings = new double[Math.Max(total, sc.Crossings.Length * 2)];
        var xs = sc.Crossings;

        // the differences become each row's count, the counts each row's first slot
        int count = 0, first = 0;
        for (int r = 0; r <= Last; r++)
        {
            count += ends[r];
            ends[r] = first;
            first += count;
        }
        ends[Last + 1] = first;
        // filling row r moves ends[r] from its first slot to row r + 1's first slot: its end
        for (int u = 0; u < used; u++)
        {
            int i = sc.Edges[u];
            double y0 = xyz[i * 3 + 1], y1 = xyz[i * 3 + 4];
            double x0 = xyz[i * 3], x1 = xyz[i * 3 + 3];
            double lo = Math.Min(y0, y1), hi = Math.Max(y0, y1);
            int nLo = Math.Max((int)Math.Ceiling(lo), tileS), nHi = Math.Min((int)Math.Ceiling(hi) - 1, tileN);
            for (int n = nLo; n <= nHi; n++)
            {
                double sampleN = n;
                xs[ends[tileN - n]++] = x0 + (sampleN - y0) / (y1 - y0) * (x1 - x0);
            }
        }

        byte cls = (byte)ring.Cls;
        bool water = ring.Cls == CoverClass.Water;
        int from = 0;
        for (int r = 0; r <= Last; r++)
        {
            int to = ends[r];
            int crossings = to - from;
            var row = xs.AsSpan(from, crossings);
            from = to;
            if (crossings < 2) continue;
            row.Sort();
            int rowAt = r * CoverFormat.Size;
            for (int k = 0; k + 1 < crossings; k += 2)
            {
                int c0 = Math.Max((int)Math.Ceiling(row[k]) - tileW, 0);
                int c1 = Math.Min((int)Math.Floor(row[k + 1]) - tileW, Last);
                if (c1 < c0) continue;
                var span = cells.AsSpan(rowAt + c0, c1 - c0 + 1);
                if (water) span.Fill(cls);
                else
                {
                    // Water is never overwritten by a later, more specific layer.
                    //
                    // The layers are stamped in order of increasing specificity, which is right for land
                    // use: an allotment inside a park should win. But a land-use polygon is an
                    // administrative boundary, not a ground surface, and several of them are drawn right
                    // across a river: the gravel extraction areas beside the Rhone at Riddes are mapped as
                    // Abbauareal over the water, which erased the river from the raster and left the Rhone
                    // rendering as a gap with a thin channel line through it. A quarry does not flow.
                    foreach (ref byte cell in span)
                        if (cell != (byte)CoverClass.Water) cell = cls;
                }
            }
        }
    }

    /// <summary>
    /// Places trees on a jittered grid whose spacing comes from the class density, so
    /// forests look natural rather than gridded while staying deterministic per tile.
    /// </summary>
    private void ScatterTrees(IReadOnlyCollection<TileId> tiles, Func<double, double, double?> heightOf)
    {
        foreach (var tile in tiles)
        {
            var cells = Cover[tile];
            var list = TreeList(tile);
            // deterministic per tile so re-runs produce identical output
            var rng = new Random(tile.E * 73856093 ^ tile.N * 19349663);

            for (int row = 0; row < CoverFormat.Size; row++)
                for (int col = 0; col < CoverFormat.Size; col++)
                {
                    int cell = row * CoverFormat.Size + col;
                    var cls = (CoverClass)cells[cell];
                    if (!CoverFormat.IsWooded(cls)) continue;
                    if (RoadMask.TryGetValue(tile, out var blocked) && blocked[cell]) continue;

                    // each 2 m cell is 0.0004 ha; density is per hectare
                    float perCell = CoverFormat.TreeDensity(cls) * 0.0004f;
                    if (rng.NextDouble() > perCell) continue;

                    double e = tile.MinE + col * ChunkFormat.SpacingM + (rng.NextDouble() - 0.5) * 2.0;
                    double n = tile.MaxN - row * ChunkFormat.SpacingM + (rng.NextDouble() - 0.5) * 2.0;
                    double? h = heightOf(e, n);
                    if (h == null) continue;

                    float height = cls switch
                    {
                        CoverClass.Forest => 14f + (float)rng.NextDouble() * 10f,
                        CoverClass.OpenForest => 10f + (float)rng.NextDouble() * 8f,
                        CoverClass.Woodland => 7f + (float)rng.NextDouble() * 6f,
                        _ => 2.5f + (float)rng.NextDouble() * 2f,
                    };

                    list.Add(new TreeInstance(
                        (float)(e - tile.MinE), (float)h.Value, (float)(tile.MaxN - n),
                        height, cls == CoverClass.Shrub ? KindShrub : KindConifer));
                    ScatteredTrees++;
                }
        }
    }

    private List<TreeInstance> TreeList(TileId tile)
    {
        if (!Trees.TryGetValue(tile, out var list)) Trees[tile] = list = new List<TreeInstance>();
        return list;
    }

    /// <summary>
    /// Plants orchards and nurseries on a regular grid. These are cultivated rows, and a
    /// random scatter at the same density reads as scrub — the layout is the whole point.
    /// The grid is anchored to world coordinates so it stays continuous across tile seams.
    /// </summary>
    private void PlantRows(IReadOnlyCollection<TileId> tiles, Func<double, double, double?> heightOf)
    {
        foreach (var tile in tiles)
        {
            var cells = Cover[tile];
            var list = TreeList(tile);
            var rng = new Random(tile.E * 40503 ^ tile.N * 12289);
            RoadMask.TryGetValue(tile, out var blocked);

            foreach (var cls in new[] { CoverClass.Orchard, CoverClass.Nursery })
            {
                double step = CoverFormat.PlantingSpacing(cls);
                double startE = Math.Ceiling(tile.MinE / step) * step;
                double startN = Math.Ceiling(tile.MinN / step) * step;

                for (double e = startE; e < tile.MinE + ChunkFormat.TileSizeM; e += step)
                    for (double n = startN; n < tile.MinN + ChunkFormat.TileSizeM; n += step)
                    {
                        int col = (int)Math.Round((e - tile.MinE) / ChunkFormat.SpacingM);
                        int row = (int)Math.Round((tile.MaxN - n) / ChunkFormat.SpacingM);
                        if ((uint)col >= CoverFormat.Size || (uint)row >= CoverFormat.Size) continue;

                        int cell = row * CoverFormat.Size + col;
                        if ((CoverClass)cells[cell] != cls) continue;
                        if (blocked != null && blocked[cell]) continue;

                        double? h = heightOf(e, n);
                        if (h == null) continue;

                        // a little wobble, well under the row spacing, so the grid reads as
                        // planted rather than as a texture
                        float jitter = (float)((rng.NextDouble() - 0.5) * 0.6);
                        list.Add(new TreeInstance(
                            (float)(e - tile.MinE) + jitter, (float)h.Value,
                            (float)(tile.MaxN - n) + jitter,
                            cls == CoverClass.Orchard
                                ? 3.4f + (float)rng.NextDouble() * 1.4f
                                : 2.2f + (float)rng.NextDouble() * 1.0f,
                            KindFruit));
                        PlantedTrees++;
                    }
            }
        }
    }

    /// <summary>
    /// TLM surveys single trees outside the forest — in villages, along field boundaries
    /// and beside roads — as <c>tlm_bb_einzelbaum</c>. These are real positions rather than
    /// a scatter, and they are what makes open farmland stop looking like a bare heightmap.
    /// </summary>
    private void AddSurveyedTrees(Microsoft.Data.Sqlite.SqliteConnection conn,
        Func<double, double, double?> heightOf, TileRegion region)
    {
        foreach (var reader in GeoPackageReader.TileRows(conn, "tlm_bb_einzelbaum", new[] { "objektart" }, region))
        {
            if (reader.IsDBNull(1)) continue;
            foreach (var (e, n, _) in GeoPackageReader.ParsePoints((byte[])reader.GetValue(1)))
            {
                var tile = TileId.FromLv95(e, n);
                if (!Cover.ContainsKey(tile)) continue;

                int col = (int)Math.Round((e - tile.MinE) / ChunkFormat.SpacingM);
                int row = (int)Math.Round((tile.MaxN - n) / ChunkFormat.SpacingM);
                if ((uint)col >= CoverFormat.Size || (uint)row >= CoverFormat.Size) continue;

                // our road ribbon is 2 m-quantised and a little wider than the real
                // carriageway, so a surveyed roadside tree can still land on it
                if (RoadMask.TryGetValue(tile, out var blocked)
                    && blocked[row * CoverFormat.Size + col]) continue;

                double? h = heightOf(e, n);
                if (h == null) continue;

                // no height attribute in TLM; vary deterministically from the position so
                // an avenue is not a row of clones
                var rng = new Random((int)(e * 4) ^ ((int)(n * 4) << 8));
                TreeList(tile).Add(new TreeInstance(
                    (float)(e - tile.MinE), (float)h.Value, (float)(tile.MaxN - n),
                    9f + (float)rng.NextDouble() * 7f, KindSolitary));
                SurveyedTrees++;
            }
        }
    }
}
