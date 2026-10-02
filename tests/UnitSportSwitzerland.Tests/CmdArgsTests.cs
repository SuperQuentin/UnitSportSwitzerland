using System.Globalization;
using UnitSport.Core;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The command-line reader (src/Core/CmdArgs.cs, linked in): each rule a migrated caller relied on.</summary>
public class CmdArgsTests
{
    private static readonly string[] Line =
        { "--server", "--port", "7777", "--perflog", "--chunks", "fixture:hairpin", "--wall", "1,5", "--crashnet", "A", "--x", "--speed", "2.5", "--speed", "9" };

    [Fact]
    public void Has_is_an_exact_match()
    {
        Assert.True(CmdArgs.Has(Line, "--server"));
        Assert.False(CmdArgs.Has(Line, "--serv"));
        Assert.False(CmdArgs.Has(System.Array.Empty<string>(), "--server"));
    }

    [Fact]
    public void Value_is_the_word_after_the_first_flag()
    {
        Assert.Equal("7777", CmdArgs.Value(Line, "--port"));
        Assert.Equal("2.5", CmdArgs.Value(Line, "--speed"));          // the first one wins
        Assert.Equal("--chunks", CmdArgs.Value(Line, "--perflog"));  // a flag counts as a value by default
        Assert.Null(CmdArgs.Value(Line, "--perflog", notFlag: true));
        Assert.Null(CmdArgs.Value(Line, "--absent"));
        Assert.Null(CmdArgs.Value(new[] { "--perflog" }, "--perflog"));  // the line ends first
    }

    [Fact]
    public void Value_at_two_reads_the_second_word()
    {
        Assert.Equal("--x", CmdArgs.Value(Line, "--crashnet", 2));
        Assert.Null(CmdArgs.Value(Line, "--crashnet", 2, notFlag: true));
        Assert.Equal("9", CmdArgs.Value(new[] { "--uishot", "a.png", "9" }, "--uishot", 2));
        Assert.Null(CmdArgs.Value(new[] { "--uishot", "a.png" }, "--uishot", 2));
    }

    [Fact]
    public void Numbers_are_invariant_and_null_when_unreadable()
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-CH");   // a comma decimal separator must not leak in
        try
        {
            Assert.Equal(2.5, CmdArgs.Double(Line, "--speed"));
            Assert.Equal(2.5f, CmdArgs.Float(Line, "--speed"));
            Assert.Equal(7777, CmdArgs.Int(Line, "--port"));
            Assert.Null(CmdArgs.Int(Line, "--speed"));
            Assert.Null(CmdArgs.Float(Line, "--wall"));              // "1,5" is not 1.5
            Assert.Null(CmdArgs.Double(Line, "--perflog"));          // the next word is a flag
            Assert.Null(CmdArgs.Float(Line, "--absent"));
            Assert.Equal(-3, CmdArgs.Int(new[] { "--n", "-3" }, "--n"));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void FlagWithShot_splits_the_first_prefixed_arg()
    {
        Assert.Equal((true, "out.png"), CmdArgs.FlagWithShot(new[] { "--a", "--treecheck,out.png,x" }, "--treecheck"));
        Assert.Equal((true, (string?)null), CmdArgs.FlagWithShot(new[] { "--treecheck" }, "--treecheck"));
        Assert.Equal((false, (string?)null), CmdArgs.FlagWithShot(new[] { "--tree" }, "--treecheck"));
    }
}
