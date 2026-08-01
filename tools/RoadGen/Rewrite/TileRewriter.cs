namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
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
        double MaxHeightShift = 1.0);

    public sealed record Stats(
        int TilesRead, int TilesWritten, int Junctions, int SegmentsWritten, int SegmentsDropped,
        double OverlapBefore, double OverlapAfter, double CarriagewayArea,
        HeightAudit Heights, int VerticesReverted);

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
        var wanted = new HashSet<TileId>(tiles);

        if (!options.Force)
        {
            int already = tiles.Count(id => HasJunctions(chunkDir, id));
            if (already > 0)
                throw new AlreadyRewrittenException(
                    $"{already} of {tiles.Count} tiles already carry junction polygons.\n"
                    + "Rewriting is not idempotent — a second pass trims roads that are already\n"
                    + "trimmed and replaces the caps with tiny ones, leaving a hole at every\n"
                    + "junction. Re-run the roads preprocessing to get clean tiles, or --force\n"
                    + "if you know these are fresh.");
        }

        var blocks = GroupIntoBlocks(tiles, options.BlockSize);

        int tilesRead = 0, tilesWritten = 0, junctionCount = 0, written = 0, dropped = 0;
        double overlapBefore = 0, overlapAfter = 0, carriageway = 0;
        int blockIndex = 0, guarded = 0;
        var audit = new HeightAuditor();

        foreach (var block in blocks)
        {
            blockIndex++;
            var context = WithHalo(block, options.Halo);

            var net = new RoadNetwork();
            var loaded = new Dictionary<TileId, RoadTile>();
            var passthrough = new Dictionary<TileId, List<RoadSegment>>();

            foreach (var id in context)
            {
                string path = Path.Combine(chunkDir, RoadFormat.FileName(id));
                if (!File.Exists(path)) continue;

                using var stream = File.OpenRead(path);
                var tile = RoadCodec.Decode(stream);
                loaded[id] = tile;
                if (block.Contains(id)) tilesRead++;

                foreach (var segment in tile.Segments)
                {
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

                    AddSegment(net, id, segment, options.DividedScale);
                }
            }

            if (net.Links.Count == 0) continue;

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
            carriageway += result.Report.CarriagewayArea;

            var terrain = options.AuditHeights ? LoadGrids(chunkDir, context) : null;

            var output = new Dictionary<TileId, List<RoadSegment>>();
            var caps = new Dictionary<TileId, List<RoadJunction>>();

            foreach (var ribbon in result.Ribbons)
            {
                var link = result.Network.Links[ribbon.LinkId];
                if (link.Tag is not Source source) continue;
                if (!block.Contains(source.Tile)) continue;      // halo tiles are context only
                if (ribbon.Stations.Count < 2) { dropped++; continue; }

                if (!output.TryGetValue(source.Tile, out var list))
                    output[source.Tile] = list = new List<RoadSegment>();

                var plan = ribbon.Stations.Select(s => s.Position).ToList();

                if (terrain is not null && link.AllowSmoothing)
                {
                    guarded += ApplyCliffGuard(plan, source, terrain, options.MaxHeightShift);
                    // structures are meant to sit above the ground, so auditing them against
                    // the terrain would measure the bridge, not the smoothing
                    audit.Add(plan, source, terrain);
                }

                list.Add(ToSegment(plan, source));
                written++;
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

            foreach (var id in block)
            {
                if (!loaded.ContainsKey(id) || !wanted.Contains(id)) continue;

                var segments = output.TryGetValue(id, out var s) ? s : new List<RoadSegment>();
                var junctions = caps.TryGetValue(id, out var j) ? j : new List<RoadJunction>();

                // cableways and watercourses go back exactly as they came in
                if (passthrough.TryGetValue(id, out var kept)) segments.AddRange(kept);

                if (options.DryRun) { tilesWritten++; continue; }

                var tile = new RoadTile { Id = id, Segments = segments, Junctions = junctions };
                string path = Path.Combine(chunkDir, RoadFormat.FileName(id));
                string temp = path + ".part";

                // write via a temp file: a half-written .road looks exactly like a valid short
                // one, and the region being rewritten is the region being played
                using (var stream = File.Create(temp)) RoadCodec.Encode(tile, stream);
                File.Move(temp, path, overwrite: true);
                tilesWritten++;
            }

            if (blockIndex % 10 == 0 || blockIndex == blocks.Count)
                log($"  block {blockIndex}/{blocks.Count}  {tilesWritten} tiles, {junctionCount} junctions");
        }

        return new Stats(tilesRead, tilesWritten, junctionCount, written, dropped,
            overlapBefore, overlapAfter, carriageway, audit.Result(), guarded);
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

    /// <summary>Reads only the 24-byte header — a pre-scan of 6,699 tiles must not decode them all.</summary>
    private static bool HasJunctions(string chunkDir, TileId id)
    {
        string path = Path.Combine(chunkDir, RoadFormat.FileName(id));
        if (!File.Exists(path)) return false;

        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[RoadFormat.HeaderSize];
        if (stream.Read(header) < RoadFormat.HeaderSize) return false;
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) > 0;
    }

    private static void AddSegment(RoadNetwork net, TileId id, RoadSegment segment, double dividedScale)
    {
        if (segment.PointCount < 2) return;

        var plan = new Vec2[segment.PointCount];
        var height = new float[segment.PointCount];
        for (int i = 0; i < segment.PointCount; i++)
        {
            plan[i] = new Vec2(id.MinE + segment.Points[i * 3], id.MaxN - segment.Points[i * 3 + 2]);
            height[i] = segment.Points[i * 3 + 1];
        }

        var source = new Source { Tile = id, Segment = segment, Plan = plan, Height = height };
        var centreline = Polyline.Dedupe(plan.ToList());
        if (centreline.Count < 2) return;

        bool structure = (segment.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0;
        int layer = (segment.Flags & RoadFlags.Bridge) != 0 ? 1
            : (segment.Flags & RoadFlags.Tunnel) != 0 ? -1 : 0;

        net.AddLink(centreline, ProfileFor(segment, dividedScale), layer, source, allowSmoothing: !structure);
    }

    private static RoadSegment ToSegment(List<Vec2> plan, Source source)
    {
        var id = source.Tile;
        var points = new float[plan.Count * 3];
        for (int i = 0; i < plan.Count; i++)
        {
            points[i * 3] = (float)(plan[i].X - id.MinE);
            points[i * 3 + 1] = source.SampleHeight(plan[i]);
            points[i * 3 + 2] = (float)(id.MaxN - plan[i].Y);
        }

        return new RoadSegment
        {
            Class = source.Segment.Class,
            Surface = source.Segment.Surface,
            Flags = source.Segment.Flags,
            Width = source.Segment.Width,
            Points = points,
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
            Layer = (sbyte)junction.Layer,
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

    private static RoadProfile ProfileFor(RoadSegment segment, double dividedScale)
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

        double width = segment.Width > 0.1 ? segment.Width : profile.Width;
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
