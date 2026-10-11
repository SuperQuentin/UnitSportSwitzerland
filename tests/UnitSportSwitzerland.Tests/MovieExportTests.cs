using UnitSport.Movie;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The movie's sound for an export (#786): clips laid out on the timeline and mixed into one track.</summary>
public class MovieExportTests
{
    [Fact]
    public void ClipsLandWhereTheyPlayAtTheTracksRate()
    {
        // a steady 0.5 tone at 10 Hz playing from 2 s, its first second trimmed off; the export from 1 s to 6 s at 20 Hz
        var tone = Enumerable.Repeat(0.5f, 100).ToArray();
        var mix = Soundtrack.Mix(new[] { new Soundtrack.Piece(tone, 10, Start: 2, In: 1, Out: 4) }, 1, 6, 20);
        Assert.Equal(100, mix.Length);
        Assert.Equal(0, mix[10]);                          // 1.5 s: before the clip
        Assert.InRange(mix[30], 16380, 16384);              // 2.5 s: in it
        Assert.InRange(mix[79], 16380, 16384);              // 4.95 s: still in it (it ends at 2 + 3 = 5)
        Assert.Equal(0, mix[90]);                          // 5.5 s: after it
    }

    [Fact]
    public void LoudOverlapsAreScaledDownInsteadOfClipping()
    {
        var loud = Enumerable.Repeat(0.8f, 40).ToArray();
        var mix = Soundtrack.Mix(new[]
        {
            new Soundtrack.Piece(loud, 10, 0, 0, 4),
            new Soundtrack.Piece(loud, 10, 0, 0, 4),
        }, 0, 2, 10);
        Assert.All(mix, s => Assert.InRange(s, 32000, 32767));   // 1.6 summed, scaled to just under full scale
        Assert.Empty(Soundtrack.Mix(Array.Empty<Soundtrack.Piece>(), 3, 3, 48000));
    }
}
