namespace UnitSport.Terrain.Format;

/// <summary>
/// Road embankment and retaining-wall geometry (#125), shared by the build-time planner
/// (RoadGen <c>EmbankmentPlanner</c>, which decides where walls stand) and the runtime blend
/// (<c>TerrainMeshBuilder.ComputeRoadBlend</c>, which shapes the ground and frees it behind a
/// wall). Both must agree to the centimetre, so every number lives here.
///
/// <para>
/// Cross-section: the road is level across at its centreline height out to
/// <see cref="EdgeOffset"/>. Beyond the edge the ground may lie no lower than the fill slope
/// (<see cref="FillSlope"/> down per metre out) and no higher than the cut slope
/// (<see cref="CutSlope"/> up per metre), up to <see cref="Reach"/> out. Where that is not enough
/// the planner stands a wall, stored as a <see cref="LinearPropType.RetainingWallFill"/> or
/// <see cref="LinearPropType.RetainingWallCut"/> linear prop.
/// </para>
///
/// <para>
/// Wall prop convention: the points are the FACE line (the exposed vertical side) with the foot
/// height at each point and the wall's height above it. The solid is on the LEFT of the point
/// order (seen from above, walking the points); the open air is on the right. A fill wall
/// therefore runs with the road on its left, a cut wall with the road on its right.
/// </para>
///
/// <para>
/// The visible wall is its crown, <see cref="WallCrown"/> thick (the stored <c>Thickness</c>).
/// The heightfield cannot hold a vertical step: its one-cell transition is up to a lattice
/// diagonal wide, wherever the line falls. So the face side is kept clean (the ground is released
/// or levelled out to <see cref="FreeDepth"/> inside the solid, more than a diagonal) and the
/// transition lies on the solid side, under a cover <see cref="CoverDepth"/> deep from the face:
/// under the road behind a fill wall, under the levelled backfill behind a cut wall. The cover is
/// part of the wall's mesh and collision (<c>RoadWallBuilder</c>).
/// </para>
/// </summary>
public static class RoadEmbankment
{
    /// <summary>Fill slope, 2:3: metres down per metre out.</summary>
    public const double FillSlope = 2.0 / 3.0;

    /// <summary>Cut slope, 1:1: metres up per metre out.</summary>
    public const double CutSlope = 1.0;

    /// <summary>How far past the edge a slope may run on a road that can have walls.</summary>
    public const double RoadReachM = 7.0;

    /// <summary>
    /// The same for everything else (tracks, paths, squares): no wall ever, so a longer reach
    /// only builds a longer ledge over the cliff a mountain path runs along
    /// (<c>draped-centreline-cliff-lip-spike</c>). The old smoothstep corridor's falloff.
    /// </summary>
    public const double MinorReachM = 3.0;

    // Swiss retaining-wall dimensions: Kanton Bern TBA, "Arbeitshilfe Entwurf und Gestaltung von
    // Stuetzmauern" (2023-08-01, after ASTRA FHB K / VSS): valley-side (fill) crown 40 cm, at least
    // 35 (45 with the VRS 2211 restraint), standing 6 cm over the asphalt; hill-side (cut) crown at
    // least 35 cm, 20 cm over the finished ground behind, which is a 50 cm level maintenance path;
    // 50 cm lateral clearance between the edge line and the wall, asphalt up to the wall.

    /// <summary>Thickness of the visible wall, its crown: the 40 cm rule (Bern TBA).</summary>
    public const float WallCrown = 0.40f;

    /// <summary>Lateral clearance between the carriageway edge and the wall (Bern TBA: 50 cm).</summary>
    public const double WallClearance = 0.5;

    /// <summary>A fill wall's crown stands this far over the road it carries (Bern TBA: 6 cm).</summary>
    public const float FillCrownLift = 0.06f;

    /// <summary>A cut wall's crown stands this far over the ground behind it (Bern TBA: 20 cm).</summary>
    public const float CutCrownOver = 0.20f;

    /// <summary>
    /// How far inside the solid, from the face, the ground stays released (fill) or level with the
    /// road (cut): more than a lattice diagonal, so no transition triangle reaches the face side.
    /// </summary>
    public const double FreeDepth = 1.5;

    /// <summary>
    /// How deep the wall's cover reaches from the face: <see cref="FreeDepth"/> plus a lattice
    /// diagonal, so it lies over every transition triangle.
    /// </summary>
    public const float CoverDepth = 3.0f;

    /// <summary>A fill wall's face stands this far out from the edge: clearance, then the crown.</summary>
    public const double FillFaceOffset = WallClearance + WallCrown;

    /// <summary>A cut wall's face stands this far out from the edge: the clearance (paved gutter).</summary>
    public const double CutFaceOffset = WallClearance;

    /// <summary>A slope that misses the ground at its reach by less than this needs no wall.</summary>
    public const double MinWallDrop = 0.5;

    /// <summary>Shorter wall runs are dropped as slivers.</summary>
    public const double MinWallRunM = 6.0;

    /// <summary><see cref="RoadLinearProp.Variant"/> of a wall prop: 0 generated and drawn.</summary>
    public const byte VariantGenerated = 0;

    /// <summary>
    /// A surveyed TLM wall (<see cref="RoadClass.Wall"/>) already stands there: the prop only frees
    /// the ground, the TLM segment draws the wall.
    /// </summary>
    public const byte VariantTlmWall = 1;

    /// <summary>Classes that may get a retaining wall: the drivable roads and railway lines.</summary>
    public static bool AllowsWall(RoadClass c) => c <= RoadClass.Lane || c == RoadClass.Railway;

    /// <summary>
    /// Whether this segment may stand on retaining walls: a drivable road or a railway line, at
    /// grade, not a ford or stairs, not a tram, funicular or a rail piece inside a carriageway
    /// (the road there decides).
    /// </summary>
    public static bool AllowsWall(RoadSegment seg) =>
        AllowsWall(seg.Class) && IsAtGrade(seg)
        && (seg.Flags & (RoadFlags.Ford | RoadFlags.Stairs | RoadFlags.Tramway | RoadFlags.Funicular)) == 0
        && (seg.Attributes.Flags & RoadAttrFlags.Embedded) == 0;

    /// <summary>Whether a segment gets an embankment at all (at-grade ground surfaces only).</summary>
    public static bool IsAtGrade(RoadSegment seg) =>
        (seg.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) == 0
        && !RoadFormat.IsAerial(seg.Class) && !RoadFormat.IsWatercourse(seg.Class) && !RoadFormat.IsWall(seg.Class);

    public static double Reach(RoadClass c) => AllowsWall(c) ? RoadReachM : MinorReachM;

    /// <summary>
    /// Distance from the centreline to the level cross-section's edge on one side: half the
    /// carriageway (the wider of the drawn width and the v3 <c>widthCm</c>) plus that side's
    /// sidewalk and verge (#119). At least one lattice spacing, or a 1.2 m footpath can miss every
    /// corner of the quad its centreline crosses.
    /// </summary>
    public static double EdgeOffset(RoadSegment seg, bool right)
    {
        var a = seg.Attributes;
        double half = Math.Max(seg.Width, a.WidthCm / 100.0) * 0.5;
        var side = right ? a.Right : a.Left;
        half += side.OuterDm / 10.0;
        return Math.Max(half, ChunkFormat.SpacingM);
    }

    /// <summary>Distance from the centreline to a wall's face, by wall type.</summary>
    public static double FaceOffset(RoadSegment seg, bool right, LinearPropType type) =>
        EdgeOffset(seg, right) + (type == LinearPropType.RetainingWallFill ? FillFaceOffset : CutFaceOffset);
}
