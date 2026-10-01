using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Accumulates flat-shaded, vertex-coloured geometry and bakes it into one <see cref="ArrayMesh"/>.
///
/// <para>
/// <b>Author facing +Z; <see cref="Build"/> emits facing −Z</b>, which is what a Godot node
/// expects. See the note there — getting this wrong does not look like a modelling mistake, it
/// looks like the vehicle is in reverse.
/// </para>
///
/// <para>
/// Everything here is built from two primitives — a tapered tube between two points, and a box.
/// A bicycle frame is tubes, a limb is a tube, a torso is a box: at this fidelity there is
/// nothing else worth having. Keeping to two primitives is also what keeps the whole avatar in
/// a single surface and therefore a single draw call.
/// </para>
///
/// <para>
/// Normals are left for Godot to compute per face rather than smoothed, because the flat facets
/// <i>are</i> the look. Colours are baked per vertex and converted to linear here — Godot
/// converts sRGB automatically for shader uniforms marked <c>source_color</c> but never for raw
/// vertex colours, and skipping it washes every dark colour out.
/// </para>
/// </summary>
public sealed class MeshScratch
{
    private readonly List<Vector3> _vertices = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();
    // panes go in a second surface, so they can take a translucent material of their own
    private readonly List<Vector3> _glassVertices = new();
    private readonly List<Color> _glassColors = new();
    private readonly List<int> _glassIndices = new();

    /// <summary>The name <see cref="Build(Vector3)"/> gives the surface <see cref="Pane"/> fills.</summary>
    public const string GlassSurface = "glass";

    public int TriangleCount => (_indices.Count + _glassIndices.Count) / 3;

    /// <summary>
    /// A tapered tube from <paramref name="a"/> to <paramref name="b"/>. Six sides by default:
    /// enough that a bicycle tube does not read as a plank, few enough to stay in period.
    /// </summary>
    public void Tube(Vector3 a, Vector3 b, float radiusA, float radiusB, Color colour, int sides = 6)
    {
        var axis = b - a;
        float length = axis.Length();
        if (length < 1e-5f || sides < 3) return;

        axis /= length;

        // any vector not parallel to the axis will do for the first perpendicular
        var reference = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        var u = axis.Cross(reference).Normalized();
        var v = axis.Cross(u);

        int start = _vertices.Count;
        var linear = colour.SrgbToLinear();

        for (int i = 0; i < sides; i++)
        {
            float angle = Mathf.Tau * i / sides;
            var offset = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
            Add(a + offset * radiusA, linear);
            Add(b + offset * radiusB, linear);
        }

        for (int i = 0; i < sides; i++)
        {
            int p = start + i * 2;
            int q = start + ((i + 1) % sides) * 2;
            Quad(p, p + 1, q + 1, q);
        }

        // caps, so a limb does not show its hollow interior when seen end-on; clockwise from
        // outside like the sides (they used to be the other way round, facing into the tube)
        CapFan(start, sides, evenOffset: 0, flip: false, linear);
        CapFan(start, sides, evenOffset: 1, flip: true, linear);
    }

    public void Tube(Vector3 a, Vector3 b, float radius, Color colour, int sides = 6) =>
        Tube(a, b, radius, radius, colour, sides);

    /// <summary>
    /// A tapered tube with no ends, each side wound both ways so it is seen from inside as well as
    /// out: a skirt, a flared sleeve (#251). Faces whose middle points within
    /// <paramref name="gapAngle"/> radians of <paramref name="gap"/> are left out, which makes a
    /// slit. Not closed, so like <see cref="Pane"/> it has no volume for <c>--meshcheck</c>.
    /// </summary>
    public void Skirt(Vector3 a, Vector3 b, float radiusA, float radiusB, Color colour, int sides = 10,
        Vector3 gap = default, float gapAngle = 0f)
    {
        var axis = b - a;
        float length = axis.Length();
        if (length < 1e-5f || sides < 3) return;
        axis /= length;

        // the same frame as Tube, so a skirt's facets line up with the body's
        var reference = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        var u = axis.Cross(reference).Normalized();
        var v = axis.Cross(u);
        var slit = gap - axis * gap.Dot(axis);
        bool open = gapAngle > 0f && slit.LengthSquared() > 1e-8f;
        if (open) slit = slit.Normalized();

        int start = _vertices.Count;
        var linear = colour.SrgbToLinear();
        for (int i = 0; i < sides; i++)
        {
            float angle = Mathf.Tau * i / sides;
            var offset = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
            Add(a + offset * radiusA, linear);
            Add(b + offset * radiusB, linear);
        }
        for (int i = 0; i < sides; i++)
        {
            if (open)
            {
                float mid = Mathf.Tau * (i + 0.5f) / sides;
                if ((u * Mathf.Cos(mid) + v * Mathf.Sin(mid)).AngleTo(slit) < gapAngle) continue;
            }
            int p = start + i * 2;
            int q = start + ((i + 1) % sides) * 2;
            Quad(p, p + 1, q + 1, q);   // outside
            Quad(p, q, q + 1, p + 1);   // inside
        }
    }

    /// <summary>An axis-aligned box, optionally rotated about its own centre.</summary>
    public void Box(Vector3 centre, Vector3 size, Color colour, Basis? orientation = null)
    {
        var basis = orientation ?? Basis.Identity;
        var half = size * 0.5f;
        int start = _vertices.Count;
        var linear = colour.SrgbToLinear();

        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
            Add(centre + basis * corner, linear);
        }

        // 0=---, 1=+--, 2=-+-, 3=++-, 4=--+, 5=+-+, 6=-++, 7=+++
        // Clockwise seen from outside, which is Godot's front face. The order used to be the
        // reverse, so every box was drawn inside out: from outside you saw its far inner walls,
        // which passes on a plain block but shows a head straight through a helmet.
        Quad(start + 0, start + 1, start + 3, start + 2);   // back
        Quad(start + 4, start + 6, start + 7, start + 5);   // front
        Quad(start + 0, start + 2, start + 6, start + 4);   // left
        Quad(start + 1, start + 5, start + 7, start + 3);   // right
        Quad(start + 2, start + 3, start + 7, start + 6);   // top
        Quad(start + 0, start + 4, start + 5, start + 1);   // bottom
    }

    /// <summary>
    /// A flat ring in the plane whose normal is <paramref name="normal"/> — a wheel rim, or a
    /// tyre, depending on how thick you make it.
    /// </summary>
    public void Ring(Vector3 centre, Vector3 normal, float innerRadius, float outerRadius,
        float thickness, Color colour, int segments = 16)
    {
        normal = normal.Normalized();
        var reference = Mathf.Abs(normal.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        var u = normal.Cross(reference).Normalized();
        var v = normal.Cross(u);
        var half = normal * (thickness * 0.5f);

        int start = _vertices.Count;
        var linear = colour.SrgbToLinear();

        for (int i = 0; i < segments; i++)
        {
            float angle = Mathf.Tau * i / segments;
            var radial = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
            Add(centre + radial * innerRadius - half, linear);
            Add(centre + radial * outerRadius - half, linear);
            Add(centre + radial * outerRadius + half, linear);
            Add(centre + radial * innerRadius + half, linear);
        }

        for (int i = 0; i < segments; i++)
        {
            int p = start + i * 4;
            int q = start + ((i + 1) % segments) * 4;
            Quad(p + 0, p + 1, q + 1, q + 0);   // inner-to-outer, one face
            Quad(p + 1, p + 2, q + 2, q + 1);   // outer rim
            Quad(p + 2, p + 3, q + 3, q + 2);   // the other face
            Quad(p + 3, p + 0, q + 0, q + 3);   // inner rim
        }
    }

    /// <summary>
    /// A flat convex polygon, <paramref name="corners"/> in order round its edge, seen from both
    /// sides: a window. It goes in the mesh's second surface (<see cref="GlassSurface"/>), for a
    /// translucent material. One sheet rather than a thin box, because behind glass you can see
    /// through, a box's far face tints everything a second time.
    /// </summary>
    public void Pane(ReadOnlySpan<Vector3> corners, Color colour)
    {
        if (corners.Length < 3) return;
        int start = _glassVertices.Count;
        var linear = colour.SrgbToLinear();
        foreach (var c in corners)
        {
            _glassVertices.Add(c);
            _glassColors.Add(linear);
        }
        for (int i = 1; i < corners.Length - 1; i++)
        {
            _glassIndices.Add(start); _glassIndices.Add(start + i); _glassIndices.Add(start + i + 1);
            _glassIndices.Add(start); _glassIndices.Add(start + i + 1); _glassIndices.Add(start + i);
        }
    }

    /// <summary>
    /// Bakes the geometry, turning it to face <b>−Z</b> on the way out.
    ///
    /// <para>
    /// Everything here is authored facing +Z, because that is the readable direction to think in
    /// while placing a saddle at "0.79 m forward". Godot's convention is the opposite: a Node3D
    /// faces −Z. Drop a +Z mesh into a node and the model points the way the node came from — the
    /// body travels correctly and the machine is turned around, which from a chase camera reads
    /// unmistakably as riding backwards. It is subtle enough to survive a preview turntable,
    /// where there is no direction of travel to contradict it.
    /// </para>
    ///
    /// <para>
    /// So the flip happens once, here, rather than at each of the four places a figure is
    /// parented to a node. A half turn about Y is a proper rotation, so the winding — and
    /// therefore the backface culling — is untouched.
    /// </para>
    /// </summary>
    public ArrayMesh Build() => Build(Vector3.Zero);

    /// <summary>
    /// As <see cref="Build()"/>, with <paramref name="pivot"/> (authored space, facing +Z) as the
    /// mesh's origin: for a part that swings on a hinge, authored in place with the rest of the
    /// machine. Its node goes at the pivot turned the same way, <c>(−x, y, −z)</c>.
    /// </summary>
    public ArrayMesh Build(Vector3 pivot)
    {
        var mesh = new ArrayMesh();
        AddSurface(mesh, _vertices, _colors, _indices, pivot, "body");
        AddSurface(mesh, _glassVertices, _glassColors, _glassIndices, pivot, GlassSurface);
        return mesh;
    }

    private static void AddSurface(ArrayMesh mesh, List<Vector3> vertices, List<Color> colors, List<int> indices,
        Vector3 pivot, string name)
    {
        if (indices.Count == 0) return;
        var facing = new Vector3[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i] - pivot;
            facing[i] = new Vector3(-v.X, v.Y, -v.Z);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = facing;
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetName(mesh.GetSurfaceCount() - 1, name);
    }

    /// <summary>
    /// Gives <paramref name="instance"/> <paramref name="body"/> on its solid surface and
    /// <paramref name="glass"/> on its panes (a mesh from <see cref="Build(Vector3)"/>).
    /// </summary>
    public static void Paint(MeshInstance3D instance, Material body, Material glass)
    {
        instance.MaterialOverride = null;
        if (instance.Mesh is not ArrayMesh mesh) return;
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
            instance.SetSurfaceOverrideMaterial(i, mesh.SurfaceGetName(i) == GlassSurface ? glass : body);
    }

    /// <summary>
    /// Volume enclosed by the triangles, signed by their winding: negative when every face is
    /// clockwise seen from outside (Godot's front face), positive when the mesh is inside out.
    /// </summary>
    public float SignedVolume
    {
        get
        {
            float sum = 0;
            for (int i = 0; i < _indices.Count; i += 3)
                sum += _vertices[_indices[i]].Dot(_vertices[_indices[i + 1]].Cross(_vertices[_indices[i + 2]]));
            return sum / 6f;
        }
    }

    /// <summary>
    /// <c>--meshcheck</c>: each primitive, alone and off the origin, must enclose its own volume
    /// with every face clockwise from outside — an inside-out primitive shows its far inner walls
    /// and nothing tucked into it is hidden (#54). Non-zero exit on the first one that is not.
    /// </summary>
    public static int Check()
    {
        var at = new Vector3(0.3f, 1.1f, -0.7f);
        var cases = new (string Name, Action<MeshScratch> Draw, float Volume)[]
        {
            ("box", m => m.Box(at, new Vector3(0.4f, 0.6f, 0.8f), Colors.White), 0.4f * 0.6f * 0.8f),
            ("turned box", m => m.Box(at, new Vector3(0.4f, 0.6f, 0.8f), Colors.White, new Basis(Vector3.Up, 0.6f)), 0.4f * 0.6f * 0.8f),
            ("tube", m => m.Tube(at, at + new Vector3(0.2f, 0.9f, 0.1f), 0.1f, Colors.White),
                Polygon(6, 0.1f) * new Vector3(0.2f, 0.9f, 0.1f).Length()),
            ("tapered tube", m => m.Tube(at, at + Vector3.Back, 0.2f, 0.1f, Colors.White),
                (Polygon(6, 0.2f) + Polygon(6, 0.1f) + Mathf.Sqrt(Polygon(6, 0.2f) * Polygon(6, 0.1f))) / 3f),
            ("ring", m => m.Ring(at, Vector3.Right, 0.2f, 0.3f, 0.1f, Colors.White),
                (Polygon(16, 0.3f) - Polygon(16, 0.2f)) * 0.1f),
        };
        int failed = 0;
        foreach (var (name, draw, volume) in cases)
        {
            var m = new MeshScratch();
            draw(m);
            float v = m.SignedVolume;
            // one face the wrong way round takes its share off twice, so the total is off
            bool ok = Mathf.Abs(-v - volume) < 1e-4f;
            GD.Print($"[meshcheck] {name,-13} signed volume {v:F5} (expect -{volume:F5})  {(ok ? "ok" : "INSIDE OUT")}");
            if (!ok) failed++;
        }
        GD.Print(failed == 0 ? "[meshcheck] RESULT: every primitive faces out" : $"[meshcheck] RESULT: FAILED — {failed} inside out");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Area of a regular polygon of <paramref name="sides"/> with corners at <paramref name="radius"/>.</summary>
    private static float Polygon(int sides, float radius) => sides / 2f * radius * radius * Mathf.Sin(Mathf.Tau / sides);

    private void Add(Vector3 position, Color linear)
    {
        _vertices.Add(position);
        _colors.Add(linear);
    }

    private void Quad(int a, int b, int c, int d)
    {
        _indices.Add(a); _indices.Add(b); _indices.Add(c);
        _indices.Add(a); _indices.Add(c); _indices.Add(d);
    }

    private void CapFan(int start, int sides, int evenOffset, bool flip, Color linear)
    {
        // reuse the ring vertices; a fan off vertex 0 is fine for a convex polygon
        for (int i = 1; i < sides - 1; i++)
        {
            int a = start + evenOffset;
            int b = start + i * 2 + evenOffset;
            int c = start + (i + 1) * 2 + evenOffset;
            if (flip) (b, c) = (c, b);
            _indices.Add(a); _indices.Add(b); _indices.Add(c);
        }
    }
}
