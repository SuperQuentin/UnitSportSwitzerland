using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Trailer;

/// <summary>
/// A dormitory of the Drognens barracks (#717), a film set the director builds for the clip, after
/// the army's own photo of a room ("61-405 Chambre"):
/// - two mirrored rows of steel single beds with brown-checked covers;
/// - a tall birch wardrobe between beds, a shelf bridging over each bed's head, a helmet on every
///   wardrobe, the packs hung on the bed ends;
/// - a grey floor, white walls, a pale wooden ceiling on beams with tube lights;
/// - windows at the far end, the teal door at the near one, open into the room from a stretch of corridor;
/// - a folding table and chairs in the aisle, dressed for poker.
/// Set space: metres, the room's floor centre at the origin, −Z toward the windows, +X to the right
/// looking there. The node carries its own lights and the collision the actors stand on.
/// </summary>
public static class DormSet
{
    public const float HalfWidth = 3.8f, HalfLength = 7.5f, Height = 3.1f;
    public const int BedsPerSide = 7;

    private const float WardrobeWide = 0.62f, BedSlot = 1.0f, BedLength = 2.0f, WardrobeDeep = 0.6f;
    private const float RowStart = -6.6f;
    /// <summary>The door in the near wall: its opening's left and right edges (x), looking in.</summary>
    private const float DoorLeft = -3.2f, DoorRight = -2.2f, DoorHeight = 2.1f;
    private const float CorridorEnd = 9.9f;

    private static readonly Color Floor = new(0.52f, 0.54f, 0.57f), Wall = new(0.9f, 0.9f, 0.87f);
    private static readonly Color Birch = new(0.86f, 0.68f, 0.43f), BirchDark = new(0.74f, 0.56f, 0.33f);
    private static readonly Color Ceiling = new(0.87f, 0.76f, 0.56f), Beam = new(0.8f, 0.66f, 0.44f);
    private static readonly Color Steel = new(0.6f, 0.62f, 0.65f), Olive = new(0.31f, 0.34f, 0.22f), OliveDark = new(0.24f, 0.27f, 0.17f);
    private static readonly Color Beige = new(0.84f, 0.71f, 0.47f), Cream = new(0.97f, 0.94f, 0.86f), Pillow = new(0.95f, 0.93f, 0.88f);
    private static readonly Color Teal = new(0.27f, 0.66f, 0.62f), Chrome = new(0.74f, 0.75f, 0.77f), Slate = new(0.18f, 0.19f, 0.2f);

    /// <summary>Where bed <paramref name="i"/> (0 at the windows) of a row lies along the room, z.</summary>
    public static float BedZ(int i) => RowStart + WardrobeWide + BedSlot * 0.5f + i * (WardrobeWide + BedSlot);

    /// <summary>The top of bed <paramref name="i"/>'s mattress, its middle; <paramref name="side"/> −1 left, +1 right.</summary>
    public static Vector3 Bed(int side, int i) => new(side * (HalfWidth - BedLength * 0.5f), 0.56f, BedZ(i));

    /// <summary>The poker table's top centre, in the aisle.</summary>
    public static readonly Vector3 Table = new(0f, 0.74f, -1.2f);

    /// <summary>The four chairs round the table: the hip of someone sitting there, and the way they face (compass, −Z = 0).</summary>
    public static readonly (Vector3 Hip, float Bearing)[] Chairs =
    {
        (new Vector3(-0.72f, 0.46f, -1.55f), 90f), (new Vector3(-0.72f, 0.46f, -0.85f), 90f),
        (new Vector3(0.72f, 0.46f, -1.55f), 270f), (new Vector3(0.72f, 0.46f, -0.85f), 270f),
    };

    /// <summary>The doorway, on the floor, and a spot in the corridor looking in through it.</summary>
    public static readonly Vector3 Doorway = new((DoorLeft + DoorRight) * 0.5f, 0f, HalfLength);
    public static readonly Vector3 Corridor = new(-1.0f, 0f, 8.9f);

    /// <summary>Cut away (no ceilings, no left wall), so the viewer looks in from outside.</summary>
    [Showcase("Trailer", "Drognens dormitory")]
    private static Node3D Showcase() => Build(cutaway: true);

    /// <summary>The room, its corridor stub, lights and collision; <paramref name="cutaway"/> leaves out the ceilings and the left wall.</summary>
    public static Node3D Build(bool cutaway = false)
    {
        var root = new Node3D { Name = "DormSet" };
        var s = new MeshScratch();
        Shell(s, cutaway);
        for (int side = -1; side <= 1; side += 2)
        {
            for (int i = 0; i <= BedsPerSide; i++) Wardrobe(s, side, RowStart + WardrobeWide * 0.5f + i * (WardrobeWide + BedSlot));
            for (int i = 0; i < BedsPerSide; i++) BedAt(s, side, i);
        }
        Poker(s);
        // MeshScratch bakes a half turn (vehicles are authored facing +Z); the set is authored in
        // its node's own frame, as its lights, collision and the director's points are, so turned back
        root.AddChild(new MeshInstance3D { Name = "Room", Mesh = s.Build(), MaterialOverride = HumanMeshBuilder.Material(), Transform = Unturn });

        var glow = new MeshScratch();
        for (int row = -1; row <= 1; row += 2)
            foreach (float z in TubeRows) glow.Box(new Vector3(row * 1.4f, Height - 0.3f, z), new Vector3(0.12f, 0.05f, 1.4f), new Color(1f, 1f, 0.96f));
        glow.Box(new Vector3(0f, Height - 0.3f, (HalfLength + CorridorEnd) * 0.5f), new Vector3(1.2f, 0.05f, 0.12f), new Color(1f, 1f, 0.96f));
        root.AddChild(new MeshInstance3D { Name = "Tubes", Mesh = glow.Build(), MaterialOverride = Unlit(new Color(1f, 1f, 0.95f)), Transform = Unturn });

        var night = new MeshScratch();
        for (int w = -1; w <= 1; w += 2) night.Box(new Vector3(w * 1.6f, 1.75f, -HalfLength + 0.06f), new Vector3(1.5f, 1.6f, 0.02f), new Color(1, 1, 1));
        root.AddChild(new MeshInstance3D { Name = "Night", Mesh = night.Build(), MaterialOverride = Unlit(new Color(0.06f, 0.09f, 0.2f)), Transform = Unturn });

        root.AddChild(DoorSign());
        foreach (float z in TubeRows)
            for (int row = -1; row <= 1; row += 2)
                root.AddChild(new OmniLight3D { Position = new Vector3(row * 1.4f, Height - 0.45f, z), OmniRange = 5.5f, LightEnergy = 0.9f, LightColor = new Color(1f, 0.97f, 0.9f) });
        root.AddChild(new OmniLight3D { Position = new Vector3(0f, Height - 0.45f, (HalfLength + CorridorEnd) * 0.5f), OmniRange = 4.5f, LightEnergy = 0.7f, LightColor = new Color(0.92f, 0.97f, 1f) });
        root.AddChild(Collision());
        return root;
    }

    private static readonly float[] TubeRows = { -5.2f, -1.6f, 2.0f, 5.6f };

    /// <summary>Undoes the half turn <see cref="MeshScratch.Build()"/> bakes in.</summary>
    private static readonly Transform3D Unturn = new(new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero);

    private static StandardMaterial3D Unlit(Color colour) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = colour,
        VertexColorUseAsAlbedo = false,
    };

    /// <summary>Floor, walls with the windows and the doorway, the ceiling on its beams, the corridor stub.</summary>
    private static void Shell(MeshScratch s, bool cutaway)
    {
        float w = HalfWidth, l = HalfLength, h = Height;
        s.Box(new Vector3(0, -0.05f, (CorridorEnd - l) * 0.5f), new Vector3(2 * w + 0.2f, 0.1f, l + CorridorEnd + 0.2f), Floor);
        if (!cutaway) s.Box(new Vector3(-w - 0.05f, h * 0.5f, 0), new Vector3(0.1f, h, 2 * l), Wall);
        s.Box(new Vector3(w + 0.05f, h * 0.5f, 0), new Vector3(0.1f, h, 2 * l), Wall);
        // the far wall, and the window frames on it
        s.Box(new Vector3(0, h * 0.5f, -l - 0.05f), new Vector3(2 * w, h, 0.1f), Wall);
        for (int x = -1; x <= 1; x += 2)
        {
            s.Box(new Vector3(x * 1.6f, 0.92f, -l + 0.08f), new Vector3(1.66f, 0.08f, 0.14f), BirchDark);
            s.Box(new Vector3(x * 1.6f, 2.58f, -l + 0.08f), new Vector3(1.66f, 0.08f, 0.1f), BirchDark);
            for (int e = -1; e <= 1; e++) s.Box(new Vector3(x * 1.6f + e * 0.79f, 1.75f, -l + 0.08f), new Vector3(0.08f, 1.66f, 0.1f), BirchDark);
        }
        // the near wall round the doorway
        s.Box(new Vector3((-w + DoorLeft) * 0.5f, h * 0.5f, l + 0.05f), new Vector3(DoorLeft + w, h, 0.1f), Wall);
        s.Box(new Vector3((DoorRight + w) * 0.5f, h * 0.5f, l + 0.05f), new Vector3(w - DoorRight, h, 0.1f), Wall);
        s.Box(new Vector3((DoorLeft + DoorRight) * 0.5f, (DoorHeight + h) * 0.5f, l + 0.05f), new Vector3(DoorRight - DoorLeft, h - DoorHeight, 0.1f), Wall);
        // the room's ceiling, its beams across, and the corridor's
        if (!cutaway)
        {
            s.Box(new Vector3(0, h + 0.05f, 0), new Vector3(2 * w, 0.1f, 2 * l), Ceiling);
            for (float z = -l + 1.2f; z < l; z += 2.4f) s.Box(new Vector3(0, h - 0.12f, z), new Vector3(2 * w, 0.24f, 0.2f), Beam);
            s.Box(new Vector3(0, h + 0.05f, (l + CorridorEnd) * 0.5f), new Vector3(2 * w, 0.1f, CorridorEnd - l), Wall);
        }
        s.Box(new Vector3(0, h * 0.5f, CorridorEnd + 0.05f), new Vector3(2 * w, h, 0.1f), Wall);
        s.Box(new Vector3(-w - 0.05f, h * 0.5f, (l + CorridorEnd) * 0.5f), new Vector3(0.1f, h, CorridorEnd - l), Wall);
        s.Box(new Vector3(w + 0.05f, h * 0.5f, (l + CorridorEnd) * 0.5f), new Vector3(0.1f, h, CorridorEnd - l), Wall);
        // the teal door, swung into the room on its left hinge, its number to the corridor
        var open = new Basis(Vector3.Up, Mathf.DegToRad(100f));
        var hinge = new Vector3(DoorLeft, 0f, l + 0.05f);
        float leaf = DoorRight - DoorLeft - 0.04f;
        s.Box(hinge + open * new Vector3(leaf * 0.5f, DoorHeight * 0.5f, 0.03f), new Vector3(leaf, DoorHeight - 0.02f, 0.05f), Teal, open);
        s.Box(hinge + open * new Vector3(leaf - 0.12f, 1.02f, 0.09f), new Vector3(0.16f, 0.025f, 0.03f), Chrome, open);
        s.Box(hinge + open * new Vector3(leaf * 0.47f, 1.45f, 0.06f), new Vector3(0.5f, 0.62f, 0.012f), Chrome, open);
        s.Box(hinge + open * new Vector3(leaf * 0.47f, 1.45f, 0.068f), new Vector3(0.42f, 0.54f, 0.01f), Slate, open);
    }

    /// <summary>The room number on the door's outer face, as the army writes it.</summary>
    private static Node3D DoorSign()
    {
        var open = new Basis(Vector3.Up, Mathf.DegToRad(100f));
        var hinge = new Vector3(DoorLeft, 0f, HalfLength + 0.05f);
        float leaf = DoorRight - DoorLeft - 0.04f;
        return new Label3D
        {
            Name = "RoomNumber",
            Text = "61-405\nChambre",
            FontSize = 64,
            PixelSize = 0.0028f,
            OutlineSize = 0,
            Modulate = new Color(0.97f, 0.97f, 0.97f),
            Shaded = false,
            DoubleSided = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            Transform = new Transform3D(open, hinge + open * new Vector3(0.06f, 1.95f, 0.065f)),
        };
    }

    private static void Wardrobe(MeshScratch s, int side, float z)
    {
        float x = side * (HalfWidth - WardrobeDeep * 0.5f);
        s.Box(new Vector3(x, 1.06f, z), new Vector3(WardrobeDeep, 2.12f, WardrobeWide - 0.02f), Birch);
        // its door's edge and handle on the aisle face, the helmet on top
        float face = side * (HalfWidth - WardrobeDeep) - side * 0.005f;
        s.Box(new Vector3(face, 1.06f, z + 0.12f), new Vector3(0.01f, 2.0f, 0.01f), BirchDark);
        s.Box(new Vector3(face - side * 0.02f, 1.05f, z + 0.2f), new Vector3(0.03f, 0.1f, 0.025f), Chrome);
        s.Tube(new Vector3(x, 2.12f, z), new Vector3(x, 2.27f, z), 0.17f, 0.07f, Olive, 8);
        s.Ring(new Vector3(x, 2.13f, z), Vector3.Up, 0.15f, 0.19f, 0.02f, OliveDark);
    }

    private static void BedAt(MeshScratch s, int side, int i)
    {
        float z = BedZ(i), head = side * HalfWidth, foot = side * (HalfWidth - BedLength), mid = (head + foot) * 0.5f;
        float half = 0.45f;
        // the steel frame: legs, rails, the foot's bar the packs hang on
        foreach (float x in new[] { head - side * 0.04f, foot + side * 0.04f })
            foreach (float dz in new[] { -half, half })
                s.Tube(new Vector3(x, 0f, z + dz), new Vector3(x, x == foot + side * 0.04f ? 0.78f : 0.62f, z + dz), 0.022f, Steel, 5);
        foreach (float dz in new[] { -half, half })
            s.Tube(new Vector3(head, 0.36f, z + dz), new Vector3(foot, 0.36f, z + dz), 0.02f, Steel, 5);
        s.Tube(new Vector3(foot + side * 0.04f, 0.78f, z - half), new Vector3(foot + side * 0.04f, 0.78f, z + half), 0.02f, Steel, 5);
        s.Tube(new Vector3(foot + side * 0.04f, 0.55f, z - half), new Vector3(foot + side * 0.04f, 0.55f, z + half), 0.016f, Steel, 5);
        // mattress, pillow and the brown-checked cover
        s.Box(new Vector3(mid, 0.44f, z), new Vector3(BedLength - 0.1f, 0.16f, 0.88f), Cream);
        s.Box(new Vector3(head - side * 0.28f, 0.56f, z), new Vector3(0.38f, 0.09f, 0.66f), Pillow);
        const int along = 8, across = 4;
        float coverFrom = head - side * 0.55f, coverLen = BedLength - 0.62f;
        for (int a = 0; a < along; a++)
            for (int c = 0; c < across; c++)
            {
                var tint = (a + c) % 2 == 0 ? Beige : Cream;
                float x = coverFrom - side * (a + 0.5f) * coverLen / along;
                s.Box(new Vector3(x, 0.535f, z - 0.44f + (c + 0.5f) * 0.88f / across), new Vector3(coverLen / along, 0.03f, 0.88f / across), tint);
            }
        // the shelf over the bed's head, between the two wardrobes
        s.Box(new Vector3(side * (HalfWidth - 0.24f), 1.84f, z), new Vector3(0.48f, 0.34f, BedSlot), Birch);
        s.Box(new Vector3(side * (HalfWidth - 0.48f), 1.84f, z), new Vector3(0.01f, 0.3f, BedSlot - 0.06f), BirchDark);
        // the pack and its pouch on the foot's bar
        float pack = foot - side * 0.12f;
        s.RoundedBox(new Vector3(pack, 0.55f, z - 0.15f), new Vector3(0.2f, 0.5f, 0.38f), Olive);
        s.RoundedBox(new Vector3(pack - side * 0.06f, 0.42f, z + 0.22f), new Vector3(0.16f, 0.26f, 0.22f), OliveDark);
    }

    /// <summary>The folding table, four chairs, and the game on it: chips, a deck, bottles.</summary>
    private static void Poker(MeshScratch s)
    {
        var t = Table;
        s.Box(t + new Vector3(0, -0.02f, 0), new Vector3(0.8f, 0.04f, 1.4f), new Color(0.62f, 0.46f, 0.3f));
        foreach (float x in new[] { -0.35f, 0.35f })
            foreach (float z in new[] { -0.62f, 0.62f })
                s.Tube(new Vector3(t.X + x, 0f, t.Z + z), new Vector3(t.X + x, t.Y - 0.04f, t.Z + z), 0.02f, Steel, 5);
        foreach (var (hip, bearing) in Chairs)
        {
            var turn = new Basis(Vector3.Up, -Mathf.DegToRad(bearing));
            var seat = hip with { Y = 0.44f };
            s.Box(seat, new Vector3(0.42f, 0.04f, 0.42f), new Color(0.2f, 0.32f, 0.22f), turn);
            s.Box(seat + turn * new Vector3(0, 0.3f, 0.2f), new Vector3(0.4f, 0.28f, 0.03f), new Color(0.2f, 0.32f, 0.22f), turn);
            foreach (var leg in new[] { new Vector3(-0.18f, 0, -0.18f), new Vector3(0.18f, 0, -0.18f), new Vector3(-0.18f, 0, 0.18f), new Vector3(0.18f, 0, 0.18f) })
                s.Tube(turn * leg + new Vector3(seat.X, 0f, seat.Z), turn * leg + new Vector3(seat.X, 0.42f, seat.Z), 0.015f, Steel, 4);
        }
        // the pot of chips, a deck, the cards down in front of each player, the bottles
        var chips = new[] { new Color(0.85f, 0.1f, 0.1f), new Color(0.1f, 0.25f, 0.8f), new Color(0.95f, 0.95f, 0.95f), new Color(0.1f, 0.55f, 0.2f) };
        for (int k = 0; k < 7; k++)
        {
            var at = t + new Vector3(Mathf.Cos(k * 2.1f) * 0.12f, 0f, Mathf.Sin(k * 2.1f) * 0.16f);
            for (int n = 0; n < 2 + k % 4; n++)
                s.Tube(at + new Vector3(0, n * 0.012f, 0), at + new Vector3(0, n * 0.012f + 0.01f, 0), 0.02f, chips[(k + n) % 4], 8);
        }
        s.Box(t + new Vector3(0.05f, 0.012f, 0.32f), new Vector3(0.065f, 0.025f, 0.09f), new Color(0.75f, 0.12f, 0.12f));
        foreach (var (hip, bearing) in Chairs)
        {
            var toward = (t - hip) with { Y = 0 };
            var spot = t + new Vector3(-toward.X * 0.55f, 0.002f, -toward.Z * 0.5f);
            s.Box(spot, new Vector3(0.065f, 0.004f, 0.09f), Cream, new Basis(Vector3.Up, bearing));
            var bottle = spot + new Vector3(0f, 0f, 0.14f);
            s.Tube(bottle, bottle + new Vector3(0, 0.15f, 0), 0.03f, new Color(0.36f, 0.19f, 0.06f), 7);
            s.Tube(bottle + new Vector3(0, 0.15f, 0), bottle + new Vector3(0, 0.22f, 0), 0.03f, 0.012f, new Color(0.36f, 0.19f, 0.06f), 7);
            s.Tube(bottle + new Vector3(0, 0.06f, 0), bottle + new Vector3(0, 0.12f, 0), 0.032f, new Color(0.93f, 0.85f, 0.4f), 7);
        }
    }

    /// <summary>What the actors stand on and cannot walk through: the floors and the walls.</summary>
    private static StaticBody3D Collision()
    {
        var body = new StaticBody3D { Name = "SetCollision" };
        void Add(Vector3 centre, Vector3 size) =>
            body.AddChild(new CollisionShape3D { Position = centre, Shape = new BoxShape3D { Size = size } });
        float w = HalfWidth, l = HalfLength, h = Height;
        Add(new Vector3(0, -0.25f, (CorridorEnd - l) * 0.5f), new Vector3(2 * w + 0.2f, 0.5f, l + CorridorEnd + 0.2f));
        Add(new Vector3(-w - 0.1f, h * 0.5f, (CorridorEnd - l) * 0.5f), new Vector3(0.2f, h, l + CorridorEnd));
        Add(new Vector3(w + 0.1f, h * 0.5f, (CorridorEnd - l) * 0.5f), new Vector3(0.2f, h, l + CorridorEnd));
        Add(new Vector3(0, h * 0.5f, -l - 0.1f), new Vector3(2 * w, h, 0.2f));
        Add(new Vector3(0, h * 0.5f, CorridorEnd + 0.1f), new Vector3(2 * w, h, 0.2f));
        Add(new Vector3(Table.X, Table.Y * 0.5f, Table.Z), new Vector3(0.8f, Table.Y, 1.4f));
        return body;
    }
}
