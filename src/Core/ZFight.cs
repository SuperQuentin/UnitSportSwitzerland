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
    /// so a face turned the other way fights too. <see cref="Opaque"/>: hides what is behind it
    /// (glass does not), for <c>visibleOnly</c>.
    /// </summary>
    public readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C, int Part = 0, int Look = 0, int Depth = 0, bool TwoSided = false,
        bool Opaque = true);

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

    /// <summary>
    /// Every fighting pair, largest area first. With <paramref name="visibleOnly"/>, only regions
    /// that can be seen from outside: a few rays from the region out along its normal must not all
    /// hit an opaque face (a floor under another floor, faces inside a closed body never flicker).
    /// </summary>
    public static List<Hit> Find(IReadOnlyList<Tri> tris, float maxGap = MaxGap, float minArea = MinArea, bool visibleOnly = false)
    {
        var hits = new List<Hit>();
        var index = new Index(tris, maxGap);
        if (index.Live < 2) return hits;
        index.Pairs((i, j) => Test(tris, index.Normals, i, j, maxGap, minArea, hits));
        if (visibleOnly) hits.RemoveAll(h => !index.Seen(h, maxGap));
        hits.Sort((x, y) => y.Area.CompareTo(x.Area));
        return hits;
    }

    /// <summary>
    /// The triangles in a uniform grid about twice the mean triangle; a triangle whose box spans more
    /// than <see cref="MaxCells"/> cells (the ground) is kept aside and tested against everything.
    /// </summary>
    private sealed class Index
    {
        private readonly IReadOnlyList<Tri> _tris;
        public readonly Vector3[] Normals;
        private readonly Vector3[] _lo, _hi;
        private readonly bool[] _ok, _big;
        private readonly List<int> _bigList = new();
        private readonly Dictionary<(int, int, int), List<int>> _grid = new();
        private readonly float _cell;
        private Vector3I _cellLo = new(int.MaxValue, int.MaxValue, int.MaxValue), _cellHi = new(int.MinValue, int.MinValue, int.MinValue);
        public readonly int Live;

        public Index(IReadOnlyList<Tri> tris, float maxGap)
        {
            _tris = tris;
            int n = tris.Count;
            Normals = new Vector3[n];
            _lo = new Vector3[n];
            _hi = new Vector3[n];
            _ok = new bool[n];
            _big = new bool[n];
            var pad = new Vector3(maxGap, maxGap, maxGap);
            double extent = 0;
            for (int i = 0; i < n; i++)
            {
                var t = tris[i];
                var cross = (t.B - t.A).Cross(t.C - t.A);
                float length = cross.Length();
                if (!(length > 1e-9f)) continue;   // degenerate (or NaN)
                Normals[i] = cross / length;
                _lo[i] = t.A.Min(t.B).Min(t.C) - pad;
                _hi[i] = t.A.Max(t.B).Max(t.C) + pad;
                var size = _hi[i] - _lo[i];
                extent += Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
                _ok[i] = true;
                Live++;
            }
            if (Live < 2) return;
            _cell = Mathf.Clamp((float)(extent / Live) * 2f, 0.05f, 200f);
            for (int i = 0; i < n; i++)
            {
                if (!_ok[i]) continue;
                var a = Cell(_lo[i]);
                var b = Cell(_hi[i]);
                long cells = (long)(b.X - a.X + 1) * (b.Y - a.Y + 1) * (b.Z - a.Z + 1);
                if (cells > MaxCells)
                {
                    _bigList.Add(i);
                    _big[i] = true;
                    continue;
                }
                _cellLo = _cellLo.Min(a);
                _cellHi = _cellHi.Max(b);
                for (int x = a.X; x <= b.X; x++)
                    for (int y = a.Y; y <= b.Y; y++)
                        for (int z = a.Z; z <= b.Z; z++)
                        {
                            if (!_grid.TryGetValue((x, y, z), out var list)) _grid[(x, y, z)] = list = new List<int>();
                            list.Add(i);
                        }
            }
        }

        private Vector3I Cell(Vector3 p) =>
            new(Mathf.FloorToInt(p.X / _cell), Mathf.FloorToInt(p.Y / _cell), Mathf.FloorToInt(p.Z / _cell));

        private bool Touch(int i, int j) =>
            _lo[i].X <= _hi[j].X && _lo[j].X <= _hi[i].X &&
            _lo[i].Y <= _hi[j].Y && _lo[j].Y <= _hi[i].Y &&
            _lo[i].Z <= _hi[j].Z && _lo[j].Z <= _hi[i].Z;

        /// <summary>Every pair whose boxes touch, once: in the cell holding the low corner of their overlap.</summary>
        public void Pairs(Action<int, int> test)
        {
            foreach (var (key, list) in _grid)
                for (int p = 0; p < list.Count; p++)
                    for (int q = p + 1; q < list.Count; q++)
                    {
                        int i = list[p], j = list[q];
                        if (!Touch(i, j)) continue;
                        var owner = Cell(_lo[i].Max(_lo[j]));
                        if ((owner.X, owner.Y, owner.Z) != key) continue;
                        test(i, j);
                    }
            foreach (int i in _bigList)
                for (int j = 0; j < _ok.Length; j++)
                    if (_ok[j] && j != i && (!_big[j] || j > i) && Touch(i, j))
                        test(i, j);
        }

        /// <summary>
        /// Rays from just over the region's centre (both sides for a two-sided pair), straight out and
        /// tilted 50 degrees four ways: seen if any leaves the scene without meeting an opaque face.
        /// </summary>
        public bool Seen(Hit hit, float maxGap)
        {
            var n = Normals[hit.First];
            var u = (_tris[hit.First].B - _tris[hit.First].A).Normalized();
            var v = n.Cross(u);
            bool twoSided = _tris[hit.First].TwoSided || _tris[hit.Second].TwoSided;
            var centre = hit.At;
            float tilt = Mathf.Tan(Mathf.DegToRad(50f));
            // Godot's front face is clockwise from the front, so (B - A) x (C - A) points in: out is minus it
            for (int side = -1; side <= (twoSided ? 1 : -1); side += 2)
            {
                var up = n * side;
                var origin = centre + up * (maxGap * 1.5f);
                if (Escapes(origin, up, hit) ||
                    Escapes(origin, (up + u * tilt).Normalized(), hit) ||
                    Escapes(origin, (up - u * tilt).Normalized(), hit) ||
                    Escapes(origin, (up + v * tilt).Normalized(), hit) ||
                    Escapes(origin, (up - v * tilt).Normalized(), hit))
                    return true;
            }
            return false;
        }

        private bool Escapes(Vector3 origin, Vector3 dir, Hit hit)
        {
            foreach (int i in _bigList)
                if (Blocks(i, origin, dir, hit)) return false;
            // Amanatides-Woo through the grid until the ray leaves the cells in use
            var c = Cell(origin);
            int sx = dir.X > 0 ? 1 : -1, sy = dir.Y > 0 ? 1 : -1, sz = dir.Z > 0 ? 1 : -1;
            float tx = Next(origin.X, dir.X, c.X, sx), ty = Next(origin.Y, dir.Y, c.Y, sy), tz = Next(origin.Z, dir.Z, c.Z, sz);
            float dx = dir.X == 0 ? float.PositiveInfinity : _cell / Mathf.Abs(dir.X);
            float dy = dir.Y == 0 ? float.PositiveInfinity : _cell / Mathf.Abs(dir.Y);
            float dz = dir.Z == 0 ? float.PositiveInfinity : _cell / Mathf.Abs(dir.Z);
            for (int guard = 0; guard < 100_000; guard++)
            {
                if (_grid.TryGetValue((c.X, c.Y, c.Z), out var list))
                    foreach (int i in list)
                        if (Blocks(i, origin, dir, hit)) return false;
                if (tx <= ty && tx <= tz) { c.X += sx; tx += dx; }
                else if (ty <= tz) { c.Y += sy; ty += dy; }
                else { c.Z += sz; tz += dz; }
                if ((sx > 0 ? c.X > _cellHi.X : c.X < _cellLo.X) ||
                    (sy > 0 ? c.Y > _cellHi.Y : c.Y < _cellLo.Y) ||
                    (sz > 0 ? c.Z > _cellHi.Z : c.Z < _cellLo.Z))
                    return true;
            }
            return true;
        }

        /// <summary>The ray's distance to the next cell wall on one axis.</summary>
        private float Next(float p, float d, int ci, int s) =>
            d == 0 ? float.PositiveInfinity : ((ci + (s > 0 ? 1 : 0)) * _cell - p) / d;

        /// <summary>Moller-Trumbore, either side: an opaque face other than the pair, ahead of the ray.</summary>
        private bool Blocks(int i, Vector3 origin, Vector3 dir, Hit hit)
        {
            if (i == hit.First || i == hit.Second || !_ok[i]) return false;
            var t = _tris[i];
            if (!t.Opaque) return false;
            var e1 = t.B - t.A;
            var e2 = t.C - t.A;
            var p = dir.Cross(e2);
            float det = e1.Dot(p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            var s = origin - t.A;
            float a = s.Dot(p) * inv;
            if (a < 0 || a > 1) return false;
            var q = s.Cross(e1);
            float b = dir.Dot(q) * inv;
            if (b < 0 || a + b > 1) return false;
            return e2.Dot(q) * inv > 1e-5f;
        }
    }

    /// <summary>The summed area of the hits, in cm².</summary>
    public static float AreaCm2(IEnumerable<Hit> hits)
    {
        float sum = 0;
        foreach (var h in hits) sum += h.Area;
        return sum * 1e4f;
    }

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
        // measured from tj's first corner dropped onto ti's plane: near the overlap, so a ground-sized
        // ti does not cost the small one its float precision
        var origin = tj.A - n * (tj.A - ti.A).Dot(n);
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
