using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The cube format's coarse levels (#1430 H4, agreed on #1455): the lattice cap, rigorous vertices
/// where a tile spans kilometres a cell, the per-level manifest and the colour bundles.
/// </summary>
public class CubePyramidTests
{
    [Theory]
    [InlineData(14, 9)]
    [InlineData(15, 8)]
    [InlineData(13, 9)]
    [InlineData(4, 9)]
    [InlineData(0, 9)]
    public void The_lattice_keeps_the_leaves_and_caps_the_levels_above(int level, int bits) =>
        Assert.Equal(bits, CubeKey.LatticeBits(level));

    [Fact]
    public void A_coarse_lattice_point_is_the_leaf_lattice_point_to_the_bit()
    {
        var coarse = CubeKey.FromLatLon(46.95, 7.44, 2);
        int bits = CubeKey.LatticeBits(2), lv = coarse.Level + bits, shift = 23 - lv;
        foreach (var (i, j) in new[] { (0, 0), (512, 0), (0, 512), (512, 512), (131, 377) })
        {
            var p = coarse.Vertex(bits, i, j, -4200);
            var leaf = CubeSphere.LatticeEcef(coarse.Face, 23, (((long)coarse.X << bits) + i) << shift, (((long)coarse.Y << bits) + j) << shift, -4200);
            Assert.Equal(leaf, p);
        }
    }

    [Theory]
    [InlineData(14, 9, 32)]
    [InlineData(13, 10, 64)]
    [InlineData(13, 9, 32)]
    [InlineData(10, 9, 4)]
    [InlineData(4, 9, 1)]
    [InlineData(0, 9, 1)]
    public void Control_points_never_span_more_than_a_hundred_metres(int level, int bits, int step) =>
        Assert.Equal(step, CubeGeometry.ControlStep(level, bits));

    [Theory]
    [InlineData(0, 46.95, 7.44)]
    [InlineData(2, -45.0, 170.0)]
    [InlineData(4, 0.5, -30.0)]
    public void A_coarse_tile_s_vertices_are_its_rigorous_points(int level, double lat, double lon)
    {
        var key = CubeKey.FromLatLon(lat, lon, level);
        int bits = CubeKey.LatticeBits(level), n = (1 << bits) + 1;
        var heights = new float[n * n];
        var rng = new Random(level);
        for (int k = 0; k < heights.Length; k++) heights[k] = (float)(rng.NextDouble() * 15000 - 10000);
        var v = CubeGeometry.Vertices(key, bits, heights);
        // the float step 5,000 km from the anchor is 0.5 m; the leaves' 32-vertex control span would be
        // off by the ellipsoid's sag over 627 km at level 0 (kilometres)
        double tolerance = level == 0 ? 1.0 : 0.25, worst = 0;
        for (int j = 0; j < n; j += 17)
            for (int i = 0; i < n; i += 13)
            {
                var (x, y, z) = CubeGeometry.Exact(key, bits, i, j, heights[j * n + i]);
                int o = (j * n + i) * 3;
                worst = Math.Max(worst, Math.Sqrt((v[o] - x) * (v[o] - x) + (v[o + 1] - y) * (v[o + 1] - y) + (v[o + 2] - z) * (v[o + 2] - z)));
            }
        Assert.InRange(worst, 0, tolerance);
    }

    [Fact]
    public void A_coarse_tile_keeps_its_heights_to_half_its_own_quantum()
    {
        Assert.Equal(0.01f, CubeKey.QuantumFor(14));
        Assert.Equal(0.01f, CubeKey.QuantumFor(13));
        Assert.Equal(1f, CubeKey.QuantumFor(4));
        Assert.Equal(1f, CubeKey.QuantumFor(0));

        var key = CubeKey.FromLatLon(11.35, 142.2, 4); // the Mariana Trench
        int bits = CubeKey.LatticeBits(4), n = (1 << bits) + 1;
        var h = new float[n * n];
        var rng = new Random(4);
        for (int k = 0; k < h.Length; k++) h[k] = (float)(-10900 + rng.NextDouble() * 19800);
        var back = CubeTileCodec.Decode(CubeTileCodec.Encode(new CubeTile { Key = key, Bits = bits, Quantum = CubeKey.QuantumFor(4), Heights = h }));
        Assert.Equal(1f, back.Quantum);
        for (int k = 0; k < h.Length; k += 97) Assert.InRange(Math.Abs(back.Heights[k] - h[k]), 0, 0.5001);
    }

    [Fact]
    public void A_version_one_manifest_is_its_leaf_level_alone()
    {
        var old = CubeManifest.FromJson("""{"version":1,"leafLevel":14,"bits":9,"bundleLevel":9,"tiles":[]}""");
        var leaf = Assert.Single(old.LevelsOrLeaf);
        Assert.Equal((14, 9, 9), (leaf.Level, leaf.Bits, leaf.BundleLevel));
        Assert.Null(old.LevelOf(3));

        var pyramid = new CubeManifest { LeafLevel = 4, Bits = 9, BundleLevel = 0 };
        for (int l = 0; l <= 4; l++)
            pyramid.Levels.Add(new CubeManifestLevel { Level = l, Bits = CubeKey.LatticeBits(l), BundleLevel = Math.Max(0, l - 2), Albedo = l <= 2 ? 256 : 512, AlbedoBundleLevel = Math.Max(0, l - 3) });
        var back = CubeManifest.FromJson(pyramid.ToJson());
        Assert.Equal(CubeManifest.CurrentVersion, back.Version);
        Assert.Equal(5, back.LevelsOrLeaf.Count);
        Assert.Equal(512, back.LevelOf(3)!.Albedo);
    }

    [Fact]
    public void Colour_round_trips_in_a_bundle_of_its_own()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cubealbedo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bundle = new CubeKey(3, 1, 1, 0);
            var entries = bundle.Children().Select((k, n) => (k, CubeAlbedoCodec.Encode(new CubeAlbedo
            {
                Key = k,
                Size = 256,
                Months = Enumerable.Range(1, 12).Select(m => new byte[] { (byte)n, (byte)m, 0xAB }).ToArray(),
            }))).ToList();
            string path = Path.Combine(dir, CubeBundle.AlbedoFileName(bundle));
            CubeBundle.Write(path, bundle, entries, CubeBundle.AlbedoMagic);
            Assert.Equal("F3_L1_1_0.cuba", Path.GetFileName(path));

            var bytes = CubeBundle.ReadBytes(path, entries[2].k, CubeBundle.AlbedoMagic);
            var a = CubeAlbedoCodec.Decode(bytes!);
            Assert.Equal(entries[2].k, a.Key);
            Assert.Equal(256, a.Size);
            Assert.Equal(new byte[] { 2, 7, 0xAB }, a.ForMonth(7));
            Assert.Equal(new byte[] { 2, 12, 0xAB }, a.ForMonth(13));
            // a colour bundle is never read as heights
            Assert.Throws<InvalidDataException>(() => CubeBundle.Read(path, entries[2].k));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_rebuilt_level_keeps_its_colour_and_names_the_tiles_it_lacks()
    {
        // #1659: --cube-swiss rebuilt levels 5-13's heights and dropped their colour, though the .cuba bundles were
        // still there: the globe drew B4's pyramid white from level 5 down
        string dir = Path.Combine(Path.GetTempPath(), "cubecolour_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bundle = new CubeKey(0, 3, 3, 3);
            var children = bundle.Children().SelectMany(c => c.Children()).Take(5).ToList();   // level 5
            var coloured = children.Take(4).Select(k => (k, new byte[] { 1, 2, 3 })).ToList();
            CubeBundle.Write(Path.Combine(dir, CubeBundle.AlbedoFileName(bundle)), bundle, coloured, CubeBundle.AlbedoMagic);
            var before = new CubeManifestLevel { Level = 5, Bits = 9, BundleLevel = 2, Albedo = 256, AlbedoBundleLevel = 3 };
            var rebuilt = new CubeManifestLevel { Level = 5, Bits = 9, BundleLevel = 2 };

            var missing = CubeColourCarry.Carry(dir, before, rebuilt, children);

            Assert.Equal(256, rebuilt.Albedo);
            Assert.Equal(3, rebuilt.AlbedoBundleLevel);
            Assert.Equal(new[] { children[4] }, missing);

            // no colour before, or its bundles gone: none after, every tile to bake
            var bare = new CubeManifestLevel { Level = 5, Bits = 9, BundleLevel = 2 };
            Assert.Equal(5, CubeColourCarry.Carry(dir, null, bare, children).Count);
            Assert.Equal(0, bare.Albedo);
            File.Delete(Path.Combine(dir, CubeBundle.AlbedoFileName(bundle)));
            Assert.Equal(5, CubeColourCarry.Carry(dir, before, bare, children).Count);
            Assert.Equal(0, bare.Albedo);
        }
        finally { Directory.Delete(dir, true); }
    }
}
