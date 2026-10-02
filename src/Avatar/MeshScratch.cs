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
/// No normals are written: the flat facets <i>are</i> the look. Colours are baked per vertex and
/// converted to linear here — Godot converts sRGB automatically for shader uniforms marked
/// <c>source_color</c> but never for raw vertex colours, and skipping it washes every dark colour
/// out.
/// </para>
///
/// <para>
/// <see cref="Smooth"/> is the exception (#311): the lit styles' figures, drawn with
/// <c>MeshDetail.High</c>, get rounder tubes and real normals for the cel light and the rim. A
/// scratch with any smooth primitive writes a normal for every vertex of its body surface; the
/// rest (a motorbike under its rider) keep the normal Godot gives a mesh that has none.
/// </para>
/// </summary>
public sealed class MeshScratch
{
    private readonly List<Vector3> _vertices = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();
    // filled only once a smooth primitive is drawn (see Smooth); otherwise no normals at all
    private readonly List<Vector3> _normals = new();
    // panes go in a second surface, so they can take a translucent material of their own
    private readonly List<Vector3> _glassVertices = new();
    private readonly List<Color> _glassColors = new();
    private readonly List<int> _glassIndices = new();

    /// <summary>The name <see cref="Build(Vector3)"/> gives the surface <see cref="Pane"/> fills.</summary>
    public const string GlassSurface = "glass";

    public int TriangleCount => (_indices.Count + _glassIndices.Count) / 3;

    /// <summary>
    /// While set, <see cref="Tube"/>, <see cref="Box"/> and <see cref="Skirt"/> draw for a lit
    /// style (#311): tubes get at least <see cref="SmoothSides"/> sides and smooth normals, boxes
    /// flat normals, skirts (same sides, so their flutter is unchanged) normals on both faces;
    /// <see cref="RoundedBox"/> is rounded only then. The figure builders set it around a figure for
    /// <c>MeshDetail.High</c> (<see cref="Smoothing"/>), so a vehicle in the same scratch is
    /// unchanged. Off, every primitive is exactly what it always was.
    /// </summary>
    public bool Smooth { get; set; }

    /// <summary>The fewest sides a <see cref="Smooth"/> tube has: round under the cel light, still low-poly.</summary>
    public const int SmoothSides = 12;

    /// <summary>
    /// Sets <see cref="Smooth"/> until the returned scope is disposed, then puts it back:
    /// <c>using var _ = scratch.Smoothing(on);</c> around a figure.
    /// </summary>
    public SmoothScope Smoothing(bool on)
    {
        var scope = new SmoothScope(this, Smooth);
        Smooth = on;
        return scope;
    }

    public readonly struct SmoothScope(MeshScratch scratch, bool was) : IDisposable
    {
        public void Dispose() => scratch.Smooth = was;
    }

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
        if (Smooth)
        {
            SmoothTube(a, b, axis, length, radiusA, radiusB, colour.SrgbToLinear(), Math.Max(sides, SmoothSides));
            return;
        }

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
    /// <paramref name="ripple"/> (a fraction of the hem radius) waves the hem in and out and up and
    /// down round its edge, at <paramref name="phase"/>: a skirt fluttering in the wind.
    /// </summary>
    public void Skirt(Vector3 a, Vector3 b, float radiusA, float radiusB, Color colour, int sides = 10,
        Vector3 gap = default, float gapAngle = 0f, float ripple = 0f, float phase = 0f)
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
            // two waves of different speed round the hem, so it never pulses as one
            float wave = Mathf.Sin(phase + i * 2.4f) * 0.7f + Mathf.Sin(phase * 1.7f + i * 1.1f) * 0.3f;
            float lift = Mathf.Cos(phase * 1.3f + i * 1.9f);
            var top = a + offset * radiusA;
            var hem = b + offset * radiusB * (1f + ripple * wave) + axis * (radiusB * ripple * 0.6f * lift);
            if (Smooth)
            {
                // the outside and the inside each their own vertices: shared, their normals cancel
                var n = TaperNormal(offset, axis, radiusA, radiusB, length);
                Add(top, linear, n); Add(hem, linear, n); Add(top, linear, -n); Add(hem, linear, -n);
            }
            else
            {
                Add(top, linear);
                Add(hem, linear);
            }
        }
        int stride = Smooth ? 4 : 2, inside = Smooth ? 2 : 0;
        for (int i = 0; i < sides; i++)
        {
            if (open)
            {
                float mid = Mathf.Tau * (i + 0.5f) / sides;
                if ((u * Mathf.Cos(mid) + v * Mathf.Sin(mid)).AngleTo(slit) < gapAngle) continue;
            }
            int p = start + i * stride;
            int q = start + ((i + 1) % sides) * stride;
            Quad(p, p + 1, q + 1, q);   // outside
            Quad(p + inside, q + inside, q + inside + 1, p + inside + 1);   // inside
        }
    }

    /// <summary>An axis-aligned box, optionally rotated about its own centre.</summary>
    public void Box(Vector3 centre, Vector3 size, Color colour, Basis? orientation = null)
    {
        var basis = orientation ?? Basis.Identity;
        var half = size * 0.5f;
        int start = _vertices.Count;
        var linear = colour.SrgbToLinear();
        if (Smooth)
        {
            FlatBox(centre, half, basis, linear);
            return;
        }

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
    /// A box with rounded edges and corners (a superellipsoid filling <paramref name="size"/>):
    /// a figure's head and hands in a lit style (#311). Its faces stay nearly flat, so what sits
    /// on a box head (a mask, glasses, a helmet) still sits flush. Smooth normals; without
    /// <see cref="Smooth"/> it is a plain <see cref="Box"/>.
    /// </summary>
    public void RoundedBox(Vector3 centre, Vector3 size, Color colour, Basis? orientation = null,
        int rings = 8, int segments = 12)
    {
        if (!Smooth)
        {
            Box(centre, size, colour, orientation);
            return;
        }
        var basis = orientation ?? Basis.Identity;
        var half = size * 0.5f;
        var linear = colour.SrgbToLinear();
        int start = _vertices.Count;

        // |x/a|^4 + |y/b|^4 + |z/c|^4 = 1, walked by latitude and longitude with exponent 1/2
        for (int j = 0; j <= rings; j++)
        {
            float lat = -Mathf.Pi / 2f + Mathf.Pi * j / rings;
            for (int i = 0; i < segments; i++)
            {
                float lon = Mathf.Tau * i / segments;
                var local = new Vector3(
                    half.X * Root(Mathf.Cos(lat)) * Root(Mathf.Cos(lon)),
                    half.Y * Root(Mathf.Sin(lat)),
                    half.Z * Root(Mathf.Cos(lat)) * Root(Mathf.Sin(lon)));
                // the gradient of the implicit surface
                var gradient = new Vector3(Cube(local.X / half.X) / half.X, Cube(local.Y / half.Y) / half.Y,
                    Cube(local.Z / half.Z) / half.Z);
                var n = gradient.LengthSquared() > 1e-12f ? (basis * gradient).Normalized() : basis.Y * Mathf.Sign(local.Y);
                Add(centre + basis * local, linear, n);
            }
        }
        for (int j = 0; j < rings; j++)
            for (int i = 0; i < segments; i++)
            {
                int p = start + j * segments + i, q = start + j * segments + (i + 1) % segments;
                OutwardTriangle(p, q, q + segments, centre);
                OutwardTriangle(p, q + segments, p + segments, centre);
            }

        static float Root(float w) => Mathf.Sign(w) * Mathf.Sqrt(Mathf.Abs(w));
        static float Cube(float w) => w * w * w;
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
        AddSurface(mesh, _vertices, _colors, _normals, _indices, pivot, "body");
        AddSurface(mesh, _glassVertices, _glassColors, null, _glassIndices, pivot, GlassSurface);
        return mesh;
    }

    /// <summary>
    /// Empties the scratch for the next figure: a figure redrawn every frame reuses one scratch
    /// (and its lists' capacity) instead of a new one per frame (#221).
    /// </summary>
    public void Clear()
    {
        _vertices.Clear(); _colors.Clear(); _indices.Clear(); _normals.Clear();
        _glassVertices.Clear(); _glassColors.Clear(); _glassIndices.Clear();
    }

    /// <summary>
    /// As <see cref="Build()"/>, into <paramref name="mesh"/>, whose surfaces are replaced: an
    /// animated figure keeps one <see cref="ArrayMesh"/> (one RID) for its whole life rather than
    /// a new one per frame left to the finalizer (#221).
    /// </summary>
    public ArrayMesh BuildInto(ArrayMesh mesh)
    {
        mesh.ClearSurfaces();
        AddSurface(mesh, _vertices, _colors, _normals, _indices, Vector3.Zero, "body");
        AddSurface(mesh, _glassVertices, _glassColors, null, _glassIndices, Vector3.Zero, GlassSurface);
        return mesh;
    }

    private static void AddSurface(ArrayMesh mesh, List<Vector3> vertices, List<Color> colors, List<Vector3>? normals,
        List<int> indices, Vector3 pivot, string name)
    {
        if (indices.Count == 0) return;
        var facing = new Vector3[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i] - pivot;
            facing[i] = new Vector3(-v.X, v.Y, -v.Z);
        }

        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = facing;
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        if (normals is { Count: > 0 })
        {
            // turned round like the positions
            var turned = new Vector3[normals.Count];
            for (int i = 0; i < normals.Count; i++) turned[i] = new Vector3(-normals[i].X, normals[i].Y, -normals[i].Z);
            arrays[(int)Mesh.ArrayType.Normal] = turned;
        }

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
    /// and nothing tucked into it is hidden (#54). The <see cref="Smooth"/> ones (#311) too, and
    /// every normal they write must point out of the shape. Non-zero exit on the first one that
    /// is not.
    /// </summary>
    public static int Check()
    {
        var at = new Vector3(0.3f, 1.1f, -0.7f);
        var size = new Vector3(0.4f, 0.6f, 0.8f);
        float box = size.X * size.Y * size.Z;
        float tube = Polygon(6, 0.1f) * new Vector3(0.2f, 0.9f, 0.1f).Length();
        float taper = (Polygon(6, 0.2f) + Polygon(6, 0.1f) + Mathf.Sqrt(Polygon(6, 0.2f) * Polygon(6, 0.1f))) / 3f;
        float smoothTaper = (Polygon(SmoothSides, 0.2f) + Polygon(SmoothSides, 0.1f)
            + Mathf.Sqrt(Polygon(SmoothSides, 0.2f) * Polygon(SmoothSides, 0.1f))) / 3f;
        // |x|^4 + |y|^4 + |z|^4 <= 1 fills Γ(5/4)³/Γ(7/4) = 0.810 of its box; the mesh, flat between its rings, a little less
        const float superellipsoid = 0.810f;
        Action<MeshScratch> ring = m => m.Ring(at, Vector3.Right, 0.2f, 0.3f, 0.1f, Colors.White);
        var cases = new (string Name, bool Smooth, Action<MeshScratch> Draw, float Volume, float Tolerance)[]
        {
            ("box", false, m => m.Box(at, size, Colors.White), box, 1e-4f),
            ("turned box", false, m => m.Box(at, size, Colors.White, new Basis(Vector3.Up, 0.6f)), box, 1e-4f),
            ("tube", false, m => m.Tube(at, at + new Vector3(0.2f, 0.9f, 0.1f), 0.1f, Colors.White), tube, 1e-4f),
            ("tapered tube", false, m => m.Tube(at, at + Vector3.Back, 0.2f, 0.1f, Colors.White), taper, 1e-4f),
            ("ring", false, ring, (Polygon(16, 0.3f) - Polygon(16, 0.2f)) * 0.1f, 1e-4f),
            ("smooth box", true, m => m.Box(at, size, Colors.White, new Basis(Vector3.Up, 0.6f)), box, 1e-4f),
            ("smooth tube", true, m => m.Tube(at, at + Vector3.Back, 0.2f, 0.1f, Colors.White), smoothTaper, 1e-4f),
            ("rounded box", true, m => m.RoundedBox(at, size, Colors.White, new Basis(Vector3.Up, 0.6f)),
                box * superellipsoid, box * 0.1f),
        };
        int failed = 0;
        foreach (var (name, smooth, draw, volume, tolerance) in cases)
        {
            var m = new MeshScratch { Smooth = smooth };
            draw(m);
            float v = m.SignedVolume;
            // one face the wrong way round takes its share off twice, so the total is off
            // the face and normal tests hold for a convex shape: all but the ring
            bool convex = draw != ring;
            bool ok = Mathf.Abs(-v - volume) < tolerance && (!convex || m.FacesOut() && m.NormalsOut());
            GD.Print($"[meshcheck] {name,-13} signed volume {v:F5} (expect -{volume:F5})  {(ok ? "ok" : "INSIDE OUT")}");
            if (!ok) failed++;
        }
        GD.Print(failed == 0 ? "[meshcheck] RESULT: every primitive faces out" : $"[meshcheck] RESULT: FAILED — {failed} inside out");
        return failed == 0 ? 0 : 1;
    }

    private Vector3 Centroid
    {
        get
        {
            var sum = Vector3.Zero;
            foreach (var v in _vertices) sum += v;
            return sum / Math.Max(1, _vertices.Count);
        }
    }

    /// <summary>Every face of a convex shape clockwise from outside: its cross product points in.</summary>
    private bool FacesOut()
    {
        var c = Centroid;
        for (int i = 0; i < _indices.Count; i += 3)
        {
            Vector3 a = _vertices[_indices[i]], b = _vertices[_indices[i + 1]], d = _vertices[_indices[i + 2]];
            if ((b - a).Cross(d - a).Dot((a + b + d) / 3f - c) > 1e-9f) return false;
        }
        return true;
    }

    /// <summary>Every normal written (a smooth primitive's) points out of a convex shape.</summary>
    private bool NormalsOut()
    {
        var c = Centroid;
        for (int i = 0; i < _normals.Count; i++)
            if (_normals[i].Dot(_vertices[i] - c) <= 0f) return false;
        return true;
    }

    /// <summary>Area of a regular polygon of <paramref name="sides"/> with corners at <paramref name="radius"/>.</summary>
    private static float Polygon(int sides, float radius) => sides / 2f * radius * radius * Mathf.Sin(Mathf.Tau / sides);

    private void Add(Vector3 position, Color linear)
    {
        _vertices.Add(position);
        _colors.Add(linear);
        if (_normals.Count > 0) _normals.Add(NoNormal);
    }

    private void Add(Vector3 position, Color linear, Vector3 normal)
    {
        // the first normal: every vertex before it gets the one Godot would have given it
        while (_normals.Count < _vertices.Count) _normals.Add(NoNormal);
        _vertices.Add(position);
        _colors.Add(linear);
        _normals.Add(normal);
    }

    /// <summary>
    /// What a vertex drawn without <see cref="Smooth"/> gets in a scratch that has normals: Godot's
    /// default for a mesh without any, +Z once <see cref="AddSurface"/> turns the mesh round.
    /// </summary>
    private static readonly Vector3 NoNormal = new(0, 0, -1);

    /// <summary>The outward normal of a tapered tube's side at <paramref name="offset"/> round it.</summary>
    private static Vector3 TaperNormal(Vector3 offset, Vector3 axis, float radiusA, float radiusB, float length) =>
        (offset + axis * ((radiusA - radiusB) / length)).Normalized();

    private void SmoothTube(Vector3 a, Vector3 b, Vector3 axis, float length, float radiusA, float radiusB,
        Color linear, int sides)
    {
        var reference = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        var u = axis.Cross(reference).Normalized();
        var v = axis.Cross(u);

        int start = _vertices.Count;
        for (int i = 0; i < sides; i++)
        {
            float angle = Mathf.Tau * i / sides;
            var offset = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
            var n = TaperNormal(offset, axis, radiusA, radiusB, length);
            Add(a + offset * radiusA, linear, n);
            Add(b + offset * radiusB, linear, n);
        }
        for (int i = 0; i < sides; i++)
        {
            int p = start + i * 2;
            int q = start + ((i + 1) % sides) * 2;
            Quad(p, p + 1, q + 1, q);
        }

        // the caps are flat: their own vertices, facing along the axis
        int caps = _vertices.Count;
        for (int i = 0; i < sides; i++)
        {
            float angle = Mathf.Tau * i / sides;
            var offset = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
            Add(a + offset * radiusA, linear, -axis);
            Add(b + offset * radiusB, linear, axis);
        }
        CapFan(caps, sides, evenOffset: 0, flip: false, linear);
        CapFan(caps, sides, evenOffset: 1, flip: true, linear);
    }

    /// <summary><see cref="Box"/> with each face its own four vertices and its own normal.</summary>
    private void FlatBox(Vector3 centre, Vector3 half, Basis basis, Color linear)
    {
        // the same faces as Box, each wound clockwise from outside: corner bits (x, y, z) and the face's normal
        ReadOnlySpan<int> faces = [0, 1, 3, 2, 4, 6, 7, 5, 0, 2, 6, 4, 1, 5, 7, 3, 2, 3, 7, 6, 0, 4, 5, 1];
        ReadOnlySpan<Vector3> normals = [Vector3.Forward, Vector3.Back, Vector3.Left, Vector3.Right, Vector3.Up, Vector3.Down];
        for (int f = 0; f < 6; f++)
        {
            int start = _vertices.Count;
            var n = (basis * normals[f]).Normalized();
            for (int k = 0; k < 4; k++)
            {
                int i = faces[f * 4 + k];
                var corner = new Vector3(
                    (i & 1) == 0 ? -half.X : half.X,
                    (i & 2) == 0 ? -half.Y : half.Y,
                    (i & 4) == 0 ? -half.Z : half.Z);
                Add(centre + basis * corner, linear, n);
            }
            Quad(start, start + 1, start + 2, start + 3);
        }
    }

    /// <summary>A triangle of a convex shape round <paramref name="centre"/>, wound clockwise from outside; slivers dropped.</summary>
    private void OutwardTriangle(int a, int b, int c, Vector3 centre)
    {
        var pa = _vertices[a];
        var cross = (_vertices[b] - pa).Cross(_vertices[c] - pa);
        if (cross.LengthSquared() < 1e-14f) return;
        // clockwise from outside is a cross product pointing in (SignedVolume)
        if (cross.Dot((pa + _vertices[b] + _vertices[c]) / 3f - centre) > 0f) (b, c) = (c, b);
        _indices.Add(a); _indices.Add(b); _indices.Add(c);
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
