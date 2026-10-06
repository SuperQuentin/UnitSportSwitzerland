using Godot;

namespace UnitSport.Avatar;

/// <summary>How a telehandler's rear wheels follow its front ones (#614).</summary>
public enum SteerMode
{
    /// <summary>Only the front wheels steer, as a car's do: on the road.</summary>
    Front = 0,
    /// <summary>The rear wheels steer opposite the front ones: half the circle, round a tight site.</summary>
    FourWheel = 1,
    /// <summary>The rear wheels steer with the front ones: it moves diagonally without turning, up to a wall.</summary>
    Crab = 2,
}

/// <summary>
/// The telehandler (#614): one set of numbers for the drawn model, its hull, the steering and the
/// telescopic boom, after a 13 m machine of the common class (Manitou MT 1335, JCB 535-125: a 2.9 m
/// wheelbase, 24-inch tyres, a boom from the back of the chassis to about 9 m out and 10 m up, a
/// self-levelling fork carriage). AUTHORED frame like every avatar mesh: +Z forward (the forks'
/// way), +X the left side, origin on the ground in the middle of the wheelbase. The cab is on the
/// left, the boom along the middle, the engine on the right. Metres and radians.
///
/// <para>Godot maths only, in a file of its own, so the unit tests link it.</para>
/// </summary>
public static class TelehandlerLayout
{
    /// <summary>The axles either side of the middle, the wheels, half the track.</summary>
    public const float FrontAxle = 1.45f, RearAxle = -1.45f, WheelRadius = 0.62f, WheelWidth = 0.45f, HalfTrack = 0.95f;
    public const float Wheelbase = FrontAxle - RearAxle;
    /// <summary>The chassis: half its width, its nose and its tail (the counterweight's face), its top.</summary>
    public const float ChassisHalf = 1.1f, Nose = 2.05f, Tail = -2.3f, ChassisTop = 1.2f;
    /// <summary>The cab on the left: its walls (authored x, + left), front, back, floor and roof.</summary>
    public const float CabLeft = 1.12f, CabRight = 0.32f, CabFront = 1.0f, CabBack = -0.9f, CabFloor = 0.95f, CabTop = 2.45f;

    /// <summary>The boom's pivot at the back of the chassis, authored, its retracted length to the carriage's pin, and how far it extends.</summary>
    public static readonly Vector3 BoomPivot = new(-0.25f, 1.55f, -1.6f);
    public const float BoomLength = 4.7f, ExtendMax = 4.3f;
    /// <summary>The boom's travel from the horizontal (down puts the forks on the ground in front), and the forks' pitch from level (+ tilted back).</summary>
    public const float LiftMin = -0.22f, LiftMax = 1.15f, TiltMin = -0.6f, TiltMax = 0.35f;
    /// <summary>How fast they move at full lever: rad/s, m/s, rad/s.</summary>
    public const float LiftRate = 0.22f, ExtendRate = 0.6f, TiltRate = 0.5f;
    /// <summary>Parked and driven: boom down, retracted, forks a touch back.</summary>
    public const float RestLift = LiftMin, RestExtend = 0f, RestTilt = 0.05f;
    /// <summary>The forks: how far they reach out from the carriage's pin, and below it.</summary>
    public const float ForkLength = 1.2f, ForkDrop = 0.55f;

    /// <summary>The wheels' lock either way, rad, and how fast they turn to it, rad/s.</summary>
    public const float MaxSteer = 0.6f, SteerRate = 0.9f;
    /// <summary>Top speed either way, m/s (25 km/h, and slower backwards), acceleration and braking.</summary>
    public const float TopSpeed = 6.9f, TopReverse = 4f, Accel = 1.4f, BrakeDecel = 3.5f;

    public static float ClampLift(float a) => Mathf.Clamp(a, LiftMin, LiftMax);
    public static float ClampExtend(float e) => Mathf.Clamp(e, 0f, ExtendMax);
    public static float ClampTilt(float a) => Mathf.Clamp(a, TiltMin, TiltMax);

    /// <summary>The rear wheels' angle for the front's in a mode.</summary>
    public static float RearSteer(float front, SteerMode mode) => mode switch
    {
        SteerMode.FourWheel => -front,
        SteerMode.Crab => front,
        _ => 0f,
    };

    /// <summary>
    /// The yaw rate (+ left) and the travel's angle from the heading (+ left, the motion's slip) of
    /// the middle of the wheelbase, at a speed and a front wheel angle (+ left), for a mode: a
    /// bicycle with both axles steered. Front steering turns about a point level with the rear axle;
    /// four-wheel about one level with the middle, on half the circle; crab does not turn at all and
    /// travels at the wheels' angle.
    /// </summary>
    public static (float YawRate, float Slip) Motion(float speed, float front, SteerMode mode)
    {
        float tf = Mathf.Tan(front), tr = Mathf.Tan(RearSteer(front, mode));
        float yawRate = speed * (tf - tr) / Wheelbase;
        // the middle's sideways velocity, over its forward one: the mean of the two axles' slopes
        float slip = Mathf.Atan((tf + tr) * 0.5f);
        return (yawRate, slip);
    }

    /// <summary>The middle's turning circle at full lock in a mode, m (infinite in crab).</summary>
    public static float TurnRadius(SteerMode mode)
    {
        var (yaw, _) = Motion(1f, MaxSteer, mode);
        return Mathf.Abs(yaw) < 1e-5f ? float.PositiveInfinity : 1f / Mathf.Abs(yaw);
    }

    /// <summary>The carriage's pin, forward of the middle and up from the ground, for a lift and an extension.</summary>
    public static Vector2 Carriage(float lift, float extend)
    {
        float len = BoomLength + extend;
        return new Vector2(BoomPivot.Z + Mathf.Cos(lift) * len, BoomPivot.Y + Mathf.Sin(lift) * len);
    }

    /// <summary>
    /// What a parked one keeps, in <c>VehicleState.Flags</c>: lift and extension eight bits each,
    /// the tilt six, the steering mode two. Zero is "never set": the rest pose, front steering.
    /// </summary>
    public static int Pack(float lift, float extend, float tilt, SteerMode mode)
    {
        static int Q(float v, float lo, float hi, int top) => Mathf.Clamp(Mathf.RoundToInt((v - lo) / (hi - lo) * top), 0, top) + 1;
        return Q(lift, LiftMin, LiftMax, 254) | Q(extend, 0f, ExtendMax, 254) << 8 | Q(tilt, TiltMin, TiltMax, 62) << 16 | (int)mode << 22;
    }

    public static (float Lift, float Extend, float Tilt, SteerMode Mode) Unpack(int flags)
    {
        if (flags == 0) return (RestLift, RestExtend, RestTilt, SteerMode.Front);
        static float U(int q, float lo, float hi, int top) => lo + (Mathf.Clamp(q, 1, top + 1) - 1) / (float)top * (hi - lo);
        int mode = flags >> 22 & 3;
        return (U(flags & 0xFF, LiftMin, LiftMax, 254), U(flags >> 8 & 0xFF, 0f, ExtendMax, 254),
            U(flags >> 16 & 0x3F, TiltMin, TiltMax, 62), (SteerMode)Mathf.Clamp(mode, 0, 2));
    }

    /// <summary>The front wheels' angle and the mode in one float of the pose: the angle (never past ±1) plus 4 a mode.</summary>
    public static float PoseSteer(float front, SteerMode mode) => front + 4f * (int)mode;

    public static (float Front, SteerMode Mode) FromPoseSteer(float x)
    {
        int mode = Mathf.Clamp(Mathf.RoundToInt(x / 4f), 0, 2);
        return (Mathf.Clamp(x - 4f * mode, -MaxSteer, MaxSteer), (SteerMode)mode);
    }

    /// <summary>The tilt's steps on the wire, over its travel.</summary>
    public const int TiltSteps = 63;

    /// <summary>The extension and the tilt in one float of the pose: the extension (never past 8 m) plus 8 a step of tilt.</summary>
    public static float PoseExtend(float extend, float tilt)
    {
        int step = Mathf.Clamp(Mathf.RoundToInt((tilt - TiltMin) / (TiltMax - TiltMin) * TiltSteps), 0, TiltSteps);
        return extend + 8f * step;
    }

    public static (float Extend, float Tilt) FromPoseExtend(float z)
    {
        int step = Mathf.Clamp(Mathf.FloorToInt((z + 0.5f) / 8f), 0, TiltSteps);
        return (ClampExtend(z - 8f * step), TiltMin + step / (float)TiltSteps * (TiltMax - TiltMin));
    }
}
