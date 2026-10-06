using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The compact tandem roller (#614): one set of numbers for the drawn model, its hull, the frame
/// steering and the vibration, after a 2.6 t machine of the common class (Hamm HD 12, Bomag BW 120:
/// two 1.2 m steel drums 0.72 m across, a 1.7 m wheelbase, 10 km/h, drums vibrating at 55 Hz).
/// AUTHORED frame like every avatar mesh: +Z forward, +X the left side, origin on the ground under
/// the articulation hinge. The operator sits over the hinge on the rear frame, the engine behind
/// them; the front frame carries the water tank and swings about the hinge to steer.
///
/// <para>Godot maths only, in a file of its own, so the unit tests link it.</para>
/// </summary>
public static class CompactRollerLayout
{
    /// <summary>The drums: where they roll, ahead of and behind the hinge, their radius and half width.</summary>
    public const float FrontAxle = 0.85f, RearAxle = -0.85f, DrumRadius = 0.36f, DrumHalf = 0.6f;
    /// <summary>The frames: half their width, the nose and the tail.</summary>
    public const float FrameHalf = 0.64f, Nose = 1.32f, Tail = -1.32f;
    /// <summary>The engine hood behind the seat, and the water tank on the front frame: their tops.</summary>
    public const float HoodTop = 1.08f, TankTop = 1.0f;
    /// <summary>The operator's platform over the hinge, and the roll bar's top behind the seat.</summary>
    public const float Floor = 0.95f, RopsTop = 2.55f, RopsZ = -0.62f;

    /// <summary>The articulation's travel either way and how fast the frame swings, rad, rad/s.</summary>
    public const float MaxArticulation = 0.5f, ArticulationRate = 0.7f;
    /// <summary>Top speed either way, m/s (10 km/h), its hydrostatic drive's acceleration and braking.</summary>
    public const float TopSpeed = 2.8f, TopReverse = 2.8f, Accel = 0.9f, BrakeDecel = 2.5f;

    /// <summary>The drums' vibration, Hz, and how long it takes to come up or die away, s.</summary>
    public const float VibrationHz = 55f, VibrationRamp = 0.6f;
    /// <summary>The driver's view at full vibration, rad either way: a tremble, not a wobble.</summary>
    public const float ShakeAmplitude = 0.0035f;

    /// <summary>The rear frame's yaw rate at a speed and an articulation (+ the front swung left).</summary>
    public static float YawRate(float speed, float articulation) =>
        FrameSteering.YawRate(speed, articulation, FrontAxle, -RearAxle);

    /// <summary>The rear drum's turning circle at an articulation, m.</summary>
    public static float TurnRadius(float articulation) => FrameSteering.TurnRadius(articulation, FrontAxle, -RearAxle);

    /// <summary>
    /// What a parked one keeps, in <c>VehicleState.Flags</c>: the articulation, eight bits over its
    /// travel. Zero is "never set": straight. Its vibration is not kept: a parked roller is off.
    /// </summary>
    public static int Pack(float articulation) =>
        Mathf.Clamp(Mathf.RoundToInt((articulation + MaxArticulation) / (2f * MaxArticulation) * 254f), 0, 254) + 1;

    public static float Unpack(int flags) =>
        flags == 0 ? 0f : -MaxArticulation + (Mathf.Clamp(flags & 0xFF, 1, 255) - 1) / 254f * 2f * MaxArticulation;
}
