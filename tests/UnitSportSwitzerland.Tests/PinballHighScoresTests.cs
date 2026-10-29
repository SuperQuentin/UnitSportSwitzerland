using UnitSport.Interiors.Pinball;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>A pinball machine's high-score table and the server's book of them (#1325, src/Interiors/Pinball/PinballHighScores.cs).</summary>
public class PinballHighScoresTests
{
    [Fact]
    public void Scores_go_in_best_first_and_only_the_best_five_stay()
    {
        var t = new PinballHighScores();
        Assert.Equal(0, t.Offer("Anna", 300));
        Assert.Equal(0, t.Offer("Beat", 500));
        Assert.Equal(2, t.Offer("Chantal", 100));
        Assert.Equal(1, t.Offer("Dario", 400));
        Assert.Equal(4, t.Offer("Elsa", 50));
        Assert.Equal(-1, t.Offer("Fritz", 10));
        Assert.Equal(2, t.Offer("Gian", 350));
        Assert.Equal(new[] { "Beat", "Dario", "Gian", "Anna", "Chantal" }, t.Entries.Select(e => e.Name));
        Assert.Equal(PinballHighScores.Kept, t.Entries.Count);
    }

    [Fact]
    public void An_equal_score_goes_below_the_older_one_and_nothing_scores_zero()
    {
        var t = new PinballHighScores();
        t.Offer("First", 1000);
        Assert.Equal(1, t.Offer("Second", 1000));
        Assert.Equal(-1, t.Offer("Nobody", 0));
        Assert.Equal("First", t.Entries[0].Name);
    }

    [Fact]
    public void Names_are_cut_to_the_displays_and_never_empty()
    {
        var t = new PinballHighScores();
        t.Offer("A very long player name indeed", 10);
        t.Offer("  \n ", 5);
        Assert.Equal(PinballHighScores.NameLength, t.Entries[0].Name.Length);
        Assert.Equal("PLAYER", t.Entries[1].Name);
    }

    [Fact]
    public void A_table_survives_the_wire()
    {
        var t = new PinballHighScores();
        t.Offer("Anna", 123_456);
        t.Offer("Beat", 99);
        var back = PinballHighScores.Unpack(t.Pack());
        // on the wire an entry is its name and score: the id stays on the server
        Assert.Equal(t.Entries.Select(e => (e.Name, e.Score)), back.Entries.Select(e => (e.Name, e.Score)));
        Assert.Empty(PinballHighScores.Unpack(Array.Empty<byte>()).Entries);
    }

    [Fact]
    public void The_book_saves_and_reads_every_machine_and_a_new_table_starts_fresh()
    {
        var book = new PinballScoreBook();
        book.Of("2510_1140_26", 7, 1).Offer("Anna", 439_230);
        book.Of("2510_1140_26", 7, 1).Offer("Beat", 293_150);
        book.Of("2510_1140_26", 8, 0).Offer("Chantal", 47_300);
        book.Of("2600_1200_3", 2, 3);   // never scored: not saved
        var read = PinballScoreBook.FromJson(book.ToJson());
        Assert.Equal(new[] { "Anna", "Beat" }, read.Of("2510_1140_26", 7, 1).Entries.Select(e => e.Name));
        Assert.Equal(47_300, read.Of("2510_1140_26", 8, 0).Entries[0].Score);
        Assert.Equal(2, read.In("2510_1140_26").Count());
        Assert.Empty(read.In("2600_1200_3"));
        // the plan changed and put another table on machine 8: its scores start again
        Assert.Empty(read.Of("2510_1140_26", 8, 2).Entries);
    }
}
