using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// What of an imported rig's scene objects collides, and how (#974): pure, so the xunit tests run it.
/// Each piece is judged by its oriented box (its mesh's box through its transform):
/// <list type="bullet">
/// <item>nothing for a piece under <see cref="TinyBelow"/> across (a clamp, a lamp, a bolt) or one thinner
/// than <see cref="LineBelow"/> on two axes (a cable, a pipe, a rod): nobody stands on them and a beam
/// passes them; nor for one lying on the floor under <see cref="FlatUnder"/> (a focus point's disc, a
/// floor plate), walked over without a lip to catch on;</item>
/// <item>a convex hull for a low walkable piece (both sides of its footprint at least
/// <see cref="WalkableSide"/>, <see cref="LowUnder"/> tall at most, standing below <see cref="ReachUnder"/>):
/// a deck is its block and a flight of stairs its ramp, as the generated stage's stairs are ramps (the
/// player has no step-up; <c>FootPlayer.MantleMin</c>);</item>
/// <item>the piece's own triangles for a tall walkable one (a set with an arch or an opening a hull
/// would fill);</item>
/// <item>its oriented box for the rest: a truss, a screen, a tower, a wall, a piece overhead.</item>
/// </list>
/// The caps keep physics cheap whatever the file: the biggest pieces are judged first, and a piece past
/// a cap falls back to its box, a box past its cap to nothing.
/// </summary>
public static class MvrCollision
{
    public enum Kind : byte { None, Box, Hull, Mesh }

    /// <summary>Under this across (m), no collision; thinner than <see cref="LineBelow"/> on two axes, none.</summary>
    public const float TinyBelow = 0.3f, LineBelow = 0.08f;
    /// <summary>A walkable piece: both footprint sides from this (m), its thinnest side from <see cref="SolidFrom"/>.</summary>
    public const float WalkableSide = 0.8f, SolidFrom = 0.12f;
    /// <summary>A walkable piece this tall at most is its hull (a mantle's reach), and stands below <see cref="ReachUnder"/>.</summary>
    public const float LowUnder = 2.1f, ReachUnder = 3f;
    /// <summary>A box is never thinner than this (m): a screen's plane still stops a walker.</summary>
    public const float MinBox = 0.1f;
    /// <summary>A piece blocks the crowd when it stands below this (m) and rises over <see cref="FlatUnder"/> (a floor plate is stood on).</summary>
    public const float BlocksUnder = 2.2f, FlatUnder = 0.15f;

    /// <summary>
    /// Caps: boxes, hulls, triangles of one mesh piece and of all. Capture's demo show (2251 pieces,
    /// 100918 triangles) needs under a thousand boxes, a few dozen hulls and a few thousand triangles.
    /// </summary>
    public const int MaxBoxes = 1500, MaxHulls = 300, MaxPieceTriangles = 4000, MaxTriangles = 30_000;

    /// <summary>A piece's oriented box in the rig's frame (centre, unit axes, full sizes), its bounds, and how it collides.</summary>
    public readonly record struct Piece(int Object, Kind Kind, Vector3 Centre, Basis Axes, Vector3 Size, Aabb Bounds);

    /// <summary>The directions a hull is sampled along (the piece's extremes): every (i, j, k) in −2..2, deduplicated.</summary>
    private static readonly Vector3[] Directions = MakeDirections();

    private static Vector3[] MakeDirections()
    {
        var list = new List<Vector3>();
        for (int i = -2; i <= 2; i++)
            for (int j = -2; j <= 2; j++)
                for (int k = -2; k <= 2; k++)
                {
                    if (i == 0 && j == 0 && k == 0) continue;
                    var d = new Vector3(i, j, k).Normalized();
                    if (!list.Any(o => o.DistanceSquaredTo(d) < 1e-6f)) list.Add(d);
                }
        return list.ToArray();
    }

    /// <summary>Every object of <paramref name="rig"/> judged, in object order.</summary>
    public static Piece[] Plan(RigData rig)
    {
        var pieces = new Piece[rig.Objects.Count];
        for (int i = 0; i < pieces.Length; i++) pieces[i] = Judge(rig, i);
        // the caps: the biggest first, the rest fall back
        var order = Enumerable.Range(0, pieces.Length).OrderByDescending(i => pieces[i].Bounds.Volume).ToArray();
        int boxes = 0, hulls = 0, triangles = 0;
        foreach (int i in order)
        {
            var p = pieces[i];
            var kind = p.Kind;
            if (kind == Kind.Mesh)
            {
                int t = rig.Meshes[rig.Objects[i].Mesh].TriangleCount;
                if (triangles + t <= MaxTriangles) triangles += t;
                else kind = Kind.Box;
            }
            if (kind == Kind.Hull && ++hulls > MaxHulls) kind = Kind.Box;
            if (kind == Kind.Box && ++boxes > MaxBoxes) kind = Kind.None;
            pieces[i] = p with { Kind = kind };
        }
        return pieces;
    }

    private static Piece Judge(RigData rig, int index)
    {
        var o = rig.Objects[index];
        var none = new Piece(index, Kind.None, Vector3.Zero, Basis.Identity, Vector3.Zero, default);
        if (o.Mesh < 0 || o.Mesh >= rig.Meshes.Count) return none;
        var m = rig.Meshes[o.Mesh];
        var xf = RigData.X(o.Xf);
        var lo = RigData.V(m.Min);
        var size = RigData.V(m.Size);
        Vector3 c0 = xf.Basis.Column0, c1 = xf.Basis.Column1, c2 = xf.Basis.Column2;
        float l0 = c0.Length(), l1 = c1.Length(), l2 = c2.Length();
        if (l0 < 1e-6f || l1 < 1e-6f || l2 < 1e-6f || m.TriangleCount == 0) return none;
        var ext = new Vector3(l0 * size.X, l1 * size.Y, l2 * size.Z);
        var bounds = new Aabb(xf * lo, Vector3.Zero);
        for (int k = 1; k < 8; k++) bounds = bounds.Expand(xf * (lo + new Vector3((k & 1) * size.X, (k >> 1 & 1) * size.Y, (k >> 2 & 1) * size.Z)));
        var piece = new Piece(index, Kind.Box, xf * (lo + size / 2), new Basis(c0 / l0, c1 / l1, c2 / l2).Orthonormalized(),
            ext.Max(new Vector3(MinBox, MinBox, MinBox)), bounds);

        float big = Math.Max(ext.X, Math.Max(ext.Y, ext.Z)), small = Math.Min(ext.X, Math.Min(ext.Y, ext.Z));
        float mid = ext.X + ext.Y + ext.Z - big - small;
        // tiny, a line, or lying flat on the floor (a marker, a plate): walked over, as the floor is
        if (big < TinyBelow || mid < LineBelow || bounds.End.Y < FlatUnder) return piece with { Kind = Kind.None };
        bool walkable = bounds.Size.X >= WalkableSide && bounds.Size.Z >= WalkableSide && small >= SolidFrom && bounds.Position.Y < ReachUnder;
        if (!walkable) return piece;
        if (bounds.Size.Y <= LowUnder) return piece with { Kind = Kind.Hull };
        return m.TriangleCount <= MaxPieceTriangles ? piece with { Kind = Kind.Mesh } : piece;
    }

    /// <summary>
    /// A piece's hull points (rig frame): its vertices' extremes along <see cref="Directions"/>, at most
    /// 98, inside the true hull and touching it, so a physics engine's own hull limit is never reached.
    /// </summary>
    public static Vector3[] Hull(Vector3[] positions, Transform3D xf)
    {
        if (positions.Length == 0) return Array.Empty<Vector3>();
        Span<float> best = stackalloc float[Directions.Length];
        Span<int> at = stackalloc int[Directions.Length];
        best.Fill(float.NegativeInfinity);
        for (int v = 0; v < positions.Length; v++)
        {
            var p = xf * positions[v];
            for (int d = 0; d < Directions.Length; d++)
            {
                float s = p.Dot(Directions[d]);
                if (s > best[d]) { best[d] = s; at[d] = v; }
            }
        }
        var picked = new HashSet<int>();
        foreach (int v in at) picked.Add(v);
        return picked.Select(v => xf * positions[v]).ToArray();
    }

    /// <summary>The <see cref="Kind.Mesh"/> pieces' triangles in the rig's frame, three corners each, for one concave shape.</summary>
    public static Vector3[] Faces(RigData rig, IReadOnlyList<Piece> plan, Vector3[][] positions, int[][] indices)
    {
        int n = 0;
        foreach (var p in plan)
            if (p.Kind == Kind.Mesh) n += indices[rig.Objects[p.Object].Mesh].Length;
        var faces = new Vector3[n];
        int f = 0;
        foreach (var p in plan)
        {
            if (p.Kind != Kind.Mesh) continue;
            var o = rig.Objects[p.Object];
            var xf = RigData.X(o.Xf);
            var pos = positions[o.Mesh];
            foreach (int i in indices[o.Mesh]) faces[f++] = xf * pos[i];
        }
        return faces;
    }

    /// <summary>
    /// Where the crowd may not stand (rig frame, x and z): the bounds of every colliding piece that stands
    /// on the floor (below <see cref="BlocksUnder"/>) and rises over a floor plate's <see cref="FlatUnder"/>.
    /// A piece overhead is walked under; a plate is stood on.
    /// </summary>
    public static List<Rect2> Footprints(IReadOnlyList<Piece> plan)
    {
        var list = new List<Rect2>();
        foreach (var p in plan)
            if (p.Kind != Kind.None && p.Bounds.Position.Y < BlocksUnder && p.Bounds.End.Y > FlatUnder)
                list.Add(new Rect2(p.Bounds.Position.X, p.Bounds.Position.Z, p.Bounds.Size.X, p.Bounds.Size.Z));
        return list;
    }

    /// <summary>Whether a point (x, z) is in any footprint, give or take <paramref name="margin"/>.</summary>
    public static bool Blocks(List<Rect2> footprints, Vector2 p, float margin)
    {
        foreach (var r in footprints)
            if (p.X > r.Position.X - margin && p.X < r.End.X + margin && p.Y > r.Position.Y - margin && p.Y < r.End.Y + margin)
                return true;
        return false;
    }
}
