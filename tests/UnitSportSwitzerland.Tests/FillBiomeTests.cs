using UnitSport.Terrain.Biomes;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The earth fill's biomes (#1809, <c>docs/plans/fill-biomes.md</c>): the photo guess on the shipped statistics at known
/// places, the Swiss guard, the latitude's snow and tree lines, the cover each biome makes, and the pass on a leaf
/// (the guard leaves it to the bit; a desert takes its trees and farms). The real leaves on the pyramid are BlendCheck's
/// <c>--fill-biomes</c>.
/// </summary>
public class FillBiomeTests
{
    private const double Far = -1e7;

    [Theory]
    [InlineData(23, 5, Biome.Desert)]          // the Sahara
    [InlineData(31.333, -0.379, Biome.Desert)] // the user's spot, LV95 1829400,-515818
    [InlineData(22, 47, Biome.Desert)]         // Arabia
    [InlineData(48.5, 67, Biome.Steppe)]       // Kazakhstan
    [InlineData(69, 70, Biome.Tundra)]         // Yamal
    [InlineData(72, -40, Biome.Ice)]           // Greenland
    [InlineData(-80, 0, Biome.Ice)]            // Antarctica
    [InlineData(-3, -62, Biome.TropicalForest)]// the Amazon
    [InlineData(0, 22, Biome.TropicalForest)]  // the Congo
    [InlineData(-2.5, 35, Biome.Savanna)]      // the Serengeti
    [InlineData(64, 27, Biome.Boreal)]         // Finland
    [InlineData(30, -40, Biome.Sea)]           // the mid-Atlantic
    [InlineData(52, -1, Biome.Swiss)]          // England: temperate, the generator's look
    public void The_guess_names_known_places(double lat, double lon, Biome expect)
    {
        Assert.NotNull(PhotoStats.Embedded);
        var b = FillBiome.Default.At(lat, lon, 100, Far, out var source);
        Assert.Equal(BiomeSource.PhotoGuess, source);
        Assert.Equal(expect, b);
    }

    [Fact]
    public void Inside_the_generators_map_the_guard_keeps_it_Swiss()
    {
        // the Sahara's statistics, but deep inside the map: Swiss, by the guard
        var b = FillBiome.Default.At(23, 5, 100, FillBiome.GuardBandM + 1, out var source);
        Assert.Equal(Biome.Swiss, b);
        Assert.Equal(BiomeSource.Guard, source);
        // across the band some points are guarded and some are not
        int guarded = 0;
        for (int k = 0; k < 400; k++)
            if (FillBiome.Default.At(23 + k * 0.01, 5, 100, FillBiome.GuardBandM / 2, out var s) == Biome.Swiss && s == BiomeSource.Guard) guarded++;
        Assert.InRange(guarded, 40, 360);
    }

    [Fact]
    public void Without_statistics_the_chain_falls_to_the_generator()
    {
        var b = new FillBiome(null).At(23, 5, 100, Far, out var source);
        Assert.Equal(Biome.Swiss, b);
        Assert.Equal(BiomeSource.Generator, source);
    }

    [Fact]
    public void Land_cover_comes_before_the_guess()
    {
        var chain = new FillBiome(PhotoStats.Embedded) { LandCover = (lat, lon) => lat > 0 ? Biome.Wetland : null };
        Assert.Equal(Biome.Wetland, chain.At(23, 5, 100, Far, out var s1));
        Assert.Equal(BiomeSource.LandCover, s1);
        // no value there: the guess
        Assert.Equal(Biome.TropicalForest, chain.At(-3, -62, 100, Far, out var s2));
        Assert.Equal(BiomeSource.PhotoGuess, s2);
    }

    [Fact]
    public void The_snow_and_tree_lines_follow_the_latitude()
    {
        Assert.Equal(2900, FillBiome.SnowLineM(46), 6);
        Assert.Equal(2100, FillBiome.TreeLineM(46), 6);
        Assert.Equal(FillBiome.SnowLineM(-46), FillBiome.SnowLineM(46));
        Assert.True(FillBiome.SnowLineM(25) > FillBiome.SnowLineM(0));
        Assert.Equal(0, FillBiome.TreeLineM(80), 6);
        // over the snow line ice, over the tree line alpine tundra; a high desert stays bare; the sea is the sea
        Assert.Equal(Biome.Ice, FillBiome.ByAltitude(Biome.Steppe, 46, 3000));
        Assert.Equal(Biome.Tundra, FillBiome.ByAltitude(Biome.Steppe, 46, 2500));
        Assert.Equal(Biome.Desert, FillBiome.ByAltitude(Biome.Desert, 30, 4000));
        Assert.Equal(Biome.Sea, FillBiome.ByAltitude(Biome.Sea, 70, 9000));
        Assert.Equal(Biome.Swiss, FillBiome.ByAltitude(Biome.Swiss, 46, 1500));
    }

    [Fact]
    public void Each_biome_makes_its_ground_and_keeps_the_rock()
    {
        foreach (var c in Enum.GetValues<CoverClass>()) Assert.Equal(c, BiomePass.Ground(Biome.Swiss, c));
        Assert.Equal(CoverClass.Sand, BiomePass.Ground(Biome.Desert, CoverClass.Open));
        Assert.Equal(CoverClass.Sand, BiomePass.Ground(Biome.Desert, CoverClass.Forest));
        Assert.Equal(CoverClass.Rock, BiomePass.Ground(Biome.Desert, CoverClass.Rock));
        Assert.Equal(CoverClass.DryGrass, BiomePass.Ground(Biome.Steppe, CoverClass.Open));
        Assert.Equal(CoverClass.OpenForest, BiomePass.Ground(Biome.Savanna, CoverClass.Forest));
        Assert.Equal(CoverClass.LushGround, BiomePass.Ground(Biome.TropicalForest, CoverClass.Open));
        Assert.Equal(CoverClass.Tundra, BiomePass.Ground(Biome.Tundra, CoverClass.Forest));
        Assert.Equal(CoverClass.Glacier, BiomePass.Ground(Biome.Ice, CoverClass.Open));
        Assert.Equal(CoverClass.Rock, BiomePass.Ground(Biome.Ice, CoverClass.Rock));
        Assert.Equal(0, BiomePass.FarmShare(Biome.Desert));
        Assert.Equal(1, BiomePass.FarmShare(Biome.Swiss));
    }

    [Fact]
    public void The_statistics_round_trip()
    {
        var s = PhotoStats.Embedded!;
        using var ms = new MemoryStream();
        s.Write(ms);
        ms.Position = 0;
        var back = PhotoStats.Read(ms);
        Assert.Equal(s.Cols, back.Cols);
        Assert.Equal(s.Rows, back.Rows);
        Assert.Equal(s.Cells, back.Cells);
        // longitude wraps: either side of the antimeridian reads the same cells
        Assert.Equal(s.At(10, 179.95), s.At(10, -180.05));
    }

    [Fact]
    public void The_noise_stays_in_range_and_is_continuous()
    {
        for (int k = 0; k < 1000; k++)
        {
            double v = FillBiome.ValueNoise(k * 0.37, -k * 0.11, 3);
            Assert.InRange(v, 0, 1);
            Assert.True(Math.Abs(FillBiome.ValueNoise(k * 0.37 + 1e-6, -k * 0.11, 3) - v) < 1e-4);
        }
    }

    private static CubeLeafContent OpenLeaf(CubeKey key, int trees)
    {
        var t = LeafFrames.Swiss.TransformOf(key);
        int n = t.Cells + 1;
        var h = new float[n * n];
        for (int k = 0; k < h.Length; k++) h[k] = 300;
        var content = new CubeLeafContent
        {
            Key = key,
            Heights = new CubeTile { Key = key, Bits = t.Bits, Heights = h, Quantum = 0.01f },
            Cover = new byte[n * n],
            Trees = new(),
        };
        for (int k = 0; k < trees; k++)
        {
            var (x, y, z) = t.LatticeToLocal(17 + k * 37 % 480, 11 + k * 53 % 490, 300);
            content.Trees.Add(new TreeInstance((float)x, (float)y, (float)z, 12, 3));
        }
        return content;
    }

    [Fact]
    public void A_guarded_leaf_is_left_to_the_bit()
    {
        var key = LeafFrames.LeafAt(46.5, 7.5);
        var content = OpenLeaf(key, 200);
        var trees = content.Trees!.ToList();
        var st = BiomePass.Apply(content, LeafFrames.Swiss.TransformOf(key), FillBiome.Default, (_, _) => 1e9);
        Assert.True(st.Untouched);
        Assert.All(content.Cover!, c => Assert.Equal((byte)CoverClass.Open, c));
        Assert.Equal(trees, content.Trees);
    }

    [Fact]
    public void A_desert_leaf_is_sand_with_few_shrubs()
    {
        var key = LeafFrames.LeafAt(23, 5);
        var content = OpenLeaf(key, 500);
        var st = BiomePass.Apply(content, LeafFrames.Swiss.TransformOf(key), FillBiome.Default, (_, _) => Far);
        Assert.Equal(Biome.Desert, st.Main);
        Assert.All(content.Cover!, c => Assert.Equal((byte)CoverClass.Sand, c));
        Assert.InRange(content.Trees!.Count, 0, 40);
        Assert.All(content.Trees, tr => Assert.Equal(1, tr.Kind));
    }
}
