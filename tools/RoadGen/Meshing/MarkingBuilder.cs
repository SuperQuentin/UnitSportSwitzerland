namespace UnitSport.Tools.RoadGen.Meshing;

using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;

/// <summary>One painted stripe, already resolved into a single unbroken run.</summary>
/// <param name="Role">edge / centre / lane / stop — carried through to the exporters.</param>
public sealed record MarkingLine(List<Vec2> Points, double Width, string Role);

/// <summary>
/// Generates lane lines as explicit traces along the alignment.
///
/// <para>
/// Two things make these accurate where a shader stripe drawn from a lateral UV cannot be.
/// First, they are true offset curves of the alignment, so on a bend the outer line is longer
/// than the inner one and both stay parallel to the kerb — a stripe drawn at a constant UV
/// offset is parallel in texture space, not in the world. Second, they exist only between the
/// junction trims, so nothing is painted inside an intersection. Lines crossing each other in
/// the middle of a junction is not a marking bug, it is the absence of a junction.
/// </para>
/// </summary>
public static class MarkingBuilder
{
    /// <summary>How far inside the kerb an edge line sits.</summary>
    private const double EdgeInset = 0.20;

    /// <summary>Stop line position back from the junction boundary.</summary>
    private const double StopLineSetback = 0.6;

    public static List<MarkingLine> Build(RoadLink link, bool stopLineAtStart, bool stopLineAtEnd)
    {
        var lines = new List<MarkingLine>();
        if (link.Alignment is not { IsEmpty: false } alignment) return lines;

        var plan = link.Profile.Markings;
        double from = link.TrimStart;
        double to = alignment.Length - link.TrimEnd;
        if (to - from < 1.0) return lines;

        double half = link.Profile.HalfWidth;
        int lanes = Math.Max(1, link.Profile.Lanes);
        double laneWidth = link.Profile.Width / lanes;

        if (plan.Edge != LineStyle.None)
        {
            AddOffsetLine(lines, alignment, from, to, half - EdgeInset, plan.Edge, plan, "edge");
            AddOffsetLine(lines, alignment, from, to, -(half - EdgeInset), plan.Edge, plan, "edge");
        }

        // interior dividers; the exact middle of an even-laned carriageway is the centre line
        for (int k = 1; k < lanes; k++)
        {
            double offset = -half + k * laneWidth;
            bool isCentre = lanes % 2 == 0 && k == lanes / 2;
            var style = isCentre ? plan.Centre : plan.LaneDivider;
            if (style == LineStyle.None) continue;
            AddOffsetLine(lines, alignment, from, to, offset, style, plan, isCentre ? "centre" : "lane");
        }

        if (stopLineAtStart) AddStopLine(lines, alignment, from + StopLineSetback, half, plan);
        if (stopLineAtEnd) AddStopLine(lines, alignment, to - StopLineSetback, half, plan);

        return lines;
    }

    private static void AddOffsetLine(List<MarkingLine> lines, Alignment alignment,
        double from, double to, double offset, LineStyle style, MarkingPlan plan, string role)
    {
        if (style == LineStyle.Solid)
        {
            var points = SampleOffset(alignment, from, to, offset);
            if (points.Count >= 2) lines.Add(new MarkingLine(points, plan.LineWidth, role));
            return;
        }

        // dashed: emit each painted run as its own trace, so the exporters and any downstream
        // mesher never have to reconstruct the pattern from a UV
        double period = plan.DashOn + plan.DashOff;
        if (period <= 1e-6) return;

        for (double s = from; s < to; s += period)
        {
            double end = Math.Min(s + plan.DashOn, to);
            if (end - s < plan.DashOn * 0.4) continue;   // no stub dashes at the junction end
            var points = SampleOffset(alignment, s, end, offset);
            if (points.Count >= 2) lines.Add(new MarkingLine(points, plan.LineWidth, role));
        }
    }

    private static void AddStopLine(List<MarkingLine> lines, Alignment alignment,
        double station, double half, MarkingPlan plan)
    {
        if (station < 0 || station > alignment.Length) return;

        var position = alignment.PointAt(station);
        var normal = Vec2.FromHeading(alignment.HeadingAt(station)).Perp;
        lines.Add(new MarkingLine(
            new List<Vec2> { position + normal * (half - EdgeInset), position - normal * (half - EdgeInset) },
            0.4, "stop"));
    }

    /// <summary>
    /// Samples a parallel curve at a constant lateral offset.
    ///
    /// <para>
    /// The guard matters: an offset curve degenerates once |offset · curvature| reaches 1,
    /// because that is the centre of curvature — the inner edge of an 11 m carriageway folds
    /// through itself on anything tighter than a 5.5 m radius. Dropping those stations leaves a
    /// gap, which is honest; drawing them produces a bow-tie.
    /// </para>
    /// </summary>
    private static List<Vec2> SampleOffset(Alignment alignment, double from, double to, double offset)
    {
        var points = new List<Vec2>();
        foreach (var sample in alignment.Sample(from, to, 0.02, 0.4, 8.0))
        {
            if (Math.Abs(offset * sample.Curvature) >= 0.95) continue;
            points.Add(sample.Position + Vec2.FromHeading(sample.Heading).Perp * offset);
        }
        return points;
    }
}
