namespace UnitSport.Terrain.Format;

/// <summary>
/// Swiss junction signs and the Wartelinie (#121), shared by the network stage that places them
/// and the game that draws them. Values and sources: docs/notes/tools/junction-priority.md
/// (SSV SR 741.21 Art. 36, 37, 75, 102, 103 and Anhang 1; Kanton Bern "Handbuch Markierung 1").
/// </summary>
public static class RoadSigns
{
    /// <summary>Sign pole: a 76 mm steel tube (Swiss standpipe type B; 60 mm type A is the light one).</summary>
    public const float PoleDiameter = 0.076f;

    /// <summary>Signal variant: <see cref="Normal"/> on main and secondary roads, <see cref="Small"/> allowed inside localities (SSV Art. 102 al. 2).</summary>
    public const byte Normal = 0, Small = 1;

    /// <summary>
    /// Side of the plate in metres (SSV Anhang 1 III): 3.02 triangle 90 cm normal / 60 cm small,
    /// 3.03 square (stood on a corner) 50 cm normal / 35 cm small.
    /// </summary>
    public static float Side(PointPropType type, byte variant) => type switch
    {
        PointPropType.MainRoadSign => variant == Small ? 0.35f : 0.50f,
        _ => variant == Small ? 0.60f : 0.90f,
    };

    /// <summary>Red (3.02) or black (3.03) border width, SSV Anhang 1 III.</summary>
    public static float Border(PointPropType type, byte variant) => type switch
    {
        PointPropType.MainRoadSign => variant == Small ? 0.02f : 0.025f,
        _ => variant == Small ? 0.05f : 0.07f,
    };

    /// <summary>Plate height: an equilateral triangle's, or a diamond's diagonal.</summary>
    public static float PlateHeight(PointPropType type, byte variant) => type == PointPropType.MainRoadSign
        ? Side(type, variant) * MathF.Sqrt(2f)
        : Side(type, variant) * MathF.Sqrt(3f) * 0.5f;

    /// <summary>
    /// Lower plate edge above the road: SSV Art. 103 al. 3 allows 0.60 to 2.50 m. 1.50 m by the
    /// road; 2.20 m where the sign stands on a sidewalk, so a pedestrian walks under it.
    /// </summary>
    public const float LowerEdge = 1.5f, LowerEdgeSidewalk = 2.2f;

    /// <summary>
    /// Carriageway edge to the nearest plate edge (SSV Art. 103 al. 4): 0.30..2.00 m inside
    /// localities, 0.50..2.00 m outside. The minimum is used.
    /// </summary>
    public static float Clearance(bool urban) => urban ? 0.30f : 0.50f;

    /// <summary>Wartelinie 6.13 (SSV Art. 75 al. 3): a row of white triangles, BE handbook p. 39.</summary>
    public const float ToothBase = 0.50f, ToothHeight = 0.60f, ToothGap = 0.25f;
}
