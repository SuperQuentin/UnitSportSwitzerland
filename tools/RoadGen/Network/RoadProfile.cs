namespace UnitSport.Tools.RoadGen.Network;

using UnitSport.Tools.RoadGen.Geometry;

public enum LineStyle { None, Solid, Dashed }

/// <summary>
/// Which longitudinal lines a carriageway carries.
///
/// <para>
/// Switzerland publishes no lane-marking dataset — <c>tlm_strassen_strasseninfo</c> is
/// junctions and POIs, not lanes — so these are <i>inferred</i> from width class, surface
/// and whether the carriageway is direction-separated. The dash periods are ordinary
/// European practice and are configurable rather than claimed as a norm.
/// </para>
/// </summary>
public sealed record MarkingPlan(
    LineStyle Centre,
    LineStyle LaneDivider,
    LineStyle Edge,
    double DashOn = 6.0,
    double DashOff = 12.0,
    double LineWidth = 0.15)
{
    public static readonly MarkingPlan None = new(LineStyle.None, LineStyle.None, LineStyle.None);
}

/// <summary>
/// Everything the geometry needs to know about a kind of road. Deliberately independent of
/// swissTLM3D so the synthesiser can make its own profiles without inventing fake attributes.
/// </summary>
/// <param name="Priority">
/// Higher wins at a junction: the minor road gets the give-way line, and the major road's
/// surface is the one drawn on top where they overlap.
/// </param>
public sealed record RoadProfile(
    string Name,
    double Width,
    double DesignRadius,
    double MinRadius,
    int Lanes,
    MarkingPlan Markings,
    int Priority,
    bool Paved = true)
{
    public double HalfWidth => Width * 0.5;

    /// <summary>
    /// Smoothing may not move the centreline outside the road's own original footprint, and
    /// the simplify tolerance is held to the same bound. A 1.1 m footpath therefore gets a
    /// 0.55 m budget and an 11 m motorway 5.5 m — which is the right way round, because the
    /// footpath is the one surveyed along a cliff edge where a metre sideways is tens of
    /// metres down.
    /// </summary>
    public CurveStyle CurveStyle(double simplifyTolerance = 0.6)
    {
        double budget = Math.Max(0.35, HalfWidth);
        return new CurveStyle(
            DesignRadius, MinRadius,
            SpiralRatio: 0.25,
            SimplifyTolerance: Math.Min(simplifyTolerance, budget),
            MaxOffset: budget);
    }

    // Design radii are the values road design actually uses for the corresponding speed:
    // a motorway is laid out around 400 m minimum, an access lane around 20 m, and an
    // alpine footpath will happily turn inside 6 m. Using one radius for everything is what
    // makes procedural roads read as either wobbly motorways or impossibly wide footpaths.
    // Lanes is the total count across the full width, so the marking builder can place
    // dividers at k·(width/lanes) and recognise the exact middle of an even-laned road as
    // the centre line without any per-class special cases.
    public static readonly RoadProfile Motorway = new(
        "motorway", 11.0, 400, 120, 3,
        new MarkingPlan(LineStyle.None, LineStyle.Dashed, LineStyle.Solid), 100);

    public static readonly RoadProfile Expressway = new(
        "expressway", 9.0, 250, 80, 2,
        new MarkingPlan(LineStyle.None, LineStyle.Dashed, LineStyle.Solid), 90);

    public static readonly RoadProfile Ramp = new(
        "ramp", 6.0, 50, 18, 1,
        new MarkingPlan(LineStyle.None, LineStyle.None, LineStyle.Solid), 80);

    public static readonly RoadProfile Major = new(
        "major", 9.0, 120, 30, 2,
        new MarkingPlan(LineStyle.Dashed, LineStyle.None, LineStyle.Solid), 70);

    public static readonly RoadProfile Road = new(
        "road", 6.0, 60, 20, 2,
        new MarkingPlan(LineStyle.Dashed, LineStyle.None, LineStyle.None), 60);

    public static readonly RoadProfile Minor = new(
        "minor", 4.0, 30, 12, 2,
        new MarkingPlan(LineStyle.Dashed, LineStyle.None, LineStyle.None), 50);

    public static readonly RoadProfile Lane = new(
        "lane", 3.0, 20, 8, 1, MarkingPlan.None, 40);

    public static readonly RoadProfile Track = new(
        "track", 2.2, 12, 5, 1, MarkingPlan.None, 30, Paved: false);

    public static readonly RoadProfile Path = new(
        "path", 1.1, 6, 2, 1, MarkingPlan.None, 20, Paved: false);

    public static readonly RoadProfile Railway = new(
        "railway", 4.5, 300, 90, 1, MarkingPlan.None, 10);
}
