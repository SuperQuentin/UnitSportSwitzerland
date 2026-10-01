using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Retaining walls (#125): the <see cref="LinearPropType.RetainingWallFill"/>/<c>Cut</c> props
/// the network stage placed, drawn as a closed prism and given collision for the face and the
/// cap. Geometry convention in <see cref="RoadEmbankment"/>: the points are the face line with
/// the foot height and the wall's height, the solid lies on their left, <c>Thickness</c> deep.
/// The ground behind and below is the road blend's (<c>TerrainMeshBuilder.ComputeRoadBlend</c>
/// frees it past the free line); the face is sunk <see cref="Sink"/> below its foot so it meets
/// that ground with no gap whatever the lattice does between two samples.
/// A <see cref="RoadEmbankment.VariantTlmWall"/> prop only frees the ground: the surveyed TLM wall
/// segment is what is drawn there.
/// </summary>
public static class RoadWallBuilder
{
    private static readonly Color FaceColor = new Color(0.55f, 0.53f, 0.50f);   // weathered concrete
    private static readonly Color CapColor = new Color(0.62f, 0.61f, 0.58f);

    /// <summary>How far the face runs on below its stored foot, into the ground.</summary>
    private const float Sink = 1.0f;

    /// <summary>
    /// A fill wall's cap is drawn this far under the road it carries: its inner metre lies under the
    /// ribbon, and a coplanar cap would z-fight it.
    /// </summary>
    private const float CapDrop = 0.04f;

    public static bool IsWall(RoadLinearProp p) =>
        p.Type is LinearPropType.RetainingWallFill or LinearPropType.RetainingWallCut
        && p.Variant == RoadEmbankment.VariantGenerated && p.PointCount >= 2;

    /// <summary>Appends every drawn wall of the tile to a road mesh under construction.</summary>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var face = FaceColor.SrgbToLinear();
        var cap = CapColor.SrgbToLinear();
        foreach (var wall in tile.LinearProps)
        {
            if (!IsWall(wall)) continue;
            var (front, back, top, foot) = Outline(wall, CapDropFor(wall));
            int n = front.Length;
            for (int i = 0; i < n - 1; i++)
            {
                // face, cap, back: each its own flat quad so it shades flat
                Quad(vertices, colors, uvs, uv2s, indices, face,
                    At(front[i], foot[i]), At(front[i + 1], foot[i + 1]), At(front[i + 1], top[i + 1]), At(front[i], top[i]));
                Quad(vertices, colors, uvs, uv2s, indices, cap,
                    At(front[i], top[i]), At(front[i + 1], top[i + 1]), At(back[i + 1], top[i + 1]), At(back[i], top[i]));
                Quad(vertices, colors, uvs, uv2s, indices, face,
                    At(back[i], top[i]), At(back[i + 1], top[i + 1]), At(back[i + 1], foot[i + 1]), At(back[i], foot[i]));
            }
            foreach (int i in (ReadOnlySpan<int>)[0, n - 1])
                Quad(vertices, colors, uvs, uv2s, indices, face,
                    At(front[i], foot[i]), At(front[i], top[i]), At(back[i], top[i]), At(back[i], foot[i]));
        }
    }

    /// <summary>
    /// Collision triangles for every drawn wall: the face, so a body walking off the top falls
    /// past it and a car cannot climb it, and the cap at the road's own height, so the shoulder
    /// over the one-cell drop the heightfield has inside the wall is solid. The back stays out: it
    /// is buried in the shelf or the hill, and a vertical edge flush with the road would snag a
    /// wheel. Goes into the bridge-deck body, which is two-sided.
    /// </summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var wall in tile.LinearProps)
        {
            if (!IsWall(wall)) continue;
            var (front, back, top, foot) = Outline(wall, 0f);
            for (int i = 0; i < front.Length - 1; i++)
            {
                Tri(faces, At(front[i], foot[i]), At(front[i + 1], foot[i + 1]), At(front[i + 1], top[i + 1]), At(front[i], top[i]));
                Tri(faces, At(front[i], top[i]), At(front[i + 1], top[i + 1]), At(back[i + 1], top[i + 1]), At(back[i], top[i]));
            }
        }
        return faces.ToArray();
    }

    private static float CapDropFor(RoadLinearProp wall) =>
        wall.Type == LinearPropType.RetainingWallFill ? CapDrop : 0f;

    /// <summary>Plan-view face and back lines, top and (sunk) foot heights per point.</summary>
    private static (Vector2[] Front, Vector2[] Back, float[] Top, float[] Foot) Outline(RoadLinearProp wall, float capDrop)
    {
        int n = wall.PointCount;
        var p = wall.Points;
        var front = new Vector2[n];
        var back = new Vector2[n];
        var top = new float[n];
        var foot = new float[n];
        for (int i = 0; i < n; i++)
        {
            front[i] = new Vector2(p[i * 4], p[i * 4 + 2]);
            foot[i] = p[i * 4 + 1] - Sink;
            top[i] = p[i * 4 + 1] + p[i * 4 + 3] - capDrop;
        }
        for (int i = 0; i < n; i++)
        {
            var dir = front[Math.Min(n - 1, i + 1)] - front[Math.Max(0, i - 1)];
            if (dir.LengthSquared() < 1e-8f) dir = Vector2.Right;
            dir = dir.Normalized();
            // left of the point order, X east and Z south (x, y here = X, Z)
            var left = new Vector2(dir.Y, -dir.X);
            back[i] = front[i] + left * wall.Thickness;
        }
        return (front, back, top, foot);
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

    private static void Tri(List<Vector3> faces, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        faces.Add(a); faces.Add(b); faces.Add(c);
        faces.Add(a); faces.Add(c); faces.Add(d);
    }
}
