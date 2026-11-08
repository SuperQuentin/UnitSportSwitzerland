using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The synthetic driving courses (src/Terrain/Fixture, linked in): the tiles they serve must be
/// valid data the game reads like real tiles.
/// </summary>
public class FixtureCourseTests
{
    private const double StartE = 2583250, StartN = 1113250;

    public static IEnumerable<object[]> Courses => FixtureCourse.Names.Select(n => new object[] { n });

    [Fact]
    public void Unknown_course_is_null() => Assert.Null(FixtureCourse.Create("mollendruz"));

    [Fact]
    public void Split_pieces_stay_in_their_tile_and_share_ends()
    {
        // a diagonal across four tiles, one point exactly on a tile corner
        var line = new List<(double E, double N, double Z)> { (2583900, 1113900, 10), (2584000, 1114000, 11), (2584600, 1114500, 12), (2582500, 1112500, 13) };
        var pieces = FixtureChunkSource.SplitAtTiles(line);
        Assert.True(pieces.Count >= 4);
        for (int i = 0; i < pieces.Count; i++)
        {
            var (tile, piece) = pieces[i];
            Assert.All(piece, p => Assert.InRange(p.E, tile.MinE - 1e-6, tile.MinE + 1000 + 1e-6));
            Assert.All(piece, p => Assert.InRange(p.N, tile.MinN - 1e-6, tile.MaxN + 1e-6));
            if (i > 0) Assert.Equal(pieces[i - 1].Piece[^1], piece[0]);
        }
        Assert.Equal(line[0], pieces[0].Piece[0]);
        Assert.Equal(line[^1], pieces[^1].Piece[^1]);
    }

    [Theory]
    [MemberData(nameof(Courses))]
    public async Task Tiles_are_valid_and_the_ground_follows_the_road(string name)
    {
        var source = FixtureChunkSource.Create(name, StartE, StartN)!;
        var manifest = await source.LoadManifestAsync();
        Assert.Equal(source.Tiles.Count, manifest.Tiles.Count);
        Assert.Contains(TileId.FromLv95(StartE, StartN), source.Tiles);
        Assert.Equal(StartE, manifest.SuggestedOriginLv95.E);

        int roadPoints = 0;
        foreach (var id in source.Tiles)
        {
            var grid = (await source.LoadChunkAsync(id))!;
            var coarse = (await source.LoadCoarseChunkAsync(id))!;
            Assert.Equal(ChunkFormat.GridSize, grid.Size);
            // the coarse tile is the full one decimated, as the real companions are
            Assert.Equal(grid.HeightAt(500, 300), coarse.HeightAt(500, 300));
            Assert.NotNull(await source.LoadBuildingsAsync(id));
            Assert.NotNull(await source.LoadTreesAsync(id));
            foreach (var seg in (await source.LoadRoadsAsync(id))!.Segments)
                for (int i = 0; i < seg.PointCount; i++)
                {
                    float x = seg.Points[i * 3], y = seg.Points[i * 3 + 1], z = seg.Points[i * 3 + 2];
                    Assert.InRange(x, -1e-3f, 1000.001f);
                    Assert.InRange(z, -1e-3f, 1000.001f);
                    // the collision blend pulls the ground to the road within 3 m; it must not have to pull far
                    Assert.InRange(grid.SampleHeight(id.MinE + x, id.MaxN - z) - y, -2.0, 2.0);
                    roadPoints++;
                }
        }
        Assert.Equal(name is not ("flat" or "crag"), roadPoints > 0);   // the crag (#1225) has no roads either
        Assert.Null(await source.LoadChunkAsync(new TileId(0, 0)));
    }

    [Fact]
    public void Hairpin_goes_downhill_at_seven_percent()
    {
        var road = FixtureCourse.Create("hairpin")!.Roads.Single().Points;
        double length = 0;
        for (int i = 1; i < road.Count; i++)
            length += Math.Sqrt(Math.Pow(road[i].X - road[i - 1].X, 2) + Math.Pow(road[i].Y - road[i - 1].Y, 2));
        Assert.InRange(length, 2800, 3200);
        Assert.Equal(-0.07 * length, road[^1].Z - road[0].Z, 1);
        // six legs: the road comes back past the start's meridian five times
        int crossings = 0;
        for (int i = 1; i < road.Count; i++)
            if ((road[i - 1].X < 225) != (road[i].X < 225)) crossings++;
        Assert.Equal(6, crossings);
    }

    // ---- the same courses as cube leaves (#1509, G2 B5) ------------------------------------------

    [Theory]
    [MemberData(nameof(Courses))]
    public void Leaves_are_valid_and_the_ground_follows_the_road(string name)
    {
        var source = FixtureChunkSource.Create(name, StartE, StartN)!;
        var leaves = FixtureLeaves.At(source);
        Assert.Contains(leaves.StartLeaf, leaves.Leaves);
        Assert.All(leaves.Leaves, k => Assert.Equal(LeafFrames.LeafLevel, k.Level));

        // the start's leaf, and every leaf a road crosses (the lake's and the airport's courses have dozens of leaves)
        var withRoads = leaves.RoadLeaves.ToList();
        int roadPoints = 0;
        foreach (var key in withRoads.Append(leaves.StartLeaf).Distinct())
        {
            var leaf = leaves.Build(key)!;
            var back = CubeLeafContent.Decode(leaf.Encode());
            Assert.Empty(back.Skipped);
            Assert.Equal(leaf.Roads!.Segments.Count, back.Roads!.Segments.Count);
            Assert.Equal(leaf.Buildings!.Buildings.Count, back.Buildings!.Buildings.Count);
            Assert.Equal(leaf.Trees!.Count, back.Trees!.Count);
            Assert.DoesNotContain(leaf.Heights!.Heights, float.IsNaN);

            var t = LeafFrames.Swiss.TransformOf(key);
            // a ski lift is a cable: cut at a leaf edge, its straight line stands over the hill (the fun course's)
            foreach (var seg in leaf.Roads.Segments.Where(s => s.Class != RoadClass.SkiLift))
                for (int i = 0; i < seg.PointCount; i++)
                {
                    var (li, lj, hEll) = t.LocalToLattice(seg.Points[i * 3], seg.Points[i * 3 + 1], seg.Points[i * 3 + 2]);
                    // in its leaf, the cut points on its edges
                    Assert.InRange(li, -1e-3, t.Cells + 1e-3);
                    Assert.InRange(lj, -1e-3, t.Cells + 1e-3);
                    // the collision blend pulls the ground to the road within 3 m; it must not have to pull far
                    Assert.InRange(MeshHeight(leaf.Heights, li, lj) - hEll, -2.0, 2.0);
                    roadPoints++;
                }
        }
        Assert.Equal(name is not ("flat" or "crag"), roadPoints > 0);
        Assert.Null(leaves.Build(new CubeKey(0, LeafFrames.LeafLevel, 0, 0)));
    }

    [Fact]
    public void The_start_stands_on_the_course_s_ground_at_its_latitude_and_longitude()
    {
        var source = FixtureChunkSource.Create("hairpin", StartE, StartN)!;
        var leaves = FixtureLeaves.At(source);
        var on = new SourceOnLeaf(LeafFrames.Swiss, leaves.StartLeaf, leaves.Source);
        var (x, y, z) = on.Local(0, 0, leaves.Course.Ground(0, 0));
        var (i, j, hEll) = on.Transform.LocalToLattice(x, y, z);
        // the leaf's ground is the course's every ~10 m, bilinear between, as the tile source's lattice (15 cm off here)
        Assert.InRange(MeshHeight(leaves.Build(leaves.StartLeaf)!.Heights!, i, j) - hEll, -0.3, 0.3);
        // and the start is where its LV95 point is
        var (lat, lon) = Lv95Source.Instance.ToLatLon(StartE, StartN);
        Assert.Equal(lat, leaves.Source.StartLatDeg, 12);
        Assert.Equal(lon, leaves.Source.StartLonDeg, 12);
    }

    [Fact]
    public void A_course_placed_elsewhere_keeps_its_metres()
    {
        // the hairpin's 3 km road at Scuol, 180 km from its LV95 start: its pieces' lengths in the leaves are the course's
        // (inside Switzerland: the sea level is LHN95's until G3)
        var source = FixtureChunkSource.Create("hairpin", StartE, StartN)!;
        var leaves = new FixtureLeaves(source, 46.80, 10.30);
        double inLeaves = 0;
        foreach (var key in leaves.Leaves)
            foreach (var seg in leaves.Build(key)!.Roads!.Segments)
                for (int i = 1; i < seg.PointCount; i++)
                {
                    double dx = seg.Points[i * 3] - seg.Points[i * 3 - 3], dy = seg.Points[i * 3 + 1] - seg.Points[i * 3 - 2];
                    double dz = seg.Points[i * 3 + 2] - seg.Points[i * 3 - 1];
                    inLeaves += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
        var road = source.Course.Roads.Single().Points;
        double course = 0;
        for (int i = 1; i < road.Count; i++)
            course += Math.Sqrt(Math.Pow(road[i].X - road[i - 1].X, 2) + Math.Pow(road[i].Y - road[i - 1].Y, 2) + Math.Pow(road[i].Z - road[i - 1].Z, 2));
        // true at the course's height (2×10⁻⁴ long without: the hairpin runs 1,000-1,200 m up)
        Assert.Equal(course, inLeaves, course * 2e-5);
        Assert.Equal(46.80, LeafFrames.Swiss.FrameOf(leaves.StartLeaf).Cell.Centre.LatDeg, 0);
    }

    [Fact]
    public void Leaf_pieces_stay_in_their_leaf_and_share_ends()
    {
        // a line of 3 km across several leaves, one leg doubling back
        var course = new CourseSource(46.2, 7.2);
        var line = new List<(double A, double B, double Z)> { (-400, -400, 10), (900, 1300, 11), (1500, 2100, 12), (-1200, 300, 13) };
        var pieces = LeafSplit.AtLeaves(course, line);
        Assert.True(pieces.Count >= 4);
        for (int i = 0; i < pieces.Count; i++)
        {
            var (leaf, piece) = pieces[i];
            for (int k = 1; k < piece.Count; k++)
                Assert.Equal(leaf, SourceOnLeaf.LeafOf(course, (piece[k - 1].A + piece[k].A) / 2, (piece[k - 1].B + piece[k].B) / 2));
            if (i > 0) Assert.Equal(pieces[i - 1].Piece[^1], piece[0]);
        }
        Assert.Equal(line[0], pieces[0].Piece[0]);
        Assert.Equal(line[^1], pieces[^1].Piece[^1]);
    }

    /// <summary>A leaf's ground at a fractional lattice point, bilinear (the mesh's triangles are within a few cm of it here).</summary>
    private static double MeshHeight(CubeTile tile, double i, double j)
    {
        int n = (1 << tile.Bits) + 1;
        int i0 = Math.Clamp((int)i, 0, n - 2), j0 = Math.Clamp((int)j, 0, n - 2);
        double u = i - i0, v = j - j0;
        var h = tile.Heights;
        double south = h[i0 + j0 * n] + (h[i0 + 1 + j0 * n] - h[i0 + j0 * n]) * u;
        double north = h[i0 + (j0 + 1) * n] + (h[i0 + 1 + (j0 + 1) * n] - h[i0 + (j0 + 1) * n]) * u;
        return south + (north - south) * v;
    }
}
