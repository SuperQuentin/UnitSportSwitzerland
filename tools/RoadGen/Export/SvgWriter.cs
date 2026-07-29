namespace UnitSport.Tools.RoadGen.Export;

using System.Globalization;
using System.Text;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Plan-view SVG of the generated network.
///
/// <para>
/// This exists because road quality is a <i>plan-view</i> property and almost invisible in a
/// 3D screenshot: a junction painted four times over still renders as grey tarmac from a
/// player's eye height. In plan view, drawn at true scale, an overlap is obvious, a kinked
/// corner is obvious, and a marking running through an intersection is obvious. It also opens
/// in any browser, which beats rebuilding a 6,699-tile region to look at one crossroads.
/// </para>
/// </summary>
public static class SvgWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(string path,
        RoadNetwork net,
        IReadOnlyList<Ribbon> ribbons,
        IReadOnlyList<Junction> junctions,
        IReadOnlyDictionary<int, List<MarkingLine>> markings,
        string title,
        bool showCentrelines = true)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        void Grow(Vec2 p)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        foreach (var ribbon in ribbons) { foreach (var p in ribbon.Left) Grow(p); foreach (var p in ribbon.Right) Grow(p); }
        foreach (var junction in junctions) foreach (var p in junction.Boundary) Grow(p);
        foreach (var link in net.Links) foreach (var p in link.Centreline) Grow(p);
        if (minX > maxX) { minX = minY = 0; maxX = maxY = 100; }

        double margin = Math.Max(10, (maxX - minX + maxY - minY) * 0.02);
        minX -= margin; maxX += margin; minY -= margin; maxY += margin;
        double width = maxX - minX, height = maxY - minY;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{N(minX)} {N(-maxY)} {N(width)} {N(height)}\" width=\"1400\">");
        sb.AppendLine($"<title>{Escape(title)}</title>");
        sb.AppendLine($"<rect x=\"{N(minX)}\" y=\"{N(-maxY)}\" width=\"{N(width)}\" height=\"{N(height)}\" fill=\"#11161c\"/>");

        // scale bar: without one, "does this junction look right" has no units
        double barMetres = NiceBarLength(width);
        sb.AppendLine($"<g stroke=\"#7f8c99\" stroke-width=\"{N(width / 900)}\" fill=\"none\">");
        sb.AppendLine($"<path d=\"M {N(minX + margin)} {N(-(minY + margin))} h {N(barMetres)}\"/></g>");
        sb.AppendLine($"<text x=\"{N(minX + margin)}\" y=\"{N(-(minY + margin) - width / 120)}\" " +
                      $"fill=\"#7f8c99\" font-family=\"sans-serif\" font-size=\"{N(width / 90)}\">{N(barMetres)} m</text>");

        sb.AppendLine("<g id=\"carriageway\" fill=\"#3a4149\" stroke=\"#556\" stroke-width=\"0.08\">");
        foreach (var ribbon in ribbons)
        {
            if (ribbon.IsEmpty) continue;
            sb.AppendLine($"  <path d=\"{Ring(ribbon.Outline())}\"/>");
        }
        sb.AppendLine("</g>");

        sb.AppendLine("<g id=\"junctions\" fill=\"#474f58\" stroke=\"#69737d\" stroke-width=\"0.12\">");
        foreach (var junction in junctions)
            sb.AppendLine($"  <path d=\"{Ring(junction.Boundary)}\"/>");
        sb.AppendLine("</g>");

        sb.AppendLine("<g id=\"markings\" stroke-linecap=\"butt\" fill=\"none\">");
        foreach (var (_, lines) in markings)
        foreach (var line in lines)
        {
            string colour = line.Role switch
            {
                "stop" => "#f2f4f6",
                "edge" => "#e8ebee",
                "centre" => "#f6d98a",
                _ => "#dfe4e8",
            };
            sb.AppendLine($"  <path d=\"{Open(line.Points)}\" stroke=\"{colour}\" stroke-width=\"{N(line.Width)}\"/>");
        }
        sb.AppendLine("</g>");

        if (showCentrelines)
        {
            sb.AppendLine("<g id=\"centrelines\" fill=\"none\" stroke=\"#39d0d8\" stroke-width=\"0.1\" stroke-dasharray=\"1.5 1.5\" opacity=\"0.55\">");
            foreach (var ribbon in ribbons)
            {
                if (ribbon.IsEmpty) continue;
                sb.AppendLine($"  <path d=\"{Open(ribbon.Stations.Select(s => s.Position).ToList())}\"/>");
            }
            sb.AppendLine("</g>");
        }

        // nodes: junctions in amber, plain joins and dead ends dimmer
        sb.AppendLine("<g id=\"nodes\">");
        double dot = Math.Max(0.35, width / 700);
        foreach (var node in net.Nodes)
        {
            string fill = node.IsJunction ? "#ffb020" : node.Degree == 1 ? "#e05555" : "#5b6b7a";
            sb.AppendLine($"  <circle cx=\"{N(node.Position.X)}\" cy=\"{N(-node.Position.Y)}\" r=\"{N(dot)}\" fill=\"{fill}\"/>");
        }
        sb.AppendLine("</g>");

        // arms whose trim hit the ceiling: the honest "this one is a gore, not a junction" flag
        var clamped = junctions.SelectMany(j => j.Arms.Where(a => a.TrimWasClamped)).ToList();
        if (clamped.Count > 0)
        {
            sb.AppendLine("<g id=\"clamped\" stroke=\"#ff4d5a\" stroke-width=\"0.25\" fill=\"none\">");
            foreach (var arm in clamped)
                sb.AppendLine($"  <path d=\"M {N(arm.Left.X)} {N(-arm.Left.Y)} L {N(arm.Right.X)} {N(-arm.Right.Y)}\"/>");
            sb.AppendLine("</g>");
        }

        sb.AppendLine("</svg>");
        File.WriteAllText(path, sb.ToString());
    }

    private static double NiceBarLength(double width)
    {
        double target = width / 6;
        double[] steps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 };
        foreach (double s in steps) if (s >= target) return s;
        return steps[^1];
    }

    private static string Ring(IReadOnlyList<Vec2> pts) => Open(pts) + " Z";

    private static string Open(IReadOnlyList<Vec2> pts)
    {
        if (pts.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append("M ").Append(N(pts[0].X)).Append(' ').Append(N(-pts[0].Y));
        for (int i = 1; i < pts.Count; i++)
            sb.Append(" L ").Append(N(pts[i].X)).Append(' ').Append(N(-pts[i].Y));
        return sb.ToString();
    }

    private static string N(double v) => v.ToString("0.###", Inv);

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
