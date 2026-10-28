using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Adventure.Together;

/// <summary>
/// A rope between two roped players (#1266 part 3.4): one shared unit-length tube (8 sides, 12 rings) stretched
/// between the two harness points by this node's transform each frame, its sag in the shader
/// (<c>shaders/rope.gdshader</c>, the <c>rope_sag</c> instance uniform, set only on a change of more than 5 cm).
/// One node per rope link, made and freed when the link book changes. Nothing rebuilds a mesh.
/// </summary>
public sealed partial class RopeNode : MeshInstance3D
{
    /// <summary>The rope's radius, m (a 10 mm single rope, drawn a little fat to be seen).</summary>
    public const float Radius = 0.012f;
    private static readonly StringName SagP = "rope_sag";
    private static ArrayMesh? _tube;
    private static ShaderMaterial? _material;
    private float _sag = -1f;

    /// <summary>The sag at the middle now, m (checks).</summary>
    public float Sag => _sag;

    /// <summary>The shared unit tube: y 0..1, radius 1, 12 rings of 8 sides.</summary>
    public static ArrayMesh UnitTube()
    {
        if (_tube != null) return _tube;
        var s = new MeshScratch();
        const int segments = 11;
        for (int i = 0; i < segments; i++)
            s.Tube(new Vector3(0, i / (float)segments, 0), new Vector3(0, (i + 1) / (float)segments, 0), 1f, Colors.White, sides: 8);
        return _tube = s.Build();
    }

    private static ShaderMaterial Material => _material ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/rope.gdshader") };

    public override void _Ready()
    {
        Mesh = UnitTube();
        MaterialOverride = Material;
        CastShadow = ShadowCastingSetting.Off;
        // the transform moves every frame: the box is the stretched unit tube's, plus the sag
        ExtraCullMargin = 2f;
    }

    /// <summary>
    /// Stretches the rope from <paramref name="a"/> to <paramref name="b"/> (world space); <paramref name="length"/> is the
    /// rope's own, so a slack rope sags by about <c>sqrt(3 L (S - L) / 8)</c>, at most 15 % of it.
    /// </summary>
    public void Place(Vector3 a, Vector3 b, float length)
    {
        var along = b - a;
        float span = along.Length();
        if (span < 0.05f)
        {
            if (Visible) Visible = false;
            return;
        }
        if (!Visible) Visible = true;
        var y = along;
        var x = y.Cross(Mathf.Abs(y.Normalized().Y) > 0.95f ? Vector3.Right : Vector3.Up).Normalized() * Radius;
        var z = x.Cross(y).Normalized() * Radius;
        GlobalTransform = new Transform3D(new Basis(x, y, z), a);
        float sag = SagOf(span, length);
        if (Mathf.Abs(sag - _sag) > 0.05f || sag == 0f && _sag != 0f)
        {
            _sag = sag;
            SetInstanceShaderParameter(SagP, sag);
        }
    }

    /// <summary>The sag at the middle of a rope of <paramref name="length"/> over <paramref name="span"/> (a parabola's approximation).</summary>
    public static float SagOf(float span, float length)
    {
        if (span >= length) return 0f;
        return Mathf.Min(0.15f * length, Mathf.Sqrt(3f * span * (length - span) / 8f));
    }

    /// <summary>The rope as the model viewer shows it: 3 m of it sagging between two harnesses, with their carabiners.</summary>
    [Showcase("Adventure", "Rope")]
    private static Node3D Showcase()
    {
        var root = new Node3D { Name = "RopeShowcase" };
        var left = new MeshInstance3D { Mesh = Harness(), Position = new Vector3(-1.5f, 0, 0), MaterialOverride = HumanMeshBuilder.Material() };
        var right = new MeshInstance3D { Mesh = Harness(), Position = new Vector3(1.5f, 0, 0), MaterialOverride = HumanMeshBuilder.Material() };
        root.AddChild(left);
        root.AddChild(right);
        var rope = new RopeNode();
        root.AddChild(rope);
        rope.Ready += () => rope.Place(new Vector3(-1.4f, 1.0f, 0), new Vector3(1.4f, 1.0f, 0), 3.4f);
        return root;
    }

    /// <summary>A sit harness round the hips (waist belt, two leg loops, the belay loop) with a screwgate carabiner on it.</summary>
    [Showcase("Adventure", "Harness")]
    public static ArrayMesh Harness()
    {
        var s = new MeshScratch();
        var belt = new Color("2a3f6a");
        var loop = new Color("e0a020");
        var metal = new Color("b8bcc4");
        s.Ring(new Vector3(0, 1.0f, 0), Vector3.Up, 0.15f, 0.18f, 0.05f, belt);
        s.Ring(new Vector3(-0.09f, 0.86f, 0.01f), new Vector3(0.15f, 1f, 0.1f), 0.07f, 0.09f, 0.04f, belt);
        s.Ring(new Vector3(0.09f, 0.86f, 0.01f), new Vector3(-0.15f, 1f, 0.1f), 0.07f, 0.09f, 0.04f, belt);
        s.Tube(new Vector3(0, 0.96f, 0.17f), new Vector3(0, 0.89f, 0.15f), 0.018f, loop);
        // the carabiner: a D of round bar, its gate's screw sleeve
        s.Ring(new Vector3(0, 0.86f, 0.17f), Vector3.Right, 0.025f, 0.037f, 0.012f, metal);
        s.Box(new Vector3(0, 0.84f, 0.205f), new Vector3(0.012f, 0.03f, 0.012f), metal);
        return s.Build();
    }
}
