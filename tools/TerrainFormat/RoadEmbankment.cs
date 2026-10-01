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
/// order (seen from above, walking the points), <see cref="WallThickness"/> deep; the open air is
/// on the right. A fill wall therefore runs with the road on its left, a cut wall with the road on
/// its right. The heightfield cannot hold a vertical step, so the runtime blend releases the
/// ground from the road's slopes past the <em>free line</em>, half the thickness inside the solid:
/// the one-cell steep transition between shelf and foot then lies inside the wall.
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
    /// The same for everything else (tracks, paths, rail, squares): no wall ever, so a longer reach
    /// only builds a longer ledge over the cliff a mountain path runs along
    /// (<c>draped-centreline-cliff-lip-spike</c>). The old smoothstep corridor's falloff.
    /// </summary>
    public const double MinorReachM = 3.0;

    /// <summary>Depth of a retaining wall's solid, face to back. Hides the one-cell transition.</summary>
    public const float WallThickness = 3.0f;

    /// <summary>A fill wall's face stands this far out from the edge (its cap is the shoulder).</summary>
    public const double FillFaceOffset = 2.0;

    /// <summary>A cut wall's face stands this far out from the edge (a small gutter).</summary>
    public const double CutFaceOffset = 0.2;

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

    /// <summary>Classes that may get a retaining wall: the drivable roads.</summary>
    public static bool AllowsWall(RoadClass c) => c <= RoadClass.Lane;

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
        half += (side.SidewalkDm + side.VergeDm) / 10.0;
        return Math.Max(half, ChunkFormat.SpacingM);
    }

    /// <summary>Distance from the centreline to a wall's face, by wall type.</summary>
    public static double FaceOffset(RoadSegment seg, bool right, LinearPropType type) =>
        EdgeOffset(seg, right) + (type == LinearPropType.RetainingWallFill ? FillFaceOffset : CutFaceOffset);
}
