using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Junction signs (#121) from the <c>.road</c> v3 point props: the Swiss "Kein Vortritt" (3.02,
/// a red-bordered white triangle on its tip) and "Hauptstrasse" (3.03, a yellow square on its
/// corner with a white and a black band), each on a grey pole. Low-poly: a square pole, the plate
/// as a flat two-sided outline and face (the road material draws both sides). Collision is the
/// pole only, a thin box any vehicle can hit; the plate is above a car. Dimensions:
/// <see cref="RoadSigns"/> (docs/notes/tools/junction-priority.md).
/// </summary>
public static class RoadSignBuilder
{
    private static readonly Color Pole = new(0.62f, 0.63f, 0.64f);      // galvanised steel
    private static readonly Color Red = new(0.78f, 0.10f, 0.12f);
    private static readonly Color White = new(0.93f, 0.93f, 0.91f);
    private static readonly Color Yellow = new(0.98f, 0.78f, 0.10f);
    private static readonly Color Black = new(0.08f, 0.08f, 0.08f);
    private static readonly Color Back = new(0.55f, 0.56f, 0.57f);      // the plate's bare aluminium back

    /// <summary>The pole runs on this far below its stored foot, so it meets the ground whatever the lattice does.</summary>
    private const float Sink = 0.5f;

    /// <summary>The plate's face stands this far in front of the pole's axis, its back this far behind the face.</summary>
    private const float PlateOffset = RoadSigns.PoleDiameter * 0.5f + 0.01f, PlateThickness = 0.01f;

    public static bool IsSign(RoadPointProp p) => p.Type is PointPropType.YieldSign or PointPropType.MainRoadSign or PointPropType.CantonSign;

    /// <summary>Appends every sign of the tile to a road mesh under construction.</summary>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        foreach (var p in tile.PointProps)
        {
            if (!IsSign(p)) continue;
            var (foot, front, right) = Frame(p);
            float plate = RoadSigns.PlateHeight(p.Type, p.Variant);
            float top = p.Height, bottom = p.Height - plate;

            // pole up to the plate's top, behind it
            float r = RoadSigns.PoleDiameter * 0.5f;
            var corners = new[] { front * r + right * r, front * r - right * r, -front * r - right * r, -front * r + right * r };
            var pole = Pole.SrgbToLinear();
            for (int k = 0; k < 4; k++)
            {
                var a = corners[k];
                var b = corners[(k + 1) % 4];
                Quad(vertices, colors, uvs, uv2s, indices, pole,
                    foot + a + Vector3.Down * Sink, foot + b + Vector3.Down * Sink, foot + b + Vector3.Up * top, foot + a + Vector3.Up * top);
            }

            var centre = foot + front * PlateOffset + Vector3.Up * ((top + bottom) * 0.5f);
            float side = RoadSigns.Side(p.Type, p.Variant), border = RoadSigns.Border(p.Type, p.Variant);
            if (p.Type == PointPropType.CantonSign)
                CantonPlate(vertices, colors, uvs, uv2s, indices, p.Variant, centre, front, right);
            else if (p.Type == PointPropType.YieldSign)
            {
                // on its tip: the inscribed circle's centre is two thirds of the height above the tip
                float h = plate;
                var c = centre + Vector3.Up * (h / 6f);
                Polygon(vertices, colors, uvs, uv2s, indices, Back.SrgbToLinear(), Triangle(c - front * PlateThickness, right, side, h));
                Polygon(vertices, colors, uvs, uv2s, indices, Red.SrgbToLinear(), Triangle(c, right, side, h));
                // the white field: the triangle shrunk by the border width (inradius h/3)
                float s = 1f - border / (h / 3f);
                Polygon(vertices, colors, uvs, uv2s, indices, White.SrgbToLinear(), Triangle(c + front * 0.002f, right, side * s, h * s));
            }
            else
            {
                float d = plate * 0.5f;   // half diagonal
                Polygon(vertices, colors, uvs, uv2s, indices, Back.SrgbToLinear(), Diamond(centre - front * PlateThickness, right, d));
                // SSV 3.03: a thin black edge, a white band (a tenth of the side), the yellow field
                float edge = border * 1.4142f, band = side * 0.1f * 1.4142f;
                Polygon(vertices, colors, uvs, uv2s, indices, Black.SrgbToLinear(), Diamond(centre, right, d));
                Polygon(vertices, colors, uvs, uv2s, indices, White.SrgbToLinear(), Diamond(centre + front * 0.001f, right, d - edge));
                Polygon(vertices, colors, uvs, uv2s, indices, Yellow.SrgbToLinear(), Diamond(centre + front * 0.002f, right, d - edge - band));
            }
        }
    }

    // a canton sign (#1038): the plate's edge, the margin round the shield and the text, the shield's size; the name's letters
    private const float CantonEdge = 0.02f, CantonMargin = 0.05f, ShieldWidth = 0.24f, ShieldHeight = 0.30f;
    private const int NameFontSize = 40;
    private const float NamePixel = 0.0025f;

    /// <summary>
    /// A canton's border sign in a test world (#1038): a white plate with a black edge, the canton's shield on its left (Geneva
    /// yellow and red side by side, Vaud white over green, the default zone grey), its name right of it (<see cref="Labels"/>).
    /// </summary>
    private static void CantonPlate(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        byte variant, Vector3 centre, Vector3 front, Vector3 right)
    {
        float w = RoadSigns.CantonSignWidth * 0.5f, h = RoadSigns.CantonSignHeight * 0.5f;
        Vector3[] Rect(Vector3 c, float hw, float hh) =>
            [c - right * hw + Vector3.Down * hh, c + right * hw + Vector3.Down * hh, c + right * hw + Vector3.Up * hh, c - right * hw + Vector3.Up * hh];
        Polygon(vertices, colors, uvs, uv2s, indices, Back.SrgbToLinear(), Rect(centre - front * PlateThickness, w, h));
        Polygon(vertices, colors, uvs, uv2s, indices, Black.SrgbToLinear(), Rect(centre, w, h));
        Polygon(vertices, colors, uvs, uv2s, indices, White.SrgbToLinear(), Rect(centre + front * 0.001f, w - CantonEdge, h - CantonEdge));

        // the shield: straight sides down to a third of its height, then to a point
        var c = centre + front * 0.002f + right * (-w + CantonMargin + ShieldWidth * 0.5f);
        float sw = ShieldWidth * 0.5f, sh = ShieldHeight * 0.5f, shoulder = -sh + ShieldHeight * 0.35f;
        Vector3 At(float x, float y) => c + right * x + Vector3.Up * y;
        var (first, second, sideBySide) = Arms(RoadSigns.CantonCode(variant));
        if (sideBySide)
        {
            Polygon(vertices, colors, uvs, uv2s, indices, first.SrgbToLinear(), [At(-sw, sh), At(0, sh), At(0, -sh), At(-sw, shoulder)]);
            Polygon(vertices, colors, uvs, uv2s, indices, second.SrgbToLinear(), [At(0, sh), At(sw, sh), At(sw, shoulder), At(0, -sh)]);
        }
        else
        {
            Polygon(vertices, colors, uvs, uv2s, indices, first.SrgbToLinear(), [At(-sw, 0), At(sw, 0), At(sw, sh), At(-sw, sh)]);
            Polygon(vertices, colors, uvs, uv2s, indices, second.SrgbToLinear(), [At(-sw, 0), At(-sw, shoulder), At(0, -sh), At(sw, shoulder), At(sw, 0)]);
        }
    }

    /// <summary>A canton's shield in two fields: Geneva's yellow and red side by side, Vaud's white over green; others grey.</summary>
    private static (Color First, Color Second, bool SideBySide) Arms(string? canton) => canton switch
    {
        "GE" => (Yellow, Red, true),
        "VD" => (White, new Color(0.10f, 0.50f, 0.22f), false),
        _ => (Back, Back, false),
    };

    /// <summary>A canton sign's name (#1038), tile-local: the middle of the text, the way the plate faces, the width it wraps to.</summary>
    public readonly record struct Label(Vector3 Position, Vector3 Front, string Text, float Width);

    /// <summary>The names on the tile's canton signs (drawn as <see cref="Label3D"/>s beside the road mesh); null when it has none.</summary>
    public static Label[]? Labels(RoadTile tile)
    {
        List<Label>? labels = null;
        foreach (var p in tile.PointProps)
        {
            if (p.Type != PointPropType.CantonSign) continue;
            var (foot, front, right) = Frame(p);
            float w = RoadSigns.CantonSignWidth * 0.5f, middle = p.Height - RoadSigns.CantonSignHeight * 0.5f;
            // right of the shield: from its margin to the plate's edge less one
            float from = -w + 2 * CantonMargin + ShieldWidth, to = w - CantonMargin;
            var at = foot + front * (PlateOffset + 0.004f) + Vector3.Up * middle + right * ((from + to) * 0.5f);
            (labels ??= new()).Add(new Label(at, front, RoadSigns.CantonName(p.Variant), to - from));
        }
        return labels?.ToArray();
    }

    /// <summary>A name as a node: black letters on the plate, wrapped to its width, seen from the front only.</summary>
    public static Label3D Make(Label label) => new()
    {
        Text = label.Text, FontSize = NameFontSize, PixelSize = NamePixel, Modulate = Black, OutlineSize = 0,
        AutowrapMode = TextServer.AutowrapMode.WordSmart, Width = label.Width / NamePixel,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        DoubleSided = false, AlphaCut = Label3D.AlphaCutMode.Discard,
        Transform = new Transform3D(Basis.LookingAt(-label.Front, Vector3.Up), label.Position),
    };

    /// <summary>Collision triangles for the poles: a closed square column from below the ground to the plate's top.</summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var p in tile.PointProps)
        {
            if (!IsSign(p) || (p.Flags & PropFlags.Solid) == 0) continue;
            var (foot, front, right) = Frame(p);
            float r = RoadSigns.PoleDiameter * 0.5f;
            var corners = new[] { front * r + right * r, front * r - right * r, -front * r - right * r, -front * r + right * r };
            for (int k = 0; k < 4; k++)
            {
                var a0 = foot + corners[k] + Vector3.Down * Sink;
                var b0 = foot + corners[(k + 1) % 4] + Vector3.Down * Sink;
                var a1 = foot + corners[k] + Vector3.Up * p.Height;
                var b1 = foot + corners[(k + 1) % 4] + Vector3.Up * p.Height;
                faces.Add(a0); faces.Add(b0); faces.Add(b1);
                faces.Add(a0); faces.Add(b1); faces.Add(a1);
            }
        }
        return faces.ToArray();
    }

    /// <summary>Foot position, the way the plate faces (horizontal) and its right as seen by the driver it faces.</summary>
    private static (Vector3 Foot, Vector3 Front, Vector3 Right) Frame(RoadPointProp p)
    {
        // heading about +Y; 0 = facing -Z
        var front = new Vector3(-Mathf.Sin(p.Heading), 0f, -Mathf.Cos(p.Heading));
        // the driver looks along -front: their right is front rotated a quarter turn the other way
        var right = new Vector3(front.Z, 0f, -front.X);
        return (new Vector3(p.X, p.Y, p.Z), front, right);
    }

    /// <summary>A triangle on its tip: base on top, centred on <paramref name="c"/> (inscribed circle's centre).</summary>
    private static Vector3[] Triangle(Vector3 c, Vector3 right, float side, float height) =>
    [
        c + Vector3.Down * (height * 2f / 3f),
        c + Vector3.Up * (height / 3f) + right * (side * 0.5f),
        c + Vector3.Up * (height / 3f) - right * (side * 0.5f),
    ];

    private static Vector3[] Diamond(Vector3 c, Vector3 right, float halfDiagonal) =>
    [
        c + Vector3.Down * halfDiagonal, c + right * halfDiagonal, c + Vector3.Up * halfDiagonal, c - right * halfDiagonal,
    ];

    private static void Polygon(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices, Color color, Vector3[] ring)
    {
        int start = vertices.Count;
        foreach (var v in ring)
        {
            vertices.Add(v);
            colors.Add(color);
            uvs.Add(Vector2.Zero);
            uv2s.Add(Vector2.Zero);
        }
        for (int i = 1; i + 1 < ring.Length; i++) { indices.Add(start); indices.Add(start + i); indices.Add(start + i + 1); }
    }

    private static void Quad(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices, Color color, Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        Polygon(vertices, colors, uvs, uv2s, indices, color, [a, b, c, d]);
}
