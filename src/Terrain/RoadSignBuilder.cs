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

    public static bool IsSign(RoadPointProp p) => p.Type is PointPropType.YieldSign or PointPropType.MainRoadSign;

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
            if (p.Type == PointPropType.YieldSign)
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
