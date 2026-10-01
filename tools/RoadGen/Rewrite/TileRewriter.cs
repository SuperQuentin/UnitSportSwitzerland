namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Globalization;
using System.IO.Compression;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Rewrites already-built <c>.road</c> tiles in place: trims carriageways back from their
/// junctions, adds the junction polygons that fill the gap, and optionally smooths the
/// centrelines.
///
/// <para>
/// Deliberately a post-process rather than a change to <c>RoadExtractor</c>. That extractor
/// carries a lot of measured, hard-won behaviour — approach ramping onto bridge decks, tunnel
/// carve masks, per-class gradient limits, structure-end detection — and re-deriving
/// centrelines inside it would put all of that at risk for a defect that lives entirely in the
/// plan view. Running afterwards also means a change can be tried against a built region in
/// seconds instead of a fourteen-minute rebuild.
/// </para>
/// </summary>
public static class TileRewriter
{
    /// <param name="BlockSize">Tiles per side processed together.</param>
    /// <param name="Halo">
    /// Extra ring of tiles loaded for context but not written. Roads are clipped at tile
    /// boundaries, so a junction sitting on one is split across two files; without the halo
    /// each side sees a dead end and no junction is built at all.
    /// </param>
    public sealed record Options(
        int BlockSize = 6,
        int Halo = 1,
        bool Smooth = true,
        double SimplifyTolerance = 0.6,
        double ChordTolerance = 0.05,
        double DividedScale = 1.0,
        bool DryRun = false,
        bool Force = false,
        /// <summary>Run the overlap analysis and the before-comparison. Slow; off for production runs.</summary>
        bool Measure = false,
        /// <summary>Audit output heights against the real terrain. Needs the .terr files.</summary>
        bool AuditHeights = true,
        /// <summary>
        /// Largest height change smoothing may introduce at any one vertex, in metres. Past
        /// this the vertex is put back on the original line. See <see cref="ApplyCliffGuard"/>.
        /// </summary>
        double MaxHeightShift = 1.0,
        /// <summary>
        /// Where the raw extractor output lives (<see cref="RawRoads"/>); null =
        /// <c>&lt;chunks&gt;_temp/roads_raw</c>. The stage always reads from here, so a rerun
        /// rebuilds from the same input and writes the same bytes.
        /// </summary>
        string? RawDir = null,
        /// <summary><c>osm_overlay.tsv</c>; null or missing = no OSM attributes.</summary>
        string? OsmOverlay = null,
        /// <summary>Leave rewritten tiles that have no raw input alone instead of refusing.</summary>
        bool SkipRewritten = false);

    public sealed record Stats(
        int TilesRead, int TilesWritten, int Junctions, int SegmentsWritten, int SegmentsDropped,
        double OverlapBefore, double OverlapAfter, double CarriagewayArea,
        HeightAudit Heights, int VerticesReverted, NetworkStats Network);

    /// <summary>Region numbers for the v3 attributes (#115): counts are segments, km are 2D.</summary>
    public sealed class NetworkStats
    {
        public int Roads, OneWay, Divided, DividedResolved, Urban, Osm, Roundabout, OsmTiles;
        public double RoadKm, OneWayKm, UrbanKm, OsmKm;
        public long Bytes, DeflatedBytes;
        /// <summary>Overlap area per class pair (<c>--measure</c>), e.g. "motorway+motorway".</summary>
        public SortedDictionary<string, double> OverlapPairs = new(StringComparer.Ordinal);
        public readonly CrossSectionPlanner.Stats Carriageways = new();
        /// <summary>Terrain under each shifted carriageway vertex vs under the TLM line it came from.</summary>
        public HeightAudit Shifted = HeightAudit.Empty;
        public int MaxBytes;
        public string MaxBytesTile = "";
        public readonly PaintEmitter.Tally Paint = new();
        public readonly RailRoadOverlap.Tally Rail = new();

        public string Format(int tiles)
        {
            var c = CultureInfo.InvariantCulture;
            string Pct(int n, int of) => of == 0 ? "-" : (100.0 * n / of).ToString("F1", c) + "%";
            return string.Create(c, $"""
                  network (v3): {Roads:N0} road segments, {RoadKm:F1} km
                    one-way   {OneWay:N0} ({OneWayKm:F1} km)   divided with a direction {DividedResolved:N0}/{Divided:N0} ({Pct(DividedResolved, Divided)})
                    urban     {Urban:N0} ({UrbanKm:F1} km)   roundabout {Roundabout:N0}
                    OSM       {Osm:N0} ({OsmKm:F1} km) on {OsmTiles} tiles flagged OSM
                    bytes     {Bytes / 1024.0:F0} KB, {(double)Bytes / Math.Max(1, tiles) / 1024:F1} KB/tile, max {MaxBytes / 1024.0:F1} KB ({MaxBytesTile}), deflated on the wire {(double)DeflatedBytes / Math.Max(1, tiles) / 1024:F1} KB/tile
                """) + "\n" + Paint.Format(tiles) + "\n" + Carriageways.Format() + "\n" + Rail.Format();
        }
    }

    /// <summary>
    /// How much worse the smoothing made each road's fit to the ground.
    ///
    /// <para>
    /// The absolute distance from a road to the terrain is not the question — the extractor
    /// deliberately leaves approach ramps above the ground and structures well above it. What
    /// matters is the <i>change</i>: a smoothed centreline takes its height from the original
    /// line at the nearest point, so wherever smoothing moved the line across a slope, it
    /// carries a height from slightly the wrong place. On flat ground that is nothing. On the
    /// cliff-edge alpine paths where swissALTI3D drops 90 m between adjacent cells, it is the
    /// one thing that could go badly wrong.
    /// </para>
    /// </summary>
    public sealed record HeightAudit(
        int Samples, double WorstDelta, double MeanDelta, double P99Delta,
        double WorstDisplacement, string WorstWhere)
    {
        public static readonly HeightAudit Empty = new(0, 0, 0, 0, 0, "");

        public string Format()
        {
            if (Samples == 0) return "  heights: not audited (no .terr files found)";
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return $"""
                  heights vs terrain, change introduced by smoothing ({Samples:N0} samples)
                    mean {MeanDelta.ToString("F3", c)} m   p99 {P99Delta.ToString("F2", c)} m   worst {WorstDelta.ToString("F2", c)} m
                    largest plan-view shift {WorstDisplacement.ToString("F2", c)} m{(WorstWhere.Length > 0 ? "   worst at " + WorstWhere : "")}
                """;
        }
    }

    /// <summary>Carried on each link so the output can find its way home.</summary>
    private sealed class Source
    {
        public required TileId Tile { get; init; }
        public required RoadSegment Segment { get; init; }
        public required Vec2[] Plan { get; init; }     // LV95 plan view of the original
        public required float[] Height { get; init; }  // altitude at each original vertex
        public RawRoads.Key? Key { get; init; }         // TLM uuid/part/along-line, for OSM rows
        public required CrossSectionPlanner.Line Line { get; init; }  // width, lanes, one-way (#117)

        /// <summary>2D distance along the original to the point nearest <paramref name="p"/>.</summary>
        public double AlongOf(Vec2 p)
        {
            double best = double.MaxValue, result = 0, travelled = 0;
            for (int i = 1; i < Plan.Length; i++)
            {
                var a = Plan[i - 1];
                var ab = Plan[i] - a;
                double len = Math.Sqrt(ab.LengthSquared);
                double t = len < 1e-9 ? 0 : Math.Clamp((p - a).Dot(ab) / (len * len), 0, 1);
                double d = p.DistanceSquaredTo(a + ab * t);
                if (d < best) { best = d; result = travelled + t * len; }
                travelled += len;
            }
            return result;
        }

        /// <summary>
        /// Altitude at an arbitrary plan-view point, taken from the original polyline.
        ///
        /// <para>
        /// Heights are not recomputed from the terrain here on purpose. The originals already
        /// carry everything the extractor worked out — the drape, the surveyed bridge deck, the
        /// tunnel's own Z, the approach ramps blended into the abutments. Re-draping would throw
        /// all of that away and drop every viaduct into its gorge. Since smoothing moves a line
        /// by less than the simplify tolerance, reading the original's height at the nearest
        /// point keeps every one of those decisions intact.
        /// </para>
        /// </summary>
        public float SampleHeight(Vec2 p)
        {
            if (Plan.Length == 0) return 0;
            if (Plan.Length == 1) return Height[0];

            double best = double.MaxValue;
            float result = Height[0];

            for (int i = 1; i < Plan.Length; i++)
            {
                var a = Plan[i - 1];
                var ab = Plan[i] - a;
                double lenSq = ab.LengthSquared;
                double t = lenSq < 1e-18 ? 0 : Math.Clamp((p - a).Dot(ab) / lenSq, 0, 1);
                double d = p.DistanceSquaredTo(a + ab * t);
                if (d >= best) continue;
                best = d;
                result = (float)(Height[i - 1] + t * (Height[i] - Height[i - 1]));
            }

            return result;
        }
    }

    /// <summary>
    /// Thrown when the tiles have already been rewritten.
    ///
    /// <para>
    /// This pass is <b>not</b> idempotent, and the second run is destructive in a way that is
    /// not obvious: the roads are already trimmed back, so the trims computed the second time
    /// are nearly zero and the junction caps come out tiny — but they still replace the
    /// full-size caps written the first time. The result is a hole at every intersection. The
    /// only safe input is freshly extracted tiles.
    /// </para>
    /// </summary>
    public sealed class AlreadyRewrittenException(string message) : Exception(message);

    public static Stats Run(string chunkDir, IReadOnlyList<TileId> tiles, Options options, Action<string> log)
    {
        string rawDir = options.RawDir ?? RawRoads.DirFor(RawRoads.DefaultTempDir(chunkDir));

        // Input per tile: the raw extractor output if kept, else a fresh (never rewritten) tile
        // in the chunk dir. A rewritten tile with no raw input would be trimmed a second time.
        var targets = new List<TileId>();
        var rewritten = new List<TileId>();
        foreach (var id in tiles)
        {
            if (File.Exists(RawRoads.RoadPath(rawDir, id))) targets.Add(id);
            else if (!File.Exists(Path.Combine(chunkDir, RoadFormat.FileName(id)))) continue;
            else if (!options.Force && RawRoads.IsRewritten(Path.Combine(chunkDir, RoadFormat.FileName(id)))) rewritten.Add(id);
            else targets.Add(id);
        }
        if (rewritten.Count > 0 && !options.SkipRewritten)
            throw new AlreadyRewrittenException(
                $"{rewritten.Count} of {tiles.Count} tiles were already rewritten (v2, or v3 from the network\n"
                + $"stage) and have no raw input in {rawDir}.\n"
                + "Rewriting is not idempotent on its own output — a second pass trims roads that are\n"
                + "already trimmed and leaves a hole at every junction. Re-run the roads preprocessing\n"
                + "(it keeps the raw input), --skip-rewritten to leave them, or --force if you know better.");
        if (rewritten.Count > 0) log($"  skipping {rewritten.Count} rewritten tiles with no raw input");

        var overlay = options.OsmOverlay is { } overlayPath ? OsmOverlayReader.TryLoad(overlayPath) : null;
        if (overlay is not null) log($"  OSM overlay: {overlay.RowCount:N0} rows from {options.OsmOverlay}");

        var wanted = new HashSet<TileId>(targets);
        var blocks = GroupIntoBlocks(targets, options.BlockSize);

        int tilesRead = 0, tilesWritten = 0, junctionCount = 0, written = 0, dropped = 0;
        double overlapBefore = 0, overlapAfter = 0, carriageway = 0;
        int blockIndex = 0, guarded = 0;
        var audit = new HeightAuditor();
        var shiftAudit = new HeightAuditor();
        var netStats = new NetworkStats();
        var embankments = new EmbankmentPlanner.Stats();

        foreach (var block in blocks)
        {
            blockIndex++;
            var context = WithHalo(block, options.Halo);

            var net = new RoadNetwork();
            var lines = new List<CrossSectionPlanner.Line>();
            var loaded = new Dictionary<TileId, RoadTile>();
            var passthrough = new Dictionary<TileId, List<RoadSegment>>();

            foreach (var id in context)
            {
                var (tile, keys) = LoadInput(chunkDir, rawDir, id, stash: wanted.Contains(id) && !options.DryRun);
                if (tile is null) continue;
                loaded[id] = tile;
                if (block.Contains(id)) tilesRead++;

                for (int si = 0; si < tile.Segments.Count; si++)
                {
                    var segment = tile.Segments[si];
                    // Aerial ropeways and watercourses are carried in the same file but are not
                    // carriageways, and must not enter the graph. A cableway would be snapped to
                    // the road it flies over; a stream confluence would be handed a junction
                    // polygon and rendered as a patch of tarmac in the middle of a river.
                    if (RoadFormat.IsAerial(segment.Class) || RoadFormat.IsWatercourse(segment.Class)
                        || RoadFormat.IsWall(segment.Class))
                    {
                        if (!passthrough.TryGetValue(id, out var keep))
                            passthrough[id] = keep = new List<RoadSegment>();
                        keep.Add(segment);
                        continue;
                    }

                    lines.Add(new CrossSectionPlanner.Line
                        { Tile = id, Segment = segment, Key = keys?[si], Write = block.Contains(id) });
                }
            }

            CrossSectionPlanner.Plan(lines, overlay, netStats.Carriageways);
            foreach (var line in lines) AddSegment(net, line, options.DividedScale);

            var rails = new RailRoadOverlap(lines, netStats.Rail);
            var output = new Dictionary<TileId, List<RoadSegment>>();
            var caps = new Dictionary<TileId, List<RoadJunction>>();
            var paint = new Dictionary<TileId, List<RoadPaint>>();

            // the full-res terrain of the block and its halo: the height audit and the walls (#125)
            var grids = LoadGrids(chunkDir, context);

            if (net.Links.Count > 0)
            {
                if (options.Measure)
                {
                    var before = Pipeline.Run(CloneNetwork(net),
                        new PipelineOptions(Smooth: false, BuildJunctions: false));
                    overlapBefore += before.Report.OverlapArea;
                }

                var result = Pipeline.Run(net, new PipelineOptions(
                    Smooth: options.Smooth,
                    BuildJunctions: true,
                    SimplifyTolerance: options.SimplifyTolerance,
                    ChordTolerance: options.ChordTolerance,
                    Analyze: options.Measure));

                overlapAfter += result.Report.OverlapArea;
                foreach (var (pair, area) in result.Report.TopOverlapPairs ?? [])
                    netStats.OverlapPairs[pair] = netStats.OverlapPairs.GetValueOrDefault(pair) + area;
                carriageway += result.Report.CarriagewayArea;

                var terrain = options.AuditHeights ? grids : null;

                // Final plan of every ribbon, halo included: the halo's divided carriageways are
                // the partners the block's ones take their direction from.
                var plans = new List<(Source Source, List<Vec2> Plan, bool Write)>();
                foreach (var ribbon in result.Ribbons)
                {
                    var link = result.Network.Links[ribbon.LinkId];
                    if (link.Tag is not Source source) continue;
                    bool write = block.Contains(source.Tile);
                    if (ribbon.Stations.Count < 2) { if (write) dropped++; continue; }

                    var plan = ribbon.Stations.Select(s => s.Position).ToList();
                    if (write && terrain is not null && source.Line.IsShifted)
                        shiftAudit.Add(plan, source, terrain);   // moved on purpose: measured, never reverted
                    else if (write && terrain is not null && link.AllowSmoothing)
                    {
                        guarded += ApplyCliffGuard(plan, source, terrain, options.MaxHeightShift);
                        // structures are meant to sit above the ground, so auditing them against
                        // the terrain would measure the bridge, not the smoothing
                        audit.Add(plan, source, terrain);
                    }
                    plans.Add((source, plan, write));
                }

                foreach (var (source, plan, write) in plans)
                {
                    // rails inside a carriageway (#124); halo rails too, their track zone may reach into the block
                    var pieces = source.Segment.Class == RoadClass.Railway
                        ? rails.Split(plan, source.SampleHeight, source.Segment, count: write) : null;
                    if (!write) continue;
                    if (!output.TryGetValue(source.Tile, out var list))
                        output[source.Tile] = list = new List<RoadSegment>();
                    if (!paint.TryGetValue(source.Tile, out var painted)) paint[source.Tile] = painted = new List<RoadPaint>();
                    if (pieces is not null)
                    {
                        foreach (var piece in pieces)
                        {
                            var flags = source.Segment.Attributes.Flags | (piece.Embedded ? RoadAttrFlags.Embedded : 0);
                            list.Add(ToSegment(piece.Plan, source, source.Segment.Attributes with { Flags = flags }, piece.Height));
                            written++;
                            if (piece.Embedded) rails.EmitGrooves(piece, source.Segment, source.Tile, painted);
                        }
                        continue;
                    }

                    var attributes = CrossSectionPlanner.Attributes(source.Line);
                    if (overlay is not null && source.Key is { } key
                        && overlay.Best(key.Uuid, key.Part,
                            key.FromM + source.AlongOf(plan[0]), key.FromM + source.AlongOf(plan[^1])) is { } row)
                        attributes = CrossSectionPlanner.Finish(OsmOverlayReader.Apply(attributes, row), source.Line);

                    var segment = ToSegment(plan, source, attributes);
                    list.Add(segment);
                    written++;

                    PaintEmitter.Emit(segment, source.Key is { } k ? k.FromM + source.AlongOf(plan[0]) : 0, painted);
                }

                foreach (var junction in result.Junctions)
                {
                    var home = TileId.FromLv95(junction.Centre.X, junction.Centre.Y);
                    if (!block.Contains(home) || !wanted.Contains(home)) continue;

                    var record = ToJunction(junction, result.Network, home);
                    if (record is null) continue;

                    if (!caps.TryGetValue(home, out var list)) caps[home] = list = new List<RoadJunction>();
                    list.Add(record);
                    junctionCount++;
                }
            }

            foreach (var id in block)
            {
                if (!loaded.ContainsKey(id) || !wanted.Contains(id)) continue;

                var segments = output.TryGetValue(id, out var s) ? s : new List<RoadSegment>();
                var junctions = caps.TryGetValue(id, out var j) ? j : new List<RoadJunction>();

                // cableways and watercourses go back exactly as they came in
                if (passthrough.TryGetValue(id, out var kept)) segments.AddRange(kept);

                var flags = RoadTileFlags.Network;
                if (segments.Any(x => x.Attributes.Has(RoadAttrFlags.Osm))) flags |= RoadTileFlags.Osm;
                var walls = grids is null ? new List<RoadLinearProp>()
                    : EmbankmentPlanner.Plan(id, segments, (e, n) => SampleGround(grids, e, n), embankments);
                var tile = new RoadTile
                {
                    Id = id, Segments = segments, Junctions = junctions, Flags = flags,
                    Paint = paint.TryGetValue(id, out var p) ? p : new List<RoadPaint>(),
                    LinearProps = walls,
                };
                rails.ClearTrackZones(tile.Paint, id);
                var bytes = Encode(tile);
                Count(netStats, tile, bytes);

                if (options.DryRun) { tilesWritten++; continue; }

                string path = Path.Combine(chunkDir, RoadFormat.FileName(id));
                string temp = path + ".part";

                // write via a temp file: a half-written .road looks exactly like a valid short
                // one, and the region being rewritten is the region being played
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                tilesWritten++;
            }

            if (blockIndex % 10 == 0 || blockIndex == blocks.Count)
                log($"  block {blockIndex}/{blocks.Count}  {tilesWritten} tiles, {junctionCount} junctions");
        }

        netStats.Shifted = shiftAudit.Result();
        log(embankments.Format());
        return new Stats(tilesRead, tilesWritten, junctionCount, written, dropped,
            overlapBefore, overlapAfter, carriageway, audit.Result(), guarded, netStats);
    }

    /// <summary>
    /// The stage's input for one tile: the raw copy if kept, else the chunk dir's tile. A fresh
    /// chunk-dir tile (a region extracted before the raw dir existed) is stashed as raw first, so
    /// the next run has the same input.
    /// </summary>
    private static (RoadTile? Tile, RawRoads.Key?[]? Keys) LoadInput(string chunkDir, string rawDir, TileId id, bool stash)
    {
        string raw = RawRoads.RoadPath(rawDir, id);
        if (File.Exists(raw))
        {
            RoadTile rawTile;
            using (var stream = File.OpenRead(raw)) rawTile = RoadCodec.Decode(stream);
            return (rawTile, RawRoads.ReadKeys(rawDir, id, rawTile.Segments.Count));
        }

        string path = Path.Combine(chunkDir, RoadFormat.FileName(id));
        if (!File.Exists(path)) return (null, null);
        RoadTile tile;
        using (var stream = File.OpenRead(path)) tile = RoadCodec.Decode(stream);
        if (stash && !RawRoads.IsRewritten(path))
        {
            Directory.CreateDirectory(rawDir);
            File.Copy(path, raw);
        }
        return (tile, null);
    }

    /// <summary>The carriageways traffic drives and orients (<c>Traffic.IsCarRoad</c>), divided ones only.</summary>
    public static bool IsDividedCarRoad(RoadSegment s) =>
        (s.Flags & RoadFlags.Divided) != 0 && (s.Flags & RoadFlags.Stairs) == 0
        && s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp or RoadClass.Major
            or RoadClass.Road or RoadClass.Minor or RoadClass.Lane;

    private static byte[] Encode(RoadTile tile)
    {
        using var ms = new MemoryStream();
        RoadCodec.Encode(tile, ms);
        return ms.ToArray();
    }

    private static void Count(NetworkStats st, RoadTile tile, byte[] bytes)
    {
        st.Bytes += bytes.Length;
        if (bytes.Length > st.MaxBytes) { st.MaxBytes = bytes.Length; st.MaxBytesTile = $"{tile.Id.E}_{tile.Id.N}"; }
        // what ChunkStreamer sends: deflate, Fastest (AssetStream.TryCompress)
        using (var ms = new MemoryStream())
        {
            using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(bytes);
            st.DeflatedBytes += Math.Min(ms.Length, bytes.Length);
        }
        if ((tile.Flags & RoadTileFlags.Osm) != 0) st.OsmTiles++;
        st.Paint.Add(tile);

        foreach (var seg in tile.Segments)
        {
            if (seg.Class > RoadClass.Square) continue;   // roads only: no rail, water, walls, ropeways
            double km = 0;
            for (int i = 3; i < seg.Points.Length; i += 3)
            {
                double dx = seg.Points[i] - seg.Points[i - 3], dz = seg.Points[i + 2] - seg.Points[i - 1];
                km += Math.Sqrt(dx * dx + dz * dz) / 1000;
            }
            var a = seg.Attributes;
            st.Roads++; st.RoadKm += km;
            if (a.OneWay != 0) { st.OneWay++; st.OneWayKm += km; }
            if (IsDividedCarRoad(seg)) { st.Divided++; if (a.OneWay != 0) st.DividedResolved++; }
            if (a.Has(RoadAttrFlags.Urban)) { st.Urban++; st.UrbanKm += km; }
            if (a.Has(RoadAttrFlags.Osm)) { st.Osm++; st.OsmKm += km; }
            if (a.Has(RoadAttrFlags.Roundabout)) st.Roundabout++;
        }
    }

    /// <summary>
    /// Puts a vertex back on the original line wherever smoothing moved it across ground steep
    /// enough to matter. Returns how many were reverted.
    ///
    /// <para>
    /// This is the one repair that has to happen here rather than in the geometry engine, and
    /// the reason is that the engine is deliberately terrain-free — it solves everything in plan
    /// view. Only the rewriter holds both the smoothed line and the heightfield at once.
    /// </para>
    ///
    /// <para>
    /// It is worth doing because the risk is extremely concentrated. Across the whole region the
    /// mean height change from smoothing is 8 mm and the 99th percentile is 9 cm, but the worst
    /// case is 12 m: a footpath surveyed on a cliff lip, moved less than half a metre, where
    /// swissALTI3D drops tens of metres between adjacent cells. Reverting exactly those vertices
    /// keeps the smoothing everywhere it is safe and costs nothing anywhere else.
    /// </para>
    /// </summary>
    private static int ApplyCliffGuard(List<Vec2> plan, Source source,
        Dictionary<TileId, ChunkGrid> terrain, double maxShift)
    {
        if (maxShift <= 0) return 0;
        int reverted = 0;

        for (int i = 0; i < plan.Count; i++)
        {
            var q = HeightAuditor.NearestOnOriginal(source, plan[i], out double moved);
            if (moved < 1e-3) continue;

            double here = HeightAuditor.Sample(terrain, plan[i]);
            double there = HeightAuditor.Sample(terrain, q);
            if (double.IsNaN(here) || double.IsNaN(there)) continue;
            if (Math.Abs(here - there) <= maxShift) continue;

            plan[i] = q;    // back onto the surveyed line, where its height is genuinely from
            reverted++;
        }

        return reverted;
    }

    private static double SampleGround(Dictionary<TileId, ChunkGrid> grids, double e, double n) =>
        grids.TryGetValue(TileId.FromLv95(e, n), out var grid) ? grid.SampleHeight(e, n) : double.NaN;

    private static Dictionary<TileId, ChunkGrid>? LoadGrids(string chunkDir, IEnumerable<TileId> tiles)
    {
        var grids = new Dictionary<TileId, ChunkGrid>();
        foreach (var id in tiles)
        {
            string path = Path.Combine(chunkDir, ChunkFormat.ChunkFileName(id));
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                grids[id] = ChunkCodec.Decode(stream);
            }
            catch (Exception)
            {
                // a tile that will not decode is the terrain pipeline's problem, not this pass's
            }
        }
        return grids.Count == 0 ? null : grids;
    }

    /// <summary>
    /// Accumulates, for every smoothed vertex, how much its height moved relative to what the
    /// original polyline sat at over the same ground.
    /// </summary>
    private sealed class HeightAuditor
    {
        private readonly List<double> _deltas = new();
        private double _worstDelta, _worstDisplacement;
        private string _worstWhere = "";

        /// <summary>
        /// An output vertex at <c>p</c> takes its height from the nearest point <c>q</c> on the
        /// original line. So the error smoothing introduced is precisely how much the ground
        /// differs between where the road now is and where its height came from —
        /// <c>|terrain(p) − terrain(q)|</c>. Nothing else needs to be modelled, and comparing
        /// absolute road-to-ground distances instead is meaningless on a cliff, where the two
        /// points sit on terrain tens of metres apart.
        /// </summary>
        public void Add(List<Vec2> plan, Source source, Dictionary<TileId, ChunkGrid> terrain)
        {
            foreach (var p in plan)
            {
                var q = NearestOnOriginal(source, p, out double displacement);

                double groundHere = Sample(terrain, p);
                double groundThere = Sample(terrain, q);
                if (double.IsNaN(groundHere) || double.IsNaN(groundThere)) continue;

                double introduced = Math.Abs(groundHere - groundThere);
                _deltas.Add(introduced);

                if (introduced > _worstDelta)
                {
                    _worstDelta = introduced;
                    _worstWhere = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"LV95 {p.X:F0}/{p.Y:F0} ({source.Segment.Class}, moved {displacement:F2} m)");
                }
                if (displacement > _worstDisplacement) _worstDisplacement = displacement;
            }
        }

        internal static double Sample(Dictionary<TileId, ChunkGrid> terrain, Vec2 p)
        {
            var id = TileId.FromLv95(p.X, p.Y);
            return terrain.TryGetValue(id, out var grid) ? grid.SampleHeight(p.X, p.Y) : double.NaN;
        }

        internal static Vec2 NearestOnOriginal(Source source, Vec2 p, out double distance)
        {
            distance = double.MaxValue;
            var best = p;

            for (int i = 1; i < source.Plan.Length; i++)
            {
                var a = source.Plan[i - 1];
                var ab = source.Plan[i] - a;
                double lenSq = ab.LengthSquared;
                double t = lenSq < 1e-18 ? 0 : Math.Clamp((p - a).Dot(ab) / lenSq, 0, 1);
                var projected = a + ab * t;
                double d = p.DistanceTo(projected);
                if (d >= distance) continue;
                distance = d;
                best = projected;
            }

            if (distance == double.MaxValue) distance = 0;
            return best;
        }

        public HeightAudit Result()
        {
            if (_deltas.Count == 0) return HeightAudit.Empty;

            _deltas.Sort();
            double mean = _deltas.Average();
            double p99 = _deltas[Math.Min(_deltas.Count - 1, (int)(_deltas.Count * 0.99))];
            return new HeightAudit(_deltas.Count, _worstDelta, mean, p99, _worstDisplacement, _worstWhere);
        }
    }

    private static void AddSegment(RoadNetwork net, CrossSectionPlanner.Line line, double dividedScale)
    {
        var (id, segment) = (line.Tile, line.Segment);
        if (segment.PointCount < 2) return;

        var height = new float[segment.PointCount];
        for (int i = 0; i < segment.PointCount; i++) height[i] = segment.Points[i * 3 + 1];

        // heights stay the original line's (nearest point), the plan may be the shifted one
        var source = new Source { Tile = id, Segment = segment, Plan = line.Plan, Height = height, Key = line.Key, Line = line };
        var centreline = Polyline.Dedupe(line.Shifted.ToList());
        if (centreline.Count < 2) return;

        bool structure = (segment.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0;
        int layer = (segment.Flags & RoadFlags.Bridge) != 0 ? 1
            : (segment.Flags & RoadFlags.Tunnel) != 0 ? -1 : 0;
        // Rails get their own layers (#124): a level crossing is not a junction. Noding it trimmed
        // both lines back from a road-coloured cap and left the track with a gap there.
        if (segment.Class == RoadClass.Railway) layer += RailLayer;

        net.AddLink(centreline, ProfileFor(segment, line.Width, dividedScale), layer, source, allowSmoothing: !structure);
    }

    /// <summary>Added to a rail link's layer so rails never node with roads (see <see cref="AddSegment"/>).</summary>
    private const int RailLayer = 100;

    private static RoadSegment ToSegment(List<Vec2> plan, Source source, RoadAttributes attributes, float[]? heights = null)
    {
        var id = source.Tile;
        var points = new float[plan.Count * 3];
        for (int i = 0; i < plan.Count; i++)
        {
            points[i * 3] = (float)(plan[i].X - id.MinE);
            points[i * 3 + 1] = heights?[i] ?? source.SampleHeight(plan[i]);
            points[i * 3 + 2] = (float)(id.MaxN - plan[i].Y);
        }

        return new RoadSegment
        {
            Class = source.Segment.Class,
            Surface = source.Segment.Surface,
            Flags = source.Segment.Flags,
            Width = source.Line.Width,
            Points = points,
            Attributes = attributes,
        };
    }

    /// <summary>
    /// Converts a junction cap to tile-local geometry, taking each vertex's height from the arms
    /// that meet there by inverse-distance weighting.
    ///
    /// <para>
    /// Weighting by inverse square distance is what makes the seam invisible: a vertex sitting
    /// on an arm end is at distance zero from that arm and so takes its height exactly, while
    /// the fillet points between two arms blend across. Averaging the arms instead would leave
    /// every approach stepping into the junction by half the height difference.
    /// </para>
    /// </summary>
    private static RoadJunction? ToJunction(Junction junction, RoadNetwork net, TileId id)
    {
        if (junction.Vertices.Count < 3 || junction.Triangles.Count < 3) return null;
        if (junction.Vertices.Count > ushort.MaxValue) return null;

        var anchors = new List<(Vec2 At, float Height)>();
        RoadClass dominant = RoadClass.Unknown;
        int bestPriority = int.MinValue;

        foreach (var arm in junction.Arms)
        {
            var link = net.Links[arm.LinkId];
            if (link.Tag is not Source source) continue;

            var mid = (arm.Left + arm.Right) * 0.5;
            anchors.Add((mid, source.SampleHeight(mid)));

            if (link.Profile.Priority > bestPriority)
            {
                bestPriority = link.Profile.Priority;
                dominant = source.Segment.Class;
            }
        }

        if (anchors.Count == 0) return null;

        var vertices = new float[junction.Vertices.Count * 3];
        for (int i = 0; i < junction.Vertices.Count; i++)
        {
            var v = junction.Vertices[i];
            vertices[i * 3] = (float)(v.X - id.MinE);
            vertices[i * 3 + 1] = HeightAt(anchors, v);
            vertices[i * 3 + 2] = (float)(id.MaxN - v.Y);
        }

        var indices = new ushort[junction.Triangles.Count];
        for (int i = 0; i < junction.Triangles.Count; i++)
            indices[i] = (ushort)junction.Triangles[i];

        return new RoadJunction
        {
            Class = dominant,
            Layer = (sbyte)(junction.Layer > RailLayer / 2 ? junction.Layer - RailLayer : junction.Layer),
            Vertices = vertices,
            Indices = indices,
        };
    }

    private static float HeightAt(List<(Vec2 At, float Height)> anchors, Vec2 p)
    {
        double weightSum = 0, valueSum = 0;
        foreach (var (at, height) in anchors)
        {
            double d2 = p.DistanceSquaredTo(at);
            if (d2 < 1e-6) return height;           // exactly on an arm end: take it verbatim
            double w = 1.0 / d2;
            weightSum += w;
            valueSum += w * height;
        }
        return weightSum < 1e-12 ? anchors[0].Height : (float)(valueSum / weightSum);
    }

    private static RoadProfile ProfileFor(RoadSegment segment, float plannedWidth, double dividedScale)
    {
        var profile = segment.Class switch
        {
            RoadClass.Motorway => RoadProfile.Motorway,
            RoadClass.Expressway => RoadProfile.Expressway,
            RoadClass.Ramp => RoadProfile.Ramp,
            RoadClass.Major => RoadProfile.Major,
            RoadClass.Road => RoadProfile.Road,
            RoadClass.Minor => RoadProfile.Minor,
            RoadClass.Lane or RoadClass.Link or RoadClass.Square => RoadProfile.Lane,
            RoadClass.Track => RoadProfile.Track,
            RoadClass.Path => RoadProfile.Path,
            RoadClass.Railway => RoadProfile.Railway,
            _ => RoadProfile.Lane,
        };

        double width = plannedWidth > 0.1 ? plannedWidth : profile.Width;
        if ((segment.Flags & RoadFlags.Divided) != 0) width *= dividedScale;

        return profile with { Width = width };
    }

    /// <summary>A shallow copy for the before-measurement, since the pipeline mutates its input.</summary>
    private static RoadNetwork CloneNetwork(RoadNetwork net)
    {
        var copy = new RoadNetwork();
        foreach (var link in net.Links)
            copy.AddLink(new List<Vec2>(link.Centreline), link.Profile, link.Layer,
                link.Tag, link.AllowSmoothing);
        return copy;
    }

    private static List<HashSet<TileId>> GroupIntoBlocks(IReadOnlyList<TileId> tiles, int size)
    {
        var blocks = new Dictionary<(int, int), HashSet<TileId>>();
        foreach (var id in tiles)
        {
            var key = ((int)Math.Floor(id.E / (double)size), (int)Math.Floor(id.N / (double)size));
            if (!blocks.TryGetValue(key, out var set)) blocks[key] = set = new HashSet<TileId>();
            set.Add(id);
        }
        return blocks.Values.ToList();
    }

    private static HashSet<TileId> WithHalo(HashSet<TileId> block, int halo)
    {
        var result = new HashSet<TileId>(block);
        foreach (var id in block)
            for (int de = -halo; de <= halo; de++)
            for (int dn = -halo; dn <= halo; dn++)
                result.Add(new TileId(id.E + de, id.N + dn));
        return result;
    }
}
