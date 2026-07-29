namespace UnitSport.Tools.RoadGen.Meshing;

using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;

/// <summary>A carriageway surface: paired left/right edges sampled along the alignment.</summary>
public sealed class Ribbon
{
    public required int LinkId { get; init; }
    public required RoadProfile Profile { get; init; }
    public required int Layer { get; init; }

    public List<AlignmentSample> Stations { get; } = new();
    public List<Vec2> Left { get; } = new();
    public List<Vec2> Right { get; } = new();

    public bool IsEmpty => Stations.Count < 2;

    /// <summary>Closed outline, counter-clockwise: down the left edge and back up the right.</summary>
    public List<Vec2> Outline()
    {
        var ring = new List<Vec2>(Left.Count + Right.Count);
        ring.AddRange(Left);
        for (int i = Right.Count - 1; i >= 0; i--) ring.Add(Right[i]);
        return ring;
    }

    public double Area
    {
        get
        {
            double total = 0;
            for (int i = 1; i < Stations.Count; i++)
            {
                // each cell is a trapezoid; sum the two triangles
                total += Math.Abs((Right[i - 1] - Left[i - 1]).Cross(Left[i] - Left[i - 1])) * 0.5;
                total += Math.Abs((Right[i] - Right[i - 1]).Cross(Left[i] - Right[i - 1])) * 0.5;
            }
            return total;
        }
    }
}

/// <summary>
/// Sweeps a carriageway along a trimmed alignment.
///
/// <para>
/// The offset is taken from the alignment's own analytic heading rather than from the
/// difference between neighbouring vertices. That distinction matters: a vertex bisector is
/// only as smooth as the tessellation, so it re-introduces the faceting the clothoid fit just
/// removed, and on a tight bend it pinches the inside edge.
/// </para>
/// </summary>
public static class RibbonBuilder
{
    public static Ribbon Build(RoadLink link, double maxDeviation = 0.05,
        double minStep = 0.5, double maxStep = 25.0)
    {
        var ribbon = new Ribbon { LinkId = link.Id, Profile = link.Profile, Layer = link.Layer };
        if (link.Alignment is not { IsEmpty: false } alignment) return ribbon;

        double from = link.TrimStart;
        double to = alignment.Length - link.TrimEnd;
        if (to - from < 1e-3) return ribbon;

        double half = link.Profile.HalfWidth;

        // sampling the trimmed range directly, not the whole alignment filtered down to it —
        // the trim points are exact stations and everything between them is tessellated
        foreach (var sample in alignment.Sample(from, to, maxDeviation, minStep, maxStep))
            AddStation(ribbon, alignment, sample.Distance, half);

        return ribbon;
    }

    private static void AddStation(Ribbon ribbon, Alignment alignment, double s, double half)
    {
        var position = alignment.PointAt(s);
        double heading = alignment.HeadingAt(s);
        double curvature = alignment.CurvatureAt(s);
        var normal = Vec2.FromHeading(heading).Perp;

        ribbon.Stations.Add(new AlignmentSample(position, heading, curvature, s));
        ribbon.Left.Add(position + normal * half);
        ribbon.Right.Add(position - normal * half);
    }

}
