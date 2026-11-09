using Godot;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// A leaf's still water cut exactly at the leaf's edges (#1632, G2 C6). The water is built on the stand-in tile's 2 m
/// samples, which never line up with the leaf's edges: left to the samples inside the leaf, each leaf's lake stopped up
/// to a sample short of its edge, and the lake bed showed through in lines between leaves. The samples are grown a few
/// metres past the leaf (<see cref="LeafStandIn.Water"/>'s margin) and the surface is clipped here by the leaf's four
/// edge planes, each through two of the leaf's corners on the ellipsoid and a point above one of them: a leaf and
/// its neighbour make the same plane from the same corners (the cube's edges are shared exactly), so their lakes meet
/// along it with neither a gap nor an overlap (a translucent overlap would read as a darker line).
/// </summary>
public static class LeafWaterClip
{
    /// <summary>A half-space in the stand-in frame: inside where <c>Normal·p + D ≥ 0</c>.</summary>
    public readonly record struct Plane(Vector3 Normal, float D)
    {
        public float Distance(Vector3 p) => Normal.Dot(p) + D;
    }

    /// <summary>The leaf's four edge planes in its stand-in tile's frame, facing in.</summary>
    public static Plane[] EdgePlanes(CubeKey leaf)
    {
        var anchor = leaf.Anchor;
        // the corners on the ellipsoid and 1 km up along their normals (lattice vertices of the leaf itself)
        EcefPos Corner(int i, int j, double h) => leaf.Vertex(0, i, j, h);
        (double X, double Y, double Z) Local(EcefPos p)
        {
            var (x, y, z) = anchor.ToLocal(p);
            return (x + LeafStandIn.Offset, y, z + LeafStandIn.Offset);
        }
        var inside = Local(anchor.Origin);
        var planes = new Plane[4];
        // edges in order round the leaf: (0,0)-(1,0), (1,0)-(1,1), (1,1)-(0,1), (0,1)-(0,0)
        ReadOnlySpan<(int I, int J)> ring = [(0, 0), (1, 0), (1, 1), (0, 1)];
        for (int e = 0; e < 4; e++)
        {
            var (ai, aj) = ring[e];
            var (bi, bj) = ring[(e + 1) % 4];
            // the shared edge's plane must come out the same from both leaves (each walks it the other way): the corners
            // in a fixed order, the vertical over the first
            var p0 = Corner(ai, aj, 0);
            var p1 = Corner(bi, bj, 0);
            bool swap = Before(p1, p0);
            if (swap) (p0, p1) = (p1, p0);
            var p2 = swap ? Corner(bi, bj, 1000) : Corner(ai, aj, 1000);
            planes[e] = Through(Local(p0), Local(p1), Local(p2), inside);
        }
        return planes;
    }

    private static bool Before(EcefPos a, EcefPos b) =>
        a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;

    private static Plane Through((double X, double Y, double Z) a, (double X, double Y, double Z) b,
        (double X, double Y, double Z) c, (double X, double Y, double Z) inside)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        (nx, ny, nz) = (nx / l, ny / l, nz / l);
        double d = -(nx * a.X + ny * a.Y + nz * a.Z);
        if (nx * inside.X + ny * inside.Y + nz * inside.Z + d < 0) (nx, ny, nz, d) = (-nx, -ny, -nz, -d);
        return new Plane(new Vector3((float)nx, (float)ny, (float)nz), (float)d);
    }

    /// <summary>
    /// A water mesh clipped to the planes: whole triangles inside are kept as they are, those across an edge are cut
    /// along it (their wave scale, UV, interpolated), those outside dropped. Null when nothing is left.
    /// </summary>
    public static (Vector3[] Vertices, Vector2[] Uvs, int[] Indices)? Clip(Vector3[] meshVertices, Vector2[] meshUvs,
        int[] meshIndices, Plane[] planes)
    {
        var mesh = (Vertices: meshVertices, Uvs: meshUvs, Indices: meshIndices);
        var vertices = new List<Vector3>(mesh.Vertices);
        var uvs = new List<Vector2>(mesh.Uvs);
        var indices = new List<int>(mesh.Indices.Length);
        var poly = new List<(Vector3 P, Vector2 Uv)>(8);
        var next = new List<(Vector3 P, Vector2 Uv)>(8);
        var idx = mesh.Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            bool whole = true, gone = false;
            foreach (var plane in planes)
            {
                float da = plane.Distance(mesh.Vertices[a]), db = plane.Distance(mesh.Vertices[b]), dc = plane.Distance(mesh.Vertices[c]);
                if (da < 0 && db < 0 && dc < 0) { gone = true; break; }
                if (da < 0 || db < 0 || dc < 0) whole = false;
            }
            if (gone) continue;
            if (whole)
            {
                indices.Add(a); indices.Add(b); indices.Add(c);
                continue;
            }

            poly.Clear();
            poly.Add((mesh.Vertices[a], mesh.Uvs[a]));
            poly.Add((mesh.Vertices[b], mesh.Uvs[b]));
            poly.Add((mesh.Vertices[c], mesh.Uvs[c]));
            foreach (var plane in planes)
            {
                next.Clear();
                for (int k = 0; k < poly.Count; k++)
                {
                    var (p, pu) = poly[k];
                    var (q, qu) = poly[(k + 1) % poly.Count];
                    float dp = plane.Distance(p), dq = plane.Distance(q);
                    if (dp >= 0) next.Add((p, pu));
                    if ((dp >= 0) != (dq >= 0))
                    {
                        float s = dp / (dp - dq);
                        next.Add((p.Lerp(q, s), pu.Lerp(qu, s)));
                    }
                }
                (poly, next) = (next, poly);
                if (poly.Count < 3) break;
            }
            if (poly.Count < 3) continue;
            int first = vertices.Count;
            foreach (var (p, uv) in poly)
            {
                vertices.Add(p);
                uvs.Add(uv);
            }
            // a fan keeps the winding: clipping a convex polygon keeps its order
            for (int k = 1; k + 1 < poly.Count; k++)
            {
                indices.Add(first); indices.Add(first + k); indices.Add(first + k + 1);
            }
        }
        return indices.Count == 0 ? null : (vertices.ToArray(), uvs.ToArray(), indices.ToArray());
    }
}
