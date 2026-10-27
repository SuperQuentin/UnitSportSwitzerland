using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The guide marmot (#1227, rewards plan 5), low-poly and rigid: a body, a head pivoting at the neck, one leg mesh
/// shared by the four legs (each its own node, turned for the gait), and a tail; nothing rebuilt as it runs
/// (<c>general/no-mesh-rebuild-per-frame</c>). Node space, −Z forward, feet on y = 0, ~0.55 m long. Vertex alpha 0
/// (the prop shader reads alpha as a lamp).
/// </summary>
public static class MarmotMeshBuilder
{
    private static Color M(string hex) { var c = new Color(hex); c.A = 0; return c; }

    private static readonly Color Fur = M("8a6a48"), FurDark = M("5e4630"), Belly = M("b89a72"), Muzzle = M("e8dcc6"), Nose = M("201812"), Eye = M("0c0c0c");

    /// <summary>Where the parts join, node space.</summary>
    public static readonly Vector3 Neck = new(0, 0.2f, -0.21f), Tail = new(0, 0.16f, 0.22f);
    public static readonly Vector3[] Hips = { new(-0.1f, 0.08f, -0.13f), new(0.1f, 0.08f, -0.13f), new(-0.1f, 0.08f, 0.12f), new(0.1f, 0.08f, 0.12f) };

    private static ArrayMesh? _body, _head, _leg, _tail;

    [Showcase("Parts", "Marmot body")]
    public static ArrayMesh Body() => _body ??= Build(s =>
    {
        using (s.Smoothing(true))
        {
            // a plump, low barrel: the rump the widest, the shoulders narrower
            s.RoundedBox(new Vector3(0, 0.15f, -0.06f), new Vector3(0.3f, 0.2f, 0.26f), Fur, null, 5, 8);
            s.RoundedBox(new Vector3(0, 0.16f, 0.1f), new Vector3(0.24f, 0.19f, 0.22f), Fur, null, 5, 8);
            s.RoundedBox(new Vector3(0, 0.09f, 0.0f), new Vector3(0.22f, 0.07f, 0.36f), Belly, null, 5, 8);
        }
    }, Vector3.Zero);

    /// <summary>The head, its origin at <see cref="Neck"/>.</summary>
    [Showcase("Parts", "Marmot head")]
    public static ArrayMesh Head() => _head ??= Build(s =>
    {
        var n = new Vector3(0, Neck.Y, -Neck.Z);   // authored (+Z forward)
        s.RoundedBox(n + new Vector3(0, 0.03f, 0.06f), new Vector3(0.14f, 0.12f, 0.14f), Fur, null, 4, 6);
        s.RoundedBox(n + new Vector3(0, 0.0f, 0.13f), new Vector3(0.08f, 0.06f, 0.05f), Muzzle, null, 3, 4);
        s.Box(n + new Vector3(0, 0.02f, 0.158f), new Vector3(0.03f, 0.02f, 0.012f), Nose);
        foreach (float x in new[] { -0.04f, 0.04f })
        {
            s.Box(n + new Vector3(x, 0.06f, 0.12f), new Vector3(0.016f, 0.016f, 0.01f), Eye);
            s.Box(n + new Vector3(x * 1.4f, 0.1f, 0.04f), new Vector3(0.03f, 0.025f, 0.015f), FurDark);
        }
    }, new Vector3(0, Neck.Y, -Neck.Z));

    /// <summary>One leg, its origin at the hip, down to a dark paw.</summary>
    [Showcase("Parts", "Marmot leg")]
    public static ArrayMesh Leg() => _leg ??= Build(s =>
    {
        s.Tube(new Vector3(0, 0.08f, 0), new Vector3(0, 0.02f, 0.01f), 0.034f, 0.024f, FurDark, 5);
        s.Box(new Vector3(0, 0.012f, 0.02f), new Vector3(0.04f, 0.024f, 0.05f), Nose);
    }, new Vector3(0, 0.08f, 0));

    /// <summary>The tail, its origin at its base.</summary>
    [Showcase("Parts", "Marmot tail")]
    public static ArrayMesh TailMesh() => _tail ??= Build(s =>
    {
        var b = new Vector3(0, Tail.Y, -Tail.Z);
        s.Tube(b, b + new Vector3(0, 0.02f, -0.16f), 0.035f, 0.025f, FurDark, 5);
    }, new Vector3(0, Tail.Y, -Tail.Z));

    /// <summary>The marmot assembled: Body, Head, Leg0-3, Tail as nodes.</summary>
    [Showcase("Animals", "Marmot")]
    public static Node3D Assemble()
    {
        var material = RewardMeshes.Material();
        var root = new Node3D { Name = "Marmot" };
        root.AddChild(new MeshInstance3D { Name = "Body", Mesh = Body(), MaterialOverride = material });
        var head = new Node3D { Name = "Head", Position = Neck };
        head.AddChild(new MeshInstance3D { Mesh = Head(), MaterialOverride = material });
        root.AddChild(head);
        for (int i = 0; i < 4; i++)
        {
            var leg = new Node3D { Name = $"Leg{i}", Position = Hips[i] };
            leg.AddChild(new MeshInstance3D { Mesh = Leg(), MaterialOverride = material });
            root.AddChild(leg);
        }
        var tail = new Node3D { Name = "Tail", Position = Tail };
        tail.AddChild(new MeshInstance3D { Mesh = TailMesh(), MaterialOverride = material });
        root.AddChild(tail);
        return root;
    }

    private static ArrayMesh Build(Action<MeshScratch> parts, Vector3 pivot)
    {
        var s = new MeshScratch();
        parts(s);
        return s.Build(pivot);
    }
}
