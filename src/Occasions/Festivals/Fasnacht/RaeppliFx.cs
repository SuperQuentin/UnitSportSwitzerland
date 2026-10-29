using Godot;
using UnitSport.Items;

namespace UnitSport.Occasions.Festivals.Fasnacht;

/// <summary>
/// A handful of Räppli (#1357): the <see cref="ItemEventKind.Confetti"/> handler, run on the thrower's machine and on every peer the server relays it to. One
/// one-shot <see cref="GpuParticles3D"/> of 90 little paper squares thrown along the aim, tumbling and coloured from a ramp of the Fasnacht's colours, freed after
/// its lifetime: the paper is the GPU's, nothing is built per frame and no mesh is rebuilt. The mesh, the material and the process material are made once.
/// </summary>
public static class RaeppliFx
{
    public const int Pieces = 90;
    public const float Lifetime = 3.2f;

    /// <summary>Handfuls thrown on this machine since start (checks).</summary>
    public static int Count { get; private set; }

    private static QuadMesh? _quad;
    private static StandardMaterial3D? _paper;
    private static ParticleProcessMaterial? _process;

    private static QuadMesh Quad => _quad ??= new QuadMesh { Size = new Vector2(0.045f, 0.045f), Material = Paper };

    private static StandardMaterial3D Paper => _paper ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private static ParticleProcessMaterial Process => _process ??= Make();

    private static ParticleProcessMaterial Make()
    {
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(0.95f, 0.15f, 0.2f));
        ramp.SetColor(1, new Color(0.2f, 0.45f, 0.95f));
        ramp.AddPoint(0.25f, new Color(0.98f, 0.82f, 0.15f));
        ramp.AddPoint(0.5f, new Color(0.2f, 0.75f, 0.35f));
        ramp.AddPoint(0.75f, new Color(0.95f, 0.95f, 0.95f));
        return new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 0, -1),
            Spread = 38f,
            InitialVelocityMin = 3.5f,
            InitialVelocityMax = 7.5f,
            Gravity = new Vector3(0, -1.6f, 0),
            DampingMin = 1.6f,
            DampingMax = 2.4f,
            AngularVelocityMin = -360f,
            AngularVelocityMax = 360f,
            AngleMin = 0f,
            AngleMax = 360f,
            ScaleMin = 0.7f,
            ScaleMax = 1.4f,
            ColorInitialRamp = new GradientTexture1D { Gradient = ramp },
        };
    }

    /// <summary>The <see cref="ItemEventKind.Confetti"/> handler: the toss's sound and the paper, at the thrower's hand, thrown along the aim.</summary>
    public static void OnConfetti(ItemEvents n, ItemEvent e)
    {
        Count++;
        var (stream, pitch, jitter) = FasnachtSounds.RaeppliToss.Pick(Rng);
        n.Sound3D(e.Position, stream, pitch, -4f + jitter, unitSize: 6f, maxDistance: 80f);
        var dir = e.Direction.LengthSquared() > 1e-6f ? e.Direction.Normalized() : Vector3.Forward;
        var up = Mathf.Abs(dir.Y) > 0.95f ? Vector3.Forward : Vector3.Up;
        var burst = new GpuParticles3D
        {
            Name = "Raeppli", Amount = Pieces, Lifetime = Lifetime, OneShot = true, Explosiveness = 0.92f, Emitting = true,
            DrawPass1 = Quad, ProcessMaterial = Process, TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-12, -12, -12), new Vector3(24, 24, 24)),
        };
        n.AddChild(burst);
        // the process material throws along -Z: turn the node so -Z is the aim
        burst.GlobalTransform = new Transform3D(Basis.LookingAt(dir, up), e.Position);
        var end = burst.CreateTween();
        end.TweenInterval(Lifetime + 0.5);
        end.TweenCallback(Callable.From(burst.QueueFree));
    }

    private static readonly Random Rng = new(1357);
}
