using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Sidewalks (#119): the v3 <see cref="RoadSide.SidewalkDm"/>/<see cref="RoadSide.KerbCm"/> the
/// network stage (<c>StreetPlanner</c>) wrote, drawn beside the carriageway and given collision.
/// A kerbed side is a slab: its inner edge on the ribbon's edge (offset exactly as
/// <c>RoadMeshBuilder.AppendSegment</c> offsets it, per-vertex bisector), its top the kerb height
/// above the road, a kerb face down to the road, and a skirt on the outer edge reaching under the
/// ground the road blend leaves there. A side with no kerb (a square, a 3 m lane) is flush paving
/// at road height. Ends where the sidewalk stops get an end face; where the next piece carries it
/// on, nothing.
///
/// <para>
/// Collision is the top and the kerb, in the two-sided bridge-deck body, with the kerb
/// <b>chamfered</b> (<see cref="Chamfer"/> out for the kerb's height up) and so are the open
/// ends: a foot capsule of 0.32 m meets a vertical 12 cm step at 51°, at the edge of its 52°
/// <c>FloorMaxAngle</c>, and would stop dead at some kerbs; a 45° chamfer it walks up. Cars
/// (0.85 m) meet even a vertical one at 31°. Flush paving needs none: the heightfield under it is
/// the road's. The ground under a slab is the road blend's (raised to the kerb a lattice cell in
/// from the kerb line, <c>ComputeRoadBlend</c>), so walking off the outer edge carries on level.
/// </para>
/// </summary>
public static class RoadStreetBuilder
{
    private static readonly Color SidewalkColor = new Color(0.47f, 0.47f, 0.46f);   // asphalt, a shade lighter than the road
    private static readonly Color KerbColor = new Color(0.70f, 0.69f, 0.66f);       // granite kerbstones
    private static readonly Color PavingColor = new Color(0.56f, 0.53f, 0.48f);     // flush paving

    /// <summary>The skirt runs this far below the top: the visual ground sits 0.35 m under the road.</summary>
    private const float Skirt = 0.6f;

    /// <summary>The collision kerb rises over this much plan distance (45° for a 12 cm kerb).</summary>
    public const float Chamfer = 0.12f;

    public static bool HasSidewalk(RoadSegment s) =>
        s.PointCount >= 2 && RoadEmbankment.IsAtGrade(s)
        && (s.Attributes.Left.SidewalkDm > 0 || s.Attributes.Right.SidewalkDm > 0);

    /// <summary>One side of one segment: inner (ribbon edge) and outer plan points, road height, kerb, open ends.</summary>
    private readonly record struct Side(Vector3[] Inner, Vector3[] Outer, Vector3[] Forward, float Kerb, bool OpenStart, bool OpenEnd);

    /// <summary>Appends every sidewalk of the tile to a road mesh under construction.</summary>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var top = SidewalkColor.SrgbToLinear();
        var kerb = KerbColor.SrgbToLinear();
        var paving = PavingColor.SrgbToLinear();
        foreach (var side in Sides(tile))
        {
            var (inner, outer) = (side.Inner, side.Outer);
            int n = inner.Length;
            var up = new Vector3(0, side.Kerb, 0);
            var down = new Vector3(0, side.Kerb - Skirt, 0);
            var surface = side.Kerb > 0 ? top : paving;
            for (int i = 0; i < n - 1; i++)
            {
                Quad(vertices, colors, uvs, uv2s, indices, surface, inner[i] + up, inner[i + 1] + up, outer[i + 1] + up, outer[i] + up);
                if (side.Kerb > 0)
                    Quad(vertices, colors, uvs, uv2s, indices, kerb, inner[i], inner[i + 1], inner[i + 1] + up, inner[i] + up);
                Quad(vertices, colors, uvs, uv2s, indices, kerb, outer[i] + up, outer[i + 1] + up, outer[i + 1] + down, outer[i] + down);
            }
            if (side.Kerb <= 0) continue;
            if (side.OpenStart)
                Quad(vertices, colors, uvs, uv2s, indices, kerb, inner[0], inner[0] + up, outer[0] + up, outer[0]);
            if (side.OpenEnd)
                Quad(vertices, colors, uvs, uv2s, indices, kerb, inner[n - 1], inner[n - 1] + up, outer[n - 1] + up, outer[n - 1]);
        }

        // junction corners (APRP): the top, and a face down every open edge (kerb or outer, the same skirt)
        foreach (var area in tile.AreaProps)
        {
            if (area.Type != AreaPropType.Sidewalk || area.Vertices.Length < 9) continue;
            var up = new Vector3(0, area.Height, 0);
            var down = new Vector3(0, area.Height - Skirt, 0);
            var surface = area.Height > 0 ? top : paving;
            for (int k = 0; k + 2 < area.Indices.Length; k += 3)
            {
                int i0 = vertices.Count;
                for (int m = 0; m < 3; m++)
                {
                    vertices.Add(Vertex(area, area.Indices[k + m]) + up);
                    colors.Add(surface); uvs.Add(Vector2.Zero); uv2s.Add(Vector2.Zero);
                }
                indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
            }
            foreach (var (a, b) in OpenEdges(area))
                Quad(vertices, colors, uvs, uv2s, indices, kerb, Vertex(area, a) + up, Vertex(area, b) + up, Vertex(area, b) + down, Vertex(area, a) + down);
        }
    }

    private static Vector3 Vertex(RoadAreaProp area, int i) =>
        new(area.Vertices[i * 3], area.Vertices[i * 3 + 1], area.Vertices[i * 3 + 2]);

    /// <summary>The polygon's outline: triangle edges no other triangle shares.</summary>
    private static List<(int, int)> OpenEdges(RoadAreaProp area)
    {
        var count = new Dictionary<(int, int), int>();
        for (int k = 0; k + 2 < area.Indices.Length; k += 3)
            for (int m = 0; m < 3; m++)
            {
                int a = area.Indices[k + m], b = area.Indices[k + (m + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        return count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    /// Collision triangles for every kerbed sidewalk: the top, and the chamfered kerb and open
    /// ends from the road up to it. Goes into the bridge-deck body, which is two-sided.
    /// </summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var side in Sides(tile))
        {
            if (side.Kerb <= 0) continue;
            var (inner, outer, fwd) = (side.Inner, side.Outer, side.Forward);
            int n = inner.Length;
            var up = new Vector3(0, side.Kerb, 0);
            // the top's inner edge, Chamfer in from the kerb line; its ends Chamfer in where open
            var innerTop = new Vector3[n];
            var outerTop = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var across = outer[i] - inner[i];
                float width = across.Length();
                innerTop[i] = inner[i] + across / Math.Max(width, 1e-4f) * Math.Min(Chamfer, width * 0.5f) + up;
                outerTop[i] = outer[i] + up;
            }
            float length = 0;
            for (int i = 1; i < n; i++) length += inner[i].DistanceTo(inner[i - 1]);
            float pull = Math.Min(Chamfer, length * 0.25f);
            if (side.OpenStart) { innerTop[0] += fwd[0] * pull; outerTop[0] += fwd[0] * pull; }
            if (side.OpenEnd) { innerTop[n - 1] -= fwd[n - 1] * pull; outerTop[n - 1] -= fwd[n - 1] * pull; }

            for (int i = 0; i < n - 1; i++)
            {
                Tri(faces, innerTop[i], innerTop[i + 1], outerTop[i + 1], outerTop[i]);
                Tri(faces, inner[i], inner[i + 1], innerTop[i + 1], innerTop[i]);
            }
            if (side.OpenStart) Tri(faces, inner[0], outer[0], outerTop[0], innerTop[0]);
            if (side.OpenEnd) Tri(faces, inner[n - 1], outer[n - 1], outerTop[n - 1], innerTop[n - 1]);
        }

        // junction corners: the top and its edges, vertical (a corner patch is a few metres across)
        foreach (var area in tile.AreaProps)
        {
            if (area.Type != AreaPropType.Sidewalk || (area.Flags & PropFlags.Solid) == 0 || area.Height <= 0) continue;
            var up = new Vector3(0, area.Height, 0);
            for (int k = 0; k + 2 < area.Indices.Length; k += 3)
            {
                faces.Add(Vertex(area, area.Indices[k]) + up);
                faces.Add(Vertex(area, area.Indices[k + 1]) + up);
                faces.Add(Vertex(area, area.Indices[k + 2]) + up);
            }
            foreach (var (a, b) in OpenEdges(area))
                Tri(faces, Vertex(area, a), Vertex(area, b), Vertex(area, b) + up, Vertex(area, a) + up);
        }
        return faces.ToArray();
    }

    /// <summary>Every sidewalk side of the tile, with whether each end is open (no sidewalk carries on there).</summary>
    private static IEnumerable<Side> Sides(RoadTile tile)
    {
        // endpoint -> (segment, at its start) for the continuation test
        var ends = new Dictionary<(int, int), List<(int Seg, bool AtStart)>>();
        for (int s = 0; s < tile.Segments.Count; s++)
        {
            var seg = tile.Segments[s];
            if (!HasSidewalk(seg)) continue;
            foreach (bool atStart in (ReadOnlySpan<bool>)[true, false])
            {
                var key = Key(seg, atStart ? 0 : seg.PointCount - 1);
                if (!ends.TryGetValue(key, out var list)) ends[key] = list = new List<(int, bool)>();
                list.Add((s, atStart));
            }
        }

        for (int s = 0; s < tile.Segments.Count; s++)
        {
            var seg = tile.Segments[s];
            if (!HasSidewalk(seg)) continue;
            foreach (bool right in new[] { false, true })
            {
                var mine = right ? seg.Attributes.Right : seg.Attributes.Left;
                if (mine.SidewalkDm == 0) continue;
                yield return Build(seg, right, mine,
                    !Continues(tile, ends, s, atStart: true, right), !Continues(tile, ends, s, atStart: false, right));
            }
        }
    }

    /// <summary>Whether another segment meets this end and carries a sidewalk on the same side of the street.</summary>
    private static bool Continues(RoadTile tile, Dictionary<(int, int), List<(int Seg, bool AtStart)>> ends,
        int s, bool atStart, bool right)
    {
        var seg = tile.Segments[s];
        if (!ends.TryGetValue(Key(seg, atStart ? 0 : seg.PointCount - 1), out var list)) return false;
        foreach (var (other, otherAtStart) in list)
        {
            if (other == s) continue;
            var o = tile.Segments[other].Attributes;
            // end to start runs the same way: same side; end to end or start to start: the other side
            bool sameWay = atStart != otherAtStart;
            var theirs = sameWay == right ? o.Right : o.Left;
            if (theirs.SidewalkDm > 0) return true;
        }
        return false;
    }

    private static Side Build(RoadSegment seg, bool right, RoadSide side, bool openStart, bool openEnd)
    {
        int n = seg.PointCount;
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++) pts[i] = new Vector3(seg.Points[i * 3], seg.Points[i * 3 + 1], seg.Points[i * 3 + 2]);
        float half = seg.Width * 0.5f, width = side.SidewalkDm / 10f, sign = right ? 1f : -1f;
        var inner = new Vector3[n];
        var outer = new Vector3[n];
        var forward = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            // as RoadMeshBuilder.AppendSegment: bisector of the neighbours, no miter
            var f = i == 0 ? pts[1] - pts[0] : i == n - 1 ? pts[n - 1] - pts[n - 2] : pts[i + 1] - pts[i - 1];
            f.Y = 0;
            if (f.LengthSquared() < 1e-8f) f = Vector3.Forward;
            f = f.Normalized();
            var across = new Vector3(-f.Z, 0, f.X) * sign;
            inner[i] = pts[i] + across * half;
            outer[i] = pts[i] + across * (half + width);
            forward[i] = f;
        }
        var keep = Simplify(inner, outer);
        return new Side(keep.Select(k => inner[k]).ToArray(), keep.Select(k => outer[k]).ToArray(),
            keep.Select(k => forward[k]).ToArray(), side.KerbCm / 100f, openStart, openEnd);
    }

    /// <summary>A slab vertex may be dropped if both edges and the height stay this close to the chord.</summary>
    private const float PlanTolerance = 0.015f, HeightTolerance = 0.01f;

    /// <summary>
    /// The vertices worth keeping: the centrelines come from the network stage at 5 cm chords and
    /// draped every few metres, and a slab copying every one of them tripled a city tile's road
    /// triangles and made its kerb collision 24 times the bridges'. Greedy: a run is extended while
    /// every vertex it skips lies within <see cref="PlanTolerance"/> of both edge chords and
    /// <see cref="HeightTolerance"/> of the height chord. The kerb stays on the ribbon's edge to
    /// within the tolerance.
    /// </summary>
    private static List<int> Simplify(Vector3[] inner, Vector3[] outer)
    {
        int n = inner.Length;
        var keep = new List<int> { 0 };
        int from = 0;
        for (int to = 2; to < n; to++)
        {
            bool fits = true;
            for (int k = from + 1; k < to && fits; k++)
                fits = Near(inner[from], inner[to], inner[k]) && Near(outer[from], outer[to], outer[k]);
            if (fits) continue;
            keep.Add(to - 1);
            from = to - 1;
        }
        if (n > 1) keep.Add(n - 1);
        return keep;
    }

    /// <summary>Whether p lies within tolerance of the chord a-b: in plan, and in height at its position along it.</summary>
    private static bool Near(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = new Vector2(b.X - a.X, b.Z - a.Z);
        var ap = new Vector2(p.X - a.X, p.Z - a.Z);
        float l2 = ab.LengthSquared();
        float t = l2 < 1e-8f ? 0 : Math.Clamp(ap.Dot(ab) / l2, 0, 1);
        if ((ap - ab * t).Length() > PlanTolerance) return false;
        return Math.Abs(a.Y + (b.Y - a.Y) * t - p.Y) <= HeightTolerance;
    }

    /// <summary>Endpoint identity, quantised to a centimetre (as <c>RoadMeshBuilder.Key</c>).</summary>
    private static (int, int) Key(RoadSegment seg, int i) =>
        ((int)MathF.Round(seg.Points[i * 3] * 100f), (int)MathF.Round(seg.Points[i * 3 + 2] * 100f));

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
