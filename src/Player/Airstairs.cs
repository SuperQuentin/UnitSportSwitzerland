using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// A mobile airstairs truck (#417, RideKind 126): driven at walking-to-jogging pace, its platform
/// raised to an aircraft door's sill and its walkable deck (<see cref="AirstairsMeshBuilder.Deck"/>)
/// leading from the ground up to that door. Docking (<see cref="Vehicles.AirstairsDock"/>): driven
/// slowly within reach of a door and let go, it lines up and raises its platform to the sill;
/// parked, its height follows the sill. See <c>docs/notes/vehicles/airstairs.md</c>.
/// </summary>
public sealed class Airstairs : Rideable
{
    public override RideKind Kind => RideKind.Airstairs;
    public override string Label => "Airstairs";
    public override string Blurb =>
        "{move_forward} drive, {move_back} brake / reverse, {move_left}{move_right} steer; stop by an aircraft door and let go: it docks";

    /// <summary>The platform's height over the ground, m (what the deck and the model are built for).</summary>
    public float Height { get; set; } = AirstairsLayout.TravelHeight;

    /// <summary>Where the platform is going, m; eased at <see cref="LiftRate"/>.</summary>
    public float TargetHeight { get; set; } = AirstairsLayout.TravelHeight;

    /// <summary>The door it is docked at (aircraft's node name and door index), or null.</summary>
    public (string Host, int Door)? Docked { get; set; }

    /// <summary>How fast the platform rises and falls, m/s.</summary>
    public const float LiftRate = 0.45f;
    /// <summary>Top speed forward and in reverse, m/s (25 and 8 km/h).</summary>
    public const float TopSpeed = 6.9f, TopReverse = 2.2f;
    private const float Accel = 1.3f, BrakeDecel = 4.5f, Drag = 0.25f, MaxSteer = 0.6f;

    private float _steer;
    private float _signed;

    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override float MaxHealth => 160f;
    public override float BodyRadius => 0.85f;
    public override float BodyHeight => 1.7f;
    public override float DismountSpeed => 1.5f;
    public override float ChaseDistance => 11f;
    public override float ChaseHeight => 5.5f;
    public override float ChasePitch => -0.35f;
    public override float BaseFov => 68f;
    public override float MaxFov => 76f;
    public override float FovSpeed => 8f;
    public override Vector3 FirstPersonEye => Flip(AirstairsLayout.Eye);
    public override float EyeHeight => AirstairsLayout.Eye.Y;
    public override Vector3 EntryPoint => Flip(AirstairsLayout.CabDoor);
    public override bool ExitLeft => true;

    /// <summary>Got in by its cab door from outside, always: its deck is stairs, not a saloon with a wheel in it.</summary>
    public override bool DrivenFromInside => false;

    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    // ---- collision: the chassis and cab, and the stairs short of the platform's front ----------
    private static Aabb NodeBox(float x0, float x1, float y0, float y1, float z0, float z1) =>
        new(new Vector3(-x1, y0, -z1), new Vector3(x1 - x0, y1 - y0, z1 - z0));

    /// <summary>
    /// Chassis and cab low, the stairs high, both ending 0.75 m short of the lip: docked, the
    /// platform's front is over the sill, inside the aircraft's parked box, and a hull there would
    /// stop it short of the door.
    /// </summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes => (
        NodeBox(-AirstairsLayout.HalfWidth, AirstairsLayout.HalfWidth, 0.45f, AirstairsLayout.CabTop, AirstairsLayout.ChassisRear, AirstairsLayout.Bumper + 0.15f),
        NodeBox(-0.8f, 0.8f, AirstairsLayout.CabTop, Height + AirstairsLayout.RailHeight, AirstairsLayout.FootZ + 0.3f, AirstairsLayout.LipZ - 0.75f));

    public override (Vector3 Centre, Vector3 Size) ParkedBox
    {
        get
        {
            var box = NodeBox(-AirstairsLayout.HalfWidth, AirstairsLayout.HalfWidth, 0f, AirstairsLayout.CabTop,
                AirstairsLayout.ChassisRear, AirstairsLayout.Bumper + 0.15f);
            return (box.GetCenter(), box.Size);
        }
    }

    /// <summary>The stairs standing parked: a box from the cab's roof to the rails, short of the lip.</summary>
    public override IEnumerable<(Transform3D Pose, Vector3 Centre, Vector3 Size)> ExtraBoxes()
    {
        var box = NodeBox(-0.8f, 0.8f, AirstairsLayout.CabTop, Height + AirstairsLayout.RailHeight, AirstairsLayout.FootZ + 0.6f, AirstairsLayout.LipZ - 0.75f);
        yield return (Transform3D.Identity, box.GetCenter(), box.Size);
    }

    // ---- the deck, rebuilt when the platform has moved 5 mm --------------------------------
    private VehicleDeck[]? _decks;
    private float _deckHeight = float.NaN;

    /// <summary>The flight, platform and plate at the current height (a new array only when it changes).</summary>
    public override VehicleDeck[] Decks
    {
        get
        {
            if (_decks == null || Mathf.Abs(_deckHeight - Height) > 0.005f)
            {
                _deckHeight = Height;
                _decks = new[] { AirstairsMeshBuilder.Deck(Height) };
            }
            return _decks;
        }
    }

    // ---- flags: what a parked one keeps (its height, in cm) -----------------------------------
    public int PackFlags() => Mathf.RoundToInt(Mathf.Clamp(Height, 0f, 10f) * 100f);

    public void UnpackFlags(int flags)
    {
        if (flags <= 0) return;
        Height = TargetHeight = AirstairsLayout.Clamp(flags / 100f);
    }

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) => AirstairsRig.Create(Height);

    public override Node3D BuildParkedVisual(int riderIndex) => AirstairsRig.Create(Height);

    /// <summary>The platform toward its target, at the lift's rate.</summary>
    public void Lift(float dt) => Height = Mathf.MoveToward(Height, AirstairsLayout.Clamp(TargetHeight), LiftRate * dt);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        // signed speed along the nose: the motion's speed is a magnitude, reversing is Slip = π
        float v = _signed;
        if (Mathf.Abs(Mathf.Abs(v) - motion.Speed) > 0.5f) v = motion.Speed * (Mathf.Abs(MathX.WrapAngle(motion.Slip)) > 1.5f ? -1f : 1f);
        float brake = Mathf.Max(input.Brake, input.Handbrake ? 1f : 0f);
        float a = 0f;
        if (input.Throttle > 0.05f)
            a = v < -0.1f ? BrakeDecel * input.Throttle : Accel * input.Throttle;
        else if (brake > 0.05f)
            // the brake stops it, then reverses it (as a car's automatic does)
            a = v > 0.1f ? -BrakeDecel * brake : -Accel * 0.6f * brake;
        if (ground.OnFloor) a += SlopeAccel(ground.Grade) * Mathf.Sign(v == 0f ? 1f : v);
        v += a * dt;
        // rolling drag; parked on its brake when nothing is pressed and it has all but stopped
        v = Mathf.MoveToward(v, 0f, (Drag + (input.Throttle < 0.05f && brake < 0.05f && Mathf.Abs(v) < 0.6f ? 2f : 0f)) * dt);
        v = Mathf.Clamp(v, -TopReverse, TopSpeed);
        _signed = v;

        _steer = Mathf.MoveToward(_steer, input.Steer, 2.5f * dt);
        float yawRate = v * Mathf.Tan(-_steer * MaxSteer) / AirstairsLayout.Wheelbase;
        motion.Yaw += yawRate * dt;
        motion.YawRate = yawRate;
        motion.Speed = Mathf.Abs(v);
        motion.Slip = v < 0f ? Mathf.Pi : 0f;
        motion.Lean = 0f;
        Lift(dt);
    }

    /// <summary>Stops it dead (docked: lined up and held there).</summary>
    public void Hold(ref RideMotion motion)
    {
        _signed = 0f;
        motion.Speed = 0f;
        motion.Slip = 0f;
        motion.YawRate = 0f;
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is AirstairsRig rig) rig.Height = Height;
    }

    /// <summary>Remote copies: the platform's height and the steer.</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) => new(Height, _steer, 0f, 0f);

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (pose.X > 0.5f) Height = pose.X;
        if (visual is AirstairsRig rig) rig.Height = Height;
    }
}
