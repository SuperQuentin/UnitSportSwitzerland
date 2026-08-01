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

        double minE = tiles.Min(t => t.MinE), maxE = tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        double minN = tiles.Min(t => t.MinN), maxN = tiles.Max(t => t.MinN) + ChunkFormat.TileSizeM;

        using var conn = GeoPackageReader.Open(_gpkgPath);

        // Layers are rasterised in order of increasing specificity: what a human would
        // name the ground wins over what grows on it. A car park stays last so it beats
        // everything, and the pitch polygon beats the sports ground that contains it.
        PolygonCount = Rasterise(conn, "tlm_bb_bodenbedeckung", CoverFormat.Parse,
            minE, minN, maxE, maxN);
        Rasterise(conn, "tlm_areale_nutzungsareal", CoverFormat.ParseLandUse,
            minE, minN, maxE, maxN);
        Rasterise(conn, "tlm_areale_freizeitareal", CoverFormat.ParseLeisure,
            minE, minN, maxE, maxN);
        Rasterise(conn, "tlm_bauten_sportbaute_ply", CoverFormat.ParseStructureArea,
            minE, minN, maxE, maxN);
        Rasterise(conn, "tlm_bauten_verkehrsbaute_ply", CoverFormat.ParseStructureArea,
            minE, minN, maxE, maxN);
        TrafficAreaCount = Rasterise(conn, "tlm_areale_verkehrsareal", CoverFormat.ParseTrafficArea,
            minE, minN, maxE, maxN);

        ScatterTrees(tiles, heightOf);
        PlantRows(tiles, heightOf);
        AddSurveyedTrees(conn, tiles, heightOf, minE, minN, maxE, maxN);

        // the three passes each ask for a list up front; drop the tiles that stayed bare
        foreach (var id in Trees.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            Trees.Remove(id);
    }

    /// <summary>
    /// Rasterises one polygonal layer, mapping its <c>objektart</c> through the supplied
    /// classifier. Returns the number of rings drawn.
    /// </summary>
    private int Rasterise(Microsoft.Data.Sqlite.SqliteConnection conn, string layer,
        Func<string?, CoverClass> classify, double minE, double minN, double maxE, double maxN)
    {
        int rings = 0;
        using var cmd = GeoPackageReader.BboxQuery(conn, layer,
            new[] { "objektart" }, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
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

    /// <summary>
    /// Scanline fill of one ring onto every tile lattice it touches. Rings are in map
    /// coordinates; each sample point is a terrain vertex, so cover and height line up
    /// index-for-index.
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

        double spacing = ChunkFormat.SpacingM;
        var xs = new List<double>(16);

        // walk sample rows (constant north) across the polygon's vertical extent
        double startN = Math.Floor(minN / spacing) * spacing;
        for (double sampleN = startN; sampleN <= maxN; sampleN += spacing)
        {
            xs.Clear();
            for (int i = 0; i < n - 1; i++)
            {
                double y0 = ring.Xyz[i * 3 + 1], y1 = ring.Xyz[(i + 1) * 3 + 1];
                if (y0 == y1) continue;
                // half-open test avoids double-counting shared vertices
                if (sampleN < Math.Min(y0, y1) || sampleN >= Math.Max(y0, y1)) continue;
                double x0 = ring.Xyz[i * 3], x1 = ring.Xyz[(i + 1) * 3];
                xs.Add(x0 + (sampleN - y0) / (y1 - y0) * (x1 - x0));
            }
            if (xs.Count < 2) continue;
            xs.Sort();

            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                double spanStart = Math.Ceiling(xs[k] / spacing) * spacing;
                for (double sampleE = spanStart; sampleE <= xs[k + 1]; sampleE += spacing)
                    Mark(sampleE, sampleN, cls);
            }
        }
    }

    /// <summary>
    /// Stamps one lattice vertex. A vertex on a tile boundary belongs to TWO tiles (four at
    /// a corner) because the edge row/column is shared, but <see cref="TileId.FromLv95"/>
    /// can only name one of them. Marking only that one leaves the neighbour's edge row
    /// unclassified, and since the water surface needs all four corners of a quad to be
    /// water, every tile seam that a river or lake crosses opens a 4 m gap.
    /// </summary>
    private void Mark(double e, double n, CoverClass cls)
    {
        int te = (int)Math.Floor(e / ChunkFormat.TileSizeM);
        int tn = (int)Math.Floor(n / ChunkFormat.TileSizeM);

        MarkIn(new TileId(te, tn), e, n, cls);
        // the west neighbour shares this vertex as its last column, the south neighbour as
        // its first row (row 0 is the north edge)
        MarkIn(new TileId(te - 1, tn), e, n, cls);
        MarkIn(new TileId(te, tn - 1), e, n, cls);
        MarkIn(new TileId(te - 1, tn - 1), e, n, cls);
    }

    private void MarkIn(TileId tile, double e, double n, CoverClass cls)
    {
        if (!Cover.TryGetValue(tile, out var cells)) return;

        double fc = (e - tile.MinE) / ChunkFormat.SpacingM;
        double fr = (tile.MaxN - n) / ChunkFormat.SpacingM;
        int col = (int)Math.Round(fc), row = (int)Math.Round(fr);
        if ((uint)col >= CoverFormat.Size || (uint)row >= CoverFormat.Size) return;
        // reject positions that only round onto this tile's lattice from outside it
        if (Math.Abs(fc - col) > 0.01 || Math.Abs(fr - row) > 0.01) return;

        int index = row * CoverFormat.Size + col;

        // Water is never overwritten by a later, more specific layer.
        //
        // The layers are stamped in order of increasing specificity, which is right for land
        // use — an allotment inside a park should win. But a land-use polygon is an
        // administrative boundary, not a ground surface, and several of them are drawn right
        // across a river: the gravel extraction areas beside the Rhône at Riddes are mapped as
        // Abbauareal over the water, which erased the river from the raster and left the Rhône
        // rendering as a gap with a thin channel line through it. A quarry does not flow.
        if ((CoverClass)cells[index] == CoverClass.Water && cls != CoverClass.Water) return;

        cells[index] = (byte)cls;
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
        IReadOnlyCollection<TileId> tiles, Func<double, double, double?> heightOf,
        double minE, double minN, double maxE, double maxN)
    {
        using var cmd = GeoPackageReader.BboxQuery(conn, "tlm_bb_einzelbaum",
            new[] { "objektart" }, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
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
