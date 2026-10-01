namespace UnitSport.Terrain.Format;

/// <summary>
/// Road railings (#126), shared by the build-time planner (RoadGen <c>RailingPlanner</c>, which
/// decides where they stand) and the runtime (<c>RailingBuilder</c>, which draws them and gives
/// them collision).
///
/// <para>
/// Prop convention (<see cref="LinearPropType.Guardrail"/>, <see cref="LinearPropType.Fence"/>,
/// <see cref="LinearPropType.MedianDouble"/>): the points are the rail's road-side face line, each
/// with its foot height (the road it guards, or a wall's crown) and the rail's top above it. The
/// road is on the RIGHT of the point order, the posts on the left. <c>Param</c> is the post
/// spacing. A <see cref="LinearPropType.MedianDouble"/> is one carriageway's beam of the median's
/// double guardrail: the other carriageway writes the other beam, back to back with it.
/// </para>
///
/// <para>
/// Typical as-built figures, not taken from a standard: W-beam (Leitschiene) top about 0.75 m
/// over the road, band about 0.3 m, posts every 2-4 m, face about 0.5 m out from the edge line
/// (the 50 cm clearance of the wall rules in <see cref="RoadEmbankment"/>); a railing (Geländer)
/// on a wall about 1.0 m high with a top and a middle rail.
/// </para>
/// </summary>
public static class RoadRailing
{
    /// <summary>Guardrail top over its foot (the road).</summary>
    public const float GuardrailTop = 0.75f;

    /// <summary>The W-beam band's height, from its top down.</summary>
    public const float BeamBand = 0.31f;

    /// <summary>Railing (fence) top over its foot.</summary>
    public const float FenceTop = 1.0f;

    /// <summary>Post spacing: a guardrail's (the low-poly 4 m of the lighter systems), a fence's.</summary>
    public const float GuardrailPostSpacing = 4f;
    public const float FencePostSpacing = 2.5f;

    /// <summary>A guardrail's face stands this far out from the carriageway's edge.</summary>
    public const double EdgeClearance = 0.5;

    /// <summary>The post line lies this far behind the beam's face.</summary>
    public const float PostSetback = 0.12f;

    /// <summary>
    /// A side needs a railing where the ground this far out from the edge lies more than
    /// <see cref="MinDrop"/> below the road (a fill slope taller than a person, or a wall).
    /// </summary>
    public const double DropReach = 5.0;
    public const double MinDrop = 3.0;

    /// <summary>
    /// The median's double guardrail goes where the other carriageway's paved edge lies at most this
    /// far from this one's (TLM pairs shifted apart by #117 leave 1.4 m).
    /// </summary>
    public const double MaxMedian = 4.0;

    /// <summary>Half the gap between the two beams of a median's double guardrail.</summary>
    public const double MedianHalfGap = 0.2;

    /// <summary>Railing runs shorter than this are dropped.</summary>
    public const double MinRunM = 12.0;

    /// <summary>How far a post runs on below its foot, into the slope beside the road.</summary>
    public const float PostSink = 0.8f;

    /// <summary>Classes that get guardrails above drops and on walls: every road class above a lane.</summary>
    public static bool GetsGuardrail(RoadClass c) => c <= RoadClass.Minor;

    /// <summary>Classes that get any railing at all (a fence on a wall's crown for the smaller ones).</summary>
    public static bool GetsRailing(RoadClass c) => c <= RoadClass.Lane;

    public static bool IsRailing(RoadLinearProp p) =>
        p.Type is LinearPropType.Guardrail or LinearPropType.Fence or LinearPropType.MedianDouble;
}
