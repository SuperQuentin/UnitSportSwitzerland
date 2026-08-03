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

    public int TriangleCount => _indices.Count / 3;

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

        // caps, so a limb does not show its hollow interior when seen end-on
        CapFan(start, sides, evenOffset: 0, flip: true, linear);
        CapFan(start, sides, evenOffset: 1, flip: false, linear);
    }

    public void Tube(Vector3 a, Vector3 b, float radius, Color colour, int sides = 6) =>
        Tube(a, b, radius, radius, colour, sides);

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
        Quad(start + 0, start + 2, start + 3, start + 1);   // back
        Quad(start + 4, start + 5, start + 7, start + 6);   // front
        Quad(start + 0, start + 4, start + 6, start + 2);   // left
        Quad(start + 1, start + 3, start + 7, start + 5);   // right
        Quad(start + 2, start + 6, start + 7, start + 3);   // top
        Quad(start + 0, start + 1, start + 5, start + 4);   // bottom
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
    public ArrayMesh Build()
    {
        var mesh = new ArrayMesh();
        if (_indices.Count == 0) return mesh;

        var facing = new Vector3[_vertices.Count];
        for (int i = 0; i < _vertices.Count; i++)
            facing[i] = new Vector3(-_vertices[i].X, _vertices[i].Y, -_vertices[i].Z);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = facing;
        arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

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
