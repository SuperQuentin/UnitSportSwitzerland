using Godot;

namespace UnitSport.Core;

/// <summary>
/// What a vehicle's steering wheel feels, for force feedback (issue #68). Worked out by the
/// vehicle model each step (<c>Rideable.Feel</c>) and turned into forces by <see cref="SteeringWheel"/>.
/// </summary>
/// <param name="Torque">
/// Self-aligning torque at the wheel, −1..1 of what the front tyres give at their peak on tarmac;
/// + pulls the wheel right. It comes from the front axle's side force times a trail that collapses
/// past the peak slip angle (<see cref="Aligning"/>), so the wheel goes light when the fronts let go.
/// </param>
/// <param name="Road">How hard the road shakes the wheel, 0..1: the surface's roughness at this speed.</param>
/// <param name="RoadHz">How fast it shakes, Hz.</param>
/// <param name="Weight">
/// How stiff the wheel is to turn on the spot, 0..1: tyres scrubbing at a standstill, heavy without
/// power assist, gone once rolling.
/// </param>
public readonly record struct WheelFeel(float Torque, float Road, float RoadHz, float Weight)
{
    /// <summary>
    /// Pneumatic trail at zero slip, m, and the slip angle by which it is gone (the tyre's contact
    /// patch sliding); the caster's mechanical trail stays, so even a drifting car's wheel centres.
    /// </summary>
    public const float PneumaticTrail = 0.035f, CasterTrail = 0.02f, SlideAlpha = 0.3f;

    /// <summary>
    /// Self-aligning torque as a share of the peak: <paramref name="fy"/> the steered axle's side
    /// force, N, + left (the bicycle models' sign); <paramref name="alpha"/> its slip angle, rad;
    /// <paramref name="peak"/> the most side force that axle gives on tarmac, N. Rolling below
    /// <paramref name="speed"/> 3 m/s it fades out: a parked tyre scrubs rather than self-aligns.
    /// </summary>
    public static float Aligning(float fy, float alpha, float peak, float speed)
    {
        float trail = PneumaticTrail * Mathf.Max(0f, 1f - Mathf.Abs(alpha) / SlideAlpha) + CasterTrail;
        // a front tyre pushing left (fy > 0) is steered left of its travel: it pulls the wheel back right
        float torque = fy * trail / (Mathf.Max(peak, 1f) * (PneumaticTrail + CasterTrail));
        return Mathf.Clamp(torque * Mathf.Clamp(Mathf.Abs(speed) / 3f, 0f, 1f), -1f, 1f);
    }

    /// <summary>
    /// The road through the rim: <paramref name="roughness"/> of the surface (0 tarmac .. 1 forest
    /// floor) at <paramref name="speed"/> m/s, plus a little tarmac texture so a road is never dead.
    /// </summary>
    public static (float Road, float Hz) RoadFrom(float roughness, float speed)
    {
        float v = Mathf.Abs(speed);
        float road = Mathf.Clamp(0.04f * Mathf.Clamp(v / 10f, 0f, 1f) + roughness * Mathf.Clamp(v / 15f, 0f, 1.2f), 0f, 1f);
        // the lattice of bumps under a tyre is about half a metre apart
        return (road, Mathf.Clamp(v / 0.5f, 6f, 40f));
    }

    /// <summary>Static steering weight: <paramref name="parked"/> at a standstill, gone by 6 m/s.</summary>
    public static float WeightFrom(float parked, float speed) => parked * (1f - Mathf.Clamp(Mathf.Abs(speed) / 6f, 0f, 1f));
}
