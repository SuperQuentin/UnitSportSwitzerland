using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// Alpine skis: no engine, no pedals, just gravity and how hard you are willing to turn.
///
/// <para>
/// Included because the terrain is 60% mountain and a bicycle only reaches the parts with roads.
/// It is also what proves the <see cref="Rideable"/> abstraction is not bicycle-shaped: it shares
/// the mass, drag and slope model and differs in exactly the two places a ski differs — nothing
/// drives it forward, and turning costs speed instead of being free.
/// </para>
///
/// <para>
/// That second point is the whole of skiing. Pointed straight down a 30% face you reach 80 km/h;
/// the only way to arrive at the bottom slower than that is to turn across the fall line and let
/// the edges scrub the speed off. So <see cref="EdgeScrub"/> is not a penalty bolted on to stop
/// the player going too fast — it is the brake, and steering is how you use it.
/// </para>
/// </summary>
public sealed class Skis : Rideable
{
    public override RideKind Kind => RideKind.Skis;
    public override string Label => "Skis";
    public override string Blurb => "Gravity only — {move_left}/{move_right} carve to shed speed, {tuck_boost} tuck, {brake} plough";

    private const float Mass = 82f;

    /// <summary>Upright, and tucked with Shift. The tuck is worth about 25 km/h on a long descent.</summary>
    private const float DragArea = 0.62f;
    private const float TuckDragArea = 0.34f;
    private const float AirDensity = 1.05f;      // thinner: this is 2,000 m, not the valley floor

    /// <summary>Kinetic friction of a waxed base on snow. Genuinely this low.</summary>
    private const float SnowFriction = 0.055f;

    /// <summary>Snowplough. Less than a brake disc, and it fades as you speed up — as it does.</summary>
    private const float PloughDecel = 4.0f;

    /// <summary>
    /// Speed lost per second at full lock, per m/s of travel. Skidding a turn at 20 m/s sheds
    /// far more than at 5, which is why a hard traverse is how you control a steep pitch.
    /// </summary>
    private const float EdgeScrub = 0.30f;

    /// <summary>
    /// Skis turn far more readily than a bicycle — no gyroscopic wheel to fight. ~57°: a World
    /// Cup giant-slalom skier is past 60°, and at the old 43° the tightest turn at 70 km/h was
    /// a 45 m arc, the radius of a lazy cruise rather than a carve.
    /// </summary>
    private const float MaxLean = 1.0f;
    private const float MaxYawRate = 2.4f;

    /// <summary>Quicker than the bike: there is no machine to tip, only knees and hips.</summary>
    private const float BankResponse = 7f;

    /// <summary>Below this you are shuffling and turn the skis directly.</summary>
    private const float SlowSpeed = 1.5f;

    /// <summary>
    /// Poling and skating, W. Skis on flat ground are close to useless, which is honest but
    /// would strand the player, so W gives you the shuffle a real skier resorts to.
    /// </summary>
    private const float PoleWatts = 110f;
    private const float PoleSpeedLimit = 6.0f;   // you cannot skate faster than this

    // ---- the Game profile (Rideable.Arcade) ----
    /// <summary>
    /// Deeper edges, a quicker roll, and a carve that bites instead of skidding: turning still
    /// brakes — that is still the whole of skiing — but a clean arc no longer bleeds a run dry.
    /// Faster skating too, so flats and uphills are a shuffle rather than a strand.
    /// </summary>
    private const float ArcadeMaxLean = 1.12f, ArcadeBankResponse = 9f, ArcadeMaxYawRate = 2.7f;
    private const float ArcadeEdgeScrub = 0.16f, ArcadeSnowFriction = 0.04f;
    private const float ArcadePoleWatts = 230f, ArcadePoleSpeedLimit = 8.5f;

    /// <summary>The figure's own eye, so first person sits where the drawn head is looking from.</summary>
    public override Vector3 FirstPersonEye { get; } = HumanMeshBuilder.MountsForPose(HumanPose.Tucked).Eye
        + new Vector3(0, 0, -0.06f);   // just proud of the face, so the head never fills the lens

    public override float EyeHeight => 1.32f;
    public override float ChaseDistance => 4.2f;
    public override float ChaseHeight => 1.60f;
    public override float MaxFov => 104f;        // steeper FOV ramp: the speed is the point
    public override float FovSpeed => 22f;
    public override float DismountSpeed => 3.0f;

    public override Node3D BuildVisual(int riderIndex) => new MeshInstance3D
    {
        Name = "Skier",
        Mesh = SkierMeshBuilder.BuildSkier(
            HumanPalette.ForRider(riderIndex), SkiPalette.ForRider(riderIndex)),
        MaterialOverride = HumanMeshBuilder.Material(),
    };

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        float v = motion.Speed;

        // --- steering -----------------------------------------------------------------
        // Same lean-driven turn as the bike, but skis hold an edge rather than balancing on a
        // contact patch, so the ceiling is higher, the roll-in quicker, and it stays usable slow.
        bool arcade = Arcade;
        float maxLean = arcade ? ArcadeMaxLean : MaxLean;
        SteerByLean(ref motion, input.Steer, v, maxLean, arcade ? ArcadeMaxYawRate : MaxYawRate,
            arcade ? ArcadeBankResponse : BankResponse, SlowSpeed, dt);

        float dragArea = input.Effort ? TuckDragArea : DragArea;

        if (!ground.OnFloor)
        {
            v -= 0.5f * AirDensity * dragArea * v * v / Mass * dt;
            motion.Speed = Mathf.Max(0f, v);
            return;
        }

        float drag = 0.5f * AirDensity * dragArea * v * v;
        float friction = (arcade ? ArcadeSnowFriction : SnowFriction) * Mass * Gravity;

        // poling: capped hard, and it does nothing once gravity is already doing the work
        float thrust = 0f;
        if (input.Throttle > 0.01f && v < (arcade ? ArcadePoleSpeedLimit : PoleSpeedLimit))
            thrust = Mathf.Min((arcade ? ArcadePoleWatts : PoleWatts) / Mathf.Max(v, 0.6f), 150f) * input.Throttle;

        float accel = (thrust - drag - friction) / Mass + SlopeAccel(ground.Grade);
        v += accel * dt;

        // The edges: this is the brake, and the reason a run is a series of turns. Scaled by the
        // SQUARE of how far over you are: a clean carve at moderate edge angle holds its speed,
        // as it does on snow, and only cranking it right over skids and scrubs. Linear in the
        // input, every small correction cost speed, so steering at all felt like braking.
        float edge = motion.Bank / maxLean;
        v -= edge * edge * (arcade ? ArcadeEdgeScrub : EdgeScrub) * v * dt;

        if (input.Brake > 0.01f) v -= input.Brake * PloughDecel * dt;

        motion.Speed = Mathf.Max(0f, v);
    }
}
