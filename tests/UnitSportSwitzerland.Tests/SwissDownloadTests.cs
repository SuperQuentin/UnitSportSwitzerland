using System.Text.Json;
using UnitSport.Map;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Tier 0 (docs/notes/general/testing.md): the pure parts of the swiss_data.py port, with no
/// network. Only <c>tools/MapCore/DownloadManifest.cs</c> is linked in (see its doc comment and
/// the csproj entry) -- <c>SwissDownload.cs</c> itself needs <c>IStepProgress</c> from
/// <c>Pipeline.cs</c> to even compile, which this project has no path to without also linking in
/// things it has no project reference for.
/// </summary>
public class SwissDownloadTests
{
    // ---------------------------------------------------------------------------
    // Multihash parsing (sha256_of_asset).
    // ---------------------------------------------------------------------------

    [Fact]
    public void Sha256OfAsset_ValidMultihash_ReturnsDigest()
    {
        string digest = new string('a', 64);
        string? result = SwissStacUtil.Sha256OfAsset("1220" + digest);
        Assert.Equal(digest, result);
    }

    [Fact]
    public void Sha256OfAsset_IsCaseInsensitive()
    {
        string digest = new string('a', 64);
        string? result = SwissStacUtil.Sha256OfAsset("1220" + digest.ToUpperInvariant());
        Assert.Equal(digest, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // wrong prefix
    [InlineData("1220" + "aaaa")] // right prefix, wrong length (too short)
    [InlineData("1220" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // too long
    public void Sha256OfAsset_Malformed_ReturnsNull(string? multihash)
    {
        Assert.Null(SwissStacUtil.Sha256OfAsset(multihash));
    }

    // ---------------------------------------------------------------------------
    // Manifest JSON round-trip (load_manifest / save_manifest).
    // ---------------------------------------------------------------------------

    [Fact]
    public void Manifest_RoundTrips_ThroughSaveAndLoad()
    {
        var dir = MakeTempDir();
        try
        {
            var original = new Dictionary<string, ManifestEntry>
            {
                ["swissalti3d_2024_2583-1113.xyz.zip"] = new ManifestEntry
                {
                    Url = "https://example.test/a.zip",
                    Size = 123456,
                    ETag = "\"abc\"",
                    LastModified = "Wed, 21 Oct 2015 07:28:00 GMT",
                    Sha256 = new string('b', 64),
                    DownloadedAt = "2026-10-05T12:00:00Z",
                },
                ["hand_downloaded.zip"] = new ManifestEntry
                {
                    Url = "https://example.test/b.zip",
                    Size = 42,
                    ETag = null,
                    LastModified = null,
                    Sha256 = null,
                    DownloadedAt = null, // predates the tool, backfilled from a HEAD
                },
            };

            DownloadManifest.Save(dir, original);
            var loaded = DownloadManifest.Load(dir);

            Assert.Equal(original.Count, loaded.Count);
            foreach (var (filename, entry) in original)
                Assert.Equal(entry, loaded[filename]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Manifest_Path_IsDotSwissDataManifestJson_InOutDir()
    {
        Assert.Equal(Path.Combine("some_dir", ".swiss_data_manifest.json"), DownloadManifest.Path("some_dir"));
    }

    [Fact]
    public void Manifest_Load_ParsesThePythonTools_OwnJsonShape()
    {
        // A hand-written sample in exactly the shape swiss_data.py's save_manifest (json.dump,
        // indent=2) produces: this is the compatibility check that matters most -- both tools
        // must read and write the very same keys.
        var dir = MakeTempDir();
        try
        {
            string json = """
            {
              "swissalti3d_2024_2583-1113.xyz.zip": {
                "url": "https://data.geo.admin.ch/example/a.zip",
                "size": 9876543,
                "etag": "\"deadbeef\"",
                "last_modified": "Wed, 21 Oct 2015 07:28:00 GMT",
                "sha256": "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
                "downloaded_at": "2026-01-01T00:00:00Z"
              },
              "predates_the_tool.zip": {
                "url": "https://data.geo.admin.ch/example/b.zip",
                "size": 42,
                "etag": null,
                "last_modified": null,
                "downloaded_at": null
              }
            }
            """;
            File.WriteAllText(DownloadManifest.Path(dir), json);

            var manifest = DownloadManifest.Load(dir);

            Assert.Equal(2, manifest.Count);
            var first = manifest["swissalti3d_2024_2583-1113.xyz.zip"];
            Assert.Equal("https://data.geo.admin.ch/example/a.zip", first.Url);
            Assert.Equal(9876543, first.Size);
            Assert.Equal("\"deadbeef\"", first.ETag);
            Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef", first.Sha256);

            var second = manifest["predates_the_tool.zip"];
            Assert.Equal(42, second.Size);
            Assert.Null(second.Sha256); // the key is entirely absent, same as a Python dict.get
            Assert.Null(second.DownloadedAt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // known_current's decision table.
    // ---------------------------------------------------------------------------

    [Fact]
    public void KnownCurrent_EntryMatchesChecksumAndSize_IsTrue()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[10]);
            var entry = new ManifestEntry { Sha256 = "abc", Size = 10 };

            Assert.True(DownloadManifest.KnownCurrent(dest, entry, "abc"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void KnownCurrent_StaleChecksum_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[10]);
            var entry = new ManifestEntry { Sha256 = "old", Size = 10 };

            Assert.False(DownloadManifest.KnownCurrent(dest, entry, "new"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void KnownCurrent_NoEntry_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[10]);

            Assert.False(DownloadManifest.KnownCurrent(dest, null, "abc"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void KnownCurrent_NoStacChecksum_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[10]);
            var entry = new ManifestEntry { Sha256 = "abc", Size = 10 };

            Assert.False(DownloadManifest.KnownCurrent(dest, entry, null));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // is_up_to_date's decision table.
    // ---------------------------------------------------------------------------

    [Fact]
    public void IsUpToDate_NoEntry_SizeMatches_IsTrue()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[100]);
            var head = new HeadInfo(100, null, null, null, false);

            Assert.True(DownloadManifest.IsUpToDate(dest, null, head));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsUpToDate_NoEntry_SizeDiffers_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[100]);
            var head = new HeadInfo(99, null, null, null, false);

            Assert.False(DownloadManifest.IsUpToDate(dest, null, head));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsUpToDate_FileMissing_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "missing.zip");
            var head = new HeadInfo(100, null, null, null, false);

            Assert.False(DownloadManifest.IsUpToDate(dest, null, head));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsUpToDate_EntryPresent_EverythingMatches_IsTrue()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[100]);
            var entry = new ManifestEntry { Size = 100, ETag = "\"x\"", LastModified = "same", Sha256 = "abc" };
            var head = new HeadInfo(100, "\"x\"", "same", "abc", false);

            Assert.True(DownloadManifest.IsUpToDate(dest, entry, head));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsUpToDate_EntryPresent_LocalSizeMismatch_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[50]); // disk disagrees with the manifest
            var entry = new ManifestEntry { Size = 100 };
            var head = new HeadInfo(100, null, null, null, false);

            Assert.False(DownloadManifest.IsUpToDate(dest, entry, head));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsUpToDate_EntryPresent_HeadChecksumDiffers_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[100]);
            var entry = new ManifestEntry { Size = 100, Sha256 = "old" };
            var head = new HeadInfo(100, null, null, "new", false);

            Assert.False(DownloadManifest.IsUpToDate(dest, entry, head));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsUpToDate_EntryPresent_HeadEtagDiffers_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[100]);
            var entry = new ManifestEntry { Size = 100, ETag = "\"old\"" };
            var head = new HeadInfo(100, "\"new\"", null, null, false);

            Assert.False(DownloadManifest.IsUpToDate(dest, entry, head));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsUpToDate_EntryPresent_HeadLastModifiedDiffers_IsFalse()
    {
        var dir = MakeTempDir();
        try
        {
            var dest = Path.Combine(dir, "file.zip");
            File.WriteAllBytes(dest, new byte[100]);
            var entry = new ManifestEntry { Size = 100, LastModified = "old" };
            var head = new HeadInfo(100, null, "new", null, false);

            Assert.False(DownloadManifest.IsUpToDate(dest, entry, head));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------------------------------
    // Tile-name to STAC-query mapping.
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryParseAltiItemId_ValidId_ParsesYearAndTile()
    {
        bool ok = SwissStacUtil.TryParseAltiItemId("swissalti3d_2024_2583-1113", out int year, out TileId tile);

        Assert.True(ok);
        Assert.Equal(2024, year);
        Assert.Equal(new TileId(2583, 1113), tile);
    }

    [Theory]
    [InlineData("swisstlm3d_2024_2583-1113")] // wrong collection
    [InlineData("swissalti3d_2024")] // missing tile
    [InlineData("swissalti3d_24_2583-1113")] // year not 4 digits
    [InlineData("")]
    public void TryParseAltiItemId_InvalidId_ReturnsFalse(string id)
    {
        bool ok = SwissStacUtil.TryParseAltiItemId(id, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void AltiAssetPattern_MatchesOnlyItsOwnResolution()
    {
        var half = SwissStacUtil.AltiAssetPattern("0.5");
        var two = SwissStacUtil.AltiAssetPattern("2");

        Assert.Matches(half, "swissalti3d_2024_2583-1113_0.5_2056_5728.xyz.zip");
        Assert.DoesNotMatch(half, "swissalti3d_2024_2583-1113_2_2056_5728.xyz.zip");
        Assert.Matches(two, "swissalti3d_2024_2583-1113_2_2056_5728.xyz.zip");
        Assert.DoesNotMatch(two, "swissalti3d_2024_2583-1113_0.5_2056_5728.xyz.zip");
    }

    // ---------------------------------------------------------------------------
    // WGS84 bbox conversion.
    // ---------------------------------------------------------------------------

    [Fact]
    public void BboxLv95ToWgs84_OrdersWestSouthEastNorthCorrectly()
    {
        // A real-ish Swiss LV95 box: min/max in LV95 must still be min/max once reprojected.
        var (w, s, e, n) = SwissStacUtil.BboxLv95ToWgs84(2579000, 1109000, 2586000, 1115000);

        Assert.True(w < e);
        Assert.True(s < n);
        // Sanity: squarely inside Switzerland's lon/lat envelope.
        Assert.InRange(w, 5.9, 10.6);
        Assert.InRange(e, 5.9, 10.6);
        Assert.InRange(s, 45.7, 47.9);
        Assert.InRange(n, 45.7, 47.9);
    }

    [Fact]
    public void TilesBboxWgs84_MatchesManualCornerConversion()
    {
        var tiles = new[] { new TileId(2583, 1113), new TileId(2584, 1115) };

        var (w, s, e, n) = SwissStacUtil.TilesBboxWgs84(tiles);
        var expected = SwissStacUtil.BboxLv95ToWgs84(2583000, 1113000, 2585000, 1116000);

        Assert.Equal(expected, (w, s, e, n));
    }

    // ---------------------------------------------------------------------------
    // swissTLM3D "latest release only" (IndexOfLatestDatetime, TlmAssetKeys).
    // ---------------------------------------------------------------------------

    [Fact]
    public void IndexOfLatestDatetime_PicksLatestByOrdinalText()
    {
        var dts = new[] { "2021-03-01T00:00:00Z", "2024-09-12T00:00:00Z", "2019-01-01T00:00:00Z" };
        Assert.Equal(1, SwissStacUtil.IndexOfLatestDatetime(dts));
    }

    [Fact]
    public void IndexOfLatestDatetime_Tie_KeepsFirst()
    {
        var dts = new[] { "2024-01-01T00:00:00Z", "2024-01-01T00:00:00Z" };
        Assert.Equal(0, SwissStacUtil.IndexOfLatestDatetime(dts));
    }

    [Fact]
    public void IndexOfLatestDatetime_Empty_ReturnsNegativeOne()
    {
        Assert.Equal(-1, SwissStacUtil.IndexOfLatestDatetime(Array.Empty<string>()));
    }

    [Fact]
    public void TlmAssetKeys_PrefersGpkgZip()
    {
        var keys = new[] { "swissTLM3D_2024.gpkg.zip", "swissTLM3D_2024.gdb.zip", "readme.txt" };
        var result = SwissStacUtil.TlmAssetKeys(keys);
        Assert.Equal(new[] { "swissTLM3D_2024.gpkg.zip" }, result);
    }

    [Fact]
    public void TlmAssetKeys_FallsBackToGdbZip_WhenNoGpkgZip()
    {
        var keys = new[] { "swissTLM3D_2024.gdb.zip", "readme.txt" };
        var result = SwissStacUtil.TlmAssetKeys(keys);
        Assert.Equal(new[] { "swissTLM3D_2024.gdb.zip" }, result);
    }

    [Fact]
    public void TlmAssetKeys_NeitherExtension_ReturnsEmpty()
    {
        var keys = new[] { "readme.txt", "metadata.xml" };
        Assert.Empty(SwissStacUtil.TlmAssetKeys(keys));
    }

    // ---------------------------------------------------------------------------
    // GWR fixed-URL resolution (not STAC at all).
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("vs", "https://public.madd.bfs.admin.ch/vs.zip", "gwr_vs.zip")]
    [InlineData("ch", "https://public.madd.bfs.admin.ch/ch.zip", "gwr_ch.zip")]
    public void GwrAsset_BuildsFixedBfsUrlAndFilename(string canton, string expectedUrl, string expectedFilename)
    {
        var (url, filename) = SwissStacUtil.GwrAsset(canton);
        Assert.Equal(expectedUrl, url);
        Assert.Equal(expectedFilename, filename);
    }

    // ---------------------------------------------------------------------------

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "swiss_download_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
