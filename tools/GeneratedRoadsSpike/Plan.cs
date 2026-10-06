using System.Globalization;
using System.Text;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

/// <summary>A plan-view SVG of a generated town: water, buildings, roads (bridges, junction polygons), signals and signs.</summary>
static class Plan
{
    const double Half = 420;   // m round the town's centre

    public static void Write(string path, ProceduralWorld world, double cE, double cN, string refDir, string inputDir,
        List<TileId> tiles, string title)
    {
        var inv = CultureInfo.InvariantCulture;
        double e0 = cE - Half, n1 = cN + Half;   // top-left corner
        string X(double e) => (e - e0).ToString("F1", inv);
        string Y(double n) => (n1 - n).ToString("F1", inv);
        var sb = new StringBuilder();
        sb.Append(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {2 * Half} {2 * Half}\" width=\"1400\" height=\"1400\">\n");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#dfe8c8\"/>\n");

        // the river: wet channel cells on a 4 m grid, merged along each row
        const double step = 4;
        sb.Append("<g fill=\"#5aa0d8\">");
        for (double n = n1 - step / 2; n > n1 - 2 * Half; n -= step)
        {
            double? runStart = null;
            for (double e = e0 + step / 2; e <= e0 + 2 * Half + step; e += step)
            {
                bool wet = e <= e0 + 2 * Half && ProceduralWorld.IsWetChannel(e, n);
                if (wet && runStart is null) runStart = e;
                if (!wet && runStart is { } r0)
                {
                    sb.Append(inv, $"<rect x=\"{X(r0 - step / 2)}\" y=\"{Y(n + step / 2)}\" width=\"{(e - r0).ToString("F1", inv)}\" height=\"{step}\"/>");
                    runStart = null;
                }
            }
        }
        sb.Append("</g>\n");

        var roads = new List<(RoadTile Tile, RoadSegment Seg)>();
        var signals = new List<(double E, double N)>();
        var yields = new List<(double E, double N)>();
        var junctions = new List<string>();
        sb.Append("<g stroke=\"#555\" stroke-width=\"0.4\">");
        foreach (var id in tiles)
        {
            string rp = Path.Combine(refDir, RoadFormat.FileName(id));
            if (File.Exists(rp))
            {
                using var s = File.OpenRead(rp);
                var t = RoadCodec.Decode(s);
                foreach (var seg in t.Segments) roads.Add((t, seg));
                foreach (var sg in t.Signals) signals.Add((id.MinE + sg.X, id.MaxN - sg.Z));
                foreach (var p in t.PointProps.Where(p => p.Type == PointPropType.YieldSign)) yields.Add((id.MinE + p.X, id.MaxN - p.Z));
                foreach (var j in t.Junctions)
                {
                    var pts = Enumerable.Range(0, j.VertexCount).Select(i => (E: id.MinE + j.Vertices[i * 3], N: id.MaxN - j.Vertices[i * 3 + 2])).ToList();
                    foreach (var (a, b, c) in Triangles(j))
                        sb.Append($"<polygon fill=\"#8a8a8a\" points=\"{X(pts[a].E)},{Y(pts[a].N)} {X(pts[b].E)},{Y(pts[b].N)} {X(pts[c].E)},{Y(pts[c].N)}\"/>");
                }
            }
        }
        sb.Append("</g>\n");

        // buildings: the hull of each one's triangles in plan, by kind
        sb.Append("<g stroke=\"#333\" stroke-width=\"0.3\">");
        foreach (var id in tiles)
        {
            string bp = Path.Combine(inputDir, BuildingFormat.FileName(id));
            if (!File.Exists(bp)) continue;
            using var s = File.OpenRead(bp);
            foreach (var b in BuildingCodec.Decode(s).Buildings)
            {
                var pts = new List<(double X, double Y)>();
                for (int i = 0; i < b.Triangles.Length; i += 3) pts.Add((id.MinE + b.Triangles[i], id.MaxN - b.Triangles[i + 2]));
                var hull = Hull(pts);
                string fill = b.Kind switch
                {
                    BuildingKind.Apartment => "#b97a6a", BuildingKind.Commercial => "#d9a441", BuildingKind.Sacral => "#8e6bb0",
                    BuildingKind.Agricultural => "#9a8456", BuildingKind.Garage => "#9aa0a6", _ => "#d2b48c",
                };
                sb.Append($"<polygon fill=\"{fill}\" points=\"{string.Join(' ', hull.Select(h => $"{X(h.X)},{Y(h.Y)}"))}\"/>");
            }
        }
        sb.Append("</g>\n");

        // roads, widest first so the narrow ones draw over them; a bridge is outlined in red
        foreach (var (t, seg) in roads.OrderByDescending(r => r.Seg.Width))
        {
            if (seg.Class > RoadClass.Square && seg.Class != RoadClass.Railway) continue;
            var line = string.Join(' ', Enumerable.Range(0, seg.PointCount).Select(i => $"{X(t.Id.MinE + seg.Points[i * 3])},{Y(t.Id.MaxN - seg.Points[i * 3 + 2])}"));
            bool bridge = (seg.Flags & RoadFlags.Bridge) != 0;
            if (bridge) sb.Append(inv, $"<polyline fill=\"none\" stroke=\"#c0392b\" stroke-width=\"{seg.Width + 1.6:F1}\" points=\"{line}\"/>");
            sb.Append(inv, $"<polyline fill=\"none\" stroke=\"{(bridge ? "#e8e0d0" : "#6b6b6b")}\" stroke-width=\"{seg.Width:F1}\" stroke-linejoin=\"round\" points=\"{line}\"/>");
        }
        foreach (var (e, n) in yields) sb.Append($"<circle cx=\"{X(e)}\" cy=\"{Y(n)}\" r=\"1.2\" fill=\"#e67e22\"/>");
        foreach (var (e, n) in signals) sb.Append($"<circle cx=\"{X(e)}\" cy=\"{Y(n)}\" r=\"4\" fill=\"#e74c3c\" stroke=\"#fff\" stroke-width=\"1\"/>");
        sb.Append(inv, $"<text x=\"8\" y=\"20\" font-family=\"sans-serif\" font-size=\"16\">{title} - 840 m square; red ring = bridge, red dot = traffic lights, orange = yield sign, blue = river</text>\n");
        sb.Append("</svg>\n");
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"  plan view: {path}");
    }

    static IEnumerable<(int, int, int)> Triangles(RoadJunction j)
    {
        for (int i = 0; i + 2 < j.Indices.Length; i += 3) yield return (j.Indices[i], j.Indices[i + 1], j.Indices[i + 2]);
    }

    static List<(double X, double Y)> Hull(List<(double X, double Y)> pts)
    {
        var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3) return p;
        double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<(double X, double Y)>();
        foreach (var q in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1); h.Add(p[i]); }
        h.RemoveAt(h.Count - 1);
        return h;
    }
}
