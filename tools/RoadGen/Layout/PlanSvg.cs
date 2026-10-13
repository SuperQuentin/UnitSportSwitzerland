namespace UnitSport.Tools.RoadGen.Layout;

using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// A plan view of a junction plan (#818, <c>--junction-svg FILE</c>), in its real geometry (north up, metres): the core
/// laid between the mouths (step 2: its outline, kerb corners with their radii, the connectors by move and the lines through
/// it) over today's cap (dashed), and every arm from its mouth out along its axis, its strips easing from the mouth section
/// (held over the storage) to the street's normal one over the taper, as the transition will (step 4).
/// </summary>
public static class PlanSvg
{
    /// <summary>Where the mouths are drawn without a core, metres from the node.</summary>
    public const double MouthAt = 14;
    private const double Shown = 70, Step = 1, Scale = 5;

    public static string Draw(JunctionPlan plan, string title)
    {
        var inv = CultureInfo.InvariantCulture;
        var core = plan.Core;
        double reach = core is { Mouths.Count: > 0 } ? core.Mouths.Max(m => m.Station) : MouthAt;
        double half = reach + Shown + 8;
        int px = (int)(half * 2 * Scale);
        double rows = plan.Arms.Count * 4.2 + 8;
        var sb = new StringBuilder();
        sb.Append(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{px}\" height=\"{(int)((half * 2 + 16 + rows) * Scale)}\" viewBox=\"{-half} {-half - 14} {half * 2} {half * 2 + 16 + rows}\" font-family=\"Segoe UI, Arial, sans-serif\">\n");
        sb.Append(inv, $"<rect x=\"{-half}\" y=\"{-half - 14}\" width=\"{half * 2}\" height=\"{half * 2 + 16 + rows}\" fill=\"#e9ece6\"/>\n");
        sb.Append(inv, $"<text x=\"{-half + 2}\" y=\"{-half - 6}\" font-size=\"4.5\" font-weight=\"600\" fill=\"#1b2327\">{Esc(title)}</text>\n");
        string Pt(Vec2 v) => string.Create(inv, $"{v.X - plan.Centre.X:F2},{-(v.Y - plan.Centre.Y):F2}");
        string Pts(IEnumerable<Vec2> vs) => string.Join(" ", vs.Select(Pt));

        foreach (var a in plan.Arms) Arm(sb, a, core?.Mouths.FirstOrDefault(m => m.Arm == a.Arm)?.Station ?? MouthAt, Pt);
        if (core is not null)
        {
            sb.Append("<polygon points=\"").Append(Pts(core.Outline)).Append("\" fill=\"#4f585e\" stroke=\"none\"/>\n");
            // the connectors' lanes, faint, then their centre lines by move
            foreach (var c in core.Connectors)
                sb.Append("<polygon points=\"").Append(Pts(c.Left.Concat(Enumerable.Reverse(c.Right)))).Append(inv, $"\" fill=\"{(c.Kind == ConnectorKind.Bike ? "#e6d79a" : "#ffffff")}\" fill-opacity=\"0.07\" stroke=\"none\"/>\n");
            foreach (var c in core.Connectors)
                sb.Append("<polyline points=\"").Append(Pts(c.Centre)).Append(inv, $"\" fill=\"none\" stroke=\"{MoveColour(c)}\" stroke-width=\"{(c.Kind == ConnectorKind.Bike ? 0.18 : 0.25)}\"{(c.Kind == ConnectorKind.Bike ? " stroke-dasharray=\"0.6 0.4\"" : "")}/>\n");
            foreach (var l in core.Lines)
                sb.Append("<polyline points=\"").Append(Pts(l.Points)).Append(inv, $"\" fill=\"none\" stroke=\"#ffffff\" stroke-width=\"0.15\"{(l.Dashed ? " stroke-dasharray=\"1 1\"" : "")}/>\n");
            // the kerb: the outline's edge, the corners' arcs stressed with their radii
            sb.Append("<polygon points=\"").Append(Pts(core.Outline)).Append("\" fill=\"none\" stroke=\"#1b2327\" stroke-width=\"0.2\"/>\n");
            foreach (var corner in core.Corners.Where(c => c.Shape == CornerShape.Arc && c.Kerb.Count > 1))
            {
                sb.Append("<polyline points=\"").Append(Pts(corner.Kerb)).Append(inv, $"\" fill=\"none\" stroke=\"{(corner.Tight ? "#d0412f" : "#f29e1f")}\" stroke-width=\"0.45\"/>\n");
                var mid = corner.Kerb[corner.Kerb.Count / 2];
                var outward = (mid - corner.Meet).Length > 1e-6 ? (corner.Meet - mid).Normalized() : new Vec2(0, 1);
                var at = mid + outward * 2.6;
                string label = corner.Tight ? "tight" : string.Create(inv, $"r {corner.Radius:F1}");
                if (plan.OldTight.Contains((corner.ArmA, corner.ArmB))) label += " (#700 tight)";
                sb.Append(inv, $"<text x=\"{at.X - plan.Centre.X:F2}\" y=\"{-(at.Y - plan.Centre.Y) + 0.9:F2}\" font-size=\"2.4\" text-anchor=\"middle\" fill=\"#7a3b00\">{Esc(label)}</text>\n");
            }
            foreach (var m in core.Mouths)
                sb.Append(inv, $"<line x1=\"{m.Left.X - plan.Centre.X:F2}\" y1=\"{-(m.Left.Y - plan.Centre.Y):F2}\" x2=\"{m.Right.X - plan.Centre.X:F2}\" y2=\"{-(m.Right.Y - plan.Centre.Y):F2}\" stroke=\"#f2c230\" stroke-width=\"0.25\"/>\n");
        }
        if (plan.OldOutline.Count > 2)
            sb.Append("<polygon points=\"").Append(Pts(plan.OldOutline)).Append("\" fill=\"none\" stroke=\"#c2185b\" stroke-width=\"0.25\" stroke-dasharray=\"1 0.7\"/>\n");

        double y = half + 4;
        foreach (var a in plan.Arms)
        {
            string steps = string.Join("; ", a.Steps.Concat(a.FitSteps));
            string open = string.Join(", ", a.Openings.Select(o => string.Create(inv, $"{(PocketRules.Right.Owns(o.Id) ? "right" : "left")} {o.Taper:F0}+{o.Storage:F0} m")));
            var mouth = core?.Mouths.FirstOrDefault(m => m.Arm == a.Arm);
            string line = $"{JunctionPlan.Compass(a.Heading)} (arm {a.Arm}): {a.Mouth.CarLanesIn()}"
                + (mouth is not null ? string.Create(inv, $"   mouth {mouth.Station:F1} m") : "")
                + (open.Length > 0 ? $"   pockets {open}" : "")
                + (a.Split > 0 ? string.Create(inv, $"   split {a.Split:F2} m") : "")
                + (steps.Length > 0 ? $"   [{steps}]" : "");
            sb.Append(inv, $"<text x=\"{-half + 2}\" y=\"{y:F1}\" font-size=\"3\" fill=\"#1b2327\">{Esc(line)}</text>\n");
            y += 4.2;
        }
        if (core is not null)
            sb.Append(inv, $"<text x=\"{-half + 2}\" y=\"{y:F1}\" font-size=\"3\" fill=\"#1b2327\">{Esc(string.Create(inv, $"core {core.Area:F0} m2 (today: cap {plan.OldArea:F0} m2 dashed, with its ribbons to these mouths {plan.OldAreaToMouths:F0} m2), {core.Connectors.Count} connectors, kerbs starting outside their mouth {core.KerbsNotTangent}, connectors leaving the outline {core.ConnectorsOutside}"))}</text>\n");
        Legend(sb, half);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string MoveColour(Connector c) => c.Kind == ConnectorKind.Bike ? "#e6c430" : c.Move switch
    {
        SignalMoves.Left => "#35b6c9",
        SignalMoves.Right => "#7f9cf5",
        _ => "#f4f4f4",
    };

    private static void Arm(StringBuilder sb, ArmPlan a, double mouthAt, Func<Vec2, string> pt)
    {
        var inv = CultureInfo.InvariantCulture;
        var axis = a.Axis ?? ArmAxis.Straight(Vec2.Zero, a.Heading);
        double hold = a.Openings.Count > 0 ? a.Openings.Max(o => o.Storage) : 0;
        double ease = a.Openings.Count > 0 ? a.Openings.Max(o => Math.Max(o.Taper, 1)) : 0;
        bool hatched = a.Mouth.Strips.Any(s => s.Kind == StripKind.Hatch);
        if (a.Openings.Count == 0 && (hatched || a.Split > 0)) ease = 30;   // an exit's hatch closes over TurnExit
        if (a.Openings.Count == 0 && a.Mouth.Strips.Any(s => s.Id == MouthPlanner.AddedBikeId)) (hold, ease) = (Math.Max(hold, 20), Math.Max(ease, 9));
        double length = Math.Min(Shown, Math.Max(5, a.Length));
        var ids = Order(a.Mouth, a.Normal);
        Vec2 P(double s, double o) => axis.At(mouthAt + s, o);
        double T(double s) => s <= hold ? 1 : ease <= 0 ? 0 : Math.Clamp(1 - (s - hold) / ease, 0, 1);

        var spans = new Dictionary<int, List<(double S, double From, double To)>>();
        var m = SpansOf(a.Mouth, ids);
        var n = SpansOf(a.Normal, ids);
        for (double s = 0; s <= length + 1e-6; s += Step)
        {
            double t = T(s);
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
            sb.Append("<polygon points=\"").Append(string.Join(" ", pts.Select(pt)))
              .Append(inv, $"\" fill=\"{Fill(strip)}\" stroke=\"#ffffff\" stroke-width=\"0.12\"/>\n");
        }
        // the centre line, and the lane arrows' moves at the mouth
        if (a.Mouth.CentreLine is { } c0 && a.Normal.CentreLine is { } c1)
        {
            var pts = new List<Vec2>();
            for (double s = 0; s <= length + 1e-6; s += Step) pts.Add(P(s, c1 + (c0 - c1) * T(s)));
            sb.Append("<polyline points=\"").Append(string.Join(" ", pts.Select(pt))).Append("\" fill=\"none\" stroke=\"#f2c230\" stroke-width=\"0.35\"/>\n");
        }
        for (int k = 0; k < a.Mouth.Strips.Count; k++)
        {
            var s = a.Mouth.Strips[k];
            if (s.Way != StripWay.In || s.Moves == SignalMoves.None) continue;
            var (f, t) = a.Mouth.Span(k);
            var at = pt(P(3.5, (f + t) * 0.5)).Split(',');
            bool bike = s.Kind == StripKind.BikeLane;
            sb.Append(inv, $"<text x=\"{at[0]}\" y=\"{double.Parse(at[1], inv) + 0.8:F2}\" font-size=\"{(bike ? 1.6 : 2.4)}\" text-anchor=\"middle\" font-weight=\"700\" fill=\"{(bike ? "#5b4a00" : "#ffffff")}\">{(bike ? "b" : "")}{Section.MovesName(s.Moves)}</text>\n");
        }
        var label = pt(P(length + 4, 0)).Split(',');
        sb.Append(inv, $"<text x=\"{label[0]}\" y=\"{double.Parse(label[1], inv) + 1.5:F2}\" font-size=\"4.5\" font-weight=\"700\" text-anchor=\"middle\" fill=\"#1b2327\">{JunctionPlan.Compass(a.Heading)}</text>\n");
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
        StripKind.CarLane when PocketRules.Left.Owns(s.Id) => "#2f7f8f",
        StripKind.CarLane when PocketRules.Right.Owns(s.Id) => "#4f6fb0",
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
            ("#4f585e", "core"), ("#f29e1f", "kerb arc"), ("#d0412f", "tight corner"), ("#35b6c9", "left connector"),
            ("#7f9cf5", "right connector"), ("#e6c430", "bike connector"), ("#c2185b", "today's cap"),
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
