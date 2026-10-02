namespace UnitSport.Terrain.Format;

/// <summary>
/// The synthetic water bed (#298): how deep the water is at a point, from its distance to the
/// shore, the local width of the water and the depth the water body may reach. One set of rules for
/// the preprocessor (real tiles without a survey), the generated world and the fixture courses, so
/// a lake looks the same whichever of them made it.
///
/// <para>
/// <b>Open water</b> (a lake, or a river wider than <see cref="OpenWidthM"/>): a littoral shelf at
/// <see cref="ShelfSlope"/> out to <see cref="ShelfWidthM"/> from the shore, then a drop-off at
/// <see cref="DropSlope"/> down to the body's maximum. <b>A channel</b> (a river, a narrow arm): a
/// parabola across the width, as deep as <see cref="ChannelDepth"/> says for that width, never
/// deeper than the open-water rule at the same distance. The two blend between
/// <see cref="ChannelWidthM"/> and <see cref="OpenWidthM"/> so a widening river runs into its lake
/// without a step. Depth is 0 on the shoreline itself, so the bed meets the bank flush.
/// </para>
/// </summary>
public static class WaterBed
{
    /// <summary>Slope of the littoral shelf (1:12): 2 m deep 24 m out.</summary>
    public const double ShelfSlope = 1.0 / 12.0;

    /// <summary>How far out the shelf runs before the drop-off.</summary>
    public const double ShelfWidthM = 24.0;

    /// <summary>Slope of the drop-off below the shelf (1:4).</summary>
    public const double DropSlope = 0.25;

    /// <summary>Below this width water is a channel.</summary>
    public const double ChannelWidthM = 80.0;

    /// <summary>Above this width water is open water; between the two it blends.</summary>
    public const double OpenWidthM = 120.0;

    /// <summary>Shallowest and deepest a body's maximum may be.</summary>
    public const double MinMaxDepthM = 1.5, MaxMaxDepthM = 150.0;

    /// <summary>
    /// The deepest a water body of this area gets: 0.3 · √A^0.6, clamped. A 50 m pond 3 m, a
    /// 500 m lake 12 m, 10 km² 38 m. Real lakes scatter a lot around any such rule; this only has
    /// to look plausible where there is no survey.
    /// </summary>
    public static double MaxDepthForArea(double areaM2) =>
        Math.Clamp(0.3 * Math.Pow(Math.Sqrt(Math.Max(areaM2, 0)), 0.6), MinMaxDepthM, MaxMaxDepthM);

    /// <summary>Centre depth of a channel of this width: 0.4 m + 4.5 cm per metre, 6 m at most (the Rhône's 50 m: 2.65 m).</summary>
    public static double ChannelDepth(double widthM) => Math.Clamp(0.4 + 0.045 * widthM, 0.4, 6.0);

    /// <summary>The open-water profile: shelf, then drop-off, capped at <paramref name="maxDepth"/>.</summary>
    public static double OpenDepth(double distToShoreM, double maxDepth)
    {
        double d = Math.Max(distToShoreM, 0);
        double depth = d <= ShelfWidthM
            ? d * ShelfSlope
            : ShelfWidthM * ShelfSlope + (d - ShelfWidthM) * DropSlope;
        return Math.Min(depth, maxDepth);
    }

    /// <summary>
    /// Depth at a point <paramref name="distToShoreM"/> from the nearest bank, in water whose
    /// local half width (distance from the bank to the middle) is <paramref name="halfWidthM"/>
    /// (<see cref="double.PositiveInfinity"/> for open water), in a body that reaches at most
    /// <paramref name="maxDepth"/>.
    /// </summary>
    public static double Depth(double distToShoreM, double halfWidthM, double maxDepth)
    {
        double open = OpenDepth(distToShoreM, maxDepth);
        double width = 2 * halfWidthM;
        if (!(width < OpenWidthM)) return open;

        double t = Math.Clamp(distToShoreM / Math.Max(halfWidthM, 1e-6), 0, 1);
        double channel = Math.Min(ChannelDepth(width), maxDepth) * (1 - (1 - t) * (1 - t));
        double narrow = Math.Min(open, channel);
        double a = SmoothStep(ChannelWidthM, OpenWidthM, width);
        return narrow + (open - narrow) * a;
    }

    private static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
