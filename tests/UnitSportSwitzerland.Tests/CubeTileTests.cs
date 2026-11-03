using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The cube tile format (#1443, G1; tools/TerrainFormat/CubeTile.cs, CubeBundle.cs, CubeGeometry.cs):
/// the height encoding against 1 cm, features and bundles round-tripping, and the mesh vertices
/// against their rigorous ECEF points.
/// </summary>
public class CubeTileTests
{
    private static float[] Alpine(int bits, int seed, bool holes = false)
    {
        int n = (1 << bits) + 1;
        var rng = new Random(seed);
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                // a 3,000 m wall, slopes and 1 m-scale noise: worse than any real 1.2 km tile
                double x = i / (double)(n - 1), y = j / (double)(n - 1);
                h[j * n + i] = (float)(1200 + 3000 * x * x + 300 * Math.Sin(9 * y) + rng.NextDouble() * 1.7);
                if (holes && i > n / 2 && j < n / 3) h[j * n + i] = float.NaN;
            }
        return h;
    }

    [Fact]
    public void Heights_come_back_within_half_a_centimetre_whatever_the_range()
    {
        var key = CubeKey.FromLatLon(45.97, 7.66, 13);
        var t = new CubeTile { Key = key, Bits = 8, Heights = Alpine(8, 1, holes: true) };
        var back = CubeTileCodec.Decode(CubeTileCodec.Encode(t));
        Assert.Equal(key, back.Key);
        Assert.Equal(8, back.Bits);
        double worst = 0;
        for (int k = 0; k < t.Heights.Length; k++)
        {
            Assert.Equal(float.IsNaN(t.Heights[k]), float.IsNaN(back.Heights[k]));
            if (!float.IsNaN(t.Heights[k])) worst = Math.Max(worst, Math.Abs(back.Heights[k] - t.Heights[k]));
        }
        // half the quantum, plus the float step at 4,200 m (0.24 mm)
        Assert.InRange(worst, 0, 0.0053);
    }

    [Fact]
    public void Negative_heights_and_an_empty_tile_round_trip()
    {
        var key = new CubeKey(2, 13, 0, 8191);
        var deep = new float[17 * 17];
        for (int k = 0; k < deep.Length; k++) deep[k] = -10_900f + k * 0.37f; // the Mariana Trench's floor
        var back = CubeTileCodec.Decode(CubeTileCodec.Encode(new CubeTile { Key = key, Bits = 4, Heights = deep }));
        for (int k = 0; k < deep.Length; k++) Assert.Equal(deep[k], back.Heights[k], 0.006);

        var none = Enumerable.Repeat(float.NaN, 17 * 17).ToArray();
        back = CubeTileCodec.Decode(CubeTileCodec.Encode(new CubeTile { Key = key, Bits = 4, Heights = none }));
        Assert.All(back.Heights, h => Assert.True(float.IsNaN(h)));
    }

    [Fact]
    public void Features_and_the_header_round_trip()
    {
        var key = new CubeKey(0, 14, 8200, 8170);
        var t = new CubeTile
        {
            Key = key, Bits = 2, Heights = Enumerable.Range(0, 25).Select(k => 400f + k).ToArray(),
            Roads = { new CubeRoad(CubeSource.BdTopo, RoadClass.Major, 6.5f, new[] { 1f, 2f, 3f, 4f, 5f, 6f }) },
            Buildings = { new CubeBuilding(CubeSource.SwissTlm, 12.5f, new[] { 0f, 400f, 0f, 10f, 401f, 0f, 10f, 401f, 8f }) },
        };
        byte[] bytes = CubeTileCodec.Encode(t);
        var (k2, bits, min, max) = CubeTileCodec.ReadHeader(bytes);
        Assert.Equal(key, k2);
        Assert.Equal(2, bits);
        Assert.Equal(400f, min);
        Assert.Equal(424f, max);

        var back = CubeTileCodec.Decode(bytes);
        Assert.Equal(RoadClass.Major, back.Roads[0].Class);
        Assert.Equal(CubeSource.BdTopo, back.Roads[0].Source);
        Assert.Equal(t.Roads[0].Points, back.Roads[0].Points);
        Assert.Equal(12.5f, back.Buildings[0].Height);
        Assert.Equal(t.Buildings[0].Points, back.Buildings[0].Points);
    }

    [Fact]
    public void A_bundle_holds_its_tiles_and_finds_each_one()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cubebundle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bundle = new CubeKey(0, 10, 512, 511);
            var tiles = bundle.Children().SelectMany(c => c.Children()).Select((k, n) =>
                (k, CubeTileCodec.Encode(new CubeTile { Key = k, Bits = 3, Heights = Enumerable.Repeat(100f + n, 81).ToArray() }))).ToList();
            string path = Path.Combine(dir, CubeBundle.FileName(bundle));
            CubeBundle.Write(path, bundle, tiles);
            Assert.Equal("F0_L10_512_511.cubb", Path.GetFileName(path));
            Assert.Equal(16, CubeBundle.ReadAll(path).Count());
            var third = CubeBundle.Read(path, tiles[3].k);
            Assert.NotNull(third);
            Assert.Equal(103f, third!.Heights[40], 0.006);
            Assert.Null(CubeBundle.Read(path, new CubeKey(0, 12, 0, 0)));
            Assert.Throws<ArgumentException>(() => CubeBundle.Write(path, bundle, new[] { (new CubeKey(1, 12, 2048, 2044), tiles[0].Item2) }));
        }
        finally { Directory.Delete(dir, true); }
    }

    public static readonly TheoryData<string, double, double, int> Places = new()
    {
        { "Bern", 46.95, 7.44, 13 },
        { "Matterhorn", 45.976, 7.658, 13 },
        { "Brest", 48.39, -4.49, 14 },
        { "a cube corner (Laptev Sea)", 73.0, 134.9, 13 },
        { "the North Pole", 89.99, 10, 13 },
        { "the middle of a face edge", 1.0, 52.0, 13 },
    };

    [Theory]
    [MemberData(nameof(Places))]
    public void Every_vertex_is_within_a_centimetre_of_its_rigorous_point(string place, double lat, double lon, int level)
    {
        int bits = level == 13 ? 10 : 9, n = (1 << bits) + 1;
        var key = CubeKey.FromLatLon(lat, lon, level);
        var heights = new float[n * n];
        var rng = new Random(place.Length);
        for (int k = 0; k < heights.Length; k++) heights[k] = (float)(rng.NextDouble() * 4800 - 50);
        var v = CubeGeometry.Vertices(key, bits, heights);
        double worst = 0;
        for (int j = 0; j < n; j += 7)
            for (int i = 0; i < n; i += 5)
            {
                var (x, y, z) = CubeGeometry.Exact(key, bits, i, j, heights[j * n + i]);
                int o = (j * n + i) * 3;
                double d = Math.Sqrt(Sq(v[o] - x) + Sq(v[o + 1] - y) + Sq(v[o + 2] - z));
                worst = Math.Max(worst, d);
            }
        // measured: 0.3 mm at most (the float step at 4.8 km up); the target is 1 cm
        Assert.InRange(worst, 0, 0.001);

        static double Sq(double a) => a * a;
    }

    [Fact]
    public void The_mesh_height_below_a_vertex_is_that_vertex()
    {
        int bits = 6, n = 65;
        var key = CubeKey.FromLatLon(46.5, 7.9, 13);
        var heights = Alpine(bits, 3);
        var v = CubeGeometry.Vertices(key, bits, heights);
        var hint = (0, 0);
        for (int j = 1; j < n - 1; j += 3)
            for (int i = 1; i < n - 1; i += 3)
            {
                int o = (j * n + i) * 3;
                Assert.Equal(v[o + 1], CubeGeometry.MeshHeightAt(v, n, v[o], v[o + 2], ref hint), 0.01);
            }
        Assert.True(double.IsNaN(CubeGeometry.MeshHeightAt(v, n, 5000, 5000, ref hint)));
    }
}
