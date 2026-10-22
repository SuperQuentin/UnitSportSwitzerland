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

    /// <summary>Plate height: an equilateral triangle's, a diamond's diagonal, or a canton sign's rectangle.</summary>
    public static float PlateHeight(PointPropType type, byte variant) => type switch
    {
        PointPropType.MainRoadSign => Side(type, variant) * MathF.Sqrt(2f),
        PointPropType.CantonSign => CantonSignHeight,
        PointPropType.TramStop => TramStopHeight,
        _ => Side(type, variant) * MathF.Sqrt(3f) * 0.5f,
    };

    /// <summary>A canton sign's plate (#1038, a test world's border): wide enough for its shield and two lines of name.</summary>
    public const float CantonSignWidth = 1.2f, CantonSignHeight = 0.6f;

    /// <summary>A tram stop's sign (#1057): an upright plate with the tram on it.</summary>
    public const float TramStopWidth = 0.45f, TramStopHeight = 0.6f;

    /// <summary>The cantons in the federal order (BFS numbers 1-26, as MapSetup bakes them into <c>switzerland.bin</c>).</summary>
    public static readonly string[] CantonCodes =
    [
        "ZH", "BE", "LU", "UR", "SZ", "OW", "NW", "GL", "ZG", "FR", "SO", "BS", "BL",
        "SH", "AR", "AI", "SG", "GR", "AG", "TG", "TI", "VD", "VS", "NE", "GE", "JU",
    ];

    // each canton's name as its border signs give it, in its own language
    private static readonly string[] CantonNames =
    [
        "Kanton Zürich", "Kanton Bern", "Kanton Luzern", "Kanton Uri", "Kanton Schwyz", "Kanton Obwalden", "Kanton Nidwalden",
        "Kanton Glarus", "Kanton Zug", "Canton de Fribourg", "Kanton Solothurn", "Kanton Basel-Stadt", "Kanton Basel-Landschaft",
        "Kanton Schaffhausen", "Kanton Appenzell Ausserrhoden", "Kanton Appenzell Innerrhoden", "Kanton St. Gallen",
        "Kanton Graubünden", "Kanton Aargau", "Kanton Thurgau", "Cantone Ticino", "Canton de Vaud", "Canton du Valais",
        "Canton de Neuchâtel", "Canton de Genève", "Canton du Jura",
    ];

    /// <summary>A <see cref="PointPropType.CantonSign"/>'s variant: its canton's place in <see cref="CantonCodes"/> plus one; 0 a test world's default zone (null, or a code not known).</summary>
    public static byte CantonVariant(string? code) => (byte)(code is null ? 0 : Array.IndexOf(CantonCodes, code) + 1);

    /// <summary>The canton's code for a sign variant, null for the default zone.</summary>
    public static string? CantonCode(byte variant) => variant >= 1 && variant <= CantonCodes.Length ? CantonCodes[variant - 1] : null;

    /// <summary>The name a canton sign shows: the canton's own, or the default zone's (the cantons whose lights are not checked yet).</summary>
    public static string CantonName(byte variant) => variant >= 1 && variant <= CantonNames.Length ? CantonNames[variant - 1] : "Other cantons";

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
