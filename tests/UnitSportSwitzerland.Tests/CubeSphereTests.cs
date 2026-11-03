using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The cube sphere (#1443, G1; tools/TerrainFormat/CubeSphere.cs, CubeKey.cs, EnuFrame.cs): its
/// fixed orientation, round trips, keys, neighbours across face edges and at the corners, and edge
/// vertices shared bit for bit.
/// </summary>
public class CubeSphereTests
{
    /// <summary>
    /// The eight cube corners, pinned: they are part of every tile's address and must never move.
    /// All are in the oceans (Natural Earth 1:110m), the nearest 151 km from a coast.
    /// </summary>
    public static readonly TheoryData<int, int, int, double, double> Corners = new()
    {
        { 1, 1, 1, 73.003, 134.946 },     // Laptev Sea
        { 1, 1, -1, 15.285, 61.689 },     // Arabian Sea
        { 1, -1, 1, 35.465, -66.550 },    // Sargasso Sea
        { 1, -1, -1, -6.459, -6.057 },    // Gulf of Guinea
        { -1, 1, 1, 6.459, 173.943 },     // central Pacific
        { -1, 1, -1, -35.465, 113.450 },  // Indian Ocean off Australia
        { -1, -1, 1, -15.285, -118.311 }, // south-east Pacific
        { -1, -1, -1, -73.003, -45.054 }, // Weddell Sea
    };

    [Theory]
    [MemberData(nameof(Corners))]
    public void The_orientation_puts_each_corner_where_it_was_chosen(int sx, int sy, int sz, double lat, double lon)
    {
        // the corner of the face whose axis is ±X, at u = ±1, v = ±1
        int face = sx > 0 ? 0 : 2;
        double u = sx > 0 ? sy : -sy;
        var g = CubeSphere.ToGeodetic(face, u, sz);
        Assert.Equal(lat, g.LatDeg, 3);
        Assert.Equal(lon, g.LonDeg, 3);
    }

    [Fact]
    public void Face_zero_is_centred_on_bern_and_the_orientation_is_a_rotation()
    {
        var g = CubeSphere.ToGeodetic(0, 0, 0);
        Assert.Equal(CubeSphere.CentreLatDeg, g.LatDeg, 9);
        Assert.Equal(CubeSphere.CentreLonDeg, g.LonDeg, 9);

        var m = CubeSphere.Orientation.ToArray();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double dot = m[i] * m[j] + m[3 + i] * m[3 + j] + m[6 + i] * m[6 + j];
                Assert.Equal(i == j ? 1.0 : 0.0, dot, 14);
            }
        double det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        Assert.Equal(1.0, det, 14);
    }

    [Fact]
    public void Lv95_origin_is_in_the_middle_of_face_zero()
    {
        var (lat, lon) = SwissGeodesy.ToLatLon(2_600_000, 1_200_000);
        var (face, u, v) = CubeSphere.FromLatLon(lat, lon);
        Assert.Equal(0, face);
        Assert.InRange(Math.Abs(u), 0, 1e-6);
        Assert.InRange(Math.Abs(v), 0, 1e-6);
        Assert.Equal(0, CubeKey.FromLatLon(lat, lon, 13).Face);
    }

    [Fact]
    public void Round_trips_stay_under_a_tenth_of_a_millimetre_everywhere()
    {
        var rng = new Random(1443);
        double worst = 0;
        for (int k = 0; k < 200_000; k++)
        {
            int face = rng.Next(6);
            // a quarter of the samples on edges and corners, where the face choice flips
            double u = Pick(rng), v = Pick(rng), h = rng.NextDouble() * 10_000 - 500;
            var p = CubeSphere.ToEcef(face, u, v, h);
            var (f2, u2, v2, h2) = CubeSphere.FromEcef(p);
            worst = Math.Max(worst, CubeSphere.ToEcef(f2, u2, v2, h2).DistanceTo(p));
        }
        Assert.InRange(worst, 0, 1e-4);

        static double Pick(Random r) => r.Next(4) switch
        {
            0 => r.Next(2) == 0 ? -1 : 1,
            _ => r.NextDouble() * 2 - 1,
        };
    }

    [Fact]
    public void Poles_and_the_antimeridian_are_ordinary_points()
    {
        foreach (var (lat, lon) in new[] { (90.0, 0.0), (-90.0, 0.0), (0.0, 180.0), (0.0, -180.0), (89.9999, 45.0), (-12.0, 179.99999) })
        {
            var p = Ellipsoid.Wgs84.ToEcef(new Geodetic(lat, lon, 100));
            var (f, u, v, h) = CubeSphere.FromEcef(p);
            Assert.InRange(CubeSphere.ToEcef(f, u, v, h).DistanceTo(p), 0, 1e-4);
            var key = CubeKey.FromEcef(p, 13);
            Assert.True(key.IsValid);
        }
        // the North Pole is inside face 0, the South Pole inside face 2
        var (fn, un, vn) = CubeSphere.FromLatLon(90, 0);
        Assert.Equal(0, fn);
        Assert.Equal(0.53, un, 2);
        Assert.Equal(0.88, vn, 2);
        Assert.Equal(2, CubeSphere.FromLatLon(-90, 0).Face);
    }

    [Fact]
    public void Keys_print_parse_and_describe()
    {
        var k = new CubeKey(0, 13, 4123, 4091);
        Assert.Equal("F0/L13/4123_4091", k.ToString());
        Assert.Equal(k, CubeKey.Parse("F0/L13/4123_4091"));
        Assert.Equal(k, CubeKey.Parse(k.FileStem));
        Assert.Matches(@"^F0/L13/4123_4091 \(4\d\.\d{5}°N \d\.\d{5}°E\)$", k.Describe());
        Assert.Throws<FormatException>(() => CubeKey.Parse("F6/L13/0_0"));
        Assert.Throws<FormatException>(() => CubeKey.Parse("F0/L2/4_0"));
    }

    [Fact]
    public void A_point_falls_in_the_cell_that_holds_it_and_its_ancestors()
    {
        var rng = new Random(7);
        for (int k = 0; k < 20_000; k++)
        {
            double lat = Math.Asin(rng.NextDouble() * 2 - 1) * 180 / Math.PI, lon = rng.NextDouble() * 360 - 180;
            var key = CubeKey.FromLatLon(lat, lon, 14);
            var (f, u, v) = CubeSphere.FromLatLon(lat, lon);
            Assert.Equal(f, key.Face);
            Assert.InRange(u, key.U0 - 1e-12, key.U0 + key.Size + 1e-12);
            Assert.InRange(v, key.V0 - 1e-12, key.V0 + key.Size + 1e-12);
            Assert.Equal(CubeKey.FromLatLon(lat, lon, 9), key.AncestorAt(9));
            Assert.Contains(key, key.Parent.Children());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Neighbours_are_mutual_and_number_eight_except_at_the_corners(int level)
    {
        int corners = 0;
        foreach (var key in AllKeys(level))
        {
            var around = key.Neighbours();
            Assert.Equal(around.Count, around.Distinct().Count());
            Assert.DoesNotContain(key, around);
            foreach (var other in around)
                Assert.Contains(key, other.Neighbours());
            if (level == 0) { Assert.Equal(4, around.Count); continue; }
            if (around.Count == 7) corners++;
            else Assert.Equal(8, around.Count);
        }
        // three cells round each of the cube's eight corners
        if (level > 0) Assert.Equal(24, corners);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void Edge_neighbours_share_their_two_corners_bit_for_bit(int level)
    {
        foreach (var key in AllKeys(level))
            for (int side = 0; side < 4; side++)
            {
                var other = key.EdgeNeighbour(side);
                var mine = CornersOf(key);
                var theirs = CornersOf(other);
                Assert.Equal(2, mine.Count(theirs.Contains));
                // and the way back is the way here
                Assert.Contains(key, Enumerable.Range(0, 4).Select(other.EdgeNeighbour));
            }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Three_cells_meet_at_each_cube_corner_and_touch_each_other(int level)
    {
        int n = 1 << level;
        foreach (var key in AllKeys(level).Where(k => k.Neighbours().Count == 7))
        {
            Assert.True((key.X == 0 || key.X == n - 1) && (key.Y == 0 || key.Y == n - 1));
            // the two cells across its two face edges touch each other too
            int dx = key.X == 0 ? -1 : 1, dy = key.Y == 0 ? -1 : 1;
            var a = key.Neighbour(dx, 0)!.Value;
            var b = key.Neighbour(0, dy)!.Value;
            Assert.NotEqual(a.Face, b.Face);
            Assert.Contains(b, a.Neighbours());
            // and all three hold the cube corner, the same point to the bit
            var shared = CornersOf(key).Intersect(CornersOf(a)).Intersect(CornersOf(b)).ToList();
            Assert.Single(shared);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(23)]
    public void Edge_vertices_are_the_same_point_from_every_face_that_holds_them(int level)
    {
        long n = 1L << level;
        int steps = (int)Math.Min(n, 512);
        int shared = 0;
        for (int face = 0; face < 6; face++)
            for (int s = 0; s <= steps; s++)
            {
                long t = s * n / steps;
                foreach (var (i, j) in new[] { (0L, t), (n, t), (t, 0L), (t, n) })
                {
                    var here = CubeSphere.LatticeEcef(face, level, i, j, 1234.5);
                    for (int other = 0; other < 6; other++)
                    {
                        if (other == face || CubeSphere.LatticeOn(face, level, i, j, other) is not { } there) continue;
                        Assert.Equal(here, CubeSphere.LatticeEcef(other, level, there.I, there.J, 1234.5));
                        shared++;
                    }
                }
            }
        Assert.True(shared > 0);
    }

    [Fact]
    public void Cell_sizes_stay_within_the_equal_angle_spread()
    {
        // level 13: 1.22 km a side at a face centre; at a corner 1.16 km sides meeting at 120°
        // (three cells round it); the shortest side, 0.87 km (1/√2), across the middle of a face edge
        double centre = SideU(new CubeKey(0, 13, 4096, 4096)), corner = SideU(new CubeKey(0, 13, 8191, 8191));
        double midEdge = SideV(new CubeKey(0, 13, 8191, 4096));
        Assert.InRange(centre, 1200, 1240);
        Assert.InRange(corner, 1130, 1180);
        Assert.InRange(midEdge, 850, 880);
        Assert.InRange(centre / midEdge, 1.40, 1.42);

        static double SideU(CubeKey k) => k.Vertex(0, 0, 0, 0).DistanceTo(k.Vertex(0, 1, 0, 0));
        static double SideV(CubeKey k) => k.Vertex(0, 0, 0, 0).DistanceTo(k.Vertex(0, 0, 1, 0));
    }

    [Fact]
    public void The_anchor_frame_is_east_north_up_at_the_centre()
    {
        var key = CubeKey.FromLatLon(46.2, 6.15, 13); // Geneva
        var a = key.Anchor;
        var c = key.Centre;
        Assert.InRange(a.Origin.DistanceTo(Ellipsoid.Wgs84.ToEcef(c)), 0, 1e-6);
        // up is the geodetic normal at the centre
        var n = CubeSphere.NormalAt(c.LatDeg, c.LonDeg);
        Assert.InRange(new EcefPos(a.Up.X - n.X, a.Up.Y - n.Y, a.Up.Z - n.Z).Length, 0, 1e-12);

        var p = key.Vertex(10, 37, 900, 420);
        var (x, y, z) = a.ToLocal(p);
        Assert.InRange(a.ToEcef(x, y, z).DistanceTo(p), 0, 1e-6);
        // within the tile: under ~1 km out, and the 420 m height mostly up
        Assert.InRange(Math.Sqrt(x * x + z * z), 0, 1000);
        Assert.InRange(y, 400, 421);
    }

    private static IEnumerable<CubeKey> AllKeys(int level)
    {
        int n = 1 << level;
        for (int f = 0; f < 6; f++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    yield return new CubeKey(f, level, x, y);
    }

    private static HashSet<EcefPos> CornersOf(CubeKey k) => new()
    {
        k.Vertex(0, 0, 0, 0), k.Vertex(0, 1, 0, 0), k.Vertex(0, 0, 1, 0), k.Vertex(0, 1, 1, 0),
    };
}
