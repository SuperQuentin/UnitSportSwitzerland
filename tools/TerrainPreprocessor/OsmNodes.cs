using System.Globalization;
using System.Text;
using static UnitSport.Tools.Preprocessor.OsmOverlay;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The overlay's point data (#347): OSM traffic signal nodes, bike boxes (<c>cycleway=asl</c>) and
/// turn restriction relations, snapped onto the TLM lines the overlay conflated their ways to, and
/// written to <c>&lt;temp&gt;/osm_nodes.tsv</c> beside <c>osm_overlay.tsv</c>. Each row says which TLM
/// line end (the junction) it belongs to, so the network stage can match it to its junction node.
/// Format: docs/notes/tools/osm-overlay.md.
/// </summary>
public static class OsmNodes
{
    public const string FileName = UnitSport.Tools.RoadGen.Import.OsmNodesReader.FileName;

    /// <summary>A signal or bike box this close to a TLM line end (along the line) belongs to that end's junction.</summary>
    private const double JunctionM = 30.0;
    /// <summary>A node snaps to a TLM line within half its width plus this.</summary>
    private const double SnapM = 10.0;
    /// <summary>A restriction's from/to line must end this close to the via node.</summary>
    private const double ViaM = 30.0;
    /// <summary>Largest angle between an OSM way and the TLM line it is matched to by geometry (as in the conflation).</summary>
    private const double MaxAngleDeg = 30.0;
    /// <summary>How far along a way or line its direction at a node or line end is measured.</summary>
    private const double TangentM = 10.0;

    /// <summary>Node tags read from the PBF; only these end up in the <c>tags</c> column.</summary>
    public static readonly HashSet<string> NodeTagKeys =
    [
        "highway", "traffic_signals", "traffic_signals:direction", "direction", "crossing", "button_operated",
        "traffic_signals:sound", "traffic_signals:vibration", "cycleway", "cycleway:left", "cycleway:right", "cycleway:both",
    ];

    public static readonly HashSet<string> RelationTagKeys = ["type", "restriction", "restriction:motorcar", "except"];

    private static readonly HashSet<string> RestrictionValues =
    [
        "no_left_turn", "no_right_turn", "no_straight_on", "no_u_turn", "only_left_turn", "only_right_turn", "only_straight_on",
    ];

    /// <summary>Highways that make a junction: a signal on a node where these meet is the junction's own signal.</summary>
    private static readonly HashSet<string> CarHighways =
    [
        "motorway", "trunk", "primary", "secondary", "tertiary", "unclassified", "residential", "living_street", "service",
        "road", "busway", "motorway_link", "trunk_link", "primary_link", "secondary_link", "tertiary_link",
    ];

    public static bool IsSignal(Dictionary<string, string> t) =>
        t.GetValueOrDefault("highway") == "traffic_signals" || t.GetValueOrDefault("crossing") == "traffic_signals";

    public static bool IsAsl(Dictionary<string, string> t) =>
        t.GetValueOrDefault("cycleway") == "asl" || t.GetValueOrDefault("cycleway:right") == "asl"
        || t.GetValueOrDefault("cycleway:left") == "asl" || t.GetValueOrDefault("cycleway:both") == "asl";

    /// <summary>A pedestrian crossing on a road (#700): <c>highway=crossing</c>, its kind in <c>crossing=*</c> (zebra, marked, uncontrolled, unmarked, traffic_signals).</summary>
    public static bool IsCrossing(Dictionary<string, string> t) => t.GetValueOrDefault("highway") == "crossing";

    public static bool KeepNode(Dictionary<string, string> t) => IsSignal(t) || IsAsl(t) || IsCrossing(t);

    public static bool KeepRelation(Dictionary<string, string> t) => t.GetValueOrDefault("type") == "restriction";

    /// <summary>A tagged OSM node in LV95.</summary>
    public sealed record Point(long Id, double E, double N, Dictionary<string, string> Tags);

    /// <summary>
    /// One output row. <see cref="Junction"/>: <c>node</c> (on an OSM node where car ways meet),
    /// <c>approach</c> (within 30 m of <see cref="LineEnd"/>), <c>mid</c> (mid-block), <c>via</c>
    /// (a restriction). <see cref="Dir"/>: <c>+</c>/<c>-</c> relative to TLM drawing, <c>both</c>, or empty.
    /// </summary>
    public sealed record NodeRow(string Kind, long OsmId, double E, double N, string Uuid, int Part, double Along,
        string Junction, string LineEnd, double EndE, double EndN, string Dir, string Value,
        string ToUuid, int ToPart, string ToEnd, string Tags);

    public sealed class Stats
    {
        public int Signals, SignalsHighway, SignalsCrossingOnly, SignalsWithDir;
        public int Asl;
        public readonly Dictionary<(string Kind, string Junction), int> Snapped = new();
        public int SignalsUnmatched, AslUnmatched;
        /// <summary>Pedestrian crossing nodes read (#700), and those on no TLM line.</summary>
        public int Crossings, CrossingsUnmatched;
        public int Restrictions, RestrictionsMapped, RestrictionsViaWay, RestrictionsUnmatched, RestrictionsOther, RestrictionsOutside;
        /// <summary>How each row was placed (overlay row, the way's own geometry, nearest line) and why some were not.</summary>
        public readonly SortedDictionary<string, int> How = new(StringComparer.Ordinal);
        public void Count(string kind, string what) => How[$"{kind}: {what}"] = How.GetValueOrDefault($"{kind}: {what}") + 1;
    }

    /// <summary>
    /// Where a node landed. <see cref="SameDir"/>: whether the OSM way runs in TLM drawing order,
    /// null when the node was only placed on the nearest line (no way to tell a direction).
    /// </summary>
    private readonly record struct Snapped(int Line, double Along, bool? SameDir, string How);

    // ---- snapping (pure, what --osm-check exercises) -----------------------------------------

    /// <summary>
    /// Snaps <paramref name="points"/> and <paramref name="relations"/> onto the TLM lines.
    /// <paramref name="osm"/> must carry node ids (<see cref="OsmWay.Refs"/>); <paramref name="position"/>
    /// gives a node's LV95 position, or null when it lies outside the region (its relation is skipped).
    /// </summary>
    public static (List<NodeRow> Rows, Stats Stats) Snap(IReadOnlyList<TlmLine> tlm, IReadOnlyList<OsmWay> osm,
        IReadOnlyList<Row> overlay, IReadOnlyList<Point> points, IReadOnlyList<PbfReader.Relation> relations,
        Func<long, (double E, double N)?> position)
    {
        var ctx = new Context(tlm, osm, overlay, points, relations);
        var rows = new List<NodeRow>();
        var stats = new Stats();

        foreach (var p in points)
        {
            bool signal = IsSignal(p.Tags), asl = IsAsl(p.Tags), crossing = IsCrossing(p.Tags);
            string kind0 = signal ? "signal" : asl ? "asl" : "crossing";
            if (signal)
            {
                stats.Signals++;
                if (p.Tags.GetValueOrDefault("highway") == "traffic_signals") stats.SignalsHighway++; else stats.SignalsCrossingOnly++;
                if (Direction(p.Tags) != "") stats.SignalsWithDir++;
            }
            if (asl) stats.Asl++;
            if (crossing) stats.Crossings++;

            var (snap, why) = ctx.SnapPoint(p);
            if (snap is not { } s)
            {
                stats.Count(kind0, "unmatched, " + why);
                if (signal) stats.SignalsUnmatched++;
                if (asl) stats.AslUnmatched++;
                if (crossing) stats.CrossingsUnmatched++;
                continue;
            }
            stats.Count(kind0, s.How + (why != "" ? " (" + why + ")" : ""));
            var line = tlm[s.Line];
            string junction = ctx.Degree(p.Id) >= 3 ? "node" : "";
            var (end, endE, endN) = ctx.NearEnd(s.Line, s.Along);
            if (junction == "") junction = end != "" ? "approach" : "mid";

            // the tag is relative to the OSM way; SameDir says how that way runs against TLM
            string dir = Direction(p.Tags) switch
            {
                "both" => "both",
                "forward" when s.SameDir is { } same => same ? "+" : "-",
                "backward" when s.SameDir is { } same => same ? "-" : "+",
                _ => "",
            };
            string tags = TagString(p.Tags);
            foreach (var kind in (string[])[signal ? "signal" : "", asl ? "asl" : "", crossing ? "crossing" : ""])
            {
                if (kind == "") continue;
                rows.Add(new NodeRow(kind, p.Id, p.E, p.N, line.Uuid, line.Part, s.Along, junction, end, endE, endN,
                    dir, kind == "crossing" ? p.Tags.GetValueOrDefault("crossing", "") : "", "", -1, "", tags));
                stats.Snapped[(kind, junction)] = stats.Snapped.GetValueOrDefault((kind, junction)) + 1;
            }
        }

        foreach (var rel in relations)
        {
            string value = rel.Tags.GetValueOrDefault("restriction") ?? rel.Tags.GetValueOrDefault("restriction:motorcar") ?? "";
            var via = rel.Members.Where(m => m.Role == "via").ToList();
            long from = rel.Members.FirstOrDefault(m => m.Role == "from" && m.Type == PbfReader.MemberType.Way).Ref;
            long to = rel.Members.FirstOrDefault(m => m.Role == "to" && m.Type == PbfReader.MemberType.Way).Ref;
            // the region test first, so every count below is per region: the via node when there is
            // one, else any member way (a via way, or a broken relation without a via)
            var viaNode = via.Count == 1 && via[0].Type == PbfReader.MemberType.Node ? position(via[0].Ref) : null;
            bool inRegion = via.Any(m => m.Type == PbfReader.MemberType.Node)
                ? viaNode != null
                : rel.Members.Any(m => m.Type == PbfReader.MemberType.Way && ctx.InRegion(m.Ref));
            if (!inRegion) { stats.RestrictionsOutside++; continue; }
            stats.Restrictions++;
            if (via.Any(m => m.Type == PbfReader.MemberType.Way)) { stats.RestrictionsViaWay++; continue; }
            if (!RestrictionValues.Contains(value)) { stats.RestrictionsOther++; continue; }
            if (viaNode is not { } v || from == 0 || to == 0)
            {
                stats.RestrictionsUnmatched++;
                stats.Count("restriction", "unmatched, malformed (no single via node, from or to way)");
                continue;
            }
            var (fromEnd, fromHow) = ctx.LineAtVia(from, via[0].Ref, v.E, v.N);
            var (toEnd, toHow) = ctx.LineAtVia(to, via[0].Ref, v.E, v.N);
            if (fromEnd is not var (fl, fEnd) || toEnd is not var (tl, tEnd))
            {
                stats.RestrictionsUnmatched++;
                stats.Count("restriction", "unmatched, " + (fromEnd == null ? "from: " + fromHow : "to: " + toHow));
                continue;
            }
            stats.Count("restriction", $"from by {fromHow}, to by {toHow}");
            double fAlong = fEnd == "start" ? 0 : ctx.Length(fl);
            var (fe, fn) = End(tlm[fl], fEnd);
            string except = rel.Tags.TryGetValue("except", out var ex) ? "except=" + Clean(ex).Replace(';', ',') : "";
            rows.Add(new NodeRow("restriction", rel.Id, v.E, v.N, tlm[fl].Uuid, tlm[fl].Part, fAlong, "via", fEnd, fe, fn,
                "", value, tlm[tl].Uuid, tlm[tl].Part, tEnd, except));
            stats.RestrictionsMapped++;
        }

        rows.Sort((x, y) =>
        {
            int c = string.CompareOrdinal(x.Kind, y.Kind);
            if (c == 0) c = string.CompareOrdinal(x.Uuid, y.Uuid);
            if (c == 0) c = x.Part.CompareTo(y.Part);
            if (c == 0) c = Math.Round(x.Along, 1).CompareTo(Math.Round(y.Along, 1));
            return c != 0 ? c : x.OsmId.CompareTo(y.OsmId);
        });
        return (rows, stats);
    }

    /// <summary><c>traffic_signals:direction</c>, else a plain <c>direction</c> (older tagging), if forward/backward/both.</summary>
    private static string Direction(Dictionary<string, string> t)
    {
        string d = t.GetValueOrDefault("traffic_signals:direction") ?? t.GetValueOrDefault("direction") ?? "";
        return d is "forward" or "backward" or "both" ? d : "";
    }

    private static (double E, double N) End(TlmLine line, string end) =>
        end == "start" ? (line.E[0], line.N[0]) : (line.E[^1], line.N[^1]);

    /// <summary><c>k=v;k=v</c> sorted by key; a <c>;</c> inside a value becomes <c>,</c>.</summary>
    private static string TagString(Dictionary<string, string> t) =>
        string.Join(';', t.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + Clean(kv.Value).Replace(';', ',')));

    private static string Clean(string v) => v.Replace('\t', ' ').Replace('\n', ' ');

    private sealed class Context
    {
        private readonly IReadOnlyList<TlmLine> _tlm;
        private readonly IReadOnlyList<OsmWay> _osm;
        private readonly Dictionary<(string, int), int> _line = new();
        private readonly double[][] _cum;
        private readonly Dictionary<long, List<Row>> _rowsByWay = new();
        /// <summary>For the nodes we look up (signals, bike boxes, via nodes): every (way piece, vertex) they sit on.</summary>
        private readonly Dictionary<long, List<(int Piece, int Vertex)>> _nodeWays = new();
        private readonly HashSet<long> _waysInRegion = new();
        private readonly SegmentGrid _grid = new();
        /// <summary>TLM line ends in 50 m cells: (line, true = start).</summary>
        private readonly Dictionary<(long, long), List<(int Line, bool Start)>> _ends = new();
        private readonly double _maxHalfWidth;
        private static readonly double CosMax = Math.Cos(MaxAngleDeg * Math.PI / 180);
        private const double Cell = 50.0;

        public Context(IReadOnlyList<TlmLine> tlm, IReadOnlyList<OsmWay> osm, IReadOnlyList<Row> overlay,
            IReadOnlyList<Point> points, IReadOnlyList<PbfReader.Relation> relations)
        {
            _tlm = tlm;
            _osm = osm;
            _cum = new double[tlm.Count][];
            for (int i = 0; i < tlm.Count; i++)
            {
                _line[(tlm[i].Uuid, tlm[i].Part)] = i;
                _cum[i] = Cumulative(tlm[i].E, tlm[i].N);
                _grid.Add(i, tlm[i].E, tlm[i].N);
                _maxHalfWidth = Math.Max(_maxHalfWidth, tlm[i].HalfWidth);
                foreach (bool start in (bool[])[true, false])
                {
                    var key = CellOf(start ? tlm[i].E[0] : tlm[i].E[^1], start ? tlm[i].N[0] : tlm[i].N[^1]);
                    if (!_ends.TryGetValue(key, out var list)) _ends[key] = list = new();
                    list.Add((i, start));
                }
            }
            foreach (var r in overlay)
            {
                if (!_rowsByWay.TryGetValue(r.Way, out var list)) _rowsByWay[r.Way] = list = new();
                list.Add(r);
            }
            foreach (var p in points) _nodeWays[p.Id] = new();
            foreach (var rel in relations)
                foreach (var m in rel.Members)
                    if (m.Type == PbfReader.MemberType.Node && m.Role == "via") _nodeWays[m.Ref] = new();
            for (int i = 0; i < osm.Count; i++)
            {
                _waysInRegion.Add(osm[i].Id);
                if (osm[i].Refs is not { } refs) continue;
                for (int k = 0; k < refs.Length; k++)
                    if (_nodeWays.TryGetValue(refs[k], out var list)) list.Add((i, k));
            }
        }

        private static (long, long) CellOf(double e, double n) => ((long)Math.Floor(e / Cell), (long)Math.Floor(n / Cell));

        public bool InRegion(long way) => _waysInRegion.Contains(way);

        public double Length(int line) => _cum[line][^1];

        private bool IsCar(int piece) => CarHighways.Contains(_osm[piece].Tags.GetValueOrDefault("highway") ?? "");

        /// <summary>Car ways meeting at a node: 2 for each one passing through, 1 for each one ending there.</summary>
        public int Degree(long node)
        {
            int degree = 0;
            if (!_nodeWays.TryGetValue(node, out var list)) return 0;
            foreach (var (piece, vertex) in list)
                if (IsCar(piece)) degree += vertex == 0 || vertex == _osm[piece].Refs!.Length - 1 ? 1 : 2;
            return degree;
        }

        /// <summary>The nearer line end if it is within <see cref="JunctionM"/> along the line, with its point.</summary>
        public (string End, double E, double N) NearEnd(int line, double along)
        {
            double length = Length(line);
            string end = along <= length - along ? "start" : "end";
            if (Math.Min(along, length - along) > JunctionM) return ("", 0, 0);
            var (e, n) = End(_tlm[line], end);
            return (end, e, n);
        }

        /// <summary>
        /// The TLM line a node lies on, car ways first: (1) the overlay rows of the ways through the
        /// node, the line passing nearest, preferring rows whose interval holds the projection;
        /// (2) the way's own direction at the node against the nearest parallel TLM line (a way too
        /// short to be conflated, typically split at a junction); (3) the nearest TLM line, no
        /// direction. The reason (1) failed comes back with (2) and (3).
        /// </summary>
        public (Snapped? Snap, string Why) SnapPoint(Point p)
        {
            var ways = _nodeWays[p.Id];
            bool anyRows = false;
            foreach (bool car in (bool[])[true, false])
            {
                Snapped? best = null;
                double bestScore = double.MaxValue;
                foreach (long wayId in ways.Where(w => IsCar(w.Piece) == car).Select(w => _osm[w.Piece].Id).Distinct().Order())
                {
                    if (!_rowsByWay.TryGetValue(wayId, out var rows)) continue;
                    anyRows = true;
                    foreach (var row in rows)
                    {
                        if (!_line.TryGetValue((row.Uuid, row.Part), out int li)) continue;
                        var (s, d) = Project(_tlm[li], _cum[li], p.E, p.N);
                        double outside = Math.Max(0, Math.Max(row.From - s, s - row.To));
                        if (d > _tlm[li].HalfWidth + SnapM || outside > JunctionM) continue;
                        double score = d + outside;
                        if (score < bestScore - 1e-9) (best, bestScore) = (new Snapped(li, s, row.SameDir, "overlay row"), score);
                    }
                }
                if (best != null) return (best, "");
            }
            string why = ways.Count == 0 ? "on no kept OSM way" : !anyRows ? "its ways not conflated" : "too far from its ways' lines";

            foreach (bool car in (bool[])[true, false])
            {
                Snapped? best = null;
                double bestD = double.MaxValue;
                foreach (var (piece, k) in ways.Where(w => IsCar(w.Piece) == car).OrderBy(w => _osm[w.Piece].Id).ThenBy(w => w.Piece))
                {
                    var w = _osm[piece];
                    var (ae, an) = Along(w.E, w.N, k, -1, TangentM);
                    var (be, bn) = Along(w.E, w.N, k, +1, TangentM);
                    double te = be - ae, tn = bn - an, tl = Math.Sqrt(te * te + tn * tn);
                    if (tl < 1e-6) continue;
                    var (li, same, _) = _grid.Search(p.E, p.N, te / tl, tn / tl, _maxHalfWidth + SnapM, CosMax, -1);
                    if (li < 0) continue;
                    var (s, d) = Project(_tlm[li], _cum[li], p.E, p.N);
                    if (d <= _tlm[li].HalfWidth + SnapM && d < bestD - 1e-9) (best, bestD) = (new Snapped(li, s, same, "way geometry"), d);
                }
                if (best != null) return (best, why);
            }

            var (idx, _, _) = _grid.Search(p.E, p.N, 1, 0, _maxHalfWidth + SnapM, 0, -1);
            if (idx < 0) return (null, why);
            var (along, dist) = Project(_tlm[idx], _cum[idx], p.E, p.N);
            return (dist <= _tlm[idx].HalfWidth + SnapM ? new Snapped(idx, along, null, "nearest line") : null, why);
        }

        /// <summary>
        /// The TLM line a restriction's from/to way maps to at the via node, and which end of it
        /// touches the junction: (1) the way's overlay rows, the line end nearest the via (within
        /// ViaM) that the row's interval reaches; (2) the way's own direction leaving the via node
        /// against the TLM line ends near it. Second item: how it was found, or why it was not.
        /// </summary>
        public ((int Line, string End)? Line, string How) LineAtVia(long wayId, long viaId, double ve, double vn)
        {
            (int, string)? best = null;
            double bestD = double.MaxValue;
            if (_rowsByWay.TryGetValue(wayId, out var rows))
            {
                foreach (var row in rows)
                {
                    if (!_line.TryGetValue((row.Uuid, row.Part), out int li)) continue;
                    double length = _cum[li][^1];
                    foreach (string end in (string[])["start", "end"])
                    {
                        // the way's own interval must reach that end of the line
                        if (end == "start" ? row.From > JunctionM : row.To < length - JunctionM) continue;
                        var (e, n) = End(_tlm[li], end);
                        double d = Math.Sqrt((e - ve) * (e - ve) + (n - vn) * (n - vn));
                        if (d <= ViaM && d < bestD - 1e-9) (best, bestD) = ((li, end), d);
                    }
                }
                if (best != null) return (best, "overlay row");
            }

            if (!InRegion(wayId)) return (null, "way not kept");
            var at = _nodeWays[viaId].Where(w => _osm[w.Piece].Id == wayId).ToList();
            if (at.Count == 0) return (null, "way does not pass the via node");
            foreach (var (piece, k) in at.OrderBy(w => w.Piece))
            {
                var w = _osm[piece];
                foreach (int side in (int[])[-1, +1])
                {
                    if (side < 0 ? k == 0 : k == w.E.Length - 1) continue;
                    // direction of the way leaving the via node, against each line's direction leaving its end
                    var (ae, an) = Along(w.E, w.N, k, side, TangentM);
                    double ue = ae - ve, un = an - vn, ul = Math.Sqrt(ue * ue + un * un);
                    if (ul < 1e-6) continue;
                    var (c0, r0) = CellOf(ve - ViaM, vn - ViaM);
                    var (c1, r1) = CellOf(ve + ViaM, vn + ViaM);
                    for (long c = c0; c <= c1; c++)
                        for (long r = r0; r <= r1; r++)
                        {
                            if (!_ends.TryGetValue((c, r), out var list)) continue;
                            foreach (var (li, start) in list)
                            {
                                var line = _tlm[li];
                                int i0 = start ? 0 : line.E.Length - 1;
                                double d = Math.Sqrt((line.E[i0] - ve) * (line.E[i0] - ve) + (line.N[i0] - vn) * (line.N[i0] - vn));
                                if (d > ViaM || d > bestD + 1e-9) continue;
                                var (be, bn) = Along(line.E, line.N, i0, start ? +1 : -1, TangentM);
                                double te = be - line.E[i0], tn = bn - line.N[i0], tl = Math.Sqrt(te * te + tn * tn);
                                if (tl < 1e-6 || (te * ue + tn * un) / (tl * ul) < CosMax) continue;
                                // equal distances (both ends of a short line) go to the lower line index, so reruns agree
                                if (d < bestD - 1e-9 || (best is var (bl, _) && li < bl))
                                    (best, bestD) = ((li, start ? "start" : "end"), d);
                            }
                        }
                }
            }
            return best != null ? (best, "way geometry") : (null, _rowsByWay.ContainsKey(wayId)
                ? "no line end near the via node" : "way not conflated and no parallel line end near the via node");
        }
    }

    /// <summary>The point <paramref name="dist"/> metres along a polyline from vertex <paramref name="k"/>, walking by <paramref name="step"/> (+1/-1), clamped to its end.</summary>
    private static (double E, double N) Along(double[] e, double[] n, int k, int step, double dist)
    {
        double left = dist;
        for (int i = k; i + step >= 0 && i + step < e.Length; i += step)
        {
            double de = e[i + step] - e[i], dn = n[i + step] - n[i], len = Math.Sqrt(de * de + dn * dn);
            if (len >= left) return (e[i] + de * left / len, n[i] + dn * left / len);
            left -= len;
        }
        return step > 0 ? (e[^1], n[^1]) : (e[0], n[0]);
    }

    /// <summary>Distance along a polyline of the point's projection onto it, and the distance to it.</summary>
    private static (double Along, double Dist) Project(TlmLine line, double[] cum, double pe, double pn)
    {
        double bestS = 0, bestD = double.MaxValue;
        for (int i = 0; i + 1 < line.E.Length; i++)
        {
            double de = line.E[i + 1] - line.E[i], dn = line.N[i + 1] - line.N[i];
            double len2 = de * de + dn * dn;
            double f = len2 < 1e-12 ? 0 : Math.Clamp(((pe - line.E[i]) * de + (pn - line.N[i]) * dn) / len2, 0, 1);
            double qe = line.E[i] + f * de - pe, qn = line.N[i] + f * dn - pn;
            double d = Math.Sqrt(qe * qe + qn * qn);
            if (d < bestD) (bestD, bestS) = (d, cum[i] + f * (cum[i + 1] - cum[i]));
        }
        return (bestS, bestD);
    }

    // ---- output --------------------------------------------------------------------------------

    public const string Header = "kind\tosm_id\te\tn\tuuid\tpart\talong_m\tjunction\tline_end\tend_e\tend_n\tdir\tvalue"
        + "\tto_uuid\tto_part\tto_end\ttags";

    public static string Format(List<NodeRow> rows, string pbfName, string tlmName, double minE, double minN, double maxE, double maxN)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append(inv, $"# osm_nodes v1 osm={pbfName} tlm={tlmName} bbox={minE:F0},{minN:F0},{maxE:F0},{maxN:F0} (c) OpenStreetMap contributors, ODbL\n");
        sb.Append(Header).Append('\n');
        foreach (var r in rows)
        {
            string endE = r.LineEnd == "" ? "" : r.EndE.ToString("F1", inv), endN = r.LineEnd == "" ? "" : r.EndN.ToString("F1", inv);
            string toPart = r.ToPart < 0 ? "" : r.ToPart.ToString(inv);
            sb.Append(inv, $"{r.Kind}\t{r.OsmId}\t{r.E:F1}\t{r.N:F1}\t{r.Uuid}\t{r.Part}\t{r.Along:F1}\t{r.Junction}\t{r.LineEnd}\t{endE}\t{endN}")
                .Append(inv, $"\t{r.Dir}\t{r.Value}\t{r.ToUuid}\t{toPart}\t{r.ToEnd}\t{r.Tags}\n");
        }
        return sb.ToString();
    }

    public static string Report(List<NodeRow> rows, Stats s, int turnLaneLines)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        int Snapped(string kind, string junction) => s.Snapped.GetValueOrDefault((kind, junction));
        sb.Append(inv, $"OSM nodes ({FileName}, {rows.Count} rows):\n");
        sb.Append(inv, $"  signal nodes read {s.Signals} (highway=traffic_signals {s.SignalsHighway}, crossing=traffic_signals only {s.SignalsCrossingOnly}, ")
            .Append(inv, $"with a direction {s.SignalsWithDir}): on a junction node {Snapped("signal", "node")}, ")
            .Append(inv, $"approach (<= {JunctionM:F0} m of a line end) {Snapped("signal", "approach")}, mid-block {Snapped("signal", "mid")}, ")
            .Append(inv, $"unmatched {s.SignalsUnmatched}\n");
        sb.Append(inv, $"  signal rows with a direction (+/-/both): {rows.Count(r => r.Kind == "signal" && r.Dir != "")}\n");
        sb.Append(inv, $"  asl nodes read {s.Asl}: junction node {Snapped("asl", "node")}, approach {Snapped("asl", "approach")}, ")
            .Append(inv, $"mid-block {Snapped("asl", "mid")}, unmatched {s.AslUnmatched}\n");
        sb.Append(inv, $"  crossing nodes read {s.Crossings} (#700; by crossing=*: {string.Join(", ", rows.Where(r => r.Kind == "crossing").GroupBy(r => r.Value == "" ? "-" : r.Value).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"))}): junction node {Snapped("crossing", "node")}, ")
            .Append(inv, $"approach {Snapped("crossing", "approach")}, mid-block {Snapped("crossing", "mid")}, unmatched {s.CrossingsUnmatched}\n");
        sb.Append(inv, $"  restrictions read {s.Restrictions}: mapped {s.RestrictionsMapped}, dropped via-way {s.RestrictionsViaWay}, ")
            .Append(inv, $"unmatched {s.RestrictionsUnmatched}, other values {s.RestrictionsOther} (outside the region, not counted: {s.RestrictionsOutside})\n");
        foreach (var g in rows.Where(r => r.Kind == "restriction").GroupBy(r => r.Value).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.Append(inv, $"    {g.Key} {g.Count()}\n");
        sb.Append("  how they were placed (overlay row > way geometry > nearest line; why the overlay row did not do it):\n");
        foreach (var (how, count) in s.How) sb.Append(inv, $"    {how}: {count}\n");
        sb.Append(inv, $"  TLM lines with turn:lanes: {turnLaneLines}\n");
        return sb.ToString();
    }

    // ---- self-check -----------------------------------------------------------------------------

    /// <summary>
    /// Part of --osm-check: a crossroads at (0, 0) with known answers for each kind of row.
    /// TLM: west and south drawn toward the junction, east and north away from it. OSM: ways split
    /// at junction node 12; east and west drawn east to west, south and north south to north. The
    /// first 8 m of the north street are their own way (201), too short to be conflated.
    /// </summary>
    public static List<string> SelfCheck()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }

        var tlm = new List<TlmLine>
        {
            new("west", 0, "6m Strasse", false, [-100, 0], [0, 0], 3),
            new("east", 0, "6m Strasse", false, [0, 100], [0, 0], 3),
            new("south", 0, "6m Strasse", false, [0, 0], [-100, 0], 3),
            new("north", 0, "6m Strasse", false, [0, 0], [0, 100], 3),
        };
        var pos = new Dictionary<long, (double E, double N)>
        {
            [10] = (100, 1), [11] = (15, 1), [12] = (0, 1), [13] = (-20, 1), [14] = (-100, 1),
            [20] = (1, -100), [24] = (1, -25), [15] = (1, -12), [23] = (1, 8), [22] = (1, 60), [21] = (1, 100), [30] = (-20, 60), [31] = (20, 60),
        };
        OsmWay Way(long id, string highway, params long[] refs) =>
            new(id, refs.Select(r => pos[r].E).ToArray(), refs.Select(r => pos[r].N).ToArray(), new() { ["highway"] = highway }, refs);
        var osm = new List<OsmWay>
        {
            Way(100, "secondary", 10, 11, 12), Way(101, "secondary", 12, 13, 14),
            Way(200, "residential", 20, 24, 15, 12), Way(201, "residential", 12, 23), Way(202, "residential", 23, 22, 21),
            Way(300, "footway", 30, 22, 31), // a crossing footway: node 22 is no junction
        };
        Point P(long id, params (string K, string V)[] tags) => new(id, pos[id].E, pos[id].N, tags.ToDictionary(t => t.K, t => t.V));
        var points = new List<Point>
        {
            P(11, ("highway", "traffic_signals"), ("traffic_signals:direction", "forward")),
            P(13, ("highway", "traffic_signals"), ("traffic_signals:direction", "backward"), ("button_operated", "no")),
            P(12, ("highway", "traffic_signals")),
            P(15, ("cycleway", "asl")),
            P(24, ("highway", "crossing"), ("crossing", "zebra")),
            P(22, ("highway", "crossing"), ("crossing", "traffic_signals"), ("button_operated", "yes")),
            P(23, ("highway", "traffic_signals"), ("traffic_signals:direction", "backward")),
        };
        PbfReader.Relation Rel(long id, string value, params PbfReader.Member[] m) =>
            new(id, m, new() { ["type"] = "restriction", ["restriction"] = value });
        var relations = new List<PbfReader.Relation>
        {
            // from the west, heading east: north is the left turn (its way 201 is not conflated)
            Rel(900, "no_left_turn", new(PbfReader.MemberType.Way, 101, "from"), new(PbfReader.MemberType.Node, 12, "via"),
                new(PbfReader.MemberType.Way, 201, "to")),
            Rel(901, "no_u_turn", new(PbfReader.MemberType.Way, 101, "from"), new(PbfReader.MemberType.Way, 100, "via"),
                new(PbfReader.MemberType.Way, 101, "to")),
            Rel(902, "only_straight_on", new(PbfReader.MemberType.Way, 200, "from"), new(PbfReader.MemberType.Node, 12, "via"),
                new(PbfReader.MemberType.Way, 202, "to")),
        };

        var overlay = Conflate(tlm, osm).Rows;
        Check(!overlay.Any(r => r.Way == 201), "way 201 (8 m) must not be conflated, or the fallback is not tested");
        var (rows, stats) = Snap(tlm, osm, overlay, points, relations, id => pos.TryGetValue(id, out var p) ? p : null);
        NodeRow? Get(string kind, long id) => rows.FirstOrDefault(r => r.Kind == kind && r.OsmId == id);

        var s11 = Get("signal", 11);
        Check(s11 is { Uuid: "east", Junction: "approach", LineEnd: "start", Dir: "-" } && Math.Abs(s11.Along - 15) < 0.01,
            $"signal 11: east line at 15 m, approach to its start, controls westbound traffic (-), got {s11}");
        var s13 = Get("signal", 13);
        Check(s13 is { Uuid: "west", Junction: "approach", LineEnd: "end", Dir: "+", EndE: 0, EndN: 0 } && Math.Abs(s13.Along - 80) < 0.01,
            $"signal 13: west line at 80 m, approach to its end, controls eastbound traffic (+), got {s13}");
        Check(s13?.Tags == "button_operated=no;highway=traffic_signals;traffic_signals:direction=backward", $"signal 13 tags, got '{s13?.Tags}'");
        var s12 = Get("signal", 12);
        Check(s12 is { Junction: "node", Dir: "" } && s12.LineEnd != "", $"signal 12: on the junction node, got {s12}");
        var s23 = Get("signal", 23);
        Check(s23 is { Uuid: "north", Junction: "approach", LineEnd: "start", Dir: "-" } && Math.Abs(s23.Along - 8) < 0.01,
            $"signal 23: on the short unconflated way, north line at 8 m by the way's geometry, southbound (-), got {s23}");
        var a15 = Get("asl", 15);
        Check(a15 is { Uuid: "south", Junction: "approach", LineEnd: "end" } && Math.Abs(a15.Along - 88) < 0.01,
            $"asl 15: south line at 88 m, approach to its end, got {a15}");
        var s22 = Get("signal", 22);
        Check(s22 is { Uuid: "north", Junction: "mid", LineEnd: "" } && s22.Tags.Contains("crossing=traffic_signals"),
            $"signal 22: pedestrian signal mid-block on the north line (footway node is no junction), got {s22}");
        var c24 = Get("crossing", 24);
        Check(c24 is { Uuid: "south", Junction: "approach", LineEnd: "end", Value: "zebra" } && Math.Abs(c24.Along - 75) < 0.01,
            $"crossing 24: a zebra on the south line at 75 m, approach to its end, got {c24}");
        var c22 = Get("crossing", 22);
        Check(c22 is { Junction: "mid", Value: "traffic_signals" } && s22 is not null, $"crossing 22: the pedestrian signal is a crossing row too, got {c22}");
        var r900 = Get("restriction", 900);
        Check(r900 is { Uuid: "west", LineEnd: "end", ToUuid: "north", ToPart: 0, ToEnd: "start", Value: "no_left_turn", Junction: "via", Along: 100 },
            $"restriction 900: from west (end) to north (start, by the way's geometry), got {r900}");
        var r902 = Get("restriction", 902);
        Check(r902 is { Uuid: "south", LineEnd: "end", ToUuid: "north", ToEnd: "start", Value: "only_straight_on" },
            $"restriction 902: from south (end) to north (start, way 202 starts 8 m up), got {r902}");
        Check(Get("restriction", 901) == null && stats.RestrictionsViaWay == 1, "restriction 901: via-way dropped and counted");
        Check(stats.Signals == 5 && stats.SignalsCrossingOnly == 1 && stats.SignalsWithDir == 3 && stats.Asl == 1,
            $"counts: 5 signals (1 crossing-only, 3 with a direction), 1 asl; got {stats.Signals}/{stats.SignalsCrossingOnly}/{stats.SignalsWithDir}/{stats.Asl}");
        Check(Format(rows, "a", "b", 0, 0, 1, 1)
              == Format(Snap(tlm, osm, Conflate(tlm, osm).Rows, points, relations, id => pos.TryGetValue(id, out var p) ? p : null).Rows, "a", "b", 0, 0, 1, 1),
            "deterministic nodes output");
        return fails;
    }
}
