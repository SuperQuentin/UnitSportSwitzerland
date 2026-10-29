using UnitSport.Net;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>#1380: the per-server index cache, the changed-tile detection and the server digest.</summary>
public class JoinCacheTests
{
    private static ManifestTile Tile(int e, int n, float min = 0, float max = 10, string? hash = null) =>
        new() { E = e, N = n, Min = min, Max = max, Hash = hash };

    private static TerrainManifest Manifest(params ManifestTile[] tiles) => new() { Tiles = tiles.ToList() };

    [Fact]
    public void ServerKeyIsCaseInsensitiveAndAddsTheDefaultPort()
    {
        Assert.Equal(JoinCache.ServerKey("Play.Example.CH", "open"), JoinCache.ServerKey("play.example.ch:7777", "open"));
        Assert.NotEqual(JoinCache.ServerKey("host", "open"), JoinCache.ServerKey("host:7778", "open"));
        Assert.NotEqual(JoinCache.ServerKey("host", "open"), JoinCache.ServerKey("host", "lights"));
    }

    [Fact]
    public void ManifestFileNameIsSafeAndOnePerServer()
    {
        string a = JoinCache.ManifestFileName(JoinCache.ServerKey("a:b/c", "w x"));
        Assert.StartsWith("server-manifest-", a);
        Assert.EndsWith(".json", a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain(':', a);
        Assert.DoesNotContain(' ', a);
        // two keys that sanitise alike still get two files
        Assert.NotEqual(JoinCache.ManifestFileName("a/b|w"), JoinCache.ManifestFileName("a:b|w"));
        Assert.Equal(JoinCache.ManifestFileName("k"), JoinCache.ManifestFileName("k"));
    }

    [Fact]
    public void ChangedTilesUseTheHashWhenBothHaveOne()
    {
        var old = Manifest(Tile(1, 1, hash: "aa"), Tile(2, 2, hash: "bb"), Tile(3, 3, hash: "cc"));
        var fresh = Manifest(Tile(1, 1, max: 99, hash: "aa"), Tile(2, 2, hash: "XX"), Tile(3, 3, hash: "cc"), Tile(4, 4, hash: "dd"));
        // (1,1) changed its range but not its hash: same content; (4,4) is new, not changed
        Assert.Equal([new TileId(2, 2)], JoinCache.ChangedTiles(old, fresh).Select(t => t.Id));
    }

    [Fact]
    public void ChangedTilesFallBackToTheHeightRange()
    {
        var old = Manifest(Tile(1, 1, 0, 10), Tile(2, 2, 0, 10));
        var fresh = Manifest(Tile(1, 1, 0, 10), Tile(2, 2, -5, 10, hash: "new"));
        Assert.Equal([new TileId(2, 2)], JoinCache.ChangedTiles(old, fresh).Select(t => t.Id));
    }

    [Fact]
    public void NoOldIndexMeansNothingChanged()
    {
        Assert.Empty(JoinCache.ChangedTiles(null, Manifest(Tile(1, 1))));
        Assert.Empty(JoinCache.ChangedTiles(Manifest(Tile(1, 1)), Manifest(Tile(1, 1))));
    }

    [Fact]
    public void FileHasHashComparesTheBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"joincache-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            string hash = JoinCache.Sha256Hex([1, 2, 3]);
            Assert.True(JoinCache.FileHasHash(path, hash));
            Assert.True(JoinCache.FileHasHash(path, hash.ToUpperInvariant()));
            Assert.False(JoinCache.FileHasHash(path, JoinCache.Sha256Hex([1, 2, 4])));
            Assert.False(JoinCache.FileHasHash(path + ".missing", hash));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DigestListsTheFilesTheServerHasAndHonoursOverrides()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"joincache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(AssetDigest.Build(dir, null, null));   // no manifest, nothing to say
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
            File.WriteAllBytes(Path.Combine(dir, "horizon.bin"), [9, 9]);
            File.WriteAllText(Path.Combine(dir, "landings.json"), "from disk");

            var digest = AssetDigest.Build(dir, null, [7]);
            Assert.NotNull(digest);
            Assert.Equal(JoinCache.Sha256Hex("{}"u8.ToArray()), digest!.Manifest);
            Assert.Equal(JoinCache.Sha256Hex([9, 9]), digest.Files["horizon.bin"]);
            Assert.Equal(JoinCache.Sha256Hex([7]), digest.Files["landings.json"]);   // the override, not the file
            Assert.False(digest.Files.ContainsKey("places.json"));                   // absent: not listed

            var back = AssetDigest.FromBytes(digest.ToBytes());
            Assert.Equal(digest.Manifest, back!.Manifest);
            Assert.Equal(digest.Files, back.Files);

            // a changed file is hashed again
            File.WriteAllBytes(Path.Combine(dir, "horizon.bin"), [1]);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "horizon.bin"), DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(JoinCache.Sha256Hex([1]), AssetDigest.Build(dir, null, null)!.Files["horizon.bin"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ADigestThatIsNotOneReadsAsNull()
    {
        Assert.Null(AssetDigest.FromBytes("nope"u8.ToArray()));
        Assert.Null(AssetDigest.FromBytes("{}"u8.ToArray()));
    }
}
