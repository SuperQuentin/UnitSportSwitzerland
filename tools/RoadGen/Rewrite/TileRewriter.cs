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
public static partial class TileRewriter
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
        /// <summary>Per part of the file ("v2" = header, segments, junctions; then each v3 section tag): raw bytes, and deflated alone.</summary>
        public readonly SortedDictionary<string, (long Raw, long Deflated)> Parts = new(StringComparer.Ordinal);
        /// <summary>Paint stored as a segment reference / as geometry, and primitives the decoder rebuilt differently (must be 0).</summary>
        public int PaintRefs, PaintGeometry, PaintMismatch;
        /// <summary>Overlap area per class pair (<c>--measure</c>), e.g. "motorway+motorway".</summary>
        public SortedDictionary<string, double> OverlapPairs = new(StringComparer.Ordinal);
        public readonly CrossSectionPlanner.Stats Carriageways = new();
        public readonly RoundaboutShaper.Stats Roundabouts = new();
        public readonly TurnLaneStats TurnLanes = new();
        public readonly SignalStats Signals = new();
        /// <summary>Terrain under each shifted carriageway vertex vs under the TLM line it came from.</summary>
        public HeightAudit Shifted = HeightAudit.Empty;
        public int MaxBytes;
        public string MaxBytesTile = "";
        public readonly PaintEmitter.Tally Paint = new();
        public readonly RailRoadOverlap.Tally Rail = new();
        public readonly PriorityPlanner.Stats Priority = new();
        public readonly BikePlanner.Stats Bikes = new();

        private string FormatParts(int tiles) => "    parts     " + string.Join(", ", Parts.Select(kv =>
            string.Create(CultureInfo.InvariantCulture,
                $"{kv.Key} {kv.Value.Raw / 1024.0 / Math.Max(1, tiles):F2}/{kv.Value.Deflated / 1024.0 / Math.Max(1, tiles):F2}")))
            + " KB/tile raw/deflated alone\n"
            + $"    paint encoding: {PaintRefs:N0} lines along a segment, {PaintGeometry:N0} as geometry, {PaintMismatch} decoded differently";

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
                """) + "\n" + FormatParts(tiles) + "\n" + Paint.Format(tiles) + "\n" + Carriageways.Format() + "\n" + Roundabouts.Format() + "\n" + Rail.Format() + "\n" + Priority.Format() + "\n" + TurnLanes.Format() + "\n" + Signals.Format() + "\n" + Bikes.Format();
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
        var cantons = Cantons.Find();   // pedestrian heads per canton (#348)
        // OSM traffic signals (#347), beside the overlay
        var signalSites = SignalSites.From(options.OsmOverlay is { } nodesBeside
            ? OsmNodesReader.TryLoad(Path.Combine(Path.GetDirectoryName(nodesBeside) ?? ".", OsmNodesReader.FileName)) : null);
        if (signalSites is not null) log($"  OSM traffic signals: {signalSites.Count:N0} junction signals");
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
        var streetStats = new StreetPlanner.Stats();
        var cornerStats = new CornerPlanner.Stats();
        var heightStats = new RoadHeights.Stats();
        var railings = new RailingPlanner.Stats();
        var buildings = new Footprints(chunkDir);

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
            // road heights against the ground (#119), before anything reads them
            var facades = new Facades(chunkDir);   // building walls, for the streets
            var field = new UrbanField(facades);
            RoadHeights.Apply(lines, field, heightStats, count: true);
            BikePlanner.PlanLines(lines, field, netStats.Bikes);   // bike lanes (#120), before the junctions read the lines
            // roundabout rings rebuilt as arcs before anything is built from them (#122)
            var islands = new Dictionary<TileId, List<RoadAreaProp>>();
            foreach (var ring in RoundaboutShaper.Shape(lines, netStats.Roundabouts))
            {
                var at = TileId.FromLv95(ring.Centre.X, ring.Centre.Y);
                if (!block.Contains(at) || RoundaboutShaper.Island(ring, at) is not { } island) continue;
                if (!islands.TryGetValue(at, out var list)) islands[at] = list = new List<RoadAreaProp>();
                list.Add(island);
                netStats.Roundabouts.Islands++;
            }
            foreach (var line in lines) AddSegment(net, line, options.DividedScale);

            var rails = new RailRoadOverlap(lines, netStats.Rail);
            var output = new Dictionary<TileId, List<RoadSegment>>();
            var caps = new Dictionary<TileId, List<RoadJunction>>();
            var paint = new Dictionary<TileId, List<RoadPaint>>();
            var signs = new Dictionary<TileId, List<RoadPointProp>>();
            var signalRecords = new Dictionary<TileId, List<RoadSignal>>();   // traffic lights (#348)
            var bikeBridges = new Dictionary<TileId, List<(RoadAreaProp Band, List<Vec2> Ring)>>();   // paths through junctions (#120)

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
                    Analyze: options.Measure,
                    JoinNearEnds: MayJoinNearEnd));
                netStats.Priority.NearEndsJoined += result.NearEndsJoined;
                // traffic lights: from OSM where the overlay covers a junction, else where two main
                // roads cross in a dense core (#348)
                var priority = PlanPriority(result, j => Lights(j, result.Network, field, signalSites,
                    block.Contains(TileId.FromLv95(j.Centre.X, j.Centre.Y)) ? netStats.Signals : null), netStats.Signals);
                var bikeLayouts = BikePlanner.StrokeLayouts(result.Network, BikeStrokeKey);   // one path layout per street (#120)
                var trackPaint = new List<(RoadSegment Segment, TileId Tile, bool Start, bool End)>();
                var lanePaint = new List<(RoadSegment Segment, TileId Tile, double Station, bool Start, bool End)>();

                overlapAfter += result.Report.OverlapArea;
                foreach (var (pair, area) in result.Report.TopOverlapPairs ?? [])
                    netStats.OverlapPairs[pair] = netStats.OverlapPairs.GetValueOrDefault(pair) + area;
                carriageway += result.Report.CarriagewayArea;

                var terrain = options.AuditHeights ? grids : null;

                // Final plan of every ribbon, halo included: the halo's divided carriageways are
                // the partners the block's ones take their direction from.
                var plans = new List<(Source Source, List<Vec2> Plan, bool Write, int LinkId)>();
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
                    plans.Add((source, plan, write, link.Id));
                }

                var segmentOf = new Dictionary<int, (RoadSegment, TileId, RoadSegment)>();   // turn lanes (#123)
                // what a sidewalk stops at (#119): every ground-level line of the block and its halo
                var obstacles = new StreetPlanner.Obstacles();
                for (int k = 0; k < plans.Count; k++)
                {
                    var seg = plans[k].Source.Segment;
                    if ((seg.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) continue;
                    // a track laid in the street (auf_strasse) is inside the carriageway, not beside it
                    if (seg.Class == RoadClass.Railway && seg.Attributes.Has(RoadAttrFlags.OnStreet)) continue;
                    obstacles.Add(plans[k].Plan, plans[k].Source.Line.Width * 0.5, k, StreetPlanner.IsCandidate(seg),
                        footway: seg.Class is RoadClass.Path or RoadClass.Track,
                        carriageway: seg.Class <= RoadClass.Lane || seg.Class == RoadClass.Railway);
                }
                foreach (var (tileId, kept) in passthrough)
                    foreach (var seg in kept)
                    {
                        if (RoadFormat.IsWall(seg.Class)) obstacles.Add(seg, tileId, 0.3, -1);
                        else if (RoadFormat.IsWatercourse(seg.Class) && (seg.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) == 0)
                            obstacles.Add(seg, tileId, seg.Width * 0.5, -1);
                    }
                // tunnel mouths of the block (#119): their approach roads run between walls as wide as the bore
                var mouths = new List<(Vec2 At, Vec2 Out, double Half)>();
                foreach (var (src, pl, _, _) in plans)
                {
                    var ts = src.Segment;
                    if ((ts.Flags & RoadFlags.Tunnel) == 0 || ts.Class > RoadClass.Square || pl.Count < 2) continue;
                    foreach (bool atStart in new[] { true, false })
                    {
                        var end = atStart ? pl[0] : pl[^1];
                        var inner = end;
                        for (int q = 1; q < pl.Count && inner.DistanceTo(end) < 3; q++) inner = atStart ? pl[q] : pl[pl.Count - 1 - q];
                        var dir = end - inner;
                        if (dir.Length < 1e-6) continue;
                        mouths.Add((end, dir / dir.Length, RoadFormat.TunnelWidth(ts.Class) * 0.5));
                    }
                }
                var streetPieces = new Dictionary<RoadSegment, List<RoadSegment>>(ReferenceEqualityComparer.Instance);
                var streets = new StreetPlanner(facades, field, obstacles, streetStats,
                    grids is null ? null : (e, n) => SampleGround(grids, e, n));

                for (int k = 0; k < plans.Count; k++)
                {
                    var (source, plan, write, linkId) = plans[k];
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
                            bool bed = !piece.Embedded && IsTownTram(source.Segment, piece.Plan, field);
                            var flags = source.Segment.Attributes.Flags | (piece.Embedded ? RoadAttrFlags.Embedded : 0)
                                | (bed ? RoadAttrFlags.PavedBed : 0);
                            list.Add(ToSegment(piece.Plan, source, source.Segment.Attributes with { Flags = flags }, piece.Height));
                            written++;
                            if (piece.Embedded) rails.EmitGrooves(piece, source.Segment, source.Tile, painted);
                            else if (bed) rails.EmitGrooves(piece, source.Segment, source.Tile, painted, ownHeight: true);
                        }
                        continue;
                    }
                    if (source.Segment.Class == RoadClass.Railway && IsTownTram(source.Segment, plan, field))
                    {
                        var heights = plan.Select(source.SampleHeight).ToArray();
                        var bedAttributes = source.Segment.Attributes with { Flags = source.Segment.Attributes.Flags | RoadAttrFlags.PavedBed };
                        list.Add(ToSegment(plan, source, bedAttributes, heights));
                        written++;
                        rails.EmitGrooves(new RailRoadOverlap.Piece(plan, heights, false), source.Segment, source.Tile, painted, ownHeight: true);
                        continue;
                    }

                    var attributes = CrossSectionPlanner.Attributes(source.Line);
                    if (overlay is not null && source.Key is { } key
                        && overlay.Best(key.Uuid, key.Part,
                            key.FromM + source.AlongOf(plan[0]), key.FromM + source.AlongOf(plan[^1])) is { } row)
                        attributes = CrossSectionPlanner.Finish(OsmOverlayReader.Apply(attributes, row), source.Line);

                    attributes = attributes with { Flags = attributes.Flags | priority.FlagsOf(linkId) };   // #121
                    var segment = ToSegment(plan, source, attributes);
                    // sidewalks (#119): the segment is planned here (its paint follows the built-up
                    // rules) but written whole until the turn lanes (#123) have read it; then it is cut
                    // where its cross-section changes. Paint is laid on the whole line, so dashes run on
                    // across the cuts.
                    // separated bike paths (#120): a street that wants one, in its street's layout
                    bool wantsPath = source.Line.BikeWanted || attributes.Left.HasTrack || attributes.Right.HasTrack;
                    var street = streets.Plan(segment, source.Tile, k, out bool urban, out bool track,
                        new StreetPlanner.BikeRequest(wantsPath ? bikeLayouts.GetValueOrDefault(linkId) : 0, netStats.Bikes));
                    if (mouths.Count > 0) street = street.Select(piece => RampShoulders(piece, source.Tile, mouths)).ToList();
                    list.Add(segment);
                    if (street.Count != 1 || !ReferenceEquals(street[0], segment)) streetPieces[segment] = street;
                    written += street.Count;

                    var link = result.Network.Links[linkId];
                    bool startsAtJunction = EndsAtJunction(result.Network, link.StartNode), endsAtJunction = EndsAtJunction(result.Network, link.EndNode);
                    var paintAttributes = attributes;
                    if (urban) paintAttributes = paintAttributes with { Flags = paintAttributes.Flags | RoadAttrFlags.Urban };
                    if (track)   // a street that got its paths has no painted lanes
                        paintAttributes = paintAttributes with { Left = NoLane(paintAttributes.Left), Right = NoLane(paintAttributes.Right) };
                    var paintOn = paintAttributes == attributes ? segment : ToSegment(plan, source, paintAttributes);
                    segmentOf[linkId] = (segment, source.Tile, paintOn);   // turn lanes find the lines on paintOn (#325)
                    double station = source.Key is { } at ? at.FromM + source.AlongOf(plan[0]) : 0;
                    PaintEmitter.Emit(paintOn, station, painted, startsAtJunction, endsAtJunction, bikeLanes: false);
                    if (paintAttributes.Left.HasLane || paintAttributes.Right.HasLane)   // on the final pieces (#120)
                        lanePaint.Add((segment, source.Tile, station, startsAtJunction, endsAtJunction));
                    if (track || attributes.Left.HasTrack || attributes.Right.HasTrack)
                        trackPaint.Add((segment, source.Tile, startsAtJunction, endsAtJunction));
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

                EmitPriority(priority, result, block, wanted, grids, buildings, paint, signs, netStats.Priority);
                var bikeBetween = new List<(RoadSegment Segment, bool Right, (double From, double To) Along)>();
                var stripOwners = new Dictionary<RoadAreaProp, RoadSegment>(ReferenceEqualityComparer.Instance);
                var pockets = EmitTurnLanes(priority, result, segmentOf, output, block, wanted, grids, buildings, paint, islands, signs,
                    bikeBetween, stripOwners, netStats.TurnLanes);
                // the bike side of a link's end piece (#351): its separated path, else its painted lane
                RoadSide BikeSideAt(int linkId, LinkEnd end, bool right)
                {
                    if (!segmentOf.TryGetValue(linkId, out var so)) return default;
                    var (whole, _, painted) = so;
                    var ends = streetPieces.TryGetValue(whole, out var cut) && cut.Count > 0 ? cut : [whole];
                    var piece = end == LinkEnd.Start ? ends[0] : ends[^1];
                    var side = right ? piece.Attributes.Right : piece.Attributes.Left;
                    if (side.HasTrack) return side;
                    var lane = right ? painted.Attributes.Right : painted.Attributes.Left;
                    return lane.HasLane ? lane : default;
                }
                EmitSignals(priority, result, pockets, BikeSideAt, block, wanted, paint, signalRecords, cantons, field, buildings, islands, signs, netStats.Signals);
                EmitRightLanes(pockets, paint, bikeBetween, netStats.TurnLanes);

                // now the streets are cut into their sidewalk pieces (#119); a side that would stand on
                // a turn lane's widening (#123) moves out past it (#120): its own street's widenings
                // only (#351: at a signalised junction an arm's widening reaches the next arm's corner)
                var finalPieces = new Dictionary<RoadSegment, List<RoadSegment>>(ReferenceEqualityComparer.Instance);
                var stripsOf = stripOwners.GroupBy(kv => kv.Value, ReferenceEqualityComparer.Instance)
                    .ToDictionary(g => (RoadSegment)g.Key!, g => g.Select(kv => kv.Key).ToList(), ReferenceEqualityComparer.Instance);
                foreach (var (tileId, list) in output)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                        if (streetPieces.TryGetValue(list[i], out var pieces))
                        {
                            var strips = stripsOf.TryGetValue(list[i], out var own) ? own : null;
                            var final = strips is null ? pieces : pieces.SelectMany(x => ShiftOffPavement(x, strips, netStats.Bikes)).ToList();
                            finalPieces[list[i]] = final;
                            list.RemoveAt(i);
                            list.InsertRange(i, final);
                        }
                }

                // the paths' paint on their final pieces, and the crossings at the junctions (#120)
                foreach (var (segment, tileId, station, start, end) in lanePaint)
                {
                    var pieces = finalPieces.TryGetValue(segment, out var cut) ? cut : [segment];
                    // layout (b) of a right-turn pocket (#351): along its reach the pocket painted the bike lane
                    var between = bikeBetween.Where(b => ReferenceEquals(b.Segment, segment)).ToList();
                    double s = station;
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        double length = RoadPaintGeometry.Length(pieces[i].Points), mid = s - station + length * 0.5;
                        bool Inside(bool right) => between.Any(b => b.Right == right && mid > b.Along.From && mid < b.Along.To);
                        PaintEmitter.BikeLanes(pieces[i], s, Get(paint, tileId), i == 0 && start, i == pieces.Count - 1 && end,
                            skipLeft: Inside(false), skipRight: Inside(true));
                        s += length;
                    }
                }
                foreach (var (segment, tileId, start, end) in trackPaint)
                    EmitTrackPaint(finalPieces.TryGetValue(segment, out var pieces) ? pieces : [segment], start, end, Get(paint, tileId));
                EmitBikeCrossings(priority, result, segmentOf, finalPieces, pockets, block, wanted, paint, signs, bikeBridges, netStats.Bikes);
            }

            foreach (var id in block)
            {
                if (!loaded.ContainsKey(id) || !wanted.Contains(id)) continue;

                var segments = output.TryGetValue(id, out var s) ? s : new List<RoadSegment>();
                var junctions = caps.TryGetValue(id, out var j) ? j : new List<RoadJunction>();

                // cableways and watercourses go back exactly as they came in
                if (passthrough.TryGetValue(id, out var kept)) segments.AddRange(kept);

                var flags = RoadTileFlags.Network | RoadTileFlags.Bikes;
                if (segments.Any(x => x.Attributes.Has(RoadAttrFlags.Osm))) flags |= RoadTileFlags.Osm;
                streetStats.Tiles++;
                var pointProps = signs.TryGetValue(id, out var sp) ? sp : new List<RoadPointProp>();
                var bridges = bikeBridges.TryGetValue(id, out var br) ? br : [];
                MoveSignsOffPaths(segments, pointProps, netStats.Bikes);   // #120
                var walls = new List<RoadLinearProp>();
                if (grids is not null)
                {
                    var owners = new List<int>();
                    walls = EmbankmentPlanner.Plan(id, segments, (e, n) => SampleGround(grids, e, n), embankments, owners);
                    walls.AddRange(RailingPlanner.Plan(id, segments, walls, owners, (e, n) => SampleGround(grids, e, n), railings));
                }
                var tile = new RoadTile
                {
                    Id = id, Segments = segments, Junctions = junctions, Flags = flags,
                    Paint = paint.TryGetValue(id, out var p) ? p : new List<RoadPaint>(),
                    LinearProps = walls,
                    AreaProps = [.. islands.TryGetValue(id, out var isl) ? isl : [],
                        .. Unbridged(id, CornerPlanner.Plan(id, segments, junctions, facades, cornerStats, isl), bridges, netStats.Bikes),   // sidewalk corners (#119)
                        .. bridges.Select(x => x.Band)],
                    PointProps = pointProps,
                    Signals = signalRecords.TryGetValue(id, out var sg) ? sg : new List<RoadSignal>(),
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
        log(heightStats.Format());
        log(streetStats.Format());
        log(cornerStats.Format());
        log(embankments.Format());
        log(railings.Format());
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

    /// <summary>Stations along a piece where a turn lane's widening is measured, metres.</summary>
    private const double WideningStep = 0.5;
    /// <summary>A widening is looked for this far out from the ribbon's edge, in steps of <see cref="WideningProbe"/>.</summary>
    private const double WideningReach = 10.0, WideningProbe = 0.05;
    /// <summary>
    /// The shift may leave the measured widening by this much between two of its breakpoints: more
    /// than a probe step, so the probe's own 5 cm jitter cuts no piece.
    /// </summary>
    private const double WideningTolerance = 0.08;

    /// <summary>
    /// A street piece beside a turn lane's widening (#123's flush <see cref="AreaPropType.Pavement"/>
    /// strips): each side that would stand on it moves out by the widening, its kerb on the strip's
    /// outer edge, instead of being dropped (#120: a bike path, its grass and the sidewalk carry on
    /// round the pocket; #123 used to drop the sidewalk there). The widening is measured every
    /// <see cref="WideningStep"/> out from the ribbon's edge, the piece is cut where it bends (taper
    /// start and end, within <see cref="WideningTolerance"/>), and each part's side carries a linear
    /// shift (<see cref="RoadSide.ShiftStartCm"/>, <see cref="RoadSide.ShiftEndCm"/>).
    /// </summary>
    private static List<RoadSegment> ShiftOffPavement(RoadSegment seg, List<RoadAreaProp> strips, BikePlanner.Stats bikes)
    {
        var a = seg.Attributes;
        static bool Moves(RoadSide s) => s.OuterDm > 0 || s.HasLane;
        if ((!Moves(a.Left) && !Moves(a.Right)) || seg.PointCount < 2) return [seg];
        var p = seg.Points;
        var st = new List<(double S, double X, double Z, double Nx, double Nz)>();
        double travelled = 0, next = 0, lastNx = 0, lastNz = 0;
        for (int i = 0; i + 1 < seg.PointCount; i++)
        {
            double ax = p[i * 3], az = p[i * 3 + 2], dx = p[i * 3 + 3] - ax, dz = p[i * 3 + 5] - az, l = Math.Sqrt(dx * dx + dz * dz);
            if (l < 1e-6) continue;
            double fx = dx / l, fz = dz / l;
            // right of travel, tile-local (x east, z south): (-fz, fx)
            for (; next <= travelled + l + 1e-9; next += WideningStep)
                st.Add((next, ax + fx * (next - travelled), az + fz * (next - travelled), -fz, fx));
            travelled += l;
            (lastNx, lastNz) = (-fz, fx);
        }
        if (st.Count == 0) return [seg];
        if (travelled - st[^1].S > 0.05) st.Add((travelled, p[^3], p[^1], lastNx, lastNz));

        double half = seg.Width * 0.5;
        double[] Widening(bool right)
        {
            var w = new double[st.Count];
            if (!Moves(right ? a.Right : a.Left)) return w;
            double sign = right ? 1 : -1;
            for (int k = 0; k < st.Count; k++)
            {
                var (_, x, z, nx, nz) = st[k];
                // the end stations a hair inside: a strip that ends there would be missed by rounding
                double inward = k == 0 ? WideningProbe : k == st.Count - 1 ? -WideningProbe : 0;
                x += nz * inward; z -= nx * inward;   // forward is (nz, -nx)
                double covered = 0;
                for (double d = WideningProbe; d <= WideningReach; d += WideningProbe)
                {
                    double px = x + nx * sign * (half + d), pz = z + nz * sign * (half + d);
                    if (!strips.Any(strip => InsideArea(strip, px, pz))) break;
                    covered = d;
                }
                // half a probe past the last point found on the strip: its edge lies between
                w[k] = covered > 0 ? covered + WideningProbe * 0.5 : 0;
            }
            return w;
        }
        var left = Widening(false);
        var right = Widening(true);
        if (left.All(w => w == 0) && right.All(w => w == 0)) return [seg];
        if ((left.Any(w => w > 0) && a.Left.HasTrack) || (right.Any(w => w > 0) && a.Right.HasTrack)) bikes.PathsShiftedOffTurnLanes++;

        // breakpoints: where either side's widening stops being a straight ramp
        var keep = new SortedSet<int> { 0, st.Count - 1 };
        void Breaks(double[] w, int i0, int i1)
        {
            int worst = -1;
            double far = WideningTolerance;
            for (int k = i0 + 1; k < i1; k++)
            {
                double t = (st[k].S - st[i0].S) / Math.Max(st[i1].S - st[i0].S, 1e-9);
                double d = Math.Abs(w[k] - (w[i0] + (w[i1] - w[i0]) * t));
                if (d > far) { far = d; worst = k; }
            }
            if (worst < 0) return;
            keep.Add(worst);
            Breaks(w, i0, worst);
            Breaks(w, worst, i1);
        }
        Breaks(left, 0, st.Count - 1);
        Breaks(right, 0, st.Count - 1);

        static ushort Cm(double m) => (ushort)Math.Clamp(Math.Ceiling(m * 100 - 1e-6), 0, ushort.MaxValue);
        RoadSide Shift(RoadSide s, double[] w, int i0, int i1) =>
            !Moves(s) ? s : s with { ShiftStartCm = Cm(w[i0]), ShiftEndCm = Cm(w[i1]) };
        var cuts = keep.ToList();
        var pieces = new List<RoadSegment>(cuts.Count - 1);
        for (int c = 0; c + 1 < cuts.Count; c++)
        {
            int i0 = cuts[c], i1 = cuts[c + 1];
            var points = StreetPlanner.Slice(seg, c == 0 ? 0 : st[i0].S, c + 2 == cuts.Count ? double.PositiveInfinity : st[i1].S);
            if (points.Length < 6) continue;
            // the yield bits belong to the segment's ends (#121)
            var flags = a.Flags;
            if (c > 0) flags &= ~RoadAttrFlags.YieldAtStart;
            if (c + 2 < cuts.Count) flags &= ~RoadAttrFlags.YieldAtEnd;
            pieces.Add(new RoadSegment
            {
                Class = seg.Class, Surface = seg.Surface, Flags = seg.Flags, Width = seg.Width, Points = points,
                Attributes = a with { Flags = flags, Left = Shift(a.Left, left, i0, i1), Right = Shift(a.Right, right, i0, i1) },
            });
        }
        return pieces.Count > 0 ? pieces : [seg];
    }

    /// <summary>A side without its painted bike lane.</summary>
    private static RoadSide NoLane(RoadSide s) => s.Bike == BikeKind.Lane ? s with { Bike = BikeKind.None, BikeDm = 0 } : s;

    private static bool InsideArea(RoadAreaProp prop, double x, double z)
    {
        var v = prop.Vertices;
        for (int t = 0; t + 2 < prop.Indices.Length; t += 3)
        {
            int i0 = prop.Indices[t] * 3, i1 = prop.Indices[t + 1] * 3, i2 = prop.Indices[t + 2] * 3;
            double d1 = (x - v[i1]) * (v[i0 + 2] - v[i1 + 2]) - (v[i0] - v[i1]) * (z - v[i1 + 2]);
            double d2 = (x - v[i2]) * (v[i1 + 2] - v[i2 + 2]) - (v[i1] - v[i2]) * (z - v[i2 + 2]);
            double d3 = (x - v[i0]) * (v[i2 + 2] - v[i0 + 2]) - (v[i2] - v[i0]) * (z - v[i0 + 2]);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(neg && pos)) return true;
        }
        return false;
    }

    /// <summary>How far out from a mouth its approach road gets the bore's width.</summary>
    private const double RampShoulderReach = 60.0;

    /// <summary>
    /// A tunnel's approach road (#119): an at-grade piece running out of a mouth along its axis
    /// (within 30 deg, within 1.5 m of the axis, up to <see cref="RampShoulderReach"/>) gets flush
    /// paved shoulders out to the bore's half width (<c>SidewalkDm</c>, no kerb). The road blend
    /// levels the ground across them and the ramp's walls (#125) stand at their edge, in line with
    /// the bore's walls; on a 2.2 m divided carriageway into a 6 m bore they stood inside the mouth.
    /// </summary>
    private static RoadSegment RampShoulders(RoadSegment seg, TileId id, List<(Vec2 At, Vec2 Out, double Half)> mouths)
    {
        if (!RoadEmbankment.IsAtGrade(seg) || seg.Class > RoadClass.Lane || seg.PointCount < 2) return seg;
        var p = seg.Points;
        var a = new Vec2(id.MinE + p[0], id.MaxN - p[2]);
        var b = new Vec2(id.MinE + p[^3], id.MaxN - p[^1]);
        var heading = b - a;
        if (heading.Length < 1e-6) return seg;
        heading = heading / heading.Length;
        foreach (var (at, dir, half) in mouths)
        {
            if (Math.Abs(heading.Dot(dir)) < 0.866) continue;
            bool on = false;
            foreach (var q in new[] { a, b, (a + b) * 0.5 })
            {
                var d = q - at;
                double along = d.Dot(dir), lateral = Math.Abs(d.Cross(dir));
                if (along >= -1 && along <= RampShoulderReach && lateral <= 1.5) { on = true; break; }
            }
            if (!on) continue;
            byte shoulder = (byte)Math.Clamp(Math.Round((half - seg.Width * 0.5) * 10), 0, 255);
            if (shoulder == 0) return seg;
            var attr = seg.Attributes;
            RoadSide Widen(RoadSide s) => s.SidewalkDm >= shoulder ? s : s with { SidewalkDm = shoulder, KerbCm = 0 };
            return new RoadSegment
            {
                Class = seg.Class, Surface = seg.Surface, Flags = seg.Flags, Width = seg.Width, Points = seg.Points,
                Attributes = attr with { Left = Widen(attr.Left), Right = Widen(attr.Right) },
            };
        }
        return seg;
    }

    /// <summary>
    /// A tram line (at grade) in a town: its track lies in paving, not on ballast (#119). Judged
    /// at the middle of the piece by the urban field the streets are lowered by.
    /// </summary>
    private static bool IsTownTram(RoadSegment rail, List<Vec2> plan, UrbanField field)
    {
        if (rail.Class != RoadClass.Railway || (rail.Flags & RoadFlags.Tramway) == 0 || plan.Count < 2
            || (rail.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0) return false;
        var mid = plan[plan.Count / 2];
        return field.Density(mid.X, mid.Y) >= UrbanField.UrbanAt;
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

    private static long DeflatedLength(ReadOnlySpan<byte> bytes)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(bytes);
        return Math.Min(ms.Length, bytes.Length);
    }

    /// <summary>Decodes the tile again: every paint primitive must come back as drawn (geometry within its 0.5 mm rounding).</summary>
    private static void CheckPaint(NetworkStats st, RoadTile tile, byte[] bytes)
    {
        if (tile.Paint.Count == 0) return;
        RoadTile back;
        using (var ms = new MemoryStream(bytes)) back = RoadCodec.Decode(ms);
        var segments = new HashSet<RoadSegment>(tile.Segments, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < tile.Paint.Count; i++)
        {
            var a = tile.Paint[i];
            bool reference = a.Segment is { } seg && segments.Contains(seg);
            if (reference) st.PaintRefs++; else st.PaintGeometry++;
            var b = i < back.Paint.Count ? back.Paint[i] : null;
            bool same = b is not null && b.Type == a.Type && b.Width == a.Width && b.Dash == a.Dash && b.Gap == a.Gap
                && b.Rgba == a.Rgba && b.Vertices.Length == a.Vertices.Length && b.Indices.SequenceEqual(a.Indices)
                && (reference ? b.Vertices.AsSpan().SequenceEqual(a.Vertices)
                    : a.Vertices.Zip(b.Vertices).All(x => Math.Abs(x.First - x.Second) <= 6e-4f));
            if (!same) st.PaintMismatch++;
        }
    }

    /// <summary>Splits the encoded tile at its v3 section boundaries for the per-part table.</summary>
    private static void CountParts(NetworkStats st, RoadTile tile, byte[] bytes)
    {
        void Add(string key, ReadOnlySpan<byte> part)
        {
            st.Parts.TryGetValue(key, out var v);
            st.Parts[key] = (v.Raw + part.Length, v.Deflated + DeflatedLength(part));
        }
        int at = RoadFormat.HeaderSize + tile.Segments.Sum(s => 12 + s.Points.Length * 4)
            + tile.Junctions.Sum(j => 8 + j.Vertices.Length * 4 + j.Indices.Length * 2);
        Add("v2", bytes.AsSpan(0, at));
        uint count = BitConverter.ToUInt32(bytes, at);
        at += 4;
        for (uint i = 0; i < count; i++)
        {
            string tag = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int length = BitConverter.ToInt32(bytes, at + 4);
            Add(tag, bytes.AsSpan(at, 8 + length));
            at += 8 + length;
        }
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
        CountParts(st, tile, bytes);
        CheckPaint(st, tile, bytes);
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
            foreach (var side in (ReadOnlySpan<RoadSide>)[a.Left, a.Right])
            {
                if (side.HasLane) { st.Bikes.LaneSideKm += km; if (a.Has(RoadAttrFlags.Urban)) st.Bikes.UrbanLaneSideKm += km; }
                st.Bikes.TrackSideKm[LayoutOf(side)] += side.HasTrack ? km : 0;
            }
        }
        st.Bikes.Symbols += tile.Paint.Count(p => p.Type == PaintType.BikeSymbol);
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
