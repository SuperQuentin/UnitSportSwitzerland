using System.Globalization;
using UnitSport.Net;
using Xunit;

namespace UnitSport.Tests;

/// <summary><c>/money</c> and <c>/bank</c> argument parsing (src/Net/AdminArgs.cs, linked in, #262).</summary>
public class AdminArgsTests
{
    [Theory]
    [InlineData("500", 500)]
    [InlineData("-200", -200)]
    [InlineData("+1k", 1_000)]
    [InlineData("2.5k", 2_500)]
    [InlineData("1M", 1_000_000)]
    [InlineData(" 42 ", 42)]
    public void TryAmount_accepts(string text, long expected)
    {
        Assert.True(AdminArgs.TryAmount(text, out long amount));
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("2,5k")]
    [InlineData("5000m")]
    [InlineData("NaN")]
    public void TryAmount_rejects(string text) => Assert.False(AdminArgs.TryAmount(text, out _));

    [Fact]
    public void TryAmount_ignores_a_comma_decimal_locale()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-CH");
            Assert.True(AdminArgs.TryAmount("1.5k", out long amount));
            Assert.Equal(1_500, amount);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void TryBank_reads_with_no_arguments()
    {
        Assert.True(AdminArgs.TryBank([], out string? who, out long? set, out long add, out _));
        Assert.Null(who);
        Assert.Null(set);
        Assert.Equal(0, add);
    }

    [Fact]
    public void TryBank_reads_a_player()
    {
        Assert.True(AdminArgs.TryBank(["Anna"], out string? who, out long? set, out long add, out _));
        Assert.Equal("Anna", who);
        Assert.Null(set);
        Assert.Equal(0, add);
    }

    [Theory]
    [InlineData(new[] { "set", "100" }, null, 100L, 0L)]
    [InlineData(new[] { "add", "2k" }, null, null, 2_000L)]
    [InlineData(new[] { "Anna", "take", "50" }, "Anna", null, -50L)]
    [InlineData(new[] { "Bob", "SET", "0" }, "Bob", 0L, 0L)]
    public void TryBank_changes(string[] args, string? player, long? expectSet, long expectAdd)
    {
        Assert.True(AdminArgs.TryBank(args, out string? who, out long? set, out long add, out _));
        Assert.Equal(player, who);
        Assert.Equal(expectSet, set);
        Assert.Equal(expectAdd, add);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("Anna add")]
    [InlineData("Anna give 5")]
    [InlineData("add many")]
    [InlineData("Anna add 5 extra")]
    public void TryBank_rejects(string line) =>
        Assert.False(AdminArgs.TryBank(line.Split(' '), out _, out _, out _, out _));
}
