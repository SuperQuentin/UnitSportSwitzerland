using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// A counterbalance forklift (#583, RideKind 193): driven at a walk, steered on its rear axle so it
/// turns in its own length, with a mast whose forks run from the ground to 3.2 m on the shift
/// paddles — a machine with no gearbox has them free, and they are already bound on the keyboard,
/// the pad and (as the hand grips, or the cab lever of <c>XrCabControls</c>) in VR.
///
/// <para>
/// Shaped on <see cref="Airstairs"/>, which is the same animal: a slow truck whose only extra state
/// is one height, eased toward a target, replicated in <see cref="WritePose"/> for a copy and in
/// <c>VehicleState.Flags</c> for a parked one. See <c>docs/notes/vehicles/forklift.md</c>.
/// </para>
/// </summary>
public sealed class Forklift : Rideable
{
    public override RideKind Kind => RideKind.Forklift;
    public override string Label => "Forklift";
    public override string Blurb =>
        "{move_forward} drive, {move_back} brake / reverse, {move_left}{move_right} steer; {shift_up}{shift_down} raise and lower the forks";

    /// <summary>The forks' height over the ground, m (what the mast is drawn at).</summary>
    public float Lift { get; set; } = ForkliftLayout.MinLift;

    /// <summary>Where the forks are going, m; eased at <see cref="ForkliftLayout.LiftRate"/>.</summary>
    public float TargetLift { get; set; } = ForkliftLayout.MinLift;

    /// <summary>
    /// What is on the forks: 0 nothing, else 1 + the pallet's load byte (#583 phase 2). It rides in
    /// the replicated pose and in the parked flags, so a copy and a parked machine both keep it.
    /// </summary>
    public int Carrying { get; set; }

    /// <summary>Top speed forward and in reverse, m/s (18 km/h both ways: it is driven backwards half the time).</summary>
    public const float TopSpeed = 5.0f, TopReverse = 5.0f;
    private const float Accel = 2.6f, BrakeDecel = 5.0f, Drag = 0.35f, MaxSteer = 0.95f;

    private float _steer;
    private float _signed;
    private float _spin, _throttle, _brake;

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 120f;
    public override float BodyRadius => 0.6f;
    public override float BodyHeight => 1.6f;
    public override float DismountSpeed => 1.5f;
    public override float ChaseDistance => 7.5f;
    public override float ChaseHeight => 3.6f;
    public override float ChasePitch => -0.3f;
    public override float BaseFov => 70f;
    public override float MaxFov => 76f;
    public override float FovSpeed => 6f;

    private static Vector3? _eye;
    public override Vector3 FirstPersonEye => _eye ??= ForkliftMeshBuilder.Parts().Cockpit!.Eye;
    public override float EyeHeight => FirstPersonEye.Y;
    public override Vector3 EntryPoint => Flip(ForkliftLayout.Step);
    public override bool ExitLeft => true;

    /// <summary>Got on from outside: it is a seat under a roof, not a cab with a door.</summary>
    public override bool DrivenFromInside => false;

    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    // ---- collision ---------------------------------------------------------------------------
    private static Aabb NodeBox(float x, float y0, float y1, float z0, float z1) =>
        new(new Vector3(-x, y0, -z1), new Vector3(x * 2f, y1 - y0, z1 - z0));

    /// <summary>
    /// The machine itself low, its overhead guard high. The forks are deliberately in neither: a
    /// hull out at the tips would stop the forks a pallet's width short of every pallet, which is
    /// the one thing a forklift must be able to do.
    /// </summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes => (
        NodeBox(ForkliftLayout.HalfWidth, 0f, ForkliftLayout.BonnetTop, ForkliftLayout.Rear, ForkliftLayout.MastZ + 0.1f),
        NodeBox(ForkliftLayout.HalfWidth, ForkliftLayout.BonnetTop, ForkliftLayout.GuardTop, -0.85f, 0.6f));

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var box = NodeBox(ForkliftLayout.HalfWidth, 0f, ForkliftLayout.GuardTop, ForkliftLayout.Rear, ForkliftLayout.MastZ + 0.1f);
            return (box.GetCenter(), box.Size);
        }
    }

    /// <summary>Parked, the forks are solid too: a tine lying on the ground is a thing you trip over.</summary>
    public override IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
        float y = ForkliftLayout.Clamp(Lift);
        var box = NodeBox(ForkliftLayout.TineX + ForkliftLayout.TineWidth, y - ForkliftLayout.TineThick, y,
            ForkliftLayout.MastZ, ForkliftLayout.MastZ + ForkliftLayout.TineLength);
        yield return (Transform3D.Identity, box.GetCenter(), box.Size);
    }

    private static SeatAnchor[]? _seats;
    public override SeatAnchor[] Seats => _seats ??= ForkliftMeshBuilder.Parts().Seats;

    // ---- flags: what a parked one keeps (the fork height in cm, and its load) -----------------
    /// <summary>Fork height in centimetres (ten bits), with what it carries above it (nine: 0 = empty forks, else 1 + the load byte).</summary>
    public int PackFlags() =>
        Mathf.RoundToInt(Mathf.Clamp(Lift, 0f, ForkliftLayout.MaxLift) * 100f) | (Mathf.Clamp(Carrying, 0, 0x1FF) << 10);

    public void UnpackFlags(int flags)
    {
        if (flags == 0) return;
        Lift = TargetLift = ForkliftLayout.Clamp((flags & 0x3FF) / 100f);
        Carrying = (flags >> 10) & 0x1FF;
    }

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        ForkliftMeshBuilder.CreateRig(Lift, HumanPalette.ForRider(riderIndex) with { Outfit = outfit }, Carrying);

    public override Node3D BuildParkedVisual(int riderIndex) => ForkliftMeshBuilder.CreateRig(Lift, null, Carrying);

    /// <summary>The forks toward their target, at the mast's rate.</summary>
    public void Mast(float dt) =>
        Lift = Mathf.MoveToward(Lift, ForkliftLayout.Clamp(TargetLift), ForkliftLayout.LiftRate * dt);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        // signed speed along the nose: the motion's speed is a magnitude, reversing is Slip = π
        float v = _signed;
        if (Mathf.Abs(Mathf.Abs(v) - motion.Speed) > 0.5f) v = motion.Speed * (Mathf.Abs(MathX.WrapAngle(motion.Slip)) > 1.5f ? -1f : 1f);
        // the pedal held at a standstill is reverse; the handbrake only holds — a machine left on
        // it with its forks going up must not back away by itself (#583: it did, 3.6 m)
        float pedal = input.Brake;
        float brake = Mathf.Max(pedal, input.Handbrake ? 1f : 0f);
        float a = 0f;
        if (input.Throttle > 0.05f)
            a = v < -0.1f ? BrakeDecel * input.Throttle : Accel * input.Throttle;
        else if (pedal > 0.05f)
            a = v > 0.1f ? -BrakeDecel * pedal : -Accel * 0.8f * pedal;
        if (ground.OnFloor && !input.Handbrake) a += SlopeAccel(ground.Grade) * Mathf.Sign(v == 0f ? 1f : v);
        v += a * dt;
        v = Mathf.MoveToward(v, 0f, (Drag + (input.Throttle < 0.05f && brake < 0.05f && Mathf.Abs(v) < 0.6f ? 2.5f : 0f)) * dt);
        if (input.Handbrake) v = Mathf.MoveToward(v, 0f, BrakeDecel * dt);
        v = Mathf.Clamp(v, -TopReverse, TopSpeed);
        _signed = v;

        // Steered on the rear axle, modelled as what that buys rather than as an offset pivot: a
        // 1.6 m wheelbase on a 54° lock is a 1.1 m circle, so it turns inside its own length. The
        // yaw keeps a car's sign — the machine goes the way the wheel is turned, as any driver
        // expects; what rear steering really adds on top (the tail swinging wide, and the
        // stability swapping between forward and reverse) is not modelled. See the note.
        _steer = Mathf.MoveToward(_steer, input.Steer, 3.5f * dt);
        float yawRate = v * Mathf.Tan(-_steer * MaxSteer) / ForkliftLayout.Wheelbase;
        motion.Yaw += yawRate * dt;
        motion.YawRate = yawRate;
        motion.Speed = Mathf.Abs(v);
        motion.Slip = v < 0f ? Mathf.Pi : 0f;
        motion.Lean = 0f;
        _throttle = input.Throttle;
        _brake = brake;
        Mast(dt);
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt) =>
        Dress(visual, _signed, _steer, _throttle, _brake, dt);

    /// <summary>The drawn machine: wheels, the wheel in the driver's hands, the dials, the mast.</summary>
    private void Dress(Node3D visual, float speed, float steer, float throttle, float brake, float dt)
    {
        if (visual is not HeavyRig rig) return;
        float angle = -steer * MaxSteer;
        rig.SteerAngle = angle;
        rig.WheelTurn = angle * HeavyCockpit.SteerRatio;
        _spin += speed / ForkliftLayout.FrontRadius * dt;
        rig.WheelSpin = _spin;
        rig.SpeedKmh = Mathf.Abs(speed) * 3.6f;
        rig.Rpm = 700f + Mathf.Abs(speed) / TopSpeed * 1500f + throttle * 250f;
        rig.Throttle = throttle;
        rig.Brake = brake;
        rig.BrakeLights = brake > 0.05f;
        rig.ReverseLights = speed < -0.1f;
        // the small dial reads the forks, 0-3.5 m, the way the airstairs' reads its platform
        rig.Air = Lift / ForkliftLayout.LiftDial * HeavyDriveline.AirMax;
        // the display: the forks in decimetres
        rig.Gear = Mathf.RoundToInt(Lift * 10f).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ForkliftMeshBuilder.MastOf(rig) is { } mast)
        {
            mast.Lift = Lift;
            // what is on the forks: a pallet, drawn on the carriage, so it crosses a bay's portal with them
            mast.Carrying = Carrying;
        }
    }

    /// <summary>
    /// Remote copies: the fork height (what their mast is drawn at, on every peer), the steer,
    /// what is on the forks, and the signed speed.
    /// </summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(Lift, _steer, Carrying, _signed);

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        Lift = TargetLift = ForkliftLayout.Clamp(pose.X);
        Carrying = Mathf.RoundToInt(pose.Z);
        Dress(visual, pose.W, pose.Y, 0f, 0f, dt);
    }
}
