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
/// </summary>
public sealed class Excavator : Rideable, IEngined
{
    public override RideKind Kind => RideKind.Excavator;
    public override string Label => "Excavator";
    public override string Blurb =>
        "{move_forward}{move_back} tracks, {move_left}{move_right} turn on the spot; {dig_mode} dig mode: "
        + "{arm_slew_left}{arm_slew_right} slew, {arm_stick_out}{arm_stick_in} stick, {arm_boom_up}{arm_boom_down} boom, {arm_bucket_curl}{arm_bucket_dump} bucket";

    /// <summary>The tracks' heading in the world (the vehicle's yaw is the cab's).</summary>
    public float TrackYaw { get; set; }

    /// <summary>The upper structure's turn on the tracks, rad (+ left): the cab's yaw less the tracks'.</summary>
    public float Slew { get; set; }

    public float Boom { get; set; } = ExcavatorLayout.RestBoom;
    public float Stick { get; set; } = ExcavatorLayout.RestStick;
    public float Bucket { get; set; } = ExcavatorLayout.RestBucket;

    /// <summary>Dig mode: the tracks hold, the sticks work the arm.</summary>
    public bool Digging { get; set; }

    /// <summary>The arm's levers this frame, -1..1 each (set by the driver's input, or a check).</summary>
    public (float Slew, float Stick, float Boom, float Bucket) Levers { get; set; }

    private float _signed, _yawRate, _throttle, _work;
    private float _left, _right;

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 400f;
    public override float BodyRadius => 1.3f;
    public override float BodyHeight => 2.2f;
    public override float DismountSpeed => 0.8f;
    public override float ChaseDistance => 12f;
    public override float ChaseHeight => 5f;
    public override float ChasePitch => -0.3f;
    public override float BaseFov => 68f;
    public override float MaxFov => 72f;
    public override float FovSpeed => 4f;

    private static Vector3? _eye;
    public override Vector3 FirstPersonEye => _eye ??= ExcavatorMeshBuilder.Parts().Cockpit!.Eye;
    public override float EyeHeight => FirstPersonEye.Y;
    /// <summary>Climbed into from the left track, in front of the cab's door.</summary>
    public override Vector3 EntryPoint => Flip(new Vector3(ExcavatorLayout.HalfGauge + ExcavatorLayout.ShoeWidth * 0.5f + 0.3f, 0f, 0.6f));
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
    public override (Aabb Lower, Aabb Upper)? HullBoxes => (
        NodeBox(1.6f, 0.3f, ExcavatorLayout.RingTop, -1.6f, 1.6f),
        NodeBox(ExcavatorLayout.HouseHalf, ExcavatorLayout.RingTop, ExcavatorLayout.HouseTop, ExcavatorLayout.HouseBack, ExcavatorLayout.HouseFront));

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var box = NodeBox(1.6f, 0f, ExcavatorLayout.HouseTop, -1.9f, 1.6f);
            return (box.GetCenter(), box.Size);
        }
    }

    // ---- what a parked one keeps -------------------------------------------------------------
    public int PackFlags() => ExcavatorLayout.Pack(Slew, Boom, Stick, Bucket);

    public void UnpackFlags(int flags)
    {
        (Slew, Boom, Stick, Bucket) = ExcavatorLayout.Unpack(flags);
        Digging = false;
    }

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        ExcavatorMeshBuilder.CreateRig(this, HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    public override Node3D BuildParkedVisual(int riderIndex) => ExcavatorMeshBuilder.CreateRig(this, null);

    // ---- the engine ------------------------------------------------------------------------
    public const float IdleRpm = 900f, WorkRpm = 1900f;
    private Audio.EngineProfile? _sound;
    public float Rpm => Mathf.Lerp(IdleRpm, WorkRpm, Mathf.Max(_throttle, _work));
    public float Rpm01 => (Rpm - IdleRpm) / (WorkRpm - IdleRpm);
    public int Gear => 1;
    public float Throttle => Mathf.Max(_throttle, _work);
    public Audio.EngineProfile Sound => _sound ??= Audio.EngineProfile.For(Audio.EngineLayout.Diesel6, IdleRpm, WorkRpm + 200f);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        // the tracks' heading follows the cab's and the slew: whatever turned the cab (the arm's
        // slew, a check's placing) is the slew's, whatever turned the tracks is the drive's
        TrackYaw = motion.Yaw - Slew;
        float target = 0f, turn = 0f;
        if (!Digging)
        {
            // throttle forward, brake back: a crawler has no brake to speak of, letting go stops it
            target = (input.Throttle - Mathf.Max(input.Brake, input.Handbrake ? 1f : 0f)) * ExcavatorLayout.TopSpeed;
            // the tracks counter-rotate to turn, so it turns as well standing as moving
            turn = -input.Steer * ExcavatorLayout.TurnRate;
            _throttle = Mathf.Max(Mathf.Abs(input.Throttle), Mathf.Abs(input.Brake));
        }
        else _throttle = 0f;
        _signed = Mathf.MoveToward(_signed, target, ExcavatorLayout.Accel * 2f * dt);
        _yawRate = Mathf.MoveToward(_yawRate, turn, ExcavatorLayout.TurnRate * 3f * dt);
        TrackYaw += _yawRate * dt;
        // the house turns with the tracks it stands on
        motion.Yaw += _yawRate * dt;
        motion.YawRate = _yawRate;
        motion.Speed = Mathf.Abs(_signed);
        // it travels along its tracks, which may point anywhere relative to the cab
        motion.Slip = MathX.WrapAngle(-Slew + (_signed < 0f ? Mathf.Pi : 0f));
        motion.Lean = 0f;
        (_left, _right) = ExcavatorLayout.Tracks(_signed, _yawRate);
    }

    /// <summary>
    /// The arm after the step: each joint runs at its rate while its lever is held and stops where
    /// it is let go. The slew turns the cab (the vehicle's yaw) and so the camera with it; the
    /// tracks stay where they are.
    /// </summary>
    public void Work(ref RideMotion motion, float dt)
    {
        var l = Digging ? Levers : default;
        float slew = l.Slew * ExcavatorLayout.SlewRate * dt;
        motion.Yaw += slew;
        Slew = MathX.WrapAngle(Slew + slew);
        motion.Slip = MathX.WrapAngle(-Slew + (_signed < 0f ? Mathf.Pi : 0f));
        Boom = ExcavatorLayout.ClampBoom(Boom + l.Boom * ExcavatorLayout.BoomRate * dt);
        Stick = ExcavatorLayout.ClampStick(Stick + l.Stick * ExcavatorLayout.StickRate * dt);
        Bucket = ExcavatorLayout.ClampBucket(Bucket + l.Bucket * ExcavatorLayout.BucketRate * dt);
        // the engine works as hard as the hardest-pushed lever
        _work = Mathf.Max(Mathf.Max(Mathf.Abs(l.Slew), Mathf.Abs(l.Stick)), Mathf.Max(Mathf.Abs(l.Boom), Mathf.Abs(l.Bucket)));
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt) => Dress(visual, dt);

    private float _scrollLeft, _scrollRight;

    /// <summary>The drawn machine: the undercarriage turned back by the slew, the arm's joints, the tracks' rollers.</summary>
    private void Dress(Node3D visual, float dt)
    {
        if (ExcavatorMeshBuilder.ArmOf(visual) is not { } arm) return;
        _scrollLeft += _left * dt;
        _scrollRight += _right * dt;
        arm.Pose(Slew, Boom, Stick, Bucket, _scrollLeft, _scrollRight);
        if (visual is HeavyRig rig)
        {
            rig.Rpm = Rpm;
            rig.Throttle = Throttle;
            rig.SpeedKmh = Mathf.Abs(_signed) * 3.6f;
            rig.Gear = Digging ? "D" : "T";
        }
    }

    /// <summary>
    /// Remote copies: the arm whole (slew, boom, stick, bucket). A copy is drawn from these alone,
    /// so it slews and digs exactly as the driver does.
    /// </summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(Slew, Boom, Stick, Bucket);

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        // a pose of all zeroes is "nothing written yet": keep the rest pose rather than a boom on the ground
        if (pose != Vector4.Zero)
        {
            Slew = pose.X;
            Boom = ExcavatorLayout.ClampBoom(pose.Y);
            Stick = ExcavatorLayout.ClampStick(pose.Z);
            Bucket = ExcavatorLayout.ClampBucket(pose.W);
        }
        Dress(visual, dt);
    }
}
