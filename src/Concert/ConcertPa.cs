using Godot;

namespace UnitSport.Concert;

/// <summary>
/// The PA a concert plays through (#885): the line arrays flown beside the stage (a J of elements, each
/// with its two woofers), the subs stacked under the deck's edge and the delay hangs out on the pitch,
/// from <see cref="StagePlan.Pa"/>. All of it one mesh, built once; the woofers pump on the kick and the
/// cabinets bounce with it, the subs hardest, in its shader (<c>shaders/concert/pa.gdshader</c>): each
/// vertex carries its element's pivot and facing, so a frame costs two uniforms and one draw.
/// </summary>
public partial class ConcertPa : Node3D
{
    private static readonly StringName Kick = "kick", Bass = "bass";
    private static readonly Color Body = new(0.06f, 0.06f, 0.07f), Cone = new(0.17f, 0.17f, 0.18f);
    private const int ConeSides = 10;

    private readonly StagePlan _plan;
    private ShaderMaterial? _material;
    private float _kick = -1f, _bass = -1f;

    public ConcertPa(StagePlan plan)
    {
        _plan = plan;
        Name = "PA";
    }

    public ConcertPa() : this(null!) { }

    public override void _Ready()
    {
        if (_plan.Pa.Count == 0) return;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetCustomFormat(1, SurfaceTool.CustomFormat.RgbaFloat);
        int cabinets = 0;
        foreach (var cab in _plan.Pa)
        {
            // +Z of the cabinet faces the audience
            var basis = Basis.LookingAt(-cab.Facing, Vector3.Up);
            if (cab.Sub)
            {
                Element(st, basis, cab.Centre, cab.Size, sub: true, Math.Min(0.42f, cab.Size.Y * 0.36f));
                cabinets++;
                continue;
            }
            // a line array: elements stacked from the top, curving down into a J
            int n = Math.Max(4, (int)MathF.Round(cab.Size.Y / 0.55f));
            float eh = cab.Size.Y / n;
            var at = cab.Centre + basis.Y * (cab.Size.Y / 2);
            float angle = 0f;
            for (int k = 0; k < n; k++)
            {
                var tilt = basis * new Basis(Vector3.Right, -angle);
                Element(st, tilt, at - tilt.Y * (eh / 2), new Vector3(cab.Size.X, eh * 0.9f, cab.Size.Z), sub: false, Math.Min(eh * 0.38f, cab.Size.X * 0.2f));
                at -= tilt.Y * eh;
                angle += Mathf.DegToRad(0.3f + k * 0.12f);   // a J of about 25° over a 19-box hang: the bottom aims down at the front rows
                cabinets++;
            }
        }
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/concert/pa.gdshader") };
        AddChild(new MeshInstance3D
        {
            Name = "Cabinets", Mesh = st.Commit(), MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        GD.Print($"[concert] PA: {_plan.Pa.Count} hangs and stacks, {cabinets} cabinets, {cabinets * 2} woofers");
    }

    /// <summary>One cabinet and the two woofers on its front.</summary>
    private static void Element(SurfaceTool st, Basis basis, Vector3 centre, Vector3 size, bool sub, float coneRadius)
    {
        var u = basis.X.Normalized();
        var v = basis.Y.Normalized();
        var front = basis.Z.Normalized();
        Vector3 x = u * (size.X / 2), y = v * (size.Y / 2), z = front * (size.Z / 2);
        var pivot = new Color(centre.X, centre.Y, centre.Z, sub ? 1f : 0f);
        var facing = new Color(front.X, front.Y, front.Z, 0f);
        Vector3 C(float a, float b, float c) => centre + x * a + y * b + z * c;
        Quad(st, C(-1, -1, 1), C(1, -1, 1), C(1, 1, 1), C(-1, 1, 1), front, Body, pivot, facing);
        Quad(st, C(1, -1, -1), C(-1, -1, -1), C(-1, 1, -1), C(1, 1, -1), -front, Body, pivot, facing);
        Quad(st, C(1, -1, 1), C(1, -1, -1), C(1, 1, -1), C(1, 1, 1), u, Body, pivot, facing);
        Quad(st, C(-1, -1, -1), C(-1, -1, 1), C(-1, 1, 1), C(-1, 1, -1), -u, Body, pivot, facing);
        Quad(st, C(-1, 1, 1), C(1, 1, 1), C(1, 1, -1), C(-1, 1, -1), v, Body, pivot, facing);
        Quad(st, C(-1, -1, -1), C(1, -1, -1), C(1, -1, 1), C(-1, -1, 1), -v, Body, pivot, facing);

        // the woofers: a shallow cone each, its rim a centimetre proud of the face (never coplanar)
        for (int c = 0; c < 2; c++)
        {
            var rim = centre + x * (c == 0 ? -0.5f : 0.5f) + front * (size.Z / 2 + 0.012f);
            var conePivot = new Color(rim.X, rim.Y, rim.Z, sub ? 3f : 2f);
            var cap = rim + front * 0.03f;
            for (int i = 0; i < ConeSides; i++)
            {
                float a0 = i * Mathf.Tau / ConeSides, a1 = (i + 1) * Mathf.Tau / ConeSides;
                var r0 = rim + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * coneRadius;
                var r1 = rim + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * coneRadius;
                Tri(st, r0, r1, cap, front, Cone, conePivot, facing);
            }
        }
    }

    private static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color col, Color c0, Color c1)
    {
        Tri(st, a, b, c, normal, col, c0, c1);
        Tri(st, a, c, d, normal, col, c0, c1);
    }

    /// <summary>A triangle facing <paramref name="normal"/>: Godot's front face winds clockwise, so it is flipped when it would face in.</summary>
    private static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color col, Color c0, Color c1)
    {
        if ((b - a).Cross(c - a).Dot(normal) > 0) (b, c) = (c, b);
        Vertex(st, a, normal, col, c0, c1);
        Vertex(st, b, normal, col, c0, c1);
        Vertex(st, c, normal, col, c0, c1);
    }

    private static void Vertex(SurfaceTool st, Vector3 p, Vector3 normal, Color col, Color c0, Color c1)
    {
        st.SetNormal(normal);
        st.SetColor(col);
        st.SetCustom(0, c0);
        st.SetCustom(1, c1);
        st.AddVertex(p);
    }

    /// <summary>One frame: the kick (and the bass for the subs), 0..1; set only when it changes.</summary>
    public void Pump(float kick, float bass)
    {
        if (_material == null) return;
        if (MathF.Abs(kick - _kick) > 0.005f) _material.SetShaderParameter(Kick, _kick = kick);
        if (MathF.Abs(bass - _bass) > 0.01f) _material.SetShaderParameter(Bass, _bass = bass);
    }
}
