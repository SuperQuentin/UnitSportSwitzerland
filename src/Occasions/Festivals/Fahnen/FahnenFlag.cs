using Godot;
using UnitSport.Core;

namespace UnitSport.Occasions.Festivals.Fahnen;

/// <summary>
/// The flag swinger's flag (#1269 part 5): a 1.5 m staff with a 1.2 m square of cloth. The staff and the cloth are two meshes built once; the
/// swinger moves and turns the node (<see cref="FlagMotion"/>), and the cloth ripples in <c>shaders/flag_cloth.gdshader</c> by the swing
/// uniform, which is written only when it moved. The node's origin is the grip, a third of the way up the staff.
/// </summary>
public partial class FahnenFlag : Node3D
{
    public const float StaffLength = 1.5f, ClothSize = 1.2f, GripAt = 0.5f;
    private const int Cols = 9, Rows = 7;

    private static ArrayMesh? _cloth;
    private static CylinderMesh? _staff;
    private static BoxMesh? _knob;
    private static ShaderMaterial? _material;
    private static StandardMaterial3D? _wood, _gold;

    private ShaderMaterial? _mine;
    private float _swing = -1f;

    public override void _Ready()
    {
        _staff ??= new CylinderMesh { TopRadius = 0.014f, BottomRadius = 0.02f, Height = StaffLength, RadialSegments = 8, Rings = 1 };
        _knob ??= new BoxMesh { Size = new Vector3(0.05f, 0.05f, 0.05f) };
        _wood ??= new StandardMaterial3D { AlbedoColor = new Color(0.52f, 0.38f, 0.22f), Roughness = 0.9f };
        _gold ??= new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.7f, 0.2f), Roughness = 0.5f, Metallic = 0.4f };
        AddChild(new MeshInstance3D { Name = "Staff", Mesh = _staff, MaterialOverride = _wood, Position = new Vector3(0, StaffLength * 0.5f - GripAt, 0) });
        AddChild(new MeshInstance3D { Name = "Knob", Mesh = _knob, MaterialOverride = _gold, Position = new Vector3(0, StaffLength - GripAt + 0.02f, 0) });
        _material ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/flag_cloth.gdshader") };
        _mine = (ShaderMaterial)_material.Duplicate();
        // the cloth hangs from the staff's top: its pinned edge is the staff, it streams to the right of the node
        AddChild(new MeshInstance3D
        {
            Name = "Cloth", Mesh = Cloth(), MaterialOverride = _mine, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0.02f, StaffLength - GripAt - ClothSize * 0.5f - 0.04f, 0),
        });
        float want = _swing;
        _swing = -1f;
        Swing = Mathf.Max(0f, want);
    }

    /// <summary>How hard the flag is being swung, 0..1: the cloth's ripple. Written to the material only when it moved.</summary>
    public float Swing
    {
        get => _swing;
        set
        {
            value = Mathf.Clamp(value, 0f, 1f);
            if (Mathf.Abs(value - _swing) < 0.02f) return;
            _swing = value;
            _mine?.SetShaderParameter("swing", value);
        }
    }

    /// <summary>A grid of quads, UV 0..1 over the cloth, its left edge on the staff; built once for every flag.</summary>
    private static ArrayMesh Cloth()
    {
        if (_cloth != null) return _cloth;
        var verts = new Vector3[Cols * Rows];
        var uvs = new Vector2[Cols * Rows];
        var normals = new Vector3[Cols * Rows];
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                float u = c / (float)(Cols - 1), v = r / (float)(Rows - 1);
                int i = r * Cols + c;
                verts[i] = new Vector3(u * ClothSize, (0.5f - v) * ClothSize, 0);
                uvs[i] = new Vector2(u, v);
                normals[i] = Vector3.Back;
            }
        var idx = new List<int>();
        for (int r = 0; r < Rows - 1; r++)
            for (int c = 0; c < Cols - 1; c++)
            {
                int a = r * Cols + c, b = a + 1, d = a + Cols, e = d + 1;
                idx.AddRange([a, b, d, b, e, d]);
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        ShowcaseTrace.Mark();
        return _cloth = mesh;
    }

    /// <summary>The flag in the model viewer, mid-swing so the cloth shows its ripple.</summary>
    [Showcase("Festivals", "Fahnenschwinger flag")]
    private static Node3D ShowFlag()
    {
        var root = new Node3D();
        root.AddChild(new FahnenFlag { Position = new Vector3(0, GripAt, 0), Swing = 0.5f });
        return root;
    }
}
