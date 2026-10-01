using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Road railings (#126): the <see cref="LinearPropType.Guardrail"/>, <see cref="LinearPropType.Fence"/>
/// and <see cref="LinearPropType.MedianDouble"/> props the network stage placed, drawn into the
/// road mesh and given collision. Convention in <see cref="RoadRailing"/>: the points are the rail's
/// road-side face line with the foot height and the rail's top above it, the road on their right,
/// the posts on their left.
///
/// <para>
/// A guardrail is its W-beam band, folded once toward the road so it catches the light like a
/// profile, on posts every <c>Param</c> metres; a fence is a top and a middle rail on thinner posts.
/// Posts run <see cref="RoadRailing.PostSink"/> below their foot so they meet the slope beside the
/// road. Collision is one vertical strip along the face, from below the foot to the top: a car
/// glances off it and a player cannot walk through, whatever the posts do between two points.
/// </para>
/// </summary>
public static class RailingBuilder
{
    private static readonly Color BeamColor = new(0.68f, 0.70f, 0.72f);   // galvanised steel
    private static readonly Color PostColor = new(0.52f, 0.54f, 0.56f);
    private static readonly Color FenceColor = new(0.30f, 0.33f, 0.34f);  // painted steel

    /// <summary>The beam's fold: its middle stands this far nearer the road than its edges.</summary>
    private const float Fold = 0.05f;

    private const float GuardPost = 0.10f;
    private const float FencePost = 0.06f;
    private const float FenceRail = 0.06f;

    /// <summary>The collision strip runs on this far below the foot.</summary>
    private const float CollisionBelow = 0.3f;

    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var beam = BeamColor.SrgbToLinear();
        var post = PostColor.SrgbToLinear();
        var fence = FenceColor.SrgbToLinear();
        foreach (var p in tile.LinearProps)
        {
            if (!RoadRailing.IsRailing(p) || p.PointCount < 2) continue;
            var (line, left, foot, top) = Outline(p);
            void Q(Color c, Vector3 a, Vector3 b, Vector3 cc, Vector3 d) => Quad(vertices, colors, uvs, uv2s, indices, c, a, b, cc, d);
            bool isFence = p.Type == LinearPropType.Fence;
            int n = line.Length;
            for (int i = 0; i < n - 1; i++)
            {
                int j = i + 1;
                if (isFence)
                {
                    foreach (float h in (ReadOnlySpan<float>)[1f, 0.5f])
                        Q(fence, At(line[i], foot[i] + top[i] * h - FenceRail), At(line[j], foot[j] + top[j] * h - FenceRail),
                            At(line[j], foot[j] + top[j] * h), At(line[i], foot[i] + top[i] * h));
                }
                else
                {
                    // the band, its middle folded toward the road (right of the points)
                    Vector2 mi = line[i] - left[i] * Fold, mj = line[j] - left[j] * Fold;
                    float bi = foot[i] + top[i] - RoadRailing.BeamBand, bj = foot[j] + top[j] - RoadRailing.BeamBand;
                    float ci = foot[i] + top[i] - RoadRailing.BeamBand * 0.5f, cj = foot[j] + top[j] - RoadRailing.BeamBand * 0.5f;
                    Q(beam, At(line[i], bi), At(line[j], bj), At(mj, cj), At(mi, ci));
                    Q(beam, At(mi, ci), At(mj, cj), At(line[j], foot[j] + top[j]), At(line[i], foot[i] + top[i]));
                }
            }

            float spacing = p.Param > 0 ? p.Param : isFence ? RoadRailing.FencePostSpacing : RoadRailing.GuardrailPostSpacing;
            float size = isFence ? FencePost : GuardPost;
            float setback = isFence ? size * 0.5f : p.Thickness;
            var postColour = isFence ? fence : post;
            float next = 0, travelled = 0;
            for (int i = 0; i < n - 1; i++)
            {
                int j = i + 1;
                float len = line[i].DistanceTo(line[j]);
                if (len < 1e-4f) continue;
                var dir = (line[j] - line[i]) / len;
                var side = new Vector2(dir.Y, -dir.X);   // left of the points: behind the face
                for (; next <= travelled + len; next += spacing)
                {
                    float t = (next - travelled) / len;
                    var c = line[i].Lerp(line[j], t) + side * setback;
                    float y0 = Mathf.Lerp(foot[i], foot[j], t) - RoadRailing.PostSink;
                    float y1 = Mathf.Lerp(foot[i] + top[i], foot[j] + top[j], t) - (isFence ? 0f : 0.03f);
                    Box(Q, postColour, c, dir * (size * 0.5f), side * (size * 0.5f), y0, y1);
                }
                travelled += len;
            }
        }
    }

    /// <summary>Collision for every railing: a vertical strip along its face, below the foot to the top.</summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var p in tile.LinearProps)
        {
            if (!RoadRailing.IsRailing(p) || (p.Flags & PropFlags.Solid) == 0 || p.PointCount < 2) continue;
            var (line, _, foot, top) = Outline(p);
            for (int i = 0; i < line.Length - 1; i++)
            {
                int j = i + 1;
                faces.Add(At(line[i], foot[i] - CollisionBelow)); faces.Add(At(line[j], foot[j] - CollisionBelow)); faces.Add(At(line[j], foot[j] + top[j]));
                faces.Add(At(line[i], foot[i] - CollisionBelow)); faces.Add(At(line[j], foot[j] + top[j])); faces.Add(At(line[i], foot[i] + top[i]));
            }
        }
        return faces.ToArray();
    }

    /// <summary>The face line in plan, its left normals (averaged at the joints), foot heights and tops.</summary>
    private static (Vector2[] Line, Vector2[] Left, float[] Foot, float[] Top) Outline(RoadLinearProp p)
    {
        int n = p.PointCount;
        var line = new Vector2[n];
        var left = new Vector2[n];
        var foot = new float[n];
        var top = new float[n];
        for (int i = 0; i < n; i++)
        {
            line[i] = new Vector2(p.Points[i * 4], p.Points[i * 4 + 2]);
            foot[i] = p.Points[i * 4 + 1];
            top[i] = p.Points[i * 4 + 3];
        }
        for (int i = 0; i < n; i++)
        {
            var dir = line[Math.Min(n - 1, i + 1)] - line[Math.Max(0, i - 1)];
            if (dir.LengthSquared() < 1e-8f) dir = Vector2.Right;
            dir = dir.Normalized();
            left[i] = new Vector2(dir.Y, -dir.X);   // X east and Z south (x, y here = X, Z)
        }
        return (line, left, foot, top);
    }

    /// <summary>An upright box with no top or bottom: four quads around <paramref name="c"/>.</summary>
    private static void Box(Action<Color, Vector3, Vector3, Vector3, Vector3> q, Color colour, Vector2 c,
        Vector2 along, Vector2 across, float y0, float y1)
    {
        var a = c - along - across; var b = c + along - across;
        var d = c + along + across; var e = c - along + across;
        q(colour, At(a, y0), At(b, y0), At(b, y1), At(a, y1));
        q(colour, At(b, y0), At(d, y0), At(d, y1), At(b, y1));
        q(colour, At(d, y0), At(e, y0), At(e, y1), At(d, y1));
        q(colour, At(e, y0), At(a, y0), At(a, y1), At(e, y1));
    }

    private static Vector3 At(Vector2 plan, float y) => new(plan.X, y, plan.Y);

    private static void Quad(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices, Color color, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int i0 = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        for (int k = 0; k < 4; k++) { colors.Add(color); uvs.Add(Vector2.Zero); uv2s.Add(Vector2.Zero); }
        // the road material is cull_disabled, so winding only needs to be consistent
        indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
        indices.Add(i0); indices.Add(i0 + 2); indices.Add(i0 + 3);
    }
}
