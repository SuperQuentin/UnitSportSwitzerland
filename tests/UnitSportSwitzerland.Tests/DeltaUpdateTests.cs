using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Delta updates (#532): the binary diff, the package and its staging, the release chain
/// (src/Core/BinaryDelta.cs, UpdatePackage.cs, UpdateInfo.cs, linked in).</summary>
public class DeltaUpdateTests
{
    private static byte[] Random(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static byte[] RoundTrip(byte[] old, byte[] nu, out int patchSize)
    {
        byte[] patch = BinaryDelta.Create(old, nu);
        patchSize = patch.Length;
        var o = new MemoryStream();
        BinaryDelta.Apply(new MemoryStream(old), new MemoryStream(patch), o);
        return o.ToArray();
    }

    [Fact]
    public void Patched_bytes_cost_bytes()
    {
        byte[] old = Random(1 << 20, 1), nu = (byte[])old.Clone();
        nu[1000] ^= 0xFF; nu[500_000] ^= 0x55; nu[^1] ^= 1;
        Assert.Equal(nu, RoundTrip(old, nu, out int size));
        Assert.True(size < 200, $"patch {size} bytes");
    }

    [Fact]
    public void Shifted_content_is_found()
    {
        byte[] old = Random(1 << 20, 2);
        byte[] nu = old[..300_000].Concat(Random(77, 3)).Concat(old[300_000..]).ToArray();
        Assert.Equal(nu, RoundTrip(old, nu, out int size));
        Assert.True(size < 400, $"patch {size} bytes");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 5000)]
    [InlineData(5000, 0)]
    [InlineData(100, 3000)]
    [InlineData(70_000, 70_001)]
    public void Edge_sizes_round_trip(int oldLen, int newLen)
    {
        byte[] old = Random(oldLen, 4), nu = Random(newLen, 5);
        Assert.Equal(nu, RoundTrip(old, nu, out _));
    }

    [Fact]
    public void A_patch_reading_outside_the_old_file_is_refused()
    {
        byte[] old = Random(10_000, 6);
        byte[] patch = BinaryDelta.Create(old, old);
        Assert.Throws<InvalidDataException>(() =>
            BinaryDelta.Apply(new MemoryStream(old[..5000]), new MemoryStream(patch), new MemoryStream()));
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("bin/../../evil")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("bin\\x.exe")]
    [InlineData("a//b")]
    [InlineData(".update/files/x")]
    [InlineData("")]
    public void Unsafe_paths_are_refused(string p) => Assert.Throws<InvalidDataException>(() => UpdatePackage.SafePath(p));

    [Fact]
    public void Safe_paths_pass() => Assert.Equal("data_x/UnitSport.dll", UpdatePackage.SafePath("data_x/UnitSport.dll"));

    private static string Dir(params (string Path, byte[] Data)[] files)
    {
        string d = Path.Combine(Path.GetTempPath(), "deltatest-" + Guid.NewGuid().ToString("N"));
        foreach (var (p, data) in files)
        {
            string f = Path.Combine(d, p);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, data);
        }
        return d;
    }

    private static MemoryStream Package(string oldDir, string newDir, string from, string to)
    {
        var ms = new MemoryStream();
        UpdatePackage.Create(oldDir, newDir, from, to, ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_chain_stages_adds_patches_and_deletes()
    {
        byte[] game = Random(200_000, 7), game2 = (byte[])game.Clone(), game3 = (byte[])game.Clone();
        game2[10] ^= 1; game3[10] ^= 1; game3[150_000] ^= 2;
        string v1 = Dir(("game.pck", game), ("old.dll", Random(500, 8)), ("same.txt", new byte[] { 1, 2 }));
        string v2 = Dir(("game.pck", game2), ("bin/new.exe", Random(900, 9)), ("same.txt", new byte[] { 1, 2 }));
        string v3 = Dir(("game.pck", game3), ("bin/new.exe", Random(900, 9)), ("same.txt", new byte[] { 1, 2 }));
        string install = Dir(("game.pck", game), ("old.dll", Random(500, 8)), ("same.txt", new byte[] { 1, 2 }));
        try
        {
            var s = new UpdatePackage.Staging(install);
            s.Apply(Package(v1, v2, "v1", "v2"), "v1", "v2");
            s.Apply(Package(v2, v3, "v2", "v3"), "v2", "v3");
            s.Finish();
            string files = Path.Combine(s.Dir, "files");
            Assert.Equal(game3, File.ReadAllBytes(Path.Combine(files, "game.pck")));
            Assert.True(File.Exists(Path.Combine(files, "bin/new.exe")));
            Assert.False(File.Exists(Path.Combine(files, "same.txt")));
            Assert.Equal(new[] { "old.dll" }, File.ReadAllLines(Path.Combine(s.Dir, "delete.txt")));
            Assert.Equal(game, File.ReadAllBytes(Path.Combine(install, "game.pck")));   // the install is untouched
        }
        finally { foreach (var d in new[] { v1, v2, v3, install }) Directory.Delete(d, true); }
    }

    [Fact]
    public void A_modified_install_or_the_wrong_delta_is_refused()
    {
        byte[] game = Random(100_000, 10), game2 = (byte[])game.Clone();
        game2[5] ^= 1;
        string v1 = Dir(("game.pck", game)), v2 = Dir(("game.pck", game2));
        string install = Dir(("game.pck", Random(100_000, 11)));
        try
        {
            Assert.Throws<InvalidDataException>(() => new UpdatePackage.Staging(install).Apply(Package(v1, v2, "v1", "v2"), "v1", "v2"));
            Assert.Throws<InvalidDataException>(() => new UpdatePackage.Staging(v1).Apply(Package(v1, v2, "v1", "v2"), "v0", "v2"));
        }
        finally { foreach (var d in new[] { v1, v2, install }) Directory.Delete(d, true); }
    }

    private static UpdateInfo.Release Rel(string tag, params string[] assets) =>
        new(tag, "", assets.Select(a => new UpdateInfo.Asset(a, "https://e/" + a, 10)).ToList());

    [Fact]
    public void The_chain_walks_every_release_after_this_one()
    {
        var releases = new[]
        {
            Rel("v0.10.0", "UnitSportSwitzerland-v0.9.0-to-v0.10.0-windows.delta"),
            Rel("v0.8.1"),
            Rel("v0.9.0", "UnitSportSwitzerland-v0.8.1-to-v0.9.0-windows.delta", "UnitSportSwitzerland-v0.8.1-to-v0.9.0-macos.delta"),
            new UpdateInfo.Release("v0.11.0-rc1", "", Array.Empty<UpdateInfo.Asset>(), Prerelease: true),
        };
        var chain = UpdateInfo.DeltaChain(releases, "0.8.1", "Windows");
        Assert.NotNull(chain);
        Assert.Equal(new[] { "v0.8.1>v0.9.0", "v0.9.0>v0.10.0" }, chain!.Select(c => $"{c.From}>{c.To}"));
        Assert.Equal("v0.10.0", UpdateInfo.Latest(releases)!.Tag);
        Assert.Null(UpdateInfo.DeltaChain(releases, "0.8.1", "macOS"));     // a step without a mac delta
        Assert.Null(UpdateInfo.DeltaChain(releases, "0.8.5", "Windows"));   // not a release
        Assert.Null(UpdateInfo.DeltaChain(releases, "0.10.0", "Windows"));  // already the latest
    }
}
