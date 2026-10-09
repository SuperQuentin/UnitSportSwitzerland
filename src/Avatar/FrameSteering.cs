using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The kinematics of a machine that steers by bending in the middle (#612, #614): the wheel loader,
/// the tandem roller. The front frame swings about a hinge between the axles and the rear frame
/// follows, so the vehicle's heading is the rear frame's and it turns at
/// <c>v·sin φ / (Lf + Lr·cos φ)</c>. Godot maths only, so the unit tests link it.
/// </summary>
public static class FrameSteering
{
    /// <summary>
    /// The rear frame's yaw rate at a speed and an articulation (+ the front frame swung left),
    /// with the front axle <paramref name="front"/> ahead of the hinge and the rear one
    /// <paramref name="behind"/> behind it (both positive).
    /// </summary>
    public static float YawRate(float speed, float articulation, float front, float behind) =>
        speed * Mathf.Sin(articulation) / (front + behind * Mathf.Cos(articulation));

    /// <summary>The rear axle's turning circle at an articulation, m (infinite going straight).</summary>
    public static float TurnRadius(float articulation, float front, float behind) =>
        Mathf.Abs(articulation) < 1e-4f ? float.PositiveInfinity
            : (front + behind * Mathf.Cos(articulation)) / Mathf.Abs(Mathf.Sin(articulation));
}
