using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A road bike, ridden on the real power equation rather than an arcade speed cap.
///
/// <para>
/// <c>m·a = P/v − ½ρ·CdA·v² − Crr·m·g·cosθ − m·g·sinθ</c>. Every term is a measured cycling
/// quantity, so the numbers that come out are the numbers a rider would recognise: 180 W holds
/// 32 km/h on the flat, the same 180 W climbs an 8% ramp at 10 km/h, and freewheeling down that
/// ramp settles at 62 km/h. None of those were tuned — they fall out of the equation, which is
/// the reason for using it.
/// </para>
///
/// <para>
/// It is also the reason the input is <see cref="RiderWatts"/> and not "how fast the bike goes".
/// A home trainer measures watts; a cadence sensor measures rpm. When RideLink is connected, the
/// number it reports drops straight into this model and the Swiss road under the wheels supplies
/// the <c>sinθ</c> — which is the whole point of putting a bike in a terrain simulator.
/// </para>
/// </summary>
public sealed class Bicycle : Rideable
{
    public override RideKind Kind => RideKind.RoadBike;
    public override string Label => "Road bike";
    public override string Blurb => "W pedal, Shift sprint, S brake, A/D steer — climbs cost you";

    // ---- the rider and the machine ----
    /// <summary>Rider, bike, bottles and kit.</summary>
    private const float Mass = 82f;

    /// <summary>Drag area on the hoods, m². A tucked pro is nearer 0.24; this is a fit amateur.</summary>
    private const float DragArea = 0.32f;
    private const float AirDensity = 1.20f;      // ~15 °C at 500 m; Swiss valley floor
    private const float RollingResistance = 0.005f;   // good tyres on tarmac

    /// <summary>Steady effort, W. Roughly a fit rider's endurance power.</summary>
    public float RiderWatts { get; set; } = 180f;

    /// <summary>Shift. Not sustainable, but nothing here is asking it to be.</summary>
    public float SprintWatts { get; set; } = 520f;

    /// <summary>
    /// Cap on pedal thrust, N. <c>P/v</c> diverges at a standstill, and unclamped it would
    /// launch a stationary bike at 40 m/s². A rider can put roughly their own body weight
    /// through the pedals out of the saddle, which is what this is.
    /// </summary>
    private const float MaxThrust = 260f;

    private const float BrakeDecel = 5.2f;       // dry tarmac, both brakes, short of a stoppie

    // ---- steering ----
    /// <summary>Maximum lean, radians. Past ~35° a road tyre lets go.</summary>
    private const float MaxLean = 0.60f;

    /// <summary>
    /// Cap on yaw rate. The physical limit <c>ω = g·tanφ/v</c> goes to infinity as the bike
    /// slows, which is true of a real bike — you can turn it on the spot at walking pace — but
    /// left uncapped a mouse-flick at 1 m/s spins the rider like a top.
    /// </summary>
    private const float MaxYawRate = 1.5f;

    /// <summary>Below this the bike is being wheeled, not ridden, and steering is direct.</summary>
    private const float WalkingPace = 1.2f;

    public override float EyeHeight => 1.48f;
    public override float ChaseDistance => 3.9f;
    public override float ChaseHeight => 1.55f;
    public override float FovSpeed => 16f;

    private float _cadence;

    public override Node3D BuildVisual(int riderIndex) => Cyclist.Create(riderIndex);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        float v = motion.Speed;

        // --- steering ---------------------------------------------------------------
        // A bicycle changes direction by leaning, so the turn radius grows with speed: the
        // same handlebar input that flicks you round a bollard at 5 km/h is a long sweeping
        // bend at 50. Modelling it the other way — a fixed turn rate — is what makes vehicles
        // in games feel like they are on rails.
        float yawRate = 0f;
        if (Mathf.Abs(input.Steer) > 0.01f)
        {
            float limit = v > WalkingPace
                ? Mathf.Min(Gravity * Mathf.Tan(MaxLean) / v, MaxYawRate)
                : MaxYawRate;
            yawRate = -input.Steer * limit;   // +yaw is left in Godot, +steer is right
        }
        motion.Yaw += yawRate * dt;
        motion.Lean = LeanFor(v, yawRate, MaxLean);

        if (!ground.OnFloor)
        {
            // airborne off a lip: no thrust, no rolling resistance, just air
            v -= 0.5f * AirDensity * DragArea * v * v / Mass * dt;
            motion.Speed = Mathf.Max(0f, v);
            return;
        }

        // --- the power equation -----------------------------------------------------
        float watts = input.Throttle * (input.Effort ? SprintWatts : RiderWatts);
        float thrust = watts > 0 ? Mathf.Min(watts / Mathf.Max(v, 0.5f), MaxThrust) : 0f;

        float drag = 0.5f * AirDensity * DragArea * v * v;
        float rolling = RollingResistance * Mass * Gravity;

        float accel = (thrust - drag - rolling) / Mass + SlopeAccel(ground.Grade);
        v += accel * dt;

        if (input.Brake > 0.01f) v -= input.Brake * BrakeDecel * dt;

        // A bicycle does not roll backwards down a hill; it stops and you put a foot down.
        // Letting the speed go negative would drive the whole model backwards through itself.
        motion.Speed = Mathf.Max(0f, v);

        // Cadence for the crank animation, from a notional 6.2 m development — about a 50×17,
        // the gear you would actually be in at cruising speed. The 112 rpm ceiling stands in
        // for shifting up rather than modelling a cassette; without it a descent has the rider
        // spinning at 300 rpm. Off the pedals the cranks stop, because a road bike freewheels.
        _cadence = watts > 0 ? Mathf.Clamp(motion.Speed * 60f / 6.2f, 40f, 112f) : 0f;
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is Cyclist cyclist) cyclist.CadenceRpm = _cadence;
    }
}
