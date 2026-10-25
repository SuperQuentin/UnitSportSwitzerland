using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary><c>unitsport://join/...</c> links from the website (src/Core/JoinLink.cs, linked in, #1220).</summary>
public class JoinLinkTests
{
    [Theory]
    [InlineData("unitsport://join/unitsport.infrack.ch", "unitsport.infrack.ch")]
    [InlineData("unitsport://join/unitsport.infrack.ch:7778", "unitsport.infrack.ch:7778")]
    [InlineData("UnitSport://join/127.0.0.1/", "127.0.0.1")]
    [InlineData("unitsport://join/example.org:7777?from=site#x", "example.org:7777")]
    [InlineData("unitsport://join/[::1]:7777", "[::1]:7777")]
    [InlineData("unitsport://join/[fe80::1]", "[fe80::1]")]
    public void Parse_accepts(string link, string endpoint) => Assert.Equal(endpoint, JoinLink.Parse(link));

    [Theory]
    [InlineData("unitsport://join/")]
    [InlineData("unitsport://connect/example.org")]
    [InlineData("https://join/example.org")]
    [InlineData("unitsport://join/exa mple.org")]
    [InlineData("unitsport://join/example.org\" --server")]
    [InlineData("unitsport://join/-flag")]
    [InlineData("unitsport://join/example.org:0")]
    [InlineData("unitsport://join/example.org:70000")]
    [InlineData("unitsport://join/example.org:77a")]
    [InlineData("unitsport://join/[::1")]
    [InlineData("unitsport://join/[::1]x")]
    [InlineData("C:\\tracks\\ride.gpx")]
    public void Parse_refuses(string link) => Assert.Null(JoinLink.Parse(link));
}
