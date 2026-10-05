using System.Collections.Generic;
using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A front door's leaves. Most doors have one, in the interior, hinged on one jamb and swinging
/// into the room; shut, it is solid, open, it is only drawn, so it can never pin a player against
/// the wall. A barn's door is a pair swinging out into the street, on the facade (<see cref="CreateOutward"/>),
/// and a garage's rolls up into its lintel, on the facade too (<see cref="CreateRollUp"/>).
/// </summary>
public partial class DoorLeaf : Node3D
{
    /// <summary>
    /// How far an inward leaf swings when fully open, radians: square to the wall, no further. A
    /// house's door can stand 0.2 m from the stair core's side wall, and any more swings the leaf into it.
    /// </summary>
    private const float InwardAngle = Mathf.Pi / 2;
    /// <summary>
    /// How far an outward leaf swings: a little past square, splayed. A barn's pair spans nearly
    /// the whole wall, so there is no facade beside the jambs for a leaf to lie back against.
    /// </summary>
    private const float OutwardAngle = 1.75f; // 100°
    private const float Thickness = 0.04f;
    private const float OutwardThickness = 0.06f;
    /// <summary>
    /// Where an outward pair's hinges stand, in front of the facade: its back face clear of the
    /// jambs (11 cm out, <c>BuildingMeshBuilder.AppendDoor</c>) and of the portal mouth.
    /// </summary>
    private static float OutwardHingeZ => Mathf.Max(DoorLink.OutsideQuadOffset, 0.11f) + 0.01f + OutwardThickness;
    /// <summary>An outward leaf's bottom: clear of the doorstep (12 cm above the sill).</summary>
    private const float OutwardBottom = 0.13f;
    /// <summary>
    /// A roll-up door's slats, in front of the facade: clear of the portal mouth, inside the jambs
    /// (which stand to 11 cm), where the facade's baked shut door is.
    /// </summary>
    private const float RollZ0 = 0.05f, RollZ1 = 0.08f;
    /// <summary>How much of a roll-up door still shows under the lintel when fully up.</summary>
    private const float RolledUp = 0.03f;
    /// <summary>Seconds a roll-up door takes, up or down: a car driving up has to find it open.</summary>
    public const float RollSeconds = 1f;

    // each hinge and the way it turns to open: + into the room, - out
    private readonly List<(Node3D Hinge, float Sign)> _hinges = new();
    private CollisionShape3D? _shape;
    private float _angle;
    // a roll-up door: the slats, hung from the lintel, squashed up into it
    private Node3D? _roll;

    /// <summary>On the facade (a pair, a roll-up door), freed with its link, rather than a leaf of an interior.</summary>
    public bool Outward { get; private init; }

    /// <summary>
    /// A facade door as its interior sees it (<see cref="CreateShutter"/>): it never swings, only
    /// shows, and stops the way out, while the door is shut.
    /// </summary>
    public bool Shutter { get; private init; }

    /// <summary>Whether a door is an outward pair: a barn's, swinging onto the yard.</summary>
    public static bool SwingsOut(DoorHang hang) => hang == DoorHang.OutwardPair;

    /// <summary>Whether a door rolls up into its lintel: a garage's, or a loading bay.</summary>
    public static bool RollsUp(DoorHang hang) => hang == DoorHang.RollUp;

    /// <summary>Whether a door hangs on the facade, with its link, rather than in the interior.</summary>
    public static bool OnFacade(DoorHang hang) => hang != DoorHang.Inward;

    /// <summary>How a kind of building's main door hangs: the default every door starts from.</summary>
    public static bool SwingsOut(BuildingKind kind) => SwingsOut(DoorBudget.HangFor(kind));
    public static bool RollsUp(BuildingKind kind) => RollsUp(DoorBudget.HangFor(kind));
    public static bool OnFacade(BuildingKind kind) => OnFacade(DoorBudget.HangFor(kind));

    /// <summary>
    /// The facade leaf for a door, on its doorway frame (<see cref="DoorLink.Outside"/>). The
    /// hang says how it moves, the kind only how it is painted.
    /// </summary>
    public static DoorLeaf CreateOnFacade(string door, Transform3D doorway, float width, float height,
        DoorHang hang, BuildingKind kind, Material material) =>
        RollsUp(hang) ? CreateRollUp(door, doorway, width, height, material)
            : CreateOutward(door, doorway, width, height, kind, material);

    /// <summary>A leaf's width: a pair splits the opening, any other door is one leaf.</summary>
    public static float LeafWidth(DoorHang hang, float doorWidth) => SwingsOut(hang) ? doorWidth / 2 : doorWidth;

    /// <summary>
    /// How much deeper than usual an open door is in reach, on the side its leaves stand: a big
    /// leaf is worked by its free edge, as far out as it sticks. Nothing for a house door's 1 m leaf.
    /// </summary>
    public static float OpenReach(DoorHang hang, float doorWidth) => Math.Max(0f, LeafWidth(hang, doorWidth) - 1f);

    /// <summary>
    /// A leaf for an entrance, in the interior node's frame: <paramref name="doorway"/> is the
    /// doorway frame (origin on the sill at the wall's outer face, Z toward the street).
    /// </summary>
    public static DoorLeaf Create(string door, Transform3D doorway, float width, float height, BuildingKind kind, Material material)
    {
        var leaf = new DoorLeaf { Name = "Leaf_" + door, Transform = doorway, _angle = InwardAngle };
        // on the room side of the reveal, where the old baked leaf stood
        var hinge = leaf.AddHinge(new Vector3(-width / 2, 0, -InteriorGenerator.WallInset), +1,
            InteriorMeshBuilder.Leaf(width, height, Thickness, kind), 0, material);

        var body = new StaticBody3D { Name = "Body" };
        leaf._shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(width, height, Thickness) },
            Position = new Vector3(width / 2, height / 2, Thickness / 2),
        };
        body.AddChild(leaf._shape);
        hinge.AddChild(body);

        // open, nothing walks into it, but the third-person camera's arm must not end inside it:
        // the lens sat in the leaf's thickness, a screen of brown (#388)
        var lensStop = new StaticBody3D { Name = "LensStop", CollisionLayer = CameraOnlyLayer, CollisionMask = 0 };
        leaf._lensStop = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(width, height, Thickness) },
            Position = new Vector3(width / 2, height / 2, Thickness / 2),
            Disabled = true,
        };
        lensStop.AddChild(leaf._lensStop);
        hinge.AddChild(lensStop);
        return leaf;
    }

    /// <summary>A physics layer only the camera arm sweeps (<c>FootPlayer.CameraMask</c>): an open leaf.</summary>
    public const uint CameraOnlyLayer = 1u << 12;
    private CollisionShape3D? _lensStop;

    /// <summary>
    /// A barn's pair, in world space on the facade doorway frame <paramref name="doorway"/>
    /// (<see cref="DoorLink.Outside"/>): one leaf per jamb, meeting in the middle. Never solid:
    /// shut, the facade behind it is.
    /// </summary>
    public static DoorLeaf CreateOutward(string door, Transform3D doorway, float width, float height, BuildingKind kind, Material material)
    {
        // top level: its transform is the world one, wherever it hangs in the tree
        var leaf = new DoorLeaf { Name = "Leaf_" + door, TopLevel = true, Transform = doorway, Outward = true, _angle = OutwardAngle };
        // the hinge on each leaf's street face, so it swings back without cutting into its jamb, and
        // on the jamb's face: shut, the pair laps the jambs by 4 cm and meets in the middle, with no
        // chink of the portal behind (the leaf mesh starts 1 cm off its hinge and ends 1 cm short)
        float z = OutwardHingeZ, hinge = width / 2 + 0.05f, leafWidth = width / 2 + 0.06f;
        leaf.AddHinge(new Vector3(-hinge, 0, z), -1,
            InteriorMeshBuilder.Leaf(leafWidth, height, OutwardThickness, kind, bottom: OutwardBottom), OutwardThickness, material);
        leaf.AddHinge(new Vector3(hinge, 0, z), +1,
            InteriorMeshBuilder.Leaf(leafWidth, height, OutwardThickness, kind, mirrored: true, bottom: OutwardBottom), OutwardThickness, material);
        return leaf;
    }

    /// <summary>
    /// A garage's roll-up door, in world space on the facade doorway frame <paramref name="doorway"/>:
    /// slats across the opening, hung from the lintel and rolled up into it. Never solid, like a
    /// barn's pair: shut, the facade behind it is.
    /// </summary>
    public static DoorLeaf CreateRollUp(string door, Transform3D doorway, float width, float height, Material material)
    {
        var leaf = new DoorLeaf { Name = "Leaf_" + door, TopLevel = true, Transform = doorway, Outward = true };
        leaf._roll = leaf.AddHinge(new Vector3(0, height, 0), 0, InteriorMeshBuilder.RollUpLeaf(width, height, RollZ0, RollZ1), 0, material);
        return leaf;
    }

    /// <summary>
    /// A barn's pair or a garage's roll-up door seen from inside, in the interior node's frame like
    /// <see cref="Create"/>: the real one hangs on the facade (<see cref="OnFacade"/>), out of the
    /// interior's world, so without this a shut barn door was a bare hole from inside, and let
    /// anyone walk (or drive) out through it. Shown and solid only
    /// while the door is shut; open, or swinging, the portal shows the real leaves.
    /// </summary>
    public static DoorLeaf CreateShutter(string door, Transform3D doorway, float width, float height, BuildingKind kind, Material material)
    {
        var leaf = new DoorLeaf { Name = "Shutter_" + door, Transform = doorway, Shutter = true };
        float half = width / 2, z = -InteriorGenerator.WallInset;
        if (RollsUp(kind))
            // a garage's roll-up door: its slats, the same as outside
            leaf.AddHinge(new Vector3(0, height, 0), 0, InteriorMeshBuilder.RollUpLeaf(width, height, z - Thickness, z), 0, material);
        else
        {
            leaf.AddHinge(new Vector3(-half, 0, z), 0, InteriorMeshBuilder.Leaf(half, height, Thickness, kind), 0, material);
            leaf.AddHinge(new Vector3(half, 0, z), 0, InteriorMeshBuilder.Leaf(half, height, Thickness, kind, mirrored: true), 0, material);
        }

        var body = new StaticBody3D { Name = "Body" };
        leaf._shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(width, height, Thickness) },
            Position = new Vector3(0, height / 2, z + Thickness / 2),
        };
        body.AddChild(leaf._shape);
        leaf.AddChild(body);
        return leaf;
    }

    private Node3D AddHinge(Vector3 at, float sign, InteriorMeshBuilder.MeshData data, float back, Material material)
    {
        var hinge = new Node3D { Name = "Hinge" + _hinges.Count, Position = at };
        AddChild(hinge);
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        hinge.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh, Position = new Vector3(0, 0, -back) });
        _hinges.Add((hinge, sign));
        return hinge;
    }

    /// <summary>0 shut .. 1 open.</summary>
    public void SetSwing(float swing)
    {
        // eased, so it starts and stops like something with weight on a hinge
        float s = swing * swing * (3 - 2 * swing);
        if (_roll != null) _roll.Scale = new Vector3(1, Mathf.Max(RolledUp, 1 - s), 1);
        else
            foreach (var (hinge, sign) in _hinges)
                hinge.Rotation = new Vector3(0, sign * s * _angle, 0);
        if (_shape != null) _shape.Disabled = swing > 0.02f;
        if (_lensStop != null) _lensStop.Disabled = swing <= 0.02f;
        // from the first moment it opens, the portal behind shows the real pair swinging
        if (Shutter) Visible = swing <= 0f;
    }
}
