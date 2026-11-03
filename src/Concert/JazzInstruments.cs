using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Styles;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Concert;

/// <summary>
/// The jazz quartet's four instruments (#1394): an upright bass, a saxophone, a piano with its stool and a drum kit. Each is one mesh built once
/// from boxes and tubes (<see cref="MeshScratch"/>), authored with its player at the origin facing -Z, feet on the floor, and put on the stage as a
/// node beside a performer's figure: nothing is rebuilt per frame or per beat (the band's sway turns the figure's node, the bass and the sax follow
/// it as its children). Shown in the model viewer under "Concert", with the whole quartet on its stage.
/// </summary>
public static class JazzInstruments
{
    private static readonly Color Spruce = Matte(0.78f, 0.55f, 0.28f), Maple = Matte(0.55f, 0.32f, 0.16f), Ebony = Matte(0.08f, 0.07f, 0.07f);
    private static readonly Color Brass = Matte(0.88f, 0.70f, 0.24f), BrassDark = Matte(0.62f, 0.46f, 0.14f), Pearl = Matte(0.96f, 0.95f, 0.90f);
    private static readonly Color Black = Matte(0.10f, 0.10f, 0.11f), Ivory = Matte(0.94f, 0.93f, 0.88f), Chrome = Matte(0.74f, 0.76f, 0.78f);
    private static readonly Color Red = Matte(0.62f, 0.10f, 0.14f), Skin = Matte(0.90f, 0.86f, 0.78f), Steel = Matte(0.55f, 0.56f, 0.58f);

    /// <summary>Which instrument.</summary>
    public enum Kind { Bass, Sax, Piano, Kit }

    private static readonly ArrayMesh?[] Cache = new ArrayMesh?[4];

    /// <summary>The instrument's mesh, built once.</summary>
    public static ArrayMesh Mesh(Kind kind) => Cache[(int)kind] ??= kind switch
    {
        Kind.Bass => BuildBass(),
        Kind.Sax => BuildSax(),
        Kind.Piano => BuildPiano(),
        _ => BuildKit(),
    };

    /// <summary>The instrument as a node with its mesh, in the style's prop material, for a stage or the viewer.</summary>
    public static MeshInstance3D Node(Kind kind) => new()
    {
        Name = kind.ToString(), Mesh = Mesh(kind), MaterialOverride = StyleKit.Material(MaterialRole.Prop),
    };

    [Showcase("Concert", "Upright bass")]
    private static ArrayMesh ShowBass() => Mesh(Kind.Bass);

    [Showcase("Concert", "Saxophone")]
    private static ArrayMesh ShowSax() => Mesh(Kind.Sax);

    [Showcase("Concert", "Piano and stool")]
    private static ArrayMesh ShowPiano() => Mesh(Kind.Piano);

    [Showcase("Concert", "Drum kit")]
    private static ArrayMesh ShowKit() => Mesh(Kind.Kit);

    // ---- the upright bass: in front of its player, a little to his right -------------------------------------------

    private static ArrayMesh BuildBass()
    {
        var s = new MeshScratch();
        // the body: a lower and an upper bout round a waist, standing on its endpin; its face looks -Z, the player's side
        s.Tube(new Vector3(0.2f, 0.28f, -0.35f), new Vector3(0.2f, 0.52f, -0.35f), 0.30f, 0.26f, Spruce, 12);
        s.Tube(new Vector3(0.2f, 0.52f, -0.35f), new Vector3(0.2f, 0.62f, -0.35f), 0.26f, 0.19f, Spruce, 12);
        s.Tube(new Vector3(0.2f, 0.62f, -0.35f), new Vector3(0.2f, 0.88f, -0.35f), 0.19f, 0.23f, Spruce, 12);
        s.Tube(new Vector3(0.2f, 0.88f, -0.35f), new Vector3(0.2f, 1.02f, -0.35f), 0.23f, 0.16f, Spruce, 12);
        // the neck, the fingerboard down its face, the scroll with its pegs
        s.Box(new Vector3(0.2f, 1.42f, -0.35f), new Vector3(0.06f, 0.84f, 0.05f), Maple);
        s.Box(new Vector3(0.2f, 1.30f, -0.385f), new Vector3(0.05f, 1.08f, 0.02f), Ebony);
        s.Tube(new Vector3(0.2f, 1.84f, -0.35f), new Vector3(0.2f, 1.96f, -0.35f), 0.04f, 0.045f, Maple, 8);
        foreach (float side in new[] { -1f, 1f })
            s.Box(new Vector3(0.2f + side * 0.045f, 1.86f, -0.35f), new Vector3(0.03f, 0.07f, 0.03f), Ebony);
        // the bridge, the tailpiece, four strings and the endpin
        s.Box(new Vector3(0.2f, 0.60f, -0.385f), new Vector3(0.14f, 0.07f, 0.02f), Maple);
        s.Box(new Vector3(0.2f, 0.34f, -0.385f), new Vector3(0.08f, 0.26f, 0.015f), Ebony);
        foreach (float x in new[] { -0.018f, -0.006f, 0.006f, 0.018f })
            s.Tube(new Vector3(0.2f + x, 0.60f, -0.39f), new Vector3(0.2f + x, 1.86f, -0.375f), 0.003f, 0.003f, Chrome, 3);
        s.Tube(new Vector3(0.2f, 0.0f, -0.35f), new Vector3(0.2f, 0.12f, -0.35f), 0.006f, 0.012f, Steel, 5);
        // the f-holes
        foreach (float side in new[] { -1f, 1f })
            s.Box(new Vector3(0.2f + side * 0.09f, 0.58f, -0.384f), new Vector3(0.02f, 0.13f, 0.01f), Ebony, new Basis(Vector3.Back, side * 0.12f));
        return s.Build();
    }

    // ---- the saxophone: hung at the player's front, the bell low and forward ---------------------------------------

    private static ArrayMesh BuildSax()
    {
        var s = new MeshScratch();
        // the mouthpiece and neck at the mouth, the body falling down the front, the bow, the bell turned up and out
        s.Tube(new Vector3(0.02f, 1.48f, -0.14f), new Vector3(0.03f, 1.50f, -0.26f), 0.012f, 0.014f, Black, 6);
        s.Tube(new Vector3(0.03f, 1.50f, -0.26f), new Vector3(0.05f, 1.38f, -0.34f), 0.014f, 0.018f, Brass, 8);
        s.Tube(new Vector3(0.05f, 1.38f, -0.34f), new Vector3(0.10f, 0.74f, -0.40f), 0.018f, 0.034f, Brass, 10);
        s.Tube(new Vector3(0.10f, 0.74f, -0.40f), new Vector3(0.12f, 0.60f, -0.42f), 0.034f, 0.040f, BrassDark, 10);
        s.Tube(new Vector3(0.12f, 0.60f, -0.42f), new Vector3(0.12f, 0.54f, -0.50f), 0.040f, 0.048f, Brass, 10);
        s.Tube(new Vector3(0.12f, 0.54f, -0.50f), new Vector3(0.12f, 0.64f, -0.62f), 0.048f, 0.075f, Brass, 12);
        s.Tube(new Vector3(0.12f, 0.64f, -0.62f), new Vector3(0.12f, 0.80f, -0.68f), 0.075f, 0.11f, Brass, 12);
        // the keys: little pearl buttons down the body
        for (int i = 0; i < 6; i++)
            s.Box(new Vector3(0.115f - 0.01f * i, 1.28f - 0.085f * i, -0.40f - 0.012f * i), new Vector3(0.03f, 0.02f, 0.02f), Pearl);
        // the strap round the player's neck
        s.Tube(new Vector3(0.05f, 1.38f, -0.34f), new Vector3(0.0f, 1.55f, -0.04f), 0.005f, 0.005f, Black, 3);
        return s.Build();
    }

    // ---- the piano and its stool: a grand seen side-on, the keys towards the player ----------------------------------

    private static ArrayMesh BuildPiano()
    {
        var s = new MeshScratch();
        // the case, one long box for the body with a curved tail made of two steps, on three turned legs
        s.Box(new Vector3(0f, 0.74f, -0.95f), new Vector3(1.50f, 0.28f, 1.55f), Black);
        s.Box(new Vector3(0f, 0.74f, -2.05f), new Vector3(1.10f, 0.28f, 0.70f), Black);
        s.Box(new Vector3(0f, 0.74f, -2.50f), new Vector3(0.70f, 0.28f, 0.28f), Black);
        foreach (var leg in new[] { new Vector2(-0.62f, -0.3f), new Vector2(0.62f, -0.3f), new Vector2(0f, -2.4f) })
            s.Tube(new Vector3(leg.X, 0f, leg.Y), new Vector3(leg.X, 0.62f, leg.Y), 0.05f, 0.07f, Black, 7);
        // the lid, propped open on its stick, the music desk, the keyboard and its fall board
        s.Box(new Vector3(0f, 0.95f, -1.25f), new Vector3(1.46f, 0.03f, 2.30f), Black, new Basis(Vector3.Back, 0.0f));
        s.Box(new Vector3(0f, 1.18f, -2.10f), new Vector3(1.30f, 0.62f, 0.03f), Black, new Basis(Vector3.Right, -0.55f));
        s.Tube(new Vector3(0.55f, 0.88f, -0.8f), new Vector3(0.55f, 1.15f, -1.55f), 0.012f, 0.012f, Chrome, 4);
        s.Box(new Vector3(0f, 0.90f, -0.20f), new Vector3(1.22f, 0.04f, 0.26f), Ivory);
        for (int k = 0; k < 11; k++)
            s.Box(new Vector3(-0.50f + k * 0.1f + (k % 7 == 2 || k % 7 == 6 ? 0.1f : 0f), 0.94f, -0.26f), new Vector3(0.06f, 0.04f, 0.14f), Black);
        s.Box(new Vector3(0f, 1.02f, -0.12f), new Vector3(1.24f, 0.2f, 0.04f), Black);
        // the stool, pushed back a step to the player's side
        s.Tube(new Vector3(0.95f, 0.46f, 0.55f), new Vector3(0.95f, 0.52f, 0.55f), 0.22f, 0.22f, Red, 10);
        foreach (var l in new[] { new Vector2(-0.14f, -0.14f), new Vector2(0.14f, -0.14f), new Vector2(-0.14f, 0.14f), new Vector2(0.14f, 0.14f) })
            s.Tube(new Vector3(0.95f + l.X, 0f, 0.55f + l.Y), new Vector3(0.95f + l.X * 0.9f, 0.46f, 0.55f + l.Y * 0.9f), 0.02f, 0.025f, Maple, 5);
        return s.Build();
    }

    // ---- the drum kit: kick, snare, two toms, hi-hat and two cymbals round the drummer ------------------------------

    private static ArrayMesh BuildKit()
    {
        var s = new MeshScratch();
        // the kick drum in front, its pedal and the red front head
        s.Tube(new Vector3(0f, 0.30f, -0.95f), new Vector3(0f, 0.30f, -1.35f), 0.30f, 0.30f, Red, 14);
        s.Tube(new Vector3(0f, 0.30f, -1.35f), new Vector3(0f, 0.30f, -1.36f), 0.28f, 0.28f, Pearl, 14);
        foreach (float x in new[] { -0.2f, 0.2f })
            s.Tube(new Vector3(x, 0.02f, -1.30f), new Vector3(x, 0.30f, -1.38f), 0.012f, 0.012f, Chrome, 4);
        s.Box(new Vector3(0.1f, 0.05f, -0.55f), new Vector3(0.08f, 0.03f, 0.30f), Steel);
        // the snare between the knees on its stand, and the hi-hat on the left
        DrumAt(s, new Vector3(0f, 0.62f, -0.50f), 0.17f, 0.12f, Pearl);
        s.Tube(new Vector3(0f, 0.0f, -0.50f), new Vector3(0f, 0.55f, -0.50f), 0.012f, 0.012f, Chrome, 4);
        s.Tube(new Vector3(-0.55f, 0.0f, -0.60f), new Vector3(-0.55f, 0.95f, -0.60f), 0.012f, 0.012f, Chrome, 4);
        Cymbal(s, new Vector3(-0.55f, 0.96f, -0.60f), 0.17f);
        Cymbal(s, new Vector3(-0.55f, 0.99f, -0.60f), 0.17f);
        // two toms over the kick, a floor tom on the right
        DrumAt(s, new Vector3(-0.17f, 0.98f, -1.10f), 0.15f, 0.14f, Red);
        DrumAt(s, new Vector3(0.17f, 0.98f, -1.10f), 0.14f, 0.13f, Red);
        s.Tube(new Vector3(0f, 0.55f, -1.05f), new Vector3(0f, 0.92f, -1.10f), 0.014f, 0.014f, Chrome, 4);
        DrumAt(s, new Vector3(0.62f, 0.50f, -0.80f), 0.20f, 0.2f, Red);
        foreach (float a in new[] { 0f, 2.1f, 4.2f })
            s.Tube(new Vector3(0.62f + 0.17f * MathF.Cos(a), 0.0f, -0.80f + 0.17f * MathF.Sin(a)), new Vector3(0.62f + 0.17f * MathF.Cos(a), 0.40f, -0.80f + 0.17f * MathF.Sin(a)), 0.01f, 0.01f, Chrome, 4);
        // a crash on the left and a ride on the right, on their booms
        s.Tube(new Vector3(-0.65f, 0.0f, -1.20f), new Vector3(-0.65f, 1.45f, -1.20f), 0.012f, 0.012f, Chrome, 4);
        Cymbal(s, new Vector3(-0.65f, 1.46f, -1.20f), 0.22f);
        s.Tube(new Vector3(0.75f, 0.0f, -1.15f), new Vector3(0.75f, 1.30f, -1.15f), 0.012f, 0.012f, Chrome, 4);
        Cymbal(s, new Vector3(0.75f, 1.31f, -1.15f), 0.26f);
        // the throne
        s.Tube(new Vector3(0f, 0.38f, 0.32f), new Vector3(0f, 0.43f, 0.32f), 0.17f, 0.17f, Black, 10);
        s.Tube(new Vector3(0f, 0.0f, 0.32f), new Vector3(0f, 0.38f, 0.32f), 0.02f, 0.02f, Chrome, 5);
        return s.Build();
    }

    private static void DrumAt(MeshScratch s, Vector3 top, float radius, float height, Color shell)
    {
        s.Tube(top - new Vector3(0, height, 0), top, radius, radius, shell, 12);
        s.Tube(top, top + new Vector3(0, 0.012f, 0), radius * 0.96f, radius * 0.96f, Skin, 12);
    }

    private static void Cymbal(MeshScratch s, Vector3 centre, float radius)
    {
        s.Tube(centre, centre + new Vector3(0, 0.012f, 0), radius, radius * 0.9f, Brass, 12);
        s.Tube(centre + new Vector3(0, 0.012f, 0), centre + new Vector3(0, 0.03f, 0), radius * 0.18f, radius * 0.1f, BrassDark, 6);
    }
}
