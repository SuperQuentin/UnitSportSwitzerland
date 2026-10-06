using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// A tracked mini dumper (#614, RideKind 200): rubber tracks that counter-rotate to turn it on the
/// spot, as the excavator's, and a skip at the front that tips forward on the tipper's action
/// (<c>destination</c>: N, the VR console's button), stopped, and comes back down again. The skip
/// is the heavy rig's tipping node (#677), its state one bit in the pose and in the parked flags.
/// See <c>docs/notes/vehicles/mini-dumper.md</c>.
/// </summary>
public sealed class MiniDumper : Rideable, IEngined
{
    public override RideKind Kind => RideKind.MiniDumper;
    public override string Label => "Mini dumper";
    public override string Blurb =>
        "{move_forward}{move_back} tracks, {move_left}{move_right} turn on the spot; stopped, {destination} tips the skip and brings it down";

    /// <summary>The skip tipped forward.</summary>
    public bool Tipped { get; set; }

    private float _signed, _yawRate, _throttle, _spin;

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 150f;
    public override float BodyRadius => 0.6f;
    public override float BodyHeight => 1.4f;
    public override float DismountSpeed => 0.8f;
    public override float ChaseDistance => 6f;
    public override float ChaseHeight => 2.6f;
    public override float ChasePitch => -0.3f;
    public override float BaseFov => 68f;
    public override float MaxFov => 72f;
    public override float FovSpeed => 4f;

    private static Vector3? _eye;
    public override Vector3 FirstPersonEye => _eye ??= MiniDumperMeshBuilder.Parts().Cockpit!.Eye;
    public override float EyeHeight => FirstPersonEye.Y;
    /// <summary>Stepped onto from the left, beside the seat.</summary>
    public override Vector3 EntryPoint => Flip(new Vector3(MiniDumperLayout.HalfWidth + 0.4f, 0f, -0.6f));
    public override bool ExitLeft => true;
    public override bool DrivenFromInside => false;

    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    private static Aabb NodeBox(float x, float y0, float y1, float z0, float z1) =>
        new(new Vector3(-x, y0, -z1), new Vector3(x * 2f, y1 - y0, z1 - z0));

    /// <summary>The tracks and chassis low, the hood and the skip high; the roll bar in neither.</summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes => (
        NodeBox(MiniDumperLayout.HalfWidth, 0.1f, MiniDumperLayout.DeckTop, -MiniDumperLayout.TrackHalfLength, MiniDumperLayout.TrackHalfLength),
        NodeBox(MiniDumperLayout.HalfWidth, MiniDumperLayout.DeckTop, MiniDumperLayout.HoodTop, MiniDumperLayout.Tail, MiniDumperLayout.SkipFront));

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var box = NodeBox(MiniDumperLayout.HalfWidth, 0f, MiniDumperLayout.SkipTop, MiniDumperLayout.Tail, MiniDumperLayout.SkipFront);
            return (box.GetCenter(), box.Size);
        }
    }

    public int PackFlags() => MiniDumperLayout.Pack(Tipped);
    public void UnpackFlags(int flags) => Tipped = MiniDumperLayout.Unpack(flags);

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        MiniDumperMeshBuilder.CreateRig(this, HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    public override Node3D BuildParkedVisual(int riderIndex) => MiniDumperMeshBuilder.CreateRig(this, null);

    // ---- the engine ------------------------------------------------------------------------
    public const float IdleRpm = 1200f, WorkRpm = 3000f;
    private Audio.EngineProfile? _sound;
    public float Rpm => Mathf.Lerp(IdleRpm, WorkRpm, _throttle);
    public float Rpm01 => (Rpm - IdleRpm) / (WorkRpm - IdleRpm);
    public int Gear => _signed < -0.1f ? -1 : 1;
    public float Throttle => _throttle;
    public Audio.EngineProfile Sound => _sound ??= Audio.EngineProfile.For(Audio.EngineLayout.Diesel6, IdleRpm, WorkRpm + 200f);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        // throttle forward, brake back: a crawler has no brake to speak of, letting go stops it
        float target = (input.Throttle - Mathf.Max(input.Brake, input.Handbrake ? 1f : 0f)) * MiniDumperLayout.TopSpeed;
        // the tracks counter-rotate to turn, so it turns as well standing as moving
        float turn = -input.Steer * MiniDumperLayout.TurnRate;
        _throttle = Mathf.Max(Mathf.Abs(input.Throttle), Mathf.Abs(input.Brake));
        _signed = Mathf.MoveToward(_signed, target, MiniDumperLayout.Accel * 2f * dt);
        _yawRate = Mathf.MoveToward(_yawRate, turn, MiniDumperLayout.TurnRate * 3f * dt);
        motion.Yaw += _yawRate * dt;
        motion.YawRate = _yawRate;
        motion.Speed = Mathf.Abs(_signed);
        motion.Slip = _signed < 0f ? Mathf.Pi : 0f;
        motion.Lean = 0f;
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt) => Dress(visual, _signed, dt);

    private void Dress(Node3D visual, float speed, float dt)
    {
        _spin += speed / MiniDumperMeshBuilder.RollerRadius * dt;
        if (visual is not HeavyRig rig) return;
        rig.Tipped = Tipped;
        rig.WheelSpin = _spin;
        rig.SpeedKmh = Mathf.Abs(speed) * 3.6f;
        rig.Rpm = Rpm;
        rig.Throttle = Throttle;
        rig.ReverseLights = speed < -0.1f;
        rig.Gear = speed < -0.1f ? "R" : Tipped ? "T" : "1";
    }

    /// <summary>Remote copies: the skip (1 tipped, else 0), and the signed speed the rollers turn by.</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(Tipped ? 1f : 0f, _signed, 0f, 0f);

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        Tipped = pose.X > 0.5f;
        Dress(visual, pose.Y, dt);
    }
}
