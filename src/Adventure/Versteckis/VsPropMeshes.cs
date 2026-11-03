using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Farming;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// The Versteckis props' models (#1268, plan section 3), each built once and shared by the decor's MultiMeshes, every
/// disguise and every decoy (a disguise is a mesh reference swap, never a build: <c>general/no-mesh-rebuild-per-frame</c>):
/// a round hay bale, a woodpile, a 40 l milk churn, a garden gnome in his red Zipfelmütze, a park bench, a flower box (the
/// venues' planter), the cow (the herds' Braunvieh, its head its own mesh), and the Milchbänkli, the milk can's bench.
/// Authored facing +Z, feet on y = 0 (<see cref="MeshScratch"/> turns them to −Z); vertex alpha 0 (the prop shader reads
/// alpha as a lamp). Drawn with the rewards' prop material (<c>RewardMeshes.Material</c>, the style kit's Prop role).
/// Every one is in the model viewer, under "Versteckis".
/// </summary>
public static class VsPropMeshes
{
    private static Color C(string hex) { var c = new Color(hex); c.A = 0; return c; }

    private static readonly Color Hay = C("d8b45a"), HayDark = C("b8923c"), Net = C("e8e4d8"), Log = C("8a5a30"), LogEnd = C("d8b07a"),
        Steel = C("b8bcc2"), SteelDark = C("7c8086"), Red = C("c8202a"), Beard = C("f2efe6"), Skin = C("e8b898"), Blue = C("2f5f9e"),
        Green = C("3f7a3a"), Boot = C("3a2a20"), Slat = C("9a6a3a"), Iron = C("3a3c40"), Plank = C("b5834a");

    private static ArrayMesh? _bale, _pile, _can, _gnome, _bench, _milchbank;

    private static ArrayMesh Build(Action<MeshScratch> parts)
    {
        var s = new MeshScratch();
        parts(s);
        return s.Build();
    }

    /// <summary>The 250 kg round bale (1.2 m across, 1.25 m wide), lying on its side in its white net wrap.</summary>
    [Showcase("Versteckis", "Hay bale")]
    public static ArrayMesh RoundBale() => _bale ??= Build(s =>
    {
        // the roll's axis across (x), the round face front and back
        s.Tube(new Vector3(-0.6f, 0.6f, 0), new Vector3(0.6f, 0.6f, 0), 0.6f, 0.6f, Hay, 12);
        // the net's bands, standing proud of the hay (zfight)
        foreach (float x in new[] { -0.4f, 0f, 0.4f })
            s.Tube(new Vector3(x - 0.03f, 0.6f, 0), new Vector3(x + 0.03f, 0.6f, 0), 0.615f, 0.615f, Net, 12);
        // the darker spiral of the faces
        foreach (float x in new[] { -0.615f, 0.615f })
            s.Tube(new Vector3(x - 0.005f, 0.6f, 0), new Vector3(x + 0.005f, 0.6f, 0), 0.35f, 0.35f, HayDark, 10);
    });

    /// <summary>A woodpile of split logs, 1.6 m long, 0.6 m deep, 1.1 m high, against a wall.</summary>
    [Showcase("Versteckis", "Woodpile")]
    public static ArrayMesh Woodpile() => _pile ??= Build(s =>
    {
        for (int row = 0; row < 5; row++)
            for (int k = 0; k < 7; k++)
            {
                float x = -0.69f + k * 0.23f + (row % 2) * 0.06f, y = 0.11f + row * 0.21f;
                s.Tube(new Vector3(x, y, -0.3f), new Vector3(x, y, 0.3f), 0.1f, 0.1f, Log, 6);
                s.Tube(new Vector3(x, y, 0.3f), new Vector3(x, y, 0.312f), 0.085f, 0.085f, LogEnd, 6);
            }
        // the little roof board on top
        s.Box(new Vector3(0, 1.1f, 0), new Vector3(1.7f, 0.04f, 0.7f), Plank);
    });

    /// <summary>A 40 l milk churn: a steel can with its shoulder, neck, lid and two handles, 0.7 m tall.</summary>
    [Showcase("Versteckis", "Milk can")]
    public static ArrayMesh MilkCan() => _can ??= Build(s =>
    {
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 0.46f, 0), 0.19f, 0.19f, Steel, 10);
        s.Tube(new Vector3(0, 0.46f, 0), new Vector3(0, 0.56f, 0), 0.19f, 0.1f, Steel, 10);
        s.Tube(new Vector3(0, 0.56f, 0), new Vector3(0, 0.64f, 0), 0.1f, 0.1f, SteelDark, 10);
        s.Tube(new Vector3(0, 0.64f, 0), new Vector3(0, 0.7f, 0), 0.12f, 0.1f, Steel, 10);
        s.Tube(new Vector3(0, 0.02f, 0), new Vector3(0, 0.05f, 0), 0.202f, 0.202f, SteelDark, 10);
        foreach (float x in new[] { -0.2f, 0.2f })
            s.Box(new Vector3(x, 0.5f, 0), new Vector3(0.03f, 0.12f, 0.08f), SteelDark);
    });

    /// <summary>A garden gnome, 0.6 m: boots, blue coat, a white beard and a tall red Zipfelmütze.</summary>
    [Showcase("Versteckis", "Garden gnome")]
    public static ArrayMesh GardenGnome() => _gnome ??= Build(s =>
    {
        foreach (float x in new[] { -0.07f, 0.07f })
            s.RoundedBox(new Vector3(x, 0.04f, 0.02f), new Vector3(0.09f, 0.08f, 0.14f), Boot);
        s.Tube(new Vector3(0, 0.06f, 0), new Vector3(0, 0.3f, 0), 0.15f, 0.12f, Blue, 8);
        // the belt round the coat's waist, standing proud of it
        s.Tube(new Vector3(0, 0.19f, 0), new Vector3(0, 0.23f, 0), 0.152f, 0.146f, C("4a3020"), 8);
        s.RoundedBox(new Vector3(0, 0.36f, 0), new Vector3(0.18f, 0.14f, 0.16f), Skin);
        s.Tube(new Vector3(0, 0.34f, 0.05f), new Vector3(0, 0.22f, 0.1f), 0.08f, 0.02f, Beard, 6);
        s.RoundedBox(new Vector3(0, 0.40f, 0.1f), new Vector3(0.04f, 0.04f, 0.04f), C("d88a78"));
        s.Tube(new Vector3(0, 0.42f, 0), new Vector3(0, 0.6f, -0.04f), 0.11f, 0.01f, Red, 8);
    });

    /// <summary>A park bench, 1.6 m: three seat slats, two back slats, cast-iron ends.</summary>
    [Showcase("Versteckis", "Bench")]
    public static ArrayMesh ParkBench() => _bench ??= Build(s =>
    {
        foreach (float x in new[] { -0.7f, 0.7f })
        {
            s.Box(new Vector3(x, 0.22f, 0.05f), new Vector3(0.06f, 0.44f, 0.5f), Iron);
            // the back's posts behind the slats and a hand higher (zfight: no face within 8 mm of a slat's)
            s.Box(new Vector3(x, 0.64f, -0.28f), new Vector3(0.06f, 0.48f, 0.06f), Iron);
        }
        for (int k = 0; k < 3; k++)
            s.Box(new Vector3(0, 0.46f, -0.1f + k * 0.14f), new Vector3(1.6f, 0.04f, 0.11f), Slat);
        for (int k = 0; k < 2; k++)
            s.Box(new Vector3(0, 0.6f + k * 0.16f, -0.23f), new Vector3(1.6f, 0.11f, 0.04f), Slat);
    });

    /// <summary>The Milchbänkli: a farm's milk bench at the road, a high plank table where the cans wait for the dairy.</summary>
    [Showcase("Versteckis", "Milchbänkli")]
    public static ArrayMesh Milchbank() => _milchbank ??= Build(s =>
    {
        foreach (var (x, z) in new[] { (-0.7f, -0.25f), (0.7f, -0.25f), (-0.7f, 0.25f), (0.7f, 0.25f) })
            s.Box(new Vector3(x, 0.4f, z), new Vector3(0.1f, 0.8f, 0.1f), Log);
        s.Box(new Vector3(0, 0.82f, 0), new Vector3(1.7f, 0.06f, 0.7f), Plank);
        s.Box(new Vector3(0, 0.3f, 0), new Vector3(1.5f, 0.04f, 0.04f), Log);
    });

    private static ArrayMesh? _post;

    /// <summary>A boundary post on the game's line: 1.2 m, red and white bands.</summary>
    [Showcase("Versteckis", "Boundary post")]
    public static ArrayMesh Post() => _post ??= Build(s =>
    {
        for (int k = 0; k < 4; k++)
            s.Box(new Vector3(0, 0.15f + k * 0.3f, 0), new Vector3(0.1f, 0.3f, 0.1f), k % 2 == 0 ? Red : Beard);
    });

    /// <summary>The flower box: the venues' terrace planter, under a front window.</summary>
    [Showcase("Versteckis", "Flower box")]
    public static ArrayMesh FlowerBox() => Interiors.TerraceProps.Planter.Mesh;

    /// <summary>The cow's body (the herds' Braunvieh, <see cref="CowMeshBuilder"/>); its head is <see cref="CowHead"/>.</summary>
    public static ArrayMesh CowBody() => CowMeshBuilder.Body(CowBreed.Brown);

    public static ArrayMesh CowHead() => CowMeshBuilder.Head(CowBreed.Brown);

    /// <summary>A disguised cow in the viewer: the body and the head on its neck joint, as <c>VsDisguise</c> draws it.</summary>
    [Showcase("Versteckis", "Cow")]
    public static Node3D Cow()
    {
        var root = new Node3D { Name = "Cow" };
        root.AddChild(new MeshInstance3D { Mesh = CowBody(), MaterialOverride = Rewards.RewardMeshes.Material() });
        root.AddChild(new MeshInstance3D { Mesh = CowHead(), Position = CowMeshBuilder.Neck, MaterialOverride = Rewards.RewardMeshes.Material() });
        return root;
    }

    /// <summary>A prop's mesh (the cow's body), shared; null for none.</summary>
    public static ArrayMesh? Of(VsProp kind) => kind switch
    {
        VsProp.HayBale => RoundBale(),
        VsProp.Woodpile => Woodpile(),
        VsProp.MilkCan => MilkCan(),
        VsProp.Gnome => GardenGnome(),
        VsProp.Bench => ParkBench(),
        VsProp.Cow => CowBody(),
        VsProp.FlowerBox => FlowerBox(),
        _ => null,
    };
}
