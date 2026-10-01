using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// PROTOTYPE (issue #181): the realistic styles' trees. Two EZ-Tree meshes (MIT; generated
/// headless by assets/proto/trees/gen.mjs with game-budget options, ~2k triangles each), scaled
/// to the unit tree every style shares (radius 1, height 1: the instance transform sets the size),
/// and an impostor of each, rendered once at startup, for the billboards beyond the 3D range.
/// </summary>
public static class RealTrees
{
    public static ArrayMesh? Conifer { get; private set; }
    public static ArrayMesh? Broadleaf { get; private set; }

    private static readonly List<Material> _bake = new();

    public static void Load(float treeNear)
    {
        Conifer = Build("pine_medium_game.json", "bark_pine.jpg", "leaf_pine.png", treeNear, new Vector2(1f, 1f));
        Broadleaf = Build("ash_medium_game.json", "bark_oak.jpg", "leaf_ash.png", treeNear, new Vector2(0.5f, 5f));
        GD.Print($"[style] realistic trees: conifer {Triangles(Conifer)} tris, broadleaf {Triangles(Broadleaf)} tris");
    }

    private static int Triangles(ArrayMesh m)
    {
        int n = 0;
        for (int s = 0; s < m.GetSurfaceCount(); s++)
            n += m.SurfaceGetArrayIndexLen(s) / 3;
        return n;
    }

    private static string Dir => ProjectSettings.GlobalizePath("res://assets/proto/trees");

    private static Texture2D Texture(string file)
    {
        var img = Image.LoadFromFile(Path.Combine(Dir, file));
        if (img.GetFormat() != Image.Format.Rgba8 && file.EndsWith(".png")) img.Convert(Image.Format.Rgba8);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    private static ArrayMesh Build(string json, string bark, string leaf, float treeNear, Vector2 barkScale)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, json)));
        var parts = doc.RootElement.GetProperty("parts");

        // unit tree: height 1 to the top of the crown, radius 1 at its widest
        float height = 0f, radius = 0f;
        foreach (var part in parts.EnumerateObject())
        {
            var p = Floats(part.Value, "position");
            for (int i = 0; i < p.Length; i += 3)
            {
                height = Mathf.Max(height, p[i + 1]);
                radius = Mathf.Max(radius, Mathf.Sqrt(p[i] * p[i] + p[i + 2] * p[i + 2]));
            }
        }
        var scale = new Vector3(1f / radius, 1f / height, 1f / radius);

        var mesh = new ArrayMesh();
        foreach (var (name, isLeaves) in new[] { ("bark", false), ("leaves", true) })
        {
            var part = parts.GetProperty(name);
            var p = Floats(part, "position");
            var nrm = Floats(part, "normal");
            var uv = Floats(part, "uv");
            var verts = new Vector3[p.Length / 3];
            var normals = new Vector3[verts.Length];
            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                verts[i] = new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]) * scale;
                // non-uniform scale: normals go through the inverse transpose
                var n = new Vector3(nrm[i * 3] / scale.X, nrm[i * 3 + 1] / scale.Y, nrm[i * 3 + 2] / scale.Z).Normalized();
                if (isLeaves)
                {
                    // leaf cards lit as one rounded crown, not as hundreds of flat planes
                    var outward = (verts[i] - new Vector3(0, 0.6f, 0)) * new Vector3(1f, 2f, 1f);
                    n = (outward.Normalized() * 0.8f + Vector3.Up * 0.35f).Normalized();
                }
                normals[i] = n;
                uvs[i] = new Vector2(uv[i * 2], 1f - uv[i * 2 + 1]);
            }
            var index = part.GetProperty("index").EnumerateArray();
            var indices = new List<int>();
            foreach (var x in index) indices.Add(x.GetInt32());

            using var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            var tex = Texture(isLeaves ? leaf : bark);
            var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/real_treepart.gdshader") };
            m.SetShaderParameter("albedo_tex", tex);
            m.SetShaderParameter("leaves", isLeaves);
            m.SetShaderParameter("tree_near", treeNear);
            if (!isLeaves) m.SetShaderParameter("uv_scale", barkScale);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, m);

            // the impostor bake draws the same surface unlit, colour only
            _bake.Add(new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = tex,
                Uv1Scale = isLeaves ? Vector3.One : new Vector3(barkScale.X, barkScale.Y, 1f),
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.45f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            });
        }
        return mesh;
    }

    private static float[] Floats(JsonElement part, string key)
    {
        var a = part.GetProperty(key);
        var f = new float[a.GetArrayLength()];
        int i = 0;
        foreach (var x in a.EnumerateArray()) f[i++] = x.GetSingle();
        return f;
    }

    /// <summary>
    /// Renders each tree once, side on, into a 512x256 transparent image (the unit tree's
    /// x -1..1 by y 0..1, exactly the billboard quad), and hands the mipmapped results to the
    /// billboard material. Two frames on a private World3D; the billboards draw nothing until then.
    /// </summary>
    public static async Task BakeImpostors(Node host, ShaderMaterial billboard)
    {
        var results = new List<Texture2D>();
        int bake = 0;
        foreach (var mesh in new[] { Conifer!, Broadleaf! })
        {
            var vp = new SubViewport
            {
                Size = new Vector2I(512, 256),
                TransparentBg = true,
                OwnWorld3D = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            };
            var mi = new MeshInstance3D { Mesh = mesh };
            for (int s = 0; s < mesh.GetSurfaceCount(); s++) mi.SetSurfaceOverrideMaterial(s, _bake[bake++]);
            var cam = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = 1f,
                Position = new Vector3(0, 0.5f, 4f),
                Current = true,
            };
            vp.AddChild(mi);
            vp.AddChild(cam);
            host.AddChild(vp);
            for (int f = 0; f < 3; f++) await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = vp.GetTexture().GetImage();
            img.GenerateMipmaps();
            results.Add(ImageTexture.CreateFromImage(img));
            vp.QueueFree();
        }
        billboard.SetShaderParameter("impostor_conifer", results[0]);
        billboard.SetShaderParameter("impostor_broadleaf", results[1]);
        GD.Print("[style] tree impostors baked");
    }
}
