using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Retaining walls (#125): the <see cref="LinearPropType.RetainingWallFill"/>/<c>Cut</c> props
/// the network stage placed, drawn and given collision. Geometry convention in
/// <see cref="RoadEmbankment"/>: the points are the face line with the foot height and the wall's
/// height, the solid lies on their left.
///
/// <para>
/// Cross-section, face to back: the face, sunk <see cref="Sink"/> below its foot so it meets the
/// ground with no gap whatever the lattice does between two samples; the crown,
/// <see cref="RoadEmbankment.WallCrown"/> thick (6 cm over the road on a fill wall); then the
/// cover out to <see cref="RoadEmbankment.CoverDepth"/>, which lies over the heightfield's
/// one-cell transition (the road blend, <c>TerrainMeshBuilder.ComputeRoadBlend</c>, puts it
/// there): paving under the road's edge behind a fill wall, the level backfill 20 cm under the
/// crown behind a cut wall. A cut wall also gets the paved gutter between the road's edge and its
/// foot.
/// </para>
///
/// <para>
/// A <see cref="RoadEmbankment.VariantTlmWall"/> prop only frees the ground: the surveyed TLM wall
/// segment is what is drawn there.
/// </para>
/// </summary>
public static class RoadWallBuilder
{
    private static readonly Color FaceColor = new Color(0.55f, 0.53f, 0.50f);   // weathered concrete
    private static readonly Color CrownColor = new Color(0.62f, 0.61f, 0.58f);
    private static readonly Color PavingColor = new Color(0.34f, 0.34f, 0.33f);  // the road's asphalt
    private static readonly Color BackfillColor = new Color(0.45f, 0.41f, 0.33f); // gravel path, earth

    /// <summary>How far the face runs on below its stored foot, into the ground.</summary>
    private const float Sink = 1.0f;

    /// <summary>
    /// The paving behind a fill wall is drawn this far under the road it carries: most of it lies
    /// under the ribbon, and a coplanar surface would z-fight it. Its collision is at the road.
    /// </summary>
    private const float PavingDrop = 0.04f;

    /// <summary>The cut wall's gutter is drawn this far under the road beside it.</summary>
    private const float GutterDrop = 0.01f;

    public static bool IsWall(RoadLinearProp p) =>
        p.Type is LinearPropType.RetainingWallFill or LinearPropType.RetainingWallCut
        && p.Variant == RoadEmbankment.VariantGenerated && p.PointCount >= 2;

    /// <summary>Appends every drawn wall of the tile to a road mesh under construction.</summary>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var face = FaceColor.SrgbToLinear();
        var crown = CrownColor.SrgbToLinear();
        var paving = PavingColor.SrgbToLinear();
        var backfill = BackfillColor.SrgbToLinear();
        foreach (var wall in tile.LinearProps)
        {
            if (!IsWall(wall)) continue;
            var w = Outline(wall, visual: true);
            bool fill = wall.Type == LinearPropType.RetainingWallFill;
            var coverColour = fill ? paving : backfill;
            void Q(Color c, Vector3 a, Vector3 b, Vector3 cc, Vector3 d) => Quad(vertices, colors, uvs, uv2s, indices, c, a, b, cc, d);
            int n = w.Front.Length;
            for (int i = 0; i < n - 1; i++)
            {
                int j = i + 1;
                // each part its own flat quad so it shades flat
                Q(face, At(w.Front[i], w.Bottom[i]), At(w.Front[j], w.Bottom[j]), At(w.Front[j], w.Crown[j]), At(w.Front[i], w.Crown[i]));
                Q(crown, At(w.Front[i], w.Crown[i]), At(w.Front[j], w.Crown[j]), At(w.Inner[j], w.Crown[j]), At(w.Inner[i], w.Crown[i]));
                Q(crown, At(w.Inner[i], w.Crown[i]), At(w.Inner[j], w.Crown[j]), At(w.Inner[j], w.Cover[j]), At(w.Inner[i], w.Cover[i]));
                Q(coverColour, At(w.Inner[i], w.Cover[i]), At(w.Inner[j], w.Cover[j]), At(w.Back[j], w.Cover[j]), At(w.Back[i], w.Cover[i]));
                Q(face, At(w.Back[i], w.Cover[i]), At(w.Back[j], w.Cover[j]), At(w.Back[j], w.Bottom[j]), At(w.Back[i], w.Bottom[i]));
                if (!fill)
                    Q(paving, At(w.Gutter[i], w.Road[i]), At(w.Gutter[j], w.Road[j]), At(w.Front[j], w.Road[j]), At(w.Front[i], w.Road[i]));
            }
            foreach (int i in (ReadOnlySpan<int>)[0, n - 1])
            {
                Q(face, At(w.Front[i], w.Bottom[i]), At(w.Front[i], w.Crown[i]), At(w.Inner[i], w.Crown[i]), At(w.Inner[i], w.Bottom[i]));
                Q(face, At(w.Inner[i], w.Bottom[i]), At(w.Inner[i], w.Cover[i]), At(w.Back[i], w.Cover[i]), At(w.Back[i], w.Bottom[i]));
            }
        }
    }

    /// <summary>
    /// Collision triangles for every drawn wall, the same surfaces as drawn: the face, so a body
    /// walking off the top falls past it and a car cannot climb it; the crown and its lip; the
    /// cover, which holds the road (fill) or the backfill (cut) over the heightfield's transition;
    /// the crown's ends. A cut wall's cover back too (the hillside may lie a little under it); a
    /// fill wall's stays out, it is buried under the road and an edge flush with the road would
    /// snag a wheel. The gutter is the heightfield's own. Goes into the bridge-deck body, which is
    /// two-sided.
    /// </summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var wall in tile.LinearProps)
        {
            if (!IsWall(wall)) continue;
            var w = Outline(wall, visual: false);
            bool fill = wall.Type == LinearPropType.RetainingWallFill;
            int n = w.Front.Length;
            for (int i = 0; i < n - 1; i++)
            {
                int j = i + 1;
                Tri(faces, At(w.Front[i], w.Bottom[i]), At(w.Front[j], w.Bottom[j]), At(w.Front[j], w.Crown[j]), At(w.Front[i], w.Crown[i]));
                Tri(faces, At(w.Front[i], w.Crown[i]), At(w.Front[j], w.Crown[j]), At(w.Inner[j], w.Crown[j]), At(w.Inner[i], w.Crown[i]));
                Tri(faces, At(w.Inner[i], w.Crown[i]), At(w.Inner[j], w.Crown[j]), At(w.Inner[j], w.Cover[j]), At(w.Inner[i], w.Cover[i]));
                Tri(faces, At(w.Inner[i], w.Cover[i]), At(w.Inner[j], w.Cover[j]), At(w.Back[j], w.Cover[j]), At(w.Back[i], w.Cover[i]));
                if (!fill)
                    Tri(faces, At(w.Back[i], w.Cover[i]), At(w.Back[j], w.Cover[j]), At(w.Back[j], w.Bottom[j]), At(w.Back[i], w.Bottom[i]));
            }
            foreach (int i in (ReadOnlySpan<int>)[0, n - 1])
                Tri(faces, At(w.Front[i], w.Bottom[i]), At(w.Front[i], w.Crown[i]), At(w.Inner[i], w.Crown[i]), At(w.Inner[i], w.Bottom[i]));
        }
        return faces.ToArray();
    }

    /// <summary>
    /// One wall's cross-section lines in plan (face, crown's inner edge, cover's back, gutter's
    /// road edge) and heights per point (sunk bottom, crown, cover, road).
    /// </summary>
    private readonly record struct Shape(Vector2[] Front, Vector2[] Inner, Vector2[] Back, Vector2[] Gutter,
        float[] Bottom, float[] Crown, float[] Cover, float[] Road);

    private static Shape Outline(RoadLinearProp wall, bool visual)
    {
        int n = wall.PointCount;
        var p = wall.Points;
        bool fill = wall.Type == LinearPropType.RetainingWallFill;
        var front = new Vector2[n];
        var inner = new Vector2[n];
        var back = new Vector2[n];
        var gutter = new Vector2[n];
        var bottom = new float[n];
        var crownY = new float[n];
        var coverY = new float[n];
        var road = new float[n];
        for (int i = 0; i < n; i++)
        {
            front[i] = new Vector2(p[i * 4], p[i * 4 + 2]);
            float foot = p[i * 4 + 1], top = foot + p[i * 4 + 3];
            bottom[i] = foot - Sink;
            if (fill)
            {
                // top = the road it carries
                crownY[i] = top + RoadEmbankment.FillCrownLift;
                coverY[i] = top - (visual ? PavingDrop : 0f);
                road[i] = top;
            }
            else
            {
                // top = the crown; foot = the road beside it
                crownY[i] = top;
                coverY[i] = top - RoadEmbankment.CutCrownOver;
                road[i] = foot - GutterDrop;
            }
        }
        for (int i = 0; i < n; i++)
        {
            var dir = front[Math.Min(n - 1, i + 1)] - front[Math.Max(0, i - 1)];
            if (dir.LengthSquared() < 1e-8f) dir = Vector2.Right;
            dir = dir.Normalized();
            // left of the point order, X east and Z south (x, y here = X, Z)
            var left = new Vector2(dir.Y, -dir.X);
            inner[i] = front[i] + left * wall.Thickness;
            back[i] = front[i] + left * RoadEmbankment.CoverDepth;
            gutter[i] = front[i] - left * (float)RoadEmbankment.CutFaceOffset;
        }
        return new Shape(front, inner, back, gutter, bottom, crownY, coverY, road);
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
