using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The mini excavator (#614): a 2.7 t crawler of the common class (Kubota KX027, CAT 302.7: 1.5 m
/// over rubber tracks, a 2.3 m boom and a 1.25 m stick, about 4.6 m of reach and 2.6 m of dig
/// depth), with a short-tail house, an open canopy and a dozer blade at the front of its tracks.
/// The excavator's numbers (<see cref="ExcavatorSpec"/>) and its canopy's. AUTHORED frame as
/// <see cref="ExcavatorLayout"/>'s. Godot maths only, so the unit tests link it.
/// </summary>
public static class MiniExcavatorLayout
{
    // ---- the undercarriage ------------------------------------------------------------------
    public const float TrackHalfLength = 0.95f, HalfGauge = 0.6f, ShoeWidth = 0.3f, TrackHeight = 0.45f;
    public const float RingTop = 0.55f;

    // ---- the upper structure: a short tail, so the house hardly swings past the tracks -------
    public const float HouseHalf = 0.74f, HouseFront = 0.55f, HouseBack = -0.92f, HouseTop = 1.3f;
    /// <summary>The canopy: half its width, its back and front posts, its floor and its roof.</summary>
    public const float CabHalf = 0.5f, CabBack = -0.62f, CabFront = 0.45f, CabFloor = 0.62f, CabTop = 2.42f;

    // ---- the arm: on a swing bracket in front of the operator --------------------------------
    public static readonly Vector3 BoomFoot = new(0f, 0.82f, 0.82f);
    public const float BoomLength = 2.3f, StickLength = 1.25f, BucketLength = 0.6f;
    public const float BoomMin = -0.85f, BoomMax = 1.15f, StickMin = -2.6f, StickMax = -0.4f, BucketMin = -2.4f, BucketMax = 0.7f;
    /// <summary>Quicker than the big one's: about 9 rpm of slew, the boom over its travel in under 4 s.</summary>
    public const float SlewRate = 0.95f, BoomRate = 0.55f, StickRate = 0.75f, BucketRate = 1.2f;
    public const float RestBoom = 0.35f, RestStick = -2.2f, RestBucket = -1.4f;

    // ---- the dozer blade ---------------------------------------------------------------------
    /// <summary>
    /// The blade's arms pivot on the track frame's front (authored), and its cutting edge is
    /// <see cref="BladeReach"/> from the pivot: along +Z and down to the ground when the arms are at 0.
    /// </summary>
    public static readonly Vector3 BladePivot = new(0f, 0.3f, 0.86f);
    public static readonly Vector2 BladeReach = new(0.5f, -0.3f);   // (forward, up)
    /// <summary>The blade's width (a little over the tracks') and height.</summary>
    public const float BladeWidth = 1.55f, BladeHeight = 0.34f;
    /// <summary>Its travel, rad: a little below the ground to cut, about 30 cm up to carry; and its rate.</summary>
    public const float BladeMin = -0.15f, BladeMax = 0.6f, BladeRate = 0.4f;

    // ---- the tracks --------------------------------------------------------------------------
    /// <summary>4.5 km/h on rubber tracks.</summary>
    public const float TopSpeed = 1.25f, Accel = 1.0f, TurnRate = 0.8f;

    public static readonly ExcavatorSpec Spec = new()
    {
        TrackHalfLength = TrackHalfLength, HalfGauge = HalfGauge, ShoeWidth = ShoeWidth, TrackHeight = TrackHeight, RingTop = RingTop,
        HouseHalf = HouseHalf, HouseFront = HouseFront, HouseBack = HouseBack, HouseTop = HouseTop,
        BoomFoot = BoomFoot, BoomLength = BoomLength, StickLength = StickLength, BucketLength = BucketLength,
        BoomMin = BoomMin, BoomMax = BoomMax, StickMin = StickMin, StickMax = StickMax, BucketMin = BucketMin, BucketMax = BucketMax,
        SlewRate = SlewRate, BoomRate = BoomRate, StickRate = StickRate, BucketRate = BucketRate,
        RestBoom = RestBoom, RestStick = RestStick, RestBucket = RestBucket,
        HasBlade = true, BladeMin = BladeMin, BladeMax = BladeMax, BladeRate = BladeRate,
        TopSpeed = TopSpeed, Accel = Accel, TurnRate = TurnRate,
        // the slew to 2.8°, the bucket to 2.8°, the blade to 6°: 32 bits for five things
        Bits = (7, 8, 8, 6, 3),
    };

    /// <summary>How high the blade's cutting edge is over the ground at a blade angle, m (below 0: cutting in).</summary>
    public static float BladeEdge(float blade)
    {
        float c = Mathf.Cos(blade), s = Mathf.Sin(blade);
        return BladePivot.Y + BladeReach.Y * c + BladeReach.X * s;
    }
}
