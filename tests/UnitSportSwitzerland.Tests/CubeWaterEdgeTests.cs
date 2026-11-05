using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The cube water stage across face edges (#1552; tools/TerrainPreprocessor/CubeWaterLattice.cs,
/// <see cref="CubeSphere.Fold"/>): a lattice past a face's edge folds onto the next face as a grid, the
/// shared edge vertices join a leaf's flood-fill labels to the other face's, and a lake straddling an edge
/// or a cube corner is one body.
/// </summary>
public class CubeWaterEdgeTests
{
    private readonly ITestOutputHelper _out;

    public CubeWaterEdgeTests(ITestOutputHelper output) => _out = output;

    private const int Level = 14;
    private static readonly int Bits = CubeKey.LatticeBits(Level);
    private static readonly int N = 1 << Bits, Side = N + 1;
    private static readonly int Last = (1 << Level) - 1;

    private static EcefPos At(int face, long i, long j) => CubeSphere.LatticeEcef(face, Level + Bits, i, j, 0);

    [Fact]
    public void A_vertex_past_each_faces_edge_folds_onto_the_neighbour_leaf_the_same_distance_in()
    {
        long faceN = (long)N << Level;
        for (int face = 0; face < CubeSphere.Faces; face++)
            for (int side = 0; side < 4; side++)
            {
                // the middle leaf of each edge, and the vertex half a leaf past it
                var leaf = side switch
                {
                    0 => new CubeKey(face, Level, Last, 5000),
                    1 => new CubeKey(face, Level, 7000, Last),
                    2 => new CubeKey(face, Level, 0, 9000),
                    _ => new CubeKey(face, Level, 11000, 0),
                };
                var (dx, dy) = side switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
                long ci = ((long)leaf.X << Bits) + N / 2 + dx * N, cj = ((long)leaf.Y << Bits) + N / 2 + dy * N;
                var past = CubeWaterLattice.Past(face, Level, Bits, ci, cj)!.Value;
                Assert.Equal(leaf.Neighbour(dx, dy), past.Leaf);
                Assert.Equal(N / 2 * Side + N / 2, past.Index);

                // along the row through the edge: the folded points go on as the grid does, k cells out as far as k cells in
                long ei = dx > 0 ? faceN : dx < 0 ? 0 : ci, ej = dy > 0 ? faceN : dy < 0 ? 0 : cj;
                var edge = At(face, ei, ej);
                for (int k = 1; k <= 300; k += 37)
                {
                    var f = CubeSphere.Fold(face, Level + Bits, ei + dx * k, ej + dy * k)!.Value;
                    Assert.NotEqual(face, f.Face);
                    double outM = At(f.Face, f.I, f.J).DistanceTo(edge), inM = At(face, ei - dx * k, ej - dy * k).DistanceTo(edge);
                    Assert.InRange(outM / inM, 0.99, 1.01);
                }
                // the edge vertex itself is the face's own
                Assert.Equal((face, ei, ej), CubeSphere.Fold(face, Level + Bits, ei, ej));
            }
        // past a cube corner: no grid, no vertex
        Assert.Null(CubeSphere.Fold(0, Level + Bits, faceN + 3, faceN + 5));
    }

    [Fact]
    public void A_folded_window_stays_a_grid_across_the_edge()
    {
        // a window row across face 0's +u edge: each cell's steps east and north about as long as the last cell's and skewed as much (no jump at the fold)
        var leaf = new CubeKey(0, Level, Last, 5000);
        long gx0 = ((long)leaf.X << Bits) + N - 40, gy = ((long)leaf.Y << Bits) + 100;
        EcefPos Cell(long gx, long gyy)
        {
            var (k, idx) = CubeWaterLattice.Past(0, Level, Bits, gx, gyy)!.Value;   // inside the face, the face's own leaf
            return k.Vertex(Bits, idx % Side, idx / Side, 0);
        }
        double lastE = 0, lastN = 0, lastCos = 0;
        int flips = 0;
        for (long gx = gx0; gx < gx0 + 80; gx++)
        {
            var p = Cell(gx, gy);
            var u = Cell(gx + 1, gy) - p;
            var v = Cell(gx, gy + 1) - p;
            double cos = (u.X * v.X + u.Y * v.Y + u.Z * v.Z) / (u.Length * v.Length);
            if (gx > gx0)
            {
                // the ellipsoid's flattening (0.34 %) makes two faces' metrics differ a little at their edge: 0.12 % here
                Assert.InRange(u.Length / lastE, 0.995, 1.005);
                Assert.InRange(v.Length / lastN, 0.995, 1.005);
                // the grid's skew mirrors at the edge (its lines are great circles that bend there): the same size, the sign flipped once
                Assert.InRange(Math.Abs(cos) - Math.Abs(lastCos), -0.02, 0.02);   // 0.005 here, the flattening again
                if (Math.Sign(cos) != Math.Sign(lastCos)) flips++;
            }
            (lastE, lastN, lastCos) = (u.Length, v.Length, cos);
        }
        Assert.Equal(1, flips);
    }

    [Fact]
    public void A_leafs_vertices_on_another_face_are_that_faces_own_bit_for_bit()
    {
        var mid = new CubeKey(0, Level, Last, 5000);
        var pairs = CubeWaterLattice.AcrossFaces(mid).ToList();
        Assert.Equal(Side, pairs.Select(p => p.Mine).Distinct().Count());   // the east column; its two ends with the diagonal too
        Assert.All(pairs, p => Assert.NotEqual(0, p.Other.Face));
        var corner = new CubeKey(0, Level, Last, Last);
        var cpairs = CubeWaterLattice.AcrossFaces(corner).ToList();
        Assert.Equal(2, cpairs.Select(p => p.Other.Face).Distinct().Count());
        foreach (var (key, list) in new[] { (mid, pairs), (corner, cpairs) })
            foreach (var (mine, other, theirs) in list)
                Assert.Equal(key.Vertex(Bits, mine % Side, mine / Side, 0), other.Vertex(Bits, theirs % Side, theirs / Side, 0));
        Assert.Empty(CubeWaterLattice.AcrossFaces(new CubeKey(0, Level, Last - 1, 5000)));
    }

    public static readonly TheoryData<string, int, int, int> Lakes = new()
    {
        { "face edge", Last, 5000, 2 },
        { "cube corner", Last, Last, 3 },
    };

    [Theory]
    [MemberData(nameof(Lakes))]
    public void A_lake_straddling_a_face_edge_is_one_body(string where, int x, int y, int faces)
    {
        // a round lake of 400 m on the leaf's far corner vertex (on the edge, or the cube corner)
        var centreLeaf = new CubeKey(0, Level, x, y);
        var centre = centreLeaf.Vertex(Bits, N, N, 0);
        var leaves = new HashSet<CubeKey> { centreLeaf };
        foreach (var k in centreLeaf.Neighbours()) leaves.Add(k);
        foreach (var k in leaves.ToList()) foreach (var k2 in k.Neighbours()) leaves.Add(k2);
        var labels = new Dictionary<CubeKey, int[]>();
        var bases = new Dictionary<CubeKey, int>();
        int total = 0;
        foreach (var k in leaves.OrderBy(k => k, Comparer<CubeKey>.Create(LeafSeam.CompareCells)))
        {
            var wet = new bool[Side * Side];
            for (int j = 0; j <= N; j++)
                for (int i = 0; i <= N; i++)
                    wet[j * Side + i] = k.Vertex(Bits, i, j, 0).DistanceTo(centre) < 400;
            var (l, count) = Label(wet);
            if (count == 0) continue;
            labels[k] = l;
            bases[k] = total;
            total += count;
        }
        Assert.Equal(faces, labels.Keys.Select(k => k.Face).Distinct().Count());

        int Bodies(bool acrossFaces)
        {
            var parent = Enumerable.Range(0, total + 1).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            void Union(int a, int b) { if (a == 0 || b == 0) return; a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
            int G(CubeKey k, int idx) => labels[k][idx] == 0 ? 0 : bases[k] + labels[k][idx];
            foreach (var k in labels.Keys)
            {
                // phase B as the stage runs it: east and north on the face, then across face edges
                if (k.Neighbour(1, 0) is { } e && e.Face == k.Face && labels.ContainsKey(e))
                    for (int j = 0; j <= N; j++) Union(G(k, j * Side + N), G(e, j * Side));
                if (k.Neighbour(0, 1) is { } nn && nn.Face == k.Face && labels.ContainsKey(nn))
                    for (int i = 0; i <= N; i++) Union(G(k, N * Side + i), G(nn, i));
                if (!acrossFaces) continue;
                foreach (var (mine, other, theirs) in CubeWaterLattice.AcrossFaces(k))
                    if (labels.ContainsKey(other)) Union(G(k, mine), G(other, theirs));
            }
            return Enumerable.Range(1, total).Select(Find).Distinct().Count();
        }
        int apart = Bodies(false), joined = Bodies(true);
        _out.WriteLine($"{where}: {labels.Count} leaves on {faces} faces hold the lake; {apart} bodies one face at a time, {joined} across faces");
        Assert.Equal(faces, apart);
        Assert.Equal(1, joined);
    }

    private static (int[] Labels, int Count) Label(bool[] wet)
    {
        var labels = new int[wet.Length];
        var stack = new Stack<int>();
        int next = 0;
        for (int s = 0; s < wet.Length; s++)
        {
            if (!wet[s] || labels[s] != 0) continue;
            labels[s] = ++next;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), r = i / Side, c = i % Side;
                foreach (int j in new[] { c > 0 ? i - 1 : -1, c < N ? i + 1 : -1, r > 0 ? i - Side : -1, r < N ? i + Side : -1 })
                    if (j >= 0 && wet[j] && labels[j] == 0) { labels[j] = next; stack.Push(j); }
            }
        }
        return (labels, next);
    }
}
