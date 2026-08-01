using Microsoft.Data.Sqlite;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Extracts swissTLM3D roads and railways for a set of tiles into .road files.
/// Geometry is clipped to tile boundaries and draped onto the terrain heightfield so
/// roads sit on the ground the game actually renders, rather than on TLM's own Z
/// (which comes from a different height model and would float or sink).
/// </summary>
public sealed class RoadExtractor
{
    private const double DrapeOffset = 0.35; // metres above the terrain surface

    /// <summary>
    /// Extra lift per road class, so ribbons that share ground do not fight for depth.
    ///
    /// <para>
    /// TLM centrelines meet exactly at a junction: an exit ramp starts on the motorway
    /// centreline, a side road ends on the through road. Both are draped from the same
    /// heightfield with the same offset, so their ribbons come out coplanar and the
    /// overlap tears into flickering stripes wherever they cross. Ordering them by class
    /// puts the bigger road on top — 1.2 cm per class step is invisible at this fidelity
    /// but decisive for the depth test.
    /// </para>
    ///
    /// <para>
    /// The arithmetic only makes sense for the ordered road classes 0..12. The aerial, water
    /// and wall families that follow are not part of that ordering and would come out
    /// negative, sinking a stream into the ground.
    /// </para>
    /// </summary>
    private static double ClassLift(RoadClass cls)
    {
        // own Z; nothing to bias
        if (RoadFormat.IsAerial(cls) || RoadFormat.IsWall(cls)) return 0;
        if (RoadFormat.IsWatercourse(cls)) return 0.06;         // just clear of the terrain
        return (12 - (int)cls) * 0.012;
    }

    /// <summary>
    /// Max spacing between draped points. TLM3D puts vertices only where the road
    /// changes direction, so a straight run can span 50 m+; sampling the terrain only at
    /// those vertices makes the ribbon cut through every bump in between and appear
    /// dashed. Roughly 2 samples per terrain cell (2 m grid) tracks the ground closely.
    /// </summary>
    private const double MaxDrapeSpacing = 4.0;

    /// <summary>
    /// Length over which a bridge deck or tunnel invert eases into the draped road at a
    /// real abutment or portal. Without it the surveyed structure height meets the draped
    /// approach at a step, and the carriageway visibly breaks.
    ///
    /// The blend is applied as a *decaying offset measured at the abutment*, never as an
    /// interpolation toward the ground under each deck point. The latter is what the first
    /// version did, and on a span shorter than 2x this length the two end blends overlap
    /// and pull the middle of the deck down toward the river bed — a deep V in the middle
    /// of a viaduct, which is exactly the artefact this constant was meant to remove.
    /// </summary>
    private const double EndBlendM = 9.0;

    /// <summary>
    /// Largest step an approach may ramp through to reach a deck. Beyond this something is
    /// wrong with one of the two heights and it is safer to leave the join alone than to
    /// throw the road tens of metres into the air.
    /// </summary>
    private const double MaxApproachStep = 20.0;

    /// <summary>Tolerance for deciding two polyline endpoints are the same place, in metres.</summary>
    private const double JoinTolerance = 0.1;

    /// <summary>Keeps each segment within the u16 point count of the .road format.</summary>
    private const int MaxPointsPerSegment = 4096;

    private static readonly string[] RoadColumns =
    {
        "uuid", "objektart", "belagsart", "wanderwege",
        "verkehrsbeschraenkung", "richtungsgetrennt", "kunstbaute",
    };

    private static readonly string[] RailColumns =
    {
        "objektart", "kunstbaute", "anzahl_spuren", "verkehrsmittel",
        "zahnradbahn", "standseilbahn", "ausser_betrieb",
    };

    /// <summary>Aerial ropeways carry only the type and the geometry.</summary>
    private static readonly string[] TypeOnlyColumns = { "objektart" };

    /// <summary><c>verlauf</c> says whether the channel is on the surface or in a pipe.</summary>
    private static readonly string[] WaterColumns = { "objektart", "verlauf" };

    private readonly string _gpkgPath;
    private readonly HashSet<string> _cycleUuids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mtbUuids = new(StringComparer.OrdinalIgnoreCase);

    public RoadExtractor(string gpkgPath, string? routeKeysPath)
    {
        _gpkgPath = gpkgPath;
        if (routeKeysPath != null && File.Exists(routeKeysPath))
            LoadRouteKeys(routeKeysPath);
    }

    public int CycleKeyCount => _cycleUuids.Count;
    public int MtbKeyCount => _mtbUuids.Count;

    private void LoadRouteKeys(string path)
    {
        using var conn = GeoPackageReader.Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select uuid, kind from route_key";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string uuid = r.GetString(0);
            if (r.GetString(1) == "cycle") _cycleUuids.Add(uuid);
            else _mtbUuids.Add(uuid);
        }
    }

    /// <summary>
    /// Extracts every tile in <paramref name="tiles"/>. <paramref name="heightOf"/> returns
    /// the terrain altitude for an LV95 position, or null when that tile has no elevation
    /// data (the road keeps its TLM Z in that case).
    /// </summary>
    /// <summary>Terrain quads to drop so tunnel portals are open; filled during Extract.</summary>
    public Dictionary<TileId, HashSet<int>> Holes { get; } = new();

    /// <summary>One TLM line with its resolved attributes, held until the join index is built.</summary>
    private sealed record PendingLine(GeoPackageReader.Polyline Line, RoadClass Cls,
        RoadSurface Surface, RoadFlags Flags, float Width)
    {
        public bool IsStructure => (Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0;
    }

    private readonly List<PendingLine> _pending = new();

    /// <summary>
    /// Surveyed height at each structure endpoint, so an approach road knows what it has to
    /// meet. Keyed on the position rounded to <see cref="JoinTolerance"/>.
    /// </summary>
    private readonly Dictionary<(long, long), double> _structureEnds = new();

    /// <summary>How many structure endpoints share a position — two means a mid-span join.</summary>
    private readonly Dictionary<(long, long), int> _structureEndCount = new();

    private static (long, long) JoinKey(double e, double n) =>
        ((long)Math.Round(e / JoinTolerance), (long)Math.Round(n / JoinTolerance));

    public Dictionary<TileId, RoadTile> Extract(IReadOnlyCollection<TileId> tiles,
        Func<double, double, double?> heightOf)
    {
        var result = tiles.ToDictionary(t => t, t => new RoadTile { Id = t, Segments = new() });
        if (tiles.Count == 0) return result;

        double minE = tiles.Min(t => t.MinE), maxE = tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        double minN = tiles.Min(t => t.MinN), maxN = tiles.Max(t => t.MinN) + ChunkFormat.TileSizeM;

        _pending.Clear();
        _structureEnds.Clear();
        _structureEndCount.Clear();

        using var conn = GeoPackageReader.Open(_gpkgPath);
        ExtractRoads(conn, minE, minN, maxE, maxN);
        ExtractRailways(conn, minE, minN, maxE, maxN);
        ExtractAerial(conn, minE, minN, maxE, maxN);
        ExtractWatercourses(conn, minE, minN, maxE, maxN);
        ExtractDefences(conn, minE, minN, maxE, maxN);

        // Where a deck or bore ends, note the height it ends at. The approach road is a
        // separate TLM feature, so this is the only way it can learn what to ramp up to —
        // and ramping the approach is what keeps the deck itself dead flat.
        foreach (var p in _pending)
        {
            if (!p.IsStructure) continue;
            foreach (int i in new[] { 0, p.Line.Count - 1 })
            {
                var key = JoinKey(p.Line.E[i], p.Line.N[i]);
                _structureEnds[key] = p.Line.Z[i];
                _structureEndCount[key] = _structureEndCount.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var p in _pending)
            Emit(p, result, heightOf);

        _pending.Clear();
        return result;
    }

    private void ExtractRoads(SqliteConnection conn,
        double minE, double minN, double maxE, double maxN)
    {
        using var cmd = GeoPackageReader.BboxQuery(conn, "tlm_strassen_strasse", "geom",
            RoadColumns, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            string? uuid = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (RoadFormat.IsNotDrivableSurface(Str(reader, 1))) continue;
            var cls = RoadFormat.ParseClass(Str(reader, 1));
            var surface = RoadFormat.ParseSurface(Str(reader, 2));
            var flags = RoadFormat.ParseFlags(Str(reader, 3), Str(reader, 6), Str(reader, 4), Str(reader, 5));

            if (uuid != null)
            {
                if (_cycleUuids.Contains(uuid)) flags |= RoadFlags.Cycle;
                if (_mtbUuids.Contains(uuid)) flags |= RoadFlags.MountainBike;
            }

            Collect(reader, cls, surface, flags, RoadFormat.WidthFor(cls, flags));
        }
    }

    /// <summary>
    /// Aerial ropeways. These keep their surveyed Z — see <see cref="RoadFormat.IsAerial"/> —
    /// so unlike everything else here the height is the answer, not a starting point.
    /// </summary>
    private void ExtractAerial(SqliteConnection conn,
        double minE, double minN, double maxE, double maxN)
    {
        using var cmd = GeoPackageReader.BboxQuery(conn, "tlm_oev_uebrige_bahn", "geom",
            TypeOnlyColumns, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            if (RoadFormat.ParseAerial(Str(reader, 0)) is not { } cls) continue;
            Collect(reader, cls, RoadSurface.Unknown, RoadFlags.None, RoadFormat.DefaultWidth(cls));
        }
    }

    /// <summary>
    /// Watercourses. Draped like a road, because their surveyed Z sits on the ground anyway —
    /// measured against our own heightfield, the median offset is −0.14 m.
    /// </summary>
    private void ExtractWatercourses(SqliteConnection conn,
        double minE, double minN, double maxE, double maxN)
    {
        using var cmd = GeoPackageReader.BboxQuery(conn, "tlm_gewaesser_fliessgewaesser", "geom",
            WaterColumns, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            if (RoadFormat.ParseWatercourse(Str(reader, 0)) is not { } cls) continue;

            // A culverted channel is in a pipe under the ground. TLM maps them as ordinary
            // watercourses and they are not a small share: 179 of the 423 around Riddes alone.
            // Drawing them puts streams down the middle of village streets.
            if (Str(reader, 1) is { } verlauf
                && verlauf.StartsWith("Unterirdisch", StringComparison.Ordinal)) continue;

            Collect(reader, cls, RoadSurface.Natural, RoadFlags.None, RoadFormat.DefaultWidth(cls));
        }
    }

    /// <summary>
    /// Protective works and walls. Like ropeways these keep their surveyed Z, because for an
    /// avalanche barrier that Z is the top of the structure; the mesh builder grows each wall
    /// from the terrain up to it.
    /// </summary>
    private void ExtractDefences(SqliteConnection conn,
        double minE, double minN, double maxE, double maxN)
    {
        using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_bauten_verbauung", "geom",
            TypeOnlyColumns, minE, minN, maxE, maxN))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (RoadFormat.ParseDefence(Str(reader, 0)) is not { } cls) continue;
                Collect(reader, cls, RoadSurface.Unknown, RoadFlags.None, RoadFormat.DefaultWidth(cls));
            }
        }

        using (var cmd = GeoPackageReader.BboxQuery(conn, "tlm_bauten_mauer", "geom",
            TypeOnlyColumns, minE, minN, maxE, maxN))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                Collect(reader, RoadClass.Wall, RoadSurface.Unknown, RoadFlags.None,
                    RoadFormat.DefaultWidth(RoadClass.Wall));
        }
    }

    private void ExtractRailways(SqliteConnection conn,
        double minE, double minN, double maxE, double maxN)
    {
        using var cmd = GeoPackageReader.BboxQuery(conn, "tlm_oev_eisenbahn", "geom",
            RailColumns, minE, minN, maxE, maxN);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            string? objektart = Str(reader, 0);
            var flags = RoadFormat.ParseFlags(null, Str(reader, 1), null, null);

            if (objektart != null && objektart.StartsWith("Schmalspur", StringComparison.Ordinal))
                flags |= RoadFlags.NarrowGauge;
            if (!reader.IsDBNull(2) && reader.GetInt32(2) >= 2)
                flags |= RoadFlags.DoubleTrack;
            if (Str(reader, 3) is "Tram" or "Metro")
                flags |= RoadFlags.Tramway;
            if (IsTrue(Str(reader, 4))) flags |= RoadFlags.RackRailway;
            if (IsTrue(Str(reader, 5))) flags |= RoadFlags.Funicular;
            if (IsTrue(Str(reader, 6))) flags |= RoadFlags.Disused;

            // formation is wider for double track
            float width = (flags & RoadFlags.DoubleTrack) != 0 ? 9.0f
                : (flags & RoadFlags.NarrowGauge) != 0 ? 3.6f : 4.6f;

            Collect(reader, RoadClass.Railway, RoadSurface.Unknown, flags, width);
        }
    }

    private void Collect(SqliteDataReader reader, RoadClass cls, RoadSurface surface,
        RoadFlags flags, float width)
    {
        int geomIndex = reader.FieldCount - 1;
        if (reader.IsDBNull(geomIndex)) return;

        foreach (var line in GeoPackageReader.ParseLines((byte[])reader.GetValue(geomIndex)))
            if (line.Count >= 2)
                _pending.Add(new PendingLine(line, cls, surface, flags, width));
    }

    private void Emit(PendingLine p, Dictionary<TileId, RoadTile> result,
        Func<double, double, double?> heightOf)
    {
        var (line, cls, surface, flags, width) = (p.Line, p.Cls, p.Surface, p.Flags, p.Width);
        {
            foreach (var piece in PolylineClipper.SplitByTile(line))
            {
                if (!result.TryGetValue(piece.Tile, out var tile)) continue;

                // bridges, tunnels and aerial ropeways keep their surveyed height; everything
                // else is draped so it follows the terrain the player actually walks on. For a
                // cableway the Z *is* the answer — draping one would lay the cable on the ground.
                bool useOwnZ = (flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0
                    || RoadFormat.IsAerial(cls) || RoadFormat.IsWall(cls);
                // structures need densifying too, otherwise the end blend has nowhere to
                // interpolate across.
                //
                // An aerial ropeway is the one thing that must NOT be densified. Densifying
                // exists to give the drape enough samples to follow the ground, and a cable is
                // not draped — but the renderer puts a tower under every vertex, so a 4 m
                // spacing turns a gondola line into a picket fence marching up the mountain.
                // The surveyed vertices are already exactly where the real pylons are, because
                // that is where a cable changes direction.
                var dense = RoadFormat.IsAerial(cls) ? piece.Points : Densify(piece.Points);

                // cumulative distance, used to ease structure heights into the approach
                var along = new double[dense.Count];
                for (int i = 1; i < dense.Count; i++)
                    along[i] = along[i - 1] + Math.Sqrt(
                        (dense[i].E - dense[i - 1].E) * (dense[i].E - dense[i - 1].E) +
                        (dense[i].N - dense[i - 1].N) * (dense[i].N - dense[i - 1].N));
                double total = along[^1];

                if ((flags & RoadFlags.Tunnel) != 0)
                    TunnelCarver.Carve(piece.Points, RoadFormat.TunnelWidth(cls),
                        RoadFormat.TunnelHeight(cls), heightOf, Holes);

                var draped = DrapeHeights(dense, heightOf, DrapeOffset + ClassLift(cls));
                LimitGrade(dense, draped, MaxGrade(cls, flags));

                // A DRAPED road that ends where a deck or bore begins ramps up to meet it.
                // Moving the approach, not the structure, is what keeps the deck flat: it is
                // the embankment that climbs to a bridge in the real world, and swissALTI3D
                // does not model that embankment, so the drape sits too low by exactly this
                // much. Structures themselves get no blend at all.
                double deltaStart = 0, deltaEnd = 0;
                if (!useOwnZ)
                {
                    deltaStart = ApproachDelta(piece.AtLineStart, dense[0], draped[0]);
                    deltaEnd = ApproachDelta(piece.AtLineEnd, dense[^1], draped[^1]);
                }
                // never let the two ramps meet in the middle of a short piece
                double blendLen = Math.Min(EndBlendM, Math.Max(total * 0.5, 0.5));

                // point counts are u16 in the file format; emit in chunks that share a
                // vertex so the ribbon stays visually continuous across the split
                for (int start = 0; start < dense.Count - 1; start += MaxPointsPerSegment - 1)
                {
                    int count = Math.Min(MaxPointsPerSegment, dense.Count - start);
                    var pts = new float[count * 3];
                    for (int i = 0; i < count; i++)
                    {
                        int gi = start + i;
                        var (e, n, z) = dense[gi];
                        // A deck or bore is exactly its surveyed height, end to end. The
                        // approach carries the whole mismatch, fading it out inland.
                        double y = useOwnZ
                            ? z
                            : draped[gi]
                              + deltaStart * Falloff(along[gi], blendLen)
                              + deltaEnd * Falloff(total - along[gi], blendLen);

                        pts[i * 3 + 0] = (float)(e - piece.Tile.MinE);
                        pts[i * 3 + 1] = (float)y;
                        pts[i * 3 + 2] = (float)(piece.Tile.MaxN - n);
                    }

                    tile.Segments.Add(new RoadSegment
                    {
                        Class = cls, Surface = surface, Flags = flags, Width = width, Points = pts,
                    });
                }
            }
        }
    }

    /// <summary>
    /// How far this end of a draped road has to rise (or fall) to meet the structure that
    /// continues from it. Zero unless the end really is a true polyline end that coincides
    /// with a deck or bore endpoint.
    /// </summary>
    private double ApproachDelta(bool atTrueEnd, (double E, double N, double Z) end, double draped)
    {
        if (!atTrueEnd) return 0;
        if (!_structureEnds.TryGetValue(JoinKey(end.E, end.N), out double structureZ)) return 0;
        double delta = structureZ - draped;
        return Math.Abs(delta) <= MaxApproachStep ? delta : 0;
    }

    /// <summary>
    /// Steepest gradient a draped centreline of this class is allowed to take, as rise over
    /// run. Deliberately about double the real-world maximum — the point is not to enforce
    /// engineering standards but to reject terrain the road cannot possibly be following.
    /// </summary>
    private static double MaxGrade(RoadClass cls, RoadFlags flags)
    {
        // stairs and via ferrata really do go straight up
        if ((flags & RoadFlags.Stairs) != 0) return double.MaxValue;
        if (cls == RoadClass.Railway)
            return (flags & (RoadFlags.RackRailway | RoadFlags.Funicular)) != 0 ? 1.2 : 0.12;
        // an Alpinwanderweg is allowed to be near-vertical; a marked footpath is not
        if ((flags & RoadFlags.MountainHiking) != 0) return 1.2;
        return cls switch
        {
            RoadClass.Motorway or RoadClass.Expressway => 0.20,
            RoadClass.Track => 0.50,
            RoadClass.Path or RoadClass.Link or RoadClass.Unknown => 0.55,

            // A mountain stream really does fall over a cliff, so clamping it to a road's
            // gradient would lift the bed clear of the gorge it runs in. A bisse is the
            // opposite case: it was dug to hold a near-constant fall, so a steep step in one
            // is a drape artefact and gets clamped like a road.
            RoadClass.Watercourse or RoadClass.DryChannel => 2.0,
            RoadClass.Bisse => 0.30,

            _ => 0.35,
        };
    }

    /// <summary>
    /// Pulls each interior vertex back inside the gradient its neighbours allow.
    ///
    /// A TLM centreline that runs along the lip of a cliff samples the heightfield either
    /// side of a near-vertical face, so a path at 2400 m picks up a 100 m step between two
    /// vertices 4 m apart — a spike the road obviously does not have. Only interior points
    /// move, and only far enough to satisfy the bound, so a genuinely steep alpine track is
    /// left exactly as draped. Several passes let a wider excursion erode from both sides.
    /// </summary>
    private static void LimitGrade(List<(double E, double N, double Z)> pts, double[] y, double maxGrade)
    {
        if (double.IsPositiveInfinity(maxGrade) || maxGrade == double.MaxValue || y.Length < 3) return;

        for (int pass = 0; pass < 24; pass++)
        {
            bool changed = false;
            for (int i = 1; i < y.Length - 1; i++)
            {
                double back = Dist(pts[i - 1], pts[i]);
                double fwd = Dist(pts[i], pts[i + 1]);
                double lo = Math.Max(y[i - 1] - maxGrade * back, y[i + 1] - maxGrade * fwd);
                double hi = Math.Min(y[i - 1] + maxGrade * back, y[i + 1] + maxGrade * fwd);
                if (lo > hi) continue;   // neighbours already disagree by more than the bound
                double clamped = Math.Clamp(y[i], lo, hi);
                if (Math.Abs(clamped - y[i]) > 1e-6) { y[i] = clamped; changed = true; }
            }
            if (!changed) break;
        }
    }

    private static double Dist((double E, double N, double Z) a, (double E, double N, double Z) b)
        => Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));

    /// <summary>
    /// 1 at the abutment, smoothly 0 at <paramref name="len"/> and beyond. Smoothstep, so
    /// the deck leaves the abutment with zero slope change and the join shows no kink.
    /// </summary>
    private static double Falloff(double distance, double len)
    {
        if (distance >= len) return 0;
        double t = Math.Clamp(distance / len, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Draped height per point, with gaps filled in.
    ///
    /// <paramref name="heightOf"/> returns null wherever the heightfield is not loaded —
    /// most often at a tile boundary that falls on the edge of the current batch. Taking
    /// TLM's own Z there instead mixes two different height models mid-polyline, and since
    /// they disagree by metres the road grows a spike at that one vertex. Interpolating
    /// across the gap from the neighbours keeps the ribbon continuous; TLM Z is used only
    /// if the whole piece missed the heightfield.
    /// </summary>
    private static double[] DrapeHeights(List<(double E, double N, double Z)> pts,
        Func<double, double, double?> heightOf, double offset)
    {
        int n = pts.Count;
        var y = new double[n];
        var known = new bool[n];

        for (int i = 0; i < n; i++)
        {
            double? h = heightOf(pts[i].E, pts[i].N);
            if (h != null) { y[i] = h.Value + offset; known[i] = true; }
        }

        int firstKnown = Array.IndexOf(known, true);
        if (firstKnown < 0)
        {
            for (int i = 0; i < n; i++) y[i] = pts[i].Z + offset;
            return y;
        }

        for (int i = 0; i < firstKnown; i++) y[i] = y[firstKnown];
        int lastKnown = Array.LastIndexOf(known, true);
        for (int i = lastKnown + 1; i < n; i++) y[i] = y[lastKnown];

        // interior runs of unknowns: straight line between the bracketing known heights
        int gapStart = -1;
        for (int i = firstKnown; i <= lastKnown; i++)
        {
            if (!known[i]) { if (gapStart < 0) gapStart = i; continue; }
            if (gapStart >= 0)
            {
                double a = y[gapStart - 1], b = y[i];
                int span = i - (gapStart - 1);
                for (int k = gapStart; k < i; k++)
                    y[k] = a + (b - a) * (k - (gapStart - 1)) / span;
                gapStart = -1;
            }
        }
        return y;
    }

    /// <summary>Inserts intermediate vertices so no span exceeds <see cref="MaxDrapeSpacing"/>.</summary>
    private static List<(double E, double N, double Z)> Densify(List<(double E, double N, double Z)> pts)
    {
        var outPts = new List<(double, double, double)>(pts.Count * 2);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            outPts.Add(a);

            double len = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            int steps = (int)Math.Ceiling(len / MaxDrapeSpacing);
            // guard against absurd vertex counts on very long straight runs
            steps = Math.Min(steps, 512);
            for (int s = 1; s < steps; s++)
            {
                double t = (double)s / steps;
                outPts.Add((a.E + (b.E - a.E) * t, a.N + (b.N - a.N) * t, a.Z + (b.Z - a.Z) * t));
            }
        }
        outPts.Add(pts[^1]);
        return outPts;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>TLM3D booleans arrive as German words, not 0/1.</summary>
    private static bool IsTrue(string? v) => v is "Wahr" or "true" or "Ja" or "1";
}
