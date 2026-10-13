namespace UnitSport.Tools.RoadGen.Layout;

using System.Globalization;
using System.Text;

/// <summary>
/// A schematic plan view of a junction plan (#818, <c>--junction-svg FILE</c>): every arm laid straight along its heading
/// from <see cref="MouthAt"/> out, its strips easing from the mouth section (held over the storage) to the street's normal
/// one over the taper, as the transition will. The core between the mouths is left blank (phase 2 builds it).
/// </summary>
public static class PlanSvg
{
    /// <summary>Where the mouths are drawn, metres from the centre (the core comes in phase 2).</summary>
    public const double MouthAt = 14;
    private const double Shown = 90, Step = 1, Scale = 4;

    public static string Draw(JunctionPlan plan, string title)
    {
        var inv = CultureInfo.InvariantCulture;
        double half = MouthAt + Shown + 10;
        int px = (int)(half * 2 * Scale);
        var sb = new StringBuilder();
        sb.Append(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{px}\" height=\"{px + 120}\" viewBox=\"{-half} {-half - 14} {half * 2} {half * 2 + 30}\" font-family=\"Segoe UI, Arial, sans-serif\">\n");
        sb.Append(inv, $"<rect x=\"{-half}\" y=\"{-half - 14}\" width=\"{half * 2}\" height=\"{half * 2 + 30}\" fill=\"#e9ece6\"/>\n");
        sb.Append(inv, $"<text x=\"{-half + 2}\" y=\"{-half - 6}\" font-size=\"5\" font-weight=\"600\" fill=\"#1b2327\">{Esc(title)}</text>\n");
        sb.Append(inv, $"<circle cx=\"0\" cy=\"0\" r=\"{MouthAt - 1}\" fill=\"none\" stroke=\"#9aa3a7\" stroke-width=\"0.3\" stroke-dasharray=\"1.5 1.5\"/>\n");
        sb.Append(inv, $"<text x=\"0\" y=\"1\" font-size=\"3\" text-anchor=\"middle\" fill=\"#6b767b\">core: phase 2</text>\n");
        foreach (var a in plan.Arms) Arm(sb, a);
        int row = 0;
        foreach (var a in plan.Arms)
        {
            string steps = string.Join("; ", a.Steps.Concat(a.FitSteps));
            string open = string.Join(", ", a.Openings.Select(o => string.Create(inv, $"{(o.Id == MouthPlanner.RightPocketId ? "right" : "left")} {o.Taper:F0}+{o.Storage:F0} m")));
            string line = $"{JunctionPlan.Compass(a.Heading)}: {a.Mouth.CarLanesIn()}"
                + (open.Length > 0 ? $"   pockets {open}" : "")
                + (a.Split > 0 ? string.Create(inv, $"   split {a.Split:F2} m") : "")
                + (steps.Length > 0 ? $"   [{steps}]" : "");
            sb.Append(inv, $"<text x=\"{-half + 2}\" y=\"{half + 2 + row * 4.2}\" font-size=\"3.2\" fill=\"#1b2327\">{Esc(line)}</text>\n");
            row++;
        }
        Legend(sb, half);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static void Arm(StringBuilder sb, ArmPlan a)
    {
        var inv = CultureInfo.InvariantCulture;
        double hold = a.Openings.Count > 0 ? a.Openings.Max(o => o.Storage) : 0;
        double ease = a.Openings.Count > 0 ? a.Openings.Max(o => Math.Max(o.Taper, 1)) : 0;
        bool hatched = a.Mouth.Strips.Any(s => s.Kind == StripKind.Hatch);
        if (a.Openings.Count == 0 && (hatched || a.Split > 0)) ease = 30;   // an exit's hatch closes over TurnExit
        if (a.Openings.Count == 0 && a.Mouth.Strips.Any(s => s.Id == MouthPlanner.AddedBikeId)) (hold, ease) = (Math.Max(hold, 20), Math.Max(ease, 9));
        double length = Math.Min(Shown, Math.Max(5, a.Length));
        var ids = Order(a.Mouth, a.Normal);
        double ux = Math.Cos(a.Heading), uy = Math.Sin(a.Heading);
        // right of a driver arriving (driving -u), in east/north
        double rx = -uy, ry = ux;
        (double X, double Y) P(double s, double o) => (ux * (MouthAt + s) + rx * o, -(uy * (MouthAt + s) + ry * o));   // svg y down

        var spans = new Dictionary<int, List<(double S, double From, double To)>>();
        for (double s = 0; s <= length + 1e-6; s += Step)
        {
            double t = s <= hold ? 1 : ease <= 0 ? 0 : Math.Clamp(1 - (s - hold) / ease, 0, 1);
            var m = SpansOf(a.Mouth, ids);
            var n = SpansOf(a.Normal, ids);
            foreach (int id in ids)
            {
                var (mf, mt) = m[id];
                var (nf, nt) = n[id];
                if (!spans.TryGetValue(id, out var list)) spans[id] = list = new();
                list.Add((s, nf + (mf - nf) * t, nt + (mt - nt) * t));
            }
        }
        foreach (int id in ids)
        {
            var strip = a.Mouth.Strips.Concat(a.Normal.Strips).First(x => x.Id == id);
            var list = spans[id];
            if (list.All(x => x.To - x.From < 0.01)) continue;
            var pts = list.Select(x => P(x.S, x.From)).Concat(list.AsEnumerable().Reverse().Select(x => P(x.S, x.To)));
            string fill = Fill(strip);
            sb.Append("<polygon points=\"").Append(string.Join(" ", pts.Select(p => string.Create(inv, $"{p.X:F2},{p.Y:F2}"))))
              .Append(inv, $"\" fill=\"{fill}\" stroke=\"#ffffff\" stroke-width=\"0.12\"/>\n");
        }
        // the centre line, and the lane arrows' moves at the mouth
        if (a.Mouth.CentreLine is { } c0 && a.Normal.CentreLine is { } c1)
        {
            var pts = new List<(double X, double Y)>();
            for (double s = 0; s <= length + 1e-6; s += Step)
            {
                double t = s <= hold ? 1 : ease <= 0 ? 0 : Math.Clamp(1 - (s - hold) / ease, 0, 1);
                pts.Add(P(s, c1 + (c0 - c1) * t));
            }
            sb.Append("<polyline points=\"").Append(string.Join(" ", pts.Select(p => string.Create(inv, $"{p.X:F2},{p.Y:F2}"))))
              .Append("\" fill=\"none\" stroke=\"#f2c230\" stroke-width=\"0.35\"/>\n");
        }
        for (int k = 0; k < a.Mouth.Strips.Count; k++)
        {
            var s = a.Mouth.Strips[k];
            if (s.Way != StripWay.In || s.Moves == Terrain.Format.SignalMoves.None) continue;
            var (f, t) = a.Mouth.Span(k);
            var (x, y) = P(2.5, (f + t) * 0.5);
            sb.Append(inv, $"<text x=\"{x:F2}\" y=\"{y + 0.8:F2}\" font-size=\"{(s.Kind == StripKind.BikeLane ? 1.6 : 2.4)}\" text-anchor=\"middle\" font-weight=\"700\" fill=\"{(s.Kind == StripKind.BikeLane ? "#5b4a00" : "#ffffff")}\">{(s.Kind == StripKind.BikeLane ? "b" : "")}{Section.MovesName(s.Moves)}</text>\n");
        }
        var (lx, ly) = P(length + 4, 0);
        sb.Append(inv, $"<text x=\"{lx:F2}\" y=\"{ly + 1.5:F2}\" font-size=\"4.5\" font-weight=\"700\" text-anchor=\"middle\" fill=\"#1b2327\">{JunctionPlan.Compass(a.Heading)}</text>\n");
    }

    /// <summary>Every strip id of either section, in their left-to-right order (the mouth's, the normal's merged in).</summary>
    private static List<int> Order(Section mouth, Section normal)
    {
        var ids = mouth.Strips.Select(s => s.Id).ToList();
        int after = -1;
        foreach (var s in normal.Strips)
        {
            int at = ids.IndexOf(s.Id);
            if (at >= 0) { after = at; continue; }
            ids.Insert(after + 1, s.Id);
            after++;
        }
        return ids;
    }

    /// <summary>Each id's span across the arm; an id the section lacks sits at zero width where its left neighbour ends.</summary>
    private static Dictionary<int, (double From, double To)> SpansOf(Section s, List<int> ids)
    {
        var own = new Dictionary<int, (double, double)>();
        for (int k = 0; k < s.Strips.Count; k++) own[s.Strips[k].Id] = s.Span(k);
        var result = new Dictionary<int, (double From, double To)>();
        double edge = s.Strips.Count > 0 ? s.Span(0).From : s.CarriagewayLeft;
        foreach (int id in ids)
        {
            if (own.TryGetValue(id, out var span)) { result[id] = span; edge = span.Item2; }
            else result[id] = (edge, edge);
        }
        return result;
    }

    private static string Fill(Strip s) => s.Kind switch
    {
        StripKind.Sidewalk => "#c4c9c4",
        StripKind.Buffer or StripKind.Verge => "#8dae6b",
        StripKind.Path => "#d97a6c",
        StripKind.BikeLane => "#e6d79a",
        StripKind.Hatch => "#b9c0c4",
        StripKind.Island => "#7f8a8f",
        StripKind.CarLane when s.Id is >= MouthPlanner.LeftPocketId and < MouthPlanner.LeftPocketId + 10 => "#2f7f8f",
        StripKind.CarLane when s.Id == MouthPlanner.RightPocketId => "#4f6fb0",
        StripKind.CarLane when s.Way == StripWay.Out => "#7a838a",
        _ => "#5d666c",
    };

    private static void Legend(StringBuilder sb, double half)
    {
        var inv = CultureInfo.InvariantCulture;
        (string Fill, string Name)[] items =
        [
            ("#5d666c", "lane in"), ("#7a838a", "lane out"), ("#2f7f8f", "left pocket"), ("#4f6fb0", "right pocket"),
            ("#e6d79a", "bike lane"), ("#b9c0c4", "hatch"), ("#d97a6c", "bike path"), ("#8dae6b", "verge"), ("#c4c9c4", "sidewalk"),
        ];
        double x = half - 34;
        for (int k = 0; k < items.Length; k++)
        {
            double y = -half - 6 + k * 4;
            sb.Append(inv, $"<rect x=\"{x:F1}\" y=\"{y:F1}\" width=\"4\" height=\"3\" fill=\"{items[k].Fill}\"/>");
            sb.Append(inv, $"<text x=\"{x + 5:F1}\" y=\"{y + 2.5:F1}\" font-size=\"3\" fill=\"#1b2327\">{items[k].Name}</text>\n");
        }
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
