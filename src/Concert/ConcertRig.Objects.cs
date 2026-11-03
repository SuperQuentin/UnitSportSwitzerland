using Godot;
using UnitSport.Concert.Mvr;
using UnitSport.Styles;

namespace UnitSport.Concert;

/// <summary>
/// An imported rig's trusses and scene objects (#958), drawn once in the rig's frame with the stage's
/// flat prop material; collision by ConcertRig.Collision (#974). A mesh placed many times is
/// one MultiMesh; the rest (Capture writes every object as its own file) are baked into one mesh, so
/// a show of nine hundred one-off files is one draw call, not nine hundred. Faces' looks (#973): the
/// vertex colours, and a surface per texture drawn by a prop material holding it.
/// </summary>
public partial class ConcertRig
{
    /// <summary>From this many pieces a mesh is instanced; fewer are baked into the shared mesh.</summary>
    private const int InstancedFrom = 4;

    private static readonly StringName AlbedoTexture = "albedo_texture";

    /// <summary>How many object pieces are drawn (for the checks).</summary>
    public int ObjectsDrawn { get; private set; }

    /// <summary>How many of the rig's textures are drawn (for the checks).</summary>
    public int TexturesDrawn { get; private set; }

    private ShaderMaterial? _propMaterial;
    private ShaderMaterial?[] _textureMaterials = Array.Empty<ShaderMaterial?>();

    /// <summary>The prop material for a texture of the rig (made once each), or the plain one for none.</summary>
    private Material LookMaterial(int texture)
    {
        _propMaterial ??= StyleKit.Material(MaterialRole.Prop);
        if (Imported is not { } rig || texture < 0 || texture >= rig.Textures.Count) return _propMaterial;
        if (_textureMaterials.Length != rig.Textures.Count) _textureMaterials = new ShaderMaterial?[rig.Textures.Count];
        if (_textureMaterials[texture] is { } made) return made;
        // checked by size on arrival; one that still does not decode draws its vertex colours alone
        var m = StyleKit.Material(MaterialRole.Prop);
        if (MvrImages.DecodeSmall(rig.Textures[texture]) is { } px)
        {
            m.SetShaderParameter(AlbedoTexture, ImageTexture.CreateFromImage(Image.CreateFromData(px.W, px.H, false, Image.Format.Rgb8, px.Rgb)));
            TexturesDrawn++;
        }
        return _textureMaterials[texture] = m;
    }

    /// <summary>
    /// The textures the pure PNG reader cannot read (JPEG, BMP, TGA, interlaced PNG): Godot's loaders, in
    /// the game only. <see cref="Core.Main"/> calls it, never a module initializer (#1010): Android's runtime
    /// runs those as the assembly loads, before Godot's interop, and this class's static StringName crashed it.
    /// </summary>
    internal static void UseGodotImages() => MvrImages.Fallback = GodotImage;

    private static (int W, int H, byte[] Rgb)? GodotImage(byte[] bytes)
    {
        using var img = new Image();
        var error = bytes switch
        {
            [0xFF, 0xD8, ..] => img.LoadJpgFromBuffer(bytes),
            [0x89, 0x50, ..] => img.LoadPngFromBuffer(bytes),
            [0x42, 0x4D, ..] => img.LoadBmpFromBuffer(bytes),
            [0x52, 0x49, 0x46, 0x46, ..] => img.LoadWebpFromBuffer(bytes),
            _ => img.LoadTgaFromBuffer(bytes),
        };
        if (error != Error.Ok || img.IsEmpty()) return null;
        // the box filter takes it to 64; first, cheaply, to a size worth copying
        int side = Math.Max(img.GetWidth(), img.GetHeight());
        if (side > 512) img.Resize(Math.Max(1, img.GetWidth() * 512 / side), Math.Max(1, img.GetHeight() * 512 / side), Image.Interpolation.Bilinear);
        img.Convert(Image.Format.Rgb8);
        return (img.GetWidth(), img.GetHeight(), img.GetData());
    }

    /// <summary>A mesh row unpacked once: positions, linear vertex colours (no glow), texture coordinates, indices, runs.</summary>
    private sealed record Looked(Vector3[] Positions, Color[] Colors, Vector2[]? Uvs, int[] Indices, (int Texture, int First, int Count)[] Runs)
    {
        public static Looked Of(RigData.MeshRow row)
        {
            var cols = RigData.VertexColorsOf(row);
            for (int i = 0; i < cols.Length; i++) cols[i] = cols[i].SrgbToLinear() with { A = 0f };
            return new Looked(RigData.Positions(row), cols, RigData.UvsOf(row), RigData.Indices(row), RigData.RunsOf(row));
        }
    }

    /// <summary>Triangles gathered per texture, then one mesh with a surface each.</summary>
    private sealed class Surfaces
    {
        private sealed class Lists
        {
            public readonly List<Vector3> V = new();
            public readonly List<Color> C = new();
            public readonly List<Vector2> Uv = new();
            public readonly List<int> I = new();
        }

        private readonly SortedDictionary<int, Lists> _by = new();
        private int[] _map = Array.Empty<int>();

        public bool Empty => _by.Count == 0;

        /// <summary>A mesh's runs, its vertices through <paramref name="xf"/>.</summary>
        public void Add(Looked mesh, Transform3D xf)
        {
            if (_map.Length < mesh.Positions.Length) _map = new int[mesh.Positions.Length];
            foreach (var (texture, first, count) in mesh.Runs)
            {
                int tex = mesh.Uvs != null ? texture : -1;
                if (!_by.TryGetValue(tex, out var s)) _by[tex] = s = new Lists();
                Array.Fill(_map, -1, 0, mesh.Positions.Length);
                for (int c = first * 3; c < (first + count) * 3 && c < mesh.Indices.Length; c++)
                {
                    int v = mesh.Indices[c];
                    if (_map[v] < 0)
                    {
                        _map[v] = s.V.Count;
                        s.V.Add(xf * mesh.Positions[v]);
                        s.C.Add(mesh.Colors[v]);
                        if (tex >= 0) s.Uv.Add(mesh.Uvs![v]);
                    }
                    s.I.Add(_map[v]);
                }
            }
        }

        public ArrayMesh ToMesh(Func<int, Material> material)
        {
            Core.ShowcaseTrace.Mark();
            var mesh = new ArrayMesh();
            foreach (var (tex, s) in _by)
            {
                if (s.I.Count == 0) continue;
                using var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = s.V.ToArray();
                arrays[(int)Mesh.ArrayType.Color] = s.C.ToArray();
                if (tex >= 0) arrays[(int)Mesh.ArrayType.TexUV] = s.Uv.ToArray();
                arrays[(int)Mesh.ArrayType.Index] = s.I.ToArray();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material(tex));
            }
            return mesh;
        }
    }

    /// <summary>A mesh row as one mesh in its own frame, a surface per texture.</summary>
    private ArrayMesh RowMesh(Looked row)
    {
        var s = new Surfaces();
        s.Add(row, Transform3D.Identity);
        return s.ToMesh(LookMaterial);
    }

    private void BuildObjects()
    {
        if (Imported is not { Objects.Count: > 0 } rig) return;
        var root = new Node3D { Name = "MvrObjects" };
        var uses = new int[rig.Meshes.Count];
        foreach (var o in rig.Objects) uses[o.Mesh]++;
        var looked = rig.Meshes.Select(Looked.Of).ToArray();

        // the one-offs, baked
        var baked = new Surfaces();
        foreach (var o in rig.Objects)
            if (uses[o.Mesh] < InstancedFrom) baked.Add(looked[o.Mesh], RigData.X(o.Xf));
        if (!baked.Empty)
            root.AddChild(new MeshInstance3D { Name = "Baked", Mesh = baked.ToMesh(LookMaterial) });

        // the repeated, instanced
        var multi = new MultiMesh?[rig.Meshes.Count];
        for (int m = 0; m < rig.Meshes.Count; m++)
        {
            if (uses[m] < InstancedFrom) continue;
            multi[m] = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = RowMesh(looked[m]),
                InstanceCount = uses[m],
            };
            root.AddChild(new MultiMeshInstance3D { Name = $"Mesh{m}", Multimesh = multi[m] });
        }
        var next = new int[rig.Meshes.Count];
        foreach (var o in rig.Objects)
            multi[o.Mesh]?.SetInstanceTransform(next[o.Mesh]++, RigData.X(o.Xf));
        AddChild(root);
        BuildObjectCollision(rig, looked.Select(l => l.Positions).ToArray(), looked.Select(l => l.Indices).ToArray());   // #974
        ObjectsDrawn = rig.Objects.Count;
    }
}
