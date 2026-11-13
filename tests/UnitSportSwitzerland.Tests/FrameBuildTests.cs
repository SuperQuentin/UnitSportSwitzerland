using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The cube-native build's cross-cutting pieces (#1507, G2 B3): the work-frame driver and its margins
/// across face edges, <see cref="LeafRegion"/> (TileRegion's contract over leaves), the stages' ground
/// (<see cref="FrameGround"/> on the leaf mesh, <see cref="Lv95Ground"/> over chunk grids), and the leaf
/// writer's seam pass (an owner per shared vertex, across frames and faces).
/// </summary>
public class FrameBuildTests
{
    private readonly ITestOutputHelper _out;

    public FrameBuildTests(ITestOutputHelper output) => _out = output;

    private const int Level = 14;

    private static readonly CubeKey Geneva = CubeKey.FromLatLon(46.2044, 6.1432, Level);

    /// <summary>The leaves of an (2r+1)² block round a leaf, across face edges.</summary>
    private static HashSet<CubeKey> Block(CubeKey centre, int r)
    {
        var set = new HashSet<CubeKey> { centre };
        for (int step = 0; step < r; step++)
            foreach (var k in set.ToList())
                foreach (var n in k.Neighbours()) set.Add(n);
        return set;
    }

    // ---- the driver -------------------------------------------------------------------------------

    [Fact]
    public void Frames_come_in_face_y_x_order_and_hold_their_own_leaves()
    {
        // leaves either side of a level-9 cell corner: four frames
        var corner = new CubeKey(0, Level, Geneva.X & ~31, Geneva.Y & ~31);
        var wanted = Block(corner, 3);
        var driver = new FrameDriver(wanted, 0, SwissSeaLevel.Instance);
        Assert.Equal(4, driver.Frames.Count);
        for (int k = 1; k < driver.Frames.Count; k++)
            Assert.True(LeafSeam.CompareCells(driver.Frames[k - 1].Cell, driver.Frames[k].Cell) < 0);
        Assert.Equal(wanted.Count, driver.Frames.Sum(f => f.Core.Count));
        foreach (var f in driver.Frames)
            Assert.All(f.Core, k => Assert.Equal(f.Cell, k.AncestorAt(WorkFrame.Level)));
    }

    [Fact]
    public void A_frames_margin_reaches_across_a_face_edge_and_stops_at_its_distance()
    {
        // the last frame of face 0 along u, the leaves either side of the edge
        var edgeLeaf = new CubeKey(0, Level, (1 << Level) - 1, 5000);
        var wanted = Block(edgeLeaf, 6);
        Assert.Contains(wanted, k => k.Face != 0);
        const double margin = 1500;
        var driver = new FrameDriver(wanted, margin, SwissSeaLevel.Instance);
        var plan = driver.Frames.Single(f => f.Cell == edgeLeaf.AncestorAt(WorkFrame.Level));
        var other = plan.Margin.Where(k => k.Face != 0).ToList();
        _out.WriteLine($"frame {plan.Cell}: {plan.Core.Count} core leaves, {plan.Margin.Count} margin leaves, {other.Count} on the next face");
        Assert.NotEmpty(other);
        // every margin leaf is outside the cell and within the margin; every wanted leaf left out is farther
        var outline = plan.Outline;
        double Dist(CubeKey k)
        {
            var poly = FrameDriver.Outline(plan.Frame.Plane, k, 8);
            double d = double.MaxValue;
            foreach (var p in poly)
                for (int b = 0; b < outline.Count; b++)
                {
                    var r = outline[b];
                    var s = outline[(b + 1) % outline.Count];
                    d = Math.Min(d, Geometry2.PointSegment(p.X, p.Y, r.X, r.Y, s.X, s.Y));
                }
            return d;
        }
        foreach (var k in plan.Margin)
        {
            Assert.NotEqual(plan.Cell, k.Face == plan.Cell.Face ? k.AncestorAt(WorkFrame.Level) : default);
            Assert.True(Dist(k) <= margin + 1, $"{k} is {Dist(k):F0} m out");
        }
        foreach (var k in wanted.Where(k => k.AncestorAt(WorkFrame.Level) != plan.Cell && !plan.Margin.Contains(k)))
            Assert.True(Dist(k) > margin - 1, $"{k} at {Dist(k):F0} m is left out of the margin");
    }

    [Fact]
    public void A_frames_query_box_holds_its_leaves_and_margin_in_each_sources_coordinates()
    {
        var wanted = Block(Geneva, 4);
        var driver = new FrameDriver(wanted, 500, SwissSeaLevel.Instance);
        foreach (var plan in driver.Frames)
            foreach (ISourceProjection source in new ISourceProjection[] { Lv95Source.Instance, new Lambert93Source(SwissSeaLevel.Instance), new GeographicSource(SwissSeaLevel.Instance) })
            {
                var (minA, minB, maxA, maxB) = plan.QueryBox(source);
                var (sa, sb) = LeafRegion.MetresPerUnit(source, plan.Cell.Centre.LatDeg, plan.Cell.Centre.LonDeg);
                foreach (var k in plan.Core.Concat(plan.Margin))
                    foreach (var (fu, fv) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) })
                    {
                        var g = CubeSphere.ToGeodetic(k.Face, k.U0 + fu * k.Size, k.V0 + fv * k.Size);
                        var (a, b) = source.FromLatLon(g.LatDeg, g.LonDeg);
                        Assert.InRange(a, minA, maxA);
                        Assert.InRange(b, minB, maxB);
                    }
                // the margin itself: a point 499 m out from the cell's south-west corner, along the outline's diagonal
                var c = plan.Outline[0];
                var (lat, lon) = plan.Frame.Plane.FromPlane(c.X - 499 / Math.Sqrt(2), c.Y - 499 / Math.Sqrt(2));
                var (ma, mb) = source.FromLatLon(lat, lon);
                Assert.True(ma >= minA - 1 / sa && ma <= maxA + 1 / sa && mb >= minB - 1 / sb && mb <= maxB + 1 / sb, $"{source.Name}: the margin is outside the box");
            }
    }

    // ---- leaf regions -----------------------------------------------------------------------------

    [Fact]
    public void A_leaf_region_touches_what_comes_within_its_margin_and_nothing_farther()
    {
        var leaves = Block(Geneva, 1);
        const double margin = 100;
        foreach (ISourceProjection source in new ISourceProjection[] { Lv95Source.Instance, new Lambert93Source(SwissSeaLevel.Instance), new GeographicSource(SwissSeaLevel.Instance) })
        {
            var region = new LeafRegion(leaves, source, margin);
            var (sa, sb) = LeafRegion.MetresPerUnit(source, Geneva.Centre.LatDeg, Geneva.Centre.LonDeg);
            // the block's outer edge middles, and points pushed straight out from the block's centre
            var c = Geneva.Centre;
            var (ca, cb) = source.FromLatLon(c.LatDeg, c.LonDeg);
            int inside = 0;
            foreach (var k in leaves.Where(k => k != Geneva))
            {
                var g = k.Centre;
                var (ka, kb) = source.FromLatLon(g.LatDeg, g.LonDeg);
                Assert.True(region.Touches(ka, kb), $"{source.Name}: a leaf's centre does not touch");
                inside++;
            }
            // a point on the block's outline, moved outward by the margin less and more a metre
            var edge = Geneva.Neighbour(1, 0)!.Value;
            var eg = CubeSphere.ToGeodetic(edge.Face, edge.U0 + edge.Size, edge.V0 + edge.Size / 2);
            var (ea, eb) = source.FromLatLon(eg.LatDeg, eg.LonDeg);
            // outward: away from the block's centre, in metres
            double dx = (ea - ca) * sa, dy = (eb - cb) * sb, len = Math.Sqrt(dx * dx + dy * dy);
            (dx, dy) = (dx / len, dy / len);
            double near = margin - 1, far = margin + LeafRegion.SlackM + 3;
            Assert.True(region.Touches(ea + dx * near / sa, eb + dy * near / sb), $"{source.Name}: {near} m out does not touch");
            Assert.False(region.Touches(ea + dx * far / sa, eb + dy * far / sb), $"{source.Name}: {far} m out touches");
            // a box straddling the block touches; one beyond the box's whole extent does not
            Assert.True(region.Touches(region.MinE, region.MaxE, region.MinN, region.MaxN));
            Assert.False(region.Touches(region.MaxE + 10 / sa, region.MaxE + 20 / sa, region.MinN, region.MaxN));
            // the same answers from many threads
            var boxes = Enumerable.Range(0, 2000).Select(i => (A: region.MinE + (region.MaxE - region.MinE) * ((i * 37) % 100) / 100.0, B: region.MinN + (region.MaxN - region.MinN) * ((i * 61) % 100) / 100.0)).ToArray();
            var one = boxes.Select(b => region.Touches(b.A, b.B)).ToArray();
            var many = new bool[boxes.Length];
            Parallel.For(0, boxes.Length, i => many[i] = region.Touches(boxes[i].A, boxes[i].B));
            Assert.Equal(one, many);
            _out.WriteLine($"{source.Name}: scale {sa:F4} / {sb:F4} m a unit, {inside} leaf centres touch, {one.Count(t => t)} of {one.Length} grid points");
        }
    }

    [Fact]
    public void The_box_and_polygon_distance_is_zero_inside_and_exact_outside()
    {
        double[] diamond = { 0, -10, 10, 0, 0, 10, -10, 0 };
        Assert.Equal(0, Geometry2.BoxPolygonDistance(-1, -1, 1, 1, diamond));
        Assert.Equal(0, Geometry2.BoxPolygonDistance(-100, -100, 100, 100, diamond));
        Assert.Equal(5, Geometry2.BoxPolygonDistance(15, -1, 20, 1, diamond), 9);
        // a box off the diamond's slanted side: the corner nearest it, to the side
        Assert.Equal(Math.Sqrt(2) * 5, Geometry2.BoxPolygonDistance(10, 10, 12, 12, diamond), 9);
    }

    // ---- the ground ------------------------------------------------------------------------------

    /// <summary>Leaves whose heights put a plane through the frame: hSea = 500 + 0.1·x − 0.05·y on the frame's plane.</summary>
    private static Dictionary<CubeKey, CubeTile> PlaneLeaves(WorkFrame frame, IEnumerable<CubeKey> keys)
    {
        var tiles = new Dictionary<CubeKey, CubeTile>();
        foreach (var key in keys)
        {
            var t = new LeafTransform(frame, key);
            int n = t.Cells + 1;
            var h = new float[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var (x, y) = t.LatticeToPlane(i, j);
                    h[j * n + i] = (float)(500 + 0.1 * x - 0.05 * y + t.UndulationAt(i, j));
                }
            tiles[key] = new CubeTile { Key = key, Bits = t.Bits, Heights = h };
        }
        return tiles;
    }

    [Fact]
    public void The_frame_ground_reads_the_leaf_mesh_and_finds_a_neighbour_at_an_edge()
    {
        var cell = Geneva.AncestorAt(WorkFrame.Level);
        var frame = new WorkFrame(cell, SwissSeaLevel.Instance);
        var keys = Block(Geneva, 1);
        var tiles = PlaneLeaves(frame, keys);
        var ground = new FrameGround(frame, k => tiles.GetValueOrDefault(k));
        var rng = new Random(1507);
        double worst = 0;
        var t = ground.TransformOf(Geneva)!;
        for (int k = 0; k < 3000; k++)
        {
            var (x, y) = t.LatticeToPlane(-400 + rng.NextDouble() * 1312, -400 + rng.NextDouble() * 1312);
            double want = 500 + 0.1 * x - 0.05 * y;
            var h = ground.Sample(x, y);
            Assert.True(h != null, $"no ground at ({x:F1}, {y:F1})");
            worst = Math.Max(worst, Math.Abs(h!.Value - want));
        }
        _out.WriteLine($"frame ground on a plane through 9 leaves: worst {worst * 1000:F3} mm");
        Assert.True(worst < 1e-3, $"the ground is {worst * 1000:F2} mm off the plane");

        // an edge with the leaf the point resolves to taken away: the neighbour answers, the same height
        var east = Geneva.EdgeNeighbour(0);
        var tEast = ground.TransformOf(east)!;
        var onEdge = Enumerable.Range(0, 50).Select(s => tEast.LatticeToPlane(0, s * 10.3)).ToList();
        var full = onEdge.Select(p => ground.Sample(p.X, p.Y)!.Value).ToList();
        foreach (var gone in new[] { Geneva, east })
        {
            var less = new FrameGround(frame, k => k == gone ? null : tiles.GetValueOrDefault(k));
            for (int s = 0; s < onEdge.Count; s++)
                Assert.Equal(full[s], less.Sample(onEdge[s].X, onEdge[s].Y)!.Value, 6);
        }
        // past every leaf: no ground
        var far = Block(Geneva, 3).Except(keys).First();
        var fc = far.Centre;
        var (fx, fy) = frame.Plane.ToPlane(fc.LatDeg, fc.LonDeg);
        Assert.Null(ground.Sample(fx, fy));
    }

    [Fact]
    public void Two_frames_grounds_agree_on_the_edge_between_them()
    {
        // two leaves either side of a frame edge, each read from its own frame
        var a = new CubeKey(0, Level, (Geneva.X | 31), Geneva.Y);
        var b = a.EdgeNeighbour(0);
        Assert.NotEqual(a.AncestorAt(WorkFrame.Level), b.AncestorAt(WorkFrame.Level));
        var fa = new WorkFrame(a.AncestorAt(WorkFrame.Level), SwissSeaLevel.Instance);
        var fb = new WorkFrame(b.AncestorAt(WorkFrame.Level), SwissSeaLevel.Instance);
        // one surface: heights from frame A's plane, for both leaves
        var tiles = PlaneLeaves(fa, new[] { a, b });
        var ga = new FrameGround(fa, k => tiles.GetValueOrDefault(k));
        var gb = new FrameGround(fb, k => tiles.GetValueOrDefault(k));
        var ta = ga.TransformOf(a)!;
        double worst = 0;
        for (int s = 0; s <= 100; s++)
        {
            var (xa, ya) = ta.LatticeToPlane(ta.Cells, s * 5.12);
            var (lat, lon) = fa.Plane.FromPlane(xa, ya);
            var (xb, yb) = fb.Plane.ToPlane(lat, lon);
            worst = Math.Max(worst, Math.Abs(ga.Sample(xa, ya)!.Value - gb.Sample(xb, yb)!.Value));
        }
        _out.WriteLine($"the same edge points from two frames: worst {worst * 1e6:F3} µm");
        Assert.True(worst < 1e-6);
    }

    [Fact]
    public void The_lv95_ground_finds_the_tile_west_or_south_of_a_boundary()
    {
        int size = ChunkFormat.GridSize;
        var heights = Enumerable.Repeat(ChunkFormat.Quantize(400), size * size).ToArray();
        var west = new TileId(2500, 1117);
        var grids = new Dictionary<TileId, ChunkGrid> { [west] = new ChunkGrid(west, heights, 400, 400) };
        var ground = new Lv95Ground(grids);
        double e = (west.E + 1) * ChunkFormat.TileSizeM, n = west.MinN + 300;
        Assert.NotNull(ground.Sample(e, n)); // on the boundary: the tile east of it is not loaded, the west one answers
        Assert.Null(ground.Sample(e + 1, n));
        Assert.Equal(ground.Sample(e - 10, n), TerrainSampler.For(grids)(e - 10, n));
    }

    // ---- the leaf writer and its seam pass -------------------------------------------------------

    [Fact]
    public void The_seam_pass_makes_every_shared_vertex_its_owners_across_frames_and_faces()
    {
        // leaves over a frame corner on face 0, and either side of face 0's edge with face 1
        var corner = new CubeKey(0, Level, Geneva.X & ~31, Geneva.Y & ~31);
        var edgeLeaf = new CubeKey(0, Level, (1 << Level) - 1, (1 << Level) - 1); // a cube corner: three faces
        var keys = Block(corner, 2).Concat(Block(edgeLeaf, 2)).ToHashSet();
        int bits = CubeKey.LatticeBits(Level), side = (1 << bits) + 1;
        var rng = new Random(9);
        var original = keys.ToDictionary(k => k, _ => { var c = new byte[side * side]; rng.NextBytes(c); return c; });
        string dir = Path.Combine(Path.GetTempPath(), "unitsport-seam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var driver = new FrameDriver(keys, 0, SwissSeaLevel.Instance);
            var writer = new LeafWriter(dir, WorkFrame.Level, new[] { LeafKind.Cover }, new[] { LatticeKind.Cover });
            long copied = 0, across = 0;
            foreach (var plan in driver.Frames)
            {
                var sections = plan.Core.ToDictionary(k => k, k => new List<LeafSection> { LeafCover.Encode((byte[])original[k].Clone(), bits) });
                foreach (var st in writer.WriteFrame(plan.Cell, sections))
                {
                    copied += st.Copied;
                    across += st.CopiedAcrossFrames;
                }
            }
            var manifest = LeafWriter.WriteManifest(dir, Level, WorkFrame.Level, new[] { "test" });
            Assert.Equal(keys.Count, manifest.Tiles.Count);
            Assert.Equal(new[] { "Cover" }, manifest.Kinds);
            Assert.All(manifest.Tiles, t => Assert.Equal(new[] { "Cover" }, t.Kinds));

            var stored = new Dictionary<CubeKey, byte[]>();
            foreach (var path in Directory.GetFiles(dir, "*.cubl"))
                foreach (var (key, bytes) in CubeBundle.ReadAllEntries(path))
                    stored[key] = LeafCover.Decode(CubeLeafCodec.ReadSection(bytes, LeafKind.Cover)!);
            Assert.Equal(keys.Count, stored.Count);
            var order = LeafSeam.FrameOrder(WorkFrame.Level);
            int shared = 0, corners3 = 0;
            foreach (var (key, cells) in stored)
                for (int b = 0; b < side; b++)
                    for (int a = 0; a < side; a++)
                    {
                        if (a != 0 && b != 0 && a != side - 1 && b != side - 1) continue;
                        var holders = LeafSeam.Holders(key, 1, a, b).Where(h => stored.ContainsKey(h.Key)).ToList();
                        if (holders.Count == 0)
                        {
                            Assert.Equal(original[key][b * side + a], cells[b * side + a]);
                            continue;
                        }
                        shared++;
                        if (holders.Count == 2 && LeafSeam.Holders(key, 1, a, b).Count() == 2) corners3++;
                        var owner = holders.Select(h => h.Key).Append(key).OrderBy(k => k, Comparer<CubeKey>.Create(order)).First();
                        var (oa, ob) = owner == key ? (a, b) : holders.First(h => h.Key == owner) is var o ? (o.A, o.B) : default;
                        byte want = original[owner][ob * side + oa];
                        Assert.Equal(want, cells[b * side + a]);
                        foreach (var h in holders) Assert.Equal(want, stored[h.Key][h.B * side + h.A]);
                    }
            // interiors untouched
            foreach (var (key, cells) in stored)
                Assert.Equal(original[key][side * (side / 2) + side / 2], cells[side * (side / 2) + side / 2]);
            _out.WriteLine($"{keys.Count} leaves in {driver.Frames.Count} frames: {shared} shared samples checked ({corners3} at a cube corner), {copied} copied, {across} across frames");
            Assert.True(across > 0);
            Assert.True(corners3 > 0);

            // a rerun of one frame alone keeps the seam: its earlier neighbours' values are on disk
            var last = driver.Frames[^1];
            var again = last.Core.ToDictionary(k => k, k => new List<LeafSection> { LeafCover.Encode((byte[])original[k].Clone(), bits) });
            writer.WriteFrame(last.Cell, again);
            foreach (var key in last.Core)
            {
                var path = writer.PathOf(last.Cell);
                var cells = LeafCover.Decode(CubeLeafCodec.ReadSection(CubeBundle.ReadBytes(path, key, CubeBundle.LeafMagic)!, LeafKind.Cover)!);
                Assert.Equal(stored[key], cells);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_seam_pass_leaves_exact_heights_alone_and_keeps_other_kinds()
    {
        var cell = Geneva.AncestorAt(WorkFrame.Level);
        var frame = new WorkFrame(cell, SwissSeaLevel.Instance);
        var keys = new[] { Geneva, Geneva.EdgeNeighbour(0), Geneva.EdgeNeighbour(1) };
        var tiles = PlaneLeaves(frame, keys);
        string dir = Path.Combine(Path.GetTempPath(), "unitsport-seam-" + Guid.NewGuid().ToString("N"));
        try
        {
            // a cover stage first, then the heights stage: the cover must survive bit for bit
            int bits = CubeKey.LatticeBits(Level), side = (1 << bits) + 1;
            var cover = new LeafWriter(dir, WorkFrame.Level, new[] { LeafKind.Cover });
            cover.WriteFrame(cell, keys.ToDictionary(k => k, k => new List<LeafSection> { LeafCover.Encode(new byte[side * side], bits) }));
            var heights = new LeafWriter(dir, WorkFrame.Level, new[] { LeafKind.Heights }, new[] { LatticeKind.Heights });
            var st = heights.WriteFrame(cell, keys.ToDictionary(k => k, k => new List<LeafSection> { LeafHeights.Encode(tiles[k]) })).Single();
            Assert.True(st.Samples > 0);
            Assert.Equal(0, st.Copied);
            var manifest = LeafWriter.WriteManifest(dir, Level, WorkFrame.Level, Array.Empty<string>());
            Assert.Equal(new[] { "Heights", "Cover" }.OrderBy(s => s), manifest.Kinds!.OrderBy(s => s));
            foreach (var t in manifest.Tiles) Assert.True(t.Max > t.Min);
            foreach (var key in keys)
            {
                var leaf = CubeBundle.ReadLeaf(heights.PathOf(cell), key)!;
                Assert.Equal(tiles[key].Heights, LeafHeights.Decode(leaf.Find(LeafKind.Heights)!).Heights.Select(h => (float)h).ToArray(), new ToleranceComparer(0.006f));
                Assert.NotNull(leaf.Find(LeafKind.Cover));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private sealed class ToleranceComparer(float tolerance) : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => Math.Abs(a - b) <= tolerance;
        public int GetHashCode(float f) => 0;
    }
}
