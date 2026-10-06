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
        speed * Mathf.Sin(articulation) / (FrontAxle + -RearAxle * Mathf.Cos(articulation));

    /// <summary>The rear axle's turning circle at an articulation, m (infinite going straight).</summary>
    public static float TurnRadius(float articulation) =>
        Mathf.Abs(articulation) < 1e-4f ? float.PositiveInfinity
            : (FrontAxle + -RearAxle * Mathf.Cos(articulation)) / Mathf.Abs(Mathf.Sin(articulation));

    /// <summary>The bucket pin's height over the ground at a lift.</summary>
    public static float PinHeight(float lift) => ArmPivot.Y + Mathf.Sin(lift) * ArmLength;

    /// <summary>
    /// What a parked one keeps, in the 32 bits of <c>VehicleState.Flags</c>: lift, tilt and the
    /// articulation, eight bits each over their travel. Zero is "never set": the rest pose, straight.
    /// </summary>
    public static int Pack(float lift, float tilt, float articulation)
    {
        static int Q(float v, float lo, float hi) => Mathf.Clamp(Mathf.RoundToInt((v - lo) / (hi - lo) * 254f), 0, 254) + 1;
        return Q(lift, LiftMin, LiftMax) | Q(tilt, TiltMin, TiltMax) << 8 | Q(articulation, -MaxArticulation, MaxArticulation) << 16;
    }

    public static (float Lift, float Tilt, float Articulation) Unpack(int flags)
    {
        if (flags == 0) return (RestLift, RestTilt, 0f);
        static float U(int q, float lo, float hi) => lo + (Mathf.Clamp(q, 1, 255) - 1) / 254f * (hi - lo);
        return (U(flags & 0xFF, LiftMin, LiftMax), U(flags >> 8 & 0xFF, TiltMin, TiltMax),
            U(flags >> 16 & 0xFF, -MaxArticulation, MaxArticulation));
    }
}
