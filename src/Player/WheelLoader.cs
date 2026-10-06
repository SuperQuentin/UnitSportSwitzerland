using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// An articulated wheel loader (#612, RideKind 195). It steers by bending in the middle: the front
/// frame, with the lift arm and the bucket, swings about a hinge, and the rear frame with the cab
/// follows — so the vehicle's yaw is the rear frame's, and it travels along it. In <b>work mode</b>
/// (the excavator's toggle, <c>dig_mode</c>) the right stick and the arrows raise the arm and tilt
/// the bucket, and unlike the excavator it still drives: a loader works on the move.
/// See <c>docs/notes/vehicles/wheel-loader.md</c>.
/// </summary>
public sealed class WheelLoader : Rideable, IEngined
{
    public override RideKind Kind => RideKind.WheelLoader;
    public override string Label => "Wheel loader";
    public override string Blurb =>
        "{move_forward} drive, {move_back} brake / reverse, {move_left}{move_right} steer (it bends in the middle); "
        + "{dig_mode} work mode: {arm_boom_up}{arm_boom_down} lift, {arm_bucket_curl}{arm_bucket_dump} tilt the bucket";

    /// <summary>The front frame's swing about the hinge, rad (+ left).</summary>
    public float Articulation { get; set; }
    public float Lift { get; set; } = WheelLoaderLayout.RestLift;
    public float Tilt { get; set; } = WheelLoaderLayout.RestTilt;

    /// <summary>Work mode: the right stick and the arrows are the arm's.</summary>
    public bool Working { get; set; }

    /// <summary>The arm's levers this frame, -1..1 (set by the driver's input, or a check).</summary>
    public (float Lift, float Tilt) Levers { get; set; }

    private float _signed, _throttle, _brake, _work, _spin;

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 360f;
    public override float BodyRadius => 1.25f;
    public override float BodyHeight => 2.6f;
    public override float DismountSpeed => 1.2f;
    public override float ChaseDistance => 11f;
    public override float ChaseHeight => 4.8f;
    public override float ChasePitch => -0.3f;
    public override float BaseFov => 68f;
    public override float MaxFov => 76f;
    public override float FovSpeed => 6f;

    private static Vector3? _eye;
    public override Vector3 FirstPersonEye => _eye ??= WheelLoaderMeshBuilder.Parts().Cockpit!.Eye;
    public override float EyeHeight => FirstPersonEye.Y;
    /// <summary>Climbed into up the ladder behind the left front wheel.</summary>
    public override Vector3 EntryPoint => Flip(new Vector3(WheelLoaderLayout.RearHalf + 0.45f, 0f, -0.7f));
    public override bool ExitLeft => true;
    public override bool DrivenFromInside => false;

    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    private static Aabb NodeBox(float x, float y0, float y1, float z0, float z1) =>
        new(new Vector3(-x, y0, -z1), new Vector3(x * 2f, y1 - y0, z1 - z0));

    /// <summary>
    /// The rear frame and the front frame to the hinge low, the cab and the engine high. The
    /// bucket is in neither: a hull out at its lip would stop it a bucket short of every heap.
    /// </summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes => (
        NodeBox(WheelLoaderLayout.RearHalf, 0.45f, 1.6f, WheelLoaderLayout.Tail, 2.4f),
        NodeBox(WheelLoaderLayout.RearHalf - 0.2f, 1.6f, WheelLoaderLayout.EngineTop, WheelLoaderLayout.Tail + 0.2f, WheelLoaderLayout.CabFront));

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var box = NodeBox(WheelLoaderLayout.RearHalf, 0f, WheelLoaderLayout.EngineTop, WheelLoaderLayout.Tail, 2.4f);
            return (box.GetCenter(), box.Size);
        }
    }

    public int PackFlags() => WheelLoaderLayout.Pack(Lift, Tilt, Articulation);

    public void UnpackFlags(int flags)
    {
        (Lift, Tilt, Articulation) = WheelLoaderLayout.Unpack(flags);
        Working = false;
    }

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        WheelLoaderMeshBuilder.CreateRig(this, HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    public override Node3D BuildParkedVisual(int riderIndex) => WheelLoaderMeshBuilder.CreateRig(this, null);

    // ---- the engine ------------------------------------------------------------------------
    public const float IdleRpm = 800f, Redline = 2100f;
    private Audio.EngineProfile? _sound;
    public float Rpm => Mathf.Lerp(IdleRpm, Redline, Mathf.Max(_throttle, _work * 0.8f));
    public float Rpm01 => (Rpm - IdleRpm) / (Redline - IdleRpm);
    public int Gear => _signed < -0.1f ? -1 : 1 + (int)(Mathf.Abs(_signed) / WheelLoaderLayout.TopSpeed * 3.99f);
    public float Throttle => Mathf.Max(_throttle, _work);
    public Audio.EngineProfile Sound => _sound ??= Audio.EngineProfile.For(Audio.EngineLayout.Diesel6, IdleRpm, Redline);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        // signed speed along the rear frame: the motion's is a magnitude, reversing is Slip = π
        float v = _signed;
        if (Mathf.Abs(Mathf.Abs(v) - motion.Speed) > 0.5f) v = motion.Speed * (Mathf.Abs(MathX.WrapAngle(motion.Slip)) > 1.5f ? -1f : 1f);
        float brake = Mathf.Max(input.Brake, input.Handbrake ? 1f : 0f);
        float a = 0f;
        if (input.Throttle > 0.05f)
            a = v < -0.1f ? WheelLoaderLayout.BrakeDecel * input.Throttle : WheelLoaderLayout.Accel * input.Throttle;
        else if (brake > 0.05f)
            // the brake stops it, then reverses it, as its powershift's shuttle does
            a = v > 0.1f ? -WheelLoaderLayout.BrakeDecel * brake : -WheelLoaderLayout.Accel * 0.7f * brake;
        if (ground.OnFloor) a += SlopeAccel(ground.Grade) * Mathf.Sign(v == 0f ? 1f : v);
        v += a * dt;
        v = Mathf.MoveToward(v, 0f, (0.3f + (input.Throttle < 0.05f && brake < 0.05f && Mathf.Abs(v) < 0.6f ? 2f : 0f)) * dt);
        v = Mathf.Clamp(v, -WheelLoaderLayout.TopReverse, WheelLoaderLayout.TopSpeed);
        _signed = v;

        // the frame swings toward the stick: left stick left swings the front left
        Articulation = Mathf.MoveToward(Articulation, -input.Steer * WheelLoaderLayout.MaxArticulation,
            WheelLoaderLayout.ArticulationRate * dt);
        float yawRate = WheelLoaderLayout.YawRate(v, Articulation);
        motion.Yaw += yawRate * dt;
        motion.YawRate = yawRate;
        motion.Speed = Mathf.Abs(v);
        motion.Slip = v < 0f ? Mathf.Pi : 0f;
        motion.Lean = 0f;
        _throttle = input.Throttle;
        _brake = brake;
    }

    /// <summary>After the step: the arm and the bucket toward wherever their levers take them, in work mode.</summary>
    public void Work(float dt)
    {
        var l = Working ? Levers : default;
        Lift = WheelLoaderLayout.ClampLift(Lift + l.Lift * WheelLoaderLayout.LiftRate * dt);
        Tilt = WheelLoaderLayout.ClampTilt(Tilt + l.Tilt * WheelLoaderLayout.TiltRate * dt);
        _work = Mathf.Max(Mathf.Abs(l.Lift), Mathf.Abs(l.Tilt));
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt) => Dress(visual, _signed, dt);

    private void Dress(Node3D visual, float speed, float dt)
    {
        _spin += speed / WheelLoaderLayout.WheelRadius * dt;
        if (WheelLoaderMeshBuilder.FrontOf(visual) is { } front) front.Pose(Articulation, Lift, Tilt, _spin);
        if (visual is not HeavyRig rig) return;
        rig.WheelSpin = _spin;
        rig.SpeedKmh = Mathf.Abs(speed) * 3.6f;
        rig.Rpm = Rpm;
        rig.Throttle = Throttle;
        rig.Brake = _brake;
        rig.BrakeLights = _brake > 0.05f;
        rig.ReverseLights = speed < -0.1f;
        rig.Gear = speed < -0.1f ? "R" : Gear.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Remote copies: the frame's bend, the arm, the bucket and the signed speed (the wheels turn by it).</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(Articulation, Lift, Tilt, _signed);

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (pose != Vector4.Zero)
        {
            Articulation = Mathf.Clamp(pose.X, -WheelLoaderLayout.MaxArticulation, WheelLoaderLayout.MaxArticulation);
            Lift = WheelLoaderLayout.ClampLift(pose.Y);
            Tilt = WheelLoaderLayout.ClampTilt(pose.Z);
        }
        Dress(visual, pose.W, dt);
    }
}
