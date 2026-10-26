using Godot;
using UnitSport.Core;

namespace UnitSport.Adventure.Places;

/// <summary>
/// The places' markers (#1224, places plan 5), each one mesh built once and drawn as a
/// <see cref="MultiMesh"/> (<c>general/no-mesh-rebuild-per-frame</c>): a summit cross, a telescope
/// (Fernrohr) at towers, cable-car tops and passes, a yellow stamp post with its stamp box at landmarks,
/// and the small Swiss flag over a synced viewpoint. Vertex-coloured boxes, origin at the foot.
/// </summary>
public static class PoiMarkerMeshBuilder
{
    private static ArrayMesh? _cross, _telescope, _post, _flag;

    private static readonly Color Wood = new(0.42f, 0.27f, 0.14f), Metal = new(0.32f, 0.34f, 0.36f),
        Brass = new(0.78f, 0.62f, 0.25f), Yellow = new(0.98f, 0.80f, 0.10f), Black = new(0.08f, 0.08f, 0.08f),
        Red = new(0.85f, 0.08f, 0.10f), White = new(0.96f, 0.96f, 0.96f), Stone = new(0.55f, 0.54f, 0.50f);

    [Showcase("Adventure", "Summit cross")]
    public static ArrayMesh Cross() => _cross ??= Build(st =>
    {
        Box(st, new Vector3(0, 0.15f, 0), new Vector3(0.9f, 0.3f, 0.9f), Stone);
        Box(st, new Vector3(0, 1.9f, 0), new Vector3(0.16f, 3.4f, 0.16f), Wood);
        Box(st, new Vector3(0, 2.75f, 0), new Vector3(1.5f, 0.16f, 0.16f), Wood);
        // the summit book's box on the upright
        Box(st, new Vector3(0, 1.3f, 0.12f), new Vector3(0.24f, 0.3f, 0.1f), Metal);
    });

    [Showcase("Adventure", "Telescope")]
    public static ArrayMesh Telescope() => _telescope ??= Build(st =>
    {
        Box(st, new Vector3(0, 0.6f, 0), new Vector3(0.12f, 1.2f, 0.12f), Metal);
        Box(st, new Vector3(0, 1.25f, 0.05f), new Vector3(0.22f, 0.22f, 0.6f), Brass);
        Box(st, new Vector3(0, 1.25f, -0.3f), new Vector3(0.14f, 0.14f, 0.12f), Black);
        Box(st, new Vector3(0, 0.03f, 0), new Vector3(0.5f, 0.06f, 0.5f), Stone);
    });

    [Showcase("Adventure", "Stamp post")]
    public static ArrayMesh StampPost() => _post ??= Build(st =>
    {
        Box(st, new Vector3(0, 1.1f, 0), new Vector3(0.1f, 2.2f, 0.1f), Metal);
        // the yellow hiking sign and the stamp box under it, as a real Stempelstelle
        Box(st, new Vector3(0.28f, 1.95f, 0), new Vector3(0.6f, 0.18f, 0.04f), Yellow);
        Box(st, new Vector3(0, 1.15f, 0.12f), new Vector3(0.3f, 0.26f, 0.16f), Yellow);
        Box(st, new Vector3(0, 1.26f, 0.205f), new Vector3(0.18f, 0.03f, 0.01f), Black);
    });

    [Showcase("Adventure", "Synced flag")]
    public static ArrayMesh Flag() => _flag ??= Build(st =>
    {
        Box(st, new Vector3(0, 0.35f, 0), new Vector3(0.03f, 0.7f, 0.03f), Metal);
        Box(st, new Vector3(0.2f, 0.58f, 0), new Vector3(0.36f, 0.24f, 0.01f), Red);
        Box(st, new Vector3(0.2f, 0.58f, 0.006f), new Vector3(0.16f, 0.05f, 0.005f), White);
        Box(st, new Vector3(0.2f, 0.58f, 0.006f), new Vector3(0.05f, 0.16f, 0.005f), White);
    });

    /// <summary>Vertex colour as the albedo, as the other markers.</summary>
    public static StandardMaterial3D Material() => new() { VertexColorUseAsAlbedo = true, Roughness = 0.8f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

    private static ArrayMesh Build(Action<SurfaceTool> parts)
    {
        ShowcaseTrace.Mark();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        parts(st);
        return st.Commit();
    }

    private static void Box(SurfaceTool st, Vector3 centre, Vector3 size, Color color)
    {
        var h = size / 2;
        // +X, -X, +Y, -Y, +Z, -Z faces, counter-clockwise seen from outside
        Face(st, centre, new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0), h.X, h.Z, h.Y, color);
        Face(st, centre, new Vector3(-1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 0), h.X, h.Z, h.Y, color);
        Face(st, centre, new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, -1), h.Y, h.X, h.Z, color);
        Face(st, centre, new Vector3(0, -1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1), h.Y, h.X, h.Z, color);
        Face(st, centre, new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 1, 0), h.Z, h.X, h.Y, color);
        Face(st, centre, new Vector3(0, 0, -1), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), h.Z, h.X, h.Y, color);
    }

    private static void Face(SurfaceTool st, Vector3 c, Vector3 n, Vector3 u, Vector3 v, float dn, float du, float dv, Color color)
    {
        var o = c + n * dn;
        Vector3 a = o - u * du - v * dv, b = o + u * du - v * dv, cc = o + u * du + v * dv, d = o - u * du + v * dv;
        foreach (var p in new[] { a, cc, b, a, d, cc })
        {
            st.SetColor(color);
            st.SetNormal(n);
            st.AddVertex(p);
        }
    }
}
