using Godot;
using UnitSport.Avatar;
using UnitSport.Occasions;

namespace UnitSport.Interiors;

/// <summary>
/// A painted wooden sign board (#1250): over the door of a restaurant, café, pub, fast food or beer
/// garden that has no awning to carry its name (<see cref="VenueSigns"/>). A board in one of a few
/// painted colours, a moulded frame, two iron brackets into the wall and two little lamps over it (lit
/// at night through the prop material's vertex alpha); the name painted on in Saira, shaded like the
/// board. Neon stays for the halls, bars and clubs. Built once per sign, nothing per frame.
/// </summary>
public static class PlankSign
{
    /// <summary>The board's height and its letters', m [S: Swiss Beiz boards 0.4-0.7 m tall].</summary>
    public const float BoardH = 0.56f, Letter = 0.26f;

    private const float Depth = 0.05f, FrameW = 0.05f, Sink = 0.012f;

    /// <summary>Board paints: oxblood, bottle green, navy, black, oiled wood; the letters cream or gold on each.</summary>
    private static readonly (Color Board, Color Letters)[] Paints =
    {
        (PropColors.Matte(0.40f, 0.09f, 0.08f), new Color(0.96f, 0.86f, 0.55f)),
        (PropColors.Matte(0.10f, 0.26f, 0.16f), new Color(0.95f, 0.90f, 0.76f)),
        (PropColors.Matte(0.10f, 0.15f, 0.30f), new Color(0.96f, 0.86f, 0.55f)),
        (PropColors.Matte(0.08f, 0.08f, 0.08f), new Color(0.95f, 0.90f, 0.76f)),
        (PropColors.Matte(0.50f, 0.33f, 0.18f), new Color(0.14f, 0.09f, 0.05f)),
    };

    private static readonly Color Frame = PropColors.Matte(0.22f, 0.14f, 0.08f), Iron = PropColors.Matte(0.10f, 0.10f, 0.11f),
        Bulb = PropColors.Lamp(1.0f, 0.86f, 0.55f);

    /// <summary>The paint a venue's board gets, from its name: the same on every client.</summary>
    public static int PaintOf(string name) => (int)(Core.Fnv.Unit(name + "|plank") * Paints.Length) % Paints.Length;

    /// <summary>
    /// A board centred on the origin, its back against the wall at z = 0 and +Z facing the street, no
    /// wider than <paramref name="maxWidth"/> (a long name is lettered smaller).
    /// </summary>
    public static Node3D Make(string name, Material material, float maxWidth = 3.6f)
    {
        string text = NeonSign.Lettering(name);
        var (board, letters) = Paints[PaintOf(name)];
        // Saira's capitals are about 0.7 of the em and an average glyph about 0.55 of it wide
        float em = Letter / 0.7f, textW = Math.Max(1, text.Length) * em * 0.55f;
        float w = Math.Clamp(textW + 0.5f, 1.3f, maxWidth);
        em *= Math.Min(1f, (w - 0.4f) / textW);

        var root = new Node3D { Name = "PlankSign" };
        root.AddChild(new MeshInstance3D
        {
            Name = "Board",
            Mesh = Mesh(w, board).Mesh,
            MaterialOverride = material,
            // MeshScratch hands its meshes back turned half round: turned back, so +Z is the street
            Rotation = new Vector3(0, Mathf.Pi, 0),
            VisibilityRangeEnd = NeonSign.DrawnM,
        });
        const int fontSize = 96;
        root.AddChild(new Label3D
        {
            Name = "Name",
            Text = text,
            Font = NeonSign.SignFont(),
            FontSize = fontSize,
            PixelSize = em / fontSize,
            Modulate = letters,
            OutlineSize = 0,
            Shaded = true,
            DoubleSided = false,
            VisibilityRangeEnd = NeonSign.DrawnM,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // painted on: 1.2 cm proud of the board, inside the frame's 2.4 cm
            Position = new Vector3(0, 0, Depth + 0.012f),
        });
        return root;
    }

    private static readonly Dictionary<(int, Color), PropKind> Meshes = new();

    /// <summary>The board, frame, brackets and lamps of width <paramref name="w"/>, authored with the wall at z = 0 and the street at +Z.</summary>
    private static PropKind Mesh(float w, Color board)
    {
        // widths round to 10 cm, so a street's signs share a few meshes
        int key = (int)MathF.Round(w * 10);
        if (Meshes.TryGetValue((key, board), out var kind)) return kind;
        float bw = key / 10f;
        kind = new PropKind($"Plank sign {bw:F1}", () => Build(bw, board), candle: false);
        Meshes[(key, board)] = kind;
        return kind;
    }

    private static ArrayMesh Build(float w, Color board)
    {
        var s = new MeshScratch();
        // the board, standing off the wall on its brackets' plates
        s.Box(new Vector3(0, 0, Depth / 2), new Vector3(w, BoardH, Depth), board);
        // the moulded frame round its face, 2.4 cm proud of it and a little past its edges
        // its back a hand off the board's, so the two never share the plane against the wall
        const float fd = Depth + 0.024f - 0.012f;
        float fz = Depth + 0.024f - fd / 2;
        s.Box(new Vector3(0, BoardH / 2 - FrameW / 2 + 0.01f, fz), new Vector3(w + 0.02f + 2 * Sink, FrameW, fd), Frame);
        s.Box(new Vector3(0, -BoardH / 2 + FrameW / 2 - 0.01f, fz), new Vector3(w + 0.02f + 2 * Sink, FrameW, fd), Frame);
        foreach (int side in new[] { -1, 1 })
        {
            s.Box(new Vector3(side * (w / 2 - FrameW / 2 + 0.01f), 0, fz), new Vector3(FrameW, BoardH - 2 * FrameW + 0.02f + 2 * Sink, fd - 2 * Sink), Frame);
            // a bracket's wall plate and arm over the board's top, then the lamp it carries out front
            float x = side * w * 0.3f;
            s.Box(new Vector3(x, BoardH / 2 + 0.12f, 0.01f), new Vector3(0.06f, 0.12f, 0.02f), Iron);
            s.Box(new Vector3(x, BoardH / 2 + 0.22f, 0.165f), new Vector3(0.025f, 0.025f, 0.33f), Iron);
            s.Box(new Vector3(x, BoardH / 2 + 0.17f, 0.33f), new Vector3(0.12f, 0.05f, 0.09f), Iron);
            s.Box(new Vector3(x, BoardH / 2 + 0.14f - Sink, 0.33f), new Vector3(0.08f, 0.02f, 0.06f), Bulb);
        }
        return s.Build();
    }

    /// <summary>Plank signs in the model viewer: a short name and a long one on each paint.</summary>
    [Core.Showcase("Interiors", "Plank sign")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> ShowcasePlanks()
    {
        string[] names = { "Café de la Gare", "Restaurant du Commerce", "The Old Pub", "Pinte Vaudoise", "Burger" };
        foreach (var n in names)
            yield return (n, () =>
            {
                var root = new Node3D();
                var sign = Make(n, Styles.StyleKit.Material(Styles.MaterialRole.Prop));
                sign.Position = new Vector3(0, 0.6f, 0);
                root.AddChild(sign);
                return root;
            });
    }
}
