using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// A compact tandem roller (#614, RideKind 197): two steel drums, frame steering like the wheel
/// loader's (it bends in the middle, the yaw is the rear frame's), a hydrostatic drive that stops
/// when the stick is let go, and drums that <b>vibrate</b> on the work-mode toggle (<c>dig_mode</c>,
/// C / pad B / the VR console's red button): a 55 Hz hum and a tremble in the driver's view, which
/// grow and die away over a little over half a second. See <c>docs/notes/vehicles/compact-roller.md</c>.
/// </summary>
public sealed class CompactRoller : Rideable, IEngined
{
    public override RideKind Kind => RideKind.CompactRoller;
    public override string Label => "Compact roller";
    public override string Blurb =>
        "{move_forward} drive, {move_back} brake / reverse, {move_left}{move_right} steer (it bends in the middle); {dig_mode} vibration";

    /// <summary>The front frame's swing about the hinge, rad (+ left).</summary>
    public float Articulation { get; set; }

    /// <summary>Whether the drums are set to vibrate.</summary>
    public bool Vibrating { get; set; }

    /// <summary>How hard they vibrate, 0..1: follows <see cref="Vibrating"/> over <see cref="CompactRollerLayout.VibrationRamp"/>.</summary>
    public float Vibration { get; private set; }

    private float _signed, _throttle, _brake, _spin;

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 220f;
    public override float BodyRadius => 0.75f;
    public override float BodyHeight => 1.6f;
    public override float DismountSpeed => 1.0f;
    public override float ChaseDistance => 7f;
    public override float ChaseHeight => 3f;
    public override float ChasePitch => -0.3f;
    public override float BaseFov => 68f;
    public override float MaxFov => 72f;
    public override float FovSpeed => 4f;

    /// <summary>The driver's view trembles with the drums: shaken by as much as they vibrate.</summary>
    public override float CameraShake => Vibration * CompactRollerLayout.ShakeAmplitude;

    private static Vector3? _eye;
    public override Vector3 FirstPersonEye => _eye ??= CompactRollerMeshBuilder.Parts().Cockpit!.Eye;
    public override float EyeHeight => FirstPersonEye.Y;
    /// <summary>Stepped onto from the left, beside the platform over the hinge.</summary>
    public override Vector3 EntryPoint => Flip(new Vector3(CompactRollerLayout.FrameHalf + 0.45f, 0f, -0.1f));
    public override bool ExitLeft => true;
    public override bool DrivenFromInside => false;

    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    private static Aabb NodeBox(float x, float y0, float y1, float z0, float z1) =>
        new(new Vector3(-x, y0, -z1), new Vector3(x * 2f, y1 - y0, z1 - z0));

    /// <summary>Both frames and drums low, the engine hood and the platform high; the roll bar in neither.</summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes => (
        NodeBox(CompactRollerLayout.FrameHalf, 0.2f, CompactRollerLayout.DrumRadius * 2f, CompactRollerLayout.Tail, CompactRollerLayout.Nose),
        NodeBox(CompactRollerLayout.FrameHalf, CompactRollerLayout.DrumRadius * 2f, CompactRollerLayout.HoodTop, CompactRollerLayout.Tail, 0.4f));

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var box = NodeBox(CompactRollerLayout.FrameHalf, 0f, CompactRollerLayout.HoodTop, CompactRollerLayout.Tail, CompactRollerLayout.Nose);
            return (box.GetCenter(), box.Size);
        }
    }

    public int PackFlags() => CompactRollerLayout.Pack(Articulation);

    public void UnpackFlags(int flags)
    {
        Articulation = CompactRollerLayout.Unpack(flags);
        Vibrating = false;
        Vibration = 0f;
    }

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        CompactRollerMeshBuilder.CreateRig(this, HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    public override Node3D BuildParkedVisual(int riderIndex) => CompactRollerMeshBuilder.CreateRig(this, null);

    // ---- the sound of the drums ----------------------------------------------------------------
    private static AudioStreamWav? _drumLoop;

    /// <summary>The drums vibrating, a 2 s seamless loop (<see cref="Audio.SiteSfx.RollerDrum"/>).</summary>
    public static AudioStreamWav DrumLoop => _drumLoop ??= Audio.Dsp.Loop(2f, 614, Audio.SiteSfx.RollerDrum);

    // ---- the engine ------------------------------------------------------------------------
    public const float IdleRpm = 1000f, Redline = 2800f;
    private Audio.EngineProfile? _sound;
    public float Rpm => Mathf.Lerp(IdleRpm, Redline, Mathf.Max(_throttle, Vibration * 0.85f));
    public float Rpm01 => (Rpm - IdleRpm) / (Redline - IdleRpm);
    public int Gear => _signed < -0.1f ? -1 : 1;
    public float Throttle => Mathf.Max(_throttle, Vibration * 0.6f);
    public Audio.EngineProfile Sound => _sound ??= Audio.EngineProfile.For(Audio.EngineLayout.Diesel6, IdleRpm, Redline);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        // signed speed along the rear frame: the motion's is a magnitude, reversing is Slip = π
        float v = _signed;
        if (Mathf.Abs(Mathf.Abs(v) - motion.Speed) > 0.5f) v = motion.Speed * (Mathf.Abs(MathX.WrapAngle(motion.Slip)) > 1.5f ? -1f : 1f);
        float brake = Mathf.Max(input.Brake, input.Handbrake ? 1f : 0f);
        float a = 0f;
        if (input.Throttle > 0.05f)
            a = v < -0.1f ? CompactRollerLayout.BrakeDecel * input.Throttle : CompactRollerLayout.Accel * input.Throttle;
        else if (brake > 0.05f)
            a = v > 0.1f ? -CompactRollerLayout.BrakeDecel * brake : -CompactRollerLayout.Accel * brake;
        if (ground.OnFloor) a += SlopeAccel(ground.Grade) * Mathf.Sign(v == 0f ? 1f : v);
        v += a * dt;
        // a hydrostatic drive holds it: let go of the stick and it stops
        if (input.Throttle < 0.05f && brake < 0.05f) v = Mathf.MoveToward(v, 0f, CompactRollerLayout.BrakeDecel * dt);
        v = Mathf.Clamp(v, -CompactRollerLayout.TopReverse, CompactRollerLayout.TopSpeed);
        _signed = v;

        Articulation = Mathf.MoveToward(Articulation, -input.Steer * CompactRollerLayout.MaxArticulation,
            CompactRollerLayout.ArticulationRate * dt);
        float yawRate = CompactRollerLayout.YawRate(v, Articulation);
        motion.Yaw += yawRate * dt;
        motion.YawRate = yawRate;
        motion.Speed = Mathf.Abs(v);
        motion.Slip = v < 0f ? Mathf.Pi : 0f;
        motion.Lean = 0f;
        _throttle = Mathf.Max(input.Throttle, brake * (v < 0.1f ? 1f : 0f));
        _brake = brake;
        Vibration = Mathf.MoveToward(Vibration, Vibrating ? 1f : 0f, dt / CompactRollerLayout.VibrationRamp);
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt) => Dress(visual, _signed, dt);

    private void Dress(Node3D visual, float speed, float dt)
    {
        _spin += speed / CompactRollerLayout.DrumRadius * dt;
        CompactRollerMeshBuilder.FrontOf(visual)?.Pose(Articulation, _spin);
        if (CompactRollerMeshBuilder.DrumsOf(visual) is { } drums)
        {
            bool on = Vibration > 0.01f;
            if (on && !drums.Playing) drums.Play();
            else if (!on && drums.Playing) drums.Stop();
            if (on) drums.VolumeDb = Mathf.LinearToDb(Vibration) - 4f;
        }
        if (visual is not HeavyRig rig) return;
        rig.WheelSpin = _spin;
        rig.SpeedKmh = Mathf.Abs(speed) * 3.6f;
        rig.Rpm = Rpm;
        rig.Throttle = Throttle;
        rig.Brake = _brake;
        rig.BrakeLights = _brake > 0.05f;
        rig.ReverseLights = speed < -0.1f;
        rig.Gear = speed < -0.1f ? "R" : Vibrating ? "V" : "1";
    }

    /// <summary>Remote copies: the frame's bend, how hard the drums vibrate, and the signed speed (the drums turn by it).</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(Articulation, Vibration, _signed, 0f);

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (pose != Vector4.Zero)
        {
            Articulation = Mathf.Clamp(pose.X, -CompactRollerLayout.MaxArticulation, CompactRollerLayout.MaxArticulation);
            Vibration = Mathf.Clamp(pose.Y, 0f, 1f);
            Vibrating = Vibration > 0.5f;
        }
        Dress(visual, pose.Z, dt);
    }
}
