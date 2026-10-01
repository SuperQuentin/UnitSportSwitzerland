using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Optional OpenStreetMap overlay (#118): conflates OSM ways onto swissTLM3D road lines and
/// writes the attributes TLM does not record (one-way, lanes, width, sidewalks, cycleways,
/// turn lanes, roundabouts, trams) to <c>&lt;temp&gt;/osm_overlay.tsv</c>, keyed by TLM uuid, part
/// and along-line interval. Geometry is never taken from OSM. Nothing reads the file yet: the
/// road network stage (#115) joins it. Format and key: docs/notes/tools/osm-overlay.md.
/// </summary>
public static class OsmOverlay
{
    public const string FileName = "osm_overlay.tsv";
    public const string ReportName = "osm_overlay_report.txt";

    /// <summary>Metres between the stations each TLM line is sampled at.</summary>
    private const double StepM = 5.0;
    /// <summary>Corridor = half the TLM carriageway width plus this, in metres.</summary>
    private const double ToleranceM = 5.0;
    /// <summary>Largest angle between the TLM line and an OSM segment that still counts as parallel.</summary>
    private const double MaxAngleDeg = 30.0;
    /// <summary>A run of one OSM way is kept if it is this long, or covers MinOverlapFraction of the line.</summary>
    private const double MinRunM = 15.0;
    private const double MinOverlapFraction = 0.5;
    /// <summary>A TLM line counts as matched when kept runs cover this share of it.</summary>
    private const double MatchedShare = 0.5;
    /// <summary>How far to look for the partner carriageway of a divided line.</summary>
    private const double PartnerSearchM = 40.0;

    private static readonly HashSet<string> TagKeys =
    [
        "highway", "railway", "area", "oneway", "junction", "lanes", "lanes:forward", "lanes:backward", "width",
        "sidewalk", "sidewalk:left", "sidewalk:right", "sidewalk:both",
        "cycleway", "cycleway:left", "cycleway:right", "cycleway:both",
        "turn:lanes", "turn:lanes:forward", "turn:lanes:backward",
    ];

    private static readonly HashSet<string> IgnoredHighways =
        ["proposed", "construction", "platform", "bus_stop", "elevator", "corridor", "abandoned", "razed", "no"];

    public sealed record TlmLine(string Uuid, int Part, string Objektart, bool Divided, double[] E, double[] N, double HalfWidth);

    public sealed record OsmWay(long Id, double[] E, double[] N, Dictionary<string, string> Tags)
    {
        public bool IsTram => Tags.GetValueOrDefault("railway") == "tram";
    }

    /// <summary>One output row: OSM attributes on [From, To] metres of a TLM line, in TLM's drawing direction.</summary>
    public sealed record Row(string Uuid, int Part, double From, double To, long Way, bool SameDir, string Highway,
        string OneWay, string Lanes, string LanesFwd, string LanesBwd, string Width,
        string SidewalkLeft, string SidewalkRight, string CyclewayLeft, string CyclewayRight,
        string TurnFwd, string TurnBwd, bool Roundabout, bool Tram);

    public sealed record Conflict(string Uuid, int Part, double From, double To, long Way, double E, double N, string Reason);

    public sealed record Result(List<Row> Rows, List<Conflict> Conflicts, Dictionary<string, (int Lines, int Matched, double Len, double MatchedLen)> PerClass);

    // ---- stage entry point -------------------------------------------------------------------

    public static int Run(string pbfPath, string tlmGpkg, string tempDir, IReadOnlyCollection<TileId> tiles, int jobs)
    {
        if (!File.Exists(pbfPath)) { Console.Error.WriteLine($"--osm-overlay: {pbfPath} not found"); return 2; }
        if (tiles.Count == 0) { Console.Error.WriteLine("--osm-overlay: no tiles"); return 2; }
        var clock = Stopwatch.StartNew();
        double minE = tiles.Min(t => t.MinE), maxE = tiles.Max(t => t.MinE) + ChunkFormat.TileSizeM;
        double minN = tiles.Min(t => t.MinN), maxN = tiles.Max(t => t.MinN) + ChunkFormat.TileSizeM;

        var tlm = LoadTlm(tlmGpkg, minE, minN, maxE, maxN);
        double tlmSec = clock.Elapsed.TotalSeconds;
        var osm = LoadOsm(pbfPath, minE - 200, minN - 200, maxE + 200, maxN + 200, jobs);
        double osmSec = clock.Elapsed.TotalSeconds - tlmSec;
        var result = Conflate(tlm, osm);
        double total = clock.Elapsed.TotalSeconds;

        Directory.CreateDirectory(tempDir);
        string outPath = Path.Combine(tempDir, FileName);
        File.WriteAllText(outPath, Format(result.Rows, Path.GetFileName(pbfPath), Path.GetFileName(tlmGpkg), minE, minN, maxE, maxN));
        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        string report = Report(result, tlm.Count, osm.Count) + MedianProbe(tlm, osm)
            + $"\ntiming: TLM {tlmSec:F1} s, OSM read {osmSec:F1} s, conflation {total - tlmSec - osmSec:F1} s, "
            + $"total {total:F1} s; peak working set {peak / 1048576.0:F0} MB\n";
        File.WriteAllText(Path.Combine(tempDir, ReportName), report);
        Console.WriteLine(report.Length > 6000 ? report[..6000] + "\n... (full report in " + ReportName + ")" : report);
        Console.WriteLine($"OSM overlay: {result.Rows.Count} intervals -> {outPath}");
        return 0;
    }

    private static List<TlmLine> LoadTlm(string gpkg, double minE, double minN, double maxE, double maxN)
    {
        var lines = new List<TlmLine>();
        using var conn = GeoPackageReader.Open(gpkg);
        using var cmd = GeoPackageReader.BboxQuery(conn, "tlm_strassen_strasse", "geom",
            ["uuid", "objektart", "richtungsgetrennt"], minE, minN, maxE, maxN);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(3)) continue;
            string uuid = r.GetString(0);
            string objektart = r.IsDBNull(1) ? "" : r.GetString(1);
            if (RoadFormat.IsNotDrivableSurface(objektart)) continue;
            var flags = RoadFormat.ParseFlags(null, null, null, r.IsDBNull(2) ? null : r.GetString(2));
            double half = RoadFormat.WidthFor(RoadFormat.ParseClass(objektart), flags) / 2;
            var parts = GeoPackageReader.ParseLines((byte[])r.GetValue(3));
            for (int p = 0; p < parts.Count; p++)
                if (parts[p].Count >= 2)
                    lines.Add(new TlmLine(uuid, p, objektart, (flags & RoadFlags.Divided) != 0, parts[p].E, parts[p].N, half));
        }
        return lines;
    }

    private static List<OsmWay> LoadOsm(string pbf, double minE, double minN, double maxE, double maxN, int jobs)
    {
        // a lat/lon box around the LV95 box; LV95 is not aligned with meridians, so take all four corners
        var corners = new[] { (minE, minN), (minE, maxN), (maxE, minN), (maxE, maxN) }
            .Select(c => SwissProjection.ToWgs84(c.Item1, c.Item2)).ToList();
        double la0 = corners.Min(c => c.Lat), la1 = corners.Max(c => c.Lat);
        double lo0 = corners.Min(c => c.Lon), lo1 = corners.Max(c => c.Lon);

        // ponytail: every node inside the box is held in one dictionary; fine for a region, about
        // 2 GB for the whole country. Two passes (ways first, then only their nodes) if that matters.
        var (nodes, ways) = PbfReader.Read(pbf, jobs,
            (lat, lon) => lat >= la0 && lat <= la1 && lon >= lo0 && lon <= lo1,
            tags => tags.GetValueOrDefault("area") != "yes"
                    && ((tags.TryGetValue("highway", out var h) && !IgnoredHighways.Contains(h))
                        || tags.GetValueOrDefault("railway") == "tram"),
            TagKeys);

        var result = new List<OsmWay>();
        foreach (var w in ways)
        {
            // a way leaving the box keeps each run of nodes that is inside it
            var e = new List<double>();
            var n = new List<double>();
            void Flush()
            {
                if (e.Count >= 2) result.Add(new OsmWay(w.Id, e.ToArray(), n.ToArray(), w.Tags));
                e.Clear(); n.Clear();
            }
            foreach (long id in w.Refs)
            {
                if (!nodes.TryGetValue(id, out var ll)) { Flush(); continue; }
                var (pe, pn) = SwissProjection.ToLv95(ll.Lat, ll.Lon);
                e.Add(pe); n.Add(pn);
            }
            Flush();
        }
        return result;
    }

    // ---- conflation (pure, what --osm-check exercises) ----------------------------------------

    public static Result Conflate(IReadOnlyList<TlmLine> tlm, IReadOnlyList<OsmWay> osm)
    {
        var roadGrid = new SegmentGrid();
        var tramGrid = new SegmentGrid();
        for (int i = 0; i < osm.Count; i++) (osm[i].IsTram ? tramGrid : roadGrid).Add(i, osm[i].E, osm[i].N);
        var dividedGrid = new SegmentGrid();
        for (int i = 0; i < tlm.Count; i++) if (tlm[i].Divided) dividedGrid.Add(i, tlm[i].E, tlm[i].N);

        double cosMax = Math.Cos(MaxAngleDeg * Math.PI / 180);
        var perLine = new (List<Row> Rows, List<Conflict> Conflicts, double Matched)[tlm.Count];
        Parallel.For(0, tlm.Count, li =>
        {
            var line = tlm[li];
            var cum = Cumulative(line.E, line.N);
            double length = cum[^1];
            int count = Math.Max(1, (int)Math.Ceiling(length / StepM));
            double radius = line.HalfWidth + ToleranceM;

            // each station: the nearest parallel OSM way (and its direction), and whether a tram runs there
            var way = new int[count];
            var same = new bool[count];
            var tram = new bool[count];
            for (int k = 0; k < count; k++)
            {
                double s = Math.Min(length, (k + 0.5) * StepM);
                var (pe, pn, te, tn) = At(line.E, line.N, cum, s);
                (way[k], same[k]) = roadGrid.Nearest(pe, pn, te, tn, radius, cosMax);
                tram[k] = tramGrid.Nearest(pe, pn, te, tn, radius, cosMax).Index >= 0;
            }

            // runs of one way in one direction; short ones at junctions are noise
            var rows = new List<Row>();
            var conflicts = new List<Conflict>();
            int expected = line.Divided ? PartnerDirection(tlm, dividedGrid, li, cum) : 0;
            double matched = 0;
            for (int a = 0; a < count;)
            {
                int b = a;
                while (b + 1 < count && way[b + 1] == way[a] && same[b + 1] == same[a]) b++;
                double from = a * StepM, to = Math.Min(length, (b + 1) * StepM);
                if (way[a] >= 0 && (to - from >= MinRunM || to - from >= MinOverlapFraction * length))
                {
                    var row = MakeRow(line, osm[way[a]], same[a], from, to, tram.AsSpan(a, b - a + 1));
                    var (me, mn, _, _) = At(line.E, line.N, cum, (from + to) / 2);
                    if (expected != 0 && row.OneWay is "1" or "-1" && int.Parse(row.OneWay) != expected)
                    {
                        conflicts.Add(new Conflict(line.Uuid, line.Part, from, to, row.Way, me, mn,
                            "OSM one-way points against the divided-carriageway direction (partner side, right-hand traffic); oneway dropped"));
                        row = row with { OneWay = "" };
                    }
                    else if (line.Divided && row.OneWay == "0")
                    {
                        conflicts.Add(new Conflict(line.Uuid, line.Part, from, to, row.Way, me, mn,
                            "OSM two-way on a TLM divided carriageway (OSM maps one way for both); oneway dropped"));
                        row = row with { OneWay = "" };
                    }
                    rows.Add(row);
                    matched += to - from;
                }
                a = b + 1;
            }
            perLine[li] = (rows, conflicts, matched);
        });

        var allRows = new List<Row>();
        var allConflicts = new List<Conflict>();
        var perClass = new Dictionary<string, (int, int, double, double)>();
        for (int i = 0; i < tlm.Count; i++)
        {
            allRows.AddRange(perLine[i].Rows);
            allConflicts.AddRange(perLine[i].Conflicts);
            double len = Cumulative(tlm[i].E, tlm[i].N)[^1];
            var (l, m, tl, tm) = perClass.GetValueOrDefault(tlm[i].Objektart);
            perClass[tlm[i].Objektart] = (l + 1, m + (perLine[i].Matched >= MatchedShare * len ? 1 : 0), tl + len, tm + perLine[i].Matched);
        }
        allRows.Sort((x, y) => (string.CompareOrdinal(x.Uuid, y.Uuid), x.Part.CompareTo(y.Part), x.From.CompareTo(y.From)) switch
        {
            (not 0 and var c, _, _) => c,
            (_, not 0 and var c, _) => c,
            (_, _, var c) => c,
        });
        allConflicts.Sort((x, y) => string.CompareOrdinal(x.Uuid, y.Uuid) is var c and not 0 ? c : x.From.CompareTo(y.From));
        return new Result(allRows, allConflicts, perClass);
    }

    /// <summary>
    /// Traffic direction of a divided carriageway inferred from its partner, the way LaneGraph does
    /// at runtime: with right-hand traffic the opposite carriageway lies to the left. +1 = drawing
    /// direction, -1 = against it, 0 = no parallel partner found.
    /// </summary>
    private static int PartnerDirection(IReadOnlyList<TlmLine> tlm, SegmentGrid grid, int self, double[] cum)
    {
        var line = tlm[self];
        var (pe, pn, te, tn) = At(line.E, line.N, cum, cum[^1] / 2);
        var (other, (qe, qn)) = grid.NearestPoint(pe, pn, te, tn, PartnerSearchM, 0.9, self);
        if (other < 0) return 0;
        double cross = te * (qn - pn) - tn * (qe - pe);
        return cross > 0 ? 1 : -1;
    }

    private static Row MakeRow(TlmLine line, OsmWay w, bool same, double from, double to, ReadOnlySpan<bool> tram)
    {
        var t = w.Tags;
        string hw = t.GetValueOrDefault("highway", "");
        bool roundabout = t.GetValueOrDefault("junction") is "roundabout" or "circular";

        // oneway in the OSM way's own direction: +1, -1, 0 (two-way), "" (reversible/unknown)
        int? osmDir = t.GetValueOrDefault("oneway") switch
        {
            "yes" or "true" or "1" => 1,
            "-1" or "reverse" => -1,
            "no" or "false" or "0" => 0,
            null => roundabout || hw == "motorway" ? 1 : 0,
            _ => null,
        };
        string oneway = osmDir is { } d ? (same ? d : -d).ToString(CultureInfo.InvariantCulture) : "";

        string Side(string key, string side)
        {
            if (t.TryGetValue($"{key}:{side}", out var v)) return v;
            if (t.TryGetValue($"{key}:both", out v)) return v;
            if (key == "sidewalk" && t.TryGetValue("sidewalk", out v))
                return v switch { "both" => "yes", "left" or "right" => v == side ? "yes" : "no", "none" => "no", _ => v };
            return key == "cycleway" && t.TryGetValue("cycleway", out v) ? v : "";
        }

        string fwdLanes = t.GetValueOrDefault("lanes:forward", ""), bwdLanes = t.GetValueOrDefault("lanes:backward", "");
        string fwdTurn = t.GetValueOrDefault("turn:lanes:forward", ""), bwdTurn = t.GetValueOrDefault("turn:lanes:backward", "");
        if (fwdTurn == "" && osmDir == 1) fwdTurn = t.GetValueOrDefault("turn:lanes", "");
        if (bwdTurn == "" && osmDir == -1) bwdTurn = t.GetValueOrDefault("turn:lanes", "");

        string sl = Side("sidewalk", "left"), sr = Side("sidewalk", "right");
        string cl = Side("cycleway", "left"), cr = Side("cycleway", "right");
        int tramCount = 0;
        foreach (bool b in tram) if (b) tramCount++;

        return new Row(line.Uuid, line.Part, Math.Round(from, 1), Math.Round(to, 1), w.Id, same, hw, oneway,
            Int(t.GetValueOrDefault("lanes")),
            Int(same ? fwdLanes : bwdLanes), Int(same ? bwdLanes : fwdLanes),
            Metres(t.GetValueOrDefault("width")),
            same ? sl : sr, same ? sr : sl, same ? cl : cr, same ? cr : cl,
            same ? fwdTurn : bwdTurn, same ? bwdTurn : fwdTurn,
            roundabout, tramCount * 2 >= tram.Length);
    }

    private static string Int(string? v) => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n.ToString(CultureInfo.InvariantCulture) : "";

    private static string Metres(string? v)
    {
        v = v?.Trim();
        if (v != null && v.EndsWith('m')) v = v[..^1].Trim();
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 && m < 100
            ? m.ToString("0.0#", CultureInfo.InvariantCulture) : "";
    }

    // ---- output --------------------------------------------------------------------------------

    public const string Header = "uuid\tpart\tfrom_m\tto_m\tosm_way\tdir\thighway\toneway\tlanes\tlanes_fwd\tlanes_bwd\twidth"
        + "\tsidewalk_left\tsidewalk_right\tcycleway_left\tcycleway_right\tturn_lanes_fwd\tturn_lanes_bwd\troundabout\ttram";

    public static string Format(List<Row> rows, string pbfName, string tlmName, double minE, double minN, double maxE, double maxN)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"# osm_overlay v1 osm={pbfName} tlm={tlmName} bbox={minE:F0},{minN:F0},{maxE:F0},{maxN:F0} (c) OpenStreetMap contributors, ODbL\n");
        sb.Append(Header).Append('\n');
        foreach (var r in rows)
            sb.Append(CultureInfo.InvariantCulture,
                $"{r.Uuid}\t{r.Part}\t{r.From:F1}\t{r.To:F1}\t{r.Way}\t{(r.SameDir ? '+' : '-')}\t{Clean(r.Highway)}\t{r.OneWay}\t{r.Lanes}\t{r.LanesFwd}\t{r.LanesBwd}\t{r.Width}"
                + $"\t{Clean(r.SidewalkLeft)}\t{Clean(r.SidewalkRight)}\t{Clean(r.CyclewayLeft)}\t{Clean(r.CyclewayRight)}"
                + $"\t{Clean(r.TurnFwd)}\t{Clean(r.TurnBwd)}\t{(r.Roundabout ? 1 : 0)}\t{(r.Tram ? 1 : 0)}\n");
        return sb.ToString();
    }

    private static string Clean(string v) => v.Replace('\t', ' ').Replace('\n', ' ');

    public static string Report(Result r, int tlmLines, int osmWays)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append(inv, $"OSM overlay match report: {tlmLines} TLM line parts, {osmWays} OSM way pieces, {r.Rows.Count} intervals\n");
        sb.Append("class                 lines  matched  lines%  length%\n");
        foreach (var (cls, (lines, matched, len, mlen)) in r.PerClass.OrderByDescending(kv => kv.Value.Lines).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(inv, $"{cls,-20} {lines,6} {matched,8} {100.0 * matched / lines,6:F1}% {100.0 * mlen / Math.Max(1, len),7:F1}%\n");

        var oneWay = r.Rows.Where(x => x.OneWay is "1" or "-1").ToList();
        sb.Append(inv, $"one-way intervals: {oneWay.Count} on {oneWay.Select(x => (x.Uuid, x.Part)).Distinct().Count()} TLM lines "
            + $"({oneWay.Where(x => x.Roundabout).Select(x => x.Uuid).Distinct().Count()} roundabout lines, "
            + $"{oneWay.Where(x => x.Highway is "motorway" or "trunk").Select(x => x.Uuid).Distinct().Count()} motorway/trunk lines)\n");
        sb.Append(inv, $"lines with: lanes {Lines(r, x => x.Lanes != "" || x.LanesFwd != "")}, width {Lines(r, x => x.Width != "")}, "
            + $"sidewalk {Lines(r, x => x.SidewalkLeft is not ("" or "no") || x.SidewalkRight is not ("" or "no"))}, "
            + $"cycleway {Lines(r, x => x.CyclewayLeft is not ("" or "no") || x.CyclewayRight is not ("" or "no"))}, "
            + $"highway=cycleway {Lines(r, x => x.Highway == "cycleway")}, turn:lanes {Lines(r, x => x.TurnFwd != "" || x.TurnBwd != "")}, "
            + $"tram {Lines(r, x => x.Tram)}\n");
        sb.Append(inv, $"conflicts: {r.Conflicts.Count}\n");
        foreach (var c in r.Conflicts)
            sb.Append(inv, $"  {c.Uuid}/{c.Part} {c.From:F0}-{c.To:F0} m way {c.Way} at {c.E:F0},{c.N:F0}: {c.Reason}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Motorway median measurement (#117): along every TLM Autobahn line, every 5 m, the distance
    /// to its partner carriageway and the signed offset of the OSM motorway way running the same
    /// direction (positive = away from the partner). OSM carriageways are traced on orthophotos
    /// at the middle of the lanes, so the offset says where the real carriageway lies relative to
    /// TLM's line. Report only; nothing is written from it.
    /// </summary>
    public static string MedianProbe(IReadOnlyList<TlmLine> tlm, IReadOnlyList<OsmWay> osm) =>
        "carriageway median probe (5 m stations; offset > 0 = OSM way lies away from the partner):\n"
        + MedianProbe(tlm, osm, "Autobahn", ["motorway"])
        + MedianProbe(tlm, osm, "Autostrasse", ["motorway", "trunk"])
        + MedianProbe(tlm, osm, "Einfahrt", ["motorway_link", "trunk_link"]);

    private static string MedianProbe(IReadOnlyList<TlmLine> tlm, IReadOnlyList<OsmWay> osm, string objektart, string[] highways)
    {
        var ways = new SegmentGrid();
        for (int i = 0; i < osm.Count; i++)
            if (highways.Contains(osm[i].Tags.GetValueOrDefault("highway"))) ways.Add(i, osm[i].E, osm[i].N);
        var partners = new SegmentGrid();
        for (int i = 0; i < tlm.Count; i++) if (tlm[i].Divided) partners.Add(i, tlm[i].E, tlm[i].N);

        var sep = new List<double>();
        var offset = new List<double>();
        var osmSep = new List<double>();
        for (int li = 0; li < tlm.Count; li++)
        {
            if (tlm[li].Objektart != objektart) continue;
            var line = tlm[li];
            var cum = Cumulative(line.E, line.N);
            for (double s = StepM / 2; s < cum[^1]; s += StepM)
            {
                var (pe, pn, te, tn) = At(line.E, line.N, cum, s);
                var (p, _, (qe, qn)) = partners.Search(pe, pn, te, tn, 25, 0.95, li);
                if (p < 0) continue;
                // beside it, not the next piece of the same carriageway just ahead
                if (Math.Abs(te * (qe - pe) + tn * (qn - pn)) > 1) continue;
                double left = te * (qn - pn) - tn * (qe - pe);   // > 0: partner on the left
                double d = Math.Abs(left);
                sep.Add(d);
                // right-hand traffic: partner on the left means traffic runs in drawing order
                bool forward = left > 0;
                var (w, same, (we, wn)) = ways.Search(pe, pn, te, tn, 10, 0.95, -1);
                if (w < 0 || same != forward || Math.Abs(te * (we - pe) + tn * (wn - pn)) > 1) continue;
                double wLeft = te * (wn - pn) - tn * (we - pe);
                double outward = forward ? -wLeft : wLeft;
                offset.Add(outward);
                osmSep.Add(d + 2 * outward);
            }
        }

        static string Q(List<double> v)
        {
            if (v.Count == 0) return "none";
            v.Sort();
            double At(double f) => v[Math.Min(v.Count - 1, (int)(f * v.Count))];
            return string.Create(CultureInfo.InvariantCulture,
                $"p10 {At(0.1):F1}  p25 {At(0.25):F1}  median {At(0.5):F1}  p75 {At(0.75):F1}  p90 {At(0.9):F1} m ({v.Count} stations)");
        }
        return $"  {objektart}\n"
            + $"    TLM partner distance         {Q(sep)}\n"
            + $"    OSM way offset, outward      {Q(offset)}\n"
            + $"    OSM carriageway separation   {Q(osmSep)}\n";
    }

    private static int Lines(Result r, Func<Row, bool> pick) => r.Rows.Where(pick).Select(x => (x.Uuid, x.Part)).Distinct().Count();

    // ---- geometry -------------------------------------------------------------------------------

    private static double[] Cumulative(double[] e, double[] n)
    {
        var cum = new double[e.Length];
        for (int i = 1; i < e.Length; i++) cum[i] = cum[i - 1] + Math.Sqrt(Sq(e[i] - e[i - 1]) + Sq(n[i] - n[i - 1]));
        return cum;
    }

    /// <summary>Position and unit tangent at distance s along a polyline.</summary>
    private static (double E, double N, double Te, double Tn) At(double[] e, double[] n, double[] cum, double s)
    {
        int i = Array.BinarySearch(cum, s);
        if (i < 0) i = ~i;
        i = Math.Clamp(i, 1, e.Length - 1);
        while (i < e.Length - 1 && cum[i] - cum[i - 1] < 1e-9) i++;
        double len = Math.Max(1e-9, cum[i] - cum[i - 1]);
        double f = Math.Clamp((s - cum[i - 1]) / len, 0, 1);
        return (e[i - 1] + f * (e[i] - e[i - 1]), n[i - 1] + f * (n[i] - n[i - 1]), (e[i] - e[i - 1]) / len, (n[i] - n[i - 1]) / len);
    }

    private static double Sq(double v) => v * v;

    /// <summary>Uniform grid of polyline segments, for "nearest parallel segment within r".</summary>
    private sealed class SegmentGrid
    {
        private const double Cell = 50.0;
        private readonly Dictionary<(long, long), List<(int Line, int Seg)>> _cells = new();
        private readonly List<(double[] E, double[] N)> _lines = new();
        private readonly Dictionary<int, int> _slot = new();

        public void Add(int id, double[] e, double[] n)
        {
            _slot[id] = _lines.Count;
            _lines.Add((e, n));
            for (int s = 0; s + 1 < e.Length; s++)
            {
                long c0 = (long)Math.Floor(Math.Min(e[s], e[s + 1]) / Cell), c1 = (long)Math.Floor(Math.Max(e[s], e[s + 1]) / Cell);
                long r0 = (long)Math.Floor(Math.Min(n[s], n[s + 1]) / Cell), r1 = (long)Math.Floor(Math.Max(n[s], n[s + 1]) / Cell);
                for (long c = c0; c <= c1; c++)
                    for (long r = r0; r <= r1; r++)
                    {
                        if (!_cells.TryGetValue((c, r), out var list)) _cells[(c, r)] = list = new();
                        list.Add((id, s));
                    }
            }
        }

        /// <summary>Nearest parallel segment's line id and whether it runs the same way; -1 if none.</summary>
        public (int Index, bool Same) Nearest(double pe, double pn, double te, double tn,
            double radius, double cosMax, int exclude = -1)
        {
            var (id, same, _) = Search(pe, pn, te, tn, radius, cosMax, exclude);
            return (id, same);
        }

        public (int Index, (double E, double N) Point) NearestPoint(double pe, double pn, double te, double tn,
            double radius, double cosMax, int exclude)
        {
            var (id, _, q) = Search(pe, pn, te, tn, radius, cosMax, exclude);
            return (id, q);
        }

        public (int, bool, (double, double)) Search(double pe, double pn, double te, double tn, double radius, double cosMax, int exclude)
        {
            int best = -1;
            bool bestSame = false;
            (double, double) bestQ = default;
            double bestD = double.MaxValue;
            long c0 = (long)Math.Floor((pe - radius) / Cell), c1 = (long)Math.Floor((pe + radius) / Cell);
            long r0 = (long)Math.Floor((pn - radius) / Cell), r1 = (long)Math.Floor((pn + radius) / Cell);
            for (long c = c0; c <= c1; c++)
                for (long r = r0; r <= r1; r++)
                {
                    if (!_cells.TryGetValue((c, r), out var list)) continue;
                    foreach (var (id, s) in list)
                    {
                        if (id == exclude) continue;
                        var (e, n) = _lines[_slot[id]];
                        double de = e[s + 1] - e[s], dn = n[s + 1] - n[s];
                        double len = Math.Sqrt(de * de + dn * dn);
                        if (len < 1e-6) continue;
                        double cos = (de * te + dn * tn) / len;
                        if (Math.Abs(cos) < cosMax) continue;
                        double f = Math.Clamp(((pe - e[s]) * de + (pn - n[s]) * dn) / (len * len), 0, 1);
                        double qe = e[s] + f * de, qn = n[s] + f * dn;
                        double d = Math.Sqrt(Sq(pe - qe) + Sq(pn - qn));
                        // ties (two ways meeting at a node) go to the lower id, so reruns agree
                        if (d > radius || d > bestD || (d == bestD && id > best)) continue;
                        (best, bestSame, bestQ, bestD) = (id, cos > 0, (qe, qn), d);
                    }
                }
            return (best, bestSame, bestQ);
        }
    }

    // ---- self-check -----------------------------------------------------------------------------

    /// <summary>
    /// --osm-check: synthetic TLM lines and OSM ways with known answers. Fails (exit 1) if the
    /// conflation's matching, direction handling or conflict rule breaks.
    /// </summary>
    public static int SelfCheck()
    {
        var tlm = new List<TlmLine>
        {
            // a 6 m street drawn west -> east
            new("street", 0, "6m Strasse", false, [0, 50, 100], [0, 0, 0], 3),
            // a divided pair: "south" drawn west -> east with its partner to the north (its left),
            // so traffic flows with the drawing; "north" drawn east -> west
            new("south", 0, "Autobahn", true, [0, 200], [100, 100], 3),
            new("north", 0, "Autobahn", true, [200, 0], [110, 110], 3),
        };
        var osm = new List<OsmWay>
        {
            // the street mapped east -> west, 1 m off, one-way in OSM's direction
            new(7, [100, 0], [1, 1], new()
            {
                ["highway"] = "secondary", ["oneway"] = "yes", ["lanes"] = "2", ["lanes:forward"] = "2",
                ["sidewalk"] = "left", ["cycleway:right"] = "lane", ["turn:lanes"] = "left|through", ["width"] = "7.5 m",
            }),
            // a perpendicular path across the street: must never match it
            new(9, [50, 50], [-50, 50], new() { ["highway"] = "footway" }),
            // the southern carriageway mapped against its traffic: a conflict, oneway dropped
            new(8, [200, 0], [100, 100], new() { ["highway"] = "motorway" }),
        };
        var r = Conflate(tlm, osm);
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }

        var street = r.Rows.Where(x => x.Uuid == "street").ToList();
        Check(street.Count == 1, $"street: one interval, got {street.Count}");
        if (street.Count == 1)
        {
            var s = street[0];
            Check(s.From == 0 && s.To == 100, $"street interval 0-100, got {s.From}-{s.To}");
            Check(s.Way == 7 && !s.SameDir, "street matched way 7, reversed");
            Check(s.OneWay == "-1", $"one-way against the TLM drawing, got '{s.OneWay}'");
            Check(s.LanesFwd == "" && s.LanesBwd == "2", $"lanes:forward lands on TLM backward, got {s.LanesFwd}/{s.LanesBwd}");
            Check(s.SidewalkLeft == "no" && s.SidewalkRight == "yes", $"OSM left sidewalk is TLM right, got {s.SidewalkLeft}/{s.SidewalkRight}");
            Check(s.CyclewayLeft == "lane" && s.CyclewayRight == "", $"OSM right cycleway is TLM left, got {s.CyclewayLeft}/{s.CyclewayRight}");
            Check(s.TurnBwd == "left|through" && s.TurnFwd == "", $"turn:lanes follows the one-way, got {s.TurnFwd}/{s.TurnBwd}");
            Check(s.Width == "7.5", $"width 7.5, got '{s.Width}'");
        }
        var south = r.Rows.Where(x => x.Uuid == "south").ToList();
        Check(south.Count == 1 && south[0].OneWay == "", "south: OSM direction conflicts with the partner rule, so oneway is dropped");
        Check(r.Conflicts.Count(c => c.Uuid == "south") == 1, "south: one conflict logged");
        Check(!r.Rows.Any(x => x.Uuid == "north"), "north: nothing within its corridor");
        Check(r.PerClass["6m Strasse"] == (1, 1, 100, 100), "per-class stats for the street");

        // same input, same bytes
        Check(Format(r.Rows, "a", "b", 0, 0, 1, 1) == Format(Conflate(tlm, osm).Rows, "a", "b", 0, 0, 1, 1), "deterministic output");

        foreach (var f in fails) Console.Error.WriteLine("FAIL " + f);
        Console.WriteLine(fails.Count == 0 ? "osm-check: OK" : $"osm-check: {fails.Count} failure(s)");
        return fails.Count == 0 ? 0 : 1;
    }
}
