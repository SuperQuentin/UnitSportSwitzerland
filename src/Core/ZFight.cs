using Godot;

namespace UnitSport.Core;

/// <summary>
/// Finds faces that fight for the depth buffer (#788): two triangles that face the same way, lie
/// within <see cref="MaxGap"/> of each other's plane and overlap by more than a sliver. A 24-bit
/// depth buffer resolves ~4 mm at 60 m, so such a pair flickers as the camera moves (an overlay
/// 2 mm proud of a body, two parts of a mesh in one plane, a roof drawn twice).
///
/// <para>
/// Pure geometry, no engine (tier-0 tests in <c>ZFightTests</c>). <c>ZFightScan</c> feeds it the
/// triangles of a node's meshes; <c>--zfightcheck</c>, the model viewer's <c>--zfight</c> and the
/// debug menu's overlay show what it finds (docs/notes/general/zfight.md).
/// </para>
/// </summary>
public static class ZFight
{
    /// <summary>Faces closer than this fight at range: overlays must stand ~1 cm proud (<c>meshscratch-boxes-render-inside-out</c>).</summary>
    public const float MaxGap = 0.008f;

    /// <summary>Overlaps smaller than this (1 cm²) are ignored: shared edges, slivers.</summary>
    public const float MinArea = 1e-4f;

    /// <summary>Two faces count as facing the same way within 10°.</summary>
    private const float Facing = 0.985f;

    /// <summary>A triangle whose box spans more cells than this is tested against everything instead.</summary>
    private const long MaxCells = 4096;

    /// <summary>
    /// One triangle as drawn. <see cref="Part"/> is the caller's label (the node it came from).
    /// Two faces with the same <see cref="Look"/> (material and colour) draw the same pixels, so
    /// they cannot flicker; the caller gives distinct looks to faces that differ. Faces in different
    /// <see cref="Depth"/> classes are pulled apart by their shader on purpose (a road over the
    /// ground, its paint over the road). <see cref="TwoSided"/>: drawn from both sides (no culling),
    /// so a face turned the other way fights too.
    /// </summary>
    public readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C, int Part = 0, int Look = 0, int Depth = 0, bool TwoSided = false);

    /// <summary>
    /// Two triangles (indices into the input) that fight: the area of the region where they lie
    /// within the gap, the gap at its centre, and the region itself (a convex polygon midway
    /// between the two planes, to draw).
    /// </summary>
    public sealed record Hit(int First, int Second, float Area, float Gap, Vector3[] Region)
    {
        public Vector3 At
        {
            get
            {
                var sum = Vector3.Zero;
                foreach (var p in Region) sum += p;
                return Region.Length == 0 ? sum : sum / Region.Length;
            }
        }
    }

    /// <summary>Every fighting pair, largest area first.</summary>
    public static List<Hit> Find(IReadOnlyList<Tri> tris, float maxGap = MaxGap, float minArea = MinArea)
    {
        var hits = new List<Hit>();
        int n = tris.Count;
        var normals = new Vector3[n];
        var lo = new Vector3[n];
        var hi = new Vector3[n];
        var ok = new bool[n];
        var pad = new Vector3(maxGap, maxGap, maxGap);
        double extent = 0;
        int live = 0;
        for (int i = 0; i < n; i++)
        {
            var t = tris[i];
            var cross = (t.B - t.A).Cross(t.C - t.A);
            float length = cross.Length();
            if (!(length > 1e-9f)) continue;   // degenerate (or NaN)
            normals[i] = cross / length;
            lo[i] = t.A.Min(t.B).Min(t.C) - pad;
            hi[i] = t.A.Max(t.B).Max(t.C) + pad;
            var size = hi[i] - lo[i];
            extent += Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
            ok[i] = true;
            live++;
        }
        if (live < 2) return hits;

        // a uniform grid about twice the mean triangle; a pair is tested in the one cell that holds
        // the low corner of the overlap of their boxes, so never twice
        float cell = Mathf.Clamp((float)(extent / live) * 2f, 0.05f, 200f);
        var grid = new Dictionary<(int, int, int), List<int>>();
        var big = new List<int>();
        var isBig = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!ok[i]) continue;
            var a = Cell(lo[i], cell);
            var b = Cell(hi[i], cell);
            long cells = (long)(b.X - a.X + 1) * (b.Y - a.Y + 1) * (b.Z - a.Z + 1);
            if (cells > MaxCells)
            {
                big.Add(i);
                isBig[i] = true;
                continue;
            }
            for (int x = a.X; x <= b.X; x++)
                for (int y = a.Y; y <= b.Y; y++)
                    for (int z = a.Z; z <= b.Z; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var list)) grid[(x, y, z)] = list = new List<int>();
                        list.Add(i);
                    }
        }

        foreach (var (key, list) in grid)
            for (int p = 0; p < list.Count; p++)
                for (int q = p + 1; q < list.Count; q++)
                {
                    int i = list[p], j = list[q];
                    if (!BoxesTouch(lo, hi, i, j)) continue;
                    var owner = Cell(lo[i].Max(lo[j]), cell);
                    if ((owner.X, owner.Y, owner.Z) != key) continue;
                    Test(tris, normals, i, j, maxGap, minArea, hits);
                }
        foreach (int i in big)
            for (int j = 0; j < n; j++)
                if (ok[j] && j != i && (!isBig[j] || j > i) && BoxesTouch(lo, hi, i, j))
                    Test(tris, normals, i, j, maxGap, minArea, hits);

        hits.Sort((x, y) => y.Area.CompareTo(x.Area));
        return hits;
    }

    /// <summary>The summed area of the hits, in cm².</summary>
    public static float AreaCm2(IEnumerable<Hit> hits)
    {
        float sum = 0;
        foreach (var h in hits) sum += h.Area;
        return sum * 1e4f;
    }

    private static Vector3I Cell(Vector3 p, float cell) =>
        new(Mathf.FloorToInt(p.X / cell), Mathf.FloorToInt(p.Y / cell), Mathf.FloorToInt(p.Z / cell));

    private static bool BoxesTouch(Vector3[] lo, Vector3[] hi, int i, int j) =>
        lo[i].X <= hi[j].X && lo[j].X <= hi[i].X &&
        lo[i].Y <= hi[j].Y && lo[j].Y <= hi[i].Y &&
        lo[i].Z <= hi[j].Z && lo[j].Z <= hi[i].Z;

    private static void Test(IReadOnlyList<Tri> tris, Vector3[] normals, int i, int j, float maxGap, float minArea, List<Hit> hits)
    {
        var ti = tris[i];
        var tj = tris[j];
        if (ti.Depth != tj.Depth || ti.Look == tj.Look) return;
        float dot = normals[i].Dot(normals[j]);
        if ((ti.TwoSided || tj.TwoSided ? Mathf.Abs(dot) : dot) < Facing) return;
        if (Overlap(ti, normals[i], tj, maxGap) is not { } region) return;
        if (region.Area < minArea) return;
        hits.Add(new Hit(i, j, region.Area, region.Gap, region.Points));
    }

    /// <summary>
    /// The region of <paramref name="tj"/> over <paramref name="ti"/> where the two planes are
    /// within the gap: <paramref name="tj"/> projected into <paramref name="ti"/>'s plane, clipped
    /// to <paramref name="ti"/> and to the strip where the height between the planes is within
    /// the gap (the height is affine over the plane, so each cut is a straight line).
    /// </summary>
    private static (float Area, float Gap, Vector3[] Points)? Overlap(in Tri ti, Vector3 n, in Tri tj, float maxGap)
    {
        var origin = ti.A;
        float hA = (tj.A - origin).Dot(n), hB = (tj.B - origin).Dot(n), hC = (tj.C - origin).Dot(n);
        if (Mathf.Min(hA, Mathf.Min(hB, hC)) > maxGap || Mathf.Max(hA, Mathf.Max(hB, hC)) < -maxGap) return null;

        var u = (ti.B - ti.A).Normalized();
        var v = n.Cross(u);
        Vector2 P(Vector3 p) => new((p - origin).Dot(u), (p - origin).Dot(v));

        Vector2 a0 = P(ti.A), a1 = P(ti.B), a2 = P(ti.C);
        Vector2 b0 = P(tj.A), b1 = P(tj.B), b2 = P(tj.C);

        // height of tj's plane over ti's: h(p) = hA + g . (p - b0)
        Vector2 e1 = b1 - b0, e2 = b2 - b0;
        float dh1 = hB - hA, dh2 = hC - hA;
        float det = e1.X * e2.Y - e1.Y * e2.X;
        if (Mathf.Abs(det) < 1e-12f) return null;
        var g = new Vector2((dh1 * e2.Y - dh2 * e1.Y) / det, (e1.X * dh2 - e2.X * dh1) / det);
        float Height(Vector2 p) => hA + g.Dot(p - b0);

        var poly = new List<Vector2>(8) { b0, b1, b2 };
        float side = Mathf.Sign((a1 - a0).Cross(a2 - a0));
        poly = Clip(poly, p => side * (a1 - a0).Cross(p - a0));
        poly = Clip(poly, p => side * (a2 - a1).Cross(p - a1));
        poly = Clip(poly, p => side * (a0 - a2).Cross(p - a2));
        poly = Clip(poly, p => maxGap - Height(p));
        poly = Clip(poly, p => maxGap + Height(p));
        if (poly.Count < 3) return null;

        float twice = 0;
        var centre = Vector2.Zero;
        for (int k = 0; k < poly.Count; k++)
        {
            twice += poly[k].Cross(poly[(k + 1) % poly.Count]);
            centre += poly[k];
        }
        centre /= poly.Count;
        var points = new Vector3[poly.Count];
        for (int k = 0; k < poly.Count; k++)
            points[k] = origin + u * poly[k].X + v * poly[k].Y + n * (Height(poly[k]) * 0.5f);
        return (Mathf.Abs(twice) * 0.5f, Mathf.Abs(Height(centre)), points);
    }

    /// <summary>Sutherland-Hodgman against one half-plane: keeps where <paramref name="inside"/> is >= 0 (an affine function, so the cut point is a straight interpolation).</summary>
    private static List<Vector2> Clip(List<Vector2> poly, Func<Vector2, float> inside)
    {
        if (poly.Count == 0) return poly;
        var result = new List<Vector2>(poly.Count + 2);
        for (int k = 0; k < poly.Count; k++)
        {
            var p = poly[k];
            var q = poly[(k + 1) % poly.Count];
            float fp = inside(p), fq = inside(q);
            if (fp >= 0) result.Add(p);
            if ((fp >= 0) != (fq >= 0)) result.Add(p + (q - p) * (fp / (fp - fq)));
        }
        return result;
    }
}
