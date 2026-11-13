using UnitSport.Tools.RoadGen.Rewrite;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The cantons the signal heads follow (#348, #1794): a tool published without the repository around it (the NAS runner)
/// reads the copy of <c>tools/MapCore/switzerland.bin</c> built into RoadGen, and it names the same cantons as the file.
/// </summary>
public class SignalCantonsTests
{
    [Fact]
    public void BuiltInCopy_ReadsAndNamesTheCantons()
    {
        var embedded = TileRewriter.Cantons.Embedded();
        Assert.NotNull(embedded);
        Assert.Equal("the copy built into the tool", embedded!.Source);
        Assert.Equal("GE", embedded.CodeAt(2_500_500, 1_117_500));   // Geneva, the Rhône's mouth
        Assert.Equal("VS", embedded.CodeAt(2_583_250, 1_113_250));   // Riddes
        Assert.Null(embedded.CodeAt(2_000_000, 1_000_000));           // off the lattice
    }

    [Fact]
    public void BuiltInCopy_AgreesWithTheRepositoryFile()
    {
        var file = TileRewriter.Cantons.Find();
        var embedded = TileRewriter.Cantons.Embedded();
        Assert.NotNull(file);
        Assert.NotNull(embedded);
        for (int e = 2_480_500; e < 2_840_000; e += 7_000)
            for (int n = 1_070_500; n < 1_300_000; n += 7_000)
                Assert.Equal(file!.CodeAt(e, n), embedded!.CodeAt(e, n));
    }
}
