using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// Building sites as plan views (#607): roads, the neighbours, the building, its hoarding and gate,
/// the zones, the cranes with their reach and the machine slots, one panel per site. What
/// <c>--constructioncheck</c> writes to <c>test_output/construction/</c>, and the picture that
/// caught the three planner bugs no unit test did (<c>docs/notes/terrain/construction-sites.md</c>).
/// </summary>
public static class SitePlanSvg
{
    private const int Panel = 520, Columns = 3;

    /// <summary>One site with what stands round it, for <see cref="Draw"/>.</summary>
    public sealed record Entry(ConstructionSite Site, RoadTile? Roads, IReadOnlyList<PlanBox?> Boxes, string Title);

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Draw(IReadOnlyList<Entry> entries)
    {
        int rows = Math.Max(1, (entries.Count + Columns - 1) / Columns);
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Panel * Math.Min(Columns, Math.Max(1, entries.Count))}\" height=\"{Panel * rows}\" font-family=\"sans-serif\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#eef0e6\"/>");
        for (int n = 0; n < entries.Count; n++) DrawOne(sb, entries[n], (n % Columns) * Panel, (n / Columns) * Panel);
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void DrawOne(StringBuilder sb, Entry entry, int px, int py)
    {
        var s = entry.Site;
        float view = Math.Max(s.Area.Width, s.Area.Depth) + 50f;
        float sc = (Panel - 20) / view;
        var c = s.Area.Center;
        float ox = px + Panel / 2f, oy = py + Panel / 2f + 10;
        float X(Vector2 p) => ox + (p.X - c.X) * sc;
        float Y(Vector2 p) => oy + (p.Y - c.Y) * sc;
        string Pt(Vector2 p) => F(X(p)) + "," + F(Y(p));
        void Poly(IEnumerable<Vector2> ps, string fill, string stroke, float w = 1, string extra = "") =>
            sb.Append($"<polygon points=\"{string.Join(" ", ps.Select(Pt))}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\" {extra}/>");
        void Line(Vector2 a, Vector2 b, string stroke, float w) =>
            sb.Append($"<line x1=\"{F(X(a))}\" y1=\"{F(Y(a))}\" x2=\"{F(X(b))}\" y2=\"{F(Y(b))}\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\"/>");
        void Text(Vector2 p, string t, int size, string anchor = "middle") =>
            sb.Append($"<text x=\"{F(X(p))}\" y=\"{F(Y(p) + 3)}\" font-size=\"{size}\" text-anchor=\"{anchor}\">{t}</text>");

        sb.Append($"<svg x=\"{px}\" y=\"{py}\" width=\"{Panel}\" height=\"{Panel}\" overflow=\"hidden\"><g transform=\"translate({-px},{-py})\">");
        if (entry.Roads != null)
            foreach (var seg in entry.Roads.Segments)
            {
                if (RoadFormat.IsAerial(seg.Class)) continue;
                var pts = Enumerable.Range(0, seg.PointCount).Select(k => new Vector2(seg.Points[k * 3], seg.Points[k * 3 + 2]));
                sb.Append($"<polyline points=\"{string.Join(" ", pts.Select(Pt))}\" fill=\"none\" stroke=\"#a4a4a4\" stroke-width=\"{F(Math.Max(1, seg.Width * sc))}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
            }
        foreach (var b in entry.Boxes)
            if (b is { } o && o.Center.DistanceTo(c) < view && o.Center.DistanceTo(s.Box.Center) > 0.01f)
                Poly(o.Corners(), "#c9b9a3", "#7d6e5c");
        Poly(s.Box.Corners(), "#dcdcdc", "#444", 1.5f, "stroke-dasharray=\"4,2\"");
        Text(s.Box.Center, "building", 11);
        foreach (var z in s.Zones)
        {
            string fill = z.Kind switch
            {
                SiteZoneKind.Office => "#5b8fd6", SiteZoneKind.Toilets => "#3fb0a0", SiteZoneKind.Skips => "#d68a3a",
                SiteZoneKind.Materials => "#b07ad6", _ => "#8a6a3a",
            };
            Poly(z.Rect.Corners(), fill, "#222", 0.8f, "fill-opacity=\"0.85\"");
            Text(z.Rect.Center, z.Kind.ToString(), 9);
        }
        foreach (var m in s.Machines)
        {
            var (w, l) = ConstructionSites.MachineSize(m.Role);
            // yaw 0 faces -Z
            var f = new Vector2(-MathF.Sin(m.Yaw), -MathF.Cos(m.Yaw));
            Poly(new SiteRect(m.At, new Vector2(f.Y, -f.X), w, l).Corners(), "#f2c200", "#222", 0.8f);
            Line(m.At, m.At + f * (l / 2), "#c00", 1.5f);
            Text(m.At + new Vector2(2, 0), m.Role.ToString(), 9, "start");
        }
        foreach (var k in s.Cranes)
        {
            sb.Append($"<circle cx=\"{F(X(k.Base))}\" cy=\"{F(Y(k.Base))}\" r=\"{F(k.JibLength * sc)}\" fill=\"none\" stroke=\"#d23\" stroke-dasharray=\"6,4\"/>");
            float h = (k.Kind == CraneKind.Tower ? ConstructionSites.TowerBase : ConstructionSites.SelfErectingBase) / 2;
            Poly(new[] { k.Base + new Vector2(-h, -h), k.Base + new Vector2(h, -h), k.Base + new Vector2(h, h), k.Base + new Vector2(-h, h) },
                "#d23", "#600");
            Line(k.Base, k.Base + new Vector2(-MathF.Sin(k.RestYaw), -MathF.Cos(k.RestYaw)) * k.JibLength, "#d23", 2);
        }
        foreach (var r in s.Hoarding) Line(r.A, r.B, "#e06000", 3);
        sb.Append($"<circle cx=\"{F(X(s.Gate))}\" cy=\"{F(Y(s.Gate))}\" r=\"5\" fill=\"#0a0\"/>");
        sb.Append("</g></svg>");

        sb.Append($"<rect x=\"{px + 6}\" y=\"{py + 6}\" width=\"{Panel - 12}\" height=\"36\" fill=\"white\" fill-opacity=\"0.88\"/>");
        sb.Append($"<text x=\"{px + 12}\" y=\"{py + 21}\" font-size=\"13\" font-weight=\"bold\">{entry.Title}: {s.Phase}, {s.BuiltStoreys}/{s.TargetStoreys} storeys, measured {F(s.Measured)} m</text>");
        string cranes = s.Cranes.Count == 0 ? "no crane"
            : string.Join("; ", s.Cranes.Select(k => $"{k.Kind}{(k.Inside ? " (inside)" : "")} jib {F(k.JibLength)} hook {F(k.HookHeight)}"));
        sb.Append($"<text x=\"{px + 12}\" y=\"{py + 36}\" font-size=\"11\">box {F(s.Box.Width)} x {F(s.Box.Depth)} m; {cranes}</text>");
        sb.Append($"<rect x=\"{px}\" y=\"{py}\" width=\"{Panel}\" height=\"{Panel}\" fill=\"none\" stroke=\"#888\"/>");
    }
}
