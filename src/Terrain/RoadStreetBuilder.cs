using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Sidewalks (#119) and separated bike paths (#120): what the v3 <see cref="RoadSide"/> of a
/// segment holds beside its carriageway, as the network stage (<c>StreetPlanner</c>) wrote it,
/// drawn and given collision. A side is laid out by <see cref="RoadStreetSection"/>, shared with
/// the network stage: a profile of (distance from the ribbon's edge, height above the road)
/// points, its pieces coloured as kerb face, sidewalk, flush paving, grass or path. Its inner
/// edge is the ribbon's edge (offset exactly as <c>RoadMeshBuilder.AppendSegment</c> offsets it,
/// per-vertex bisector), its outer edge gets a skirt reaching under the ground the road blend
/// leaves there. Ends where the side stops get an end face; where the next piece carries it on,
/// nothing.
///
/// <para>
/// Collision is the profile's tops and kerbs, in the two-sided bridge-deck body, with every
/// vertical kerb <b>chamfered</b> 45° (<see cref="RoadStreetSection.Chamfered"/>) and so are the
/// open ends: a foot capsule of 0.32 m meets a vertical 12 cm step at 51°, at the edge of its 52°
/// <c>FloorMaxAngle</c>, and would stop dead at some kerbs; a 45° chamfer it walks up. Cars
/// (0.85 m) meet even a vertical one at 31°. The sloped kerbs beside a bike path are their own
/// ramp. Flush paving needs none: the heightfield under it is the road's. The ground under a
/// slab is the road blend's (raised to the side's outer height a lattice cell in from its edge,
/// <c>ComputeRoadBlend</c>), so walking off the outer edge carries on level.
/// </para>
/// </summary>
public static class RoadStreetBuilder
{
    private static readonly Color SidewalkColor = new Color(0.47f, 0.47f, 0.46f);   // asphalt, a shade lighter than the road
    private static readonly Color KerbColor = new Color(0.70f, 0.69f, 0.66f);       // granite kerbstones
    private static readonly Color PavingColor = new Color(0.56f, 0.53f, 0.48f);     // flush paving
    private static readonly Color PathColor = new Color(0.40f, 0.40f, 0.41f);       // bike path: asphalt, a shade darker than the sidewalk
    private static readonly Color GrassColor = new Color(0.33f, 0.42f, 0.22f);      // verge and buffer strips

    /// <summary>The skirt runs this far below the top: the visual ground sits 0.35 m under the road.</summary>
    private const float Skirt = 0.6f;

    /// <summary>The collision kerb rises over this much plan distance (45° for a 12 cm kerb).</summary>
    public const float Chamfer = 0.12f;

    public static bool HasSidewalk(RoadSegment s) =>
        s.PointCount >= 2 && RoadEmbankment.IsAtGrade(s)
        && (s.Attributes.Left.OuterDm > 0 || s.Attributes.Right.OuterDm > 0);

    /// <summary>
    /// One side of one segment: the ribbon's edge points, the unit vector out across the side at
    /// each, the forward direction, the profile, and whether each end is open.
    /// </summary>
    private readonly record struct Side(Vector3[] Edge, Vector3[] Across, Vector3[] Forward,
        RoadStreetSection.Profile Profile, bool OpenStart, bool OpenEnd)
    {
        /// <summary>The profile point <paramref name="k"/> of <paramref name="p"/> at vertex <paramref name="i"/>.</summary>
        public Vector3 At(RoadStreetSection.Profile p, int i, int k) => Edge[i] + Across[i] * p.D[k] + new Vector3(0, p.H[k], 0);

        /// <summary>The same point on the road surface (height 0 above the road).</summary>
        public Vector3 Base(RoadStreetSection.Profile p, int i, int k) => Edge[i] + Across[i] * p.D[k];

        public bool Raised => Profile.H.Any(h => h > 0);
    }

    private static Color ColorOf(StreetSurface s) => s switch
    {
        StreetSurface.Kerb => KerbColor,
        StreetSurface.Paving => PavingColor,
        StreetSurface.Track => PathColor,
        StreetSurface.Verge or StreetSurface.Buffer => GrassColor,
        _ => SidewalkColor,
    };

    /// <summary>Appends every sidewalk and bike path of the tile to a road mesh under construction.</summary>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var top = SidewalkColor.SrgbToLinear();
        var kerb = KerbColor.SrgbToLinear();
        var paving = PavingColor.SrgbToLinear();
        foreach (var side in Sides(tile))
        {
            var p = side.Profile;
            int n = side.Edge.Length, last = p.Count - 1;
            var drop = new Vector3(0, -Skirt, 0);
            for (int k = 0; k < last; k++)
            {
                var color = ColorOf(p.Surface[k]).SrgbToLinear();
                for (int i = 0; i < n - 1; i++)
                    Quad(vertices, colors, uvs, uv2s, indices, color, side.At(p, i, k), side.At(p, i + 1, k), side.At(p, i + 1, k + 1), side.At(p, i, k + 1));
            }
            for (int i = 0; i < n - 1; i++)
                Quad(vertices, colors, uvs, uv2s, indices, kerb, side.At(p, i, last), side.At(p, i + 1, last), side.At(p, i + 1, last) + drop, side.At(p, i, last) + drop);
            if (!side.Raised) continue;
            foreach (var (open, i) in (ReadOnlySpan<(bool, int)>)[(side.OpenStart, 0), (side.OpenEnd, n - 1)])
            {
                if (!open) continue;
                for (int k = 0; k < last; k++)
                    if (p.D[k + 1] - p.D[k] > 1e-4f)
                        Quad(vertices, colors, uvs, uv2s, indices, kerb, side.Base(p, i, k), side.At(p, i, k), side.At(p, i, k + 1), side.Base(p, i, k + 1));
            }
        }

        // junction corners (APRP), and a path's bands carried through a junction (#120): the top,
        // and a face down every open edge (kerb or outer, the same skirt)
        foreach (var area in tile.AreaProps)
        {
            if (!StreetAreas.Is(area.Type) || area.Vertices.Length < 9) continue;
            var up = new Vector3(0, area.Height, 0);
            var down = new Vector3(0, area.Height - Skirt, 0);
            var surface = area.Type == AreaPropType.BikePath ? PathColor.SrgbToLinear()
                : area.Type == AreaPropType.Grass ? GrassColor.SrgbToLinear() : area.Height > 0 ? top : paving;
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
    /// Collision triangles for every raised side: its chamfered profile, and the open ends from
    /// the road up to it, pulled in like the kerb. Goes into the bridge-deck body, which is two-sided.
    /// </summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var side in Sides(tile))
        {
            if (!side.Raised) continue;
            var p = RoadStreetSection.Chamfered(side.Profile);
            int n = side.Edge.Length, count = p.Count;
            var top = new Vector3[n, count];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < count; k++)
                    top[i, k] = side.At(p, i, k);
            float length = 0;
            for (int i = 1; i < n; i++) length += side.Edge[i].DistanceTo(side.Edge[i - 1]);
            float pull = Math.Min(Chamfer, length * 0.25f);
            for (int k = 0; k < count; k++)
            {
                if (p.H[k] <= 0) continue;
                if (side.OpenStart) top[0, k] += side.Forward[0] * pull;
                if (side.OpenEnd) top[n - 1, k] -= side.Forward[n - 1] * pull;
            }

            for (int k = 0; k + 1 < count; k++)
            {
                for (int i = 0; i < n - 1; i++)
                    Tri(faces, top[i, k], top[i + 1, k], top[i + 1, k + 1], top[i, k + 1]);
                if (p.D[k + 1] - p.D[k] < 1e-4f) continue;
                if (side.OpenStart) Tri(faces, side.Base(p, 0, k), side.Base(p, 0, k + 1), top[0, k + 1], top[0, k]);
                if (side.OpenEnd) Tri(faces, side.Base(p, n - 1, k), side.Base(p, n - 1, k + 1), top[n - 1, k + 1], top[n - 1, k]);
            }
        }

        // junction corners: the top and its edges, vertical (a corner patch is a few metres across)
        foreach (var area in tile.AreaProps)
        {
            if (!StreetAreas.Is(area.Type) || (area.Flags & PropFlags.Solid) == 0 || area.Height <= 0) continue;
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

    /// <summary>Every side of the tile with something beside its carriageway, with whether each end is open (nothing carries on there).</summary>
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
                if (RoadStreetSection.For(mine) is not { } profile) continue;
                yield return Build(seg, right, profile,
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
            if (theirs.OuterDm > 0) return true;
        }
        return false;
    }

    private static Side Build(RoadSegment seg, bool right, RoadStreetSection.Profile profile, bool openStart, bool openEnd)
    {
        int n = seg.PointCount;
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++) pts[i] = new Vector3(seg.Points[i * 3], seg.Points[i * 3 + 1], seg.Points[i * 3 + 2]);
        float half = seg.Width * 0.5f, width = profile.Width, sign = right ? 1f : -1f;
        var inner = new Vector3[n];
        var outer = new Vector3[n];
        var across = new Vector3[n];
        var forward = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            // as RoadMeshBuilder.AppendSegment: bisector of the neighbours, no miter
            var f = i == 0 ? pts[1] - pts[0] : i == n - 1 ? pts[n - 1] - pts[n - 2] : pts[i + 1] - pts[i - 1];
            f.Y = 0;
            if (f.LengthSquared() < 1e-8f) f = Vector3.Forward;
            f = f.Normalized();
            across[i] = new Vector3(-f.Z, 0, f.X) * sign;
            inner[i] = pts[i] + across[i] * half;
            outer[i] = pts[i] + across[i] * (half + width);
            forward[i] = f;
        }
        var keep = Simplify(inner, outer);
        return new Side(keep.Select(k => inner[k]).ToArray(), keep.Select(k => across[k]).ToArray(),
            keep.Select(k => forward[k]).ToArray(), profile, openStart, openEnd);
    }

    /// <summary>A slab vertex may be dropped if both edges and the height stay this close to the chord.</summary>
    private const float PlanTolerance = 0.015f, HeightTolerance = 0.01f;

    /// <summary>
    /// The vertices worth keeping: the centrelines come from the network stage at 5 cm chords and
    /// draped every few metres, and a slab copying every one of them tripled a city tile's road
    /// triangles and made its kerb collision 24 times the bridges'. Greedy: a run is extended while
    /// every vertex it skips lies within <see cref="PlanTolerance"/> of both edge chords and
    /// <see cref="HeightTolerance"/> of the height chord. The kerb stays on the ribbon's edge to
    /// within the tolerance, and every line between the two edges (a band of the profile) too: it
    /// is their blend.
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
