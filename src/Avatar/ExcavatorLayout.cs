using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The tracked excavator (#611): one set of numbers for the drawn model, its hull, the arm's
/// joints and what the tracks do, after a 20 t crawler of the common class (Liebherr R 920,
/// CAT 320: 2.98 m over the tracks, a 5.7 m boom and a 2.9 m stick, about 10 m of reach and
/// 6.5 m of dig depth). AUTHORED frame like every avatar mesh: +Z forward (the arm's way), +X the
/// left side, origin on the ground under the slewing ring. Metres and radians.
///
/// <para>
/// The cab sits in the middle of the upper structure here, not on its left as on a real one: the
/// heavy cockpit (<see cref="HeavyCabin"/>) is built for a cab across the vehicle's middle, and a
/// low-poly excavator reads as one either way. The boom stands to its right.
/// </para>
///
/// <para>
/// Godot maths only, in a file of its own, so the unit tests link it.
/// </para>
/// </summary>
public static class ExcavatorLayout
{
    // ---- the undercarriage ------------------------------------------------------------------
    /// <summary>The tracks: half their length, the half gauge (track centre to centre), a shoe's width, their height.</summary>
    public const float TrackHalfLength = 2.22f, HalfGauge = 1.19f, ShoeWidth = 0.6f, TrackHeight = 0.9f;
    /// <summary>The slewing ring's top: where the upper structure stands.</summary>
    public const float RingTop = 1.1f;

    // ---- the upper structure ----------------------------------------------------------------
    /// <summary>The house: half its width, its front and back (the counterweight's face), its roof.</summary>
    public const float HouseHalf = 1.37f, HouseFront = 1.45f, HouseBack = -2.25f, HouseTop = 2.35f;
    /// <summary>The cab: half its width, its back, its floor and its roof.</summary>
    public const float CabHalf = 0.5f, CabBack = 0.0f, CabFloor = 1.25f, CabTop = 3.0f;

    // ---- the arm ----------------------------------------------------------------------------
    /// <summary>The boom's foot pivot, authored: right of the cab, at the house's front.</summary>
    public static readonly Vector3 BoomFoot = new(-0.95f, 1.75f, 1.1f);
    /// <summary>Boom, stick and bucket (pivot to cutting edge), m.</summary>
    public const float BoomLength = 5.7f, StickLength = 2.9f, BucketLength = 1.45f;

    /// <summary>
    /// The joints' travel, rad. The boom from the horizontal, up positive; the stick and the
    /// bucket each from the line of the part before, down (folded in) negative.
    /// </summary>
    public const float BoomMin = -0.75f, BoomMax = 1.05f, StickMin = -2.55f, StickMax = -0.45f, BucketMin = -2.4f, BucketMax = 0.6f;

    /// <summary>How fast each moves at full stick, rad/s: about 6 rpm of slew, a boom from bottom to top in 5 s.</summary>
    public const float SlewRate = 0.65f, BoomRate = 0.36f, StickRate = 0.5f, BucketRate = 0.85f;

    /// <summary>The arm as it is parked and delivered: boom a little up, stick folded, bucket curled under.</summary>
    public const float RestBoom = 0.25f, RestStick = -2.2f, RestBucket = -1.3f;

    // ---- the tracks -------------------------------------------------------------------------
    /// <summary>Top speed on the tracks either way, m/s (5.8 km/h), and how fast it gets there.</summary>
    public const float TopSpeed = 1.6f, Accel = 1.2f;
    /// <summary>A full counter-rotation of the tracks, turning on the spot, rad/s.</summary>
    public const float TurnRate = 0.55f;

    /// <summary>These numbers as the spec the ride, the arm node and the checks work from.</summary>
    public static readonly ExcavatorSpec Spec = new()
    {
        TrackHalfLength = TrackHalfLength, HalfGauge = HalfGauge, ShoeWidth = ShoeWidth, TrackHeight = TrackHeight, RingTop = RingTop,
        HouseHalf = HouseHalf, HouseFront = HouseFront, HouseBack = HouseBack, HouseTop = HouseTop,
        BoomFoot = BoomFoot, BoomLength = BoomLength, StickLength = StickLength, BucketLength = BucketLength,
        BoomMin = BoomMin, BoomMax = BoomMax, StickMin = StickMin, StickMax = StickMax, BucketMin = BucketMin, BucketMax = BucketMax,
        SlewRate = SlewRate, BoomRate = BoomRate, StickRate = StickRate, BucketRate = BucketRate,
        RestBoom = RestBoom, RestStick = RestStick, RestBucket = RestBucket,
        TopSpeed = TopSpeed, Accel = Accel, TurnRate = TurnRate,
        Bits = (8, 8, 8, 8, 0),
    };

    public static float ClampBoom(float a) => Spec.ClampBoom(a);
    public static float ClampStick(float a) => Spec.ClampStick(a);
    public static float ClampBucket(float a) => Spec.ClampBucket(a);

    /// <summary>
    /// Where the bucket's cutting edge is in the arm's own plane: metres forward of the slewing
    /// ring and up from the ground, for these joint angles.
    /// </summary>
    public static Vector2 Edge(float boom, float stick, float bucket) => Spec.Edge(boom, stick, bucket);

    /// <summary>
    /// What a parked one keeps of its arm, in the 32 bits of <c>VehicleState.Flags</c>: the slew
    /// and the three joints, eight bits each over their travel (1.4° of slew, under a degree of a
    /// joint). Zero is "never set": a fresh machine parks in its rest pose.
    /// </summary>
    public static int Pack(float slew, float boom, float stick, float bucket) => Spec.Pack(slew, boom, stick, bucket);

    /// <summary>The slew and joints a packed word holds, or the rest pose for zero.</summary>
    public static (float Slew, float Boom, float Stick, float Bucket) Unpack(int flags)
    {
        var (s, b, k, u, _) = Spec.Unpack(flags);
        return (s, b, k, u);
    }

    /// <summary>
    /// The tracks' speeds, m/s, for a travel speed and a turn rate (+ turning left): what the
    /// drawn tracks run at, and what makes it a crawler — on the spot, one runs back as the other
    /// runs forward.
    /// </summary>
    public static (float Left, float Right) Tracks(float speed, float yawRate) => Spec.Tracks(speed, yawRate);
}
