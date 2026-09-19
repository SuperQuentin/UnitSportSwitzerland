using Godot;

namespace UnitSport.Gpx;

/// <summary>
/// A big comic-strip "HERE" arrow that pops in above the runner's head whenever the active
/// camera — whichever mode it is in — has drifted far enough away that the runner is hard to
/// pick out. A wide Locked-off tripod or a Free-flown spectator camera are both places this
/// matters; the trigger is pure distance, not which mode asked.
///
/// <para>
/// Billboarded, so it always faces the lens regardless of where the camera ended up — the whole
/// point is legibility from any angle, not a prop with a "correct" side. Drawn with depth testing
/// off, so it reads through the terrain, a tree or a building exactly when that is what is
/// hiding the runner in the first place.
/// </para>
/// </summary>
public partial class AttentionArrow : Node3D
{
    /// <summary>Distance beyond which the runner counts as hard to spot.</summary>
    private const float ShowBeyond = 35f;

    /// <summary>
    /// Comes back below this, well short of <see cref="ShowBeyond"/> — the gap is hysteresis, so
    /// a runner drifting back and forth across one fixed threshold does not flicker the arrow on
    /// and off several times a second.
    /// </summary>
    private const float HideBelow = 25f;

    /// <summary>How fast the pop-in/out eases, per second.</summary>
    private const float FadeRate = 4f;

    private MeshInstance3D _mesh = null!;
    private Label3D _label = null!;
    private float _shown;      // 0..1, eased
    private float _bob;

    /// <summary>Master on/off. Disabling eases the arrow out rather than snapping it away.</summary>
    public bool Enabled { get; set; } = true;

    public static AttentionArrow Create() => new() { Name = "AttentionArrow" };

    public override void _Ready()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.98f, 0.16f, 0.10f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            // Full spherical, not fixed-Y: Godot's "enabled" billboard is screen-aligned rather
            // than a look-at, so it copies the CAMERA's own right/up vectors directly onto the
            // quad instead of rotating toward it - there is no degenerate pole the way a look-at
            // billboard has, so it reads correctly even from TopDown, straight down. Fixed-Y was
            // tried first and is worse here: it only rotates around the vertical axis, so viewed
            // from near-overhead - TopDown, a climbed DroneReveal - the plane turns edge-on and
            // "HERE" collapses into an unreadable sliver. Measured with --forceshot "Top down".
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = true,   // reads through scenery - it exists to find what scenery hides
            RenderPriority = 10,
        };
        _mesh = new MeshInstance3D { Mesh = BuildArrowMesh(), MaterialOverride = material };
        AddChild(_mesh);

        _label = new Label3D
        {
            Text = "HERE",
            // See the arrow mesh's material above for why this is full spherical, not fixed-Y.
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            RenderPriority = 11,
            FontSize = 140,
            OutlineSize = 26,
            Modulate = new Color(1f, 0.96f, 0.35f),
            OutlineModulate = new Color(0.05f, 0.05f, 0.05f),
            Position = new Vector3(0, 3.05f, 0),
        };
        AddChild(_label);

        Visible = false;
    }

    /// <summary>
    /// Down-pointing chevron: a short shaft above a wide triangular head, comic-strip
    /// proportions and big enough to read from well past <see cref="ShowBeyond"/>. Local origin
    /// sits at the tip, so positioning this node is just "float it above the head".
    /// </summary>
    private static ArrayMesh BuildArrowMesh()
    {
        var verts = new[]
        {
            // shaft — a thin quad, two triangles
            new Vector3(-0.5f, 2.7f, 0), new Vector3(0.5f, 2.7f, 0), new Vector3(-0.5f, 1.1f, 0),
            new Vector3(0.5f, 2.7f, 0), new Vector3(0.5f, 1.1f, 0), new Vector3(-0.5f, 1.1f, 0),
            // head — a wide triangle pointing down at the local origin
            new Vector3(-1.5f, 1.2f, 0), new Vector3(1.5f, 1.2f, 0), new Vector3(0, 0, 0),
        };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// Called once a frame with wherever the runner and the active camera actually are.
    /// <paramref name="dt"/> is screen seconds; pass 0 to snap instead of ease (seeking).
    /// </summary>
    public void UpdateFrame(Vector3 headWorld, Vector3 cameraWorld, float dt)
    {
        float dist = headWorld.DistanceTo(cameraWorld);
        bool want = Enabled && (_shown > 0.01f ? dist > HideBelow : dist > ShowBeyond);

        _shown = dt > 0
            ? Mathf.MoveToward(_shown, want ? 1f : 0f, dt * FadeRate)
            : (want ? 1f : 0f);

        Visible = _shown > 0.01f;
        if (!Visible) return;

        // a comic pop needs overshoot, not a linear fade - sin(shown * pi/2) eases into place
        // and the bob on top of it is what actually reads as "alive" rather than a static icon
        _bob += dt * 6f;
        float bounce = Mathf.Abs(Mathf.Sin(_bob)) * 0.2f;
        GlobalPosition = headWorld + Vector3.Up * (0.6f + bounce);
        Scale = Vector3.One * Mathf.Sin(_shown * Mathf.Pi * 0.5f);
    }
}
