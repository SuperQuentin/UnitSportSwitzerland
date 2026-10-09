using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The articulated wheel loader (#612): one set of numbers for the drawn model, its hull, the lift
/// arm and the frame steering, after a 17 t machine of the common class (Liebherr L 550, CAT 950:
/// 3.3 m wheelbase, 1.75 m tyres, a 3 m³ bucket, about 4 m to the hinge pin at full lift). AUTHORED
/// frame like every avatar mesh: +Z forward (the bucket's way), +X the left side, origin on the
/// ground under the articulation hinge. The cab and the engine are on the rear frame, the lift arm
/// and the bucket on the front one, which swings about the hinge to steer. Metres and radians.
///
/// <para>Godot maths only, in a file of its own, so the unit tests link it.</para>
/// </summary>
public static class WheelLoaderLayout
{
    /// <summary>The axles, ahead of and behind the hinge, and the wheels on them.</summary>
    public const float FrontAxle = 1.65f, RearAxle = -1.65f, WheelRadius = 0.86f, WheelWidth = 0.6f, HalfTrack = 1.0f;
    /// <summary>The rear frame: half its width, its tail (the counterweight's face), the engine cover's top.</summary>
    public const float RearHalf = 1.25f, Tail = -3.35f, EngineTop = 2.5f;
    /// <summary>The cab, just behind the hinge: its front, back, floor and roof, and half its width.</summary>
    public const float CabFront = 0.05f, CabBack = -1.45f, CabFloor = 1.6f, CabTop = 3.35f, CabHalf = 0.72f;

    /// <summary>The lift arm's pivot on the front frame, authored, and its length to the bucket's pin.</summary>
    public static readonly Vector3 ArmPivot = new(0f, 2.05f, 0.75f);
    public const float ArmLength = 2.95f;
    /// <summary>The bucket: half its width, how far its lip stands out from the pin, its height.</summary>
    public const float BucketHalf = 1.4f, BucketReach = 1.35f, BucketHeight = 1.25f;

    /// <summary>The arm's travel from the horizontal (down puts the bucket on the ground), and the bucket's, from the arm's line (back is up).</summary>
    public const float LiftMin = -0.62f, LiftMax = 0.95f, TiltMin = -0.95f, TiltMax = 0.75f;
    /// <summary>How fast they move at full lever, rad/s: a full lift in about 6 s, a dump in 2.</summary>
    public const float LiftRate = 0.27f, TiltRate = 0.85f;
    /// <summary>Carried low, bucket rolled back: how it parks and how it drives a load.</summary>
    public const float RestLift = -0.45f, RestTilt = 0.55f;

    /// <summary>The articulation's travel either way and how fast the frame swings, rad, rad/s.</summary>
    public const float MaxArticulation = 0.70f, ArticulationRate = 0.8f;
    /// <summary>Top speed forward and in reverse, m/s (32 and 18 km/h), acceleration and braking.</summary>
    public const float TopSpeed = 8.9f, TopReverse = 5f, Accel = 1.5f, BrakeDecel = 4f;

    public static float ClampLift(float a) => Mathf.Clamp(a, LiftMin, LiftMax);
    public static float ClampTilt(float a) => Mathf.Clamp(a, TiltMin, TiltMax);

    /// <summary>
    /// The rear frame's yaw rate at a speed and an articulation (+ the front frame swung left):
    /// a frame-steered machine's kinematics with the hinge between equal half wheelbases. The rear
    /// axle runs along the rear frame, so that is the vehicle's heading.
    /// </summary>
    public static float YawRate(float speed, float articulation) =>
        FrameSteering.YawRate(speed, articulation, FrontAxle, -RearAxle);

    /// <summary>The rear axle's turning circle at an articulation, m (infinite going straight).</summary>
    public static float TurnRadius(float articulation) => FrameSteering.TurnRadius(articulation, FrontAxle, -RearAxle);

    /// <summary>The bucket pin's height over the ground at a lift.</summary>
    public static float PinHeight(float lift) => ArmPivot.Y + Mathf.Sin(lift) * ArmLength;

    // ---- the fork carriage (#615): the loader with forks instead of a bucket ---------------------

    /// <summary>
    /// The forks' pitch from level (+ back): the carriage levels itself, as a loader's parallel
    /// linkage keeps a fork frame level, so it is not the bucket's tilt against the arm.
    /// </summary>
    public const float ForkTiltMin = -0.5f, ForkTiltMax = 0.3f, ForkRestTilt = 0.05f;
    /// <summary>
    /// The tines: their top face this far under the arm's pin (on the ground with the arm down),
    /// their heel this far ahead of it, their length and half their span outside edge to outside edge.
    /// </summary>
    public const float ForkTop = 0.31f, ForkFace = 0.25f, ForkLength = 1.2f, TineHalfSpan = 0.45f;

    public static float ClampForkTilt(float a) => Mathf.Clamp(a, ForkTiltMin, ForkTiltMax);

    /// <summary>The arm's pin, forward of the hinge (in the front frame) and up from the ground, at a lift.</summary>
    public static Vector2 Pin(float lift) => new(ArmPivot.Z + Mathf.Cos(lift) * ArmLength, PinHeight(lift));

    /// <summary>The tines' top face over the ground at a lift, m.</summary>
    public static float ForkHeight(float lift) => PinHeight(lift) - ForkTop;

    /// <summary>
    /// What a parked fork loader keeps: lift eight bits, the forks' pitch and the articulation seven
    /// each, and what is on the forks ten (<c>Pallets.Carried</c>). Zero: the rest pose, empty forks.
    /// </summary>
    public static int PackForks(float lift, float tilt, float articulation, int carrying) =>
        PackArm(lift, tilt, ForkTiltMin, ForkTiltMax, articulation, carrying);

    public static (float Lift, float Tilt, float Articulation, int Carrying) UnpackForks(int flags) =>
        flags == 0 ? (LiftMin, ForkRestTilt, 0f, 0) : UnpackArm(flags, ForkTiltMin, ForkTiltMax);

    private static int PackArm(float lift, float tilt, float tiltMin, float tiltMax, float articulation, int carrying)
    {
        static uint Q(float v, float lo, float hi, int top) => (uint)(Mathf.Clamp(Mathf.RoundToInt((v - lo) / (hi - lo) * top), 0, top) + 1);
        return unchecked((int)(Q(lift, LiftMin, LiftMax, 254) | Q(tilt, tiltMin, tiltMax, 126) << 8
            | Q(articulation, -MaxArticulation, MaxArticulation, 126) << 15 | (uint)Mathf.Clamp(carrying, 0, 0x3FF) << 22));
    }

    private static (float Lift, float Tilt, float Articulation, int Carrying) UnpackArm(int flags, float tiltMin, float tiltMax)
    {
        uint f = unchecked((uint)flags);
        static float U(uint q, float lo, float hi, int top) => lo + (Mathf.Clamp((int)q, 1, top + 1) - 1) / (float)top * (hi - lo);
        return (U(f & 0xFF, LiftMin, LiftMax, 254), U(f >> 8 & 0x7F, tiltMin, tiltMax, 126),
            U(f >> 15 & 0x7F, -MaxArticulation, MaxArticulation, 126), (int)(f >> 22 & 0x3FF));
    }

    // ---- the bucket as the pallets see it (#615) -------------------------------------------------

    /// <summary>The bucket's floor, in its own frame (authored, from the arm's pin): its middle's height, and how far ahead of the pin it is.</summary>
    public const float BucketFloorY = -BucketHeight * 0.9f + 0.05f, BucketFloorZ = BucketReach * 0.5f;

    /// <summary>The lift and what is on the forks in one float of the pose: the lift (never past ±2) plus 4 a step of carrying.</summary>
    public static float PoseLift(float lift, int carrying) => lift + 4f * Mathf.Clamp(carrying, 0, 0x3FF);

    public static (float Lift, int Carrying) FromPoseLift(float y)
    {
        int carrying = Mathf.Clamp(Mathf.FloorToInt((y + 2f) / 4f), 0, 0x3FF);
        return (ClampLift(y - 4f * carrying), carrying);
    }

    /// <summary>
    /// What a parked one keeps, in the 32 bits of <c>VehicleState.Flags</c>: lift eight bits, tilt and
    /// the articulation seven each, and what is in the bucket ten (<c>Pallets.Carried</c>, #615: before
    /// it the three were eight bits each). Zero is "never set": the rest pose, straight, empty.
    /// </summary>
    public static int Pack(float lift, float tilt, float articulation, int carrying = 0) =>
        PackArm(lift, tilt, TiltMin, TiltMax, articulation, carrying);

    public static (float Lift, float Tilt, float Articulation, int Carrying) Unpack(int flags) =>
        flags == 0 ? (RestLift, RestTilt, 0f, 0) : UnpackArm(flags, TiltMin, TiltMax);
}
