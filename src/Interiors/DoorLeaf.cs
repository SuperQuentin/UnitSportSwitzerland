using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A front door's leaf in an interior, hinged on one jamb and swinging into the room. Shut, it
/// is solid; open, it is only drawn, so it can never pin a player against the wall.
/// </summary>
public partial class DoorLeaf : Node3D
{
    /// <summary>How far it swings when fully open, radians.</summary>
    private const float OpenAngle = 1.75f;
    private const float Thickness = 0.04f;

    private Node3D _hinge = null!;
    private CollisionShape3D _shape = null!;

    /// <summary>
    /// A leaf for an entrance, in the interior node's frame: <paramref name="doorway"/> is the
    /// doorway frame (origin on the sill at the wall's outer face, Z toward the street).
    /// </summary>
    public static DoorLeaf Create(string door, Transform3D doorway, float width, float height, BuildingKind kind, Material material)
    {
        var leaf = new DoorLeaf { Name = "Leaf_" + door, Transform = doorway };
        // on the room side of the reveal, where the old baked leaf stood
        leaf._hinge = new Node3D { Name = "Hinge", Position = new Vector3(-width / 2, 0, -InteriorGenerator.WallInset) };
        leaf.AddChild(leaf._hinge);

        var data = InteriorMeshBuilder.Leaf(width, height, Thickness, kind);
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        leaf._hinge.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh });

        var body = new StaticBody3D { Name = "Body" };
        leaf._shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(width, height, Thickness) },
            Position = new Vector3(width / 2, height / 2, Thickness / 2),
        };
        body.AddChild(leaf._shape);
        leaf._hinge.AddChild(body);
        return leaf;
    }

    /// <summary>0 shut .. 1 open.</summary>
    public void SetSwing(float swing)
    {
        // eased, so it starts and stops like something with weight on a hinge
        float s = swing * swing * (3 - 2 * swing);
        _hinge.Rotation = new Vector3(0, s * OpenAngle, 0);
        _shape.Disabled = swing > 0.02f;
    }
}
