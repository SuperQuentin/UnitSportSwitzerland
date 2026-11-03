using Godot;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A pit as the ground sees it (#619), tile-local (x east of the tile's west edge, z south of its
/// north edge): the rectangle dug out, the floor's height, and the ramp, from its top edge on the
/// pit's edge (<see cref="TopOuter"/> on the pit's own edge, <see cref="TopInner"/> on the lane's
/// inner edge) <see cref="RampLength"/> m down the lane (<see cref="Down"/>) to the floor.
/// </summary>
public sealed record PitCut(SiteRect Rect, float Floor, Vector2 TopOuter, Vector2 TopInner, Vector2 Down, float RampLength);

/// <summary>
/// Digs a site's pit into the ground (#619), in its mesh and its collision alike, from the very
/// lattice and heights the ground is made of (<c>terrain/anything-meet-terrain-has-built</c>).
///
/// <para>
/// <b>The hole.</b> A pit is turned with its building, so it never lines up with the 1 m lattice.
/// Every ground triangle that reaches into the pit is dropped and its part outside the pit put back,
/// clipped to the pit's straight edges (the <i>collar</i>): the ground then meets the pit's walls on
/// a straight line, at its own heights, at any LOD stride, with nothing poking into the pit and no
/// gap round it. The collision's height map cannot lose part of a quad, so it loses every quad that
/// reaches in (<see cref="Holes"/>, NaN: <c>terrain/jolt-heightmapshape3d-treats-nan-heights</c>), and
/// Jolt loses the four quads round each NaN; their part outside the pit comes back as faces.
/// </para>
///
/// <para>
/// <b>The lining</b> is drawn with the ground, not with the site: walls from the ground's edge down
/// to the floor, the floor, and the ramp down the lane. So a tile whose site has been shed with its
/// buildings (far off, #553) still shows a closed pit, never a hole into the void. The site's prop
/// mesh stands its sheet piles just in front of these walls.
/// </para>
/// </summary>
public static class PitTerrain
{
    /// <summary>Full-resolution quads per tile side (<c>HoleFormat.QuadsPerSide</c>).</summary>
    public const int QuadsPerSide = 1000;

    // raw linear, alpha 0: no surface pattern (the ground's palette writes one in the alpha)
    private static readonly Color DugFace = new Color(0.36f, 0.29f, 0.22f).SrgbToLinear() with { A = 0f };
    private static readonly Color DugFoot = new Color(0.27f, 0.22f, 0.17f).SrgbToLinear() with { A = 0f };
    private static readonly Color Gravel = new Color(0.47f, 0.44f, 0.39f).SrgbToLinear() with { A = 0f };
    private static readonly Color Track = new Color(0.41f, 0.33f, 0.25f).SrgbToLinear() with { A = 0f };

    /// <summary>The full-resolution quads (row × <see cref="QuadsPerSide"/> + column) that reach into any pit.</summary>
    public static HashSet<int> Holes(IReadOnlyList<PitCut> pits)
    {
        var holes = new HashSet<int>();
        var square = new List<Corner>(4);
        foreach (var pit in pits)
        {
            var (x0, z0, x1, z1) = Bounds(pit.Rect);
            for (int r = Math.Max(0, (int)MathF.Floor(z0)); r <= Math.Min(QuadsPerSide - 1, (int)MathF.Floor(z1)); r++)
                for (int c = Math.Max(0, (int)MathF.Floor(x0)); c <= Math.Min(QuadsPerSide - 1, (int)MathF.Floor(x1)); c++)
                {
                    square.Clear();
                    square.Add(new Corner(new Vector3(c, 0, r), default, Vector3.Up));
                    square.Add(new Corner(new Vector3(c + 1, 0, r), default, Vector3.Up));
                    square.Add(new Corner(new Vector3(c + 1, 0, r + 1), default, Vector3.Up));
                    square.Add(new Corner(new Vector3(c, 0, r + 1), default, Vector3.Up));
                    if (Area(Inside(square, pit.Rect)) > 1e-4f) holes.Add(r * QuadsPerSide + c);
                }
        }
        return holes;
    }

    /// <summary>
    /// The collision that goes with <see cref="Holes"/>: the collar over every quad Jolt drops (the
    /// holes and the quads round their NaN corners), clipped to outside the pits, and each pit's
    /// lining. <paramref name="height"/> is the full-resolution ground the height map is made of
    /// (with the road blend in it), by column and row.
    /// </summary>
    public static List<Vector3> CollisionFaces(IReadOnlyList<PitCut> pits, HashSet<int> holes, Func<int, int, float> height)
    {
        var faces = new List<Vector3>();
        // a NaN at a quad's top-left corner (StampHoles) takes the four quads round that corner
        var dropped = new HashSet<int>();
        foreach (int h in holes)
        {
            int c = h % QuadsPerSide, r = h / QuadsPerSide;
            for (int dr = -1; dr <= 0; dr++)
                for (int dc = -1; dc <= 0; dc++)
                    if (c + dc >= 0 && r + dr >= 0) dropped.Add((r + dr) * QuadsPerSide + c + dc);
        }
        var tri = new List<Corner>(3);
        foreach (int q in dropped)
        {
            int c = q % QuadsPerSide, r = q / QuadsPerSide;
            var v00 = new Vector3(c, height(c, r), r);
            var v10 = new Vector3(c + 1, height(c + 1, r), r);
            var v01 = new Vector3(c, height(c, r + 1), r + 1);
            var v11 = new Vector3(c + 1, height(c + 1, r + 1), r + 1);
            foreach (var (a, b, d) in new[] { (v00, v10, v01), (v10, v11, v01) })
            {
                tri.Clear();
                tri.Add(new Corner(a, default, Vector3.Up)); tri.Add(new Corner(b, default, Vector3.Up)); tri.Add(new Corner(d, default, Vector3.Up));
                foreach (var piece in OutsideAll(tri, pits))
                    for (int k = 1; k + 1 < piece.Count; k++)
                    {
                        faces.Add(piece[0].P); faces.Add(piece[k].P); faces.Add(piece[k + 1].P);
                    }
            }
        }
        var sampler = new Sampler(1f, QuadsPerSide + 1, height);
        foreach (var pit in pits)
            Lining(pit, sampler, (a, b, d, _, _) => { faces.Add(a); faces.Add(b); faces.Add(d); });
        return faces;
    }

    /// <summary>
    /// A ground mesh with the pits cut out and lined: the grid triangles that reach into a pit
    /// dropped and their outside put back (colours and normals interpolated), then each pit's walls,
    /// floor and ramp, their normals straight up (the terrain body's "keep the facet light", #883).
    /// <paramref name="m"/> vertices per side at <paramref name="quad"/> m: the grid is the first m²
    /// vertices, laid out row by row; anything after it (the skirts) is left alone.
    /// </summary>
    public static (Vector3[] Vertices, Color[] Colors, Vector3[] Normals, int[] Indices) Cut(Vector3[] vertices, Color[] colors,
        Vector3[] normals, int[] indices, int m, float quad, IReadOnlyList<PitCut> pits)
    {
        if (pits.Count == 0) return (vertices, colors, normals, indices);
        var bounds = pits.Select(p => Bounds(p.Rect)).ToArray();
        // the lattice rows any pit reaches, so most triangles are passed over with one division
        int rowLo = int.MaxValue, rowHi = int.MinValue;
        foreach (var (_, z0, _, z1) in bounds)
        {
            rowLo = Math.Min(rowLo, (int)MathF.Floor(z0 / quad) - 1);
            rowHi = Math.Max(rowHi, (int)MathF.Floor(z1 / quad) + 1);
        }
        var verts = new List<Vector3>();
        var cols = new List<Color>();
        var norms = new List<Vector3>();
        var idx = new List<int>();
        // the triangles kept as they were, in runs copied whole
        var kept = new int[indices.Length];
        int keptCount = 0;
        int grid = m * m;
        var tri = new List<Corner>(3);
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            bool near = false;
            int row = i0 / m;
            if (row >= rowLo && row <= rowHi && i0 < grid && i1 < grid && i2 < grid)
            {
                var a = vertices[i0]; var b = vertices[i1]; var c = vertices[i2];
                float tx0 = Math.Min(a.X, Math.Min(b.X, c.X)), tx1 = Math.Max(a.X, Math.Max(b.X, c.X));
                float tz0 = Math.Min(a.Z, Math.Min(b.Z, c.Z)), tz1 = Math.Max(a.Z, Math.Max(b.Z, c.Z));
                foreach (var (x0, z0, x1, z1) in bounds)
                    if (tx1 > x0 && tx0 < x1 && tz1 > z0 && tz0 < z1) { near = true; break; }
            }
            if (!near)
            {
                kept[keptCount++] = i0; kept[keptCount++] = i1; kept[keptCount++] = i2;
                continue;
            }
            tri.Clear();
            tri.Add(new Corner(vertices[i0], colors[i0], normals[i0]));
            tri.Add(new Corner(vertices[i1], colors[i1], normals[i1]));
            tri.Add(new Corner(vertices[i2], colors[i2], normals[i2]));
            bool touches = false;
            foreach (var pit in pits)
                if (Area(Inside(tri, pit.Rect)) > 1e-5f) { touches = true; break; }
            if (!touches)
            {
                kept[keptCount++] = i0; kept[keptCount++] = i1; kept[keptCount++] = i2;
                continue;
            }
            // clipping keeps the vertex order, so the pieces face the way the triangle did; the new
            // vertices are numbered after the mesh's own
            foreach (var piece in OutsideAll(tri, pits))
            {
                int first = vertices.Length + verts.Count;
                foreach (var (p, col, n) in piece) { verts.Add(p); cols.Add(col); norms.Add(n); }
                for (int k = 1; k + 1 < piece.Count; k++) { idx.Add(first); idx.Add(first + k); idx.Add(first + k + 1); }
            }
        }
        var sampler = new Sampler(quad, m, (c, r) => vertices[r * m + c].Y);
        foreach (var pit in pits)
            Lining(pit, sampler, (a, b, d, col, facing) =>
            {
                // the ground's front face: clockwise from where it is seen (BuildSurfaceCore's winding)
                if ((b - a).Cross(d - a).Dot(facing) > 0) (b, d) = (d, b);
                int first = vertices.Length + verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(d);
                cols.Add(col); cols.Add(col); cols.Add(col);
                norms.Add(Vector3.Up); norms.Add(Vector3.Up); norms.Add(Vector3.Up);
                idx.Add(first); idx.Add(first + 1); idx.Add(first + 2);
            });
        var outV = new Vector3[vertices.Length + verts.Count];
        var outC = new Color[outV.Length];
        var outN = new Vector3[outV.Length];
        Array.Copy(vertices, outV, vertices.Length);
        Array.Copy(colors, outC, colors.Length);
        Array.Copy(normals, outN, normals.Length);
        verts.CopyTo(outV, vertices.Length);
        cols.CopyTo(outC, colors.Length);
        norms.CopyTo(outN, normals.Length);
        var outI = new int[keptCount + idx.Count];
        Array.Copy(kept, outI, keptCount);
        idx.CopyTo(outI, keptCount);
        return (outV, outC, outN, outI);
    }

    /// <summary>
    /// The ground as one LOD draws it: a lattice of <see cref="Quad"/> m, each quad split from its
    /// top-right to its bottom-left corner (<c>BuildSurfaceCore</c>'s triangles), heights by column and row.
    /// </summary>
    public readonly struct Sampler(float quad, int m, Func<int, int, float> height)
    {
        public float Quad => quad;

        public float At(float x, float z)
        {
            float gx = x / quad, gz = z / quad;
            int c = Math.Clamp((int)MathF.Floor(gx), 0, m - 2), r = Math.Clamp((int)MathF.Floor(gz), 0, m - 2);
            float fx = gx - c, fz = gz - r;
            float h00 = height(c, r), h10 = height(c + 1, r), h01 = height(c, r + 1), h11 = height(c + 1, r + 1);
            return fx + fz <= 1f
                ? h00 + fx * (h10 - h00) + fz * (h01 - h00)
                : h11 + (1 - fx) * (h01 - h11) + (1 - fz) * (h10 - h11);
        }

        /// <summary>
        /// Points along a <paramref name="from"/>-<paramref name="to"/> segment where the drawn ground
        /// bends (lattice lines and quad diagonals), the ends included, with the ground's height: the
        /// polyline the clipped ground ends on.
        /// </summary>
        public List<Vector3> Profile(Vector2 from, Vector2 to)
        {
            var ts = new List<float> { 0f, 1f };
            void Crossings(float f0, float f1)
            {
                if (MathF.Abs(f1 - f0) < 1e-6f) return;
                float lo = Math.Min(f0, f1), hi = Math.Max(f0, f1);
                for (float k = MathF.Floor(lo) + 1; k < hi; k++) ts.Add((k - f0) / (f1 - f0));
            }
            Crossings(from.X / quad, to.X / quad);
            Crossings(from.Y / quad, to.Y / quad);
            Crossings((from.X + from.Y) / quad, (to.X + to.Y) / quad);
            ts.Sort();
            var pts = new List<Vector3>(ts.Count);
            float last = -1f;
            foreach (float t in ts)
            {
                if (t - last < 1e-5f) continue;
                last = t;
                var p = from.Lerp(to, t);
                pts.Add(new Vector3(p.X, At(p.X, p.Y), p.Y));
            }
            return pts;
        }
    }

    /// <summary>
    /// A pit's lining as triangles, each with its colour and the side it is seen from: the walls
    /// from the ground's edge down to the floor (but across the ramp's mouth), the floor, the ramp
    /// from the ground at its mouth down to the floor, and the ramp's open side.
    /// </summary>
    public static void Lining(PitCut pit, Sampler ground, Action<Vector3, Vector3, Vector3, Color, Vector3> emit)
    {
        var k = pit.Rect.Corners();
        float floor = pit.Floor;
        var centre = pit.Rect.Center;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color top, Color bottom, Vector3 facing)
        {
            emit(a, b, c, top, facing);
            emit(a, c, d, bottom, facing);
        }
        void Wall(Vector2 a, Vector2 b)
        {
            if (a.DistanceTo(b) < 1e-3f) return;
            var mid = (a + b) / 2;
            var inward = new Vector3(centre.X - mid.X, 0, centre.Y - mid.Y);
            // the inward normal of the edge, not toward the centre: on a long pit the two differ
            var e = (b - a).Normalized();
            var n = new Vector3(-e.Y, 0, e.X);
            if (n.Dot(inward) < 0) n = -n;
            var pts = ground.Profile(a, b);
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var p0 = pts[i]; var p1 = pts[i + 1];
                float y0 = Math.Max(p0.Y, floor), y1 = Math.Max(p1.Y, floor);
                Quad(p0 with { Y = y0 }, p1 with { Y = y1 }, p1 with { Y = floor }, p0 with { Y = floor }, DugFace, DugFoot, n);
            }
        }

        // the walls, the one with the mouth in two parts either side of it
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = k[i], b = k[(i + 1) % 4];
            var e = b - a;
            float len = e.Length();
            var dir = e / len;
            float So(Vector2 p) => (p - a).Dot(dir);
            bool OnEdge(Vector2 p) => MathF.Abs((p - a).Dot(new Vector2(-dir.Y, dir.X))) < 0.05f && So(p) > -0.05f && So(p) < len + 0.05f;
            if (pit.RampLength > 0 && OnEdge(pit.TopOuter) && OnEdge(pit.TopInner))
            {
                float s0 = Math.Min(So(pit.TopOuter), So(pit.TopInner)), s1 = Math.Max(So(pit.TopOuter), So(pit.TopInner));
                Wall(a, a + dir * s0);
                Wall(a + dir * s1, b);
            }
            else Wall(a, b);
        }

        // the floor
        var f = k.Select(p => new Vector3(p.X, floor, p.Y)).ToArray();
        Quad(f[0], f[1], f[2], f[3], Gravel, Gravel, Vector3.Up);

        if (pit.RampLength <= 0) return;
        // the ramp: from the ground's own profile across its mouth, straight down to the floor
        var top = ground.Profile(pit.TopOuter, pit.TopInner);
        var down = new Vector3(pit.Down.X, 0, pit.Down.Y) * pit.RampLength;
        for (int i = 0; i + 1 < top.Count; i++)
        {
            var t0 = top[i] with { Y = Math.Max(top[i].Y, floor) };
            var t1 = top[i + 1] with { Y = Math.Max(top[i + 1].Y, floor) };
            Quad(t0, t1, (t1 + down) with { Y = floor }, (t0 + down) with { Y = floor }, Track, Track, Vector3.Up);
        }
        // its open side, toward the building: a triangle from its top down to its foot
        var ti = top[^1] with { Y = Math.Max(top[^1].Y, floor) };
        var toBuilding = new Vector3(pit.TopInner.X - pit.TopOuter.X, 0, pit.TopInner.Y - pit.TopOuter.Y).Normalized();
        emit(ti, ti with { Y = floor }, (ti + down) with { Y = floor }, DugFace, toBuilding);
    }

    // ---- clipping ------------------------------------------------------------------------------

    /// <summary>The axis-aligned box round a pit's rectangle: (x0, z0, x1, z1).</summary>
    public static (float X0, float Z0, float X1, float Z1) Bounds(SiteRect r)
    {
        var c = r.Corners();
        return (c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
    }

    /// <summary>The rectangle's four sides as (outward normal, offset): a point is outside side i when n·p > d.</summary>
    private static (Vector2 N, float D)[] Sides(SiteRect r) =>
    [
        (r.AxisU, r.AxisU.Dot(r.Center) + r.Width / 2), (-r.AxisU, -r.AxisU.Dot(r.Center) + r.Width / 2),
        (r.AxisV, r.AxisV.Dot(r.Center) + r.Depth / 2), (-r.AxisV, -r.AxisV.Dot(r.Center) + r.Depth / 2),
    ];

    /// <summary>A polygon's corner: where it is, its colour, its normal (the terrain's smoothed one, #883).</summary>
    public readonly record struct Corner(Vector3 P, Color C, Vector3 N);

    /// <summary>A convex polygon's part on one side of a line (Sutherland-Hodgman), positions, colours and normals interpolated.</summary>
    private static List<Corner> Clip(List<Corner> poly, Vector2 n, float d, bool outside)
    {
        var res = new List<Corner>(poly.Count + 2);
        float S(Vector3 p) => (outside ? 1 : -1) * (n.X * p.X + n.Y * p.Z - d);
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            float sp = S(a.P), sq = S(b.P);
            if (sp >= 0) res.Add(a);
            if (sp >= 0 != sq >= 0)
            {
                float t = sp / (sp - sq);
                var nt = a.N.Lerp(b.N, t);
                res.Add(new Corner(a.P.Lerp(b.P, t), a.C.Lerp(b.C, t), nt.LengthSquared() > 1e-8f ? nt.Normalized() : Vector3.Up));
            }
        }
        return res;
    }

    /// <summary>The part of a convex polygon inside the rectangle.</summary>
    private static List<Corner> Inside(List<Corner> poly, SiteRect r)
    {
        var rest = poly;
        foreach (var (n, d) in Sides(r))
        {
            rest = Clip(rest, n, d, outside: false);
            if (rest.Count < 3) return rest;
        }
        return rest;
    }

    /// <summary>A convex polygon less a rectangle, as convex pieces: outside the first side, then inside it and outside the next...</summary>
    private static List<List<Corner>> Outside(List<Corner> poly, SiteRect r)
    {
        var pieces = new List<List<Corner>>();
        var rest = poly;
        foreach (var (n, d) in Sides(r))
        {
            var piece = Clip(rest, n, d, outside: true);
            if (piece.Count >= 3 && Area(piece) > 1e-6f) pieces.Add(piece);
            rest = Clip(rest, n, d, outside: false);
            if (rest.Count < 3) break;
        }
        return pieces;
    }

    /// <summary>A convex polygon less every pit.</summary>
    private static List<List<Corner>> OutsideAll(List<Corner> poly, IReadOnlyList<PitCut> pits)
    {
        var pieces = new List<List<Corner>> { poly };
        foreach (var pit in pits)
        {
            var next = new List<List<Corner>>();
            foreach (var p in pieces) next.AddRange(Outside(p, pit.Rect));
            pieces = next;
        }
        return pieces;
    }

    /// <summary>A polygon's area in plan.</summary>
    public static float Area(List<Corner> poly)
    {
        float a = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i].P;
            var q = poly[(i + 1) % poly.Count].P;
            a += p.X * q.Z - q.X * p.Z;
        }
        return MathF.Abs(a) / 2;
    }
}
