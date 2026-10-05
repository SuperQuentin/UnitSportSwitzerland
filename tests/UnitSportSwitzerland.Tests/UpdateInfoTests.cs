using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The update check's version compare and asset pick (src/Core/UpdateInfo.cs, linked in, #532).</summary>
public class UpdateInfoTests
{
    [Theory]
    [InlineData("v0.9.0", "0.8.1", true)]
    [InlineData("v0.8.10", "0.8.9", true)]
    [InlineData("v1.0.0", "0.99.99", true)]
    [InlineData("v0.8.1", "0.8.1", false)]
    [InlineData("v0.8.0", "0.8.1", false)]
    [InlineData("v0.9.0", "", false)]
    [InlineData("v0.9.0", null, false)]
    [InlineData("nightly", "0.8.1", false)]
    [InlineData("v0.9.0-rc1", "0.8.1", true)]
    public void IsNewer(string? latest, string? current, bool expected) =>
        Assert.Equal(expected, UpdateInfo.IsNewer(latest, current));

    private const string Json = """
        {"tag_name":"v0.9.0","html_url":"https://github.com/x/y/releases/tag/v0.9.0","assets":[
          {"name":"UnitSportSwitzerland-v0.9.0-windows.zip","browser_download_url":"https://e/w.zip","size":123},
          {"name":"UnitSportSwitzerland-v0.9.0-linux-x86_64.tar.gz","browser_download_url":"https://e/l.tgz","size":456},
          {"name":"UnitSportSwitzerland-v0.9.0-macos.tar.gz","browser_download_url":"https://e/m.tgz","size":789}]}
        """;

    [Theory]
    [InlineData("Windows", "https://e/w.zip")]
    [InlineData("Linux", "https://e/l.tgz")]
    [InlineData("macOS", "https://e/m.tgz")]
    [InlineData("Android", null)]
    public void Picks_the_platform_asset(string os, string? url)
    {
        var r = UpdateInfo.Parse(Json);
        Assert.NotNull(r);
        Assert.Equal("v0.9.0", r!.Tag);
        Assert.Equal(3, r.Assets.Count);
        Assert.Equal(url, UpdateInfo.AssetFor(r, os)?.Url);
    }

    [Theory]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Parse_rejects_error_bodies(string body) => Assert.Null(UpdateInfo.Parse(body));
}
