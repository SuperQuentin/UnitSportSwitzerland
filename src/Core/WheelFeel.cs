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
/// <param name="Engine">The engine's shake through the column, 0..1 (<see cref="EngineFrom"/>); 0 with it off.</param>
/// <param name="EngineHz">Its rate, Hz: the crankshaft's turns per second.</param>
public readonly record struct WheelFeel(float Torque, float Road, float RoadHz, float Weight, float Engine = 0f, float EngineHz = 0f)
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
    /// floor) once rolling at <paramref name="speed"/> m/s, at most half the scale: off-road, not
    /// everywhere. It once added a tarmac texture growing with speed and reached 0.8 on a gravel
    /// track, a buzz drowning everything else. What keeps a smooth road alive is the engine.
    /// </summary>
    public static (float Road, float Hz) RoadFrom(float roughness, float speed)
    {
        float v = Mathf.Abs(speed);
        float road = Mathf.Clamp(0.5f * roughness * Mathf.Clamp(v / 15f, 0f, 1f), 0f, 1f);
        // the lattice of bumps under a tyre is about half a metre apart
        return (road, Mathf.Clamp(v / 0.5f, 6f, 40f));
    }

    /// <summary>
    /// The engine through the column: a light shake at idle, a little more toward the redline, at
    /// the crankshaft's rate (rpm / 60), held to what a wheel's motor can draw (8..60 Hz).
    /// </summary>
    public static (float Engine, float Hz) EngineFrom(float rpm, float rpm01) =>
        (0.15f + 0.35f * rpm01 * rpm01, Mathf.Clamp(rpm / 60f, 8f, 60f));

    /// <summary>Static steering weight: <paramref name="parked"/> at a standstill, gone by 6 m/s.</summary>
    public static float WeightFrom(float parked, float speed) => parked * (1f - Mathf.Clamp(Mathf.Abs(speed) / 6f, 0f, 1f));
}
