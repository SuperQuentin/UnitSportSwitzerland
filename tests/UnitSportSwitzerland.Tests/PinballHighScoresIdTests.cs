using UnitSport.Interiors.Pinball;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>High scores belong to a player's id and show its name as it is now (#1359).</summary>
public class PinballHighScoresIdTests
{
    [Fact]
    public void A_score_shows_the_name_the_id_has_now()
    {
        var t = new PinballHighScores();
        t.OfferId("acct:7", "Anna", 500);
        t.Offer("Old", 300);
        string Resolve(string id, string? backup) => id == "acct:7" ? "Anna2" : "?";
        var back = PinballHighScores.Unpack(t.Pack(Resolve));
        Assert.Equal(new[] { "Anna2", "Old" }, back.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Ids_survive_the_save_and_a_file_without_ids_still_reads()
    {
        var book = new PinballScoreBook();
        book.Of("k", 1, 0).OfferId("acct:7", "Anna", 500);
        book.Of("k", 1, 0).Offer("Old", 300);
        var read = PinballScoreBook.FromJson(book.ToJson());
        Assert.Equal(new[] { "acct:7", "" }, read.Of("k", 1, 0).Entries.Select(e => e.Id));

        const string legacy = "[{\"Key\":\"k\",\"Index\":2,\"Table\":0,\"Names\":[\"Beat\"],\"Scores\":[900]}]";
        var old = PinballScoreBook.FromJson(legacy).Of("k", 2, 0).Entries;
        Assert.Equal(("", "Beat", 900L), (old[0].Id, old[0].Name, old[0].Score));
    }

    [Fact]
    public void A_legacy_name_is_claimed_once_by_an_id_without_scores_of_its_own()
    {
        var book = new PinballScoreBook();
        book.Of("k", 1, 0).Offer("Anna", 500);
        book.Of("k", 2, 0).Offer("anna", 400);
        book.Of("k", 3, 0).OfferId("acct:7", "Other", 300);
        book.Of("k", 3, 0).Offer("Anna", 100);
        Assert.Equal(2, book.ClaimLegacy("Anna", "acct:7"));   // machine 3: the id has a score there, its legacy one stays
        Assert.Equal("acct:7", book.Of("k", 1, 0).Entries[0].Id);
        Assert.Equal("acct:7", book.Of("k", 2, 0).Entries[0].Id);
        Assert.Equal(new[] { "acct:7", "" }, book.Of("k", 3, 0).Entries.Select(e => e.Id));
        Assert.Equal(0, book.ClaimLegacy("Anna", "acct:7"));   // nothing left to claim: it has scores everywhere
    }

    [Fact]
    public void The_stored_name_is_the_backup_and_follows_the_player()
    {
        var t = new PinballHighScores();
        t.OfferId("acct:7", "Anna", 500);
        // an id nothing knows any more falls back to the stored name
        Assert.Equal("Anna", PinballHighScores.Unpack(t.Pack((id, backup) => backup ?? "Rider")).Entries[0].Name);
        t.Touch("acct:7", "Anna2");
        Assert.Equal("Anna2", t.Entries[0].Name);
    }

    [Fact]
    public void A_name_cut_by_the_displays_is_not_claimed()
    {
        var t = new PinballHighScores();
        t.Offer("A very long player name", 10);
        Assert.Equal(0, t.ClaimLegacy("A very long player name", "acct:1"));
    }
}
