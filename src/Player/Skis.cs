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
        "Gravity: {move_left}/{move_right} carve, {brake} plough, {brake} + turn to skid to a stop, tap {move_forward} to skate, {tuck_boost} tuck";

    /// <summary>
    /// How far the skis tip onto their edges. ~57 degrees: a World Cup giant-slalom skier is past
    /// 60, and at 43 the tightest turn at 70 km/h was a 45 m arc.
    /// </summary>
    private const float MaxLean = 1.0f;
    private const float MaxYawRate = 2.4f;

    /// <summary>Quicker than the bike: there is no machine to tip, only knees and hips.</summary>
    private const float BankResponse = 7f;

    /// <summary>Below this you are shuffling and turn the skis directly.</summary>
    private const float SlowSpeed = 1.5f;

    /// <summary>A hockey stop: brake with a turn swings the skis across at this rate, whatever the speed.</summary>
    private const float PivotRate = 3f;

    // ---- the Game profile (Rideable.Arcade): deeper edges, a quicker roll ----
    private const float ArcadeMaxLean = 1.12f, ArcadeBankResponse = 9f, ArcadeMaxYawRate = 2.7f;

    /// <summary>The figure's own eye, so first person sits where the drawn head is looking from.</summary>
    public override Vector3 FirstPersonEye { get; } = HumanMeshBuilder.MountsForPose(HumanPose.Tucked).Eye
        + new Vector3(0, 0, -0.06f);   // just proud of the face, so the head never fills the lens

    public override float EyeHeight => 1.32f;
    public override float ChaseDistance => 4.2f;
    public override float ChaseHeight => 1.60f;
    public override float MaxFov => 104f;        // steeper FOV ramp: the speed is the point
    public override float FovSpeed => 22f;
    public override float DismountSpeed => 3.0f;

    public override Node3D BuildVisual(int riderIndex, Avatar.Outfit outfit = default) => new MeshInstance3D
    {
        Name = "Skier",
        Mesh = SkierMeshBuilder.BuildSkier(
            HumanPalette.ForRider(riderIndex) with { Outfit = outfit }, SkiPalette.ForRider(riderIndex)),
        MaterialOverride = HumanMeshBuilder.FigureMaterial(),
    };

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        bool arcade = Arcade;
        var tuning = arcade ? SkiDynamics.Tuning.Game : SkiDynamics.Tuning.Sim;
        float maxLean = arcade ? ArcadeMaxLean : MaxLean;

        // a hockey stop: brake with a turn throws the skis across the travel, not into a lean
        bool pivot = ground.OnFloor && input.Brake > 0.3f && Mathf.Abs(input.Steer) > 0.3f && motion.Speed > 2f;
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
            SteerByLean(ref motion, input.Steer, motion.Speed, maxLean, arcade ? ArcadeMaxYawRate : MaxYawRate,
                arcade ? ArcadeBankResponse : BankResponse, SlowSpeed, dt);
            motion.Slip -= motion.Yaw - before;
        }

        float edge = pivot ? 1f : Mathf.Abs(motion.Bank) / maxLean;
        Glide(input, ground, tuning, edge, pivot ? 0f : input.Brake, 1f, dt, ref motion);
    }
}
