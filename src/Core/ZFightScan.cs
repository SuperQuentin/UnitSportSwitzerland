using System.Globalization;
using System.Runtime.CompilerServices;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// The engine side of <see cref="ZFight"/> (#788): the triangles of every visible mesh under a
/// node, with their look (material and vertex colour) and depth class (roads and their paint are
/// pulled toward the eye by their shader, <c>shaders/body/road.gdshaderinc</c>), and the fighting
/// regions as one magenta mesh to draw over them. Used by <c>--zfightcheck</c>, the model viewer's
/// <c>--zfight</c> and the debug menu's overlay (docs/notes/general/zfight.md).
/// </summary>
public static class ZFightScan
{
    /// <summary>The triangles found, and the node each came from (<see cref="ZFight.Tri.Part"/> indexes <see cref="Parts"/>).</summary>
    public sealed class Scene
    {
        public readonly List<ZFight.Tri> Tris = new();
        public readonly List<string> Parts = new();
        public bool Truncated;

        public string Between(ZFight.Hit hit)
        {
            string a = Parts[Tris[hit.First].Part], b = Parts[Tris[hit.Second].Part];
            return a == b ? a : $"{a} / {b}";
        }
    }

    /// <summary>
    /// A mesh's surfaces as read back once: reading a tile's ground off the GPU every scan costs a
    /// hitch, so the overlay keeps them while it is on (keyed weakly by mesh: freed tiles drop out).
    /// </summary>
    public sealed class Cache
    {
        internal readonly ConditionalWeakTable<Mesh, Surface[]> Meshes = new();
    }

    internal sealed record Surface(Vector3[] Vertices, int[] Indices, Color[] Colours, Vector2[] Uv2, bool Triangles);

    /// <summary>
    /// Every visible <see cref="MeshInstance3D"/> and <see cref="MultiMeshInstance3D"/> under
    /// <paramref name="root"/> (itself included), in the space of <paramref name="space"/> (the
    /// inverse of a root's global transform gives model space). With <paramref name="within"/>,
    /// only triangles whose box touches it; <paramref name="skip"/> leaves a node and its children
    /// out. Stops at <paramref name="maxTris"/> (<see cref="Scene.Truncated"/>).
    /// </summary>
    public static Scene Gather(Node root, Transform3D space, Aabb? within = null, Func<Node, bool>? skip = null,
        Cache? cache = null, int maxTris = 600_000)
    {
        var scene = new Scene();
        var kinds = new Dictionary<Material, (int Id, int Depth, bool TwoSided, bool Opaque)>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0 && !scene.Truncated)
        {
            var node = stack.Pop();
            if (skip?.Invoke(node) == true) continue;
            if (node is Node3D { Visible: false }) continue;
            for (int c = node.GetChildCount() - 1; c >= 0; c--) stack.Push(node.GetChild(c));

            if (node is MeshInstance3D { Mesh: { } mesh } mi)
            {
                var xf = space * mi.GlobalTransform;
                if (within is { } box && !box.Intersects(xf * mi.GetAabb())) continue;
                int part = Part(scene, root, node);
                var surfaces = Read(mesh, cache);
                for (int s = 0; s < surfaces.Length; s++)
                {
                    var material = mi.MaterialOverride ?? mi.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s);
                    Add(scene, surfaces[s], xf, part, KindOf(material, kinds), Colors.White, within, maxTris);
                }
            }
            else if (node is MultiMeshInstance3D { Multimesh: { Mesh: { } instanced } multi } mmi)
            {
                var surfaces = Read(instanced, cache);
                int part = Part(scene, root, node);
                var baseXf = space * mmi.GlobalTransform;
                int count = multi.VisibleInstanceCount >= 0 ? multi.VisibleInstanceCount : multi.InstanceCount;
                for (int k = 0; k < count && !scene.Truncated; k++)
                {
                    var xf = baseXf * multi.GetInstanceTransform(k);
                    if (within is { } box && !box.Intersects(xf * instanced.GetAabb())) continue;
                    var tint = multi.UseColors ? multi.GetInstanceColor(k) : Colors.White;
                    for (int s = 0; s < surfaces.Length; s++)
                        Add(scene, surfaces[s], xf, part, KindOf(mmi.MaterialOverride ?? instanced.SurfaceGetMaterial(s), kinds), tint, within, maxTris);
                }
            }
        }
        return scene;
    }

    private static int Part(Scene scene, Node root, Node node)
    {
        string name = node == root ? node.Name.ToString() : root.GetPathTo(node).ToString();
        scene.Parts.Add(name);
        return scene.Parts.Count - 1;
    }

    private static Surface[] Read(Mesh mesh, Cache? cache)
    {
        if (cache != null && cache.Meshes.TryGetValue(mesh, out var known)) return known;
        var surfaces = new Surface[mesh.GetSurfaceCount()];
        for (int s = 0; s < surfaces.Length; s++)
        {
            bool triangles = mesh is not ArrayMesh am || am.SurfaceGetPrimitiveType(s) == Mesh.PrimitiveType.Triangles;
            var arrays = triangles ? mesh.SurfaceGetArrays(s) : null;
            surfaces[s] = arrays == null || arrays.Count <= (int)Mesh.ArrayType.Index
                ? new Surface([], [], [], [], false)
                : new Surface(
                    Get(arrays, Mesh.ArrayType.Vertex, v => v.AsVector3Array()),
                    Get(arrays, Mesh.ArrayType.Index, v => v.AsInt32Array()),
                    Get(arrays, Mesh.ArrayType.Color, v => v.AsColorArray()),
                    Get(arrays, Mesh.ArrayType.TexUV2, v => v.AsVector2Array()),
                    true);
        }
        cache?.Meshes.AddOrUpdate(mesh, surfaces);
        return surfaces;
    }

    private static T[] Get<T>(Godot.Collections.Array arrays, Mesh.ArrayType type, Func<Variant, T[]> read) =>
        arrays[(int)type] is { VariantType: not Variant.Type.Nil } v ? read(v) : [];

    /// <summary>
    /// What a material does to depth and culling: a road shader pulls the road (class 1) and its
    /// paint (class 2, <c>UV2.x</c> 6) toward the eye; <c>cull_disabled</c> draws both sides; a
    /// blended or alpha one (glass) hides nothing behind it.
    /// </summary>
    private static (int Id, int Depth, bool TwoSided, bool Opaque) KindOf(Material? material, Dictionary<Material, (int, int, bool, bool)> known)
    {
        if (material == null) return (0, 0, false, true);
        if (known.TryGetValue(material, out var kind)) return kind;
        kind = material switch
        {
            ShaderMaterial { Shader: { } shader } => (known.Count + 1,
                shader.Code.Contains("road.gdshaderinc", StringComparison.Ordinal) ? 1 : 0,
                shader.Code.Contains("cull_disabled", StringComparison.Ordinal),
                !shader.Code.Contains("blend_", StringComparison.Ordinal) && !shader.Code.Contains("ALPHA", StringComparison.Ordinal)),
            BaseMaterial3D b => (known.Count + 1, 0, b.CullMode == BaseMaterial3D.CullModeEnum.Disabled,
                b.Transparency == BaseMaterial3D.TransparencyEnum.Disabled),
            _ => (known.Count + 1, 0, false, true),
        };
        known[material] = kind;
        return kind;
    }

    private static void Add(Scene scene, Surface surface, Transform3D xf, int part, (int Id, int Depth, bool TwoSided, bool Opaque) kind,
        Color tint, Aabb? within, int maxTris)
    {
        if (!surface.Triangles || surface.Vertices.Length == 0) return;
        var v = surface.Vertices;
        var idx = surface.Indices;
        int count = idx.Length > 0 ? idx.Length / 3 : v.Length / 3;
        for (int t = 0; t < count; t++)
        {
            int i0 = idx.Length > 0 ? idx[t * 3] : t * 3;
            int i1 = idx.Length > 0 ? idx[t * 3 + 1] : t * 3 + 1;
            int i2 = idx.Length > 0 ? idx[t * 3 + 2] : t * 3 + 2;
            Vector3 a = xf * v[i0], b = xf * v[i1], c = xf * v[i2];
            if (within is { } box && !box.Intersects(new Aabb(a, Vector3.Zero).Expand(b).Expand(c).Grow(ZFight.MaxGap))) continue;
            if (scene.Tris.Count >= maxTris)
            {
                scene.Truncated = true;
                return;
            }
            var colour = tint;
            if (surface.Colours.Length > 0)
            {
                var sum = surface.Colours[i0] + surface.Colours[i1] + surface.Colours[i2];
                colour *= new Color(sum.R / 3, sum.G / 3, sum.B / 3, sum.A / 3);
            }
            int depth = kind.Depth;
            if (depth == 1 && surface.Uv2.Length > 0 && Mathf.RoundToInt(surface.Uv2[i0].X) == 6) depth = 2;
            // a colour step of 1/32 still reads as the same face
            int look = HashCode.Combine(kind.Id, Mathf.RoundToInt(colour.R * 32), Mathf.RoundToInt(colour.G * 32),
                Mathf.RoundToInt(colour.B * 32), Mathf.RoundToInt(colour.A * 32));
            scene.Tris.Add(new ZFight.Tri(a, b, c, part, look, depth, kind.TwoSided, kind.Opaque));
        }
    }

    /// <summary>"3 places, 12.5 cm²" (invariant: logs are read by scripts).</summary>
    public static string Summary(IReadOnlyList<ZFight.Hit> hits) =>
        string.Create(CultureInfo.InvariantCulture, $"{hits.Count} places, {ZFight.AreaCm2(hits):0.#} cm²");

    /// <summary>The summary and the worst few places, with the parts that fight there.</summary>
    public static string Describe(Scene scene, IReadOnlyList<ZFight.Hit> hits, int worst = 3)
    {
        var text = new System.Text.StringBuilder(Summary(hits));
        for (int k = 0; k < Math.Min(worst, hits.Count); k++)
        {
            var at = hits[k].At;
            text.Append(CultureInfo.InvariantCulture,
                $"\n    {hits[k].Area * 1e4f:0.#} cm² {hits[k].Gap * 1000:0.#} mm apart at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}): {scene.Between(hits[k])}");
        }
        return text.ToString();
    }

    /// <summary>The hits' regions as magenta patches, seen through everything, in the scan's space; null with none.</summary>
    public static ArrayMesh? Patches(IReadOnlyList<ZFight.Hit> hits)
    {
        if (hits.Count == 0) return null;
        var points = new List<Vector3>();
        foreach (var hit in hits)
            for (int k = 1; k + 1 < hit.Region.Length; k++)
            {
                points.Add(hit.Region[0]);
                points.Add(hit.Region[k]);
                points.Add(hit.Region[k + 1]);
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, PatchMaterial);
        return mesh;
    }

    private static readonly StandardMaterial3D PatchMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(1f, 0.1f, 0.85f, 0.75f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = true,
        RenderPriority = 20,
    };
}
