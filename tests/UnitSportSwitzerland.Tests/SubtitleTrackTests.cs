using System.Collections.Generic;
using UnitSport.Trailer;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The films' subtitle tracks: ids, translations, overlap, SRT (src/Trailer/SubtitleTrack.cs, linked in, #862).</summary>
public class SubtitleTrackTests
{
    [Theory]
    [InlineData("Who stole the fondue?!", "who-stole-the-fondue")]
    [InlineData("Léa, ça va? Don't panic.", "lea-ca-va-dont-panic")]
    [InlineData("!!!", "line")]
    public void An_id_is_the_slug_of_the_english(string english, string id) =>
        Assert.Equal(id, SubtitleTrack.Slug(english));

    [Fact]
    public void A_long_line_s_id_is_cut_at_a_word()
    {
        string id = SubtitleTrack.Slug("Over there, behind the hotel! A light in the sky, coming down fast!");
        Assert.True(id.Length <= 48);
        Assert.Equal("over-there-behind-the-hotel-a-light-in-the-sky", id);
    }

    [Fact]
    public void Srt_times_are_frame_exact_to_the_millisecond()
    {
        Assert.Equal("00:00:00,000", SubtitleTrack.Time(0, 30));
        Assert.Equal("00:00:01,033", SubtitleTrack.Time(31, 30));
        Assert.Equal("01:02:03,500", SubtitleTrack.Time((3723 * 30) + 15, 30));
        string srt = SubtitleTrack.Srt([new Subtitle(30, 90, "Hello"), new Subtitle(90, 120, "A\nB")], 30);
        Assert.Equal("1\n00:00:01,000 --> 00:00:03,000\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nA\nB\n\n", srt);
    }

    [Fact]
    public void Overlapping_lines_become_subtitles_that_do_not_overlap()
    {
        var subs = SubtitleTrack.Flatten(
        [
            (0, 60, Cue.Bubble, "Who took it?"),
            (30, 90, Cue.Bubble, "Not me."),
            (40, 50, Cue.Caption, "GORNERGRAT"),
        ]);
        Assert.Equal(
        [
            new Subtitle(0, 30, "Who took it?"),
            new Subtitle(30, 40, "- Who took it?\n- Not me."),
            new Subtitle(40, 50, "- Who took it?\n- Not me.\nGORNERGRAT"),
            new Subtitle(50, 60, "- Who took it?\n- Not me."),
            new Subtitle(60, 90, "Not me."),
        ], subs);
    }

    private static readonly CueFile[] Two =
    [
        new(1, 30, 90, [new Cue("hello", Cue.Bubble, "Hello!", 15, 60)]),
        new(2, 30, 120, [new Cue("bye", Cue.Bubble, "Bye.", 0, 200), new Cue("gone", Cue.Caption, "Gone", 50, 50)]),
    ];

    [Fact]
    public void A_cut_lays_its_shots_end_to_end_and_translates()
    {
        var t = SubtitleTrack.ReadTranslations("""
            { "hello": { "en": "Hello!", "fr": "Salut !", "rm": "Allegra!" }, "bye": { "en": "Bye.", "fr": "Au revoir." } }
            """);
        Assert.Equal(["en", "fr", "rm"], SubtitleTrack.LanguagesOf(t));
        var warnings = new List<string>();
        var fr = SubtitleTrack.Cut(Two, t, "fr", warnings);
        // the second shot starts after the first's 90 frames, its cue clipped to its 120
        Assert.Equal([new Subtitle(15, 60, "Salut !"), new Subtitle(90, 210, "Au revoir.")], fr);
        Assert.Empty(warnings);
        var rm = SubtitleTrack.Cut(Two, t, "rm", warnings);
        Assert.Equal("Bye.", rm[1].Text);
        Assert.Single(warnings);
        Assert.Contains("bye", warnings[0]);
    }

    [Fact]
    public void English_reworded_under_an_explicit_id_is_flagged()
    {
        var t = SubtitleTrack.ReadTranslations("""{ "hello": { "en": "Hi!", "fr": "Salut !" } }""");
        var warnings = new List<string>();
        SubtitleTrack.Cut(Two, t, "en", warnings);
        Assert.Contains(warnings, w => w.Contains("\"hello\""));
    }

    [Fact]
    public void Sync_adds_new_lines_keeps_translations_and_lists_the_unused()
    {
        string json = """{ "hello": { "en": "Hello!", "de": "Hallo!" }, "old": { "en": "Old", "fr": "Vieux" } }""";
        var unused = new List<string>();
        var reworded = new List<string>();
        string synced = SubtitleTrack.Sync(json, [("hello", "Hello!"), ("new-line", "New line")], unused, reworded);
        var back = SubtitleTrack.ReadTranslations(synced);
        Assert.Equal("Hallo!", back["hello"]["de"]);
        Assert.Equal("New line", back["new-line"]["en"]);
        Assert.Equal("Vieux", back["old"]["fr"]);
        Assert.Equal(["old"], unused);
        Assert.Empty(reworded);
        Assert.Contains("\"Hallo!\"", synced);
        SubtitleTrack.Sync(json, [("hello", "Hello there!")], unused, reworded);
        Assert.Equal(["hello"], reworded);
    }
}
