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
    public override string Blurb => "Gravity only — A/D carve to shed speed, Shift tuck, S plough";

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

    /// <summary>Skis turn far more readily than a bicycle — no gyroscopic wheel to fight.</summary>
    private const float MaxLean = 0.75f;
    private const float MaxYawRate = 2.0f;

    /// <summary>
    /// Poling and skating, W. Skis on flat ground are close to useless, which is honest but
    /// would strand the player, so W gives you the shuffle a real skier resorts to.
    /// </summary>
    private const float PoleWatts = 110f;
    private const float PoleSpeedLimit = 6.0f;   // you cannot skate faster than this

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
        float yawRate = 0f;
        if (Mathf.Abs(input.Steer) > 0.01f)
        {
            // Same lean-limited turn as the bike, but skis hold an edge rather than balancing
            // on a contact patch, so the ceiling is higher and it stays usable when slow.
            float limit = v > 1.5f
                ? Mathf.Min(Gravity * Mathf.Tan(MaxLean) / v, MaxYawRate)
                : MaxYawRate;
            yawRate = -input.Steer * limit;
        }
        motion.Yaw += yawRate * dt;
        motion.Lean = LeanFor(v, yawRate, MaxLean);

        float dragArea = input.Effort ? TuckDragArea : DragArea;

        if (!ground.OnFloor)
        {
            v -= 0.5f * AirDensity * dragArea * v * v / Mass * dt;
            motion.Speed = Mathf.Max(0f, v);
            return;
        }

        float drag = 0.5f * AirDensity * dragArea * v * v;
        float friction = SnowFriction * Mass * Gravity;

        // poling: capped hard, and it does nothing once gravity is already doing the work
        float thrust = 0f;
        if (input.Throttle > 0.01f && v < PoleSpeedLimit)
            thrust = Mathf.Min(PoleWatts / Mathf.Max(v, 0.6f), 150f) * input.Throttle;

        float accel = (thrust - drag - friction) / Mass + SlopeAccel(ground.Grade);
        v += accel * dt;

        // the edges: this is the brake, and the reason a run is a series of turns
        v -= Mathf.Abs(input.Steer) * EdgeScrub * v * dt;

        if (input.Brake > 0.01f) v -= input.Brake * PloughDecel * dt;

        motion.Speed = Mathf.Max(0f, v);
    }
}
