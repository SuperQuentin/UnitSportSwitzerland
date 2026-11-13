namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// Raw lines joined back across tile cuts (#1660). The extractor cuts every line where the tiling cuts it (LV95 km lines,
/// leaf edges); two pieces that continue each other across an edge are one line again here, so nothing planned from it
/// depends on where the cut was. Two piece ends join when the first piece's last point and the second's first point
/// meet on the edge between their two tiles (within <see cref="MeetM"/>), the segment records are the same (class,
/// surface, flags, width, attributes) and, where both carry a key, the keys agree (uuid, part, the second's
/// <c>FromM</c> where the first ends). Keys only confirm: French BD TOPO and generated lines have none and join too.
/// </summary>
public static class LineJoin
{
    /// <summary>Two piece ends this close on the plane are one point (the pieces' float frames, ~0.1 mm).</summary>
    public const double MeetM = 0.005;

    /// <summary>
    /// A cut point this close to the straight line between its neighbours is no point of the line (#1691): the cut put it
    /// there, so the joined line drops it, and every tiling's line has the same vertices (m; a cut point is off the straight
    /// line by its frame's rounding, ~0.1 mm).
    /// </summary>
    public const double StraightM = 0.001;

    /// <summary>
    /// The lattice a source's points lie on (#1691), m: 2^-10, so a point is exact as a float in any frame snapped to 2 m within
    /// 16 km of it, and every tile, leaf or block frame reads it at the same place on the plane.
    /// </summary>
    public const double Lattice = 1.0 / 1024;

    /// <summary>A plane coordinate on <see cref="Lattice"/>.</summary>
    public static double OnLattice(double v) => Math.Round(v / Lattice) * Lattice;

    /// <summary>A line's point on the plane: x, y and its height (the segment's y).</summary>
    public readonly record struct Point(double X, double Y, float H);

    /// <summary>A raw piece as read: its tile, its index in the tile, its segment and key.</summary>
    public readonly record struct Piece(TileId Tile, int Index, RoadSegment Segment, RawRoads.Key? Key);

    /// <summary>A whole line: its points on the plane, the first piece's record and key, the pieces it was joined from.</summary>
    public sealed class Line
    {
        public required RoadSegment Template { get; init; }
        public required List<Point> Points { get; init; }
        public RawRoads.Key? Key { get; init; }
        public required List<Piece> Parts { get; init; }

        /// <summary>The line's first point lies on a tile edge with nothing joined before it: a cut whose other side was not read.</summary>
        public bool OpenStart { get; set; }
        public bool OpenEnd { get; set; }

        public double Length
        {
            get
            {
                double s = 0;
                for (int i = 1; i < Points.Count; i++) s += Math.Sqrt(Sq(Points[i].X - Points[i - 1].X) + Sq(Points[i].Y - Points[i - 1].Y));
                return s;
            }
        }
    }

    /// <summary>A piece's points on the plane.</summary>
    public static List<Point> PlanePoints(IUnits units, TileId tile, RoadSegment seg)
    {
        double west = units.West(tile), north = units.North(tile);
        var pts = new List<Point>(seg.PointCount);
        for (int i = 0; i < seg.PointCount; i++)
            pts.Add(new Point(west + seg.Points[i * 3], north - seg.Points[i * 3 + 2], seg.Points[i * 3 + 1]));
        return pts;
    }

    /// <summary>Whether two pieces may be one line (records and keys); the meeting point is checked apart.</summary>
    public static bool SameLine(RoadSegment a, RawRoads.Key? ka, double lengthA, RoadSegment b, RawRoads.Key? kb)
    {
        if (a.Class != b.Class || a.Surface != b.Surface || a.Flags != b.Flags || a.Width != b.Width || a.Attributes != b.Attributes) return false;
        if (ka is null != kb is null) return false;
        if (ka is { } x && kb is { } y)
        {
            if (x.Uuid != y.Uuid || x.Part != y.Part) return false;
            // the keys' metres are true metres, the plane's within 2e-4 (LV95 grid metres, #1507)
            if (Math.Abs(x.FromM + lengthA - y.FromM) > 0.5 + 5e-4 * lengthA) return false;
        }
        return true;
    }

    /// <summary>
    /// Joins the pieces into whole lines, in a fixed order (the pieces' order as given, a line starting at its first
    /// piece). A piece joins the next where exactly one piece continues it across a tile edge.
    /// </summary>
    public static List<Line> Join(IReadOnlyList<Piece> pieces, IUnits units)
    {
        int n = pieces.Count;
        var points = new List<Point>[n];
        var lengths = new double[n];
        for (int k = 0; k < n; k++)
        {
            points[k] = PlanePoints(units, pieces[k].Tile, pieces[k].Segment);
            double s = 0;
            for (int i = 1; i < points[k].Count; i++)
                s += Math.Sqrt(Sq(points[k][i].X - points[k][i - 1].X) + Sq(points[k][i].Y - points[k][i - 1].Y));
            lengths[k] = s;
        }
        // starts by 1 cm cell
        var starts = new Dictionary<(long, long), List<int>>();
        for (int k = 0; k < n; k++)
        {
            if (points[k].Count < 2) continue;
            var p = points[k][0];
            var cell = ((long)Math.Floor(p.X / 0.01), (long)Math.Floor(p.Y / 0.01));
            if (!starts.TryGetValue(cell, out var list)) starts[cell] = list = new();
            list.Add(k);
        }
        bool OnItsEdge(int k, Point p) =>
            units.OnEdge(pieces[k].Tile, p.X - units.West(pieces[k].Tile), units.North(pieces[k].Tile) - p.Y, MeetM);

        var next = new int[n];
        var prev = new int[n];
        Array.Fill(next, -1);
        Array.Fill(prev, -1);
        for (int k = 0; k < n; k++)
        {
            if (points[k].Count < 2) continue;
            var end = points[k][^1];
            if (!OnItsEdge(k, end)) continue;
            int found = -1, count = 0;
            long cx = (long)Math.Floor(end.X / 0.01), cy = (long)Math.Floor(end.Y / 0.01);
            for (long ix = cx - 1; ix <= cx + 1; ix++)
                for (long iy = cy - 1; iy <= cy + 1; iy++)
                    if (starts.TryGetValue((ix, iy), out var list))
                        foreach (int b in list)
                        {
                            if (b == k || pieces[b].Tile == pieces[k].Tile) continue;
                            var s = points[b][0];
                            if (Sq(s.X - end.X) + Sq(s.Y - end.Y) > MeetM * MeetM) continue;
                            if (!OnItsEdge(b, s)) continue;
                            if (!SameLine(pieces[k].Segment, pieces[k].Key, lengths[k], pieces[b].Segment, pieces[b].Key)) continue;
                            found = b;
                            count++;
                        }
            if (count == 1) next[k] = found;
        }
        // a piece continued by two (never seen in the data) or claimed twice joins nothing there
        var claims = new int[n];
        for (int k = 0; k < n; k++) if (next[k] >= 0) claims[next[k]]++;
        for (int k = 0; k < n; k++)
        {
            if (next[k] < 0) continue;
            if (claims[next[k]] != 1) { next[k] = -1; continue; }
            prev[next[k]] = k;
        }

        var lines = new List<Line>();
        var done = new bool[n];
        void Chain(int first)
        {
            var parts = new List<Piece>();
            var pts = new List<Point>();
            int last = first;
            for (int k = first; k >= 0 && !done[k]; k = next[k])
            {
                done[k] = true;
                parts.Add(pieces[k]);
                last = k;
                var p = points[k];
                // (#1691) the cut point the two pieces share is dropped where they run straight on through it
                if (pts.Count >= 2 && p.Count >= 2 && Straight(pts[^2], pts[^1], p[1])) pts.RemoveAt(pts.Count - 1);
                pts.AddRange(pts.Count == 0 ? p : p.Skip(1));
            }
            if (pts.Count == 0) return;
            var line = new Line { Template = pieces[first].Segment, Points = pts, Key = pieces[first].Key, Parts = parts };
            line.OpenStart = pts.Count >= 2 && OnItsEdge(first, pts[0]);
            line.OpenEnd = pts.Count >= 2 && OnItsEdge(last, pts[^1]);
            lines.Add(line);
        }
        for (int k = 0; k < n; k++)
            if (!done[k] && prev[k] < 0) Chain(k);
        for (int k = 0; k < n; k++)
            if (!done[k]) Chain(k);   // a ring of pieces: started at its first piece as given
        return lines;
    }

    /// <summary>
    /// A line as one segment in <paramref name="tile"/>'s frame (points past the tile's own square kept: every tile frame
    /// is a translation of the plane), the first piece's record otherwise.
    /// </summary>
    public static RoadSegment ToSegment(Line line, IUnits units, TileId tile) =>
        Segment(line.Template, line.Points, units, tile);

    /// <summary>Points on the plane as a segment in <paramref name="tile"/>'s frame, with <paramref name="template"/>'s record.</summary>
    public static RoadSegment Segment(RoadSegment template, IReadOnlyList<Point> pts, IUnits units, TileId tile)
    {
        double west = units.West(tile), north = units.North(tile);
        var xyz = new float[pts.Count * 3];
        for (int i = 0; i < pts.Count; i++)
        {
            xyz[i * 3] = (float)(pts[i].X - west);
            xyz[i * 3 + 1] = pts[i].H;
            xyz[i * 3 + 2] = (float)(north - pts[i].Y);
        }
        return new RoadSegment
        {
            Class = template.Class, Surface = template.Surface, Flags = template.Flags, Width = template.Width,
            Points = xyz, Attributes = template.Attributes,
        };
    }

    /// <summary>Whether <paramref name="j"/> lies between <paramref name="a"/> and <paramref name="b"/> within <see cref="StraightM"/> of the line through them.</summary>
    private static bool Straight(Point a, Point j, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return false;
        double t = ((j.X - a.X) * dx + (j.Y - a.Y) * dy) / len2;
        if (t <= 0 || t >= 1) return false;
        double ox = a.X + dx * t - j.X, oy = a.Y + dy * t - j.Y;
        return ox * ox + oy * oy <= StraightM * StraightM;
    }

    private static double Sq(double v) => v * v;
}
