using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Roof overhangs for <see cref="Styles.MeshDetail.High"/> (#1067): every pitched roof plane's
/// free edges carried on past the walls in the roof's own plane, the eaves (the low edges) by
/// <see cref="Reach.Eave"/> and the gable verges (the edges climbing the slope) by
/// <see cref="Reach.Verge"/>. Pure geometry, any thread; <see cref="BuildingMeshBuilder"/> draws
/// the strips (a slab <see cref="Thickness"/> deep: top, soffit, fascia). Collision never sees
/// them: gameplay stays on the surveyed solid whatever the style.
///
/// <para>
/// A free edge is one no other roof triangle of the solid shares (a ridge, a hip or a valley is
/// shared and stays), facing down or across the slope (the top edge of a lean-to against a
/// taller wall faces up it and stays). One that would run into a neighbour's roof (a terraced
/// row's party wall) or under a higher roof of its own solid stays too: <see cref="RoofCover"/>.
/// Strips meet at a corner on the line their offset edges cross (a mitre), and an eave next to
/// a hip ends on the hip's line carried on, so a hipped roof's corners close.
/// </para>
/// </summary>
public static class RoofOverhangs
{
    /// <summary>How deep the overhang's slab is (vertical): the fascia board, and the soffit that far under the roof.</summary>
    public const float Thickness = 0.18f;

    /// <summary>How far past the wall (metres in the roof's plane) the eaves and the verges reach.</summary>
    public readonly record struct Reach(float Eave, float Verge);

    /// <summary>
    /// A kind's overhang: a barn's deep eaves over its doors and stacked hay, a house's 70 cm, a
    /// block's or a works' shallow ones, a church's stone walls barely sheltered.
    /// </summary>
    public static Reach For(BuildingKind kind) => kind switch
    {
        BuildingKind.Agricultural => new(1.1f, 0.6f),
        BuildingKind.House => new(0.7f, 0.4f),
        BuildingKind.Apartment => new(0.5f, 0.3f),
        BuildingKind.Sacral => new(0.4f, 0.25f),
        BuildingKind.Industrial or BuildingKind.Commercial => new(0.4f, 0.25f),
        BuildingKind.Garage or BuildingKind.Annex => new(0.35f, 0.25f),
        _ => new(0.5f, 0.3f),
    };

    /// <summary>
    /// One overhang strip: the roof edge (<see cref="P"/>, <see cref="Q"/>) and where it ends
    /// past it (<see cref="OuterP"/>, <see cref="OuterQ"/>), all in the roof plane; the plane's
    /// upward <see cref="Normal"/>, its unit <see cref="UpSlope"/> and its <see cref="Plane"/> key
    /// (as <see cref="PitchedRoof"/> gives it), the horizontal way <see cref="Out"/> of the
    /// building, and whether each end needs closing (its neighbour edge has no strip).
    /// </summary>
    public readonly record struct Strip(Vector3 P, Vector3 Q, Vector3 OuterP, Vector3 OuterQ, Vector3 Normal,
        Vector3 UpSlope, Vector3 Out, (int, int, int, int) Plane, bool CapP, bool CapQ);

    /// <summary>
    /// A roof triangle that slopes (flat roofs, |normal.y| above 0.97, have no eave to measure
    /// from): its upward unit normal, its unit up-slope vector and its plane's key, the normal and
    /// the plane's offset rounded, so the triangles of one roof face group together. The source
    /// TINs wind faces either way, so the normal is turned to face up.
    /// </summary>
    public static bool PitchedRoof(Vector3 a, Vector3 c, Vector3 d, out Vector3 normal, out Vector3 upSlope, out (int, int, int, int) key)
    {
        var n = (c - a).Cross(d - a);
        normal = upSlope = Vector3.Zero;
        key = default;
        float len = n.Length();
        if (len < 1e-6f) return false;
        n /= len;
        if (n.Y < 0f) n = -n;
        if (n.Y > 0.97f) return false;
        normal = n;
        upSlope = (Vector3.Up - n * n.Y).Normalized();
        key = ((int)MathF.Round(n.X * 50f), (int)MathF.Round(n.Y * 50f), (int)MathF.Round(n.Z * 50f),
            (int)MathF.Round(n.Dot(a) * 10f));
        return true;
    }

    // roof by slope alone: the solids that get overhangs have no spire
    private static bool IsRoof(Vector3 a, Vector3 c, Vector3 d)
    {
        var n = (c - a).Cross(d - a);
        float len = n.Length();
        return len > 1e-6f && Mathf.Abs(n.Y / len) >= BuildingTriangles.RoofNormalY;
    }

    private static (int, int, int) Key(Vector3 p) =>
        ((int)MathF.Round(p.X * 100f), (int)MathF.Round(p.Y * 100f), (int)MathF.Round(p.Z * 100f));

    private static ((int, int, int), (int, int, int)) EdgeKey(Vector3 p, Vector3 q)
    {
        var a = Key(p);
        var b = Key(q);
        return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }

    // a plane's boundary edge: its outward way in the plane, its unit direction, its reach (0: no strip)
    private struct Edge
    {
        public Vector3 P, Q, Dir, Out;
        public float Depth;
        public bool Free;      // no other roof triangle of the solid shares it
    }

    /// <summary>
    /// The overhang strips of building <paramref name="index"/> (whose solid is <paramref name="b"/>),
    /// reaching <paramref name="reach"/>; <paramref name="cover"/>, when given, keeps the edges whose
    /// strip would meet another roof.
    /// </summary>
    public static List<Strip> Plan(Building b, int index, Reach reach, RoofCover? cover)
    {
        var strips = new List<Strip>();
        // how many roof triangles of the solid share each edge, and its pitched planes
        var shared = new Dictionary<((int, int, int), (int, int, int)), int>();
        var planes = new Dictionary<(int, int, int, int), List<int>>();
        var normals = new Dictionary<(int, int, int, int), (Vector3 N, Vector3 Up)>();
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, c, d) = b.Tri(t);
            if (!IsRoof(a, c, d)) continue;
            Count(a, c); Count(c, d); Count(d, a);
            if (!PitchedRoof(a, c, d, out var n, out var up, out var key)) continue;
            if (!planes.TryGetValue(key, out var list)) planes[key] = list = new List<int>();
            list.Add(t);
            normals.TryAdd(key, (n, up));
        }
        void Count(Vector3 p, Vector3 q)
        {
            var k = EdgeKey(p, q);
            shared[k] = shared.TryGetValue(k, out int n) ? n + 1 : 1;
        }

        var inPlane = new Dictionary<((int, int, int), (int, int, int)), (int Count, Vector3 P, Vector3 Q, Vector3 Third)>();
        var edges = new List<Edge>();
        var ends = new Dictionary<(int, int, int), List<int>>(); // vertex -> the plane's boundary edges there
        foreach (var (key, tris) in planes)
        {
            var (n, up) = normals[key];
            // the plane's own edges: those of one triangle only are its boundary
            inPlane.Clear();
            float s0 = float.MaxValue, s1 = float.MinValue;
            foreach (int t in tris)
            {
                var (a, c, d) = b.Tri(t);
                Add(a, c, d); Add(c, d, a); Add(d, a, c);
                s0 = Mathf.Min(s0, Mathf.Min(a.Dot(up), Mathf.Min(c.Dot(up), d.Dot(up))));
                s1 = Mathf.Max(s1, Mathf.Max(a.Dot(up), Mathf.Max(c.Dot(up), d.Dot(up))));
            }
            void Add(Vector3 p, Vector3 q, Vector3 third)
            {
                var k = EdgeKey(p, q);
                inPlane[k] = inPlane.TryGetValue(k, out var e) ? (e.Count + 1, e.P, e.Q, e.Third) : (1, p, q, third);
            }
            // a small roof's overhang no more than half its slope
            float span = s1 - s0;
            edges.Clear();
            ends.Clear();
            foreach (var (k, (count, p, q, third)) in inPlane)
            {
                if (count != 1) continue;
                var dir = q - p;
                float len = dir.Length();
                if (len < 1e-4f) continue;
                dir /= len;
                var o = n.Cross(dir).Normalized();
                if (o.Dot(third - p) > 0f) o = -o;
                bool free = shared[k] == 1;
                float depth = 0f;
                // down the slope (an eave) or across it (a verge); never up it (a ridge, a lean-to's head)
                if (free && len >= 0.05f && o.Dot(up) < 0.3f)
                {
                    float down = Mathf.Clamp(-o.Dot(up), 0f, 1f);
                    depth = Mathf.Min(Mathf.Lerp(reach.Verge, reach.Eave, down), 0.5f * span);
                    if (cover != null && Blocked(cover, index, key, p, q, o, depth)) depth = 0f;
                }
                int i = edges.Count;
                edges.Add(new Edge { P = p, Q = q, Dir = dir, Out = o, Depth = depth, Free = free });
                Link(Key(p), i);
                Link(Key(q), i);
            }
            void Link((int, int, int) v, int i)
            {
                if (!ends.TryGetValue(v, out var l)) ends[v] = l = new List<int>(2);
                l.Add(i);
            }

            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                if (e.Depth <= 0f) continue;
                var (outerP, capP) = Corner(edges, ends, i, e.P);
                var (outerQ, capQ) = Corner(edges, ends, i, e.Q);
                var horizontal = new Vector3(e.Out.X, 0f, e.Out.Z);
                horizontal = horizontal.LengthSquared() > 1e-8f ? horizontal.Normalized() : e.Out;
                strips.Add(new Strip(e.P, e.Q, outerP, outerQ, n, up, horizontal, key, capP, capQ));
            }
        }
        return strips;
    }

    /// <summary>
    /// Where edge <paramref name="i"/>'s strip ends at its vertex <paramref name="p"/>: on the line
    /// where its offset edge crosses the plane's other edge there (that one's offset, or the edge
    /// itself carried on when it has no strip), and whether the end needs closing (the other edge
    /// is a free one without a strip). Anything else (no other edge, or several, or one in line)
    /// ends square.
    /// </summary>
    private static (Vector3 Outer, bool Cap) Corner(List<Edge> edges, Dictionary<(int, int, int), List<int>> ends, int i, Vector3 p)
    {
        var e = edges[i];
        var square = p + e.Out * e.Depth;
        if (!ends.TryGetValue(Key(p), out var list) || list.Count != 2) return (square, false);
        var f = edges[list[0] == i ? list[1] : list[0]];
        float cross = e.Dir.Dot(f.Out);
        if (Mathf.Abs(cross) < 0.2f) return (square, false);
        // X = p + e.Out * e.Depth + e.Dir * beta, with (X - p) . f.Out = f.Depth
        float beta = (f.Depth - e.Depth * e.Out.Dot(f.Out)) / cross;
        if (Mathf.Abs(beta) > 3f * Mathf.Max(Mathf.Max(e.Depth, f.Depth), 0.1f)) return (square, false);
        return (square + e.Dir * beta, f.Depth <= 0f && f.Free);
    }

    // whether a strip along p-q, out by depth, would run into another roof at its height or above
    private static bool Blocked(RoofCover cover, int index, (int, int, int, int) plane, Vector3 p, Vector3 q, Vector3 o, float depth)
    {
        for (int s = 0; s < 3; s++)
        {
            var m = p.Lerp(q, 0.15f + 0.35f * s) + o * (depth * 0.5f);
            if (cover.HeightAt(m.X, m.Z, index, plane) >= m.Y - 0.3f) return true;
        }
        return false;
    }
}

/// <summary>
/// A tile's roofs seen from above (every roof-facing triangle of the drawn solids, in 4 m cells),
/// for <see cref="RoofOverhangs"/>: how high the roofs over a plan point are, leaving out one
/// building's one roof plane (the strip's own).
/// </summary>
public sealed class RoofCover
{
    private const float Cell = 4f;
    private readonly List<(Vector3 A, Vector3 B, Vector3 C, int Building, (int, int, int, int) Plane)> _tris = new();
    private readonly Dictionary<(int, int), List<int>> _cells = new();

    public RoofCover(BuildingTile tile, Func<int, bool> drawn)
    {
        for (int bi = 0; bi < tile.Buildings.Count; bi++)
        {
            if (!drawn(bi)) continue;
            var b = tile.Buildings[bi];
            for (int t = 0; t < b.TriangleCount; t++)
            {
                var (a, c, d) = b.Tri(t);
                var n = (c - a).Cross(d - a);
                float len = n.Length();
                if (len < 1e-6f || Mathf.Abs(n.Y / len) < BuildingTriangles.RoofNormalY) continue;
                RoofOverhangs.PitchedRoof(a, c, d, out _, out _, out var key);
                int i = _tris.Count;
                _tris.Add((a, c, d, bi, key));
                int x0 = (int)MathF.Floor(Mathf.Min(a.X, Mathf.Min(c.X, d.X)) / Cell);
                int x1 = (int)MathF.Floor(Mathf.Max(a.X, Mathf.Max(c.X, d.X)) / Cell);
                int z0 = (int)MathF.Floor(Mathf.Min(a.Z, Mathf.Min(c.Z, d.Z)) / Cell);
                int z1 = (int)MathF.Floor(Mathf.Max(a.Z, Mathf.Max(c.Z, d.Z)) / Cell);
                for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!_cells.TryGetValue((x, z), out var list)) _cells[(x, z)] = list = new List<int>(8);
                    list.Add(i);
                }
            }
        }
    }

    /// <summary>
    /// The highest roof over plan point (x, z), leaving out <paramref name="building"/>'s plane
    /// <paramref name="plane"/>; <see cref="float.MinValue"/> when none is.
    /// </summary>
    public float HeightAt(float x, float z, int building, (int, int, int, int) plane)
    {
        float best = float.MinValue;
        if (!_cells.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var list)) return best;
        foreach (int i in list)
        {
            var (a, b, c, owner, key) = _tris[i];
            if (owner == building && key == plane) continue;
            float area = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-6f) continue;
            float w1 = ((x - a.X) * (c.Z - a.Z) - (z - a.Z) * (c.X - a.X)) / area;
            float w2 = ((b.X - a.X) * (z - a.Z) - (b.Z - a.Z) * (x - a.X)) / area;
            float w0 = 1f - w1 - w2;
            if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
            best = Mathf.Max(best, a.Y * w0 + b.Y * w1 + c.Y * w2);
        }
        return best;
    }
}
