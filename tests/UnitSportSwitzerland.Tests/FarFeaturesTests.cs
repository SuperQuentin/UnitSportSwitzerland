using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// What stands on the ground, baked for the far view (#1657): the <see cref="CubeFarFeatures"/> entry, its sampling, and the
/// pyramid chain carrying it (the index's flag, section 13 from the server). The bake itself is <c>--cube-features</c>'.
/// </summary>
public class FarFeaturesTests
{
    private static readonly CubeKey Key = CubeKey.FromLatLon(46.2044, 6.1432, 13);

    private static CubeFarFeatures Sample()
    {
        var f = CubeFarFeatures.Empty(Key);
        for (int k = 0; k < f.Forest.Length; k++)
        {
            f.Forest[k] = (byte)(k % 256);
            f.Built[k] = (byte)(255 - k % 256);
            f.Skin[k] = (byte)(k % 80);
        }
        return f;
    }

    [Fact]
    public void An_entry_reads_back()
    {
        var f = Sample();
        var back = CubeFarFeaturesCodec.Decode(CubeFarFeaturesCodec.Encode(f));
        Assert.Equal(f.Key, back.Key);
        Assert.Equal(f.Forest, back.Forest);
        Assert.Equal(f.Built, back.Built);
        Assert.Equal(f.Paved, back.Paved);
        Assert.Equal(f.Skin, back.Skin);
    }

    [Fact]
    public void Sampling_is_exact_at_a_vertex_and_between_them_bilinear()
    {
        var f = CubeFarFeatures.Empty(Key);
        int n = CubeFarFeatures.N;
        f.Skin[3 * n + 2] = 40;
        f.Skin[3 * n + 3] = 80;
        Assert.Equal(10f, f.At(2, 3).SkinM, 3);
        Assert.Equal(15f, f.At(2.5f, 3).SkinM, 3);
        Assert.Equal(0f, f.At(2, 4).SkinM, 3);
    }

    [Fact]
    public void The_chain_streams_features_as_their_own_section()
    {
        var heights = new float[65 * 65];
        var hBytes = CubeTileCodec.Encode(new CubeTile { Key = Key, Bits = 6, Heights = heights, Quantum = 1 });
        var fBytes = CubeFarFeaturesCodec.Encode(Sample());
        var index = new PyramidIndex(new Dictionary<CubeKey, PyramidIndex.Entry>
        {
            [Key] = new(6, hBytes.Length, PyramidIndex.FeaturesBit, PyramidIndex.StampOf(Key, hBytes, fBytes)),
        });
        var back = PyramidIndex.FromBytes(index.ToBytes())!;
        Assert.True(back.Tiles[Key].HasFeatures);
        Assert.False(back.Tiles[Key].HasColour);

        var chain = new PyramidSource([]);
        var asked = new List<int>();
        chain.UseRemote(back, (k, e, s) => { asked.Add(s); return s == PyramidSource.FeaturesSection ? fBytes : hBytes; });
        Assert.True(chain.HasFeatures(Key));
        Assert.False(chain.HasColour(Key));
        Assert.Equal(Sample().Skin, chain.Features(Key)!.Skin);
        Assert.Equal([PyramidSource.FeaturesSection], asked);
        // the features are in the stamp: baked again, a cached tile misses
        Assert.NotEqual(PyramidIndex.StampOf(Key, hBytes), PyramidIndex.StampOf(Key, hBytes, fBytes));
    }
}
