namespace UnitSport.Tools.RoadGen.Diagnostics;

using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;

/// <summary>
/// Plan view of built street tiles for review (#119): <c>RoadGen --street-svg E,N [--size M]
/// --chunks DIR --svg FILE</c>. Building walls black, carriageways and junction caps grey, bridges
/// slate blue, each
/// sidewalk as a band beside its carriageway coloured by width (narrow red, 1.5-2.5 m tan,
/// wider orange, flush squares lilac), urban roads with no sidewalk on a side as a thin red edge,
/// retaining walls blue. Bike infrastructure (#120): a separated path as a band coloured by its
/// layout (1 sky, 2 blue, 3 teal, 4 green, 5 purple) with its grass strips green, the sidewalk
/// behind it; bike paint on top: yellow lines, red crossings, symbols as yellow dots. Reads the
/// final <c>.road</c> and <c>.bldg</c> files; nothing is built.
/// </summary>
public static class StreetView
{

    /// <summary>Bike path colour by layout 1..5 (#120).</summary>
    private static readonly string[] LayoutColours = ["#000", "#5fb4e8", "#2a6fd6", "#1fa39a", "#3c9a3c", "#8e5bc2"];

    public static int Run(string chunks, double e, double n, double size, string outFile, Action<string> log)
    {
        double minE = e - size / 2, maxN = n + size / 2;
        double PxPerM = Math.Max(4, 1200 / size);
        var c = CultureInfo.InvariantCulture;
        string X(double east) => ((east - minE) * PxPerM).ToString("F1", c);
        string Y(double north) => ((maxN - north) * PxPerM).ToString("F1", c);
        int px = (int)Math.Round(size * PxPerM);

        var svg = new StringBuilder();
        svg.Append(c, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{px}\" height=\"{px}\" viewBox=\"0 0 {px} {px}\">\n");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#eef0e6\"/>\n");

        var tiles = new List<TileId>();
        for (int te = (int)Math.Floor(minE / 1000); te <= (int)Math.Floor((minE + size) / 1000); te++)
            for (int tn = (int)Math.Floor((maxN - size) / 1000); tn <= (int)Math.Floor(maxN / 1000); tn++)
                tiles.Add(new TileId(te, tn));

        int segments = 0, sides = 0, tracks = 0;
        var bikes = new StringBuilder();
        var roads = new StringBuilder();
        var walks = new StringBuilder();
        var walls = new StringBuilder();
        foreach (var id in tiles)
        {
            string bpath = System.IO.Path.Combine(chunks, BuildingFormat.FileName(id));
            if (File.Exists(bpath))
            {
                using var bs = File.OpenRead(bpath);
                foreach (var b in BuildingCodec.Decode(bs).Buildings)
                {
                    var t = b.Triangles;
                    for (int k = 0; k + 8 < t.Length; k += 9)
                    {
                        double ux = t[k + 3] - t[k], uy = t[k + 4] - t[k + 1], uz = t[k + 5] - t[k + 2];
                        double vx = t[k + 6] - t[k], vy = t[k + 7] - t[k + 1], vz = t[k + 8] - t[k + 2];
                        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len < 1e-9 || Math.Abs(ny) / len > 0.3) continue;
                        walls.Append(c, $"<path d=\"M{X(id.MinE + t[k])} {Y(id.MaxN - t[k + 2])}L{X(id.MinE + t[k + 3])} {Y(id.MaxN - t[k + 5])}L{X(id.MinE + t[k + 6])} {Y(id.MaxN - t[k + 8])}Z\"/>");
                    }
                }
            }

            string rpath = System.IO.Path.Combine(chunks, RoadFormat.FileName(id));
            if (!File.Exists(rpath)) continue;
            RoadTile tile;
            using (var rs = File.OpenRead(rpath)) tile = RoadCodec.Decode(rs);

            foreach (var j in tile.Junctions)
            {
                var v = j.Vertices;
                for (int k = 0; k + 2 < j.Indices.Length; k += 3)
                {
                    int a = j.Indices[k] * 3, b2 = j.Indices[k + 1] * 3, c2 = j.Indices[k + 2] * 3;
                    roads.Append(c, $"<path d=\"M{X(id.MinE + v[a])} {Y(id.MaxN - v[a + 2])}L{X(id.MinE + v[b2])} {Y(id.MaxN - v[b2 + 2])}L{X(id.MinE + v[c2])} {Y(id.MaxN - v[c2 + 2])}Z\" fill=\"#9a9a9a\"/>");
                }
            }

            foreach (var seg in tile.Segments)
            {
                if (seg.PointCount < 2 || RoadFormat.IsAerial(seg.Class) || RoadFormat.IsWall(seg.Class)) continue;
                var pts = new (double E, double N)[seg.PointCount];
                for (int i = 0; i < seg.PointCount; i++) pts[i] = (id.MinE + seg.Points[i * 3], id.MaxN - seg.Points[i * 3 + 2]);
                bool bridge = (seg.Flags & RoadFlags.Bridge) != 0;
                bool tunnel = (seg.Flags & RoadFlags.Tunnel) != 0;
                string colour = RoadFormat.IsWatercourse(seg.Class) ? "#7fb2e0" : bridge ? "#5a5a8a" : tunnel ? "#d070d0" : seg.Class == RoadClass.Railway ? "#6b5a4a"
                    : seg.Class >= RoadClass.Track ? "#c9b48a" : "#9a9a9a";
                double half = Math.Max(seg.Width, seg.Attributes.WidthCm / 100.0) * 0.5;
                roads.Append(c, $"<path d=\"{Offset(pts, 0, X, Y)}\" stroke=\"{colour}\" stroke-width=\"{(2 * half * PxPerM):F1}\" fill=\"none\" stroke-linejoin=\"round\"/>");
                segments++;

                var a = seg.Attributes;
                foreach (bool right in (ReadOnlySpan<bool>)[false, true])
                {
                    var side = right ? a.Right : a.Left;
                    double sign = right ? 1 : -1;
                    // a path's bands first, then the sidewalk behind them, past a turn lane's widening (#120)
                    double inner = (side.ShiftStartCm + side.ShiftEndCm) / 200.0;
                    if (side.HasTrack)
                    {
                        void Band(double from, double width, string fill) =>
                            walks.Append(c, $"<path d=\"{Offset(pts, sign * (half + from + width / 2), X, Y)}\" stroke=\"{fill}\" stroke-width=\"{(width * PxPerM):F1}\" fill=\"none\" stroke-linecap=\"butt\"/>");
                        double verge = side.VergeDm / 10.0, path = side.BikeDm / 10.0, buffer = side.BufferDm / 10.0;
                        if (verge > 0) Band(inner, verge, "#7caa4a");
                        Band(inner + verge, path, LayoutColours[Rewrite.TileRewriter.LayoutOf(side)]);
                        if (buffer > 0) Band(inner + verge + path, buffer, "#7caa4a");
                        inner += verge + path + buffer;
                        tracks++;
                    }
                    if (side.SidewalkDm > 0)
                    {
                        double w = side.SidewalkDm / 10.0;
                        string fill = side.KerbCm == 0 ? "#b9a6d6" : w < 1.5 ? "#d9534f" : w <= 2.5 ? "#d8c08a" : "#f0a040";
                        walks.Append(c, $"<path d=\"{Offset(pts, sign * (half + inner + w / 2), X, Y)}\" stroke=\"{fill}\" stroke-width=\"{(w * PxPerM):F1}\" fill=\"none\" stroke-linecap=\"butt\"/>");
                        sides++;
                    }
                    else if (a.Has(RoadAttrFlags.Urban))
                        walks.Append(c, $"<path d=\"{Offset(pts, sign * (half + 0.15), X, Y)}\" stroke=\"#d9534f\" stroke-width=\"1\" fill=\"none\" stroke-dasharray=\"4 3\"/>");
                }
            }

            // turn lanes' widenings (#123, #348): flush asphalt beside the ribbon
            foreach (var area in tile.AreaProps)
            {
                if (area.Type != AreaPropType.Pavement) continue;
                var v = area.Vertices;
                for (int k = 0; k + 2 < area.Indices.Length; k += 3)
                {
                    int i0 = area.Indices[k] * 3, i1 = area.Indices[k + 1] * 3, i2 = area.Indices[k + 2] * 3;
                    roads.Append(c, $"<path d=\"M{X(id.MinE + v[i0])} {Y(id.MaxN - v[i0 + 2])}L{X(id.MinE + v[i1])} {Y(id.MaxN - v[i1 + 2])}L{X(id.MinE + v[i2])} {Y(id.MaxN - v[i2 + 2])}Z\" fill=\"#8a8a8a\" stroke=\"#8a8a8a\" stroke-width=\"0.5\"/>");
                }
            }

            // white paint (lane lines, stop lines, arrows, hatches) under the bike paint
            foreach (var paint in tile.Paint)
            {
                if (paint.Rgba != Meshing.PaintEmitter.White || paint.Vertices.Length < 6) continue;
                var v = paint.Vertices;
                if (paint.Shape == PaintShape.Triangles)
                {
                    for (int k = 0; k + 2 < paint.Indices.Length; k += 3)
                    {
                        int i0 = paint.Indices[k] * 3, i1 = paint.Indices[k + 1] * 3, i2 = paint.Indices[k + 2] * 3;
                        bikes.Append(c, $"<path d=\"M{X(id.MinE + v[i0])} {Y(id.MaxN - v[i0 + 2])}L{X(id.MinE + v[i1])} {Y(id.MaxN - v[i1 + 2])}L{X(id.MinE + v[i2])} {Y(id.MaxN - v[i2 + 2])}Z\" fill=\"#ffffff\"/>");
                    }
                    continue;
                }
                var pts = new (double E, double N)[v.Length / 3];
                for (int i = 0; i < pts.Length; i++) pts[i] = (id.MinE + v[i * 3], id.MaxN - v[i * 3 + 2]);
                string dash = paint.Dash > 0 ? string.Create(c, $" stroke-dasharray=\"{paint.Dash * PxPerM:F1} {paint.Gap * PxPerM:F1}\"") : "";
                bikes.Append(c, $"<path d=\"{Offset(pts, 0, X, Y)}\" stroke=\"#ffffff\" stroke-width=\"{Math.Max(paint.Width, 0.1) * PxPerM:F1}\" fill=\"none\"{dash}/>");
            }

            foreach (var area in tile.AreaProps)
            {
                if (!StreetAreas.Is(area.Type)) continue;
                var v = area.Vertices;
                string fill = area.Type == AreaPropType.BikePath ? "#8e8ec2" : area.Type == AreaPropType.Grass ? "#7caa4a" : area.Type == AreaPropType.Kerb ? "#e0ddd4"
                    : area.Height > 0 ? "#c9a96a" : "#b9a6d6";
                for (int k = 0; k + 2 < area.Indices.Length; k += 3)
                {
                    int i0 = area.Indices[k] * 3, i1 = area.Indices[k + 1] * 3, i2 = area.Indices[k + 2] * 3;
                    walks.Append(c, $"<path d=\"M{X(id.MinE + v[i0])} {Y(id.MaxN - v[i0 + 2])}L{X(id.MinE + v[i1])} {Y(id.MaxN - v[i1 + 2])}L{X(id.MinE + v[i2])} {Y(id.MaxN - v[i2 + 2])}Z\" fill=\"{fill}\" stroke=\"{fill}\" stroke-width=\"0.5\"/>");
                }
            }

            // bike paint (#120): lines and crossings as drawn, a symbol as a dot
            foreach (var paint in tile.Paint)
            {
                if ((paint.Type is not (PaintType.YellowDashed or PaintType.YellowSolid or PaintType.BikeCrossing or PaintType.BikeSymbol) && paint.Rgba != Meshing.PaintEmitter.Yellow) || paint.Shape != PaintShape.Polyline || paint.Vertices.Length < 6) continue;
                var v = paint.Vertices;
                var pts = new (double E, double N)[v.Length / 3];
                for (int i = 0; i < pts.Length; i++) pts[i] = (id.MinE + v[i * 3], id.MaxN - v[i * 3 + 2]);
                if (paint.Type == PaintType.BikeSymbol)
                    bikes.Append(c, $"<circle cx=\"{X(pts[0].E)}\" cy=\"{Y(pts[0].N)}\" r=\"{Math.Max(2, PxPerM * 0.6):F1}\" fill=\"#e6be33\" stroke=\"#5a4a00\" stroke-width=\"0.7\"/>");
                else
                {
                    bool red = paint.Type == PaintType.BikeCrossing;
                    double width = red ? paint.Width : Math.Max(paint.Width, 0.3);
                    string dash = paint.Dash > 0 ? string.Create(c, $" stroke-dasharray=\"{paint.Dash * PxPerM:F1} {paint.Gap * PxPerM:F1}\"") : "";
                    bikes.Append(c, $"<path d=\"{Offset(pts, 0, X, Y)}\" stroke=\"{(red ? "#c0392b" : "#e6be33")}\" stroke-width=\"{width * PxPerM:F1}\" fill=\"none\"{dash}/>");
                }
            }

            foreach (var prop in tile.LinearProps)
            {
                var pts = new (double E, double N)[prop.PointCount];
                for (int i = 0; i < prop.PointCount; i++) pts[i] = (id.MinE + prop.Points[i * 4], id.MaxN - prop.Points[i * 4 + 2]);
                walls.Append(c, $"<path d=\"{Offset(pts, 0, X, Y)}\" stroke=\"#2a6fd6\" stroke-width=\"3\" fill=\"none\"/>");
            }
        }

        svg.Append("<g>").Append(roads).Append("</g>\n");
        svg.Append("<g>").Append(walks).Append("</g>\n");
        svg.Append("<g>").Append(bikes).Append("</g>\n");
        svg.Append("<g fill=\"#222\" stroke=\"#222\" stroke-width=\"1\">").Append(walls).Append("</g>\n");
        svg.Append(c, $"<text x=\"8\" y=\"20\" font-family=\"sans-serif\" font-size=\"16\">LV95 {e:F0},{n:F0}, {size:F0} m; {segments} segments, {sides} sidewalk sides, {tracks} bike path sides</text>\n");
        svg.Append("</svg>\n");
        File.WriteAllText(outFile, svg.ToString());
        log($"street view -> {outFile} ({segments} segments, {sides} sidewalk sides)");
        return 0;
    }

    /// <summary>
    /// <c>--dump-street E,N</c>: the corner patches and segment ends within 12 m of a point, with
    /// heights, for chasing a step the plan view cannot show.
    /// </summary>
    /// <summary>A side's bands outward, in metres: verge + path (kind) + buffer + sidewalk, and the kerb.</summary>
    private static string Bands(RoadSide s) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"v{s.VergeDm / 10.0:F1}+{s.Bike}{s.BikeDm / 10.0:F1}+b{s.BufferDm / 10.0:F1}+s{s.SidewalkDm / 10.0:F1} k{s.KerbCm}");

    public static int Dump(string chunks, double e, double n, Action<string> log)
    {
        var id = TileId.FromLv95(e, n);
        RoadTile tile;
        using (var rs = File.OpenRead(System.IO.Path.Combine(chunks, RoadFormat.FileName(id)))) tile = RoadCodec.Decode(rs);
        double x0 = e - id.MinE, z0 = id.MaxN - n;
        bool Near(float x, float z) => (x - x0) * (x - x0) + (z - z0) * (z - z0) < 144;
        var c = CultureInfo.InvariantCulture;
        foreach (var a in tile.AreaProps)
        {
            var v = a.Vertices;
            if (!Enumerable.Range(0, v.Length / 3).Any(i => Near(v[i * 3], v[i * 3 + 2]))) continue;
            log(string.Create(c, $"area {a.Type} h {a.Height:F2}: ") + string.Join(" ", Enumerable.Range(0, v.Length / 3)
                .Select(i => string.Create(c, $"({id.MinE + v[i * 3]:F1},{id.MaxN - v[i * 3 + 2]:F1} y {v[i * 3 + 1]:F3})"))));
            double tris = 0, ring = 0;
            for (int k = 0; k + 2 < a.Indices.Length; k += 3)
            {
                int i0 = a.Indices[k] * 3, i1 = a.Indices[k + 1] * 3, i2 = a.Indices[k + 2] * 3;
                tris += Math.Abs((v[i1] - v[i0]) * (v[i2 + 2] - v[i0 + 2]) - (v[i2] - v[i0]) * (v[i1 + 2] - v[i0 + 2])) / 2;
            }
            for (int i = 0; i < v.Length / 3; i++)
            {
                int j = (i + 1) % (v.Length / 3);
                ring += v[i * 3] * v[j * 3 + 2] - v[j * 3] * v[i * 3 + 2];
            }
            log(string.Create(c, $"  triangles {a.Indices.Length / 3}, their area {tris:F2} m2, the outline's {Math.Abs(ring) / 2:F2} m2: [{string.Join(",", a.Indices)}]"));
        }
        // every line passing within 15 m: its nearest point and height there
        foreach (var s in tile.Segments)
        {
            double best = double.MaxValue, by = 0, bx = 0, bz = 0;
            for (int i = 0; i + 1 < s.PointCount; i++)
            {
                double ax = s.Points[i * 3], az = s.Points[i * 3 + 2], cx = s.Points[i * 3 + 3], cz = s.Points[i * 3 + 5];
                double dx = cx - ax, dz = cz - az, l2 = dx * dx + dz * dz;
                double t = l2 < 1e-9 ? 0 : Math.Clamp(((x0 - ax) * dx + (z0 - az) * dz) / l2, 0, 1);
                double px = ax + dx * t, pz = az + dz * t, d = Math.Sqrt((px - x0) * (px - x0) + (pz - z0) * (pz - z0));
                if (d < best) { best = d; bx = px; bz = pz; by = s.Points[i * 3 + 1] + (s.Points[i * 3 + 4] - s.Points[i * 3 + 1]) * t; }
            }
            if (best < 15)
                log(string.Create(c, $"passes {s.Class} {s.Flags} at {best:F1} m: ({id.MinE + bx:F1},{id.MaxN - bz:F1}) y {by:F3} width {s.Width:F1} sidewalks {s.Attributes.Left.SidewalkDm / 10.0:F1}/{s.Attributes.Right.SidewalkDm / 10.0:F1} sides {Bands(s.Attributes.Left)} / {Bands(s.Attributes.Right)}"
                    + $"; ends ({id.MinE + s.Points[0]:F1},{id.MaxN - s.Points[2]:F1} y {s.Points[1]:F2}) ({id.MinE + s.Points[^3]:F1},{id.MaxN - s.Points[^1]:F1} y {s.Points[^2]:F2})"));
        }
        foreach (var s in tile.Segments)
            foreach (int i in new[] { 0, s.PointCount - 1 })
                if (s.PointCount >= 2 && Near(s.Points[i * 3], s.Points[i * 3 + 2]))
                    log(string.Create(c, $"{s.Class} {(i == 0 ? "start" : "end")} ({id.MinE + s.Points[i * 3]:F1},{id.MaxN - s.Points[i * 3 + 2]:F1}) y {s.Points[i * 3 + 1]:F3} width {s.Width:F1} left {s.Attributes.Left.SidewalkDm / 10.0:F1}/{s.Attributes.Left.KerbCm} right {s.Attributes.Right.SidewalkDm / 10.0:F1}/{s.Attributes.Right.KerbCm}"));
        return 0;
    }

    /// <summary>The polyline offset sideways by <paramref name="offset"/> (+ right of travel), per-vertex bisector.</summary>
    private static string Offset((double E, double N)[] p, double offset, Func<double, string> x, Func<double, string> y)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < p.Length; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(p.Length - 1, i + 1);
            double fe = p[b].E - p[a].E, fn = p[b].N - p[a].N, fl = Math.Sqrt(fe * fe + fn * fn);
            if (fl < 1e-9) { fe = 0; fn = 1; fl = 1; }
            // right of travel in E/N: (fn, -fe)
            double ee = p[i].E + fn / fl * offset, nn = p[i].N - fe / fl * offset;
            sb.Append(i == 0 ? 'M' : 'L').Append(x(ee)).Append(' ').Append(y(nn));
        }
        return sb.ToString();
    }
}
