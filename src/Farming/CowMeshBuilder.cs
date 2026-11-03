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

    private static readonly ArrayMesh?[] Bodies = new ArrayMesh?[4], Heads = new ArrayMesh?[2];

    /// <summary>A breed's body (legs, udder, tail, bell), shared by every herd of it.</summary>
    public static ArrayMesh Body(CowBreed breed) => Bodies[(int)breed] ??= BuildBody(breed, true);

    /// <summary>A breed's body without its little cowbell and strap: the Désalpe's cows wear a Treichel of their own (#1269 part 7).</summary>
    public static ArrayMesh BodyWithoutBell(CowBreed breed) => Bodies[2 + (int)breed] ??= BuildBody(breed, false);

    /// <summary>A breed's head, its pivot at <see cref="Neck"/> (the mesh's origin).</summary>
    public static ArrayMesh Head(CowBreed breed) => Heads[(int)breed] ??= BuildHead(breed);

    /// <summary>A rounded box, low-poly (the PS1 and Cartoon looks both want facets, not a sphere).</summary>
    private static void Lump(MeshScratch s, Vector3 centre, Vector3 size, Color c, Basis? turn = null) =>
        s.RoundedBox(centre, size, c, turn, 5, 8);

    private static ArrayMesh BuildBody(CowBreed breed, bool bell)
    {
        // authored facing +Z, as every MeshScratch model is (Build turns it to face -Z)
        var s = new MeshScratch();
        bool sim = breed == CowBreed.Simmental;
        Color coat = sim ? White : BrownCoat, patch = sim ? Red : BrownCoat, leg = sim ? White : BrownCoat;
        using (s.Smoothing(true))
        {
            // the barrel in three overlapping lumps: shoulders, belly, rump (a Simmental's red patches
            // fore and aft, its middle white), the withers and the hip bones standing a little proud
            Lump(s, new Vector3(0, 1.04f, 0.55f), new Vector3(0.78f, 0.74f, 0.86f), patch);
            Lump(s, new Vector3(0, 0.98f, -0.04f), new Vector3(0.84f, 0.76f, 0.80f), coat);
            Lump(s, new Vector3(0, 1.05f, -0.58f), new Vector3(0.78f, 0.70f, 0.78f), patch);
            Lump(s, new Vector3(0, 1.26f, 0.60f), new Vector3(0.46f, 0.30f, 0.50f), patch);
            Lump(s, new Vector3(0, 1.30f, -0.74f), new Vector3(0.66f, 0.26f, 0.34f), patch);
            if (sim) Lump(s, new Vector3(0, 0.70f, 0.1f), new Vector3(0.56f, 0.16f, 1.30f), White);   // the white belly
            // the neck, rising forward to the head's joint, and the dewlap under it
            Lump(s, new Vector3(0, 1.14f, 0.98f), new Vector3(0.44f, 0.56f, 0.52f), sim ? White : coat, new Basis(Vector3.Right, -0.45f));
            Lump(s, new Vector3(0, 0.84f, 0.98f), new Vector3(0.16f, 0.30f, 0.34f), sim ? White : coat);
            // udder
            Lump(s, new Vector3(0, 0.64f, -0.42f), new Vector3(0.36f, 0.24f, 0.36f), Udder);
            // the cowbell on its broad leather strap
            if (bell) Lump(s, new Vector3(0, 1.02f, 1.12f), new Vector3(0.48f, 0.10f, 0.12f), Strap, new Basis(Vector3.Right, -0.45f));
        }
        // the legs, tapering to dark hooves, the hind ones angled back a little
        foreach (var (x, z, back) in new[] { (-0.24f, -0.66f, 0f), (0.24f, -0.66f, 0f), (-0.24f, 0.70f, 0.06f), (0.24f, 0.70f, 0.06f) })
        {
            s.Tube(new Vector3(x, 0.84f, -z), new Vector3(x, 0.10f, -z - back), 0.11f, 0.065f, leg, 6);
            s.Box(new Vector3(x, 0.05f, -z - back - 0.02f), new Vector3(0.15f, 0.10f, 0.18f), Hoof);
        }
        foreach (var (x, z) in new[] { (-0.09f, 0.34f), (0.09f, 0.34f), (-0.09f, 0.50f), (0.09f, 0.50f) })
            s.Tube(new Vector3(x, 0.55f, -z), new Vector3(x, 0.45f, -z), 0.028f, 0.022f, Udder, 4);
        // the tail from the tail head down to its dark switch
        s.Tube(new Vector3(0, 1.32f, -0.96f), new Vector3(0, 0.62f, -1.02f), 0.045f, 0.03f, sim ? White : coat, 5);
        s.Box(new Vector3(0, 0.52f, -1.02f), new Vector3(0.10f, 0.20f, 0.10f), sim ? White : BrownDark);
        // the bell itself: a brass bell under the throat, wider at the mouth
        if (bell) s.Tube(new Vector3(0, 0.86f, 1.20f), new Vector3(0, 0.62f, 1.22f), 0.08f, 0.14f, Brass, 7);
        return s.Build();
    }

    private static ArrayMesh BuildHead(CowBreed breed)
    {
        var s = new MeshScratch();
        bool sim = breed == CowBreed.Simmental;
        Color face = sim ? White : BrownCoat, muzzle = sim ? M(0.92f, 0.78f, 0.72f) : BrownMuzzle;
        Color ear = sim ? Red : BrownDark;
        using (s.Smoothing(true))
        {
            // built at the joint: the poll and broad forehead, the long face sloping down to the muzzle
            Lump(s, new Vector3(0, 0.08f, 0.18f), new Vector3(0.38f, 0.34f, 0.34f), face);
            Lump(s, new Vector3(0, -0.06f, 0.42f), new Vector3(0.30f, 0.30f, 0.36f), face, new Basis(Vector3.Right, -0.35f));
            Lump(s, new Vector3(0, -0.18f, 0.60f), new Vector3(0.30f, 0.22f, 0.20f), muzzle);
            // a Simmental's red round the eyes, its side 1 cm inside the eyes' outer faces
            // (#942: centred at 0.15 it lay 3-7 mm under them, black on red: a z-fight)
            if (sim)
            {
                Lump(s, new Vector3(-0.14f, 0.08f, 0.27f), new Vector3(0.10f, 0.14f, 0.16f), Red);
                Lump(s, new Vector3(0.14f, 0.08f, 0.27f), new Vector3(0.10f, 0.14f, 0.16f), Red);
            }
            // ears out to the side
            Lump(s, new Vector3(-0.29f, 0.10f, 0.14f), new Vector3(0.22f, 0.07f, 0.13f), ear, new Basis(Vector3.Forward, -0.3f));
            Lump(s, new Vector3(0.29f, 0.10f, 0.14f), new Vector3(0.22f, 0.07f, 0.13f), ear, new Basis(Vector3.Forward, 0.3f));
        }
        s.Box(new Vector3(0, -0.19f, 0.70f), new Vector3(0.20f, 0.11f, 0.02f), Nose);
        // short horns curving up and out
        s.Tube(new Vector3(-0.14f, 0.22f, 0.16f), new Vector3(-0.26f, 0.36f, 0.22f), 0.04f, 0.015f, Horn, 5);
        s.Tube(new Vector3(0.14f, 0.22f, 0.16f), new Vector3(0.26f, 0.36f, 0.22f), 0.04f, 0.015f, Horn, 5);
        // eyes
        s.Box(new Vector3(-0.185f, 0.06f, 0.30f), new Vector3(0.03f, 0.06f, 0.06f), Nose);
        s.Box(new Vector3(0.185f, 0.06f, 0.30f), new Vector3(0.03f, 0.06f, 0.06f), Nose);
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
