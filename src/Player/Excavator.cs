using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// A tracked excavator (#611, RideKind 194): the first crawler in the game. It drives on two
/// tracks that counter-rotate to turn it on the spot, and in <b>dig mode</b> its tracks hold while
/// the two sticks work the arm in the ISO pattern — slew and stick on the left, boom and bucket on
/// the right. See <c>docs/notes/vehicles/excavator.md</c>.
///
/// <para>
/// <b>The heading is the cab's.</b> The upper structure slews on the undercarriage, and the cab,
/// the driver, the cockpit and the camera all slew with it, so the vehicle's yaw
/// (<see cref="RideMotion.Yaw"/>) is the upper structure's and the tracks keep a heading of their
/// own (<see cref="TrackYaw"/>). It travels along its tracks, not its nose: <see cref="RideMotion.Slip"/>
/// is the angle between the two, which is exactly how a drifting car already tells the player
/// physics it is not going where it points. The drawn undercarriage turns back by the slew.
/// </para>
///
/// <para>
/// Shaped on the forklift (#583): the arm's state is four angles, eased by nothing (a hydraulic
/// arm moves while its lever is held and stops where it is let go), replicated whole in
/// <see cref="WritePose"/> for a copy and packed in <c>VehicleState.Flags</c> for a parked one.
/// </para>
///
/// <para>
/// <b>Two sizes</b> (#614): the 20 t crawler and the 2.7 t mini (RideKind 196), the same machine
/// over a different <see cref="ExcavatorSpec"/> and a different drawn body. The mini has a dozer
/// blade on its tracks, raised and lowered whether it drives or digs.
/// </para>
/// </summary>
public sealed class Excavator : Rideable, IEngined
{
    /// <summary>A 20 t crawler, or with <paramref name="mini"/> a 2.7 t mini with its blade.</summary>
    public Excavator(bool mini = false)
    {
        Mini = mini;
        Spec = mini ? MiniExcavatorLayout.Spec : ExcavatorLayout.Spec;
        Boom = Spec.RestBoom;
        Stick = Spec.RestStick;
        Bucket = Spec.RestBucket;
    }

    /// <summary>The 2.7 t mini (#614) rather than the 20 t machine.</summary>
    public bool Mini { get; }

    /// <summary>Its size: dimensions, the joints' travel and rates, the tracks.</summary>
    public ExcavatorSpec Spec { get; }

    public override RideKind Kind => Mini ? RideKind.MiniExcavator : RideKind.Excavator;
    public override string Label => Mini ? "Mini excavator" : "Excavator";
    public override string Blurb =>
        "{move_forward}{move_back} tracks, {move_left}{move_right} turn on the spot; {dig_mode} dig mode: "
        + "{arm_slew_left}{arm_slew_right} slew, {arm_stick_out}{arm_stick_in} stick, {arm_boom_up}{arm_boom_down} boom, {arm_bucket_curl}{arm_bucket_dump} bucket"
        + (Mini ? "; {blade_raise}{blade_lower} blade" : "");

    /// <summary>The tracks' heading in the world (the vehicle's yaw is the cab's).</summary>
    public float TrackYaw { get; set; }

    /// <summary>The upper structure's turn on the tracks, rad (+ left): the cab's yaw less the tracks'.</summary>
    public float Slew { get; set; }

    public float Boom { get; set; }
    public float Stick { get; set; }
    public float Bucket { get; set; }

    /// <summary>The dozer blade's arms, rad (0: its edge on the ground, + raised). Always 0 without one.</summary>
    public float Blade { get; set; }

    /// <summary>The blade's lever this frame, -1..1 (+ raises): it works driving or digging.</summary>
    public float BladeLever { get; set; }

    /// <summary>Dig mode: the tracks hold, the sticks work the arm.</summary>
    public bool Digging { get; set; }

    /// <summary>The arm's levers this frame, -1..1 each (set by the driver's input, or a check).</summary>
    public (float Slew, float Stick, float Boom, float Bucket) Levers { get; set; }

    private float _signed, _yawRate, _throttle, _work;
    private float _left, _right;

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => Mini ? 200f : 400f;
    public override float BodyRadius => Mini ? 0.75f : 1.3f;
    public override float BodyHeight => Mini ? 1.6f : 2.2f;
    public override float DismountSpeed => 0.8f;
    public override float ChaseDistance => Mini ? 7f : 12f;
    public override float ChaseHeight => Mini ? 3f : 5f;
    public override float ChasePitch => -0.3f;
    public override float BaseFov => 68f;
    public override float MaxFov => 72f;
    public override float FovSpeed => 4f;

    private static Vector3? _eye, _miniEye;
    public override Vector3 FirstPersonEye => Mini
        ? _miniEye ??= MiniExcavatorMeshBuilder.Parts().Cockpit!.Eye
        : _eye ??= ExcavatorMeshBuilder.Parts().Cockpit!.Eye;
    public override float EyeHeight => FirstPersonEye.Y;
    /// <summary>Climbed into from the left track, in front of the cab's door (beside the mini's open canopy).</summary>
    public override Vector3 EntryPoint => Flip(new Vector3(Spec.HalfGauge + Spec.ShoeWidth * 0.5f + 0.3f, 0f, Mini ? 0f : 0.6f));
    public override bool ExitLeft => true;
    public override bool DrivenFromInside => false;

    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    // ---- collision: the undercarriage low, the house high; never the arm ---------------------
    private static Aabb NodeBox(float x, float y0, float y1, float z0, float z1) =>
        new(new Vector3(-x, y0, -z1), new Vector3(x * 2f, y1 - y0, z1 - z0));

    /// <summary>
    /// A square under the slewing ring, which the tracks are near enough to whichever way the
    /// house is slewed, and the house above it. The arm is in neither: a hull out at the bucket
    /// would stop it digging into the very ground it is for.
    /// </summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes => Mini
        ? (NodeBox(0.75f, 0.15f, Spec.RingTop, -0.75f, 0.75f),
            NodeBox(Spec.HouseHalf, Spec.RingTop, Spec.HouseTop, Spec.HouseBack, Spec.HouseFront))
        : (NodeBox(1.6f, 0.3f, Spec.RingTop, -1.6f, 1.6f),
            NodeBox(Spec.HouseHalf, Spec.RingTop, Spec.HouseTop, Spec.HouseBack, Spec.HouseFront));

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var box = Mini
                ? NodeBox(Spec.HalfGauge + Spec.ShoeWidth * 0.5f, 0f, MiniExcavatorLayout.CabTop, Spec.HouseBack - 0.05f, Spec.TrackHalfLength)
                : NodeBox(1.6f, 0f, Spec.HouseTop, -1.9f, 1.6f);
            return (box.GetCenter(), box.Size);
        }
    }

    // ---- what a parked one keeps -------------------------------------------------------------
    public int PackFlags() => Spec.Pack(Slew, Boom, Stick, Bucket, Blade);

    public void UnpackFlags(int flags)
    {
        (Slew, Boom, Stick, Bucket, Blade) = Spec.Unpack(flags);
        Digging = false;
    }

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        ExcavatorMeshBuilder.CreateRig(this, HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    public override Node3D BuildParkedVisual(int riderIndex) => ExcavatorMeshBuilder.CreateRig(this, null);

    // ---- the engine ------------------------------------------------------------------------
    public const float IdleRpm = 900f, WorkRpm = 1900f;
    /// <summary>The mini's small diesel turns faster.</summary>
    public const float MiniIdleRpm = 1150f, MiniWorkRpm = 2450f;
    private Audio.EngineProfile? _sound;
    private float Idle => Mini ? MiniIdleRpm : IdleRpm;
    private float Top => Mini ? MiniWorkRpm : WorkRpm;
    public float Rpm => Mathf.Lerp(Idle, Top, Mathf.Max(_throttle, _work));
    public float Rpm01 => (Rpm - Idle) / (Top - Idle);
    public int Gear => 1;
    public float Throttle => Mathf.Max(_throttle, _work);
    public Audio.EngineProfile Sound => _sound ??= Audio.EngineProfile.For(Audio.EngineLayout.Diesel6, Idle, Top + 200f);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        // the tracks' heading follows the cab's and the slew: whatever turned the cab (the arm's
        // slew, a check's placing) is the slew's, whatever turned the tracks is the drive's
        TrackYaw = motion.Yaw - Slew;
        float target = 0f, turn = 0f;
        if (!Digging)
        {
            // throttle forward, brake back: a crawler has no brake to speak of, letting go stops it
            target = (input.Throttle - Mathf.Max(input.Brake, input.Handbrake ? 1f : 0f)) * Spec.TopSpeed;
            // the tracks counter-rotate to turn, so it turns as well standing as moving
            turn = -input.Steer * Spec.TurnRate;
            _throttle = Mathf.Max(Mathf.Abs(input.Throttle), Mathf.Abs(input.Brake));
        }
        else _throttle = 0f;
        _signed = Mathf.MoveToward(_signed, target, Spec.Accel * 2f * dt);
        _yawRate = Mathf.MoveToward(_yawRate, turn, Spec.TurnRate * 3f * dt);
        TrackYaw += _yawRate * dt;
        // the house turns with the tracks it stands on
        motion.Yaw += _yawRate * dt;
        motion.YawRate = _yawRate;
        motion.Speed = Mathf.Abs(_signed);
        // it travels along its tracks, which may point anywhere relative to the cab
        motion.Slip = MathX.WrapAngle(-Slew + (_signed < 0f ? Mathf.Pi : 0f));
        motion.Lean = 0f;
        (_left, _right) = Spec.Tracks(_signed, _yawRate);
    }

    /// <summary>
    /// The arm after the step: each joint runs at its rate while its lever is held and stops where
    /// it is let go. The slew turns the cab (the vehicle's yaw) and so the camera with it; the
    /// tracks stay where they are. The blade, where there is one, works in either mode: a mini
    /// pushes soil with it as it drives.
    /// </summary>
    public void Work(ref RideMotion motion, float dt)
    {
        var l = Digging ? Levers : default;
        float slew = l.Slew * Spec.SlewRate * dt;
        motion.Yaw += slew;
        Slew = MathX.WrapAngle(Slew + slew);
        motion.Slip = MathX.WrapAngle(-Slew + (_signed < 0f ? Mathf.Pi : 0f));
        Boom = Spec.ClampBoom(Boom + l.Boom * Spec.BoomRate * dt);
        Stick = Spec.ClampStick(Stick + l.Stick * Spec.StickRate * dt);
        Bucket = Spec.ClampBucket(Bucket + l.Bucket * Spec.BucketRate * dt);
        float blade = Spec.HasBlade ? BladeLever : 0f;
        Blade = Spec.ClampBlade(Blade + blade * Spec.BladeRate * dt);
        // the engine works as hard as the hardest-pushed lever
        _work = Mathf.Max(Mathf.Max(Mathf.Max(Mathf.Abs(l.Slew), Mathf.Abs(l.Stick)), Mathf.Max(Mathf.Abs(l.Boom), Mathf.Abs(l.Bucket))), Mathf.Abs(blade));
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt) => Dress(visual, dt);

    private float _scrollLeft, _scrollRight;

    /// <summary>The drawn machine: the undercarriage turned back by the slew, the arm's joints, the tracks' rollers.</summary>
    private void Dress(Node3D visual, float dt)
    {
        if (ExcavatorMeshBuilder.ArmOf(visual) is not { } arm) return;
        _scrollLeft += _left * dt;
        _scrollRight += _right * dt;
        arm.Pose(Slew, Boom, Stick, Bucket, _scrollLeft, _scrollRight, Blade);
        if (visual is HeavyRig rig)
        {
            rig.Rpm = Rpm;
            rig.Throttle = Throttle;
            rig.SpeedKmh = Mathf.Abs(_signed) * 3.6f;
            rig.Gear = Digging ? "D" : "T";
        }
    }

    /// <summary>
    /// Remote copies: the arm whole (slew, boom, stick, bucket), and a mini's blade in the bucket's
    /// float (<see cref="ExcavatorSpec.PoseW"/>). A copy is drawn from these alone, so it slews and
    /// digs exactly as the driver does.
    /// </summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(Slew, Boom, Stick, Spec.PoseW(Bucket, Blade));

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        // a pose of all zeroes is "nothing written yet": keep the rest pose rather than a boom on the ground
        if (pose != Vector4.Zero)
        {
            Slew = pose.X;
            Boom = Spec.ClampBoom(pose.Y);
            Stick = Spec.ClampStick(pose.Z);
            var (bucket, blade) = Spec.FromPoseW(pose.W);
            Bucket = Spec.ClampBucket(bucket);
            Blade = Spec.ClampBlade(blade);
        }
        Dress(visual, dt);
    }
}
