using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The tiles' content fingerprint (#924): which files belong to a tile, a tile's hash and a set's,
/// and that the manifest reads the same with and without hashes, old and new.
/// </summary>
public class TileFingerprintTests
{
    [Theory]
    [InlineData("chunk_2579_1109.terr", 2579, 1109)]
    [InlineData("chunk_2579_1109.terrc", 2579, 1109)]
    [InlineData("roads_2600_1200.road.swiss", 2600, 1200)]
    [InlineData("photo_2579_1109.jpg", 2579, 1109)]
    public void A_tile_file_names_its_tile(string name, int e, int n)
    {
        Assert.True(TileFingerprint.TryTileOf(name, out var id));
        Assert.Equal(new TileId(e, n), id);
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("horizon.bin")]
    [InlineData("places.json")]
    [InlineData("chunk_2579_1109.terr.tmp")]
    [InlineData("chunk_2579_1109.")]
    public void A_region_file_is_no_tile(string name) => Assert.False(TileFingerprint.TryTileOf(name, out _));

    [Fact]
    public void A_tile_hash_ignores_the_order_and_follows_every_file()
    {
        var a = TileFingerprint.OfTile([("chunk_1_1.terr", "aa"), ("roads_1_1.road", "bb")]);
        var b = TileFingerprint.OfTile([("roads_1_1.road", "bb"), ("chunk_1_1.terr", "aa")]);
        Assert.Equal(a, b);
        Assert.Equal(TileFingerprint.Digits, a.Length);
        Assert.NotEqual(a, TileFingerprint.OfTile([("chunk_1_1.terr", "aa"), ("roads_1_1.road", "bc")]));
        // a new file (a photo) changes it too
        Assert.NotEqual(a, TileFingerprint.OfTile([("chunk_1_1.terr", "aa"), ("roads_1_1.road", "bb"), ("photo_1_1.jpg", "cc")]));
    }

    [Fact]
    public void A_set_fingerprint_is_its_tiles_whatever_the_order_and_tells_a_missing_hash()
    {
        var x = TileFingerprint.OfSet([(new TileId(1, 2), "a"), (new TileId(3, 4), "b")]);
        Assert.Equal(x, TileFingerprint.OfSet([(new TileId(3, 4), "b"), (new TileId(1, 2), "a"), (new TileId(1, 2), "a")]));
        Assert.NotEqual(x, TileFingerprint.OfSet([(new TileId(1, 2), "a"), (new TileId(3, 4), null)]));
        Assert.NotEqual(x, TileFingerprint.OfSet([(new TileId(1, 2), "a")]));
    }

    [Fact]
    public void A_file_hash_is_the_sha256_of_its_bytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fp_{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "abc");
        try { Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", TileFingerprint.OfFile(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_manifest_from_before_924_reads_with_no_hashes()
    {
        const string old = """
            {"formatVersion": 2, "gridSize": 1001, "spacingM": 1, "heightScale": 0.07,
             "suggestedOriginLv95": {"e": 2600000, "n": 1200000}, "boundsLv95": {"minE": 0, "minN": 0, "maxE": 1, "maxN": 1},
             "tiles": [{"e": 2600, "n": 1200, "min": 400, "max": 500}]}
            """;
        var m = TerrainManifest.FromJson(old);
        Assert.Equal(1, m.ManifestVersion);
        Assert.Null(m.HashAlgorithm);
        Assert.Null(m.Tiles[0].Hash);
        // and written back without them: nothing new appears in an unhashed manifest
        Assert.DoesNotContain("hash", m.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_hashed_manifest_round_trips()
    {
        var m = new TerrainManifest
        {
            ManifestVersion = TerrainManifest.HashedVersion,
            HashAlgorithm = TileFingerprint.Algorithm,
            Shared = new SortedDictionary<string, string> { ["horizon.bin"] = "0123456789abcdef" },
        };
        m.Tiles.Add(new ManifestTile { E = 2600, N = 1200, Min = 1, Max = 2, Hash = "fedcba9876543210" });
        var back = TerrainManifest.FromJson(m.ToJson());
        Assert.Equal(2, back.ManifestVersion);
        Assert.Equal(TileFingerprint.Algorithm, back.HashAlgorithm);
        Assert.Equal("fedcba9876543210", back.Tiles[0].Hash);
        Assert.Equal("0123456789abcdef", back.Shared!["horizon.bin"]);
    }
}
