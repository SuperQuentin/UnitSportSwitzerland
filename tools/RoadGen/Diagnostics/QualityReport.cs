namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;

/// <summary>
/// Numbers that say whether the output is actually better, rather than whether it looks
/// better in one screenshot.
/// </summary>
/// <param name="OverlapArea">
/// Square metres of ground covered by more than one carriageway <i>on the same layer</i>.
/// This is the merge problem measured directly. Layers are excluded from each other because
/// a bridge over a road is supposed to overlap it in plan view.
/// </param>
/// <param name="WorstTurn">
/// Sharpest heading change between consecutive tessellation segments, in degrees. The
/// fluidity number: a raw surveyed polyline turns tens of degrees at a vertex, a clothoid
/// alignment sampled to a 5 cm chord tolerance turns a fraction of one.
/// </param>
public sealed record QualityReport(
    int Links,
    int Nodes,
    int Junctions,
    int ClampedArms,
    double CarriagewayArea,
    double JunctionArea,
    double OverlapArea,
    /// <summary>Two carriageways on top of each other — the defect this tool exists to remove.</summary>
    double OverlapRoadRoad,
    /// <summary>Two junction polygons on top of each other: nodes closer than their own trims.</summary>
    double OverlapJunctionJunction,
    /// <summary>A junction polygon over a carriageway, i.e. a trim that did not reach far enough.</summary>
    double OverlapRoadJunction,
    /// <summary>
    /// Of the road/road overlap, how much sits away from any node. This is the part junction
    /// trimming cannot touch: two separately surveyed centrelines running parallel closer
    /// together than their inferred widths.
    /// </summary>
    double OverlapInCorridors,
    double WorstTurn,
    double WorstCurvatureJump,
    int RibbonVertices,
    int MarkingRuns,
    IReadOnlyList<(string Pair, double Area)>? TopOverlapPairs = null)
{
    public double OverlapPercent => CarriagewayArea < 1e-6 ? 0 : 100.0 * OverlapArea / CarriagewayArea;

    public string Format(string title)
    {
        var c = CultureInfo.InvariantCulture;
        string pairs = TopOverlapPairs is { Count: > 0 }
            ? Environment.NewLine + "                worst pairs: "
              + string.Join(", ", TopOverlapPairs.Take(4)
                  .Select(p => $"{p.Pair} {p.Area.ToString("N0", c)} m²"))
            : "";

        return $"""
            {title}
              links {Links}   nodes {Nodes}   junctions {Junctions}{(ClampedArms > 0 ? $"   arms clamped {ClampedArms}" : "")}
              carriageway   {CarriagewayArea.ToString("N0", c)} m²   junction area {JunctionArea.ToString("N0", c)} m²
              OVERLAP       {OverlapArea.ToString("N1", c)} m²  ({OverlapPercent.ToString("F2", c)}% of carriageway)
                            road/road {OverlapRoadRoad.ToString("N1", c)} (of which {OverlapInCorridors.ToString("N1", c)} away from any node)
                            junction/junction {OverlapJunctionJunction.ToString("N1", c)}   road/junction {OverlapRoadJunction.ToString("N1", c)}{pairs}
              worst turn    {WorstTurn.ToString("F1", c)}° between segments
              curvature     worst jump {WorstCurvatureJump.ToString("F4", c)} 1/m across piece joins
              output        {RibbonVertices.ToString("N0", c)} ribbon vertices, {MarkingRuns.ToString("N0", c)} marking runs
            """;
    }
}

public static class QualityAnalyzer
{
    /// <summary>
    /// Areas and counts only, with no overlap rasterisation.
    ///
    /// <para>
    /// The full analysis stamps every surface onto a quarter-metre grid, which is the right
    /// price for answering "did this actually help" on a handful of tiles and completely the
    /// wrong one for rewriting a whole country. This is what a production run uses.
    /// </para>
    /// </summary>
    public static QualityReport Summarize(
        IReadOnlyList<Ribbon> ribbons, IReadOnlyList<Junction> junctions, int links, int nodes)
    {
        double carriageway = 0;
        int vertices = 0;
        foreach (var ribbon in ribbons)
        {
            if (ribbon.IsEmpty) continue;
            carriageway += ribbon.Area;
            vertices += ribbon.Stations.Count * 2;
        }

        return new QualityReport(
            links, nodes, junctions.Count,
            junctions.Sum(j => j.Arms.Count(a => a.TrimWasClamped)),
            carriageway, junctions.Sum(j => j.Area),
            0, 0, 0, 0, 0, 0, 0, vertices, 0);
    }

    /// <summary>
    /// Measures overlap by stamping every surface onto a grid and counting cells hit twice.
    ///
    /// <para>
    /// Exact polygon–polygon area would need a clipper and would spend most of its time on
    /// pairs that do not touch. Rasterising at a fraction of a lane width answers the only
    /// question being asked — "how much ground is painted twice" — to well inside the
    /// precision anyone can see, and it degrades gracefully instead of falling over on the
    /// self-touching rings real data produces.
    /// </para>
    /// </summary>
    public static QualityReport Analyze(
        IReadOnlyList<Ribbon> ribbons,
        IReadOnlyList<Junction> junctions,
        int links, int nodes,
        double cellSize = 0.25,
        IReadOnlyList<Vec2>? nodePositions = null,
        double nodeReach = 30.0)
    {
        // road and junction hits are counted separately so the report can say *which* surfaces
        // are colliding — "overlap went up" is not actionable, "junction polygons are landing
        // on each other" is
        var roads = new Dictionary<(long, long, int), int>();
        var caps = new Dictionary<(long, long, int), int>();

        // remember the first two profiles to claim each cell, so the report can name which
        // kinds of road are landing on each other rather than only how much
        var firstOwner = new Dictionary<(long, long, int), string>();
        var pairArea = new Dictionary<string, double>();

        double carriageway = 0;
        int vertices = 0;
        foreach (var ribbon in ribbons)
        {
            if (ribbon.IsEmpty) continue;
            carriageway += ribbon.Area;
            vertices += ribbon.Stations.Count * 2;

            for (int i = 1; i < ribbon.Stations.Count; i++)
                StampQuad(roads, cellSize, ribbon.Layer,
                    ribbon.Left[i - 1], ribbon.Left[i], ribbon.Right[i], ribbon.Right[i - 1],
                    ribbon.Profile.Name, firstOwner, pairArea, cellSize * cellSize);
        }

        double junctionArea = 0;
        foreach (var junction in junctions)
        {
            junctionArea += junction.Area;
            StampPolygon(caps, cellSize, junction.Layer, junction.Boundary);
        }

        double cellArea = cellSize * cellSize;
        double roadRoad = 0, capCap = 0, cross = 0, corridors = 0;

        // a coarse grid of node positions, so "is this overlap anywhere near a junction" is a
        // lookup rather than a scan over every node
        var nodeGrid = new Dictionary<(long, long), List<Vec2>>();
        if (nodePositions is not null)
            foreach (var p in nodePositions)
            {
                var key = ((long)Math.Floor(p.X / nodeReach), (long)Math.Floor(p.Y / nodeReach));
                if (!nodeGrid.TryGetValue(key, out var list)) nodeGrid[key] = list = new List<Vec2>();
                list.Add(p);
            }

        foreach (var (key, count) in roads)
        {
            if (count > 1)
            {
                double area = cellArea * (count - 1);
                roadRoad += area;

                if (nodePositions is not null)
                {
                    var centre = new Vec2((key.Item1 + 0.5) * cellSize, (key.Item2 + 0.5) * cellSize);
                    if (!NearAnyNode(nodeGrid, centre, nodeReach)) corridors += area;
                }
            }
            if (caps.ContainsKey(key)) cross += cellArea;
        }
        foreach (var count in caps.Values)
            if (count > 1) capCap += cellArea * (count - 1);

        double overlap = roadRoad + capCap + cross;

        double worstTurn = 0;
        foreach (var ribbon in ribbons)
        {
            for (int i = 2; i < ribbon.Stations.Count; i++)
            {
                var a = ribbon.Stations[i - 1].Position - ribbon.Stations[i - 2].Position;
                var b = ribbon.Stations[i].Position - ribbon.Stations[i - 1].Position;
                if (a.LengthSquared < 1e-12 || b.LengthSquared < 1e-12) continue;
                double turn = Math.Abs(Angles.Delta(a.Heading, b.Heading)) * 180.0 / Math.PI;
                if (turn > worstTurn) worstTurn = turn;
            }
        }

        return new QualityReport(
            links, nodes, junctions.Count,
            junctions.Sum(j => j.Arms.Count(a => a.TrimWasClamped)),
            carriageway, junctionArea, overlap, roadRoad, capCap, cross, corridors,
            worstTurn, 0, vertices, 0,
            pairArea.OrderByDescending(kv => kv.Value).Take(6)
                    .Select(kv => (kv.Key, kv.Value)).ToList());
    }

    private static bool NearAnyNode(Dictionary<(long, long), List<Vec2>> grid, Vec2 p, double reach)
    {
        long cx = (long)Math.Floor(p.X / reach), cy = (long)Math.Floor(p.Y / reach);
        for (long dx = -1; dx <= 1; dx++)
        for (long dy = -1; dy <= 1; dy++)
        {
            if (!grid.TryGetValue((cx + dx, cy + dy), out var list)) continue;
            foreach (var q in list) if (p.DistanceSquaredTo(q) <= reach * reach) return true;
        }
        return false;
    }

    private static void StampQuad(Dictionary<(long, long, int), int> counts, double cell, int layer,
        Vec2 a, Vec2 b, Vec2 c, Vec2 d, string owner,
        Dictionary<(long, long, int), string> firstOwner,
        Dictionary<string, double> pairArea, double cellArea)
    {
        StampPolygon(counts, cell, layer, new List<Vec2> { a, b, c, d }, owner, firstOwner, pairArea, cellArea);
    }

    private static void StampPolygon(Dictionary<(long, long, int), int> counts, double cell, int layer,
        List<Vec2> polygon,
        string? owner = null,
        Dictionary<(long, long, int), string>? firstOwner = null,
        Dictionary<string, double>? pairArea = null,
        double cellArea = 0)
    {
        if (polygon.Count < 3) return;

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in polygon)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        long x0 = (long)Math.Floor(minX / cell), x1 = (long)Math.Floor(maxX / cell);
        long y0 = (long)Math.Floor(minY / cell), y1 = (long)Math.Floor(maxY / cell);
        if ((x1 - x0 + 1) * (y1 - y0 + 1) > 4_000_000) return;   // pathological shape, skip it

        var seen = new HashSet<(long, long)>();
        for (long gx = x0; gx <= x1; gx++)
        for (long gy = y0; gy <= y1; gy++)
        {
            var centre = new Vec2((gx + 0.5) * cell, (gy + 0.5) * cell);
            if (!Contains(polygon, centre)) continue;
            if (!seen.Add((gx, gy))) continue;
            var key = (gx, gy, layer);
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;

            if (owner is null || firstOwner is null || pairArea is null) continue;
            if (!firstOwner.TryGetValue(key, out string? held)) { firstOwner[key] = owner; continue; }

            // name the pair in a stable order so "track over path" and "path over track" are
            // the same entry
            string pair = string.CompareOrdinal(held, owner) <= 0 ? $"{held}+{owner}" : $"{owner}+{held}";
            pairArea[pair] = pairArea.TryGetValue(pair, out double area) ? area + cellArea : cellArea;
        }
    }

    private static bool Contains(List<Vec2> polygon, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            if (polygon[i].Y > p.Y != polygon[j].Y > p.Y &&
                p.X < (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y)
                      / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        }
        return inside;
    }
}
