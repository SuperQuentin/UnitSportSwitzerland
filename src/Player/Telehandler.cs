using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// A telehandler (#614, RideKind 198): a telescopic boom from the back of the chassis with a
/// self-levelling fork carriage, and <b>three steering modes</b> on the car's roof switch
/// (<c>roof_toggle</c>: O / D-pad left / a VR dash button): front wheels only, all four the
/// opposite way round (half the circle), or all four the same way, which crabs it sideways without
/// turning. In <b>work mode</b> (the excavator's and the loader's toggle) the right stick and the
/// arrows lift the boom and tilt the forks, the gear paddles (Shift / Ctrl, RB / LB, the forklift
/// mast's) run it out and in, and it still drives. See <c>docs/notes/vehicles/telehandler.md</c>.
/// </summary>
public sealed class Telehandler : Rideable, IEngined
{
    public override RideKind Kind => RideKind.Telehandler;
    public override string Label => "Telehandler";
    public override string Blurb =>
        "{move_forward} drive, {move_back} brake / reverse, {move_left}{move_right} steer; {roof_toggle} steering: front, four-wheel, crab; "
        + "{dig_mode} work mode: {arm_boom_up}{arm_boom_down} lift, {shift_up}{shift_down} extend, {arm_bucket_curl}{arm_bucket_dump} tilt the forks";

    /// <summary>The front wheels' angle, rad (+ left).</summary>
    public float Steer { get; set; }
    public SteerMode Mode { get; set; }
    public float Lift { get; set; } = TelehandlerLayout.RestLift;
    public float Extend { get; set; } = TelehandlerLayout.RestExtend;
    public float Tilt { get; set; } = TelehandlerLayout.RestTilt;

    /// <summary>Work mode: the right stick, the arrows and the paddles are the boom's.</summary>
    public bool Working { get; set; }

    /// <summary>The boom's levers this frame, -1..1 (set by the driver's input, or a check).</summary>
    public (float Lift, float Extend, float Tilt) Levers { get; set; }

    private float _signed, _throttle, _brake, _work, _spin;

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 320f;
    public override float BodyRadius => 1.15f;
    public override float BodyHeight => 2.4f;
    public override float DismountSpeed => 1.2f;
    public override float ChaseDistance => 10f;
    public override float ChaseHeight => 4.2f;
    public override float ChasePitch => -0.28f;
    public override float BaseFov => 68f;
    public override float MaxFov => 76f;
    public override float FovSpeed => 6f;

    private static Vector3? _eye;
    public override Vector3 FirstPersonEye => _eye ??= TelehandlerMeshBuilder.Parts().Cockpit!.Eye;
    public override float EyeHeight => FirstPersonEye.Y;
    /// <summary>Climbed into through the cab's door on the left, between the wheels.</summary>
    public override Vector3 EntryPoint => Flip(new Vector3(TelehandlerLayout.ChassisHalf + 0.45f, 0f, 0f));
    public override bool ExitLeft => true;
    public override bool DrivenFromInside => false;

    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    private static Aabb NodeBox(float x, float y0, float y1, float z0, float z1) =>
        new(new Vector3(-x, y0, -z1), new Vector3(x * 2f, y1 - y0, z1 - z0));

    /// <summary>The chassis and its wheels low, the cab and the engine high. The boom is in neither: a hull out at the forks would stop them short of every pallet.</summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes => (
        NodeBox(TelehandlerLayout.ChassisHalf, 0.35f, TelehandlerLayout.ChassisTop, TelehandlerLayout.Tail, TelehandlerLayout.Nose),
        NodeBox(TelehandlerLayout.ChassisHalf, TelehandlerLayout.ChassisTop, TelehandlerLayout.CabTop, TelehandlerLayout.CabBack - 0.6f, TelehandlerLayout.CabFront));

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var box = NodeBox(TelehandlerLayout.ChassisHalf, 0f, TelehandlerLayout.CabTop, TelehandlerLayout.Tail, TelehandlerLayout.Nose);
            return (box.GetCenter(), box.Size);
        }
    }

    public int PackFlags() => TelehandlerLayout.Pack(Lift, Extend, Tilt, Mode);

    public void UnpackFlags(int flags)
    {
        (Lift, Extend, Tilt, Mode) = TelehandlerLayout.Unpack(flags);
        Working = false;
    }

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        TelehandlerMeshBuilder.CreateRig(this, HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    public override Node3D BuildParkedVisual(int riderIndex) => TelehandlerMeshBuilder.CreateRig(this, null);

    /// <summary>The next steering mode: front, four-wheel, crab, front again.</summary>
    public void NextMode() => Mode = (SteerMode)(((int)Mode + 1) % 3);

    // ---- the engine ------------------------------------------------------------------------
    public const float IdleRpm = 850f, Redline = 2400f;
    private Audio.EngineProfile? _sound;
    public float Rpm => Mathf.Lerp(IdleRpm, Redline, Mathf.Max(_throttle, _work * 0.8f));
    public float Rpm01 => (Rpm - IdleRpm) / (Redline - IdleRpm);
    public int Gear => _signed < -0.1f ? -1 : 1 + (int)(Mathf.Abs(_signed) / TelehandlerLayout.TopSpeed * 3.99f);
    public float Throttle => Mathf.Max(_throttle, _work);
    public Audio.EngineProfile Sound => _sound ??= Audio.EngineProfile.For(Audio.EngineLayout.Diesel6, IdleRpm, Redline);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        // signed speed along the heading: the motion's is a magnitude, reversing is the slip past a right angle
        float v = _signed;
        if (Mathf.Abs(Mathf.Abs(v) - motion.Speed) > 0.5f) v = motion.Speed * (Mathf.Abs(MathX.WrapAngle(motion.Slip)) > 1.6f ? -1f : 1f);
        float brake = Mathf.Max(input.Brake, input.Handbrake ? 1f : 0f);
        float a = 0f;
        if (input.Throttle > 0.05f)
            a = v < -0.1f ? TelehandlerLayout.BrakeDecel * input.Throttle : TelehandlerLayout.Accel * input.Throttle;
        else if (brake > 0.05f)
            // the brake stops it, then reverses it, as its powershift's shuttle does
            a = v > 0.1f ? -TelehandlerLayout.BrakeDecel * brake : -TelehandlerLayout.Accel * 0.7f * brake;
        if (ground.OnFloor) a += SlopeAccel(ground.Grade) * Mathf.Sign(v == 0f ? 1f : v);
        v += a * dt;
        v = Mathf.MoveToward(v, 0f, (0.3f + (input.Throttle < 0.05f && brake < 0.05f && Mathf.Abs(v) < 0.6f ? 2f : 0f)) * dt);
        v = Mathf.Clamp(v, -TelehandlerLayout.TopReverse, TelehandlerLayout.TopSpeed);
        _signed = v;

        Steer = Mathf.MoveToward(Steer, -input.Steer * TelehandlerLayout.MaxSteer, TelehandlerLayout.SteerRate * dt);
        var (yawRate, slip) = TelehandlerLayout.Motion(v, Steer, Mode);
        motion.Yaw += yawRate * dt;
        motion.YawRate = yawRate;
        motion.Speed = Mathf.Abs(v);
        // the way it travels, from its nose: crabbing that is the wheels' angle; backwards, that turned half round
        motion.Slip = v < 0f ? MathX.WrapAngle(slip + Mathf.Pi) : slip;
        motion.Lean = 0f;
        _throttle = input.Throttle;
        _brake = brake;
    }

    /// <summary>After the step: the boom toward wherever its levers take it, in work mode.</summary>
    public void Work(float dt)
    {
        var l = Working ? Levers : default;
        Lift = TelehandlerLayout.ClampLift(Lift + l.Lift * TelehandlerLayout.LiftRate * dt);
        Extend = TelehandlerLayout.ClampExtend(Extend + l.Extend * TelehandlerLayout.ExtendRate * dt);
        Tilt = TelehandlerLayout.ClampTilt(Tilt + l.Tilt * TelehandlerLayout.TiltRate * dt);
        _work = Mathf.Max(Mathf.Abs(l.Lift), Mathf.Max(Mathf.Abs(l.Extend), Mathf.Abs(l.Tilt)));
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt) => Dress(visual, _signed, dt);

    private void Dress(Node3D visual, float speed, float dt)
    {
        _spin += speed / TelehandlerLayout.WheelRadius * dt;
        TelehandlerMeshBuilder.BoomOf(visual)?.Pose(Lift, Extend, Tilt, TelehandlerLayout.RearSteer(Steer, Mode), _spin);
        if (visual is not HeavyRig rig) return;
        rig.SteerAngle = Steer;
        rig.WheelSpin = _spin;
        rig.SpeedKmh = Mathf.Abs(speed) * 3.6f;
        rig.Rpm = Rpm;
        rig.Throttle = Throttle;
        rig.Brake = _brake;
        rig.BrakeLights = _brake > 0.05f;
        rig.ReverseLights = speed < -0.1f;
        // the dash shows the steering mode where a gear would be: F, 4 or C
        rig.Gear = speed < -0.1f ? "R" : Mode switch { SteerMode.FourWheel => "4", SteerMode.Crab => "C", _ => "F" };
    }

    /// <summary>
    /// Remote copies: the front wheels with the mode (<see cref="TelehandlerLayout.PoseSteer"/>), the
    /// lift, the extension with the tilt (<see cref="TelehandlerLayout.PoseExtend"/>), and the signed
    /// speed the wheels turn by.
    /// </summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(TelehandlerLayout.PoseSteer(Steer, Mode), Lift, TelehandlerLayout.PoseExtend(Extend, Tilt), _signed);

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (pose != Vector4.Zero)
        {
            (Steer, Mode) = TelehandlerLayout.FromPoseSteer(pose.X);
            Lift = TelehandlerLayout.ClampLift(pose.Y);
            (Extend, Tilt) = TelehandlerLayout.FromPoseExtend(pose.Z);
        }
        Dress(visual, pose.W, dt);
    }
}
