using Godot;
using UnitSport.Terrain.Earth;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// A leaf's lake cut at its edges (#1632, G2 C6): two neighbouring leaves make the same edge plane, so their lakes meet
/// with neither a gap nor an overlap, and a lake covering a leaf is cut to the leaf's own area.
/// </summary>
public class LeafWaterClipTests
{
    private sealed record Mesh(Vector3[] Vertices, Vector2[] Uvs, int[] Indices);

    private static Mesh? Clip(Mesh m, LeafWaterClip.Plane[] planes) =>
        LeafWaterClip.Clip(m.Vertices, m.Uvs, m.Indices, planes) is { } r ? new Mesh(r.Vertices, r.Uvs, r.Indices) : null;

    private static readonly CubeKey Geneva = CubeKey.FromLatLon(46.21, 6.15, LeafFrames.LeafLevel);

    private static EcefPos ToEcef(CubeKey leaf, Vector3 standIn) =>
        leaf.Anchor.ToEcef(standIn.X - LeafStandIn.Offset, standIn.Y, standIn.Z - LeafStandIn.Offset);

    private static Vector3 Local(CubeKey leaf, EcefPos p)
    {
        var (x, y, z) = leaf.Anchor.ToLocal(p);
        return new Vector3((float)(x + LeafStandIn.Offset), (float)y, (float)(z + LeafStandIn.Offset));
    }

    /// <summary>A flat lake over the whole stand-in tile, 2 m quads, at a height.</summary>
    private static Mesh Sheet(float y)
    {
        const int m = 501;
        var v = new Vector3[m * m];
        var uv = new Vector2[m * m];
        for (int r = 0; r < m; r++)
            for (int c = 0; c < m; c++)
            {
                v[r * m + c] = new Vector3(c * 2f, y, r * 2f);
                uv[r * m + c] = new Vector2(0.5f, 2f);
            }
        var idx = new List<int>();
        for (int r = 0; r + 1 < m; r++)
            for (int c = 0; c + 1 < m; c++)
            {
                int a = r * m + c, b = a + 1, d = a + m, e = d + 1;
                idx.AddRange([a, e, b, a, d, e]);
            }
        return new Mesh(v, uv, idx.ToArray());
    }

    private static double Area(Mesh mesh)
    {
        double sum = 0;
        for (int t = 0; t < mesh.Indices.Length; t += 3)
        {
            var a = mesh.Vertices[mesh.Indices[t]];
            var b = mesh.Vertices[mesh.Indices[t + 1]];
            var c = mesh.Vertices[mesh.Indices[t + 2]];
            sum += 0.5 * Math.Abs((double)(b.X - a.X) * (c.Z - a.Z) - (double)(c.X - a.X) * (b.Z - a.Z));
        }
        return sum;
    }

    private static EcefPos[] Corners(CubeKey leaf) =>
        [leaf.Vertex(0, 0, 0, 372), leaf.Vertex(0, 1, 0, 372), leaf.Vertex(0, 1, 1, 372), leaf.Vertex(0, 0, 1, 372)];

    private static double Dist(EcefPos a, EcefPos b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    [Fact]
    public void TheLeafCentreIsInsideEveryPlane()
    {
        var centre = new Vector3((float)LeafStandIn.Offset, 0, (float)LeafStandIn.Offset);
        foreach (var plane in LeafWaterClip.EdgePlanes(Geneva))
            Assert.True(plane.Distance(centre) > 250, $"centre {plane.Distance(centre):F1} m from an edge");
    }

    [Fact]
    public void NeighboursShareTheirEdgePlane()
    {
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var other = Geneva.Neighbour(dx, dy)!.Value;
            var mine = LeafWaterClip.EdgePlanes(Geneva);
            var theirs = LeafWaterClip.EdgePlanes(other);
            // the corners both leaves have: their shared edge, as lattice indices of this leaf
            var shared = new[] { (0, 0), (1, 0), (1, 1), (0, 1) }
                .Where(c => Corners(other).Any(o => Dist(o, Geneva.Vertex(0, c.Item1, c.Item2, 372)) < 1e-6)).ToArray();
            Assert.Equal(2, shared.Length);
            // points along it, at the lake's height and 50 m above (the lattice's own verticals), sit on one plane of
            // each, within a millimetre
            int hits = 0;
            foreach (double h in new[] { 372.0, 422.0 })
                for (int k = 0; k <= 10; k++)
                {
                    double s = k / 10.0;
                    var c0 = Geneva.Vertex(0, shared[0].Item1, shared[0].Item2, h);
                    var c1 = Geneva.Vertex(0, shared[1].Item1, shared[1].Item2, h);
                    var p = new EcefPos(c0.X + (c1.X - c0.X) * s, c0.Y + (c1.Y - c0.Y) * s, c0.Z + (c1.Z - c0.Z) * s);
                    float da = mine.Min(pl => Math.Abs(pl.Distance(Local(Geneva, p))));
                    float db = theirs.Min(pl => Math.Abs(pl.Distance(Local(other, p))));
                    Assert.True(da < 0.002f && db < 0.002f, $"({dx},{dy}) at {s}, {h} m: {da * 1000:F2} / {db * 1000:F2} mm off the edge plane");
                    hits++;
                }
            Assert.Equal(22, hits);
        }
    }

    [Fact]
    public void ALakeOverTheTileIsCutToTheLeaf()
    {
        var clipped = Clip(Sheet(380f), LeafWaterClip.EdgePlanes(Geneva));
        Assert.NotNull(clipped);
        // the leaf's own area from its corners, in the stand-in frame
        var p = new[] { (0, 0), (1, 0), (1, 1), (0, 1) }.Select(c => Local(Geneva, Geneva.Vertex(0, c.Item1, c.Item2, 380))).ToArray();
        double leaf = 0;
        for (int k = 0; k < 4; k++)
        {
            var a = p[k];
            var b = p[(k + 1) % 4];
            leaf += (double)a.X * b.Z - (double)b.X * a.Z;
        }
        leaf = Math.Abs(leaf) / 2;
        double area = Area(clipped!);
        Assert.True(Math.Abs(area - leaf) < leaf * 1e-4, $"clipped {area:F0} m² against the leaf's {leaf:F0} m²");
        // the wave scale carried over
        Assert.All(clipped!.Uvs, uv => Assert.Equal(0.5f, uv.X, 4));
    }

    [Fact]
    public void TwoNeighboursTileTheirSharedLakeWithoutGapOrOverlap()
    {
        var east = Geneva.Neighbour(1, 0)!.Value;
        var a = Clip(Sheet(380f), LeafWaterClip.EdgePlanes(Geneva))!;
        var b = Clip(Sheet(380f), LeafWaterClip.EdgePlanes(east))!;
        // sample a line across the shared edge, in Geneva's frame: each point is covered by exactly one of the two
        var mid0 = Geneva.Vertex(0, 1, 0, 380);
        var mid1 = Geneva.Vertex(0, 1, 1, 380);
        var mid = new EcefPos((mid0.X + mid1.X) / 2, (mid0.Y + mid1.Y) / 2, (mid0.Z + mid1.Z) / 2);
        var across = Local(Geneva, mid);
        var dir = (Local(Geneva, mid1) - Local(Geneva, mid0)).Normalized();
        var normal = new Vector3(dir.Z, 0, -dir.X);
        for (int k = -20; k <= 20; k++)
        {
            var at = across + normal * (k * 0.05f + 0.013f);
            int covered = (Covers(a, at) ? 1 : 0) + (Covers(b, Local(east, ToEcef(Geneva, at))) ? 1 : 0);
            Assert.True(covered == 1, $"{k * 0.05f + 0.013f:F3} m across the edge covered {covered} times");
        }
    }

    private static bool Covers(Mesh mesh, Vector3 p)
    {
        for (int t = 0; t < mesh.Indices.Length; t += 3)
        {
            var a = mesh.Vertices[mesh.Indices[t]];
            var b = mesh.Vertices[mesh.Indices[t + 1]];
            var c = mesh.Vertices[mesh.Indices[t + 2]];
            if (Math.Max(a.X, Math.Max(b.X, c.X)) < p.X - 0.01f || Math.Min(a.X, Math.Min(b.X, c.X)) > p.X + 0.01f) continue;
            if (Math.Max(a.Z, Math.Max(b.Z, c.Z)) < p.Z - 0.01f || Math.Min(a.Z, Math.Min(b.Z, c.Z)) > p.Z + 0.01f) continue;
            double d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(neg && pos)) return true;
        }
        return false;
    }

    private static double Cross(Vector3 a, Vector3 b, Vector3 p) =>
        (double)(b.X - a.X) * (p.Z - a.Z) - (double)(b.Z - a.Z) * (p.X - a.X);
}
