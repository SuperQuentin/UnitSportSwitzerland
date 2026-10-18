using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Farming;

/// <summary>
/// The procedural cow (#863), low-poly: a body with its legs, udder, tail and cowbell in one mesh,
/// and the head in another, pivoting at the neck so a herd's MultiMesh can lower it into the grass
/// or turn it to a passer-by (<see cref="Herds"/>). Two Swiss breeds: Braunvieh (grey-brown, a pale
/// ring round the muzzle) and Simmental (red-brown patches on white, a white head). Node space,
/// −Z forward, feet on y = 0; ~2.3 m long, 1.4 m at the withers. Vertex alpha 0 everywhere
/// (the prop shader reads alpha as a lamp), but for nothing: a cow is not a light.
/// </summary>
public static class CowMeshBuilder
{
    /// <summary>The neck joint the head turns about, node space.</summary>
    public static readonly Vector3 Neck = new(0, 1.22f, -0.98f);

    /// <summary>The head lowered to graze, rad (pitch, nose down).</summary>
    public const float GrazePitch = 1.05f;

    private static Color M(float r, float g, float b) => new(r, g, b, 0f);

    private static readonly Color Hoof = M(0.12f, 0.10f, 0.09f), Horn = M(0.82f, 0.78f, 0.66f), Udder = M(0.86f, 0.62f, 0.60f);
    private static readonly Color Nose = M(0.16f, 0.13f, 0.12f), Brass = M(0.80f, 0.62f, 0.22f), Strap = M(0.30f, 0.16f, 0.08f);
    private static readonly Color BrownCoat = M(0.40f, 0.32f, 0.26f), BrownDark = M(0.30f, 0.24f, 0.20f), BrownMuzzle = M(0.82f, 0.77f, 0.66f);
    private static readonly Color White = M(0.90f, 0.88f, 0.82f), Red = M(0.56f, 0.26f, 0.12f);

    private static readonly ArrayMesh?[] Bodies = new ArrayMesh?[2], Heads = new ArrayMesh?[2];

    /// <summary>A breed's body (legs, udder, tail, bell), shared by every herd of it.</summary>
    public static ArrayMesh Body(CowBreed breed) => Bodies[(int)breed] ??= BuildBody(breed);

    /// <summary>A breed's head, its pivot at <see cref="Neck"/> (the mesh's origin).</summary>
    public static ArrayMesh Head(CowBreed breed) => Heads[(int)breed] ??= BuildHead(breed);

    private static ArrayMesh BuildBody(CowBreed breed)
    {
        var s = new MeshScratch();
        bool sim = breed == CowBreed.Simmental;
        Color coat = sim ? White : BrownCoat, back = sim ? Red : BrownDark, leg = sim ? White : BrownCoat;

        // the barrel: shoulders, belly, rump, slightly deeper at the chest
        s.Box(new Vector3(0, 1.02f, -0.55f), new Vector3(0.78f, 0.68f, 0.70f), sim ? Red : coat);
        s.Box(new Vector3(0, 0.98f, 0.05f), new Vector3(0.82f, 0.66f, 0.60f), coat);
        s.Box(new Vector3(0, 1.04f, 0.62f), new Vector3(0.76f, 0.62f, 0.62f), sim ? Red : coat);
        // the back line, the hips and the withers a little above the barrel
        s.Box(new Vector3(0, 1.36f, -0.10f), new Vector3(0.50f, 0.10f, 1.60f), back);
        s.Box(new Vector3(0, 1.38f, 0.78f), new Vector3(0.66f, 0.12f, 0.26f), back);
        if (sim)
        {
            // white patches breaking the red over the flanks
            s.Box(new Vector3(0.41f, 0.96f, -0.42f), new Vector3(0.02f, 0.34f, 0.30f), White);
            s.Box(new Vector3(-0.41f, 1.02f, 0.66f), new Vector3(0.02f, 0.30f, 0.36f), White);
            s.Box(new Vector3(0, 0.66f, -0.55f), new Vector3(0.70f, 0.04f, 0.60f), White);   // the belly white
        }
        // the neck, from the shoulders up to the head's joint
        s.Box(new Vector3(0, 1.16f, -0.92f), new Vector3(0.46f, 0.48f, 0.34f), sim ? White : coat, new Basis(Vector3.Right, 0.35f));
        // dewlap
        s.Box(new Vector3(0, 0.86f, -0.92f), new Vector3(0.16f, 0.26f, 0.28f), sim ? White : coat);

        // the legs, tapering to dark hooves
        foreach (var (x, z) in new[] { (-0.26f, -0.70f), (0.26f, -0.70f), (-0.26f, 0.72f), (0.26f, 0.72f) })
        {
            s.Tube(new Vector3(x, 0.78f, z), new Vector3(x, 0.10f, z), 0.10f, 0.07f, leg, 5);
            s.Box(new Vector3(x, 0.05f, z - 0.02f), new Vector3(0.15f, 0.10f, 0.18f), Hoof);
        }
        // udder and teats
        s.Box(new Vector3(0, 0.62f, 0.42f), new Vector3(0.36f, 0.20f, 0.34f), Udder);
        foreach (var (x, z) in new[] { (-0.09f, 0.34f), (0.09f, 0.34f), (-0.09f, 0.50f), (0.09f, 0.50f) })
            s.Tube(new Vector3(x, 0.53f, z), new Vector3(x, 0.45f, z), 0.025f, 0.02f, Udder, 4);
        // the tail, from the tail head down to a dark switch
        s.Tube(new Vector3(0, 1.34f, 0.94f), new Vector3(0, 0.62f, 1.00f), 0.04f, 0.03f, sim ? White : coat, 4);
        s.Box(new Vector3(0, 0.52f, 1.00f), new Vector3(0.09f, 0.18f, 0.09f), sim ? White : BrownDark);

        // the cowbell on its broad strap round the neck
        s.Box(new Vector3(0, 1.05f, -1.06f), new Vector3(0.50f, 0.08f, 0.06f), Strap, new Basis(Vector3.Right, 0.35f));
        s.Tube(new Vector3(0, 0.84f, -1.12f), new Vector3(0, 0.62f, -1.14f), 0.08f, 0.13f, Brass, 6);
        s.Box(new Vector3(0, 0.88f, -1.12f), new Vector3(0.10f, 0.06f, 0.06f), Strap);
        return s.Build();
    }

    private static ArrayMesh BuildHead(CowBreed breed)
    {
        var s = new MeshScratch();
        bool sim = breed == CowBreed.Simmental;
        Color face = sim ? White : BrownCoat, muzzle = sim ? M(0.92f, 0.80f, 0.74f) : BrownMuzzle;
        // built at the joint: forward and down from it, the forehead flat, the muzzle broad
        s.Box(new Vector3(0, 0.06f, -0.22f), new Vector3(0.34f, 0.36f, 0.30f), face);
        s.Box(new Vector3(0, -0.06f, -0.46f), new Vector3(0.28f, 0.28f, 0.26f), face, new Basis(Vector3.Right, 0.25f));
        s.Box(new Vector3(0, -0.14f, -0.62f), new Vector3(0.26f, 0.20f, 0.12f), muzzle);
        s.Box(new Vector3(0, -0.14f, -0.685f), new Vector3(0.20f, 0.12f, 0.02f), Nose);
        // ears out to the side, small horns curving up
        s.Box(new Vector3(-0.27f, 0.12f, -0.16f), new Vector3(0.20f, 0.06f, 0.12f), sim ? Red : BrownDark, new Basis(Vector3.Forward, -0.25f));
        s.Box(new Vector3(0.27f, 0.12f, -0.16f), new Vector3(0.20f, 0.06f, 0.12f), sim ? Red : BrownDark, new Basis(Vector3.Forward, 0.25f));
        s.Tube(new Vector3(-0.14f, 0.24f, -0.20f), new Vector3(-0.24f, 0.36f, -0.24f), 0.035f, 0.015f, Horn, 4);
        s.Tube(new Vector3(0.14f, 0.24f, -0.20f), new Vector3(0.24f, 0.36f, -0.24f), 0.035f, 0.015f, Horn, 4);
        // eyes
        s.Box(new Vector3(-0.17f, 0.07f, -0.30f), new Vector3(0.02f, 0.05f, 0.05f), Nose);
        s.Box(new Vector3(0.17f, 0.07f, -0.30f), new Vector3(0.02f, 0.05f, 0.05f), Nose);
        return s.Build();
    }

    /// <summary>A whole cow as one node, head at <paramref name="headPitch"/>: the model viewer, and anything that wants one cow.</summary>
    public static Node3D Cow(CowBreed breed, float headPitch = 0f)
    {
        var root = new Node3D { Name = $"Cow{breed}" };
        var material = Herds.Material();
        root.AddChild(new MeshInstance3D { Name = "Body", Mesh = Body(breed), MaterialOverride = material });
        root.AddChild(new MeshInstance3D
        {
            Name = "Head", Mesh = Head(breed), MaterialOverride = material, Position = Neck,
            Basis = new Basis(Vector3.Right, -headPitch),
        });
        return root;
    }

    /// <summary>Both breeds, standing and grazing, in the model viewer (--models).</summary>
    [Showcase("Animals", "Cow")]
    private static IEnumerable<(string, Func<Node3D>)> Showcase() =>
    [
        ("Braunvieh", () => Cow(CowBreed.Brown)),
        ("Braunvieh grazing", () => Cow(CowBreed.Brown, GrazePitch)),
        ("Simmental", () => Cow(CowBreed.Simmental)),
        ("Simmental grazing", () => Cow(CowBreed.Simmental, GrazePitch)),
    ];
}
