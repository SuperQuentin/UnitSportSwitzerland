using Godot;
using UnitSport.Birds;

namespace UnitSport.Player;

/// <summary>
/// Play as a feral pigeon (#217): worn like the paraglider (not a vehicle), flown by
/// <see cref="PigeonFlight"/>, perched on the <see cref="TownPerches"/> of #143 and dropping on
/// people through the same <c>Dropping</c> path as the town birds (<c>FootPlayer.Pigeon.cs</c>).
/// Others see the bird itself at bird scale. No items while a pigeon (<c>ItemController.UsablePlayer</c>
/// is on foot only); they are kept and come back on foot.
/// </summary>
public sealed class Pigeon : Flyer
{
    private static BirdSpecies? _species;
    /// <summary>The town pigeon of the bird catalogue: its mesh, length and wing beat.</summary>
    public static BirdSpecies Species => _species ??= System.Array.Find(BirdCatalog.All, s => s.Name == "Rock Dove")!;

    private static readonly NodePath WingP = "Bird/WingP", WingN = "Bird/WingN";
    private float _phase;

    public override RideKind Kind => RideKind.Pigeon;
    public override string Label => "Pigeon";
    public override string Blurb =>
        "{jump} flap, let go to glide, {crouch_slide} dive, {move_left}/{move_right} turn; {move_back} slows: glide slowly onto a roof or ledge to perch; {fire} drop";

    public override float EyeHeight => 0.22f;
    public override Vector3 FirstPersonEye => new(0, EyeHeight, -0.12f);
    public override float BodyRadius => 0.12f;
    public override float BodyHeight => 0.3f;
    /// <summary>A pigeon bounces off a wall; nothing it flies into is a crash.</summary>
    public override float CrashSpeed => 60f;
    public override float LookBank => 0.8f;
    public override float CameraDistance => 1.9f;
    public override float CameraHeight => 0.45f;
    public override float CameraPivot => 0.15f;
    public override bool CameraFollowsPitch => false;
    public override Vector3 Pivot => new(0, 0.12f, 0);
    public override (float Calm, float Fast) Thrill => (8f, 22f);
    public override float BaseFov => 75f;
    public override float MaxFov => 95f;
    public override float FovSpeed => 25f;

    public override Node3D BuildVisual(int riderIndex, Avatar.Outfit outfit = default)
    {
        var parts = BirdMesh.Get(Species);
        var root = new Node3D { Name = "Pigeon" };
        // bird meshes face +Z, a body faces −Z (BirdLife turns its birds by yaw + π the same way)
        var bird = new Node3D { Name = "Bird", Rotation = new Vector3(0, Mathf.Pi, 0) };
        root.AddChild(bird);
        bird.AddChild(new MeshInstance3D { Mesh = parts.Body, MaterialOverride = BirdMesh.Material });
        foreach (var wing in new[] { parts.WingA, parts.WingB })
            bird.AddChild(new MeshInstance3D
            {
                // the half turn in MeshScratch.Build decides which side a wing ends up on; ask the mesh
                Name = wing.GetAabb().GetCenter().X >= 0 ? "WingP" : "WingN",
                Mesh = wing,
                MaterialOverride = BirdMesh.Material,
                Position = parts.Shoulder,
                Visible = false,
            });
        return root;
    }

    public static PigeonFlight.State ToState(in FlightMotion m) => new()
    {
        Velocity = m.Velocity, Yaw = m.Yaw, Bank = m.Bank, Flap = m.Spool, Mode = (PigeonFlight.Mode)(int)m.Control,
    };

    public static void FromState(in PigeonFlight.State s, ref FlightMotion m)
    {
        m.Velocity = s.Velocity;
        m.Yaw = s.Yaw;
        m.Bank = s.Bank;
        m.Spool = s.Flap;
        m.Control = (int)s.Mode;
    }

    public static PigeonFlight.Mode ModeOf(in FlightMotion m) => (PigeonFlight.Mode)(int)m.Control;

    public override void Begin(ref FlightMotion m, Vector3 velocity, float yaw)
    {
        base.Begin(ref m, velocity, yaw);
        m.Control = (int)PigeonFlight.Mode.Ground;
    }

    public override FlightEvent Fly(in FlightInput input, in FlightEnv env, float dt, ref FlightMotion m)
    {
        var c = new PigeonFlight.Controls(input.Stick, input.Up > 0.5f, input.Down > 0.5f, input.Effort);
        var s = PigeonFlight.Step(ToState(m), c, env.OnFloor, dt);
        FromState(s, ref m);
        // the body leans with the turn and noses down in a dive, level on the ground
        float pitch = s.Mode == PigeonFlight.Mode.Air ? Mathf.Clamp(s.Velocity.Y / 20f, -0.6f, 0.4f) : 0f;
        m.Attitude = new Basis(Vector3.Up, m.Yaw) * new Basis(Vector3.Right, pitch) * new Basis(Vector3.Back, -m.Bank);
        return FlightEvent.None;
    }

    /// <summary>Spool = wing beat 0..1, Control = mode: the same two numbers a remote copy gets.</summary>
    public override void AnimateFlight(Node3D visual, in FlightMotion m, float dt)
    {
        bool air = ModeOf(m) == PigeonFlight.Mode.Air;
        float hz = 2.5f * Mathf.Pow(Species.Length, -0.7f);
        _phase = Mathf.PosMod(_phase + Mathf.Tau * hz * dt * Mathf.Max(m.Spool, 0.15f), Mathf.Tau);
        // flapping beats round the stroke; gliding holds the wings out, a touch raised
        float angle = Mathf.Lerp(0.25f, Mathf.Sin(_phase) * 0.9f, m.Spool);
        Wing(visual, WingP, air, angle);
        Wing(visual, WingN, air, -angle);
    }

    private static void Wing(Node3D visual, NodePath path, bool shown, float angle)
    {
        if (visual.GetNodeOrNull<Node3D>(path) is not { } wing) return;
        wing.Visible = shown;
        wing.Rotation = new Vector3(0, 0, angle);
    }
}
