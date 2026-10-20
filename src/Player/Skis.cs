using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// Alpine skis: no engine, no pedals, just gravity, the edges and the snow.
///
/// <para>
/// The force model is <see cref="SkiDynamics"/> (#899, docs/plans/ski-physics.md): the skis keep a
/// velocity of their own, so a turn either carves (the edges turn the speed into the new line at
/// no cost) or skids (the sideways speed is scraped off: the brake, and the reason a run is a
/// series of turns). Gravity pulls down the fall line, so flat skis on a traverse slip down it.
/// Brake alone is the snowplough; brake with a turn pivots the skis across the travel for a
/// hockey stop. Off snow they barely slide, and forward is a stride per press, never a hold:
/// skating on a flat run, small steps on grass or tarmac.
/// </para>
/// </summary>
public sealed class Skis : SnowRide
{
    public override RideKind Kind => RideKind.Skis;
    public override string Label => "Skis";
    public override string Blurb =>
        "Gravity: {move_left}/{move_right} carve, {brake} snowplough, fast: {brake} + turn to skid to a stop, tap {move_forward} to skate, hold {tuck_boost} to schuss";

    /// <summary>
    /// How far the skis tip onto their edges. ~57 degrees: a World Cup giant-slalom skier is past
    /// 60, and at 43 the tightest turn at 70 km/h was a 45 m arc.
    /// </summary>
    private const float MaxLean = 1.0f;
    /// <summary>
    /// The quickest the skis come round, rad/s (#933: 2.4 felt like a snap). 1.5 is a short swing
    /// at 6 m/s on a 4 m arc; faster, the lean sets a longer one.
    /// </summary>
    private const float MaxYawRate = 1.5f;

    /// <summary>The roll onto the edges, 1/s: knees and hips, a quarter second to settle (#933: 7 was a flick).</summary>
    private const float BankResponse = 4.5f;

    /// <summary>Below this you are shuffling and turn the skis directly.</summary>
    private const float SlowSpeed = 1.5f;

    /// <summary>A hockey stop: brake with a turn swings the skis across at this rate, whatever the speed.</summary>
    private const float PivotRate = 3f;

    /// <summary>Above this a brake with a turn is a hockey stop; below, a snowplough turn, m/s.</summary>
    private const float PivotSpeed = 5f;

    /// <summary>The schuss (Shift, #933): the skis flat and straight, steering only nudges them.</summary>
    private const float SchussSteer = 0.3f;

    // ---- the Game profile (Rideable.Arcade): deeper edges, a quicker roll ----
    private const float ArcadeMaxLean = 1.12f, ArcadeBankResponse = 5.5f, ArcadeMaxYawRate = 1.75f;

    public override float EyeHeight => 1.32f;
    public override float ChaseDistance => 5.4f;
    public override float ChaseHeight => 1.85f;
    public override float MaxFov => 104f;        // steeper FOV ramp: the speed is the point
    public override float FovSpeed => 22f;
    public override float DismountSpeed => 3.0f;

    /// <summary>The live skier (#933): the knees, the plough, the skate, the poles move.</summary>
    public override Node3D BuildVisual(int riderIndex, Avatar.Outfit outfit = default) =>
        SnowRig.Skier(HumanPalette.ForRider(riderIndex) with { Outfit = outfit }, SkiPalette.ForRider(riderIndex));

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        bool arcade = Arcade;
        var tuning = arcade ? SkiDynamics.Tuning.Game : SkiDynamics.Tuning.Sim;
        float maxLean = arcade ? ArcadeMaxLean : MaxLean;

        // a hockey stop: brake with a turn throws the skis across the travel, not into a lean
        bool pivot = ground.OnFloor && input.Brake > 0.3f && Mathf.Abs(input.Steer) > 0.3f && motion.Speed > PivotSpeed;
        // the schuss holds the line: the skis flat, a steer only nudges them
        float steer = input.Effort && ground.OnFloor ? input.Steer * SchussSteer : input.Steer;
        if (pivot)
        {
            float yawRate = -Mathf.Sign(input.Steer) * PivotRate;
            motion.Yaw += yawRate * dt;
            motion.YawRate = yawRate;
            motion.Bank = Mathf.MoveToward(motion.Bank, -Mathf.Sign(input.Steer) * maxLean, 4f * dt);
            motion.Lean = motion.Bank * 0.6f;
            // the heading turned, the velocity did not: it is that much further off the nose
            motion.Slip -= yawRate * dt;
        }
        else
        {
            float before = motion.Yaw;
            SteerByLean(ref motion, steer, motion.Speed, maxLean, arcade ? ArcadeMaxYawRate : MaxYawRate,
                arcade ? ArcadeBankResponse : BankResponse, SlowSpeed, dt);
            motion.Slip -= motion.Yaw - before;
        }

        float edge = pivot ? 1f : Mathf.Abs(motion.Bank) / maxLean;
        // brake alone, or slow with a turn, is the snowplough: the tails pushed out into a V
        float plough = pivot || !ground.OnFloor ? 0f : input.Brake;
        Glide(input, ground, tuning, edge, plough, 1f, dt, ref motion, plough);
    }
}
